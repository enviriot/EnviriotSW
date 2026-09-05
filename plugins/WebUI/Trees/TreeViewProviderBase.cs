///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Routing shell shared by the three Inspector view providers. Each owns a dictionary of
  // per-document controllers created lazily on first expand and disposed on Close(vid); the
  // request plumbing around that - CanHandle, the five forwarding entry points, the owner
  // lookup and the teardown - is identical in all three, so it lives here once.
  //
  // Extracted after the same duplication cost showed up twice: State and Manifest were
  // character-identical apart from the controller type, and the "controller disposed itself
  // but stays in _roots" gap below existed in all three copies.
  internal abstract class TreeViewProviderBase<TController> : ViewProviderBase where TController : class, IDisposable {
    protected readonly Action<JSC.JSObject> _send;
    protected readonly ViewTargetRegistry _targets;
    // Null until this provider's subclass is converted - controllers treat null as "inline".
    protected readonly Action<string, Action> _post;
    // No lock: every entry point - the five request forwarders and ForgetRoot - now arrives
    // through the session's queue and runs on the engine thread.
    // Keyed by the document's root Topic, not by the vid it was opened with. A vid is a path,
    // and Topic.Move rewrites Topic._path in place (Topic.UpdatePath), so an opened-with key
    // goes stale the moment the root is renamed while the Topic reference stays valid. No id of
    // its own is needed: the reference is already stable, costs no lookup and cannot
    // desynchronise - same idiom as LogramPl._items and PersistentStorage's Dictionary<Topic,..>.
    // vid remains what it was designed to be: the identifier on the wire.
    private readonly Dictionary<Topic, TController> _roots = new Dictionary<Topic, TController>();

    /// <param name="post">Queues work for the engine thread; handed on to the controllers this
    /// provider creates. Omitted means "run it here, now", so a subclass that has not been
    /// converted yet keeps today's synchronous behaviour.</param>
    /// <param name="prim">This session's client topic, handed on to the controllers this
    /// provider creates so their writes are attributable. Null means writes carry no prim.</param>
    protected TreeViewProviderBase(Action<JSC.JSObject> send, ViewTargetRegistry targets, Action<string, Action> post = null, Func<Topic> prim = null) {
      _send = send;
      _targets = targets;
      _post = post;
      _prim = prim;
    }

    protected readonly Func<Topic> _prim;

    protected abstract string ViewName { get; }
    protected abstract TController CreateController(Topic rootTopic);
    protected abstract void SendControllerRoot(TController controller);
    /// <summary>The controller's CURRENT root vid - recomputed from its root topic's path.</summary>
    /// <remarks>Teardown needs a vid to clear the target registry with, and it has to be the
    /// live one: the rows whose targets are registered were last sent under the root's current
    /// path, which a rename changes.</remarks>
    protected abstract string RootVidOf(TController controller);
    protected abstract ViewOpResult ExpandCore(TController controller, string vid, bool expand);
    protected abstract ViewOpResult CommitCore(TController controller, string vid, JSC.JSValue value);
    protected abstract ViewOpResult BuildMenuCore(TController controller, string vid, out List<MenuItemDto> items);
    protected abstract ViewOpResult ExecuteRpcCore(TController controller, string vid, string cmd, JSC.JSValue args);

    /// <summary>Whether a request naming <paramref name="topic"/> belongs to the document rooted
    /// at <paramref name="root"/>.</summary>
    /// <remarks>State and Manifest own exactly one topic: every vid such a controller owns names
    /// that same topic and differs only in field path. Comparing topics rather than paths also
    /// retires the whole textual-prefix hazard the vid form had - root "/Test/X" versus a request
    /// for the real child topic "/Test/X/Y" is now simply two different references, and cannot be
    /// confused the way a string prefix once made a commit land on the wrong topic's manifest.
    /// The Children tree is a real topic graph, so it overrides this with a descendant test.</remarks>
    protected virtual bool Owns(Topic root, Topic topic) {
      return ReferenceEquals(root, topic);
    }

    /// <summary>The topic a request's vid names, or null when it names nothing that exists.</summary>
    protected static Topic ResolveTopic(string vid) {
      if(string.IsNullOrEmpty(vid)) return null;
      string path = VidHelper.GetTopicPath(vid);
      return string.IsNullOrEmpty(path) ? null : Topic.root.Get(path, false);
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult Expand(string vid, bool expand) {
      TController controller = GetOrCreateOwner(vid);
      if(controller == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + VidHelper.GetTopicPath(vid));
      }
      return ExpandCore(controller, vid, expand);
    }

    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
      TController controller = FindOwner(vid);
      return controller == null ? NotFound(vid) : CommitCore(controller, vid, value);
    }

    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      TController controller = FindOwner(vid);
      return controller == null ? NotFound(vid) : BuildMenuCore(controller, vid, out items);
    }

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      TController controller = FindOwner(vid);
      return controller == null ? NotFound(vid) : ExecuteRpcCore(controller, vid, cmd, args);
    }

    public override ViewOpResult Close(string vid) {
      DropOwner(vid, dispose: true);
      return ViewOpResult.Success();
    }

    public override void Dispose() {
      List<TController> doomed = new List<TController>(_roots.Values);
      _roots.Clear();
      foreach(TController controller in doomed) controller.Dispose();
    }

    /// <summary>Forgets a root whose controller has already torn itself down.</summary>
    /// <remarks>A controller that loses its own topic disposes itself from the engine tick
    /// thread and cannot reach this dictionary, which used to leave a dead entry behind for
    /// later requests to be routed into. Controllers call this instead.
    /// <para>Matched on the controller itself rather than looked up by its root topic: the entry
    /// must be dropped only if it is still THIS controller's, or a late callback from a
    /// torn-down controller would evict the live replacement a concurrent request stored for the
    /// same root. Same reasoning, and the same fix, as LogramViewProvider.ForgetRoot.</para>
    /// <para>Typed as object because TController is the concrete leaf type while the callback
    /// is handed to controllers that only know their own base - Action&lt;TController&gt; is not
    /// assignable there. The reference comparison below needs no more than object.</para></remarks>
    internal void ForgetRoot(object controller) {
      TController owner = null;
      Topic ownerKey = null;
      foreach(KeyValuePair<Topic, TController> kv in _roots) {
        if(ReferenceEquals(kv.Value, controller)) { ownerKey = kv.Key; owner = kv.Value; break; }
      }
      if(ownerKey == null) return;
      _roots.Remove(ownerKey);
      ClearTargets(owner);
    }

    private void DropOwner(string vid, bool dispose) {
      Topic topic = ResolveTopic(vid);
      Topic ownerKey = FindOwnerKeyLocked(topic);
      if(ownerKey == null) return;
      TController controller = _roots[ownerKey];
      _roots.Remove(ownerKey);
      // The registry is cleared BEFORE Dispose, while the controller can still report its live
      // root vid.
      ClearTargets(controller);
      if(dispose) controller.Dispose();
    }

    private void ClearTargets(TController controller) {
      if(controller == null) return;
      string rootVid = RootVidOf(controller);
      if(string.IsNullOrEmpty(rootVid)) return;
      _targets.RemoveSubtree(rootVid);
      _targets.Remove(rootVid);
    }

    private TController GetOrCreateOwner(string vid) {
      // The first request for a not-yet-seen root can only be a request against the root vid
      // itself - the frontend has no way to reference a descendant vid before the root row has
      // been sent at least once - so the topic this vid names is that root.
      Topic topic = ResolveTopic(vid);
      if(topic == null) return null;

      TController existing = FindOwnerLocked(topic);
      if(existing != null) return existing;

      TController created = CreateController(topic);
      _roots[topic] = created;
      SendControllerRoot(created);
      return created;
    }

    /// <summary>The controller owning this vid, or null - for subclasses adding their own entry points.</summary>
    protected TController FindOwnerFor(string vid) {
      return FindOwner(vid);
    }

    private TController FindOwner(string vid) {
      return FindOwnerLocked(ResolveTopic(vid));
    }

    private TController FindOwnerLocked(Topic topic) {
      Topic key = FindOwnerKeyLocked(topic);
      return key == null ? null : _roots[key];
    }

    private Topic FindOwnerKeyLocked(Topic topic) {
      if(topic == null) return null;
      // Exact hit first - the only case State and Manifest have, and a dictionary lookup rather
      // than a scan. Ownership beyond that (the Children tree's subtree rule) needs the scan.
      if(_roots.ContainsKey(topic)) return topic;
      foreach(Topic key in _roots.Keys) {
        if(Owns(key, topic)) return key;
      }
      return null;
    }

    private static ViewOpResult NotFound(string vid) {
      return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
    }
  }
}
