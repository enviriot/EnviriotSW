///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace X13.WebUI.Helpers {
  // Address filter for the WebUI endpoints. The core is IsAllowed(peer, spec): a pure
  // decision over a specification string, deliberately unaware of HTTP and of where the
  // string came from. That is the seam for the per-branch rules SCADA will need - the same
  // matcher will be handed a spec taken from a branch's manifest instead of a global topic.
  internal static class NetworkAcl {
    // "local" expands to the subnets of the machine's own adapters. Enumerating interfaces
    // per request is far too expensive, and VPN/DHCP change the picture while running, so the
    // expansion is cached and dropped whenever Windows reports an address change.
    private const string LocalToken = "local";

    private static readonly object _sync = new object();
    // Keyed by spec text: trustedNets and trustedProxies are checked alternately, so a
    // single-slot cache would re-parse on every request.
    private static readonly Dictionary<string, List<Net>> _specCache = new Dictionary<string, List<Net>>(StringComparer.Ordinal);
    private static List<Net> _localNets;
    private static int _networkHookInstalled;

    /// <summary>True when <paramref name="peer"/> may use an endpoint guarded by <paramref name="spec"/>.</summary>
    /// <param name="spec">Comma/space separated tokens: "local", a CIDR, or a bare address.</param>
    public static bool IsAllowed(IPAddress peer, string spec) {
      if(peer == null) {
        return false;
      }
      // Loopback is always allowed, regardless of configuration: locking oneself out of the
      // machine's own UI by mistyping the spec must not be possible.
      return IPAddress.IsLoopback(Unmap(peer)) || IsInSpec(peer, spec);
    }

    /// <summary>Plain membership test, without the loopback exemption of <see cref="IsAllowed"/>.</summary>
    /// <remarks>Used for proxy trust, where an empty spec has to mean "trust nobody" - including
    /// loopback, since a local process must not be able to forge X-Real-IP.</remarks>
    public static bool IsInSpec(IPAddress peer, string spec) {
      if(peer == null) {
        return false;
      }
      IPAddress address = Unmap(peer);
      foreach(Net net in ResolveSpec(spec)) {
        if(net.Contains(address)) {
          return true;
        }
      }
      return false;
    }

    /// <summary>Unwraps ::ffff:a.b.c.d, which a socket bound to IPAddress.Any may report.</summary>
    /// <remarks>Without this a plain LAN address arrives in mapped form and matches no IPv4 rule.</remarks>
    public static IPAddress Unmap(IPAddress address) {
      if(address != null && address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6) {
        return address.MapToIPv4();
      }
      return address;
    }

    private static List<Net> ResolveSpec(string spec) {
      string text = spec ?? string.Empty;
      lock(_sync) {
        List<Net> cached;
        if(_specCache.TryGetValue(text, out cached)) {
          return cached;
        }
        List<Net> nets = new List<Net>();
        foreach(string token in text.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
          if(string.Equals(token, LocalToken, StringComparison.OrdinalIgnoreCase)) {
            nets.AddRange(LocalNets());
            continue;
          }
          Net net;
          if(TryParseNet(token, out net)) {
            nets.Add(net);
          } else {
            // A typo must not lock the user out of every other rule on the same line.
            Log.Warning("WebUI trusted network '{0}' is not a CIDR or address, ignored", token);
          }
        }
        _specCache[text] = nets;
        return nets;
      }
    }

    // Called under _sync from ResolveSpec.
    private static List<Net> LocalNets() {
      InstallNetworkHook();
      if(_localNets != null) {
        return _localNets;
      }
      List<Net> nets = new List<Net>();
      try {
        foreach(NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
          if(nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
            continue;  // loopback is handled unconditionally in IsAllowed
          }
          foreach(UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses) {
            int prefix = PrefixLength(info);
            if(prefix >= 0) {
              nets.Add(new Net(info.Address, prefix));
            }
          }
        }
      }
      catch(Exception ex) {
        Log.Warning("WebUI cannot enumerate local subnets - {0}", ex.Message);
      }
      _localNets = nets;
      return nets;
    }

    private static int PrefixLength(UnicastIPAddressInformation info) {
      if(info.Address.AddressFamily == AddressFamily.InterNetwork) {
        IPAddress mask = info.IPv4Mask;
        return mask == null ? -1 : MaskToPrefix(mask.GetAddressBytes());
      }
      if(info.Address.AddressFamily == AddressFamily.InterNetworkV6) {
        try {
          return info.PrefixLength;
        }
        catch(PlatformNotSupportedException) {
          return -1;
        }
      }
      return -1;
    }

    private static int MaskToPrefix(byte[] mask) {
      int prefix = 0;
      for(int i = 0; i < mask.Length; i++) {
        byte b = mask[i];
        while(b != 0) {
          prefix += b & 1;
          b >>= 1;
        }
      }
      return prefix;
    }

    private static void InstallNetworkHook() {
      if(Interlocked.CompareExchange(ref _networkHookInstalled, 1, 0) != 0) {
        return;
      }
      try {
        NetworkChange.NetworkAddressChanged += delegate {
          lock(_sync) {
            _localNets = null;
            _specCache.Clear();  // parsed specs have the old "local" expansion baked in
          }
        };
      }
      catch(Exception ex) {
        Log.Warning("WebUI cannot watch network changes - {0}", ex.Message);
      }
    }

    private static bool TryParseNet(string token, out Net net) {
      net = default(Net);
      if(string.IsNullOrEmpty(token)) {
        return false;
      }
      int slash = token.IndexOf('/');
      string addressText = slash < 0 ? token : token.Substring(0, slash);
      IPAddress address;
      if(!IPAddress.TryParse(addressText, out address)) {
        return false;
      }
      address = Unmap(address);
      int bits = address.GetAddressBytes().Length * 8;
      int prefix = bits;  // a bare address means a single host
      if(slash >= 0) {
        if(!int.TryParse(token.Substring(slash + 1), out prefix) || prefix < 0 || prefix > bits) {
          return false;
        }
      }
      net = new Net(address, prefix);
      return true;
    }

    private struct Net {
      private readonly byte[] _network;
      private readonly int _prefix;
      private readonly AddressFamily _family;

      public Net(IPAddress address, int prefix) {
        IPAddress unmapped = Unmap(address);
        _family = unmapped.AddressFamily;
        _prefix = prefix;
        _network = Mask(unmapped.GetAddressBytes(), prefix);
      }

      public bool Contains(IPAddress address) {
        if(address == null || address.AddressFamily != _family) {
          return false;  // never compare an IPv4 rule against an IPv6 peer
        }
        byte[] candidate = Mask(address.GetAddressBytes(), _prefix);
        if(candidate.Length != _network.Length) {
          return false;
        }
        for(int i = 0; i < candidate.Length; i++) {
          if(candidate[i] != _network[i]) {
            return false;
          }
        }
        return true;
      }

      private static byte[] Mask(byte[] bytes, int prefix) {
        byte[] result = (byte[])bytes.Clone();
        for(int i = 0; i < result.Length; i++) {
          int keep = prefix - i * 8;
          if(keep >= 8) {
            continue;
          }
          result[i] = keep <= 0 ? (byte)0 : (byte)(result[i] & (0xFF << (8 - keep)));
        }
        return result;
      }
    }
  }
}
