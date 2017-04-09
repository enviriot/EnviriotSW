///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace X13.Periphery {
  internal class MsGUdp : IMsGate {
    private MQTT_SNPl _pl;
    private byte[][] _myIps;
    private IPAddress[] _bcIps;
    private UdpClient _udp;
    private Timer _advTick;
    private byte _gwRadius;

    public MsGUdp(MQTT_SNPl pl) {
      _pl = pl;
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
            bc.Add(new IPAddress(result));
          }
        }
      }
      catch(Exception) {    // MONO: NotImplementedException
      }
      if(bc.Count == 0) {
        bc.Add(new IPAddress(new byte[] { 255, 255, 255, 255 }));
      }
      _bcIps = bc.ToArray();

      try {
        _udp = new UdpClient(1883);
        _udp.EnableBroadcast = true;
        _udp.BeginReceive(new AsyncCallback(ReceiveCallback), null);
        _advTick = new Timer(SendAdv, null, 4500, 900000);
        //IPAddress wla_ip, wlm_ip;
        //Topic wla_t, wlm_t;
        //if(Topic.root.Exist("/local/cfg/MQTT-SN.udp/whitelist_addr", out wla_t) && Topic.root.Exist("/local/cfg/MQTT-SN.udp/whitelist_mask", out wlm_t)
        //  && wla_t.valueType == typeof(string) && wlm_t.valueType == typeof(string)
        //  && IPAddress.TryParse((wla_t as DVar<string>).value, out wla_ip) && IPAddress.TryParse((wlm_t as DVar<string>).value, out wlm_ip)) {
        //  _wla_arr = wla_ip.GetAddressBytes();
        //  _wlm_arr = wlm_ip.GetAddressBytes();
        //} else {
        //  _wla_arr = new byte[] { 0, 0, 0, 0 };
        //  _wlm_arr = _wla_arr;
        //}
        //Topic t;
        //DVar<long> tl;
        //if(Topic.root.Exist("/local/cfg/MQTT-SN.udp/radius", out t) && t.valueType == typeof(long) && (tl = t as DVar<long>) != null) {
        //  _gwRadius = (byte)tl.value;
        //  if(_gwRadius < 1 || _gwRadius > 3) {
        //    _gwRadius = 0;
        //  }
        //} else {
          _gwRadius = 1;
        //}
      }
      catch(Exception ex) {
        Log.Error("MsGUdp.ctor() {0}", ex.Message);
      }
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
        bool allow = true;
        if(!_myIps.Any(z => addr.SequenceEqual(z))) {
          if(buf.Length > 1) {
            var mt = (MsMessageType)(buf[0] > 1 ? buf[1] : buf[3]);
            //if((mt == MsMessageType.CONNECT || mt == MsMessageType.SEARCHGW) && addr.Length == _wla_arr.Length) {
            //  for(int i = addr.Length - 1; i >= 0; i--) {
            //    if((addr[i] & _wlm_arr[i]) != (_wla_arr[i] & _wlm_arr[i])) {
            //      allow = false;
            //      break;
            //    }
            //  }
            //}
            if(allow) {
              _pl.ProcessInPacket(this, addr, buf, 0, buf.Length);
            } else if(_pl.verbose) {
              var msg = MsMessage.Parse(buf, 0, buf.Length);
              if(msg != null) {
                Log.Debug("restricted  {0}: {1}  {2}", this.Addr2If(addr), BitConverter.ToString(buf), msg.ToString());
              }

            }
          }
        }
      }
      catch(ObjectDisposedException) {
        return;
      }
      catch(Exception ex) {
        Log.Error("ReceiveCallback({0}, {1}) - {2}", re, buf == null ? "null" : BitConverter.ToString(buf), ex.ToString());
      }
      if(_udp != null && _udp.Client != null) {
        _udp.BeginReceive(new AsyncCallback(ReceiveCallback), null);
      }
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
        foreach(var bc in _bcIps) {
          _udp.Send(buf, buf.Length, new IPEndPoint(bc, 1883));
        }
      } else if(dev.addr != null && dev.addr.Length == 4) {
        addr = new IPAddress(dev.addr);
        _udp.Send(buf, buf.Length, new IPEndPoint(addr, 1883));
      } else {
        return;
      }
      if(_pl.verbose) {
        Log.Debug("s {0}: {1}  {2}", addr, BitConverter.ToString(buf), msg.ToString());
      }
    }
    public byte gwIdx { get { return 0; } }
    public byte gwRadius { get { return _gwRadius; } }
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
  }
}
