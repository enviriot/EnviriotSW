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
    private Topic _verbose;
    private Random _rand;
    private SubRec _subMs;

    internal List<IMsGate> _gates;
    internal List<MsDevice> _devs;
    internal List<DevicePLC> _plcs;

    public MQTT_SNPl() {
      _gates = new List<IMsGate>();
      _devs = new List<MsDevice>();
      _plcs = new List<DevicePLC>();
      _rand = new Random((int)DateTime.Now.Ticks);
    }

    #region IPlugModul Members
    public void Init() {
      RPC.Register("MQTT_SN.SendDisconnect", SendDisconnectRpc);
      RPC.Register("MQTT_SN.PLC.Build", PlcBuildRpc);
      RPC.Register("MQTT_SN.PLC.Run", PlcRunRpc);
      RPC.Register("MQTT_SN.PLC.Start", PlcStartRpc);
      RPC.Register("MQTT_SN.PLC.Stop", PlcStopRpc);
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

    #region RPC
    private void SendDisconnectRpc(JSC.JSValue[] obj) {
      string path;
      if(obj == null || obj.Length != 1 || obj[0] == null || obj[0].ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(path = obj[0].Value as string)) {
        return;
      }
      var d = _devs.FirstOrDefault(z => z.owner.path == path);
      if(d != null) {
        d.Send(new MsDisconnect());
        d.Disconnect();
      }
    }
    private void PlcBuildRpc(JSC.JSValue[] obj) {
      string path;
      if(obj == null || obj.Length != 1 || obj[0] == null || obj[0].ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(path = obj[0].Value as string)) {
        return;
      }
      var d = _plcs.FirstOrDefault(z => z.path == path);
      if(d != null) {
        d.Build();
      }
    }
    private void PlcStartRpc(JSC.JSValue[] obj) {
      string path;
      if(obj == null || obj.Length != 1 || obj[0] == null || obj[0].ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(path = obj[0].Value as string)) {
        return;
      }
      var d = _plcs.FirstOrDefault(z => z.path == path);
      if(d != null) {
        d.StartPlc();
      }
    }
    private void PlcStopRpc(JSC.JSValue[] obj) {
      string path;
      if(obj == null || obj.Length != 1 || obj[0] == null || obj[0].ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(path = obj[0].Value as string)) {
        return;
      }
      var d = _plcs.FirstOrDefault(z => z.path == path);
      if(d != null) {
        d.StopPlc();
      }
    }
    private void PlcRunRpc(JSC.JSValue[] obj) {
      string path;
      if(obj == null || obj.Length != 1 || obj[0] == null || obj[0].ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(path = obj[0].Value as string)) {
        return;
      }
      var d = _plcs.FirstOrDefault(z => z.path == path);
      if(d != null) {
        d.Run();
      }
    }



    #endregion RPC
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
        MsGSerial.Init(this);
      }
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
          var dt = Topic.root.Get("/dev/" + cm.ClientId, true, _owner);
          dt.SetAttribute(Topic.Attribute.Readonly);
          dev = new MsDevice(this, dt);
          _devs.Add(dev);
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
