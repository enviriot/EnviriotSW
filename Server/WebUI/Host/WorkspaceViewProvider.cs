///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal sealed class WorkspaceViewProvider : ViewProviderBase {
    private const string ViewName = "workspace";
    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly WorkspaceExpansionTracker _expansion;
    private readonly WorkspaceRowProjector _rowProjector;

    public WorkspaceViewProvider(Action<JSC.JSObject> send, ViewTargetRegistry targets) {
      _send = send;
      _targets = targets;
      _expansion = new WorkspaceExpansionTracker();
      _rowProjector = new WorkspaceRowProjector(_expansion.IsExpanded);
    }

    public void SendRoot() {
      SendAdd(_rowProjector.BuildRootRow());
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult TryCreateTarget(string vid) {
      ViewTarget target = _targets.GetOrCreate(vid, CreateTarget);
      return target == null
        ? ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"))
        : ViewOpResult.Success();
    }

    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;

      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }

      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }

      items = WorkspaceMenuBuilder.Build(topic);
      return ViewOpResult.Success();
    }

    public override ViewOpResult Expand(string vid, bool expand) {
      Topic topic = Topic.root.Get(VidHelper.GetTopicPath(vid), false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + VidHelper.GetTopicPath(vid));
      }

      if(expand) ExpandTopic(topic);
      else CollapseTopic(topic);
      return ViewOpResult.Success();
    }

    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
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

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
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


    public override ViewOpResult Open(string vid, string view) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      string topicPath = VidHelper.GetTopicPath(vid);
      if(topicPath != "/") {
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

    public override void Dispose() {
      _expansion.Clear();
    }

    private void ExpandTopic(Topic topic) {
      string vid = WorkspaceRowProjector.TopicVid(topic);
      _expansion.Expand(vid, () => topic.Subscribe(SubRec.SubMask.Children | SubRec.SubMask.Value | SubRec.SubMask.Field, OnTopicChanged));

      SendUpd(topic, topic.HasChildren() ? 2 : 0);
      foreach(Topic child in topic.children) {
        SendAdd(_rowProjector.BuildTopicRow(child));
      }
    }

    private void CollapseTopic(Topic topic) {
      string vid = WorkspaceRowProjector.TopicVid(topic);
      _expansion.ExitExpanded(vid);
      WatchCollapsedExpander(topic);

      SendUpd(topic, topic.HasChildren() ? 1 : 0);
      foreach(Topic child in topic.children) {
        _send(ViewProtocolSerializer.Del(WorkspaceRowProjector.TopicVid(child)));
        _targets.RemoveSubtree(WorkspaceRowProjector.TopicVid(child));
        _expansion.RemoveWatchSubtree(WorkspaceRowProjector.TopicVid(child), VidHelper.IsDescendant);
      }

      _expansion.RemoveExpandedDescendants(vid, VidHelper.IsDescendant);
    }

    private void OnTopicChanged(Perform perform, SubRec sub) {
      try {
        if(perform == null || perform.src == null || sub == null || perform.src.parent != sub.setTopic) return;

        string parentVid = WorkspaceRowProjector.TopicVid(sub.setTopic);
        bool parentExpanded = _expansion.IsExpanded(parentVid);

        if(perform.Art == Perform.E_Art.create) {
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
          if(parentExpanded) SendAdd(_rowProjector.BuildTopicRow(perform.src));
        } else if(perform.Art == Perform.E_Art.remove) {
          string vid = WorkspaceRowProjector.TopicVid(perform.src);
          if(parentExpanded) _send(ViewProtocolSerializer.Del(vid));
          _targets.RemoveSubtree(vid);
          _expansion.RemoveWatchSubtree(vid, VidHelper.IsDescendant);
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
        } else if(perform.Art == Perform.E_Art.changedState || perform.Art == Perform.E_Art.changedField) {
          SendUpd(perform.src, null);
        } else if(perform.Art == Perform.E_Art.move) {
          string oldPath = perform.o as string;
          if(parentExpanded) {
            if(!string.IsNullOrEmpty(oldPath)) _send(ViewProtocolSerializer.Del(ViewName + "#" + oldPath));
            SendAdd(_rowProjector.BuildTopicRow(perform.src));
          }
          SendUpd(sub.setTopic, sub.setTopic.HasChildren() ? (parentExpanded ? 2 : 1) : 0);
        }
      }
      catch(Exception ex) {
        Log.Warning("WorkspaceViewProvider.OnTopicChanged - {0}", ex.Message);
      }
    }

    private void SendAdd(ViewRowDto row) {
      RememberTarget(row);
      Topic topic = Topic.root.Get(VidHelper.GetTopicPath(row.Vid), false);
      WatchCollapsedExpander(topic);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, row.Vid);
      dto["level"] = new JSL.Number(row.Level);
      dto["expander"] = new JSL.Number(row.Expander);
      dto["icon"] = row.Icon ?? string.Empty;
      dto["name"] = row.Name ?? string.Empty;
      dto["editor"] = row.Editor ?? "Default";
      dto["value"] = row.Value ?? JSC.JSValue.Null;
      dto["readonly"] = row.Readonly;
      if(row.Options != null) dto["options"] = row.Options;
      _send(dto);
    }

    private void SendUpd(Topic topic, int? expander) {
      ViewRowDto row = topic == Topic.root ? _rowProjector.BuildRootRow() : _rowProjector.BuildTopicRow(topic);
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
      if(previous == null || !StringEquals(previous.OptionsKey, row.OptionsKey)) {
        dto["options"] = row.Options ?? JSC.JSValue.Null;
        changed = true;
      }

      RememberTarget(row);
      if(changed) _send(dto);
    }

    private static bool StringEquals(string left, string right) {
      return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool JsValueEquals(JSC.JSValue left, JSC.JSValue right) {
      if(left == null || !left.Defined) return right == null || !right.Defined;
      if(right == null || !right.Defined) return false;
      if(object.ReferenceEquals(left, right)) return true;
      return left.Equals(right);
    }

    private void WatchCollapsedExpander(Topic topic) {
      if(topic == null) return;
      string vid = WorkspaceRowProjector.TopicVid(topic);
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

    private static ViewTarget CreateTarget(string vid) {
      if(VidHelper.GetView(vid) != ViewName) return null;
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
