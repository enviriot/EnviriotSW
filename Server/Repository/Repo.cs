///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace X13.Repository {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 1)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Repository")]
  public class Repo : IPlugModul {
    internal static string configPath;
    /// <summary>How many phase lists there are. Derived, so it cannot drift from Phase.</summary>
    /// <remarks>It was a 6 with "see Phase" beside it, and that comment was the only thing tying
    /// the two together: add a phase, forget the constant, and the last one is never applied and
    /// never published. Silently. One reflection call at type initialisation buys that away, and
    /// nothing here needs a compile-time constant - every use is an array size or a loop bound.</remarks>
    private static readonly int PH_COUNT = Enum.GetValues(typeof(Phase)).Length;
    private const int SAVE_RETRY_SEC = 30;   // after an export that could not be written

    #region internal Members
    private readonly ConcurrentQueue<Cmd> _tcQueue;
    private readonly List<Cmd>[] _phases;              // what this tick was asked to do
    private readonly List<TopicEvent>[] _events;       // what came of it, in the same order
    private readonly Dictionary<Topic, int> _stateAt;  // topic -> its entry in the state phase
    private readonly Dictionary<FieldKey, int> _fieldAt;   // topic+field -> its entry in the manifest phase
    private volatile Action<TopicEvent>[] _subscribers;
    private int _busyFlag;
    private DateTime? _saveConfigT;
    private bool _loaded;

    /// <summary>Queues a change. It is applied by the next tick and not before.</summary>
    /// <remarks>There was a second way in: DoCmd(cmd, intern: true) ran the whole pipeline on the
    /// spot, so a change made from within a tick took effect inside it. Nothing ever passed true -
    /// the only call site that passed a variable was Topic.Resolve, and every caller of that passed
    /// false - and a whole cursor-fixup mechanism inside the queue existed to serve it.
    /// <para>It is not missed. Logram is the one component that computes chains of values, and it
    /// does its own layered propagation: a chain resolves inside a single Logram tick, and the
    /// topic is where the result is written, not what the result is computed through.</para>
    /// </remarks>
    internal void DoCmd(Cmd cmd) {
      _tcQueue.Enqueue(cmd);
    }

    /// <summary>Registers a repository-wide callback and hands back the way to stop it.</summary>
    /// <remarks>Returned rather than void, which is what this was: a plugin could start receiving
    /// every event in the tree and had no way to stop receiving them. PersistentStorage paid for
    /// that concretely - its Stop() disposes the AutoResetEvent that its own callback then went on
    /// to Set() on the next repository change.
    /// <para>The list is replaced rather than mutated, so the publish loop below reads a snapshot
    /// that cannot change under it. Writes happen three times at startup and three times at
    /// shutdown; the read runs on every published event.</para></remarks>
    internal IDisposable SubscribeAll(Action<TopicEvent> func) {
      if(func == null) {
        throw new ArgumentNullException("func");
      }
      var old = _subscribers;
      var next = new Action<TopicEvent>[old.Length + 1];
      Array.Copy(old, next, old.Length);
      next[old.Length] = func;
      _subscribers = next;
      return new AllSubRec(this, func);
    }
    private void UnsubscribeAll(Action<TopicEvent> func) {
      var old = _subscribers;
      int idx = Array.IndexOf(old, func);
      if(idx < 0) {
        return;
      }
      var next = new Action<TopicEvent>[old.Length - 1];
      Array.Copy(old, 0, next, 0, idx);
      Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
      _subscribers = next;
    }

    /// <summary>What SubscribeAll hands back. Disposing twice is a no-op, as is disposing late.</summary>
    private sealed class AllSubRec : IDisposable {
      private Repo _owner;
      private readonly Action<TopicEvent> _func;

      public AllSubRec(Repo owner, Action<TopicEvent> func) {
        _owner = owner;
        _func = func;
      }
      public void Dispose() {
        Repo owner = _owner;
        _owner = null;
        if(owner != null) {
          owner.UnsubscribeAll(_func);
        }
      }
    }

    /// <summary>Files one command under its phase, folding what can be folded.</summary>
    /// <remarks>Three commands do not simply queue. A removal fans out over the subtree, because
    /// every descendant goes with it and every subscriber to one of them has to hear so. A
    /// manifest write merges into whatever this tick is already building for that topic, and only
    /// the first of them keeps a command. A state write replaces an earlier write to the same
    /// topic in place, so a sensor reporting faster than the tick costs one event rather than
    /// hundreds, and the phase keeps the order topics were first touched in.
    /// <para>That fold used to be a linear scan of the whole pending queue, run for every command
    /// filed. At ten thousand changes in one tick it was fifty million comparisons, and it
    /// measured: 2.3 seconds inside a tick that lasts 15.6 milliseconds.</para></remarks>
    private void Dispatch(Cmd c) {
      CmdSubscribe sub = c as CmdSubscribe;
      if(sub != null) {
        Snapshot(sub);
        return;
      }
      if(c is CmdRemove) {
        // In name order, which costs nothing now that children are kept in it
        foreach(Topic tmp in new Topic.Bill(c.Target, true)) {
          _phases[(int)Phase.Remove].Add(new CmdRemove(tmp, c.Author));
        }
        return;
      }
      CmdField fld = c as CmdField;
      if(fld != null) {
        // Every write is filed, not just the first: the batch keeps the manifest single, the
        // commands keep their own paths. Two writes into the SAME field fold, like state does.
        fld.Batch = Topic.SetField(fld);
        List<Cmd> phase = _phases[(int)Phase.Field];
        FieldKey key = new FieldKey(c.Target, fld.Path);
        int at;
        if(_fieldAt.TryGetValue(key, out at)) {
          phase[at] = c;   // the later write wins, in the place the first one took
        } else {
          _fieldAt[key] = phase.Count;
          phase.Add(c);
        }
        return;
      }
      if(c is CmdState) {
        List<Cmd> phase = _phases[(int)Phase.State];
        int at;
        if(_stateAt.TryGetValue(c.Target, out at)) {
          phase[at] = c;   // the later write wins, in the place the first one took
        } else {
          _stateAt[c.Target] = phase.Count;
          phase.Add(c);
        }
        return;
      }
      _phases[(int)c.Phase].Add(c);
    }

    /// <summary>The state a new subscription is owed: one event per topic it reaches.</summary>
    /// <remarks>Spelled out here rather than by applying a command, because it is many events out
    /// of one command and they belong to the subscription phase - ahead of whatever else this tick
    /// is about to change, so a subscriber is never told of a change to a topic it has not been
    /// introduced to yet. The acknowledgement goes last of all, which is what makes it mean
    /// anything.</remarks>
    private void Snapshot(CmdSubscribe c) {
      SubRec sr = c.Sub;
      List<TopicEvent> evs = _events[(int)Phase.Sub];
      if((sr.mask & SubRec.SubMask.Once) == SubRec.SubMask.Once) {
        evs.Add(TopicEvent.Snapshot(c.Target, sr));
      }
      Topic.Bill b = null;
      if((sr.mask & SubRec.SubMask.Children) == SubRec.SubMask.Children) {
        b = new Topic.Bill(c.Target, false);
      }
      if((sr.mask & SubRec.SubMask.All) == SubRec.SubMask.All) {
        b = new Topic.Bill(c.Target, true);
      }
      if(b != null) {
        foreach(Topic tmp in b) {
          if((sr.mask & SubRec.SubMask.Value) == SubRec.SubMask.Value
            || (sr.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || string.IsNullOrEmpty(sr.prefix) || tmp.GetField(sr.prefix).Defined) {
            evs.Add(TopicEvent.Snapshot(tmp, sr));
          }
        }
      }
      _events[(int)Phase.Ack].Add(TopicEvent.Ready(c.Target, sr));
    }

    private void PublishSaveConfig(TopicEvent e) {
      if(e.Kind == EventKind.FieldChanged || e.Kind == EventKind.StateChanged || e.Kind == EventKind.Removed) {
        if(e.Source.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
          _saveConfigT = DateTime.Now.AddSeconds(5);
        }
      }
    }

    #endregion internal Members

    public Repo() {
      _tcQueue = new ConcurrentQueue<Cmd>();
      _phases = new List<Cmd>[PH_COUNT];
      _events = new List<TopicEvent>[PH_COUNT];
      for(int i = 0; i < PH_COUNT; i++) {
        _phases[i] = new List<Cmd>(64);
        _events[i] = new List<TopicEvent>(64);
      }
      _stateAt = new Dictionary<Topic, int>();
      _fieldAt = new Dictionary<FieldKey, int>();
      _subscribers = new Action<TopicEvent>[0];
      _saveConfigT = null;
    }

    #region IPlugModul Members

    public void Init() {
      Topic.Init(this);
      _busyFlag = 1;
      Xst.Import(configPath);   // does nothing when configPath is null or absent
      this.Tick();
      this.Tick();
      _loaded = true;   // last line on purpose: see Stop
    }

    public void Start() {
      SubscribeAll(PublishSaveConfig);
    }

    /// <summary>Applies one batch of changes and publishes what came of it.</summary>
    /// <remarks>Three walks over the same phases in the same order, which IS the order of the
    /// tick: structure, subscription snapshots, manifest, state, removals, acknowledgements.
    /// Applying the whole batch before publishing any of it is what lets a subscriber see a
    /// settled tree rather than one caught mid-change.
    /// <para>The body ends in a finally because the busy flag is what keeps the tick out of
    /// itself, and losing it is unrecoverable: it was returned to 1 by the last statement of the
    /// method, so any escaping exception left it captured at 2 and every later tick returned on
    /// the first line - silently. Program.PrThread catches and logs a plugin's Tick, so the
    /// process went on running, the websockets went on answering, and the tree never changed
    /// again. One ArgumentOutOfRangeException out of the publish walk was enough.</para>
    /// <para>Each step is additionally guarded per item: one unprocessable change must not cost
    /// the rest of the batch. The phases are cleared in the same finally, so a batch that failed
    /// half way through is not published a second time on the next tick.</para></remarks>
    public void Tick() {
      if(Interlocked.CompareExchange(ref _busyFlag, 2, 1) != 1) {
        return;
      }
      try {
        Cmd cmd;
        while(_tcQueue.TryDequeue(out cmd)) {
          if(cmd == null || cmd.Target == null) {
            continue;
          }
          try {
            Dispatch(cmd);
          }
          catch(Exception ex) {
            Failed("Dispatch", cmd, ex);
          }
        }

        for(int p = 0; p < PH_COUNT; p++) {
          List<Cmd> phase = _phases[p];
          List<TopicEvent> evs = _events[p];
          for(int i = 0; i < phase.Count; i++) {
            try {
              TopicEvent e = phase[i].Apply();
              if(e != null) {
                evs.Add(e);
              }
            }
            catch(Exception ex) {
              Failed("Apply", phase[i], ex);
            }
          }
        }

        for(int p = 0; p < PH_COUNT; p++) {
          List<TopicEvent> evs = _events[p];
          for(int i = 0; i < evs.Count; i++) {
            try {
              CCtor.Check(evs[i]);
            }
            catch(Exception ex) {
              Failed("CCtor", evs[i], ex);
            }
          }
        }

        for(int p = 0; p < PH_COUNT; p++) {
          List<TopicEvent> evs = _events[p];
          for(int i = 0; i < evs.Count; i++) {
            TopicEvent e = evs[i];
            try {
              Topic.Publish(e);
            }
            catch(Exception ex) {
              Failed("Publish", e, ex);
            }
            // One read of the field, then iterate that: a callback may unsubscribe - its own
            // registration or another's - and the loop must not be walking the list it changed.
            var subs = _subscribers;
            for(int k = subs.Length-1; k>=0; k--) {
              var func = subs[k];
              try {
                func.Invoke(e);
              }
              catch(Exception ex) {
                PluginFailed((func.Target != null ? func.Target.ToString() : func.Method.DeclaringType.Name) + "." + func.Method.Name, e, ex);
              }
            }
          }
        }
        if(_saveConfigT!=null && _saveConfigT<DateTime.Now) {
          _saveConfigT=null;
          try {
            Xst.Export(configPath, Topic.root, true);
          }
          catch(Exception ex) {
            // Asked again rather than dropped. Clearing the timer and giving up meant the change
            // that asked for the save was simply never written - not until the next configuration
            // change, or until Stop(). And the failure this was written for is transient by
            // nature: a file another process held for a moment, exporting cleanly again minutes
            // later. A configuration silently not saved is the sort of loss found after a power
            // cut and not before it.
            _saveConfigT = DateTime.Now.AddSeconds(SAVE_RETRY_SEC);
            Failed("Export", null, ex);
          }
        }
        _faults.Flush(DateTime.Now);
      }
      finally {
        for(int p = 0; p < PH_COUNT; p++) {
          _phases[p].Clear();
          _events[p].Clear();
        }
        _stateAt.Clear();
        _fieldAt.Clear();
        _busyFlag = 1;
      }
    }

    /// <summary>A fault in the repository's own work: the tick swallowed it and carried on.</summary>
    /// <remarks>Applying a change is the one thing here that nobody outside this assembly can
    /// cause, so a throw is a bug in the repository. The change may be half made and no event will
    /// be published for it - but the rest of the batch still goes through, because dropping it
    /// would lose changes that had nothing to do with the failure, and because the alternative
    /// once was to lose the repository altogether.</remarks>
    internal void Failed(string where, object subject, Exception ex) {
      _faults.Report(true, "Repo." + where, subject, ex);
    }

    /// <summary>A fault in somebody else's callback: a type constructor, a subscriber.</summary>
    /// <remarks>The tree is already consistent by the time anything is delivered; the only
    /// question a throw here answers is who does not get told. A warning, not an error, and the
    /// remaining subscribers still get their turn.</remarks>
    internal void PluginFailed(string who, object subject, Exception ex) {
      _faults.Report(false, who, subject, ex);
    }
    private readonly FaultThrottle _faults = new FaultThrottle();
    /// <summary>One topic's one field - what a manifest write is folded by within a tick.</summary>
    /// <remarks>Reference equality on the topic, ordinal on the path: two Topic instances are
    /// never equal to each other, and a field path is a name rather than text to be compared
    /// loosely.</remarks>
    private struct FieldKey : IEquatable<FieldKey> {
      private readonly Topic _topic;
      private readonly string _path;

      public FieldKey(Topic topic, string path) {
        _topic = topic;
        _path = path;
      }
      public bool Equals(FieldKey other) {
        return object.ReferenceEquals(_topic, other._topic) && string.Equals(_path, other._path, StringComparison.Ordinal);
      }
      public override bool Equals(object obj) {
        return obj is FieldKey && Equals((FieldKey)obj);
      }
      public override int GetHashCode() {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_topic) ^ (_path == null ? 0 : _path.GetHashCode());
      }
    }

    /// <summary>Writes the configuration back out - but never a tree that was not fully read in.</summary>
    /// <remarks>Import throws on a truncated or malformed server.xst, which is exactly what a
    /// power cut during the previous Export leaves behind. Startup then fails and the server is
    /// torn down - and this method, running as part of that teardown, would export whatever made
    /// it into the tree before the parser gave up, straight over the file that has the rest of it.
    /// A configuration one could still repair by hand becomes an empty one. Reproduced, not
    /// imagined: a deliberately truncated config came back as a 92-byte empty export.
    /// <para>Only Init sets the flag, and only as its last statement, so "loaded" means the whole
    /// of it - Topic.Init, the import and both ticks.</para></remarks>
    public void Stop() {
      if(!_loaded) {
        Log.Warning("Repository did not finish loading; {0} is left as it is", configPath);
        return;
      }
      Xst.Export(configPath, Topic.root, true);
    }

    /// <summary>Where the repository's own settings would live. Nothing is created until read.</summary>
    /// <remarks>Deliberately not touched by <see cref="enabled"/> below, unlike every other
    /// plugin: Topic.root does not exist until Init() runs Topic.Init(this), and enabled is
    /// asked first, so a topic-backed answer here would dereference null on the way up.</remarks>
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }
    private const string OWNER_PATH = "/$YS/Repository";
    private Topic _owner;

    /// <summary>Always on - the one plugin that does not read this from its Owner topic.</summary>
    /// <remarks>Every other plugin may be switched off from the tree; switching this one off would
    /// make InitPlugins skip the component that owns the tree, leaving the rest of the server with
    /// nothing to run against. A constant says that better than the ApplicationException the
    /// setter used to throw, which nothing could reach anyway.</remarks>
    public bool enabled { get { return true; } }
    #endregion IPlugModul Members
  }
}
