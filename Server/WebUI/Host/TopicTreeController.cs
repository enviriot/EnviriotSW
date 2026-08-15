///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  // Live-topic-subtree orchestration shared by WorkspaceViewProvider (rooted at
  // Topic.root) and InspectorChildrenViewProvider (one instance per open Inspector
  // document, rooted at that document's topic). Extracted from what used to be
  // WorkspaceViewProvider's private methods; behavior is unchanged for Workspace.
  internal sealed class TopicTreeController : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly WorkspaceExpansionTracker _expansion;
    private readonly WorkspaceRowProjector _rowProjector;
    private readonly Topic _rootTopic;
    private readonly string _viewName;
    private readonly Func<Topic, ViewRowDto> _rootRowTemplate;
    private readonly Func<Topic, Topic, string> _resolveEditorView;

    // resolveEditorView is an optional per-consumer hook (rootTopic, childTopic) ->
    // ViewRowDto.EditorView override ("value" or null for "use the default") - Workspace
    // never passes one (its rows always use the default), InspectorChildrenViewProvider
    // uses it to give a DevicePLC document's "src" child the multi-line JS editor.
    internal TopicTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, Func<Topic, ViewRowDto> rootRowTemplate, Func<Topic, Topic, string> resolveEditorView = null) {
      _send = send;
      _targets = targets;
      _rootTopic = rootTopic ?? Topic.root;
      _viewName = string.IsNullOrEmpty(viewName) ? "workspace" : viewName;
      _rootRowTemplate = rootRowTemplate;
      _resolveEditorView = resolveEditorView;
      _expansion = new WorkspaceExpansionTracker();
      _rowProjector = new WorkspaceRowProjector(_viewName, _rootTopic, _expansion.IsExpanded);
    }

    internal string RootVid {
      get { return _rowProjector.TopicVid(_rootTopic); }
    }

    internal void SendRoot() {
      SendAdd(BuildRow(_rootTopic));
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

      string catalogUri = CatalogViewProvider.EnsureCatalogUri();
      JSC.JSObject data = JSC.JSObject.CreateObject();
      data["uri"] = catalogUri;
      return ViewOpResult.Open("catalog", "catalog#/", "Catalog", data);
    }

    internal ViewOpResult Expand(string vid, bool expand) {
      Topic topic = Topic.root.Get(VidHelper.GetTopicPath(vid), false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + VidHelper.GetTopicPath(vid));
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

      Topic topic = Topic.root.Get(target.TopicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + target.TopicPath);
      }

      topic.SetState(value);
      return ViewOpResult.Success();
    }

    internal ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }

      items = WorkspaceMenuBuilder.Build(topic);
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

      return WorkspaceRpcDispatcher.Execute(topic, cmd, args);
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
          // Computed from the topic directly, not template.IsLogram - root row
          // templates (BuildWorkspaceRootRow/BuildChildrenRootRow) are generic
          // placeholder builders that don't know about Logram at all, so they never
          // set it, and the root row would always incorrectly report false otherwise.
          // This matters concretely: the Children tree's root row represents the
          // currently-open document's own topic, so opening a Logram diagram FROM
          // there (e.g. re-clicking its own "Children" header) needs this to be
          // right, same as any other row - see WorkspaceRowProjector.BuildTopicRow.
          IsLogram = string.Equals(JsLib.OfString(topic.GetField("type"), null), "Core/Logram", StringComparison.Ordinal),
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
      // Topic.root and can never itself be removed - WorkspaceRpcDispatcher.ExecuteDelete
      // already rejects topic.parent == null).
      SubRec.SubMask mask = SubRec.SubMask.Children | SubRec.SubMask.Value | SubRec.SubMask.Field;
      if(topic == _rootTopic) mask |= SubRec.SubMask.Once;
      _expansion.Expand(vid, () => topic.Subscribe(mask, OnTopicChanged));

      SendUpd(topic, topic.HasChildren() ? 2 : 0);
      foreach(Topic child in topic.children) {
        SendAdd(BuildRow(child));
      }
    }

    private void CollapseTopic(Topic topic) {
      string vid = _rowProjector.TopicVid(topic);
      _expansion.ExitExpanded(vid);
      WatchCollapsedExpander(topic);

      SendUpd(topic, topic.HasChildren() ? 1 : 0);
      foreach(Topic child in topic.children) {
        _send(ViewProtocolSerializer.Del(_rowProjector.TopicVid(child)));
        _targets.RemoveSubtree(_rowProjector.TopicVid(child));
        _expansion.RemoveWatchSubtree(_rowProjector.TopicVid(child), VidHelper.IsDescendant);
      }

      _expansion.RemoveExpandedDescendants(vid, VidHelper.IsDescendant);
    }

    private void OnTopicChanged(Perform perform, SubRec sub) {
      try {
        if(perform == null || perform.src == null || sub == null) return;

        // This controller's own root being deleted out from under it (only reachable
        // when Once was added to the root's own subscription mask, see ExpandTopic) -
        // the regular child-focused guard just below would reject a self-event anyway
        // (perform.src.parent never equals sub.setTopic for a self-event), so this has
        // to be handled before it, not folded into the existing remove branch.
        if(perform.Art == Perform.E_Art.remove && perform.src == _rootTopic && sub.setTopic == _rootTopic) {
          HandleRootRemoved();
          return;
        }
        if(perform.src.parent != sub.setTopic) return;

        string parentVid = _rowProjector.TopicVid(sub.setTopic);
        bool parentExpanded = _expansion.IsExpanded(parentVid);

        if(perform.Art == Perform.E_Art.create) {
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
          if(parentExpanded) SendAdd(BuildRow(perform.src));
        } else if(perform.Art == Perform.E_Art.remove) {
          string vid = _rowProjector.TopicVid(perform.src);
          if(parentExpanded) _send(ViewProtocolSerializer.Del(vid));
          _targets.RemoveSubtree(vid);
          _expansion.RemoveWatchSubtree(vid, VidHelper.IsDescendant);
          // Skipped once the parent is itself disposed - removing a topic with
          // children fans out into one remove Perform per descendant (Repo.cs
          // TickStep1: `foreach(Topic tmp in c.src.all) ...`, self first, then
          // children), so a child's own removal handler would otherwise send a
          // pointless evnt.upd "refreshing" a parent row the client was just told
          // (via the parent's own remove event) to delete entirely.
          if(!sub.setTopic.disposed) SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
        } else if(perform.Art == Perform.E_Art.changedState || perform.Art == Perform.E_Art.changedField) {
          SendUpd(perform.src, null);
        } else if(perform.Art == Perform.E_Art.move) {
          string oldPath = perform.o as string;
          if(parentExpanded) {
            if(!string.IsNullOrEmpty(oldPath)) _send(ViewProtocolSerializer.Del(_viewName + "#" + oldPath));
            SendAdd(BuildRow(perform.src));
          }
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);

          // The callback above only fires for the NEW parent's subscription (see the
          // guard at the top: perform.src.parent now points at the new parent, so
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
        Log.Warning("TopicTreeController.OnTopicChanged - {0}", ex.Message);
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

    private void SendAdd(ViewRowDto row) {
      RememberTarget(row);
      Topic topic = Topic.root.Get(VidHelper.GetTopicPath(row.Vid), false);
      WatchCollapsedExpander(topic);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, row.Vid);
      dto["level"] = new JSL.Number(row.Level);
      if(row.Expander != 0) dto["expander"] = new JSL.Number(row.Expander);
      dto["icon"] = row.Icon ?? string.Empty;
      dto["name"] = row.Name ?? string.Empty;
      if(!string.IsNullOrEmpty(row.Editor) && row.Editor != "Default") dto["editor"] = row.Editor;
      if(!IsDefaultValue(row.Value)) dto["value"] = row.Value;
      if(row.Readonly) dto["readonly"] = row.Readonly;
      if(row.IsLogram) dto["isLogram"] = row.IsLogram;
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
      if(previous == null || !StringEquals(previous.Icon, row.Icon)) {
        dto["icon"] = row.Icon ?? string.Empty;
        changed = true;
      }
      if(previous == null || !StringEquals(previous.Editor, row.Editor)) {
        dto["editor"] = row.Editor ?? "Default";
        changed = true;
      }
      if(previous == null || !JsValueEquals(previous.Value, row.Value)) {
        dto["value"] = row.Value ?? JSC.JSValue.Null;
        changed = true;
      }
      if(previous == null || previous.Readonly != row.Readonly) {
        dto["readonly"] = row.Readonly;
        changed = true;
      }
      if(previous == null || previous.IsLogram != row.IsLogram) {
        dto["isLogram"] = row.IsLogram;
        changed = true;
      }
      if(previous == null || !StringEquals(previous.OptionsKey, row.OptionsKey)) {
        dto["options"] = row.Options ?? JSC.JSValue.Null;
        changed = true;
      }
      if(previous == null || !StringEquals(previous.EditorView, row.EditorView)) {
        dto["editorView"] = row.EditorView ?? string.Empty;
        changed = true;
      }

      RememberTarget(row);
      if(changed) _send(dto);
    }

    private static bool StringEquals(string left, string right) {
      return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    // A row's "value" counts as the client's default (view-store.js's normalizeRow()
    // falls back to '') when it's absent or an empty string - used by SendAdd to skip
    // the field on evnt.add rows instead of sending an empty value every time.
    private static bool IsDefaultValue(JSC.JSValue value) {
      if(value == null || !value.Defined || value.IsNull) return true;
      return value.ValueType == JSC.JSValueType.String && string.IsNullOrEmpty(value.Value as string);
    }

    private static bool JsValueEquals(JSC.JSValue left, JSC.JSValue right) {
      if(left == null || !left.Defined) return right == null || !right.Defined;
      if(right == null || !right.Defined) return false;
      if(object.ReferenceEquals(left, right)) return true;
      return left.Equals(right);
    }

    private void WatchCollapsedExpander(Topic topic) {
      if(topic == null) return;
      string vid = _rowProjector.TopicVid(topic);
      _expansion.Watch(vid, () => topic.Subscribe(SubRec.SubMask.Children, OnTopicChanged));
    }

    private void RememberTarget(ViewRowDto row) {
      _targets.Add(row.Vid, new ViewTarget() {
        TopicPath = VidHelper.GetTopicPath(row.Vid),
        FieldPath = string.Empty,
        TargetKind = ViewTargetKind.Topic,
        CachedRow = row,
      });
    }

    private ViewTarget CreateTarget(string vid) {
      if(VidHelper.GetView(vid) != _viewName) return null;
      string topicPath = VidHelper.GetTopicPath(vid);
      string fieldPath = VidHelper.GetFieldPath(vid);
      if(string.IsNullOrEmpty(topicPath)) return null;
      return new ViewTarget() {
        TopicPath = topicPath,
        FieldPath = fieldPath,
        TargetKind = ViewTargetKind.Topic,
      };
    }
  }
}
