///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Live-topic-subtree orchestration shared by WorkspaceViewProvider (rooted at
  // Topic.root) and InspectorChildrenViewProvider (one instance per open Inspector
  // document, rooted at that document's topic). Extracted from what used to be
  // WorkspaceViewProvider's private methods; behavior is unchanged for Workspace.
  internal sealed class TopicTreeController : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    private readonly ViewTargetRegistry _targets;
    private readonly WorkspaceExpansionTracker _expansion;
    private readonly RowProjector _rowProjector;
    private readonly Topic _rootTopic;
    private readonly string _viewName;
    private readonly Func<Topic, ViewRowDto> _rootRowTemplate;
    private readonly Func<Topic, Topic, string> _resolveEditorView;
    private readonly Action<object> _onRootGone;

    // resolveEditorView is an optional per-consumer hook (rootTopic, childTopic) ->
    // ViewRowDto.EditorView override ("value" or null for "use the default") - Workspace
    // never passes one (its rows always use the default), InspectorChildrenViewProvider
    // uses it to give a DevicePLC document's "src" child the multi-line JS editor.
    /// <param name="post">Queues work for the engine thread. Omitted means "run it here, now",
    /// which is both what the tests want and what lets this controller be converted ahead of the
    /// others: an unconverted caller keeps today's synchronous behaviour and still compiles.</param>
    internal TopicTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, Func<Topic, ViewRowDto> rootRowTemplate, Func<Topic, Topic, string> resolveEditorView = null, Action<object> onRootGone = null, Action<string, Action> post = null, Func<Topic> prim = null) {
      _prim = prim;
      _post = post ?? ((what, work) => work());
      _send = send;
      _targets = targets;
      _rootTopic = rootTopic ?? Topic.root;
      _viewName = string.IsNullOrEmpty(viewName) ? "workspace" : viewName;
      _rootRowTemplate = rootRowTemplate;
      _resolveEditorView = resolveEditorView;
      _onRootGone = onRootGone;
      _expansion = new WorkspaceExpansionTracker();
      _rowProjector = new RowProjector(_viewName, _rootTopic, _expansion.IsExpanded);
    }

    private readonly Func<Topic> _prim;

    /// <summary>This session's client topic, carried as TopicEvent.Author on everything it writes.</summary>
    /// <remarks>Read per write rather than cached: the topic is created on the engine thread
    /// after this controller exists, and renamed again when the reverse DNS lookup lands. Null
    /// when no session owns these writes, which is how every existing test constructs one.</remarks>
    private Topic Prim {
      get { return _prim == null ? null : _prim(); }
    }

    internal string RootVid {
      get { return _rowProjector.TopicVid(_rootTopic); }
    }

    internal void SendRoot() {
      SendAdd(_rootTopic);
    }

    // Shared by WorkspaceViewProvider.Open and InspectorChildrenViewProvider.Open -
    // Catalog is only ever meaningful from the true broker root, regardless of
    // which tree (Workspace, Inspector's Children) the request came from.
    internal ViewOpResult OpenCatalog(string vid, string view) {
      if(!string.Equals(VidHelper.GetTopicPath(vid), "/", StringComparison.Ordinal)) {
        return ViewOpResult.Error("catalog_root_required", "Catalog can be opened only from root topic");
      }
      if(!string.Equals(view, "catalog", StringComparison.Ordinal)) {
        return ViewOpResult.Error("view_open_not_supported", "Open is not supported for view: " + (view ?? "<null>"));
      }

      string catalogUri = CatalogSettings.EnsureUri();
      JSC.JSObject data = JSC.JSObject.CreateObject();
      data["uri"] = catalogUri;
      return ViewOpResult.Open("catalog", "catalog#/", "Catalog", data);
    }

    internal ViewOpResult Expand(string vid, bool expand) {
      Topic topic = Topic.root.Get(VidHelper.GetTopicPath(vid), false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + VidHelper.GetTopicPath(vid));
      }
      // The providers route by vid, so this never fires today - but the vid carries a raw
      // topic path, and nothing else here stops it naming a topic outside this controller's
      // own subtree. IsWithinRoot already expresses that notion for the move branch.
      if(!IsWithinRoot(topic)) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }

      if(expand) ExpandTopic(topic);
      else CollapseTopic(topic);
      return ViewOpResult.Success();
    }

    internal ViewOpResult TryCreateTarget(string vid) {
      ViewTarget target = _targets.GetOrCreate(vid, CreateTarget);
      return target == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : ViewOpResult.Success();
    }

    internal ViewOpResult Commit(string vid, JSC.JSValue value) {
      ViewTarget target = _targets.GetOrCreate(vid, CreateTarget);
      if(target == null || target.TargetKind != ViewTargetKind.Topic) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }

      // Derived here rather than read off the target: CreateTarget only ever set TopicPath to
      // this very expression, so the stored copy could not differ from the vid it came from.
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }
      if(!IsWithinRoot(topic)) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      // The row DTO already carries Readonly (RowProjector), so the UI greys this
      // out - the check is what stops a request that ignores the hint.
      // WritePermission, not StateTreeController: this tree has no field path, so it needs only
      // the topic-level half - and reaching into the state tree for it was one of the two calls
      // that stopped Catalog and State from being self-contained blocks.
      string blocked = WritePermission.CheckTopic(topic);
      if(blocked != null) return ViewOpResult.Error(WritePermission.ReadonlyCode, blocked);

      topic.SetState(value, Prim);
      return ViewOpResult.Success();
    }

    internal ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }

      items = MenuBuilder.Build(topic);
      return ViewOpResult.Success();
    }

    internal ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      if(string.IsNullOrWhiteSpace(cmd)) {
        return ViewOpResult.Error("rpc_command_missing", "RPC command is missing");
      }

      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }

      return TopicRpcDispatcher.Execute(topic, cmd, args, Prim);
    }

    internal void Close() {
      Dispose();
    }

    public void Dispose() {
      _expansion.Clear();
    }

    // Unifies root-vs-regular row construction so every call site - not just the
    // ones that happen to know about the root today - gets the caller-supplied
    // root identity (e.g. Inspector's "Children" label/no-value) instead of it
    // being silently overwritten by the generic topic projector.
    private ViewRowDto BuildRow(Topic topic) {
      if(topic == _rootTopic && _rootRowTemplate != null) {
        ViewRowDto template = _rootRowTemplate(topic) ?? new ViewRowDto();
        string vid = _rowProjector.TopicVid(topic);
        return new ViewRowDto() {
          Vid = vid,
          Level = 0,
          Expander = topic.HasChildren() ? (_expansion.IsExpanded(vid) ? 2 : 1) : 0,
          Icon = template.Icon,
          Name = template.Name,
          Editor = template.Editor,
          Value = template.Value,
          Readonly = template.Readonly,
          OptionsKey = template.OptionsKey,
          Options = template.Options,
          EditorView = template.EditorView,
          // The only row that carries it, and the only one that needs to: a tree's root row is
          // the open document's own topic, and the breadcrumb bar of that document is the sole
          // reader. Computed from the topic rather than the template, because the root row
          // templates (BuildWorkspaceRootRow/BuildChildrenRootRow) are generic placeholder
          // builders that know nothing about views and would leave it null.
          AltView = RowProjector.ResolveAltView(topic),
        };
      }
      ViewRowDto row = _rowProjector.BuildTopicRow(topic);
      if(_resolveEditorView != null) row.EditorView = _resolveEditorView(_rootTopic, topic);
      return row;
    }

    private void ExpandTopic(Topic topic) {
      string vid = _rowProjector.TopicVid(topic);
      // Children|Value|Field alone only observes CHILDREN of the subscribed topic (see
      // StateTreeController's ctor comment for the full explanation of why Once is what
      // additionally makes a subscription observe the subscribed topic's own events) -
      // added only for this controller's own root so it can detect being deleted out
      // from under an open Inspector document (harmless for Workspace, whose root is
      // Topic.root and can never itself be removed - TopicRpcDispatcher.ExecuteDelete
      // already rejects topic.parent == null).
      SubRec.SubMask mask = SubRec.SubMask.Children | SubRec.SubMask.Value | SubRec.SubMask.Field;
      if(topic == _rootTopic) mask |= SubRec.SubMask.Once;
      _expansion.Expand(vid, () => topic.Subscribe(mask, OnTopicChanged));

      SendUpd(topic, topic.HasChildren() ? 2 : 0);
      foreach(Topic child in topic.children) {
        SendAdd(child);
      }
    }

    private void CollapseTopic(Topic topic) {
      string vid = _rowProjector.TopicVid(topic);
      _expansion.ExitExpanded(vid);
      WatchCollapsedExpander(topic);

      SendUpd(topic, topic.HasChildren() ? 1 : 0);
      foreach(Topic child in topic.children) {
        // One del per direct child is enough: view-store.js's remove() drops the row and
        // every following row of a greater level, so the client collapses the subtree itself.
        string childVid = _rowProjector.TopicVid(child);
        _send(ViewProtocolSerializer.Del(childVid));
        _targets.RemoveSubtree(childVid);
        _expansion.RemoveWatchSubtree(childVid, VidHelper.IsDescendant);
      }

      _expansion.RemoveExpandedDescendants(vid, VidHelper.IsDescendant);
    }

    // Queued whole, not in pieces: the body reads the repository AND mutates this controller's
    // "what the client already has" state AND writes the socket. Splitting that across ticks
    // would reintroduce exactly the interleaving the locks exist to prevent. The topic path is in
    // the label for the same reason the session id is in a frame's - without it a pump failure
    // does not say which document broke.
    private void OnTopicChanged(TopicEvent perform, SubRec sub) {
      _post("callback " + (perform == null || perform.Source == null ? "?" : perform.Source.path),
        () => OnTopicChangedCore(perform, sub));
    }

    private void OnTopicChangedCore(TopicEvent perform, SubRec sub) {
      try {
        if(perform == null || perform.Source == null || sub == null) return;

        // This controller's own root being deleted out from under it (only reachable
        // when Once was added to the root's own subscription mask, see ExpandTopic) -
        // the regular child-focused guard just below would reject a self-event anyway
        // (perform.Source.parent never equals sub.setTopic for a self-event), so this has
        // to be handled before it, not folded into the existing remove branch.
        if(perform.Kind == EventKind.Removed && perform.Source == _rootTopic && sub.setTopic == _rootTopic) {
          HandleRootRemoved();
          return;
        }

        // The root's OWN fields changing - observable for the same reason the removal above is,
        // and rejected by the same child-focused guard just below, so it has to be handled here
        // too. It matters because the root row carries AltView, which is read from exactly those
        // fields (RowProjector.ResolveAltView): without this, turning archiving on, or retyping a
        // topic to Core/Logram, would leave the open document's own breadcrumb button describing
        // what the topic used to be until someone navigated away and back.
        if(perform.Kind == EventKind.FieldChanged && perform.Source == _rootTopic && sub.setTopic == _rootTopic) {
          SendUpd(_rootTopic, null);
          return;
        }
        if(perform.Source.parent != sub.setTopic) return;

        string parentVid = _rowProjector.TopicVid(sub.setTopic);
        bool parentExpanded = _expansion.IsExpanded(parentVid);

        if(perform.Kind == EventKind.Created) {
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
          if(parentExpanded) SendAdd(perform.Source);
        } else if(perform.Kind == EventKind.Removed) {
          string vid = _rowProjector.TopicVid(perform.Source);
          if(parentExpanded) _send(ViewProtocolSerializer.Del(vid));
          _targets.RemoveSubtree(vid);
          _expansion.RemoveWatchSubtree(vid, VidHelper.IsDescendant);
          // Skipped once the parent is itself disposed - removing a topic with
          // children fans out into one remove TopicEvent per descendant (Repo.cs
          // TickStep1: `foreach(Topic tmp in c.Target.all) ...`, self first, then
          // children), so a child's own removal handler would otherwise send a
          // pointless evnt.upd "refreshing" a parent row the client was just told
          // (via the parent's own remove event) to delete entirely.
          if(!sub.setTopic.disposed) SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
        } else if(perform.Kind == EventKind.StateChanged || perform.Kind == EventKind.FieldChanged) {
          SendUpd(perform.Source, null);
        } else if(perform.Kind == EventKind.Moved) {
          string oldPath = perform.OldPath;
          if(parentExpanded) {
            if(!string.IsNullOrEmpty(oldPath)) _send(ViewProtocolSerializer.Del(_viewName + "#" + oldPath));
            SendAdd(perform.Source);
          }
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);

          // The callback above only fires for the NEW parent's subscription (see the
          // guard at the top: perform.Source.parent now points at the new parent, so
          // the OLD parent's own subscription - if any - never matches and never
          // runs). Without this, the old parent's expander arrow goes stale when it
          // loses its last child (or gains one back via a subsequent move/paste).
          // Only relevant if the old parent is actually part of THIS controller's
          // own rooted subtree - for Workspace (root=Topic.root) that's always true,
          // but Inspector's Children controller is rooted elsewhere, and a move can
          // land a topic there from a completely unrelated part of the tree (e.g.
          // paste from a sibling branch); that old parent belongs to no vid this
          // controller owns, so building one for it would be meaningless.
          string oldParentPath = ParentTopicPath(oldPath);
          if(oldParentPath != null) {
            Topic oldParent = Topic.root.Get(oldParentPath, false);
            if(oldParent != null && oldParent != sub.setTopic && IsWithinRoot(oldParent)) {
              SendUpd(oldParent, oldParent.HasChildren() ? (_expansion.IsExpanded(_rowProjector.TopicVid(oldParent)) ? 2 : 1) : 0);
            }
          }
        }
      }
      catch(Exception ex) {
        // Full stack: an unexpected fault inside a tick callback is exactly the case where
        // the message alone does not identify the frame that produced it.
        Log.Warning("TopicTreeController.OnTopicChanged - {0}", ex.ToString());
      }
    }

    // Tells the frontend this controller's own root is gone (evnt.del on RootVid - the
    // frontend recognizes this as "the open Inspector document's own topic was deleted"
    // and navigates away, see app-shell.js #onDocumentRootDeleted) and stops this
    // controller from doing anything further - mirrors Dispose(), reused directly since
    // there is nothing left to keep alive once the root itself no longer exists.
    private void HandleRootRemoved() {
      string vid = RootVid;
      _send(ViewProtocolSerializer.Del(vid));
      _targets.RemoveSubtree(vid);
      _targets.Remove(vid);
      Dispose();
      // Let the owning provider forget us - a disposed controller left in its map would keep
      // receiving requests. Workspace passes no callback: its root is Topic.root and cannot go.
      if(_onRootGone != null) _onRootGone(this);
    }

    private static string ParentTopicPath(string path) {
      if(string.IsNullOrEmpty(path) || path == "/") return null;
      int index = path.LastIndexOf('/');
      return index <= 0 ? "/" : path.Substring(0, index);
    }

    private bool IsWithinRoot(Topic topic) {
      for(Topic cur = topic; cur != null; cur = cur.parent) {
        if(cur == _rootTopic) return true;
      }
      return false;
    }

    // Takes the Topic rather than re-deriving it from row.Vid: every caller already has it,
    // and the lookup was both a full tree walk per emitted row and a window in which the
    // topic could vanish, silently skipping its collapsed-expander watch.
    private void SendAdd(Topic topic) {
      ViewRowDto row = BuildRow(topic);
      RememberTarget(row);
      WatchCollapsedExpander(topic);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, row.Vid);
      dto["level"] = new JSL.Number(row.Level);
      if(row.Expander != 0) dto["expander"] = new JSL.Number(row.Expander);
      dto["icon"] = row.Icon ?? string.Empty;
      dto["name"] = row.Name ?? string.Empty;
      if(!string.IsNullOrEmpty(row.Editor) && row.Editor != "Default") dto["editor"] = row.Editor;
      if(!JsonTreeRowHelpers.IsDefaultValue(row.Value)) dto["value"] = row.Value;
      if(row.Readonly) dto["readonly"] = row.Readonly;
      if(!string.IsNullOrEmpty(row.AltView)) dto["altView"] = row.AltView;
      if(row.Options != null) dto["options"] = row.Options;
      if(!string.IsNullOrEmpty(row.EditorView)) dto["editorView"] = row.EditorView;
      _send(dto);
    }

    private void SendUpd(Topic topic, int? expander) {
      ViewRowDto row = BuildRow(topic);
      if(expander.HasValue) row.Expander = expander.Value;

      ViewRowDto previous = null;
      ViewTarget target = _targets.Get(row.Vid);
      if(target != null) previous = target.CachedRow;

      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, row.Vid);
      bool changed = false;

      if(expander.HasValue && (previous == null || previous.Expander != row.Expander)) {
        dto["expander"] = new JSL.Number(row.Expander);
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.Icon, row.Icon)) {
        dto["icon"] = row.Icon ?? string.Empty;
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.Editor, row.Editor)) {
        dto["editor"] = row.Editor ?? "Default";
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.JsValueEquals(previous.Value, row.Value)) {
        dto["value"] = row.Value ?? JSC.JSValue.Null;
        changed = true;
      }
      if(previous == null || previous.Readonly != row.Readonly) {
        dto["readonly"] = row.Readonly;
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.AltView, row.AltView)) {
        // Empty rather than omitted when it goes away: an update carries only what changed, so a
        // missing key would leave the client holding the previous answer - the topic having just
        // stopped being archived is exactly when the button has to disappear.
        dto["altView"] = row.AltView ?? string.Empty;
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.OptionsKey, row.OptionsKey)) {
        dto["options"] = row.Options ?? JSC.JSValue.Null;
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.EditorView, row.EditorView)) {
        dto["editorView"] = row.EditorView ?? string.Empty;
        changed = true;
      }

      RememberTarget(row);
      if(changed) _send(dto);
    }

    private void WatchCollapsedExpander(Topic topic) {
      if(topic == null) return;
      string vid = _rowProjector.TopicVid(topic);
      _expansion.Watch(vid, () => topic.Subscribe(SubRec.SubMask.Children, OnTopicChanged));
    }

    private void RememberTarget(ViewRowDto row) {
      _targets.Add(row.Vid, new ViewTarget() {
        TargetKind = ViewTargetKind.Topic,
        CachedRow = row,
      });
    }

    // The emptiness guard stays even though the path is no longer stored: it is what makes a vid
    // naming no topic fail as view_target_not_found instead of resolving to Topic.root, which
    // Topic.Resolve returns for an empty path.
    private ViewTarget CreateTarget(string vid) {
      if(VidHelper.GetView(vid) != _viewName) return null;
      if(string.IsNullOrEmpty(VidHelper.GetTopicPath(vid))) return null;
      return new ViewTarget() {
        TargetKind = ViewTargetKind.Topic,
      };
    }
  }
}
