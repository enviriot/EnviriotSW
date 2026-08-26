///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Backs the Inspector document's "Children" tree: a live topic subtree rooted at whichever
  // topic the document is currently showing (not the broker root). One TopicTreeController per
  // distinct root vid, created lazily on first expand and disposed via Close(vid) - unlike
  // Workspace, expansion state here never outlives the open document. Lifecycle and request
  // routing live in TreeViewProviderBase; what is specific to this tree is below.
  internal sealed class InspectorChildrenViewProvider : TreeViewProviderBase<TopicTreeController> {
    private const string View = "inspchildren";
    private static readonly HashSet<string> RootOnlySuppressedCmds =
      new HashSet<string>(StringComparer.Ordinal) { "rename", "cut", "delete" };

    public InspectorChildrenViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, post, prim) {
    }

    protected override string ViewName {
      get { return View; }
    }

    // A real topic graph, unlike State/Manifest: a request legitimately names a different child
    // topic under the same root, so ownership is a subtree test rather than the base's exact
    // match. Strict descendant only - the base answers the equal case with a dictionary hit
    // before it ever gets here, exactly as VidHelper.IsDescendant returned false for equal vids.
    protected override bool Owns(Topic root, Topic topic) {
      if(root == null || topic == null) return false;
      for(Topic parent = topic.parent; parent != null; parent = parent.parent) {
        if(ReferenceEquals(parent, root)) return true;
      }
      return false;
    }

    protected override TopicTreeController CreateController(Topic rootTopic) {
      return new TopicTreeController(_send, _targets, rootTopic, View, BuildChildrenRootRow, ResolveEditorView, ForgetRoot, _post, _prim);
    }

    protected override void SendControllerRoot(TopicTreeController controller) {
      controller.SendRoot();
    }

    protected override string RootVidOf(TopicTreeController controller) {
      return controller.RootVid;
    }

    protected override ViewOpResult ExpandCore(TopicTreeController controller, string vid, bool expand) {
      return controller.Expand(vid, expand);
    }

    protected override ViewOpResult CommitCore(TopicTreeController controller, string vid, JSC.JSValue value) {
      return controller.Commit(vid, value);
    }

    protected override ViewOpResult BuildMenuCore(TopicTreeController controller, string vid, out List<MenuItemDto> items) {
      ViewOpResult result = controller.BuildMenu(vid, out items);
      if(result.Ok && vid == controller.RootVid) SuppressRootOnlyCommands(items);
      return result;
    }

    protected override ViewOpResult ExecuteRpcCore(TopicTreeController controller, string vid, string cmd, JSC.JSValue args) {
      return controller.ExecuteRpc(vid, cmd, args);
    }

    // Only meaningful for a Children tree whose root vid IS the true broker root
    // (topic.parent == null) - same "Catalog can be opened only from root topic" rule
    // Workspace enforces, via the same shared TopicTreeController.OpenCatalog.
    public override ViewOpResult Open(string vid, string view) {
      TopicTreeController controller = FindOwnerFor(vid);
      if(controller == null || vid != controller.RootVid) {
        return ViewOpResult.Error("view_open_not_supported", "Open is not supported for view target: " + (vid ?? "<null>"));
      }
      return controller.OpenCatalog(vid, view);
    }

    // The document's own topic having a DevicePLC editor (Ext/DevicePLC, set via MsDevice.cs
    // on its "pa0" child) means it also has a direct child topic literally named "src"
    // (MsDevice.cs sets that child's own manifest editor to "JS") holding its program source -
    // give that one row the multi-line/auto-growing JS editor (view-row.js's "value" view)
    // instead of the compact single-line default every other Children row gets, since PLC
    // source is usually more than one line.
    private static string ResolveEditorView(Topic rootTopic, Topic childTopic) {
      if(childTopic == null || childTopic.parent != rootTopic) return null;
      if(!string.Equals(childTopic.name, "src", StringComparison.Ordinal)) return null;
      return string.Equals(EditorHelper.Resolve(rootTopic), "DevicePLC", StringComparison.Ordinal) ? "value" : null;
    }

    // No Icon - see WorkspaceViewProvider.BuildWorkspaceRootRow: a synthetic header row names no
    // topic, and what an iconless row draws is the client's decision.
    private static ViewRowDto BuildChildrenRootRow(Topic topic) {
      return new ViewRowDto() {
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
