///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // The expand/collapse/diff machinery shared by the Inspector's State and Manifest trees.
  // Both walk one topic's JSON value as a recursive key tree - State the topic's live state,
  // Manifest its manifest - and everything that does not depend on WHICH of the two lives
  // here: the expansion bookkeeping, the child reconciliation, the evnt.add/upd/del shaping
  // and the root subscription. Subclasses supply only what genuinely differs (see the
  // abstract members at the bottom).
  //
  // Extracted after the same defect had to be fixed twice in a row in both copies - the
  // _lastChildren subtree leak and the missing lock around _expanded/_lastChildren - and
  // after a third (the Readonly write check) went into the State copy only.
  internal abstract class JsonTreeControllerBase : IDisposable {
    protected readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    protected readonly ViewTargetRegistry _targets;
    protected readonly Topic _rootTopic;
    private readonly string _viewName;

    // No lock: both collections used to be touched from the WebSocket thread (Expand/
    // CollapseField) and from the engine tick thread (OnRootChanged), which is what the gate
    // here was for. Both now arrive through the session's queue and run on the engine thread -
    // and an expand/collapse is finally atomic against a tick, which under the old lock it
    // never was (the lock only kept each collection internally consistent).
    private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _lastChildren = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    // Lets the owning provider drop its entry when this controller tears itself down; without
    // it a dead controller stayed in the provider's map and later requests routed into it.
    private readonly Action<object> _onRootGone;
    private SubRec _sub;

    /// <param name="mask">Value|Field|Once for State, Field|Once for Manifest. Once is what
    /// makes a subscription observe the subscribed topic's OWN changedState/changedField
    /// rather than only its children's - confirmed by precedent in every plugin that watches
    /// one specific topic (MQTTPl's verboserSR, PersistentStorage*'s KEEP_FIELD watchers).</param>
    /// <remarks>Subscribing here means OnRootChanged can in principle reach an override before
    /// the subclass constructor body has run. Safe as written because neither subclass declares
    /// instance state of its own; anything added there must be initialised before this runs.</remarks>
    /// <param name="post">Queues work for the engine thread; null means "run it here, now".</param>
    protected JsonTreeControllerBase(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, string defaultViewName, SubRec.SubMask mask, Action<object> onRootGone = null, Action<string, Action> post = null, Func<Topic> prim = null) {
      _prim = prim;
      _post = post ?? ((what, work) => work());
      _send = send;
      _targets = targets;
      _rootTopic = rootTopic ?? Topic.root;
      _viewName = string.IsNullOrEmpty(viewName) ? defaultViewName : viewName;
      _onRootGone = onRootGone;
      _sub = _rootTopic.Subscribe(mask, OnRootChanged);
    }

    private readonly Func<Topic> _prim;

    /// <summary>This session's client topic, carried as Perform.Prim on everything it writes.</summary>
    /// <remarks>Read per write rather than cached: the topic is created on the engine thread
    /// after this controller exists, and renamed again when the reverse DNS lookup lands. Null
    /// when no session owns these writes, which is how every existing test constructs one.</remarks>
    protected Topic Prim {
      get { return _prim == null ? null : _prim(); }
    }

    internal string RootVid {
      get { return _viewName + "#" + _rootTopic.path; }
    }

    internal void SendRoot() {
      SendAdd(BuildRow(string.Empty));
    }

    internal ViewOpResult Expand(string vid, bool expand) {
      if(expand) ExpandField(vid);
      else CollapseField(vid);
      return ViewOpResult.Success();
    }

    internal void Close() {
      Dispose();
    }

    public virtual void Dispose() {
      SubRec sub = System.Threading.Interlocked.Exchange(ref _sub, null);
      if(sub != null) sub.Dispose();
      _expanded.Clear();
      _lastChildren.Clear();
    }

    #region expansion bookkeeping
    protected bool IsExpanded(string vid) {
      return _expanded.Contains(vid);
    }
    private void MarkExpanded(string vid) {
      _expanded.Add(vid);
    }
    private void UnmarkExpanded(string vid) {
      _expanded.Remove(vid);
    }
    private void ForgetExpandedSubtree(string vid) {
      _expanded.RemoveWhere(v => v == vid || VidHelper.IsDescendant(vid, v));
    }
    private string[] ExpandedSnapshot() {
      return _expanded.ToArray();
    }
    // The stored sets are never mutated after being stored (SendChildren builds a fresh one
    // and replaces), so handing the reference out is safe.
    private HashSet<string> ChildrenOf(string vid) {
      HashSet<string> keys;
      return _lastChildren.TryGetValue(vid, out keys) ? keys : null;
    }
    private void SetChildren(string vid, HashSet<string> keys) {
      if(keys != null && keys.Count > 0) _lastChildren[vid] = keys;
      else _lastChildren.Remove(vid);
    }
    // Drops the whole subtree, not just the node: collapsing a parent used to leave the
    // records of any expanded grandchild behind for the life of the session.
    private void ForgetChildrenSubtree(string vid) {
      _lastChildren.Remove(vid);
      foreach(string key in _lastChildren.Keys.Where(k => VidHelper.IsDescendant(vid, k)).ToArray()) _lastChildren.Remove(key);
    }
    #endregion expansion bookkeeping

    private void ExpandField(string vid) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      MarkExpanded(vid);
      SendUpd(fieldPath);
      SendChildren(fieldPath, vid, false);
    }

    private void CollapseField(string vid) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      RemoveChildrenOf(vid, fieldPath);
      UnmarkExpanded(vid);
      SendUpd(fieldPath);
    }

    private void RemoveChildrenOf(string vid, string fieldPath) {
      HashSet<string> previousKeys = ChildrenOf(vid);
      if(previousKeys == null) return;
      foreach(string key in previousKeys) {
        string childVid = RootVid + "#" + ChildFieldPath(fieldPath, key);
        _send(ViewProtocolSerializer.Del(childVid));
        _targets.RemoveSubtree(childVid);
        ForgetExpandedSubtree(childVid);
        ForgetChildrenSubtree(childVid);
      }
      ForgetChildrenSubtree(vid);
    }

    // Inserts (or, when diff=true, reconciles) the row set for fieldPath's own object keys.
    // diff=false is used right after Expand (the client has no rows for this subtree yet, so
    // everything is a fresh add); diff=true is used from OnRootChanged, where existing children
    // may just need an update, new keys need an add, and removed keys need a del - mirroring
    // InValue.UpdateData's full recursive re-walk on every change.
    private void SendChildren(string fieldPath, string vid, bool diff) {
      JSC.JSValue value = ResolveValueAt(fieldPath);
      HashSet<string> currentKeys = new HashSet<string>(StringComparer.Ordinal);
      if(JsonTreeRowHelpers.HasFields(value)) {
        foreach(var kv in value.OrderBy(z => z.Key, StringComparer.Ordinal)) {
          currentKeys.Add(kv.Key);
          string childFieldPath = ChildFieldPath(fieldPath, kv.Key);
          string childVid = RootVid + "#" + childFieldPath;
          if(diff && _targets.Get(childVid) != null) SendUpd(childFieldPath);
          else SendAdd(BuildRow(childFieldPath));
        }
      }

      if(diff) {
        HashSet<string> previousKeys = ChildrenOf(vid);
        if(previousKeys != null) {
          foreach(string staleKey in previousKeys) {
            if(currentKeys.Contains(staleKey)) continue;
            string staleVid = RootVid + "#" + ChildFieldPath(fieldPath, staleKey);
            _send(ViewProtocolSerializer.Del(staleVid));
            _targets.RemoveSubtree(staleVid);
            ForgetExpandedSubtree(staleVid);
            ForgetChildrenSubtree(staleVid);
          }
        }
      }

      SetChildren(vid, currentKeys);
    }

    private static string ChildFieldPath(string fieldPath, string key) {
      return string.IsNullOrEmpty(fieldPath) ? key : fieldPath + "." + key;
    }

    // Queued whole - see TopicTreeController.OnTopicChanged for why it cannot be split: the body
    // reads the value, reconciles the expanded/children bookkeeping and writes the socket.
    private void OnRootChanged(Perform perform, SubRec sub) {
      _post("callback " + RootVid, () => OnRootChangedCore(perform, sub));
    }

    private void OnRootChangedCore(Perform perform, SubRec sub) {
      try {
        if(perform == null) return;
        if(perform.Art == Perform.E_Art.remove) {
          HandleRootRemoved();
          return;
        }
        if(!IsRelevantChange(perform.Art)) return;
        SendUpd(string.Empty);
        foreach(string vid in ExpandedSnapshot().OrderBy(v => v, StringComparer.Ordinal)) {
          SendChildren(VidHelper.GetFieldPath(vid), vid, true);
        }
      }
      catch(Exception ex) {
        // Full stack: an unexpected fault inside a tick callback is exactly the case where
        // the message alone does not identify the frame that produced it.
        Log.Warning("{0}.OnRootChanged - {1}", GetType().Name, ex.ToString());
      }
    }

    // Tells the frontend this document's root is gone (evnt.del on RootVid - see app-shell.js
    // #onDocumentRootDeleted) and stops this controller, mirroring Dispose().
    private void HandleRootRemoved() {
      string vid = RootVid;
      _send(ViewProtocolSerializer.Del(vid));
      _targets.RemoveSubtree(vid);
      _targets.Remove(vid);
      Dispose();
      if(_onRootGone != null) _onRootGone(this);
    }

    protected void SendAdd(ViewRowDto row) {
      RememberTarget(row);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, row.Vid);
      dto["level"] = new JSL.Number(row.Level);
      if(row.Expander != 0) dto["expander"] = new JSL.Number(row.Expander);
      dto["icon"] = row.Icon ?? string.Empty;
      dto["name"] = row.Name ?? string.Empty;
      if(!string.IsNullOrEmpty(row.Editor) && row.Editor != "Default") dto["editor"] = row.Editor;
      if(!JsonTreeRowHelpers.IsDefaultValue(row.Value)) dto["value"] = row.Value;
      if(row.Readonly) dto["readonly"] = row.Readonly;
      if(row.Options != null) dto["options"] = row.Options;
      _send(dto);
    }

    private void SendUpd(string fieldPath) {
      ViewRowDto row = BuildRow(fieldPath);
      ViewRowDto previous = null;
      ViewTarget target = _targets.Get(row.Vid);
      if(target != null) previous = target.CachedRow;

      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, row.Vid);
      bool changed = false;

      if(previous == null || previous.Expander != row.Expander) {
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
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.OptionsKey, row.OptionsKey)) {
        dto["options"] = row.Options ?? JSC.JSValue.Null;
        changed = true;
      }

      RememberTarget(row);
      if(changed) _send(dto);
    }

    private void RememberTarget(ViewRowDto row) {
      _targets.Add(row.Vid, new ViewTarget() {
        TargetKind = TargetKind,
        CachedRow = row,
      });
    }

    #region supplied by the concrete tree
    protected abstract ViewTargetKind TargetKind { get; }

    /// <summary>The JSON value this tree walks: the topic's state, or its manifest.</summary>
    protected abstract JSC.JSValue ResolveValueAt(string fieldPath);

    /// <summary>Row shape for one node - the schema/editor/icon rules differ per tree.</summary>
    protected abstract ViewRowDto BuildRow(string fieldPath);

    /// <summary>Which Perform kinds this tree has to redraw for.</summary>
    protected abstract bool IsRelevantChange(Perform.E_Art art);
    #endregion supplied by the concrete tree
  }
}
