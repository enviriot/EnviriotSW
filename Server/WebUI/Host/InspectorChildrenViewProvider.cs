///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  // Backs the Inspector document's "Children" tree: a live topic subtree rooted at
  // whichever topic the Inspector document is currently showing (not the broker
  // root). One TopicTreeController is created lazily per distinct root vid, the
  // first time it's expanded, and disposed explicitly via Close(vid) when the
  // frontend navigates away from that document - unlike Workspace, expansion state
  // here never outlives the open document.
  internal sealed class InspectorChildrenViewProvider : ViewProviderBase {
    private const string ViewName = "inspchildren";
    private static readonly HashSet<string> RootOnlySuppressedCmds =
      new HashSet<string>(StringComparer.Ordinal) { "rename", "cut", "delete" };

    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly object _gate = new object();
    private readonly Dictionary<string, TopicTreeController> _roots = new Dictionary<string, TopicTreeController>(StringComparer.Ordinal);

    public InspectorChildrenViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets) {
      _send = send;
      _targets = targets;
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult Expand(string vid, bool expand) {
      TopicTreeController controller = GetOrCreateOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + VidHelper.GetTopicPath(vid));
      }
      return controller.Expand(vid, expand);
    }

    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
      TopicTreeController controller = FindOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return controller.Commit(vid, value);
    }

    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      TopicTreeController controller = FindOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      ViewOpResult result = controller.BuildMenu(vid, out items);
      if(result.Ok && vid == controller.RootVid) SuppressRootOnlyCommands(items);
      return result;
    }

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      TopicTreeController controller = FindOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return controller.ExecuteRpc(vid, cmd, args);
    }

    // Only meaningful for a Children tree whose root vid IS the true broker root
    // (topic.parent == null) - same "Catalog can be opened only from root topic"
    // rule Workspace enforces, via the same shared TopicTreeController.OpenCatalog.
    public override ViewOpResult Open(string vid, string view) {
      TopicTreeController controller = FindOwner(vid);
      if(controller == null || vid != controller.RootVid) {
        return ViewOpResult.Error("view_open_not_supported", "Open is not supported for view target: " + (vid ?? "<null>"));
      }
      return controller.OpenCatalog(vid, view);
    }

    public override ViewOpResult Close(string vid) {
      lock(_gate) {
        string ownerKey = FindOwnerKeyLocked(vid);
        if(ownerKey == null) return ViewOpResult.Success();
        _roots[ownerKey].Dispose();
        _targets.RemoveSubtree(ownerKey);
        _targets.Remove(ownerKey);
        _roots.Remove(ownerKey);
        return ViewOpResult.Success();
      }
    }

    public override void Dispose() {
      lock(_gate) {
        foreach(TopicTreeController controller in _roots.Values) controller.Dispose();
        _roots.Clear();
      }
    }

    private TopicTreeController GetOrCreateOwner(string vid) {
      lock(_gate) {
        TopicTreeController existing = FindOwnerLocked(vid);
        if(existing != null) return existing;

        // The first request for a not-yet-seen root can only be a request against
        // the root vid itself - the frontend has no way to reference a descendant
        // vid before the root row has been sent at least once.
        string topicPath = VidHelper.GetTopicPath(vid);
        Topic rootTopic = Topic.root.Get(topicPath, false);
        if(rootTopic == null) return null;

        TopicTreeController controller = new TopicTreeController(_send, _targets, rootTopic, ViewName, BuildChildrenRootRow, ResolveEditorView);
        _roots[vid] = controller;
        controller.SendRoot();
        return controller;
      }
    }

    // The document's own topic having a DevicePLC editor (Ext/DevicePLC, set via
    // MsDevice.cs on its "pa0" child) means it also has a direct child topic literally
    // named "src" (MsDevice.cs sets that child's own manifest editor to "JS") holding
    // its program source - give that one row the multi-line/auto-growing JS editor
    // (view-row.js's "value" view) instead of the compact single-line default every
    // other Children row gets, since PLC source is usually more than one line.
    private static string ResolveEditorView(Topic rootTopic, Topic childTopic) {
      if(childTopic == null || childTopic.parent != rootTopic) return null;
      if(!string.Equals(childTopic.name, "src", StringComparison.Ordinal)) return null;
      return string.Equals(EditorHelper.Resolve(rootTopic), "DevicePLC", StringComparison.Ordinal) ? "value" : null;
    }

    private TopicTreeController FindOwner(string vid) {
      lock(_gate) {
        return FindOwnerLocked(vid);
      }
    }

    private TopicTreeController FindOwnerLocked(string vid) {
      string key = FindOwnerKeyLocked(vid);
      return key == null ? null : _roots[key];
    }

    private string FindOwnerKeyLocked(string vid) {
      if(string.IsNullOrEmpty(vid)) return null;
      foreach(string key in _roots.Keys) {
        if(key == vid || VidHelper.IsDescendant(key, vid)) return key;
      }
      return null;
    }

    private static ViewRowDto BuildChildrenRootRow(Topic topic) {
      return new ViewRowDto() {
        Icon = "/ide_icons/ty_topic.png",
        Name = "Children",
        Editor = "Default",
        Value = null,
        Readonly = true,
      };
    }

    private static void SuppressRootOnlyCommands(List<MenuItemDto> items) {
      if(items == null) return;
      foreach(MenuItemDto item in items) {
        if(item.Cmd != null && RootOnlySuppressedCmds.Contains(item.Cmd)) item.Enabled = false;
        if(item.Children != null) SuppressRootOnlyCommands(item.Children);
      }
    }
  }
}
