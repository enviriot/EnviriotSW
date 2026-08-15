///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  // Backs the Inspector document's "State" tree: the topic's own live JSON State
  // value, rendered as a second root alongside the "Children" tree (see
  // InspectorChildrenViewProvider) inside the same document. One
  // StateTreeController is created lazily per distinct root vid, the first time
  // it's expanded, and disposed explicitly via Close(vid) when the frontend
  // navigates away from that document.
  internal sealed class InspectorStateViewProvider : ViewProviderBase {
    private const string ViewName = "inspstate";

    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly object _gate = new object();
    private readonly Dictionary<string, StateTreeController> _roots = new Dictionary<string, StateTreeController>(StringComparer.Ordinal);

    public InspectorStateViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets) {
      _send = send;
      _targets = targets;
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult Expand(string vid, bool expand) {
      StateTreeController controller = GetOrCreateOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + VidHelper.GetTopicPath(vid));
      }
      return controller.Expand(vid, expand);
    }

    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
      StateTreeController controller = FindOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return controller.Commit(vid, value);
    }

    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      StateTreeController controller = FindOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return controller.BuildMenu(vid, out items);
    }

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      StateTreeController controller = FindOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      return controller.ExecuteRpc(vid, cmd, args);
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
        foreach(StateTreeController controller in _roots.Values) controller.Dispose();
        _roots.Clear();
      }
    }

    private StateTreeController GetOrCreateOwner(string vid) {
      lock(_gate) {
        StateTreeController existing = FindOwnerLocked(vid);
        if(existing != null) return existing;

        // The first request for a not-yet-seen root can only be a request against
        // the root vid itself - the frontend has no way to reference a descendant
        // vid before the root row has been sent at least once.
        string topicPath = VidHelper.GetTopicPath(vid);
        Topic rootTopic = Topic.root.Get(topicPath, false);
        if(rootTopic == null) return null;

        StateTreeController controller = new StateTreeController(_send, _targets, rootTopic, ViewName);
        _roots[vid] = controller;
        controller.SendRoot();
        return controller;
      }
    }

    private StateTreeController FindOwner(string vid) {
      lock(_gate) {
        return FindOwnerLocked(vid);
      }
    }

    private StateTreeController FindOwnerLocked(string vid) {
      string key = FindOwnerKeyLocked(vid);
      return key == null ? null : _roots[key];
    }

    // Exact topic-path match, not VidHelper.IsDescendant - unlike
    // InspectorChildrenViewProvider (a real topic-graph tree, where a descendant vid
    // legitimately names a *different* child topic under the same root), every vid a
    // StateTreeController ever owns shares the *one* root topic path, differing only
    // in field path. IsDescendant walks parent vids via dotted field-path removal,
    // which also happens to match when one topic's path is textually a path-prefix of
    // another *unrelated* topic's path (e.g. root "/Test/X" vs a request for its real
    // child topic "/Test/X/Y", both with empty field path) - that's a different
    // topic's own inspstate document, not a field of this one, and must not be routed
    // here (confirmed live: a stray commit landed on the wrong topic's manifest
    // before this fix).
    private string FindOwnerKeyLocked(string vid) {
      if(string.IsNullOrEmpty(vid)) return null;
      string topicPath = VidHelper.GetTopicPath(vid);
      foreach(string key in _roots.Keys) {
        if(string.Equals(VidHelper.GetTopicPath(key), topicPath, StringComparison.Ordinal)) return key;
      }
      return null;
    }
  }
}
