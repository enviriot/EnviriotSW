///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.BaseLibrary;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using X13.Repository;

namespace X13.WebUI {
  internal class ApiV04 : WebSocketBehavior {
    private static Timer _pingTimer;
    private static WebSocketSessionManager _wsMan;

    static ApiV04() {
      _pingTimer = new Timer(PingF, null, 270000, 300000);
    }

    private static void PingF(object o) {
      if (_wsMan != null) {
        _wsMan.Broadping();
      }
    }

    private List<X13.Repository.SubRec> _subscriptions;
    private Session _ses;

    protected override void OnOpen() {
      if (_wsMan == null) {
        _wsMan = Sessions;
      }
      string sid = null;
      if (Context.CookieCollection["sessionId"] != null) {
        sid = Context.CookieCollection["sessionId"].Value;
      }
      System.Net.IPEndPoint remoteEndPoint = Context.UserEndPoint;
      {
        System.Net.IPAddress remIP;
        if (Context.Headers.Contains("X-Real-IP") && System.Net.IPAddress.TryParse(Context.Headers["X-Real-IP"], out remIP)) {
          remoteEndPoint = new System.Net.IPEndPoint(remIP, remoteEndPoint.Port);
        }
      }
      _ses = Session.Get(sid, remoteEndPoint);
      _subscriptions = new List<Repository.SubRec>();
      Send(string.Concat("I\t", _ses.id, "\t", (string.IsNullOrEmpty(_ses.userName) ? (/*_disAnonym.value?"false":*/"null") : "true")));
      if (WebUI_Pl.verbose) {
        X13.Log.Debug("{0} connect webSocket", _ses.owner.name);
      }
    }
    protected override void OnMessage(MessageEventArgs e) {
      string[] sa;
      if (e.IsText && !string.IsNullOrEmpty(e.Data) && (sa = e.Data.Split('\t')) != null && sa.Length > 0) {
        if (WebUI_Pl.verbose) {
          X13.Log.Debug("ws.msg({0})", string.Join(", ", sa));
        }
        if (sa[0] == "C" && sa.Length == 3) {  // Connect, username, password
          /*if((sa[1]!="local" || _ses.ip.IsLocal()) && MQTT.MqBroker.CheckAuth(sa[1], sa[2])) {
            _ses.userName=sa[1];
            Send("C\ttrue");
            if(WebUI_Pl.verbose) {
              X13.Log.Info("{0} logon as {1} success", _ses.owner.name, _ses.ToString());
            }
          } else */
          {
            Send("C\tfalse");
            if (WebUI_Pl.verbose) {
              X13.Log.Warning("{0}@{2} logon  as {1} failed", _ses.owner.name, sa[1], _ses.owner.GetState());
            }
            Sessions.CloseSession(base.ID);
          }
        } else if (/*!_disAnonym.value || */(_ses != null /*&& !string.IsNullOrEmpty(_ses.userName)*/)) {
          if (sa[0] == "P" && sa.Length == 3) {
            if (sa[1] != null && (sa[1].StartsWith("/export/") || CheckAccess(sa[1]))) {
              WebUI_Pl.ProcessPublish(sa[1], sa[2], _ses);
            } else {
              X13.Log.Warning("{0}.publish({1}) - access forbinden", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
            }
          } else if (sa[0] == "S" && sa.Length == 2) {
            if (sa[1] != null && (sa[1].StartsWith("/export/") || CheckAccess(sa[1]))) {
              string p = sa[1];
              SubRec.SubMask mask = Repository.SubRec.SubMask.Value;
              Topic t;
              int idx = p.IndexOfAny(new[] { '+', '#' });
              if (idx < 0) {
                mask |= SubRec.SubMask.Once;
              } else if (idx == p.Length - 1 && p[idx - 1] == '/') {
                mask |= p[idx] == '#' ? SubRec.SubMask.All : SubRec.SubMask.Chldren;
                p = p.Substring(0, p.Length - 2);
              } else {
                X13.Log.Warning("{0}.subscribe({1}) - access forbinden", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
                return;
              }
              if (Topic.root.Exist(p, out t)) {
                _subscriptions.Add(t.Subscribe(mask, SubChanged));
              } else {
                X13.Log.Warning("{0}.subscribe({1}) - path not exist", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
              }
            } else {
              X13.Log.Warning("{0}.subscribe({1}) - bad path", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
            }
          }
        }
      }
    }

    private bool CheckAccess(string sa) {
      if (sa[0] != Topic.Bill.delmiter) {
        return false;
      }
      var idx = sa.IndexOf(Topic.Bill.delmiter, 1);
      if (idx < 1) {
        return false;
      }
      var n1 = sa.Substring(1, idx - 1);
      var t1 = Topic.root.children.FirstOrDefault(z => z.name == n1);
      if (t1 == null) {
        return false;
      }
      var f = t1.GetField("WebUI.Filter");
      string fs;
      IPAddress ip;
      int mask;
      if (f.ValueType != NiL.JS.Core.JSValueType.String
        || string.IsNullOrWhiteSpace(fs = f.Value as string)
        || (idx = fs.IndexOf('/')) < 7
        || !IPAddress.TryParse(fs.Substring(0, idx), out ip)
        || !int.TryParse(fs.Substring(idx + 1), out mask)) {
        t1.SetField("WebUI.Filter", "127.0.0.0/32", _ses.owner);
        return false;
      }
      var a1 = _ses.ip.GetAddressBytes();
      var a2 = ip.GetAddressBytes();
      if (a1.Length != a2.Length) {
        return false;
      }
      for (int i = 0; i < a1.Length; i++) {
        if (mask >= 0) {
          if (mask < 8) {
            var bm = (byte)(0xFF << (8 - mask));
            a1[i] &= bm;
            a2[i] &= bm;
          }
          if (a1[i] != a2[i]) {
            return false;
          }
        }
        mask -= 8;
      }
      return true;
    }

    private void SubChanged(Perform p, SubRec sr) {
      var vj = JsLib.Stringify(p.src.GetState());
      string meta = string.Empty;
      if (p.art == Perform.Art.subscribe) {
        var mo = NiL.JS.Core.JSObject.CreateObject();
        var to = p.src.GetField("type");
        string ts;
        if (to.ValueType == NiL.JS.Core.JSValueType.String && !string.IsNullOrEmpty(ts = to.As<string>()) && ts.StartsWith("WebUI")) {
          mo["type"] = ts.Substring("WebUI/".Length);
        }
        if ((to = p.src.GetField("WebUI")).ValueType == NiL.JS.Core.JSValueType.Object){
          foreach(var kv in to) {
            mo[kv.Key] = kv.Value;
          }
        }
        if (mo.Any()) {
          meta = "\t" + JsLib.Stringify(mo);
        }
      }
      Send(string.Concat("P\t", p.src.path, "\t", vj, meta));
      if (WebUI_Pl.verbose) {
        X13.Log.Debug("ws.snd({0}, {1}, {2})", p.src.path, vj, meta);
      }
    }
    protected override void OnClose(CloseEventArgs e) {
      if (_ses != null) {
        _ses.Close();
        if (WebUI_Pl.verbose) {
          X13.Log.Info("{0} Disconnect: [{1}]{2}", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, e.Code, e.Reason);
        }
        _ses = null;
      }
      foreach (var s in _subscriptions) {
        s.Dispose();
      }
    }
  }
  internal class Session : IDisposable {
    private static List<WeakReference> sessions;

    static Session() {
      sessions = new List<WeakReference>();
    }
    public static Session Get(string sid, System.Net.IPEndPoint ep, bool create = true) {
      Session s;
      if (string.IsNullOrEmpty(sid) || (s = sessions.Where(z => z.IsAlive).Select(z => z.Target as Session).FirstOrDefault(z => z != null && z.id == sid && z.ip.Equals(ep.Address))) == null) {
        if (create) {
          s = new Session(ep);
          sessions.Add(new WeakReference(s));
        } else {
          s = null;
        }
      }
      return s;
    }

    private Session(System.Net.IPEndPoint ep) {
      Topic r = Topic.root.Get("/$YS/WebUI/clients");
      this.id = Guid.NewGuid().ToString();
      this.ip = ep.Address;
      int i = 1;
      string pre = ip.ToString();
      while (r.Exist(pre + i.ToString())) {
        i++;
      }
      _owner = r.Get(pre + i.ToString());
      owner.ClearAttribute(Topic.Attribute.Saved);
      try {
        var he = System.Net.Dns.GetHostEntry(this.ip);
        _host = string.Format("{0}[{1}]", he.HostName, this.ip.ToString());
        var tmp = he.HostName.Split('.');
        if (tmp.Length > 0 && !string.IsNullOrEmpty(tmp[0])) {
          i = 1;
          while (r.Exist(tmp[0] + "-" + i.ToString())) {
            i++;
          }
          _owner.Move(r, tmp[0] + "-" + i.ToString());
        }
      }
      catch (Exception) {
        _host = string.Format("[{0}]", this.ip.ToString());
      }
      this.owner.SetState(_host);
      if (WebUI_Pl.verbose) {
        Log.Info("{0} session[{2}] - {1}", owner.name, this._host, this.id);
      }
    }
    private string _host;
    private Topic _owner;
    public readonly string id;
    public readonly System.Net.IPAddress ip;
#pragma warning disable 649
    public string userName;
#pragma warning restore 649
    public Topic owner { get { return _owner; } }
    public void Close() {
      sessions.RemoveAll(z => !z.IsAlive || z.Target == this);
      Dispose();
    }
    public override string ToString() {
      return (string.IsNullOrEmpty(userName) ? "anonymus" : userName) + "@" + _host;
    }
    public void Dispose() {
      var o = Interlocked.Exchange(ref _owner, null);
      if (o != null && !o.disposed) {
        o.Remove();
      }
    }
  }
}
