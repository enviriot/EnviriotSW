///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using X13.Repository;

namespace X13.Periphery {
  internal class MsDevice : IMsGate {
    private const int ACK_TIMEOUT = 600;
    private const ushort RTC_EXCH = 0xFF07;
    private const ushort LOG_D_ID = 0xFFE0;
    private const ushort LOG_I_ID = 0xFFE1;
    private const ushort LOG_W_ID = 0xFFE2;
    private const ushort LOG_E_ID = 0xFFE3;
    private static Random _rand;
    private static Func<string, JSC.JSValue> _createConv;
    private static byte[] _baEmty = new byte[0];
    private static byte[] _baFalse = new byte[] { 0 };
    private static byte[] _baTrue = new byte[] { 1 };


    public static byte[] Serialize(TopicInfo ti) {
      var val = ti.topic.GetState();
      var dType = ti.dType;
      if(ti.convOut != null) {
        try {
          val = ti.convOut.Call(ti.owner._self, new JSC.Arguments { ti.topic.name, val });
        }
        catch(Exception ex) {
          if(ti.owner._pl.verbose) {
            Log.Warning("{0}.MQTT-SN.convIn - {1}", ti.topic, ex.Message);
          }
        }
      }
      return Serialize(val, dType);
    }
    private static byte[] Serialize(JSC.JSValue val, DType dType) {
      switch(dType & DType.TypeMask) {
      case DType.Boolean:
        return ((bool)val) ? _baTrue : _baFalse;
      case DType.Integer: {
          List<byte> ret = new List<byte>();
          long vo = (long)val;
          long v = vo;
          do {
            ret.Add((byte)v);
            v = v >> 8;
          } while(vo < 0 ? (v < -1 || (ret[ret.Count - 1] & 0x80) == 0) : (v > 0 || (ret[ret.Count - 1] & 0x80) != 0));
          return ret.ToArray();
        }
      case DType.String: {
          var s = val.Value as string;
          if(string.IsNullOrEmpty(s)) {
            return _baEmty;
          } else {
            return Encoding.Default.GetBytes(s);
          }
        }
      case DType.ByteArray: {
          ByteArray ba;
          if((ba = val as ByteArray) != null || (ba = val.Value as ByteArray) != null) {
            return ba.GetBytes();
          }
        }
        break;
      }
      return _baEmty;
    }
    private static JSL.Function CreateConv(string body) {
      return _createConv(body) as JSL.Function;
    }
    static MsDevice() {
      _rand = new Random((int)DateTime.Now.Ticks);
      _createConv = (JsExtLib.Context.Eval("Function('src', 'return Function(\"name\", \"value\", src);')") as JSL.Function).MakeDelegate(typeof(Func<string, JSC.JSValue>)) as Func<string, JSC.JSValue>;
    }

    private string _oldName;
    private List<SubRec> _subsscriptions;
    private SubRec _srOwner;
    private Queue<MsMessage> _sendQueue;
    private List<TopicInfo> _topics;
    private MQTT_SNPl _pl;
    private State _state;
    private bool _waitAck;
    private int _duration;
    private int _messageIdGen;
    private DateTime _toActive;
    private string _willPath;
    private byte[] _wilMsg;
    private bool _willRetain;
    private MsPublish _lastInPub;
    private bool _has_RTC;
    private DateTime _last_RTC;
    private JSC.JSObject _self;
    private byte[] _suppressedInputs;

    public readonly Topic owner;
    public IMsGate _gate;
    public byte[] addr;

    public MsDevice(MQTT_SNPl pl, Topic owner) {
      this.owner = owner;
      this._oldName = owner.name;
      this._pl = pl;

      _subsscriptions = new List<SubRec>(4);
      _sendQueue = new Queue<MsMessage>();
      _topics = new List<TopicInfo>(16);
      _duration = 3000;
      _messageIdGen = 0;
      _srOwner = this.owner.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT-SN", OwnerChanged);

      _self = JSC.JSObject.CreateObject();
      _self["GetState"] = JSC.JSValue.Marshal(new Func<string, JSC.JSValue>(GetState));
      _self["GetField"] = JSC.JSValue.Marshal(new Func<string, string, JSC.JSValue>(GetField));
    }

    private JSC.JSValue GetState(string path) {
      Topic t;
      if(owner.Exist(path, out t)) {
        return t.GetState();
      }
      return JSC.JSValue.NotExists;
    }
    private JSC.JSValue GetField(string path, string field) {
      Topic t;
      if(owner.Exist(path, out t)) {
        return t.GetField(field);
      }
      return JSC.JSValue.NotExists;

    }

    private void OwnerChanged(Perform p, SubRec sr) {
      if(p.art == Perform.Art.remove) {
        _pl._devs.Remove(this);
        this.Stop();
        return;
      }
      if(!(state == State.Connected || state == State.ASleep || state == State.AWake) || p.prim == owner) {
        return;
      }
      if(p.art == Perform.Art.changedField) {
        var fp = "." + (p.FieldPath ?? string.Empty);
        var pt = PredefinedTopics.FirstOrDefault(z => z.Item2 == fp);
        if(pt == null || pt.Item1 >= 0xFFC0) {
          return;
        }
        var val = owner.GetField(p.FieldPath);
        if(!val.IsNull) {
          Send(new MsPublish(pt.Item1, Serialize(val, pt.Item3)));
        }
      } else if(p.art == Perform.Art.move) {
        if(_oldName != owner.name) {
          Send(new MsPublish(0xFF00, Encoding.UTF8.GetBytes(owner.name)));  // _sName
          _state = State.Disconnected;
          _oldName = owner.name;
        }
      }
    }

    #region IMsGate Members
    public void SendGw(byte[] addr, MsMessage msg) {
      if(_gate != null && addr != null) {
        _gate.SendGw(this, new MsForward(addr, msg));
      }
    }
    public void SendGw(MsDevice dev, MsMessage msg) {
      if(_gate != null) {
        _gate.SendGw(this, new MsForward(dev.addr, msg));
      }
    }
    public byte gwIdx { get { return (byte)(_gate == null ? 0xFF : _gate.gwIdx); } }
    public byte gwRadius { get { return 0; } }
    public string name { get { return owner.name; } }
    public string Addr2If(byte[] addr) {
      return _gate != null ? _gate.Addr2If(addr) : string.Concat(BitConverter.ToString(addr), " via ", this.name);
    }
    public void Stop() {
      //if(_nodes == null || !_nodes.Any()) {
      //  return;
      //}
      //var nodes = _nodes.ToArray();
      //for(int i = 0; i < nodes.Length; i++) {
      //  nodes[i].Stop();
      //}
      //if(_gate != null) {
      //  _gate.SendGw(this, new MsDisconnect());
      //  Stat(true, MsMessageType.DISCONNECT, false);
      //}
      //state = State.Disconnected;
      var sr = Interlocked.Exchange(ref _srOwner, null);
      if(sr != null) {
        sr.Dispose();
      }
    }
    #endregion IMsGate Members

    public State state {
      get {
        return _state;
      }
      set {
        if(_state != value) {
          _state = value;
          int st;
          if(_state == State.Connected || _state == State.AWake) {
            st = 1;
          } else if(_state == State.ASleep) {
            st = 2;
          } else {
            st = 0;
          }
          var v = JSC.JSObject.CreateObject();
          v["st"] = st;
          if(st != 0 && _gate != null) {
            v["via"] = _gate.name;
          }
          owner.SetState(v);
        }
      }
    }

    /// <summary>Check Address for DHCP</summary>
    /// <param name="addr">checked address</param>
    /// <returns>busy</returns>
    public bool CheckAddr(byte[] addr) {
      if(addr == null) {
        return false;
      }
      if(this.addr != null && this.addr.Length - 1 == addr.Length && this.addr.Skip(1).SequenceEqual(addr)) {
        return true;
      }
      ByteArray ba;
      for(int i = 2; i < 5; i++) {
        var a = owner.GetField(string.Format("MQTT-SN.phy{0}_addr", i));
        if((ba = a as ByteArray) != null || (ba = a.Value as ByteArray) != null && ba.GetBytes().Length == addr.Length && ba.GetBytes().SequenceEqual(addr)) {
          return true;
        }
      }
      return false;
    }
    public void Connect(MsConnect msg) {
      if(msg.CleanSession) {
        foreach(var s in _subsscriptions) {
          s.Dispose();
        }
        _subsscriptions.Clear();
        foreach(var ts in _topics) {
          ts.Dispose();
        }
        _topics.Clear();
        lock(_sendQueue) {
          _sendQueue.Clear();
        }
        _waitAck = false;
        //  if(_statistic.value) {
        //    StatConnectTime();
        //  }
      } else if(_state == State.Lost || _state == State.Disconnected) {
        Send(new MsDisconnect());
        return;
      }
      _duration = msg.Duration * 1100;
      ResetTimer();
      if(msg.Will) {
        _willPath = string.Empty;
        _wilMsg = null;
        if(msg.CleanSession) {
          Log.Info("{0}.state {1} => WILLTOPICREQ", owner.path, state);
        }
        state = State.WillTopic;
        Send(new MsMessage(MsMessageType.WILLTOPICREQ));
      } else {
        if(msg.CleanSession) {
          Log.Info("{0} {1} => PreConnect", owner.path, state);
          state = State.PreConnect;
        } else {
          state = State.Connected;
        }
        Send(new MsConnack(MsReturnCode.Accepted));
      }
      //via = _gate.name;

      //if(_statistic.value) {
      //  Stat(false, MsMessageType.CONNECT, msg.CleanSession);
      //}
    }
    public void ProcessInPacket(MsMessage msg) {
      //if(_statistic.value && msg.MsgTyp != MsMessageType.EncapsulatedMessage && msg.MsgTyp != MsMessageType.PUBLISH) {
      //  Stat(false, msg.MsgTyp);
      //}
      switch(msg.MsgTyp) {
      case MsMessageType.WILLTOPIC: {
          var tmp = msg as MsWillTopic;
          if(state == State.WillTopic) {
            _willPath = tmp.Path;
            _willRetain = tmp.Retain;
            state = State.WillMsg;
            ProccessAcknoledge(msg);
          }
        }
        break;
      case MsMessageType.WILLMSG: {
          var tmp = msg as MsWillMsg;
          if(state == State.WillMsg) {
            _wilMsg = tmp.Payload;
            Log.Info("{0}.state {1} => WILLTOPICREQ", owner.path, state);
            state = State.PreConnect;
            ProccessAcknoledge(msg);
            Send(new MsConnack(MsReturnCode.Accepted));
          }
        }
        break;
      case MsMessageType.SUBSCRIBE: {
          var tmp = msg as MsSubscribe;

          SyncMsgId(msg.MessageId);
          int idx;
          ushort topicId = tmp.topicId;
          TopicInfo ti = null;
          MsReturnCode retCode = MsReturnCode.Accepted;
          Topic t = null;
          SubRec.SubMask mask = SubRec.SubMask.Value | SubRec.SubMask.Field;

          if(tmp.topicIdType != TopicIdType.Normal) {
            ti = GetTopicInfo(tmp.topicId, tmp.topicIdType);
            if(ti != null) {
              topicId = ti.TopicId;
              t = ti.topic;
            } else {
              retCode = MsReturnCode.InvalidTopicId;
            }
          } else if((idx = tmp.path.IndexOfAny(new[] { '+', '#' })) < 0) {
            ti = GetTopicInfo(tmp.path, false);
            if(ti != null) {
              topicId = ti.TopicId;
              t = ti.topic;
            } else {
              retCode = MsReturnCode.InvalidTopicId;
            }
          } else if(idx != tmp.path.Length - 1) {
            retCode = MsReturnCode.InvalidTopicId;
          } else {
            mask |= tmp.path[idx] == '#' ? SubRec.SubMask.All : SubRec.SubMask.Chldren;
            if(tmp.path.Length > 1) {
              if(tmp.path[0] == '/' && !tmp.path.StartsWith(owner.path)) {
                retCode = MsReturnCode.InvalidTopicId;
              } else {
                t = owner.Get(tmp.path.Substring(0, idx), true, owner);
              }
            } else {
              t = owner;
            }
          }
          Send(new MsSuback(tmp.qualityOfService, topicId, msg.MessageId, retCode));
          if(state == State.PreConnect) {
            state = State.Connected;
            UpdateSuppressedInputs();
          }
          if(t != null) {
            SubRec s = t.Subscribe(mask, "MQTT-SN.", PublishTopic);
            _subsscriptions.Add(s);
          }
        }
        break;
      case MsMessageType.REGISTER: {
          var tmp = msg as MsRegister;
          ResetTimer();
          try {
            TopicInfo ti;

            ti = GetTopicInfo(tmp.TopicPath, false);
            if(ti != null) {
              Send(new MsRegAck(ti.TopicId, tmp.MessageId, MsReturnCode.Accepted));
              if((ti.dType == DType.Boolean || ti.dType == DType.Integer) && !ti.topic.GetState().Defined) {
                SetValue(ti, new byte[] { 0 }, false);
              }
            } else {
              Send(new MsRegAck(0, tmp.MessageId, MsReturnCode.NotSupportes));
              Log.Warning("Unknown variable type by register {0}, {1}", owner.path, tmp.TopicPath);
            }
          }
          catch(Exception ex) {
            Send(new MsRegAck(0, tmp.MessageId, MsReturnCode.Congestion));
            Log.Warning("Error by register {0}, {1}", owner.path, tmp.TopicPath, ex.Message);
          }
        }
        break;
      case MsMessageType.REGACK: {
          var tmp = msg as MsRegAck;
          ProccessAcknoledge(tmp);
          TopicInfo ti = _topics.FirstOrDefault(z => z.TopicId == tmp.TopicId);
          if(ti == null) {
            if(tmp.TopicId != 0xFFFF) { // 0xFFFF - remove variable
              Log.Warning("{0} RegAck({1:X4}) for unknown variable", owner.path, tmp.TopicId);
            }
            return;
          }
          if(tmp.RetCode == MsReturnCode.Accepted) {
            ti.registred = true;
            if(ti.it != TopicIdType.PreDefined) {
              Send(new MsPublish(ti));
              if(ti.topic.GetField("MQTT-SN.tag").ValueType != NiL.JS.Core.JSValueType.String) {
                ti.topic.SetField("MQTT-SN.tag", ti.tag, owner);
              }
            }
          } else {
            Log.Warning("{0} registred failed: {1}", ti.topic.path, tmp.RetCode.ToString());
            _topics.Remove(ti);
            ti.topic.Remove(owner);
            //UpdateInMute();
          }
        }
        break;
      case MsMessageType.PUBLISH: {
          var tmp = msg as MsPublish;
          //    if(_statistic.value) {
          //      Stat(false, msg.MsgTyp, tmp.Dup);
          //    }
          TopicInfo ti = _topics.Find(z => z.TopicId == tmp.TopicId && z.it == tmp.topicIdType);
          if(ti == null && tmp.topicIdType != TopicIdType.Normal) {
            ti = GetTopicInfo(tmp.TopicId, tmp.topicIdType, false);
          }
          if(tmp.qualityOfService == QoS.AtMostOnce || (tmp.qualityOfService == QoS.MinusOne && (tmp.topicIdType == TopicIdType.PreDefined || tmp.topicIdType == TopicIdType.ShortName))) {
            ResetTimer();
          } else if(tmp.qualityOfService == QoS.AtLeastOnce) {
            SyncMsgId(tmp.MessageId);
            Send(new MsPubAck(tmp.TopicId, tmp.MessageId, ti != null ? MsReturnCode.Accepted : MsReturnCode.InvalidTopicId));
          } else if(tmp.qualityOfService == QoS.ExactlyOnce) {
            SyncMsgId(tmp.MessageId);
            // QoS2 not supported, use QoS1
            Send(new MsPubAck(tmp.TopicId, tmp.MessageId, ti != null ? MsReturnCode.Accepted : MsReturnCode.InvalidTopicId));
          } else {
            throw new NotSupportedException("QoS -1 not supported " + owner.path);
          }
          if(ti != null) {
            switch(ti.dType & ~DType.TypeMask) {
            case DType.None:
              if(!tmp.Dup || _lastInPub == null || tmp.MessageId != _lastInPub.MessageId) {  // else arready recieved
                SetValue(ti, tmp.Data, tmp.Retained);
              }
              _lastInPub = tmp;
              break;
            case DType.RTC:
              if(tmp.Data != null && tmp.Data.Length == 6) {
                try {
                  _last_RTC = new DateTime((DateTime.Now.Year / 100) * 100 + BCD2int(tmp.Data[5]), BCD2int(tmp.Data[4] & 0x1F), BCD2int(tmp.Data[3] & 0x3F)
                    , ((tmp.Data[2] & 0x40) != 0 ? 12 : 0) + BCD2int(tmp.Data[2] & 0x3F), BCD2int(tmp.Data[1] & 0x7F), BCD2int(tmp.Data[0] & 0x7F));
                }
                catch(Exception ex) {
                  Log.Warning("{0}.RTC({1}) - {2}", owner.name, BitConverter.ToString(tmp.Data), ex.Message);
                }
                if(Math.Abs((_last_RTC - DateTime.Now).TotalSeconds) > 2) {
                  _last_RTC = new DateTime(1);
                }
                _has_RTC = true;
              }
              break;
            case DType.LOG: {
                string str = string.Format("{0} msgId={2:X4}  msg={1}", owner.name, tmp.Data == null ? "null" : (BitConverter.ToString(tmp.Data) + "[" + Encoding.ASCII.GetString(tmp.Data.Select(z => (z < 0x20 || z > 0x7E) ? (byte)'.' : z).ToArray()) + "]"), tmp.MessageId);
                switch(tmp.TopicId) {
                case LOG_D_ID:
                  Log.Debug("{0}", str);
                  break;
                case LOG_I_ID:
                  Log.Info("{0}", str);
                  break;
                case LOG_W_ID:
                  Log.Warning("{0}", str);
                  break;
                case LOG_E_ID:
                  Log.Error("{0}", str);
                  break;
                }
              }
              break;
            case DType.TWI:
            case DType.PLC: {
                if(ti.extension != null) {
                  ti.extension.Recv(tmp.Data);
                }
              }
              break;
            }
          }
        }
        break;
      case MsMessageType.PUBACK: {
          ProccessAcknoledge(msg);
        }
        break;
      case MsMessageType.PINGREQ: {
          var tmp = msg as MsPingReq;
          if(state == State.ASleep) {
            if(string.IsNullOrEmpty(tmp.ClientId) || tmp.ClientId == owner.name) {
              state = State.AWake;
              ProccessAcknoledge(msg);    // resume send proccess
            } else {
              SendGw(this, new MsDisconnect());
              state = State.Lost;
              Log.Warning("{0} PingReq from unknown device: {1}", owner.path, tmp.ClientId);
            }
          } else {
            ResetTimer();
            if(_gate != null) {
              _gate.SendGw(this, new MsMessage(MsMessageType.PINGRESP));
              //if(_statistic.value) {
              //  Stat(true, MsMessageType.PINGRESP, false);
              //}
            }
          }
        }
        break;
      case MsMessageType.DISCONNECT:
        Disconnect((msg as MsDisconnect).Duration);
        break;
      case MsMessageType.CONNECT:
        Connect(msg as MsConnect);
        break;
      case MsMessageType.EncapsulatedMessage: {
          var fm = msg as MsForward;
          if(fm.msg == null) {
            if(_pl.verbose) {
              Log.Warning("bad message {0}:{1}", _gate, fm.ToString());
            }
            return;
          }
          if(fm.msg.MsgTyp == MsMessageType.SEARCHGW) {
            _gate.SendGw(this, new MsGwInfo(gwIdx));
          } else if(fm.msg.MsgTyp == MsMessageType.DHCP_REQ) {
            var dr = fm.msg as MsDhcpReq;
            //******************************
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
                      resp[j] = (byte)_rand.Next((i < 3 && hLen == 1) ? 32 : 1, (i < 3 && hLen == 1) ? 126 : (j == 0 ? 254 : 255));
                    }
                    if(_pl._devs.All(z => !z.CheckAddr(resp))) {
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
                if(_pl.verbose) {
                  Log.Warning("{0}:{1} DhcpReq.hLen is too high", BitConverter.ToString(fm.addr), fm.msg.ToString());
                }
                ackAddr = null;
                break;
              }
            }
            if(ackAddr != null) {
              _gate.SendGw(this, new MsForward(fm.addr, new MsDhcpAck(gwIdx, dr.xId, ackAddr.ToArray())));
            }
            //******************************
          } else {
            if(fm.msg.MsgTyp == MsMessageType.CONNECT) {
              var cm = fm.msg as MsConnect;
              MsDevice dev = _pl._devs.FirstOrDefault(z => z.owner != null && z.owner.name == cm.ClientId);
              if(dev == null) {
                dev = new MsDevice(_pl, Topic.root.Get("/dev/" + cm.ClientId, true, owner));
                _pl._devs.Add(dev);
              }
              dev._gate = this;
              dev.addr = fm.addr;
              dev.Connect(cm);
              foreach(var dub in _pl._devs.Where(z => z != dev && z.CheckAddr(addr) && z._gate == this).ToArray()) {
                dub.addr = null;
                dub._gate = null;
                dub.state = State.Disconnected;
              }
            } else {
              MsDevice dev = _pl._devs.FirstOrDefault(z => z.addr != null && z.addr.SequenceEqual(fm.addr) && z._gate == this);
              if(dev != null
                && ((dev.state != State.Disconnected && dev.state != State.Lost)
                  || (fm.msg.MsgTyp == MsMessageType.PUBLISH && (fm.msg as MsPublish).qualityOfService == QoS.MinusOne))) {
                dev.ProcessInPacket(fm.msg);
              } else if(fm.msg.MsgTyp == MsMessageType.PUBLISH && (fm.msg as MsPublish).qualityOfService == QoS.MinusOne) {
                var tmp = fm.msg as MsPublish;
                if(tmp.topicIdType == TopicIdType.PreDefined && tmp.TopicId >= LOG_D_ID && tmp.TopicId <= LOG_E_ID) {
                  string str = string.Format("{0}: msgId={2:X4} msg={1}", BitConverter.ToString(this.addr), tmp.Data == null ? "null" : (BitConverter.ToString(tmp.Data) + "[" + Encoding.ASCII.GetString(tmp.Data.Select(z => (z < 0x20 || z > 0x7E) ? (byte)'.' : z).ToArray()) + "]"), tmp.MessageId);
                  switch(tmp.TopicId) {
                  case LOG_D_ID:
                    Log.Debug(str);
                    break;
                  case LOG_I_ID:
                    Log.Info(str);
                    break;
                  case LOG_W_ID:
                    Log.Warning(str);
                    break;
                  case LOG_E_ID:
                    Log.Error(str);
                    break;
                  }
                }
              } else {
                if(_pl.verbose) {
                  if(dev == null || dev.owner == null) {
                    Log.Debug("{0} via {1} unknown device", BitConverter.ToString(fm.addr), this.name);
                  } else {
                    Log.Debug("{0} via {1} inactive", dev.owner.name, this.name);
                  }
                }
                _gate.SendGw(this, new MsForward(fm.addr, new MsDisconnect()));
              }
            }
          }
        }
        break;
      }
    }
    public void Tick() {
      if(_state != State.Lost && _state != State.Disconnected && _toActive < DateTime.Now) {
        MsMessage msg = null;
        lock(_sendQueue) {
          if(_sendQueue.Count > 0) {
            msg = _sendQueue.Peek();
          }
        }
        _waitAck = false;
        if(msg == null) {
          ResetTimer();
          return;
        } else {
          //Log.Debug("$ {0}.TimeOut try={1} msg={2}", owner.name, msg.tryCnt, msg);
          if(!msg.IsRequest || msg.tryCnt > 0) {
            SendIntern(msg);
            return;
          }
        }
        state = State.Lost;
        if(owner != null) {
          Disconnect();
          //if(_statistic.value) {
          //  Stat(false, MsMessageType.GWINFO);
          //}
          Log.Warning("{0} Lost", owner.path);
        }
        lock(_sendQueue) {
          _sendQueue.Clear();
        }
        if(_gate != null) {
          _gate.SendGw(this, new MsDisconnect());
          //if(_statistic.value) {
          //  Stat(true, MsMessageType.DISCONNECT, false);
          //}
        }
        return;
      }
      if(_state == State.Connected || _state==State.AWake) {
        foreach(var t in _topics) {
          if(t.extension != null) {
            try {
              t.extension.Tick();
            }
            catch(Exception ex) {
              Log.Warning("{0}.Tick - {1}", t.topic.path, ex.ToString());
            }
          }
        }
        if(_has_RTC) {
          var now = DateTime.Now;
          if((now - _last_RTC).TotalHours > 1) {
            _last_RTC = now;
            var pl = new byte[6] {int2BCD(_last_RTC.Second), int2BCD(_last_RTC.Minute), int2BCD(_last_RTC.Hour)
          , int2BCD(_last_RTC.Day), (byte)((( ( (_last_RTC.DayOfWeek==DayOfWeek.Sunday)?7:(int)_last_RTC.DayOfWeek))  <<5) |  int2BCD(_last_RTC.Month)),int2BCD(_last_RTC.Year%100)};
            Send(new MsPublish(RTC_EXCH, pl));
          }
        }
      }
    }

    private ushort CalculateTopicId(string path) {
      ushort id;
      byte[] buf = Encoding.UTF8.GetBytes(path);
      id = Crc16.ComputeChecksum(buf);
      while(id == 0 || id == 0xF000 || id == 0xFFFF || _topics.Any(z => z.it == TopicIdType.Normal && z.TopicId == id)) {
        id = Crc16.UpdateChecksum(id, (byte)_rand.Next(0, 255));
      }
      return id;
    }
    /// <summary>Find or create TopicInfo by Topic</summary>
    /// <param name="tp">Topic as key</param>
    /// <param name="sendRegister">Send MsRegister for new TopicInfo</param>
    /// <returns>found TopicInfo or null</returns>
    private TopicInfo GetTopicInfo(Topic tp, bool sendRegister = true, string tag = null) {
      if(tp == null) {
        return null;
      }
      TopicInfo rez = null;
      bool field = !string.IsNullOrEmpty(tag) && tag[0] == '.';
      for(int i = _topics.Count - 1; i >= 0; i--) {
        if(_topics[i].topic == tp && (!field || _topics[i].tag == tag)) {
          rez = _topics[i];
          break;
        }
      }
      if(rez == null) {
        if(tag == null) {
          var siv = tp.GetField("MQTT-SN.tag");
          if(siv.ValueType != NiL.JS.Core.JSValueType.String || (tag = siv.Value as string) == null) {
            if(tp != owner) {
              tag = (tp.path.StartsWith(owner.path)) ? tp.path.Substring(owner.path.Length + 1) : tp.path;
            } else {
              return null;
            }
          } else if((tag = siv.Value as string) == "---") {
            return null;
          }
        }
        rez = new TopicInfo();
        rez.owner = this;
        rez.topic = tp;
        rez.tag = tag;
        var pt = PredefinedTopics.FirstOrDefault(z => z.Item2 == tag);
        UpdateConverters(rez);
        if(pt != null) {
          rez.TopicId = pt.Item1;
          rez.dType = pt.Item3;
          rez.it = TopicIdType.PreDefined;
          rez.registred = true;
        } else {
          var nt = _NTTable.FirstOrDefault(z => tag.StartsWith(z.Item1));
          if(nt != null) {
            rez.TopicId = CalculateTopicId(rez.topic.path);
            rez.dType = nt.Item2;
            rez.it = TopicIdType.Normal;
          } else {
            Log.Warning(owner.path + ".register(" + tag + ") - unknown type");
            return null;
          }
          _topics.Add(rez);
        }
        var extMask = rez.dType & ~DType.TypeMask;
        if(extMask == DType.TWI && rez.tag.StartsWith("Ta")) {
          rez.extension = new TWI(rez.topic, rez.PublishWithPayload);
        } else if(extMask == DType.PLC && rez.tag == "pa0") {
          var p = new DevicePLC(rez.topic, rez.PublishWithPayload);
          rez.extension = p;
          _pl._plcs.Add(p);
        }
        //UpdateInMute();
      }
      if(!rez.registred) {
        if(sendRegister) {
          Send(new MsRegister(rez.TopicId, rez.tag));
        } else {
          rez.registred = true;
        }
      }
      return rez;
    }

    private static void UpdateConverters(TopicInfo ti) {
      JSC.JSValue msTmp;
      string sTmp;
      if((msTmp = ti.topic.GetField("MQTT-SN.convIn")).ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(sTmp = msTmp.Value as string)) {
        try {
          ti.convIn = CreateConv(sTmp);
        }
        catch(Exception ex) {
          ti.convIn = null;
          Log.Warning("{0}.MQTT-SN.convIn - {1}", ti.topic.path, ex.Message);
        }
      } else {
        ti.convIn = null;
      }
      if((msTmp = ti.topic.GetField("MQTT-SN.convOut")).ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(sTmp = msTmp.Value as string)) {
        try {
          ti.convOut = CreateConv(sTmp);
        }
        catch(Exception ex) {
          ti.convOut = null;
          Log.Warning("{0}.MQTT-SN.convOut - {1}", ti.topic.path, ex.Message);
        }
      } else {
        ti.convOut = null;
      }
    }
    private TopicInfo GetTopicInfo(string tag, bool sendRegister = true) {
      if(string.IsNullOrEmpty(tag)) {
        return null;
      }
      TopicInfo ti;
      Topic cur = null;
      int idx = tag.LastIndexOf('/');
      string cName = tag.Substring(idx + 1);
      if(tag[0] == '.') {
        cur = owner;
      } else {
        cur = owner.all.FirstOrDefault(z => {
          var nf = z.GetField("MQTT-SN.tag");
          return nf.ValueType == NiL.JS.Core.JSValueType.String && (nf.Value as string) == tag;
        });
        if(cur == null) {
          if(tag[0] == '/' && !tag.StartsWith(owner.path)) {
            cur = owner.Get(tag, false, owner);
            if(cur == null) {
              return null;
            }
          } else {
            cur = owner.Get(tag, true, owner);
          }
          cur.SetField("MQTT-SN.tag", tag, owner);
          if(tag.StartsWith("Ta")) {
            cur.SetField("type", "TWI", owner);
            cur.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly);
          } else if(tag == "pa0") {
            cur.SetField("type", "MsExt/DevicePLC", owner);
            cur.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly);
            var src = cur.Get("src", true, owner);
            src.SetField("editor", "JS", owner);
            src.SetField("MQTT-SN.tag", "---", owner);
            src.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
            src.SetState("", owner);
          }
        }
      }
      ti = GetTopicInfo(cur, sendRegister, tag);
      return ti;
    }
    private TopicInfo GetTopicInfo(ushort topicId, TopicIdType topicIdType, bool sendRegister = true) {
      var ti = _topics.Find(z => z.it == topicIdType && z.TopicId == topicId);
      if(ti == null) {
        if(topicIdType == TopicIdType.PreDefined) {
          var pt = PredefinedTopics.FirstOrDefault(z => z.Item1 == topicId);
          if(pt != null) {
            ti = GetTopicInfo(pt.Item2, sendRegister);
          }
        } else if(topicIdType == TopicIdType.ShortName) {
          ti = GetTopicInfo(string.Format("{0}{1}", (char)(topicId >> 8), (char)(topicId & 0xFF)), sendRegister);
        }
        if(ti != null) {
          ti.it = topicIdType;
        }
      }
      return ti;
    }

    private void PublishTopic(Perform p, SubRec sb) {
      if(!(state == State.Connected || state == State.ASleep || state == State.AWake) || (p.prim == owner && p.art != Perform.Art.subscribe) || p.src == owner) {
        return;
      }
      if(p.art == Perform.Art.create) {
        GetTopicInfo(p.src);
        return;
      }
      TopicInfo ti = null;
      for(int i = _topics.Count - 1; i >= 0; i--) {
        if(_topics[i].topic == p.src) {
          ti = _topics[i];
          break;
        }
      }
      if(p.art == Perform.Art.changedField && ti != null) {
        UpdateConverters(ti);
        UpdateSuppressedInputs();
      }
      if(ti == null && (p.art == Perform.Art.changedState || p.art == Perform.Art.subscribe)) {
        ti = GetTopicInfo(p.src, true);
      }
      if(ti == null || ti.TopicId >= 0xFFC0 || !ti.registred) {
        return;
      }
      if((p.art == Perform.Art.changedState || p.art == Perform.Art.subscribe)) {
        if((ti.dType & ~DType.TypeMask) == DType.None) {
          Send(new MsPublish(ti));
        }
      } else if(p.art == Perform.Art.remove) {          // Remove by device
        if(ti.it == TopicIdType.Normal) {
          Send(new MsRegister(0xFFFF, ti.tag));
        }
        _topics.Remove(ti);
      }
    }
    internal void PublishWithPayload(Topic t, byte[] payload) {
      if(state == State.Disconnected || state == State.Lost || _topics == null) {
        return;
      }
      TopicInfo ti = null;
      for(int i = _topics.Count - 1; i >= 0; i--) {
        if(_topics[i].topic == t) {
          ti = _topics[i];
          break;
        }
      }
      if(ti == null) {
        return;
      }
      if(_pl.verbose) {
        Log.Debug("{0}.Snd {1}", t.name, BitConverter.ToString(payload));
      }
      Send(new MsPublish(ti) { Data = payload });
    }
    private void SetValue(TopicInfo ti, byte[] msgData, bool retained) {
      if(ti != null) {
        if(!ti.topic.path.StartsWith(owner.path)) {
          return;     // not allowed publish
        }
        JSC.JSValue val;
        switch(ti.dType) {
        case DType.Boolean:
          val = new JSL.Boolean((msgData[0] != 0));
          break;
        case DType.Integer: {
            long rv = (msgData[msgData.Length - 1] & 0x80) == 0 ? 0 : -1;
            for(int i = msgData.Length - 1; i >= 0; i--) {
              rv <<= 8;
              rv |= msgData[i];
            }
            val = new JSL.Number(rv);
          }
          break;
        case DType.String:
          val = new JSL.String(Encoding.Default.GetString(msgData));
          break;
        case DType.ByteArray: {
            val = new ByteArray(msgData);
          }
          break;
        default:
          return;
        }
        if(ti.tag[0] == '.') {
          if(ti.tag == ".MQTT-SN.tag" && val.ValueType == JSC.JSValueType.String) {
            var v = val.Value as string;
            var type = "MQTT-SN/" + v.Substring(0, v.IndexOf('.'));
            ti.topic.SetField("type", type, owner);
          }
          ti.topic.SetField(ti.tag.Substring(1), val, owner);
        } else {
          if(retained) {
            if(!ti.topic.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
              ti.topic.SetAttribute(Topic.Attribute.DB);
            }
          } else {
            if(ti.topic.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
              ti.topic.ClearAttribute(Topic.Attribute.DB);
            }
          }
          if(ti.it == TopicIdType.PreDefined && ti.TopicId >= 0xFFC0) {
            ti.topic.SetAttribute(Topic.Attribute.Readonly);
          }
          if(ti.convIn != null) {
            try {
              val = ti.convIn.Call(this._self, new JSC.Arguments { ti.topic.name, val });
            }
            catch(Exception ex) {
              if(_pl.verbose) {
                Log.Warning("{0}.MQTT-SN.convIn - {1}", ti.topic, ex.Message);
              }
            }
          }
          ti.topic.SetState(val, owner);
        }
      }
    }
    private ushort NextMsgId() {
      int rez = Interlocked.Increment(ref _messageIdGen);
      Interlocked.CompareExchange(ref _messageIdGen, 1, 0xFFFF);
      return (ushort)rez;
    }
    private void SyncMsgId(ushort p) {
      ResetTimer();
      int nid = p;
      if(nid == 0xFFFE) {
        nid++;
        nid++;
      }
      if(nid > (int)_messageIdGen || (nid < 0x0100 && _messageIdGen > 0xFF00)) {
        _messageIdGen = (ushort)nid;      // synchronize messageId
      }
    }

    private void UpdateSuppressedInputs() {
      List<byte> si = new List<byte>();
      si.Add(0);
      int idx, i;
      JSC.JSValue sj;
      foreach(var ti in _topics.Where(z=>z.tag.StartsWith("I") || z.tag.StartsWith("A"))) {
        if(ti.tag.Length > 2 && int.TryParse(ti.tag.Substring(2), out idx) && (sj = ti.topic.GetField("MQTT-SN.suppressed")).ValueType == JSC.JSValueType.Boolean && ((bool)sj)) {
          i = idx / 8;
          while(si.Count <= i) {
            si.Add(0);
          }
          si[i] |= (byte)(1 << (idx % 8));
        }
      }
      if(_suppressedInputs == null || _suppressedInputs.Length != si.Count || !_suppressedInputs.SequenceEqual(si)) {
        _suppressedInputs = si.ToArray();
        Send(new MsPublish(0xFF09, _suppressedInputs)); //".MQTT-SN.SupressInputs"
      }
    }

    private int BCD2int(int c) {
      return (c >> 4) * 10 + (c & 0x0F);
    }
    private byte int2BCD(int c) {
      return (byte)((c / 10) * 16 + (c % 10));
    }

    private void ProccessAcknoledge(MsMessage rMsg) {
      MsMessage msg = null;
      lock(_sendQueue) {
        MsMessage reqMsg;
        if(_sendQueue.Count > 0 && (reqMsg = _sendQueue.Peek()).MsgTyp == rMsg.ReqTyp && reqMsg.MessageId == rMsg.MessageId) {
          _sendQueue.Dequeue();
          _waitAck = false;
          if(_sendQueue.Count > 0 && !(msg = _sendQueue.Peek()).IsRequest) {
            _sendQueue.Dequeue();
          }
        }
      }
      if(msg == null && !_waitAck && state == State.AWake) {
        Tick();
        if(_waitAck) {
          return; // is busy
        }
      }
      if(msg != null || state == State.AWake) {
        SendIntern(msg);
      } else if(!_waitAck) {
        ResetTimer();
      }
    }
    internal void Send(MsMessage msg) {
      if(state != State.Disconnected && state != State.Lost) {
        bool send = true;
        if(msg.MessageId == 0 && msg.IsRequest) {
          msg.MessageId = NextMsgId();
          lock(_sendQueue) {
            if(_sendQueue.Count > 0 || state == State.ASleep) {
              send = false;
            }
            _sendQueue.Enqueue(msg);
          }
        }
        if(send) {
          SendIntern(msg);
        }
      }
    }
    private void SendIntern(MsMessage msg) {
      while(state == State.AWake || (msg != null && (state != State.ASleep || msg.MsgTyp == MsMessageType.DISCONNECT))) {
        if(msg != null) {
          if(_gate != null) {
            //if(_statistic.value) {
            //  Stat(true, msg.MsgTyp, ((msg is MsPublish && (msg as MsPublish).Dup) || (msg is MsSubscribe && (msg as MsSubscribe).dup)));
            //}
            try {
              if(msg.IsRequest) {
                msg.tryCnt--;
              }
              _gate.SendGw(this, msg);
            }
            catch(ArgumentOutOfRangeException ex) {
              Log.Warning("{0} - {1}", this.name, ex.Message);
              if(msg.IsRequest) {
                lock(_sendQueue) {
                  if(_sendQueue.Count > 0 && _sendQueue.Peek() == msg) {
                    _sendQueue.Dequeue();
                    _waitAck = false;
                  }
                }
              }
              msg = null;
            }
          }
          if(msg != null && msg.IsRequest) {
            ResetTimer(_rand.Next(ACK_TIMEOUT, ACK_TIMEOUT * 5 / 3) / (msg.tryCnt + 1));  // 333, 500, 1000
            _waitAck = true;
            break;
          }
          if(_waitAck) {
            break;
          }
        }
        msg = null;
        lock(_sendQueue) {
          if(_sendQueue.Count == 0 && state == State.AWake) {
            if(_gate != null) {
              _gate.SendGw(this, new MsMessage(MsMessageType.PINGRESP));
              //if(_statistic.value) {
              //  Stat(true, MsMessageType.PINGRESP, false);
              //}
            }
            var st = owner.GetField("MQTT-SN.SleepTime");
            ResetTimer(st.IsNumber && (int)st > 0 ? (3100 + (int)st * 1550) : _duration);  // t_wakeup
            ResetTimer(_duration);
            state = State.ASleep;
            break;
          }
          if(_sendQueue.Count > 0 && !(msg = _sendQueue.Peek()).IsRequest) {
            _sendQueue.Dequeue();
          }
        }
      }
    }
    private void ResetTimer(int period = 0) {
      if(period == 0) {
        if(_waitAck) {
          return;
        }
        if(_sendQueue.Count > 0) {
          period = _rand.Next(ACK_TIMEOUT * 3 / 4, ACK_TIMEOUT);  // 450, 600
        } else if(_duration > 0) {
          period = _duration;
        }
      }
      //Log.Debug("$ {0}._activeTimer={1}", Owner.name, period);
      _toActive = DateTime.Now.AddMilliseconds(period);
    }
    internal void Disconnect(ushort duration = 0) {
      if(duration == 0 && !string.IsNullOrEmpty(_willPath)) {
        TopicInfo ti = GetTopicInfo(_willPath, false);
        if(ti != null) {
          SetValue(ti, _wilMsg, _willRetain);
        }
      }
      if(duration > 0) {
        if(state == State.ASleep) {
          state = State.AWake;
        }
        ResetTimer(3100 + duration * 1550);  // t_wakeup
        this.Send(new MsDisconnect());
        state = State.ASleep;
        owner.SetField("MQTT-SN.SleepTime", new JSL.Number(duration), owner);
      } else {
        this._gate = null;
        if(state != State.Lost) {
          state = State.Disconnected;
          if(owner != null) {
            Log.Info("{0} Disconnected", owner.path);
          }
        }
        foreach(var s in _subsscriptions) {
          s.Dispose();
        }
        _subsscriptions.Clear();
        foreach(var ts in _topics) {
          ts.Dispose();
        }
        _topics.Clear();
        lock(_sendQueue) {
          _sendQueue.Clear();
        }
        // Disconnect all devices connected via this
        foreach(var d in _pl._devs.Where(z => z._gate == this && z.state != State.Disconnected && z.state != State.Lost).ToArray()) {
          d.Disconnect(0);
        }
      }
      _waitAck = false;
    }

    internal class TopicInfo : IDisposable {
      public MsDevice owner;
      public Topic topic;
      public ushort TopicId;
      public TopicIdType it;
      public bool registred;
      public string tag;
      public DType dType;
      public JSL.Function convIn;
      public JSL.Function convOut;
      public IMsExt extension;
      public void PublishWithPayload(byte[] payload) {
        if(owner.state == State.Disconnected || owner.state == State.Lost) {
          return;
        }
        owner.Send(new MsPublish(this) { Data = payload });
      }

      public void Dispose() {
        var t = Interlocked.Exchange(ref extension, null);
        if(t != null) {
          owner._pl._plcs.Remove(t as DevicePLC);
          t.Dispose();
        }
      }
    }
    [Flags]
    internal enum DType {
      None = 0,
      Boolean = 1,
      Integer = 2,
      String = 3,
      ByteArray = 4,
      TypeMask = 0xFF,
      RTC = 0x100,
      LOG = 0x200,
      TWI = 0x300,
      PLC = 0x400,
    }
    private static Tuple<string, DType>[] _NTTable = new Tuple<string, DType>[]{ 
      new Tuple<string, DType>("In", DType.Boolean),
      new Tuple<string, DType>("Ip", DType.Boolean),
      new Tuple<string, DType>("Op", DType.Boolean),
      new Tuple<string, DType>("On", DType.Boolean),
      new Tuple<string, DType>("OA", DType.Boolean),   // output high if active
      new Tuple<string, DType>("Oa", DType.Boolean),   // output low if active
      new Tuple<string, DType>("Mz", DType.Boolean),   // Merkers

      new Tuple<string, DType>("Ai", DType.Integer),   //uint16 Analog ref
      new Tuple<string, DType>("AI", DType.Integer),   //uint16 Analog ref2
      new Tuple<string, DType>("Av", DType.Integer),   //uint16
      new Tuple<string, DType>("Ae", DType.Integer),   //uint16
      new Tuple<string, DType>("Pp", DType.Integer),   //uint16 PWM positive
      new Tuple<string, DType>("Pn", DType.Integer),   //uint16 PWM negative
      new Tuple<string, DType>("Mb", DType.Integer),   //int8
      new Tuple<string, DType>("MB", DType.Integer),   //uint8
      new Tuple<string, DType>("Mw", DType.Integer),   //int16
      new Tuple<string, DType>("MW", DType.Integer),   //uint16
      new Tuple<string, DType>("Md", DType.Integer),   //int32
      new Tuple<string, DType>("MD", DType.Integer),   //uint32
      new Tuple<string, DType>("Mq", DType.Integer),   //int64

      new Tuple<string, DType>("Ms", DType.String),

      new Tuple<string, DType>("St", DType.ByteArray),  // Serial port transmit
      new Tuple<string, DType>("Sr", DType.ByteArray),  // Serial port recieve
      new Tuple<string, DType>("Ma", DType.ByteArray),  // Merkers

      new Tuple<string, DType>("pa", DType.ByteArray | DType.PLC),    // Program
      new Tuple<string, DType>("Ta", DType.ByteArray | DType.TWI),

    };
    private static Tuple<ushort, string, DType>[] PredefinedTopics = new Tuple<ushort, string, DType>[]{
      new Tuple<ushort, string, DType>(0xFF01, ".MQTT-SN.SleepTime",      DType.Integer),
      new Tuple<ushort, string, DType>(RTC_EXCH, ".RTC_EXCH",             DType.ByteArray | DType.RTC),  //0xFF07
      new Tuple<ushort, string, DType>(0xFF08, ".MQTT-SN.ADCintegrate",   DType.Integer),
      new Tuple<ushort, string, DType>(0xFF09, ".MQTT-SN.SupressInputs",  DType.ByteArray),

      new Tuple<ushort, string, DType>(0xFF10, ".MQTT-SN.DeviceAddr",     DType.Integer),
      new Tuple<ushort, string, DType>(0xFF11, ".MQTT-SN.GroupID",        DType.Integer),
      new Tuple<ushort, string, DType>(0xFF12, ".MQTT-SN.Channel",        DType.Integer),
      new Tuple<ushort, string, DType>(0xFF14, ".MQTT-SN.GateId",         DType.Integer),
      new Tuple<ushort, string, DType>(0xFF16, ".MQTT-SN.Power",          DType.Integer),
      new Tuple<ushort, string, DType>(0xFF18, ".MQTT-SN.Key",            DType.ByteArray),
      
      new Tuple<ushort, string, DType>(0xFF20, ".MQTT-SN.MACAddr",        DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFF21, ".MQTT-SN.IPAddr",         DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFF22, ".MQTT-SN.IPMask",         DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFF23, ".MQTT-SN.IPRouter",       DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFF24, ".MQTT-SN.IPBroker",       DType.ByteArray),

      new Tuple<ushort, string, DType>(0xFFC0, ".MQTT-SN.tag",            DType.String),
      new Tuple<ushort, string, DType>(0xFFC1, ".MQTT-SN.phy1_addr",      DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFFC2, ".MQTT-SN.phy2_addr",      DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFFC3, ".MQTT-SN.phy3_addr",      DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFFC4, ".MQTT-SN.phy4_addr",      DType.ByteArray),
      new Tuple<ushort, string, DType>(0xFFC8, "_RSSI",                   DType.Integer),

      new Tuple<ushort, string, DType>(LOG_D_ID, ".Log.Debug",            DType.ByteArray | DType.LOG),   // 0xFFE0
      new Tuple<ushort, string, DType>(LOG_I_ID, ".Log.Info",             DType.ByteArray | DType.LOG),   // 0xFFE1
      new Tuple<ushort, string, DType>(LOG_W_ID, ".Log.Warning",          DType.ByteArray | DType.LOG),   // 0xFFE2
      new Tuple<ushort, string, DType>(LOG_E_ID, ".Log.Error",            DType.ByteArray | DType.LOG),   // 0xFFE3
    };
  }
}
