///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;
using System.IO.Ports;

namespace X13.Periphery {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 7)]
  [ExportMetadata("name", "MQTT_SN")]
  public class MQTT_SNPl : IPlugModul {
    private Topic _owner;
    private bool _scanAllPorts;
    private int _scanBusy;
    private Topic _verbose;
    private Random _rand;
    private SubRec _subMs;

    internal AutoResetEvent _startScan;
    internal List<IMsGate> _gates;
    internal List<MsDevice> _devs;

    public MQTT_SNPl() {
      _scanBusy = 0;
      _startScan = new AutoResetEvent(false);
      _gates = new List<IMsGate>();
      _devs = new List<MsDevice>();
      _rand = new Random((int)DateTime.Now.Ticks);
    }

    #region IPlugModul Members
    public void Init() {
    }

    public void Start() {
      _owner = Topic.root.Get("/$YS/MQTT-SN");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
      //var verV = _owner.GetField("ver");
      //string verS;
      //Version ver, verC = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

      //if(verV.ValueType != JSC.JSValueType.String || (verS = verV.Value as string) == null || !verS.StartsWith("¤VR") || !Version.TryParse(verS.Substring(3), out ver) || ver < verC) {
      //  var man = Topic.root.Get("/$YS/TYPES/Core/Manifest");
      //  var manJ = JsLib.Clone(man.GetState());
      //  JsLib.SetField(ref manJ, "Fields.MQTT-SN", JSL.JSON.parse(X13.Periphery.Properties.Resources.MQTT_SN_MANIFEST));
      //  man.SetState(manJ);
      //  _owner.SetField("version", "¤VR" + verC.ToString());
      //}
      _subMs = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "MQTT-SN.phy1_addr", SubFunc);
    }

    public void Tick() {
      for(int i = _devs.Count - 1; i >= 0; i--) {
        _devs[i].Tick();
      }
    }

    public void Stop() {
      var sr = Interlocked.Exchange(ref _subMs, null);
      if(sr != null) {
        sr.Dispose();
      }
      foreach(var g in _gates.ToArray()) {
        try {
          g.Stop();
        }
        catch(Exception) {
        }
      }
    }

    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/MQTT-SN", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/MQTT-SN", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members

    public bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }

    private void SubFunc(Perform p, SubRec sb) {
      if(p.art == Perform.Art.subscribe) {
        if(p.src.GetField("MQTT-SN.phy1_addr").Defined) {
          var dev = _devs.FirstOrDefault(z => z.name == p.src.name);
          if(dev == null) {
            dev = new MsDevice(this, p.src);
            _devs.Add(dev);
          }
        }
      } else if(p.art == Perform.Art.subAck) {
        _gates.Add(new MsGUdp(this));
        ThreadPool.RegisterWaitForSingleObject(_startScan, ScanPorts, null, 45000, false);
        _scanBusy = 1;
        _startScan.Set();
      }
    }
    private void ScanPorts(object o, bool b) {
      if(Interlocked.CompareExchange(ref _scanBusy, 2, 1) != 1) {
        return;
      }

      byte[] buf = new byte[64];
      byte[] tmpBuf = new byte[64];
      byte[] disconnectAll = new byte[] { 0x02, 0x02, 0x18, 0xC0 };
      bool escChar;
      int cnt = 0, tryCnt, length;
      SerialPort port = null;
      bool found;
      DateTime to;

      List<string> pns = new List<string>();
      /*
      Topic dev = Topic.root.Get("/dev");
      lock(dev) {
        var ifs = dev.children.OfType<DVar<MsDevice>>().Where(z => z.value != null).Select(z => z.value).ToArray();
        foreach(var devSer in ifs) {
          cnt++;
          if(devSer.state == State.Connected) {
            continue;
          }
          if(string.IsNullOrWhiteSpace(devSer.via)) {
            _scanAllPorts = true;
            break;
          }
          string via = devSer.via;
          if(via != "offline" && !pns.Exists(z => string.Equals(z, via, StringComparison.InvariantCultureIgnoreCase))) {
            pns.Add(via);
          }
        }
      }*/
      if(_scanAllPorts || cnt == 0) {
        _scanAllPorts = false;
        pns.Clear();
        pns.AddRange(SerialPort.GetPortNames());
      } else {
        pns = pns.Intersect(SerialPort.GetPortNames()).ToList();
      }
      /*
      Topic tmp;
      if(Topic.root.Exist("/local/cfg/MQTT-SN.Serial/whitelist", out tmp)) {
        var whl = tmp as DVar<string>;
        if(whl != null && !string.IsNullOrEmpty(whl.value)) {
          var wps = whl.value.Split(';', ',');
          if(wps != null && wps.Length > 0) {
            pns = pns.Intersect(wps).ToList();
          }
        }
      }
      if(Topic.root.Exist("/local/cfg/MQTT-SN.Serial/blacklist", out tmp)) {
        var bll = tmp as DVar<string>;
        if(bll != null && !string.IsNullOrEmpty(bll.value)) {
          var bps = bll.value.Split(';', ',');
          if(bps != null && bps.Length > 0) {
            pns = pns.Except(bps).ToList();
          }
        }
      }
      */
      for(int i = 0; i < pns.Count; i++) {
        if(_gates.Exists(z => z.name == pns[i])) {
          continue;
        }

        try {
          port = new SerialPort(pns[i], 38400, Parity.None, 8, StopBits.One);
          port.ReadBufferSize = 300;
          port.WriteBufferSize = 300;
          port.ReadTimeout = 5;
          port.Open();
          tryCnt = 3;
          found = false;
          do {
            to = DateTime.Now.AddMilliseconds(1100);
            cnt = -1;
            length = -1;

            port.DiscardInBuffer();
            port.Write(disconnectAll, 0, disconnectAll.Length);   // Send Disconnect
            if(verbose) {
              Log.Debug("s {0}: {1}  DISCONNECT", port.PortName, BitConverter.ToString(disconnectAll, 0, disconnectAll.Length));
            }

            while(to > DateTime.Now) {
              if(port.BytesToRead > 0) {
                to = DateTime.Now.AddMilliseconds(100);
                if(cnt >= 0) {
                  buf[cnt] = (byte)port.ReadByte();
                } else {
                  length = port.ReadByte();
                }
                cnt++;
              }
              Thread.Sleep(0);
            }

            if(cnt > 2 && cnt >= length) {
              var msgTyp = (MsMessageType)(buf[0] > 1 ? buf[1] : buf[3]);
              if(msgTyp == MsMessageType.SEARCHGW || msgTyp == MsMessageType.DHCP_REQ) {   // Received Ack
                escChar = false;
                if(cnt > length && buf[cnt - 1] == 0xC0) {
                  int j, k = -1;
                  for(j = 0; j < cnt; j++) {
                    if(buf[j] == 0xDB) {
                      escChar = true;
                      continue;
                    }
                    if(escChar) {
                      buf[++k] = (byte)(buf[j] ^ 0x20);
                      escChar = false;
                    } else {
                      buf[++k] = buf[j];
                    }
                  }
                  escChar = true;
                  cnt = k;
                }
                if(cnt == length) {
                  found = true;
                  MsGSerial gw;
                  lock(_gates) {
                    gw = new MsGSerial(this, port);
                    _gates.Add(gw);
                  }
                  gw._useSlip = escChar;
                  Log.Debug("I {0}: SLIP={1}", port.PortName, escChar);
                  ProcessInPacket(gw, gw._gateAddr, buf, 0, cnt);
                  break;
                }
              }
              if(verbose) {
                Log.Debug("r {0}: {1}  {2}", pns[i], BitConverter.ToString(buf, 0, cnt), msgTyp);
              }
            }
            Thread.Sleep(90);
          } while(--tryCnt > 0);

          if(!found) {
            port.Close();
            continue;
          }
        }
        catch(Exception ex) {
          if(verbose) {
            Log.Debug("MQTT-SN.Serial search on {0} - {1}", pns[i], ex.Message);
          }
          try {
            if(port != null) {
              if(port != null && port.IsOpen) {
                port.Close();
              }
              port.Dispose();
            }
          }
          catch(Exception) {
          }
        }
        port = null;
      }
      _scanBusy = 1;
    }
    internal bool ProcessInPacket(IMsGate gate, byte[] addr, byte[] buf, int start, int end) {
      var msg = MsMessage.Parse(buf, start, end);
      if(msg == null) {
        if(verbose) {
          Log.Warning("r {0}: {1}  bad message", gate.Addr2If(addr), BitConverter.ToString(buf, start, end - start));
        }
        return false;
      }
      if(msg.MsgTyp == MsMessageType.ADVERTISE || msg.MsgTyp == MsMessageType.GWINFO) {
        return true;
      }
      if(verbose) {
        Log.Debug("r {0}: {1}  {2}", gate.Addr2If(addr), BitConverter.ToString(buf, start, end - start), msg.ToString());
      }
      if(msg.MsgTyp == MsMessageType.SEARCHGW) {
        if((msg as MsSearchGW).radius == 0 || (msg as MsSearchGW).radius == gate.gwRadius) {
          gate.SendGw((MsDevice)null, new MsGwInfo(gate.gwIdx));
        }
        return true;
      }
      if(msg.MsgTyp == MsMessageType.DHCP_REQ) {
        var dr = msg as MsDhcpReq;
        if((dr.radius == 0 || dr.radius == 1)) {
          List<byte> ackAddr = new List<byte>();
          byte[] respPrev = null;
          foreach(byte hLen in dr.hLen) {
            if(hLen == 0) {
              continue;
            } else if(hLen <= 8) {
              byte[] resp;
              if(respPrev != null && respPrev.Length == hLen) {
                resp = respPrev;
              } else {
                resp = new byte[hLen];
                for(int i = 0; i < 5; i++) {
                  for(int j = 0; j < resp.Length; j++) {
                    resp[j] = (byte)_rand.Next(j == 0 ? 4 : 0, (i < 3 && hLen == 1) ? 31 : (j == 0 ? 254 : 255));
                  }
                  if(!_devs.Any(z => z.gwIdx == gate.gwIdx && z.CheckAddr(resp))) {
                    break;
                  } else if(i == 4) {
                    for(int j = 0; j < resp.Length; j++) {
                      resp[j] = 0xFF;
                    }
                  }
                }
                respPrev = resp;
              }
              ackAddr.AddRange(resp);
            } else {
              if(verbose) {
                Log.Warning("r {0}: {1}  DhcpReq.hLen is too high", gate.Addr2If(addr), BitConverter.ToString(buf, start, end - start));
              }
              ackAddr = null;
              break;
            }
          }
          if(ackAddr != null) {
            gate.SendGw((MsDevice)null, new MsDhcpAck(gate.gwIdx, dr.xId, ackAddr.ToArray()));
          }
        }
        return true;
      }
      if(msg.MsgTyp == MsMessageType.CONNECT) {
        var cm = msg as MsConnect;
        MsDevice dev = _devs.FirstOrDefault(z => z.owner != null && z.owner.name == cm.ClientId);
        if(dev == null) {
          dev = new MsDevice(this, Topic.root.Get("/vacant/" + cm.ClientId, true, _owner));
          _devs.Add(dev);
          Log.Info(dev.owner.path + " created on connect");
        }
        dev._gate = gate;
        dev.addr = addr;
        dev.Connect(cm);
        foreach(var dub in _devs.Where(z => z != dev && z.CheckAddr(addr) && z._gate == gate).ToArray()) {
          dub.addr = null;
          dub._gate = null;
          dub.state = State.Disconnected;
        }
      } else {
        MsDevice dev = _devs.FirstOrDefault(z => z.addr != null && z.addr.SequenceEqual(addr) && z._gate == gate);
        if(dev != null && (dev.state != State.Disconnected && dev.state != State.Lost)) {
          dev.ProcessInPacket(msg);
        } else {
          if(verbose) {
            if(dev == null || dev.owner == null) {
              Log.Debug("{0} unknown device", gate.Addr2If(addr));
            } else {
              Log.Debug("{0} inactive device: {1}", gate.Addr2If(addr), dev.owner.path);
            }
          }
          gate.SendGw(addr, new MsDisconnect());
        }
      }
      return true;
    }

  }
  /// <summary>Quality of service levels</summary>
  internal enum QoS : byte {
    AtMostOnce = 0,
    AtLeastOnce = 1,
    ExactlyOnce = 2,
    MinusOne = 3
  }
  internal enum State {
    Disconnected = 0,
    WillTopic,
    WillMsg,
    Connected,
    ASleep,
    AWake,
    Lost,
    PreConnect,
  }

}
