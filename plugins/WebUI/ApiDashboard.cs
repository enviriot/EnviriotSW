///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Net;
using X13.Repository;
using X13.WebUI.Helpers;
using JSC = NiL.JS.Core;

namespace X13.WebUI {
  // The dashboard protocol: tab separated text frames, carried over /api/dashboard.
  //
  //   in   P <path> <json>   publish; a json of "null" or empty removes the topic
  //        S <path>          subscribe; trailing /+ is children, trailing /# is the subtree
  //        A <cid> <path> <action> [<json>]  invoke an action the topic declares
  //        C <user> <pass>   log on - refused, authentication is not implemented
  //   out  I <sid> <auth> <token>   handshake; token is what /api/archivist checks
  //        P <path> <json>   a value the client is subscribed to
  //        A <cid> <json>    what that action answered: {"ok":true,"data":..} or {"ok":false,"error":..}
  //        C false           logon refused
  //
  // Split from the transport the same way ViewSession is: the WebSocketBehavior in WebUiHost
  // owns the socket, this owns the protocol, and a test can drive Handle synchronously by
  // passing a post that runs its work immediately.
  //
  // Engine thread only, reached through WebUiHost.Post - H04 touched Topic straight from the
  // socket thread and from the subscription callback.
  internal sealed class DashboardSession : IDisposable {
    private readonly Action<string> _send;
    private readonly Action<string, Action> _post;
    private readonly Action _close;
    private readonly Func<bool> _verbose;
    private readonly ClientSession _client;
    private readonly string _apiToken;
    private readonly List<SubRec> _subscriptions = new List<SubRec>();
    private bool _disposed;

    /// <param name="post">Queues work for the engine thread (WebUiHost.Post, drained by
    /// WebUiPl.Tick). Omitted means "run it here, now" - the mode tests use.</param>
    public DashboardSession(Action<string> send, IPAddress client, string apiToken,
                            Action<string, Action> post = null, Action close = null, Func<bool> verbose = null) {
      _send = send;
      _post = post ?? ((what, work) => work());
      _close = close ?? (() => { });
      _verbose = verbose ?? (() => false);
      _apiToken = apiToken;
      _client = new ClientSession(client, _post);
    }

    internal ClientSession Client { get { return _client; } }

    /// <summary>Sends the handshake. The client waits for it before subscribing to anything.</summary>
    public void Start() {
      _post(Label("hello"), () => {
        if(_disposed) return;
        // "null" rather than "false" for the auth field: the client reads it as "no logon was
        // attempted" and proceeds anonymously. See wsBond.f.onMessage.
        _send(string.Concat("I\t", _client.id, "\tnull\t", _apiToken ?? string.Empty));
      });
    }

    /// <summary>Queues one incoming frame for the engine thread.</summary>
    public void Handle(string text) {
      _post(Label("frame " + Excerpt(text)), () => HandleCore(text));
    }

    private void HandleCore(string text) {
      if(_disposed || string.IsNullOrEmpty(text)) return;
      string[] sa = text.Split('\t');
      if(sa.Length == 0) return;
      if(_verbose()) Log.Debug("dashboard.msg({0})", string.Join(", ", sa));

      switch(sa[0]) {
      case "C":
        if(sa.Length == 3) HandleLogon(sa[1]);
        break;
      case "P":
        if(sa.Length == 3) HandlePublish(sa[1], sa[2]);
        break;
      case "S":
        if(sa.Length == 2) HandleSubscribe(sa[1]);
        break;
      case "A":
        if(sa.Length >= 4) HandleAction(sa[1], sa[2], sa[3], sa.Length > 4 ? sa[4] : null);
        break;
      }
    }

    /// <remarks>H04 carried a commented-out MqBroker.CheckAuth here and answered every logon
    /// with "false" regardless - the branch has never authenticated anyone. Kept as an explicit
    /// refusal rather than as dead code pretending otherwise.</remarks>
    private void HandleLogon(string user) {
      Log.Warning("dashboard logon as {0} from {1} refused - authentication is not implemented", user, _client.ip);
      _send("C\tfalse");
      _close();
    }

    private void HandlePublish(string path, string json) {
      if(string.IsNullOrEmpty(path) || !DashboardAcl.CanWrite(_client.ip, path)) {
        Log.Warning("dashboard {0}.publish({1}) - forbidden", _client.ip, path);
        return;
      }
      Topic owner = _client.EnsureOwner();
      if(string.IsNullOrEmpty(json) || json == "null") {
        // Looked up without creating: H04 created the topic and removed it again, so publishing
        // a removal to a path that did not exist churned the repository to no end.
        Topic existing = Topic.root.Get(path, false);
        if(existing != null) existing.Remove(owner);
      } else {
        Topic.root.Get(path, true, owner).SetState(JsLib.ParseJson(json), owner);
      }
    }


    /// <summary>Invokes an action the topic declares, and answers with what it reported.</summary>
    /// <remarks>Two gates, and neither is new: the topic's manifest has to list the action -
    /// TopicRpcDispatcher.ExecuteAction decides that, the same way it does for the IDE - and
    /// CanWrite has to allow the topic. CanWrite rather than CanRead because invoking an action is
    /// asking for a change, so it belongs behind the same declaration that lets this client publish
    /// there at all. A dashboard page therefore reaches exactly what its own topics offer it.
    /// <para>Unlike P and S, a refusal is answered instead of merely logged: the caller is holding
    /// a promise, and silence would leave it pending for as long as the page is open. The ACL is
    /// checked before the topic is looked up, so a client without rights cannot learn from the
    /// answer whether the path exists.</para></remarks>
    private void HandleAction(string cid, string path, string action, string argsJson) {
      // No correlation id, nowhere to send the answer - and an action whose outcome cannot be
      // reported is exactly what this whole frame exists to avoid.
      if(string.IsNullOrEmpty(cid)) {
        Log.Warning("dashboard {0}.action({1}) - no correlation id", _client.ip, path);
        return;
      }
      if(string.IsNullOrEmpty(path) || !DashboardAcl.CanWrite(_client.ip, path)) {
        Log.Warning("dashboard {0}.action({1}, {2}) - forbidden", _client.ip, path, action);
        SendActionResult(cid, ViewOpResult.Error("action_forbidden", "Action is not allowed here"));
        return;
      }
      Topic topic = Topic.root.Get(path, false);
      if(topic == null) {
        SendActionResult(cid, ViewOpResult.Error("topic_not_found", "Topic not found: " + path));
        return;
      }
      JSC.JSValue args = JSC.JSValue.Undefined;
      if(!string.IsNullOrEmpty(argsJson)) {
        try {
          args = JsLib.ParseJson(argsJson);
        }
        catch(Exception ex) {
          SendActionResult(cid, ViewOpResult.Error("bad_json", ex.Message));
          return;
        }
      }
      _client.EnsureOwner();

      ViewOpResult result = TopicRpcDispatcher.ExecuteAction(topic, action, args);
      if(result != null && result.Continuation != null) {
        // Same shape as ViewSession.HandleRpc: the plugin may answer from its own thread, and
        // everything this session sends has to leave from the engine thread.
        result.Continuation(final => _post(Label("action " + action), () => SendActionResult(cid, final)));
        return;
      }
      SendActionResult(cid, result);
    }

    private void SendActionResult(string cid, ViewOpResult result) {
      if(_disposed) return;
      bool ok = result != null && result.Ok;
      JSC.JSObject dto = JSC.JSObject.CreateObject();
      dto["ok"] = ok;
      if(ok) {
        if(result.Data != null && result.Data.Defined) dto["data"] = result.Data;
      } else {
        dto["error"] = result == null ? "action_failed" : (result.ErrorCode ?? "action_failed");
        dto["message"] = result == null ? "Action failed" : (result.ErrorMessage ?? "Action failed");
      }
      string json = JsLib.Stringify(dto);
      _send(string.Concat("A\t", cid, "\t", json));
      if(_verbose()) Log.Debug("dashboard.snd(A {0}, {1})", cid, json);
    }

    private void HandleSubscribe(string path) {
      if(string.IsNullOrEmpty(path)) return;
      SubRec.SubMask mask = SubRec.SubMask.Value;
      string basePath = path;
      int idx = path.IndexOfAny(new[] { '+', '#' });
      if(idx < 0) {
        mask |= SubRec.SubMask.Once;
      } else if(idx == path.Length - 1 && idx > 0 && path[idx - 1] == Topic.Bill.delmiter) {
        mask |= path[idx] == '#' ? SubRec.SubMask.All : SubRec.SubMask.Children;
        basePath = path.Substring(0, path.Length - 2);
        if(basePath.Length == 0) basePath = Topic.Bill.delmiterStr;
      } else {
        // A wildcard anywhere but the last segment is not something the protocol expresses.
        Log.Warning("dashboard {0}.subscribe({1}) - malformed path", _client.ip, path);
        return;
      }
      if(!DashboardAcl.CanRead(_client.ip, basePath)) {
        Log.Warning("dashboard {0}.subscribe({1}) - forbidden", _client.ip, path);
        return;
      }
      Topic topic;
      if(!Topic.root.Exist(basePath, out topic)) {
        Log.Warning("dashboard {0}.subscribe({1}) - path does not exist", _client.ip, path);
        return;
      }
      _client.EnsureOwner();
      _subscriptions.Add(topic.Subscribe(mask, SubChanged));
    }

    private void SubChanged(Perform p, SubRec sr) {
      _post(Label("callback " + (p == null || p.src == null ? "?" : p.src.path)), () => SubChangedCore(p));
    }

    private void SubChangedCore(Perform p) {
      if(_disposed || p == null || p.src == null) return;
      if(p.Art == Perform.E_Art.subAck) return;
      // owner is checked for null explicitly: a write from a plugin that passes no prim also
      // has Prim == null, and before this session has a topic that would suppress every such
      // value as if the session had produced it.
      Topic owner = _client.owner;
      if(owner != null && p.Prim == owner) return;
      // A /# subscription spans a subtree that may hold declarations narrower than the one the
      // subscribe was granted under, so the grant is re-checked per value rather than only at
      // subscribe time.
      if(!DashboardAcl.CanRead(_client.ip, p.src.path)) return;

      // On remove the topic still answers GetState() with its last value; sending that would
      // tell the client the topic is alive and holding it. "null" is what the protocol spells
      // a removal with, and what the client already parses as one.
      string json = p.Art == Perform.E_Art.remove ? "null" : JsLib.Stringify(p.src.GetState());
      _send(string.Concat("P\t", p.src.path, "\t", json));
      if(_verbose()) Log.Debug("dashboard.snd({0}, {1})", p.src.path, json);
    }

    private string Label(string what) {
      return "dashboard " + _client.id + " " + what;
    }

    private static string Excerpt(string text) {
      if(string.IsNullOrEmpty(text)) return "<empty>";
      return text.Length <= 80 ? text : text.Substring(0, 80);
    }

    public void Dispose() {
      if(_disposed) return;
      _disposed = true;
      foreach(SubRec sub in _subscriptions) sub.Dispose();
      _subscriptions.Clear();
      _client.Dispose();
    }
  }
}
