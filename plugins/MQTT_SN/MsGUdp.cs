///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.Periphery {
  internal class MsGUdp : IMsGate {
    private const int HEX_PREVIEW = 32;      // bytes of a refused frame that reach the log
    private const int REJECT_QUIET_SEC = 10; // at most one refusal line per this many seconds

    private readonly object _rejectLock = new object();
    private DateTime _rejectQuiet;
    private int _rejected;

    private MQTT_SNPl _pl;
    private Topic _udpT;
    private byte[][] _myIps;
    private IPAddress[] _bcIps;
    private UdpClient _udp;
    private Timer _advTick;
    private AddrWithMask[] _whiteList;
    private int _scanBusy;
    //private System.Collections.Concurrent.ConcurrentQueue<Tuple<byte[], byte[]>> _inBuf;

    public MsGUdp(MQTT_SNPl pl) {
      _pl = pl;
      _scanBusy = 0;
      //_inBuf = new System.Collections.Concurrent.ConcurrentQueue<Tuple<byte[], byte[]>>();
      _udpT = Topic.root.Get("/$YS/MQTT-SN/udp");

      // Deliberately a raw ValueType test, NOT AsBool/AsString: this decides whether the config
      // topic has to be CREATED and seeded. A reader with a default cannot tell "not set yet"
      // from "set to the default", so the topic would never be created.
      if(!_udpT.CheckAttribute(Topic.Attribute.Required) || _udpT.GetState().ValueType!=JSC.JSValueType.Boolean) {
        _udpT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        var act = new JSL.Array(1);
        var r_a = JSC.JSObject.CreateObject();
        r_a["name"] = "MQTT_SN.RefreshNIC";
        r_a["text"] = "Refresh";
        act[0] = r_a;
        _udpT.SetField("Action", act);
        _udpT.SetState(true);
      } else if(!(bool)_udpT.GetState()) {
        return;  // udp disabled
      }
      _scanBusy = 1;
      ScanNIC(false);

      try {
        _udp = new UdpClient(1883);
        _udp.EnableBroadcast = true;
        _udp.BeginReceive(new AsyncCallback(ReceiveCallback), null);
        _advTick = new Timer(SendAdv, null, 4500, 900000);
      }
      catch(Exception ex) {
        Log.Error("MsGUdp.ctor() {0}", ex.Message);
      }
    }
    public void RefreshNIC() {
      ThreadPool.QueueUserWorkItem((o) => ScanNIC(true));
    }

    private void ScanNIC(bool rescan) {
      if(Interlocked.CompareExchange(ref _scanBusy, 2, 1) != 1) {
        return;
      }
      List<AddrWithMask> wl = new List<AddrWithMask>();
      if(!rescan) {
        foreach(var c in _udpT.children.Where(z => z.GetState().Is<string>())) {
          var we = AddrWithMask.Parse(c.GetState().AsString(null), c.name);
          if(we == null) {
            Log.Warning("{0} = {1} is not IpAddress with Mask", c.path, c.GetState().AsString(null));
          } else {
            wl.Add(we);
          }
        }

        if(wl.Any()) {
          _whiteList = wl.ToArray();
          wl = null;
        }
      }
      _myIps = Dns.GetHostEntry(Dns.GetHostName()).AddressList.Where(z => z.AddressFamily == AddressFamily.InterNetwork).Union(new IPAddress[] { IPAddress.Loopback }).Select(z => z.GetAddressBytes()).ToArray();
      List<IPAddress> bc = new List<IPAddress>();
      try {
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
          var ipProps = nic.GetIPProperties();
          var ipv4Addrs = ipProps.UnicastAddresses.Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork);
          foreach(var addr in ipv4Addrs) {
            if(addr.IPv4Mask == null)
              continue;
            var ip = addr.Address.GetAddressBytes();
            var mask = addr.IPv4Mask.GetAddressBytes();
            var result = new Byte[ip.Length];
            for(int i = 0; i < ip.Length; ++i) {
              result[i] = (Byte)(ip[i] | (mask[i] ^ 255));
            }
            if(nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus!=OperationalStatus.Up) {
              continue;
            }
            X13.Log.Info("{0} - {1}/{2} - {3}", nic.Name, addr.Address, addr.IPv4Mask, nic.OperationalStatus);
            bc.Add(new IPAddress(result));
            if(wl != null) {
              wl.Add(new AddrWithMask(ip, mask, nic.Name));
            }
          }
        }
      }
      catch(Exception ex) {    // MONO: NotImplementedException
        Log.Debug("ScanNIC().GetAllNetworkInterfaces - {0}", ex.Message);
      }
      if(bc.Count == 0) {
        bc.Add(new IPAddress(new byte[] { 255, 255, 255, 255 }));
        Log.Info("ScanNIC() - set default bradcast");
      }
      _bcIps = bc.ToArray();
      if(wl != null) {
        _whiteList = wl.ToArray();
        foreach(var we in wl) {
          var t = _udpT.Get(we.name, true, _udpT);
          t.SetAttribute(Topic.Attribute.Config);
          t.SetState(we.ToString());
        }
        wl = null;
      }
      _scanBusy = 1;
    }
    private void ReceiveCallback(IAsyncResult ar) {
      if(_udp == null || _udp.Client == null) {
        return;
      }
      IPEndPoint re = new IPEndPoint(IPAddress.Any, 0);
      Byte[] buf = null;
      try {
        buf = _udp.EndReceive(ar, ref re);
        byte[] addr = re.Address.GetAddressBytes();
        if(!_myIps.Any(z => addr.SequenceEqual(z))) {
          MsMessageType mt;
          // TryReadHeader, not `buf[0] > 1 ? buf[1] : buf[3]` behind a `buf.Length > 1` guard:
          // a two- or three-byte datagram with a leading 0 or 1 passed that guard and then
          // indexed the fourth byte, so anyone on the network could turn one short datagram into
          // an Error line with a stack trace and the whole payload - as often as they cared to.
          if(MsMessage.TryReadHeader(buf, 0, buf.Length, out mt, out _, out MsParseError perr)) {
            if((mt != MsMessageType.CONNECT && mt != MsMessageType.SEARCHGW) || _whiteList.Any(z => z.Check(addr))) {
              //_inBuf.Enqueue(new Tuple<byte[],byte[]>(addr, buf));
              _pl.ProcessInPacket(this, addr, buf, 0, buf.Length);
            } else if(_pl.verbose) {
              var msg = MsMessage.Parse(buf, 0, buf.Length);
              if(msg != null) {
                Log.Debug("restricted  {0}: {1}  {2}", this.Addr2If(addr), MsMessage.HexPreview(buf, 0, buf.Length, HEX_PREVIEW), msg.ToString());
              }

            }
          } else {
            RejectFrame(re, buf, perr.ToString());
          }
        }
      }
      catch(ObjectDisposedException) {
        return;
      }
      catch(Exception ex) {
        RejectFrame(re, buf, ex.Message);
      }
      if(_udp != null && _udp.Client != null) {
        _udp.BeginReceive(new AsyncCallback(ReceiveCallback), null);
      }
    }
    /// <summary>One log line per rejected datagram, and no more than one per interval.</summary>
    /// <remarks>The rejection itself is cheap; the log line was not. It printed the entire payload
    /// and, on the exception path, the full stack trace - a remote sender decided both how often
    /// this ran and how much it wrote. Rate limiting alone would not be enough: a single 64 KB
    /// datagram is one line, so the preview is bounded too.
    /// <para>Warning, not Error: a frame this gate refuses to read is an event on the wire, not a
    /// fault in the server, and Error is the level someone puts an alert on. What gets lost to the
    /// throttle is counted rather than dropped silently, so a flood reads as a flood.</para></remarks>
    private void RejectFrame(IPEndPoint from, byte[] buf, string reason) {
      Interlocked.Increment(ref _rejected);
      DateTime now = DateTime.Now;
      lock(_rejectLock) {
        if(now < _rejectQuiet) {
          return;
        }
        _rejectQuiet = now.AddSeconds(REJECT_QUIET_SEC);
      }
      int total = Interlocked.Exchange(ref _rejected, 0);
      Log.Warning("MsGUdp refused {0}: {1} - {2}{3}", from, MsMessage.HexPreview(buf, 0, buf == null ? 0 : buf.Length, HEX_PREVIEW),
        reason, total > 1 ? (" (+" + (total - 1).ToString() + " more since the last line)") : string.Empty);
    }

    private void SendAdv(object o) {
      SendGw((MsDevice)null, new MsAdvertise(0, 900));
    }

    #region IMsGate Members
    public void SendGw(byte[] arr, MsMessage msg) {
      if(_udp == null || arr == null || arr.Length != 4 || msg == null) {
        return;
      }
      byte[] buf = msg.GetBytes();
      IPAddress addr = new IPAddress(arr);
      _udp.Send(buf, buf.Length, new IPEndPoint(addr, 1883));
      if(_pl.verbose) {
        Log.Debug("s {0}: {1}  {2}", addr, BitConverter.ToString(buf), msg.ToString());
      }
    }
    public void SendGw(MsDevice dev, MsMessage msg) {
      if(_udp == null || msg == null) {
        return;
      }

      byte[] buf = msg.GetBytes();
      IPAddress addr;
      if(dev == null) {
        addr = IPAddress.Broadcast;
        if(_bcIps==null) {
          Log.Error("bcIps == null");
          return;
        }
        foreach(var bc in _bcIps) {
          try {
            _udp.Send(buf, buf.Length, new IPEndPoint(bc, 1883));
          }
          catch(Exception ex) {
            if(_pl.verbose) {
              Log.Warning("MsGUdp.SendGw({0}, {1}) - {2}", bc, msg, ex.Message);
            }
          }
        }
      } else if(dev.addr != null && dev.addr.Length == 4) {
        addr = new IPAddress(dev.addr);
        try {
          _udp.Send(buf, buf.Length, new IPEndPoint(addr, 1883));
        }
        catch(Exception ex) {
          if(_pl.verbose) {
            Log.Warning("MsGUdp.SendGw({0}, {1}) - {2}", addr, msg, ex.Message);
          }
        }
      } else {
        return;
      }
      if(_pl.verbose) {
        Log.Debug("s {0}: {1}  {2}", addr, BitConverter.ToString(buf), msg.ToString());
      }
    }
    public void Tick() {
      //Tuple<byte[], byte[]> pck;
      //while(_inBuf.TryDequeue(out pck)) {
      //  try {
      //    _pl.ProcessInPacket(this, pck.Item1, pck.Item2, 0, pck.Item2.Length);
      //  }
      //  catch(Exception ex) {
      //    Log.Error("ReceiveCallback({0}, {1}) - {2}", new IPAddress(pck.Item1), pck.Item2 == null ? "null" : BitConverter.ToString(pck.Item2), ex.ToString());
      //  }
      //}
    }

    public byte gwIdx { get { return 0; } }
    public byte gwRadius { get { return MQTT_SNPl.gwRadius; } }
    public string name { get { return "UDP"; } }
    public string Addr2If(byte[] addr) {
      return (new IPAddress(addr)).ToString();
    }

    public void Stop() {
      var u = Interlocked.Exchange(ref _udp, null);
      if(u != null) {
        try {
          foreach(var n in _pl._devs.Where(z => z._gate == this).ToArray()) {
            n.Stop();
          }
          u.Close();
        }
        catch(Exception ex) {
          Log.Error("MsGUdp.Close() - {0}", ex.ToString());
        }
      }
    }
    #endregion IMsGate Members

    private class AddrWithMask {
      private byte[] _addr;
      private byte[] _mask;
      public readonly string name;

      public static AddrWithMask Parse(string s, string name) {
        if(string.IsNullOrWhiteSpace(s)) {
          return null;
        }
        var sp = s.Split('/');
        if(sp.Length == 0) {
          return null;
        }
        byte[] addr;
        IPAddress tmp;
        if(IPAddress.TryParse(sp[0], out tmp)) {
          addr = tmp.GetAddressBytes();
        } else {
          return null;
        }
        byte[] mask;
        int mn;
        if(sp.Length < 2) {  // no mask
          mask = Enumerable.Repeat((byte)0xFF, addr.Length).ToArray();
        } else if(int.TryParse(sp[1], out mn)) {
          mask = new byte[addr.Length];
          for(int i = 0; i < mask.Length; i++) {
            if(mn < 1) {
              mask[i] = 0;
            } else if(mn > 7) {
              mask[i] = 0xFF;
            } else {
              mask[i] = (byte)(0xFF << mn);
            }
            mn -= 8;
          }
        } else {
          mask = IPAddress.Parse(sp[1]).GetAddressBytes();
        }
        if(addr.Length != mask.Length) {
          return null;
        }
        return new AddrWithMask(addr, mask, name);
      }
      public AddrWithMask(byte[] addr, byte[] mask, string name) {
        _addr = new byte[addr.Length];
        for(int i = 0; i < addr.Length; i++) {
          _addr[i] = (byte)(addr[i] & mask[i]);
        }
        _mask = mask;
        this.name = name;
      }
      public bool Check(byte[] addr) {
        if(addr == null || addr.Length != _addr.Length) {
          return false;
        }
        for(int i = 0; i < addr.Length; i++) {
          if(_addr[i] != (addr[i] & _mask[i])) {
            return false;
          }
        }
        return true;
      }
      public override string ToString() {
        return (new IPAddress(_addr)).ToString() + "/" + (new IPAddress(_mask)).ToString();
      }
    }
  }
}
