///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.EsBroker {
  internal class EsConnection : EsSocket {
    private static readonly JSC.JSValue EmptyManifest;

    static EsConnection() {
      EmptyManifest = JSC.JSObject.CreateObject();
      EmptyManifest["attr"] = 0;
    }


    private EsBrokerPl _basePl;
    private Topic _owner;
    private List<Tuple<SubRec, EsMessage>> _subscriptions;
    private Action<Perform, SubRec> _subCB;

    public EsConnection(EsBrokerPl pl, TcpClient tcp)
      : base(tcp, null) { //
      base._callback = new Action<EsMessage>(RcvMsg);
      this._basePl = pl;
      this._subCB = new Action<Perform, SubRec>(TopicChanged);
      this._subscriptions = new List<Tuple<SubRec, EsMessage>>();
      // Hello
      this.SendArr(new JSL.Array { 1, Environment.MachineName });
      _owner = Topic.root.Get("/$YS/ES").Get(base.ToString());
      _owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly);
      _owner.SetField("type", "Ext/Connection", _owner);
      System.Net.Dns.BeginGetHostEntry(EndPoint.Address, EndDnsReq, null);
    }
    private void EndDnsReq(IAsyncResult ar) {
      try {
        var h = Dns.EndGetHostEntry(ar);
        var v = JSC.JSObject.CreateObject();
        v["Address"] = EndPoint.Address.ToString();
        v["Port"] = EndPoint.Port;
        v["Dns"] = h.HostName;
        _owner.SetState(v, _owner);
        Log.Info("{0} connected from {1}[{2}]:{3}", _owner.path, h.HostName, EndPoint.Address.ToString(), EndPoint.Port);
      }
      catch(SocketException) {
        var v = JSC.JSObject.CreateObject();
        v["Address"] = EndPoint.Address.ToString();
        v["Port"] = EndPoint.Port;
        _owner.SetState(v, _owner);
        Log.Info("{0} connected from {1}:{2}", _owner.path, EndPoint.Address.ToString(), EndPoint.Port);
      }
      Log.Write += Log_Write;
    }

    private void Log_Write(LogLevel ll, DateTime dt, string msg, bool local) {
      base.SendArr(new JSL.Array { 90, JSC.JSValue.Marshal(dt.ToUniversalTime()), (int)ll, msg }, false);
    }
    private void RcvMsg(EsMessage msg) {
      if(msg.Count == 0) {
        return;
      }
      try {
        if(msg[0].IsNumber) {
          switch((int)msg[0]) {
          case 4:
            this.Subscribe(msg);
            break;
          case 6:
            this.SetState(msg);
            break;
          case 8:
            this.Create(msg);
            break;
          case 10:
            this.Move(msg);
            break;
          case 12:
            this.Remove(msg);
            break;
          case 14:
            this.SetField(msg);
            break;
          case 16:
            this.Import(msg);
            break;
          case 91:
            this.History(msg);
            break;
          case 99: {
              var o = Interlocked.Exchange(ref _owner, null);
              if(o != null) {
                Log.Write -= Log_Write;
                Log.Info("{0} connection dropped", o.path);
                o.Remove(o);
                lock(_subscriptions) {
                  foreach(var sr in _subscriptions) {
                    sr.Item1.Dispose();
                  }
                }
              }
            }
            break;    // Disconnect
          }
        } else {
          _basePl.AddRMsg(msg);
        }
      }
      catch(Exception ex) {
        if(_basePl.verbose) {
          Log.Warning("{0} - {1}", msg, ex);
        }
      }
    }
    public override bool verbose {
      get {
        return _basePl.verbose;
      }
      set {
      }
    }

    /// <summary>Subscribe topics</summary>
    /// <param name="args">
    /// REQUEST:  [4, msgId, path, mask], mask: 1 - data, 2 - children
    /// RESPONSE: [3, msgId, success, exist]
    /// </param>
    private void Subscribe(EsMessage msg) {
      if(msg.Count != 4 || !msg[1].IsNumber || msg[2].ValueType != JSC.JSValueType.String || !msg[3].IsNumber) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      Topic parent = Topic.root.Get(msg[2].Value as string, false, _owner);
      if(parent != null) {
        var sr1 = parent.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.Field | SubRec.SubMask.Once | SubRec.SubMask.Chldren, string.Empty, _subCB);
        lock(_subscriptions) {
          _subscriptions.Add(new Tuple<SubRec, EsMessage>(sr1, msg));
        }
      } else {
        msg.Response(3, msg[1], true, false);
      }
    }
    /// <summary>set topics state</summary>
    /// <param name="args">
    /// REQUEST: [6, msgId, path, value]
    /// RESPONSE: [3, msgId, success, [errorMsg] ]
    /// </param> 
    private void SetState(EsMessage msg) {
      if(msg.Count != 4 || !msg[1].IsNumber || msg[2].ValueType != JSC.JSValueType.String) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      string path = msg[2].Value as string;

      Topic t = Topic.root.Get(path, false, _owner);
      if(t != null) {
        t.SetState(msg[3], _owner);
        msg.Response(3, msg[1], true);
      } else {
        msg.Response(3, msg[1], false, "TopicNotExist");
      }
    }
    /// <summary>set topics field</summary>
    /// <param name="args">
    /// REQUEST: [14, msgId, path, fieldName, value]
    /// RESPONSE: [3, msgId, success, [errorMsg] ]
    /// </param> 
    private void SetField(EsMessage msg) {
      if(msg.Count != 5 || !msg[1].IsNumber || msg[2].ValueType != JSC.JSValueType.String || msg[3].ValueType != JSC.JSValueType.String) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      Topic t = Topic.root.Get(msg[2].Value as string, false, _owner);
      if(t != null) {
        if(t.TrySetField(msg[3].Value as string, msg[4], _owner)) {
          msg.Response(3, msg[1], true);
        } else {
          msg.Response(3, msg[1], false, "FieldAccessError");
        }
      } else {
        msg.Response(3, msg[1], false, "TopicNotExist");
      }
    }
    /// <summary>Create topic</summary>
    /// <param name="args">
    /// REQUEST: [8, msgId, path, state, manifest]
    /// </param>
    private void Create(EsMessage msg) {
      if(msg.Count < 4 || !msg[1].IsNumber || msg[2].ValueType != JSC.JSValueType.String) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      Create(Topic.root, msg[2].Value as string, msg[3], (msg[4].ValueType == JSC.JSValueType.Object && msg[4].Value != null) ? msg[4] : null);
    }
    private void Create(Topic parent, string path, JSC.JSValue state, JSC.JSValue manifest) {
      var t = Topic.I.Get(parent, path, true, _owner, false, false);
      Topic.I.Fill(t, state, manifest, _owner);
      JSC.JSValue typeS, typeV, ChL;
      Topic typeT;

      if(manifest != null && (typeS = manifest["type"]).ValueType == JSC.JSValueType.String && !string.IsNullOrWhiteSpace(typeS.Value as string)
        && Topic.root.Get("/$YS/TYPES").Exist(typeS.Value as string, out typeT) && (typeV = typeT.GetState()) != null && typeV.ValueType == JSC.JSValueType.Object && typeV.Value != null
        && (ChL = typeV["Children"]).ValueType == JSC.JSValueType.Object && ChL.Value != null) {
          foreach(var ch in ChL) {
            if((JsLib.OfInt(JsLib.GetField(ch.Value, "manifest.attr"), 0) & (int)Topic.Attribute.Required) != 0) {
              Create(t, ch.Key, ch.Value["default"], ch.Value["manifest"]);
            }
          }
      }
    }

    /// <summary>Move topic</summary>
    /// <param name="args">
    /// REQUEST: [10, msgId, path source, path destinations parent, new name(optional rename)]
    /// </param>
    private void Move(EsMessage msg) {
      if(msg.Count < 5 || !msg[1].IsNumber || msg[2].ValueType != JSC.JSValueType.String || msg[3].ValueType != JSC.JSValueType.String
        || (msg.Count > 5 && msg[4].ValueType != JSC.JSValueType.String)) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      Topic t = Topic.root.Get(msg[2].Value as string, false, _owner);
      Topic p = Topic.root.Get(msg[3].Value as string, false, _owner);
      if(t != null && p != null) {
        string nname = msg.Count < 5 ? t.name : (msg[4].Value as string);
        t.Move(p, nname, _owner);
      }
    }
    /// <summary>Remove topic</summary>
    /// <param name="args">
    /// REQUEST: [12, msgId, path]
    /// </param>
    private void Remove(EsMessage msg) {
      if(msg.Count != 3 || !msg[1].IsNumber || msg[2].ValueType != JSC.JSValueType.String) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      Topic t = Topic.root.Get(msg[2].Value as string, false, _owner);
      if(t != null) {
        t.Remove(_owner);
      }
    }
    /// <summary>Import</summary>
    /// <param name="msg">
    /// Command: [16, FileName, payload(Base64)]
    /// </param>
    private void Import(EsMessage msg) {
      if(msg.Count != 3 || msg[1].ValueType != JSC.JSValueType.String || msg[2].ValueType != JSC.JSValueType.String) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      try {
        var str = Encoding.UTF8.GetString(Convert.FromBase64String(msg[2].Value as string));
        using(var sr = new StringReader(str)) {
          Repo.Import(sr, null);
        }
      }
      catch(Exception ex) {
        Log.Warning("Import({0}) - {1}", msg[1].Value as string, ex.Message);
      }
    }
    /// <summary>Read old log entrys</summary>
    /// <param name="msg">Req: [91, Date. count]</param>
    private void History(EsMessage msg) {
      if(msg.Count != 3 || msg[1].ValueType != JSC.JSValueType.Date || !msg[2].IsNumber) {
        if(_basePl.verbose) {
          Log.Warning("Syntax error: {0}", msg);
        }
        return;
      }
      try {
        if(Log.History != null) {
          var resp = Log.History((msg[1].Value as JSL.Date).ToDateTime(), (int)msg[2]);
          foreach(var e in resp) {
            base.SendArr(new JSL.Array { 90, JSC.JSValue.Marshal(e.dt), (int)e.ll, e.format }, false);
          }
        }
      }
      catch(Exception ex) {
        Log.Warning("History({0}) - {1}", msg[1].Value as string, ex.Message);
      }
    }

    private void TopicChanged(Perform p, SubRec sb) {
      switch(p.art) {
      case Perform.Art.create:
        if(sb.setTopic != p.src) {
          base.SendArr(new JSL.Array { 4, p.src.path });
        }
        break;
      case Perform.Art.subscribe:
        if(sb.setTopic == p.src) {
          var m = p.src.GetField(null);
          if(m == null || m.ValueType!=JSC.JSValueType.Object || m.Value == null) {
            m = EmptyManifest;
          }
          base.SendArr(new JSL.Array { 4, p.src.path, p.src.GetState(), m });
        } else {
          base.SendArr(new JSL.Array { 4, p.src.path });
        }
        break;
      case Perform.Art.subAck:
        var sr = p.o as SubRec;
        if(sr != null) {
          lock(_subscriptions) {
            foreach(var msg in _subscriptions.Where(z => z.Item1 == sr && z.Item2 != null).Select(z => z.Item2)) {
              msg.Response(3, msg[1], true, true);
            }
          }
        }
        break;
      case Perform.Art.changedState:
        if(p.prim != _owner && sb.setTopic == p.src) {
          base.SendArr(new JSL.Array { 6, p.src.path, p.src.GetState() });
        }
        break;
      case Perform.Art.changedField:
        if(sb.setTopic == p.src) {
          base.SendArr(new JSL.Array { 14, p.src.path, p.src.GetField(null) });
        }
        break;
      case Perform.Art.move:
        if(sb.setTopic != p.src) {
          base.SendArr(new JSL.Array { 10, p.o as string, p.src.parent.path, p.src.name });
        }
        break;
      case Perform.Art.remove:
        if(sb.setTopic == p.src.parent) {
          base.SendArr(new JSL.Array { 12, p.src.path });
          lock(_subscriptions) {
            _subscriptions.RemoveAll(z => z.Item1.setTopic == p.src);
          }
        }
        break;

      }
    }
  }
}
