///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using NiL.JS.Extensions;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal sealed class ViewSession : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly WorkspaceViewProvider _workspace;
    private readonly CatalogViewProvider _catalog;
    private readonly List<IViewProvider> _providers;

    public ViewSession(Action<JSC.JSObject> send) {
      _send = send;
      _targets = new ViewTargetRegistry();
      _workspace = new WorkspaceViewProvider(_send, _targets);
      _catalog = new CatalogViewProvider(_send);
      _providers = new List<IViewProvider>();
      _providers.Add(_workspace);
      _providers.Add(_catalog);
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

    public void Dispose() {
      foreach(IViewProvider provider in _providers) provider.Dispose();
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
