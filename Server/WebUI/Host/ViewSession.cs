///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using NiL.JS.Extensions;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal sealed class ViewSession : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly WorkspaceViewProvider _workspace;
    private readonly CatalogViewProvider _catalog;
    private readonly InspectorChildrenViewProvider _inspectorChildren;
    private readonly InspectorStateViewProvider _inspectorState;
    private readonly InspectorManifestViewProvider _inspectorManifest;
    private readonly LogramViewProvider _logram;
    private readonly LogHandler _log;
    private readonly List<IViewProvider> _providers;

    // sendRaw bypasses the caller's verbose WS-traffic tracing (which itself logs via
    // X13.Log.Debug) - required for LogHandler's live push, since tracing an evnt.log
    // send would fire X13.Log.Write again and push another evnt.log, forever. Falls
    // back to send when the caller has no separate raw channel (e.g. tests).
    public ViewSession(Action<JSC.JSObject> send, Action<JSC.JSObject> sendRaw = null) {
      _send = send;
      _targets = new ViewTargetRegistry();
      _workspace = new WorkspaceViewProvider(_send, _targets);
      _catalog = new CatalogViewProvider(_send);
      _inspectorChildren = new InspectorChildrenViewProvider(_send, _targets);
      _inspectorState = new InspectorStateViewProvider(_send, _targets);
      _inspectorManifest = new InspectorManifestViewProvider(_send, _targets);
      _logram = new LogramViewProvider(_send);
      _log = new LogHandler(sendRaw ?? _send);
      _providers = new List<IViewProvider>();
      _providers.Add(_workspace);
      _providers.Add(_catalog);
      _providers.Add(_inspectorChildren);
      _providers.Add(_inspectorState);
      _providers.Add(_inspectorManifest);
      _providers.Add(_logram);
    }

    public void HandleHello(JSC.JSValue request) {
      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespHello, true);
      response["name"] = Environment.MachineName;
      _send(response);
      _workspace.SendRoot();
    }

    public void HandleExpand(JSC.JSValue request) {
      string vid = JsLib.OfString(request["vid"], null);
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : provider.Expand(vid, request["expand"].As<bool>());
      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "view_expand_failed" : (result.ErrorCode ?? "view_expand_failed"), result == null ? "View expand failed" : (result.ErrorMessage ?? "View expand failed")));
        return;
      }
      _send(ResponseBase(request, ViewMessageTypes.RespExpand, true));
    }

    public void HandleCommit(JSC.JSValue request) {
      string vid = JsLib.OfString(request["vid"], null);
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : provider.Commit(vid, request["value"]);
      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "view_commit_failed" : (result.ErrorCode ?? "view_commit_failed"), result == null ? "View commit failed" : (result.ErrorMessage ?? "View commit failed")));
        return;
      }
      _send(ResponseBase(request, ViewMessageTypes.RespCommit, true));
    }

    public void HandleMenu(JSC.JSValue request) {
      string vid = JsLib.OfString(request["vid"], null);
      List<MenuItemDto> items = null;
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : provider.BuildMenu(vid, out items);
      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "view_menu_failed" : (result.ErrorCode ?? "view_menu_failed"), result == null ? "View menu failed" : (result.ErrorMessage ?? "View menu failed")));
        return;
      }

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespMenu, true);
      response["vid"] = vid;
      response["items"] = ViewProtocolSerializer.SerializeMenuItems(items);
      _send(response);
    }


    public void HandleOpen(JSC.JSValue request) {
      string vid = JsLib.OfString(request["vid"], null);
      string view = JsLib.OfString(request["view"], null);
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
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : provider.Open(vid, view);
      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "view_open_failed" : (result.ErrorCode ?? "view_open_failed"), result == null ? "View open failed" : (result.ErrorMessage ?? "View open failed")));
        return;
      }

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespOpen, true);
      response["view"] = result.View ?? view ?? string.Empty;
      response["vid"] = result.Vid ?? vid ?? string.Empty;
      response["title"] = result.Title ?? string.Empty;
      if(result.Data != null && result.Data.Defined) response["data"] = result.Data;
      _send(response);
    }

    // Resolves Core/Logram itself (one field read - see LogramViewProvider.Open)
    // instead of asking the frontend to guess a view#-prefixed vid up front. Opens
    // exactly one side and pushes its evnt.add rows before responding, same as the
    // explicit-view path above would for whichever side wins - the losing side is
    // never touched, so a large Logram's children are never fetched just to be
    // discarded, and a plain topic's Logram graph never gets built for nothing.
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
      bool isLogram = string.Equals(JsLib.OfString(topic.GetField("type"), null), "Core/Logram", StringComparison.Ordinal);

      if(isLogram) {
        string logramVid = "logram#" + path;
        ViewOpResult result = _logram.Open(logramVid, "logram");
        if(result == null || !result.Ok) {
          _send(Error(request, result == null ? "view_open_failed" : (result.ErrorCode ?? "view_open_failed"), result == null ? "View open failed" : (result.ErrorMessage ?? "View open failed")));
          return;
        }
        JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespOpen, true);
        response["view"] = result.View ?? "logram";
        response["vid"] = result.Vid ?? logramVid;
        response["title"] = result.Title ?? string.Empty;
        if(result.Data != null && result.Data.Defined) response["data"] = result.Data;
        _send(response);
        return;
      }

      ViewOpResult stateResult = _inspectorState.Expand("inspstate#" + path, true);
      ViewOpResult manifestResult = _inspectorManifest.Expand("inspmanifest#" + path, true);
      ViewOpResult childrenResult = _inspectorChildren.Expand("inspchildren#" + path, true);
      bool allOk = stateResult != null && stateResult.Ok && manifestResult != null && manifestResult.Ok && childrenResult != null && childrenResult.Ok;
      if(!allOk) {
        _send(Error(request, "view_open_failed", "View open failed: " + path));
        return;
      }

      JSC.JSObject inspectorResponse = ResponseBase(request, ViewMessageTypes.RespOpen, true);
      inspectorResponse["view"] = "inspector";
      inspectorResponse["vid"] = path;
      inspectorResponse["title"] = topic.name ?? string.Empty;
      _send(inspectorResponse);
    }

    public void HandleRpc(JSC.JSValue request) {
      string vid = JsLib.OfString(request["vid"], null);
      string cmd = JsLib.OfString(request["cmd"], null);
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : provider.ExecuteRpc(vid, cmd, request["args"]);

      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "view_rpc_failed" : (result.ErrorCode ?? "view_rpc_failed"), result == null ? "View RPC failed" : (result.ErrorMessage ?? "View RPC failed")));
        return;
      }

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespRpc, true);
      if(result.Data != null && result.Data.Defined) response["data"] = result.Data;
      _send(response);
    }

    public void HandleLog(JSC.JSValue request) {
      ViewOpResult result = _log.HandleHistory(request);
      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "log_history_failed" : (result.ErrorCode ?? "log_history_failed"), result == null ? "Log history failed" : (result.ErrorMessage ?? "Log history failed")));
        return;
      }

      JSC.JSObject response = ResponseBase(request, ViewMessageTypes.RespLog, true);
      if(result.Data != null && result.Data.Defined) response["items"] = result.Data;
      _send(response);
    }

    public void HandleClose(JSC.JSValue request) {
      string vid = JsLib.OfString(request["vid"], null);
      IViewProvider provider = ResolveProvider(vid);
      ViewOpResult result = provider == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : provider.Close(vid);
      if(result == null || !result.Ok) {
        _send(Error(request, result == null ? "view_close_failed" : (result.ErrorCode ?? "view_close_failed"), result == null ? "View close failed" : (result.ErrorMessage ?? "View close failed")));
        return;
      }
      _send(ResponseBase(request, ViewMessageTypes.RespClose, true));
    }

    public void Dispose() {
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
      if(request != null && request.ValueType == JSC.JSValueType.Object && request["id"].Defined) response["id"] = request["id"];
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
