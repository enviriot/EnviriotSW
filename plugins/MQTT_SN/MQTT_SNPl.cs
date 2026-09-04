///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using NiL.JS.Extensions;
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
    private const string OWNER_PATH = "/$YS/MQTT-SN";
    private const int HEX_PREVIEW = 32;  // bytes of a frame that reach a log line
    private const Topic.Attribute CfgAttr = Topic.Attribute.Required | Topic.Attribute.Config;
    private const Topic.Attribute DbAttr = Topic.Attribute.Required | Topic.Attribute.DB;
#if DEBUG
    private const bool VerboseDefault = true;
#else
    private const bool VerboseDefault = false;
#endif

    private Topic _owner;
    private SubRec[] _cfg;
    private Random _rand;
    private bool _statistic;

    /// <summary>Flags belonging to DevicePLC, TWI and the gates, seeded here for all of them.</summary>
    /// <remarks>Static because the topics are: /$YS/DevicePLC/verbose is one setting for every PLC
    /// device, not one per device, yet DevicePLC and TWI are constructed per device and were each
    /// seeding it in their own constructor - so the same topic was written as many times as there
    /// were devices, and with EnsureCfg that would have been one subscription per device with
    /// nothing to dispose them. MEF gives exactly one MQTT_SNPl (CreationPolicy.Shared), which is
    /// what makes a static the same thing as an instance field here, minus threading a plugin
    /// reference through two constructors for a diagnostic flag.
    /// <para>gwRadius is the same story: MsGSerial and MsGUdp read /$YS/MQTT-SN/radius with
    /// byte-for-byte identical code, and neither seeded it - the topic existed only if someone
    /// created it by hand.</para></remarks>
    internal static bool verbosePlc, verboseTwi;
    internal static byte gwRadius;

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
      CCtor.Register("MqsDev", MqsDevCctor);
      RPC.Register("MQTT_SN.RefreshPorts", RefreshPortsRpc);
      RPC.Register("MQTT_SN.RefreshNIC", RefreshNICRpc);
    }

    /// <summary>Every setting this plugin owns, declared before the first gate exists.</summary>
    /// <remarks>The order is load-bearing: both gates read <see cref="gwRadius"/> in their
    /// constructors, so it has to hold its configured value by the time they are built. EnsureCfg
    /// applies before it returns for exactly this reason - the subscription alone would not have
    /// run yet, since Subscribe only queues a TopicEvent for the next Repo tick.
    /// <para>radius is clamped in the apply rather than at the read sites: 1..3 is the range the
    /// protocol defines, and anything else means "no radius", which is what both gates already
    /// did with the raw value.</para></remarks>
    public void Start() {
      _cfg = new SubRec[] {
        JsExtLib.EnsureCfg(Owner, "verbose", CfgAttr, v => verbose = v, VerboseDefault),
        JsExtLib.EnsureCfg(Owner, "statistic", CfgAttr, v => _statistic = v, false),
        JsExtLib.EnsureCfg(Owner, "radius", CfgAttr, v => gwRadius = (byte)(v >= 1 && v <= 3 ? v : 0), 1),
        JsExtLib.EnsureCfg(Topic.root.Get("/$YS/DevicePLC", true), "verbose", DbAttr, v => verbosePlc = v, VerboseDefault),
        JsExtLib.EnsureCfg(Topic.root.Get("/$YS/TWI", true), "verbose", DbAttr, v => verboseTwi = v, VerboseDefault),
      };
      _gates.Add(new MsGUdp(this));
      MsGSerial.Init(this);
    }

    public void Tick() {
      int i;
      for(i = _devs.Count - 1; i >= 0; i--) {
        _devs[i].Tick();
      }
      for(i = _gates.Count - 1; i >= 0; i--) {
        _gates[i].Tick();
      }
    }

    public void Stop() {
      // Released here because EnsureCfg hands ownership to the caller. The hand-rolled
      // subscription this replaced was never released at all.
      if(_cfg != null) {
        foreach(var s in _cfg) s.Dispose();
        _cfg = null;
      }
      foreach(var g in _gates.ToArray()) {
        try {
          g.Stop();
        }
        catch(Exception) {
        }
      }
    }

    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        // Is<bool>, NOT AsBool/AsString: this decides whether the config topic has to be CREATED
        // and seeded. A reader with a default cannot tell "not set yet" from "set to the
        // default", so the topic would never be created.
        if(!Owner.GetState().Is<bool>()) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
      }
    }
    #endregion IPlugModul Members

    public bool verbose;

    public bool Statistic { get { return _statistic; } }
    #region RPC
    private void SendDisconnectRpc(Topic t, JSC.JSValue arg) {
      var d = _devs.FirstOrDefault(z => z.owner == t);
      if(d != null) {
        d.Send(new MsDisconnect());
        d.Disconnect();
      }
    }
    private void PlcBuildRpc(Topic t, JSC.JSValue arg) {
      var d = _plcs.FirstOrDefault(z => z.Path == t.path);
      if(d != null) {
        d.Build();
      }
    }
    private void PlcStartRpc(Topic t, JSC.JSValue arg) {
      var d = _plcs.FirstOrDefault(z => z.Path == t.path);
      if(d != null) {
        d.StartPlc();
      }
    }
    private void PlcStopRpc(Topic t, JSC.JSValue arg) {
      var d = _plcs.FirstOrDefault(z => z.Path == t.path);
      if(d != null) {
        d.StopPlc();
      }
    }
    private void PlcRunRpc(Topic t, JSC.JSValue arg) {
      var plc = _plcs.FirstOrDefault(z => z.Path == t.path);
      var d = _devs.FirstOrDefault(z => t.path.StartsWith(z.owner.path + "/"));
      if(plc != null) {
        plc.Run(d);
      }
    }

    private void MqsDevCctor(Topic t, EventKind a) {
      var dev = _devs.FirstOrDefault(z => z.name == t.name);
      if(dev == null) {
        dev = new MsDevice(this, t);
        _devs.Add(dev);
      }
    }

    private void RefreshPortsRpc(Topic t, JSC.JSValue arg) {
      MsGSerial.StartScan();
    }

    private void RefreshNICRpc(Topic t, JSC.JSValue arg) {
      var ug = _gates.OfType<MsGUdp>().FirstOrDefault();
      if(ug!=null) {
        ug.RefreshNIC();
      }
    }

    #endregion RPC

    internal bool ProcessInPacket(IMsGate gate, byte[] addr, byte[] buf, int start, int end) {
      MsMessage msg;
      MsParseError perr;
      if(!MsMessage.TryParse(buf, start, end, out msg, out perr)) {
        // The reason, not just "bad message": an unknown type, a truncated frame and a body that
        // contradicts its own header used to arrive here as the same null.
        if(verbose) {
          Log.Warning("r {0}: {1}  {2}", gate.Addr2If(addr), MsMessage.HexPreview(buf, start, end - start, HEX_PREVIEW), perr.ToString());
        }
        return false;
      }
      if(msg.MsgTyp == MsMessageType.ADVERTISE || msg.MsgTyp == MsMessageType.GWINFO) {
        return true;
      }
      if(verbose) {
        Log.Debug("r {0}: {1}  {2}", gate.Addr2If(addr), MsMessage.HexPreview(buf, start, end - start, HEX_PREVIEW), msg.ToString());
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
                Log.Warning("r {0}: {1}  DhcpReq.hLen is too high", gate.Addr2If(addr), MsMessage.HexPreview(buf, start, end - start, HEX_PREVIEW));
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
          var dt = Topic.root.Get("/dev/" + cm.ClientId, true, Owner);
          dev = new MsDevice(this, dt);
          _devs.Add(dev);
          dt.SetAttribute(Topic.Attribute.Readonly);
          dt.SetField("editor", "MsStatus", Owner);
          dt.SetField("cctor.MqsDev", string.Empty, Owner);
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
