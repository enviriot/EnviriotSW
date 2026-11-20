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
  /// <summary>Плагин подключения устройств по MQTT-SN протоколу: шлюзы на UART и UDP, устройства и компилятор DPLC.</summary>
  /// <remarks>Устройство забирается подпиской на поле манифеста, а не реестром в ядре. 
  /// Идентификатор поля в эфире и его локальный путь - разные вещи, поэтому переименование поля в дереве устройства не касается.</remarks>
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 7)]
  [ExportMetadata("name", "MQTT_SN")]
  public class MQTT_SNPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/MQTT-SN";
    private const int HEX_PREVIEW = 32;  // число байтов кадра, попадающих в запись журнала
    private const Topic.Attribute CfgAttr = Topic.Attribute.Required | Topic.Attribute.Config;
#if DEBUG
    private const bool VerboseDefault = true;
#else
    private const bool VerboseDefault = false;
#endif

    private Topic _owner;
    private SubRec[] _cfg;
    private Random _rand;
    private bool _statistic;

    /// <summary>Флаги DevicePLC, TWI и шлюзов: здесь задаются начальные значения для всех экземпляров.</summary>
    /// <remarks>Статические, поскольку общие и топики: /$YS/MQTT-SN/DPLC_verbose задаёт одну настройку для всех PLC, 
    /// чтобы не требовалось передавать ссылку на плагин через два конструктора ради диагностического флага.</remarks>
    internal static bool verbosePlc, verboseTwi;
    internal static byte gwRadius;

    internal List<IMsGate> _gates;
    internal List<MsDevice> _devs;

    /// <summary>Поле манифеста, обозначающее устройство MQTT-SN и его модель.</summary>
    internal const string FLD_MODEL = "MQTT-SN.model";
    private SubRec _devSub;
    /// <summary>Схема диаграмм pa1 для редактора: выходы Func и меню переменных, см. LP_Schema.</summary>
    private X13.LogramPLC.LP_Schema.Watcher _lplcSchema;
    private X13.LogramPLC.LP_Status.Watcher _lplcStatus;
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
      // Подписка здесь, а не в Start, к запуску этого плагина (приоритет 7) его уже восстановил PersistentStorage (приоритет 2).
      _devSub = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, FLD_MODEL, MqsDevSub);
      // Не внутри DevicePLC: он существует только после регистрации pa1 устройством, а диаграмму редактируют и без подключения.
      _lplcSchema = new X13.LogramPLC.LP_Schema.Watcher(Topic.root);
      _lplcStatus = new X13.LogramPLC.LP_Status.Watcher(Topic.root);
      RPC.Register("MQTT_SN.RefreshPorts", RefreshPortsRpc);
      RPC.Register("MQTT_SN.RefreshNIC", RefreshNICRpc);
    }

    /// <summary>Все настройки плагина объявляются до создания первого шлюза.</summary>
    /// <remarks>Порядок существенен: оба шлюза читают <see cref="gwRadius"/> в конструкторах, поэтому к этому моменту значение должно быть настроено. 
    /// Именно поэтому EnsureCfg применяет значение до возврата: одной подписки недостаточно, поскольку Subscribe лишь ставит TopicEvent в очередь следующего тика Repo.
    /// <para>radius ограничивается при применении, а не в местах чтения: протокол задаёт диапазон 1..3; всё остальное означает отсутствие радиуса. 
    /// Так оба шлюза уже обрабатывали исходное значение.</para></remarks>
    public void Start() {
      Configure();
      X13.LogramPLC.LP_Tree.ImportTypes();
      _gates.Add(new MsGUdp(this));
      MsGSerial.Init(this);
    }

    /// <summary>Настройки применяются до открытия UDP/UART; повторный вызов не дублирует подписки.</summary>
    internal void Configure() {
      if(_cfg != null) return;
      _cfg = new SubRec[] {
        JsExtLib.EnsureCfg(Owner, "verbose", CfgAttr, v => verbose = v, VerboseDefault),
        JsExtLib.EnsureCfg(Owner, "statistic", CfgAttr, v => _statistic = v, false),
        JsExtLib.EnsureCfg(Owner, "radius", CfgAttr, v => gwRadius = (byte)(v >= 1 && v <= 3 ? v : 0), 1),
        EnsureVerbose("DPLC_verbose", "/$YS/DevicePLC/verbose", v => verbosePlc = v),
        EnsureVerbose("TWI_verbose", "/$YS/TWI/verbose", v => verboseTwi = v),
      };
    }

    /// <summary>Переносит прежний флаг, сохраняя значение; настроенный новый путь имеет приоритет.</summary>
    private SubRec EnsureVerbose(string name, string oldPath, Action<bool> apply) {
      var old = Topic.root.Get(oldPath, false);
      bool fallback = old == null ? VerboseDefault : old.GetState().AsBool(VerboseDefault);
      if(old != null) {
        var folder = old.parent;
        old.Remove(Owner);
        if(!folder.children.Any() && !folder.GetState().Defined) folder.Remove(Owner);
      }
      return JsExtLib.EnsureCfg(Owner, name, CfgAttr, apply, fallback);
    }

    public void Tick() {
      int i;
      for(i = _devs.Count - 1; i >= 0; i--) {
        _devs[i].Tick();
      }
      for(i = _gates.Count - 1; i >= 0; i--) {
        _gates[i].Tick();
      }
      _lplcStatus?.Tick();
    }

    public void Stop() {
      // Получено в Init, освобождается здесь: Stop отменяет и частично выполненную инициализацию.
      if(_devSub != null) {
        _devSub.Dispose();
        _devSub = null;
      }
      if(_lplcSchema != null) {
        _lplcSchema.Dispose();
        _lplcSchema = null;
      }
      if(_lplcStatus != null) {
        _lplcStatus.Dispose();
        _lplcStatus = null;
      }
      // EnsureCfg передаёт владение подпиской вызывающему коду, поэтому освобождаем здесь.
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
    internal void PlcBuildRpc(Topic t, JSC.JSValue arg) {
      if(X13.LogramPLC.LP_Schema.IsPa1(t)) {
        string fingerprint;
        new X13.LogramPLC.LP_Program(t, X13.DevicePLC.EP_Target.EspNode).Compile(true, out fingerprint);
        return;
      }
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

    /// <summary>Создаёт объект устройства для топика с полем MQTT-SN.model.</summary>
    /// <remarks>Сопоставление по имени, а не по топику. Переименованное устройство сохраняет прежний объект.</remarks>
    private void MqsDevSub(TopicEvent p, SubRec sr) {
      if(p.Kind != EventKind.Created && p.Kind != EventKind.FieldChanged && p.Kind != EventKind.Snapshot) {
        return;
      }
      Topic t = p.Source;
      if(!t.GetField(FLD_MODEL).Defined) {
        return;
      }
      var dev = _devs.FirstOrDefault(z => z.name == t.name);
      if(dev == null) {
        _devs.Add(new MsDevice(this, t));
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
        // Причина, а не просто неверное сообщение: неизвестный тип, усечённый кадр и тело, противоречащее заголовку, прежде возвращали один и тот же null.
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
          dt.SetField(FLD_MODEL, string.Empty, Owner);
        }
        dev._gate = gate;
        dev.addr = addr;
        dev.Connect(cm);
        foreach(var dub in _devs.Where(z => z != dev && z.CheckAddr(addr) && z._gate == gate).ToArray()) {
          dub.addr = null;
          dub._gate = null;
          dub.State = State.Disconnected;
        }
      } else {
        MsDevice dev = _devs.FirstOrDefault(z => z.addr != null && z.addr.SequenceEqual(addr) && z._gate == gate);
        if(dev != null && (dev.State != State.Disconnected && dev.State != State.Lost)) {
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
  /// <summary>Уровни качества обслуживания</summary>
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
