///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSL = NiL.JS.BaseLibrary;
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
    /* Config-editor channel for this session — see the "G" handler below.
     * A topic pair per session rather than one shared pair: Repo's EnquePerf
     * collapses several setState performs for the same topic inside one tick,
     * so two browsers configuring at once would silently lose a request. */
    private Topic _cfgReqT, _cfgRspT;
    private SubRec _cfgRspSub;

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
            // /export/* used to be open to any connected client unconditionally.
            // Matches SW/Server's asw_ws_gateway model instead, added 2026-08-08
            // so the same web_loc/ front-end behaves identically against either
            // backend: LAN may always write, a remote client only while
            // /local/asw/access is 2 (Remote). /Public/* and everything else
            // still goes through the existing per-topic CheckAccess() filter
            // unchanged (that already covers /local/* once its "WebUI.Filter"
            // is set — see AntSwPl.Start()).
            bool allowed = sa[1] != null && (
              sa[1].StartsWith("/export/") ? (IsLan() || RemoteWriteAllowed())
              : (sa[1].StartsWith("/Public/") || CheckAccess(sa[1])));
            if (allowed) {
              WebUI_Pl.ProcessPublish(sa[1], sa[2], _ses);
            } else {
              X13.Log.Warning("{0}.publish({1}) - access forbinden", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
            }
          } else if (sa[0] == "S" && sa.Length == 2) {
            if (sa[1] != null && (sa[1].StartsWith("/export/") || sa[1].StartsWith("/Public/") || CheckAccess(sa[1]))) {
              string p = sa[1];
              SubRec.SubMask mask = Repository.SubRec.SubMask.Value;
              Topic t;
              if (p == "/local" || p.StartsWith("/local/")) {
                // Any /local/... subscribe grants the whole /local subtree — a
                // blanket "give me the LAN-only firehose" flag, not a per-path
                // filter. Deliberately mirrors asw_ws_gateway (gateway_main.c's
                // ws_client_thread), so the same web_loc/ front-end works
                // against either backend: it asks for "/local/out/#", but the
                // topic it actually needs is /local/asw/access. Under plain
                // subtree subscription semantics that asks for a sibling tree
                // (and "/local/out" doesn't even exist), so nothing was ever
                // delivered and the UI sat at its "no data yet" state. Also
                // means a future /local/* topic needs no change here.
                p = "/local";
                mask |= SubRec.SubMask.All;
              } else {
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
              }
              if (Topic.root.Exist(p, out t)) {
                _subscriptions.Add(t.Subscribe(mask, SubChanged));
              } else {
                X13.Log.Warning("{0}.subscribe({1}) - path not exist", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
              }
            } else {
              X13.Log.Warning("{0}.subscribe({1}) - bad path", (_ses == null || _ses.owner == null) ? "UNK" : _ses.owner.name, sa[1]);
            }
          } else if (sa[0] == "G" && sa.Length >= 2) {
            // Config editor (web_loc/SetupRemote.html): request/response, not
            // pub/sub. The payload is already one of the CFG* command lines
            // asw_core's IPC listener accepts, so nothing here needs to
            // understand node/code/data — it only checks the command name, so
            // that this cannot become a way to poke arbitrary internal topics,
            // and hands the line to AntSw over a per-session topic pair.
            // LAN-only, unconditionally: same rule as everything under
            // /local/, and SetupRemote.html is a LAN-only page to begin with.
            string line = e.Data.Substring(2);
            string verb = sa[1];
            if (!IsLan()) {
              // Silent, like every other /local-flavoured rejection.
            } else if (verb != "CFGGET" && verb != "CFGSET") {
              Send(string.Concat("R\t", line, "\tERR\tbad cmd"));
            } else {
              CfgChannel().SetState(line, _ses.owner);
            }
          }
        }
      }
    }

    /* Lazily creates this session's request topic (and starts listening on the
     * matching response one). Both live under /$YS/, which is not /export,
     * /Public or /local — so CheckAccess can never expose this channel to a
     * browser however the filters are configured. */
    private Topic CfgChannel() {
      if (_cfgReqT == null) {
        _cfgRspT = Topic.root.Get("/$YS/AntSw/cfg/rsp", true).Get(_ses.id, true);
        _cfgRspT.SetState(string.Empty);
        _cfgRspSub = _cfgRspT.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, CfgAnswered);
        _cfgReqT = Topic.root.Get("/$YS/AntSw/cfg/req", true).Get(_ses.id, true);
      }
      return _cfgReqT;
    }

    private void CfgAnswered(Perform p, SubRec sr) {
      if (p.Art != Perform.E_Art.changedState) {
        return;
      }
      var v = p.src.GetState();
      if (v.ValueType != NiL.JS.Core.JSValueType.String) {
        return;
      }
      var s = v.Value as string;
      if (!string.IsNullOrEmpty(s)) {
        Send(string.Concat("R\t", s));
      }
    }

    // Reuses CheckAccess's own per-top-level-topic "WebUI.Filter" CIDR against
    // "/local" specifically, rather than duplicating the CIDR-matching logic —
    // "is this client LAN" and "is this client inside /local's configured
    // filter" are the same question once AntSwPl.Start() sets /local's filter
    // to the LAN CIDR. The path itself doesn't need to exist as a topic, only
    // "/local" (the first segment) does — see CheckAccess.
    private bool IsLan() {
      return CheckAccess("/local/asw");
    }

    // The one access tri-state, 0=Lock / 1=Local / 2=Remote — same topic, same
    // encoding as SW/Server's asw_state_t.access (see its state.h). Only 2
    // lets a client outside the LAN write.
    private bool RemoteWriteAllowed() {
      Topic t;
      if (!Topic.root.Exist("/local/asw/access", out t)) {
        return false;   // fail-safe closed, e.g. if AntSw never started
      }
      var v = t.GetState();
      return v.IsNumber && (int)v == 2;
    }

    private bool CheckAccess(string sa) {
      // A loopback client is always trusted, regardless of any topic's
      // configured WebUI.Filter CIDR — added 2026-08-09 after a real test:
      // the default lan_cidr (192.168.0.0/16) doesn't include 127.0.0.1, so
      // testing from the same machine the host runs on (a completely normal
      // thing to do) got rejected as "remote" every time. This matches how
      // most people expect localhost to behave, and IsLan() (used for the
      // /export/req write gate) shares this same bypass since it calls
      // through CheckAccess.
      if (_ses != null && _ses.ip != null && IPAddress.IsLoopback(_ses.ip)) {
        return true;
      }
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
      // Self-echo suppression (Prim == this session's own topic) is kept for
      // the normal Enviriot UI, where an input box would otherwise fight with
      // the value it just sent. It is deliberately NOT applied under /local/,
      // to match asw_ws_gateway, which echoes every publish to every
      // subscriber including the writer — web_loc relies on that: its
      // Lock/Local/Remote tab only repaints on an incoming value, never
      // optimistically on click, so without the echo a click appears to do
      // nothing even though the state did change.
      if(p.Art==Perform.E_Art.subAck
        || (p.Prim==_ses.owner && !p.src.path.StartsWith("/local/"))) {
        return;
      }
      var vj = JsLib.Stringify(p.src.GetState());
      Send(string.Concat("P\t", p.src.path, "\t", vj));
      if (WebUI_Pl.verbose) {
        X13.Log.Debug("ws.snd({0}, {1})", p.src.path, vj);
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
      /* Drop this session's config channel — otherwise every browser that ever
       * opened SetupRemote.html leaves a pair of topics behind for the life of the
       * process. Unsubscribe before removing, so the removal itself cannot
       * come back through CfgAnswered on a socket that is already closing. */
      if (_cfgRspSub != null) {
        _cfgRspSub.Dispose();
        _cfgRspSub = null;
      }
      if (_cfgReqT != null) {
        _cfgReqT.Remove();
        _cfgReqT = null;
      }
      if (_cfgRspT != null) {
        _cfgRspT.Remove();
        _cfgRspT = null;
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
