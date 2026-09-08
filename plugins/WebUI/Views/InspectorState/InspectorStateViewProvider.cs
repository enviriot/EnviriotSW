///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;

namespace X13.WebUI {
  // Backs the Inspector document's "State" tree: the topic's own live JSON State value,
  // rendered as a second root alongside the "Children" tree (see
  // InspectorChildrenViewProvider) inside the same document. One StateTreeController per
  // distinct root vid, created lazily on first expand and disposed via Close(vid) when the
  // frontend navigates away - the lifecycle and request routing live in TreeViewProviderBase.
  internal sealed class InspectorStateViewProvider : TreeViewProviderBase<StateTreeController> {
    private const string View = "inspstate";

    public InspectorStateViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, post, prim) {
    }

    protected override string ViewName {
      get { return View; }
    }

    protected override StateTreeController CreateController(Topic rootTopic) {
      return new StateTreeController(_send, _targets, rootTopic, View, ForgetRoot, _post, _prim);
    }

    protected override void SendControllerRoot(StateTreeController controller) {
      controller.SendRoot();
    }

    protected override string RootVidOf(StateTreeController controller) {
      return controller.RootVid;
    }

    protected override ViewOpResult ExpandCore(StateTreeController controller, string vid, bool expand) {
      return controller.Expand(vid, expand);
    }

    protected override ViewOpResult CommitCore(StateTreeController controller, string vid, JSC.JSValue value) {
      return controller.Commit(vid, value);
    }

    protected override ViewOpResult BuildMenuCore(StateTreeController controller, string vid, out List<MenuItemDto> items) {
      return controller.BuildMenu(vid, out items);
    }

    protected override ViewOpResult ExecuteRpcCore(StateTreeController controller, string vid, string cmd, JSC.JSValue args) {
      return controller.ExecuteRpc(vid, cmd, args);
    }
  }
}
