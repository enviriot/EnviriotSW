///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal sealed class ViewSession : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    // Engine thread only - it is the only thread that runs anything of this session's, so no
    // volatile and no lock. Guards work that was queued before Dispose and dequeued after it.
    private bool _disposed;
    private readonly int _sessionId;
    private readonly string _apiToken;
    private readonly Func<Topic> _prim;
    private readonly SortedList<string, Action<JSC.JSValue>> _handlers;
    private readonly ViewTargetRegistry _targets;
    private readonly WorkspaceViewProvider _workspace;
    private readonly CatalogViewProvider _catalog;
    private readonly InspectorChildrenViewProvider _inspectorChildren;
    private readonly InspectorStateViewProvider _inspectorState;
    private readonly InspectorManifestViewProvider _inspectorManifest;
    private readonly LogramViewProvider _logram;
    private readonly ChartViewProvider _chart;
    private readonly LogHandler _log;
    private readonly List<IViewProvider> _providers;

    /// <param name="send">Transport sink. The bool suppresses the transport's verbose
    /// tracing: that tracing goes through X13.Log.Debug, so tracing LogHandler's live
    /// evnt.log push would fire X13.Log.Write again and push another evnt.log, forever.
    /// The log stream cannot log itself.</param>
    /// <param name="apiToken">Handed to the client in resp.hello; the HTTP export/import
    /// endpoints reject anything without it.</param>
    /// <param name="post">Queues work for the engine thread (WebUiHost.Post, drained by
    /// WebUiPl.Tick). Omitted means "run it here, now" - the mode tests use, so a test can drive
    /// Handle synchronously the way it always could. It is an injection point, not a stub: the
    /// queued semantics themselves (ordering, coalescing, command_error on a throwing handler)
    /// are covered by tests that pass a real queue.</param>
    /// <param name="prim">This session's client topic, read per write and passed as the
    /// TopicEvent.Author of everything the session changes. A function rather than a Topic because
    /// the topic is created on the engine thread after the constructor has returned, and it is
    /// renamed later still when the reverse DNS lookup lands. Omitted means writes carry no
    /// prim, which is what they did before and what tests still exercise.</param>
    public ViewSession(Action<JSC.JSObject, bool> send, int sessionId = 0, string apiToken = null, Action<string, Action> post = null, Func<Topic> prim = null) {
      _post = post ?? ((what, work) => work());
      _send = m => send(m, false);
      Action<JSC.JSObject> sendQuiet = m => send(m, true);
      _sessionId = sessionId;
      _apiToken = apiToken;
      _prim = prim;
      _targets = new ViewTargetRegistry();
      _workspace = new WorkspaceViewProvider(_send, _targets, Post, prim);
      _catalog = new CatalogViewProvider(_send, Post, null, prim);
      _inspectorChildren = new InspectorChildrenViewProvider(_send, _targets, Post, prim);
      _inspectorState = new InspectorStateViewProvider(_send, _targets, Post, prim);
      _inspectorManifest = new InspectorManifestViewProvider(_send, _targets, Post, prim);
      _logram = new LogramViewProvider(_send, Post, prim);
      _chart = new ChartViewProvider(_send, Post);
      _log = new LogHandler(sendQuiet, Post);
      _providers = new List<IViewProvider>();
      _providers.Add(_workspace);
      _providers.Add(_catalog);
      _providers.Add(_inspectorChildren);
      _providers.Add(_inspectorState);
      _providers.Add(_inspectorManifest);
      _providers.Add(_logram);
      _providers.Add(_chart);

      _handlers = new SortedList<string, Action<JSC.JSValue>>(StringComparer.Ordinal);
      _handlers[ViewMessageTypes.ReqHello] = HandleHello;
      _handlers[ViewMessageTypes.ReqExpand] = HandleExpand;
      _handlers[ViewMessageTypes.ReqCommit] = HandleCommit;
      _handlers[ViewMessageTypes.ReqMenu] = HandleMenu;
      _handlers[ViewMessageTypes.ReqRpc] = HandleRpc;
      _handlers[ViewMessageTypes.ReqOpen] = HandleOpen;
      _handlers[ViewMessageTypes.ReqClose] = HandleClose;
      _handlers[ViewMessageTypes.ReqLog] = HandleLog;
    }

    /// <summary>Queues one incoming frame for the engine thread.</summary>
    /// <remarks>Returns before the frame is processed, so resp.* leaves later, from the pump.
    /// The client is built for that: ws-client.js correlates on `id` and has no timeout at all.
    /// Parsing is inside the queued work on purpose - parsing on the socket thread would let a
    /// bad_json answer overtake the responses to frames accepted before it.</remarks>
    public void Handle(string text) {
      _post(Label("frame " + Excerpt(text)), () => HandleCore(text));
    }

    /// <summary>Queues work for the engine thread, tagging it with this session. Handed to
    /// providers so their repository callbacks run on the pump rather than on the repo thread.</summary>
    internal void Post(string what, Action work) {
      _post(Label(what), work);
    }

    /// <summary>Tags queued work with the session it belongs to, so a pump failure names it.</summary>
    private string Label(string what) {
      return "WS#" + _sessionId + " " + what;
    }

    // Enough of the frame to identify the command without turning an error line into a dump. The
    // verbose trace already logs frames whole (WebUiHost "WS#{0} <= {1}"), so this adds no new
    // kind of content to the log - only a bounded amount of it, on a path that is already an error.
    private static string Excerpt(string text) {
      const int Max = 160;
      if(string.IsNullOrEmpty(text)) return "<empty>";
      return text.Length <= Max ? text : text.Substring(0, Max) + "...";
    }

    private void HandleCore(string text) {
      if(_disposed) return;
      JSC.JSValue request;
      try {
        request = JsLib.ParseJson(text);
      }
      catch(Exception ex) {
        // Logged unconditionally: verbose switches WS traffic tracing, it must not silence
        // diagnostics - an error that reached only the client leaves nothing to debug from.
        // No stack for the client-fault cases, so a broken client in a reconnect loop cannot
        // bury the log in traces.
        X13.Log.Warning("WebUI WS#{0} bad JSON - {1}", _sessionId, ex.Message);
        _send(Error(null, "bad_json", ex.Message));
        return;
      }

      // JsLib.GetField, not request["type"]: "null" is valid JSON, so ParseJson accepts it and
      // hands back JSValue.Null - and indexing that throws a NiL TypeError. This read sits
      // outside the try/catch below, so the exception left Handle entirely, on the
      // WebSocketSharp callback thread. Every read of a client-supplied value in this file goes
      // through JsLib for the same reason: it checks the container at each hop instead of
      // assuming one. See ViewSessionTests.
      string type = request.AsString("type", null);
      Action<JSC.JSValue> handler;
      if(!string.IsNullOrEmpty(type) && _handlers.TryGetValue(type, out handler)) {
        try {
          handler(request);
        }
        catch(Exception ex) {
          X13.Log.Warning("WebUI WS#{0} command {1} error - {2}", _sessionId, type, ex.ToString());
          _send(Error(request, "command_error", ex.Message));
        }
      } else {
        X13.Log.Warning("WebUI WS#{0} unsupported command - {1}", _sessionId, type ?? "<null>");
        _send(Error(request, "unsupported_command", "Unsupported command: " + (type ?? "<null>")));
      }
    }

    public void HandleHello(JSC.JSValue request) {
      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespHello, true);
      response["name"] = Environment.MachineName;
      if(!string.IsNullOrEmpty(_apiToken)) response["token"] = _apiToken;
      _send(response);
      _workspace.SendRoot();
    }

    /// <summary>Resolves the provider, runs <paramref name="op"/>, reports any failure.</summary>
    /// <returns>The successful result, or null when an error was already sent and the caller
    /// must stop. Keeps the "provider missing / result null / result not Ok" unwrapping - and
    /// its fallback code and message - in one place instead of once per handler.</returns>
    private ViewOpResult Run(JSC.JSValue request, string vid, string failCode, string failText, Func<IViewProvider, ViewOpResult> op) {
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : op(provider);
      if(result != null && result.Ok) return result;
      _send(Error(request,
        result == null ? failCode : (result.ErrorCode ?? failCode),
        result == null ? failText : (result.ErrorMessage ?? failText)));
      return null;
    }

    public void HandleExpand(JSC.JSValue request) {
      string vid = request.AsString("vid", null);
      if(Run(request, vid, "view_expand_failed", "View expand failed", p => p.Expand(vid, request.Field("expand").AsBool(false))) == null) return;
      _send(ResponseBase(request, ViewMessageTypes.RespExpand, true));
    }

    public void HandleCommit(JSC.JSValue request) {
      string vid = request.AsString("vid", null);
      if(Run(request, vid, "view_commit_failed", "View commit failed", p => p.Commit(vid, request.Field("value"))) == null) return;
      _send(ResponseBase(request, ViewMessageTypes.RespCommit, true));
    }

    public void HandleMenu(JSC.JSValue request) {
      string vid = request.AsString("vid", null);
      List<MenuItemDto> items = null;
      if(Run(request, vid, "view_menu_failed", "View menu failed", p => p.BuildMenu(vid, out items)) == null) return;

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespMenu, true);
      response["vid"] = vid;
      response["items"] = ViewProtocolSerializer.SerializeMenuItems(items);
      _send(response);
    }


    public void HandleOpen(JSC.JSValue request) {
      string vid = request.AsString("vid", null);
      string view = request.AsString("view", null);
      if(view == null) {
        // Auto-open: caller doesn't know the topic's type yet, so vid here is a
        // bare topic path (no view#-prefix, ResolveProvider/CanHandle don't apply).
        // We resolve Core/Logram ourselves and open exactly one side - Logram, or
        // all three Inspector panes - never both, so nothing is ever opened only to
        // be immediately closed (see InspectorChildrenViewProvider's isLogram hint,
        // which this replaces for navigation paths that had no prior hint).
        HandleOpenAuto(request, vid);
        return;
      }
      ViewOpResult result = Run(request, vid, "view_open_failed", "View open failed", p => p.Open(vid, view));
      if(result == null) return;

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespOpen, true);
      response["view"] = result.View ?? view ?? string.Empty;
      response["vid"] = result.Vid ?? vid ?? string.Empty;
      response["title"] = result.Title ?? string.Empty;
      if(result.Data != null && result.Data.Defined) response["data"] = result.Data;
      _send(response);
    }

    /// <summary>Auto-open: resolves which document the topic is, answers, then opens it.</summary>
    /// <remarks>Resolving Core/Logram here (one field read - see LogramViewProvider.Open) rather
    /// than asking the frontend to guess a view#-prefixed vid up front means exactly one side is
    /// ever opened: the losing side is never touched, so a large Logram's children are never
    /// fetched just to be discarded, and a plain topic's Logram graph never gets built for nothing.
    /// <para>The answer goes out BEFORE that side is opened, which is the opposite of the
    /// explicit-view path above and is the whole point. Rows sent ahead of the answer would reach
    /// a client that does not yet know which document it is looking at, forcing it to guess one to
    /// hold them - and to visibly correct itself when the answer disagreed. Answering first, the
    /// client builds the right document and every row lands in it: resp.open resolves its promise
    /// in a microtask of the frame that delivered it, so the continuation runs before the socket
    /// can deliver the first row.</para>
    /// <para>The cost is that a failure to open can no longer be reported as this request's
    /// answer. It is logged instead. Everything that can be checked cheaply - the path, the topic,
    /// its type - is checked before the answer goes out, so what remains is a view failing to open
    /// a topic that exists and is of the right type.</para>
    /// <para>For the same reason the answer here carries no "data": it is assembled before the
    /// view that would supply it has run, so the three fields below are everything it can say.
    /// Nothing reachable this way returns any today - LogramViewProvider.Open answers with
    /// view/vid/title alone, and Catalog is never opened from here - but a view that starts to
    /// would lose it silently. The answer then belongs in its own packet: moving this one back
    /// after the open would put the rows ahead of it again, which is the very thing the ordering
    /// above exists to prevent.</para></remarks>
    private void HandleOpenAuto(JSC.JSValue request, string path) {
      if(string.IsNullOrEmpty(path)) {
        _send(Error(request, "view_target_not_found", "View target not found: <null>"));
        return;
      }
      Topic topic = Topic.root.Get(path, false);
      if(topic == null) {
        _send(Error(request, "topic_not_found", "Topic not found: " + path));
        return;
      }
      bool isLogram = string.Equals(topic.GetField("type").AsString(null), "Core/Logram", StringComparison.Ordinal);
      string logramVid = "logram#" + path;

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespOpen, true);
      response["view"] = isLogram ? "logram" : "inspector";
      response["vid"] = isLogram ? logramVid : path;
      response["title"] = topic.name ?? string.Empty;
      _send(response);

      if(isLogram) {
        ViewOpResult result = _logram.Open(logramVid, "logram");
        if(result == null || !result.Ok) {
          X13.Log.Warning("WebUI WS#{0} auto-open({1}) - {2}", _sessionId, logramVid,
            result == null ? "no result" : (result.ErrorMessage ?? result.ErrorCode ?? "failed"));
        }
        return;
      }

      ViewOpResult stateResult = _inspectorState.Expand("inspstate#" + path, true);
      ViewOpResult manifestResult = _inspectorManifest.Expand("inspmanifest#" + path, true);
      ViewOpResult childrenResult = _inspectorChildren.Expand("inspchildren#" + path, true);
      bool allOk = stateResult != null && stateResult.Ok && manifestResult != null && manifestResult.Ok && childrenResult != null && childrenResult.Ok;
      if(!allOk) {
        X13.Log.Warning("WebUI WS#{0} auto-open({1}) - one of the Inspector panes failed to open", _sessionId, path);
      }
    }

    /// <summary>Runs one view command, answering now or later depending on what it returns.</summary>
    /// <remarks>An action declared on a topic reaches a plugin, and a plugin's work does not have
    /// to fit inside this call - a device round trip does not. So ExecuteRpc may answer with a
    /// Pending result, and the response is assembled when the continuation fires, the same way
    /// HandleLog assembles resp.log. Exactly one answer is guaranteed upstream: RPC.Call passes on
    /// only the first, and PendingRpc supplies one on a deadline if the plugin never does.</remarks>
    public void HandleRpc(JSC.JSValue request) {
      string vid = request.AsString("vid", null);
      string cmd = request.AsString("cmd", null);
      ViewOpResult result = Run(request, vid, "view_rpc_failed", "View RPC failed", p => p.ExecuteRpc(vid, cmd, request.Field("args")));
      if(result == null) return;

      if(result.Continuation != null) {
        // Through Post even when the plugin answers inline: a handler is free to reply from its
        // own worker thread, and everything this session sends has to leave from the engine
        // thread. Paying one queue hop on the inline path is the price of not having two.
        result.Continuation(final => Post("resp.rpc " + (cmd ?? "<null>"), () => SendRpcResult(request, final)));
        return;
      }
      SendRpcResult(request, result);
    }

    // Reached from the pump for a deferred answer, so _disposed is live again here - the tab can
    // close while a plugin is still working, and Dispose does not cancel what is already queued.
    private void SendRpcResult(JSC.JSValue request, ViewOpResult result) {
      if(_disposed) return;
      if(result == null || !result.Ok) {
        _send(Error(request,
          result == null ? "view_rpc_failed" : (result.ErrorCode ?? "view_rpc_failed"),
          result == null ? "View RPC failed" : (result.ErrorMessage ?? "View RPC failed")));
        return;
      }

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespRpc, true);
      if(result.Data != null && result.Data.Defined) response["data"] = result.Data;
      _send(response);
    }

    // Answers from a callback rather than by returning, as HandleRpc's deferred path does: the LiteDB scan
    // behind req.log runs on lane B, so resp.log is assembled once it comes back. Validation
    // failures still answer inline, and BeginHistory guarantees exactly one call on the engine
    // thread - the client has no timeout, so an unanswered req.log would hang its promise for
    // the life of the page.
    public void HandleLog(JSC.JSValue request) {
      _log.BeginHistory(request, result => {
        if(result == null || !result.Ok) {
          _send(Error(request, result == null ? "log_history_failed" : (result.ErrorCode ?? "log_history_failed"), result == null ? "Log history failed" : (result.ErrorMessage ?? "Log history failed")));
          return;
        }

        JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespLog, true);
        if(result.Data != null && result.Data.Defined) response["items"] = result.Data;
        _send(response);
      });
    }

    public void HandleClose(JSC.JSValue request) {
      string vid = request.AsString("vid", null);
      if(Run(request, vid, "view_close_failed", "View close failed", p => p.Close(vid)) == null) return;
      _send(ResponseBase(request, ViewMessageTypes.RespClose, true));
    }

    public void Dispose() {
      if(_disposed) return;
      _disposed = true;
      // _handlers is not cleared: its delegates only reference this session, so there is
      // nothing to release, and clearing it would race with a Handle() still in flight.
      foreach(IViewProvider provider in _providers) provider.Dispose();
      _log.Dispose();
      _targets.Clear();
    }

    private IViewProvider ResolveProvider(string vid) {
      foreach(IViewProvider provider in _providers) {
        if(provider.CanHandle(vid)) return provider;
      }
      return null;
    }

    private static JSC.JSObject ResponseBase(JSC.JSValue request, string type, bool ok) {
      JSC.JSObject response = JSC.JSObject.CreateObject();
      // Not `ValueType == Object` on its own: JSValue.Null reports ValueType Object too (with a
      // null Value), so that test passed for a "null" frame and the read below threw. GetField
      // makes the same distinction internally, which is why it is used rather than repaired
      // by hand here.
      JSC.JSValue id = request.Field("id");
      if(id.Defined) response["id"] = id;
      response["type"] = type;
      response["ok"] = ok;
      return response;
    }

    private static JSC.JSObject Error(JSC.JSValue request, string code, string message) {
      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.ProtocolError, false);
      response["code"] = code;
      response["message"] = message;
      return response;
    }
  }
}
