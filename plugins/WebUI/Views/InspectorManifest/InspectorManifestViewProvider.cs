///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;

namespace X13.WebUI {
  // Backs the Inspector document's "Manifest" tree: the topic's own manifest, rendered as a
  // third root alongside "State" and "Children" inside the same document. One
  // ManifestTreeController per distinct root vid, created lazily on first expand and disposed
  // via Close(vid) - the lifecycle and request routing live in TreeViewProviderBase.
  internal sealed class InspectorManifestViewProvider : TreeViewProviderBase<ManifestTreeController> {
    private const string View = "inspmanifest";

    public InspectorManifestViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, post, prim) {
    }

    protected override string ViewName {
      get { return View; }
    }

    protected override ManifestTreeController CreateController(Topic rootTopic) {
      return new ManifestTreeController(_send, _targets, rootTopic, View, ForgetRoot, _post, _prim);
    }

    protected override void SendControllerRoot(ManifestTreeController controller) {
      controller.SendRoot();
    }

    protected override string RootVidOf(ManifestTreeController controller) {
      return controller.RootVid;
    }

    protected override ViewOpResult ExpandCore(ManifestTreeController controller, string vid, bool expand) {
      return controller.Expand(vid, expand);
    }

    protected override ViewOpResult CommitCore(ManifestTreeController controller, string vid, JSC.JSValue value) {
      return controller.Commit(vid, value);
    }

    protected override ViewOpResult BuildMenuCore(ManifestTreeController controller, string vid, out List<MenuItemDto> items) {
      return controller.BuildMenu(vid, out items);
    }

    protected override ViewOpResult ExecuteRpcCore(ManifestTreeController controller, string vid, string cmd, JSC.JSValue args) {
      return controller.ExecuteRpc(vid, cmd, args);
    }
  }
}
