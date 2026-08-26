///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal sealed class WorkspaceViewProvider : ViewProviderBase {
    private const string ViewName = "workspace";
    private readonly TopicTreeController _controller;

    public WorkspaceViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets, Action<string, Action> post = null, Func<Topic> prim = null) {
      _controller = new TopicTreeController(send, targets, Topic.root, ViewName, BuildWorkspaceRootRow, null, null, post, prim);
    }

    // No Icon: this row names no topic to resolve one from, and the default an iconless row
    // draws is the client's call (view-row.js `row.icon || '/ide_icons/ty_topic.png'`). Naming
    // the file here pinned this row to a value the client is free to change.
    private static ViewRowDto BuildWorkspaceRootRow(Topic root) {
      return new ViewRowDto() {
        Name = Environment.MachineName,
        Editor = "ConnectionStatus",
        Value = "connected",
        Readonly = true,
      };
    }

    public void SendRoot() {
      _controller.SendRoot();
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult TryCreateTarget(string vid) {
      return _controller.TryCreateTarget(vid);
    }

    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return _controller.BuildMenu(vid, out items);
    }

    public override ViewOpResult Expand(string vid, bool expand) {
      return _controller.Expand(vid, expand);
    }

    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
      return _controller.Commit(vid, value);
    }

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return _controller.ExecuteRpc(vid, cmd, args);
    }

    public override ViewOpResult Open(string vid, string view) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return _controller.OpenCatalog(vid, view);
    }

    public override void Dispose() {
      _controller.Dispose();
    }
  }
}
