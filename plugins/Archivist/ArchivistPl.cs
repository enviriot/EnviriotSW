///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using X13.Repository;
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Archivist {
  /// <summary>Records the history of topics that ask for it, and answers range queries over it.</summary>
  /// <remarks>Split out of PersistentStorage: the two shared a plugin but almost no state - the
  /// archive only ever borrowed the value converters. Priority 3 puts it after the repository (1)
  /// and the state store (2), so the tree and the manifests are in place before the first sample.</remarks>
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 3)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Archivist")]
  internal class ArchivistPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/Archivist";
    private const string DEFAULT_DIR = "../data";

    private readonly string _dir;
    private readonly ConcurrentQueue<Perform> _q;
    private readonly AutoResetEvent _tick;
    private ArchStore _store;
    private Topic _owner;
    private Thread _tr;
    private bool _terminate;
    private int _rrIdx;
    private DateTime _nextHotRebuild;
    private DateTime _nextRawRebuild;

    public ArchivistPl() : this(DEFAULT_DIR) {
    }
    /// <summary>Directory is injectable so the store can be exercised outside a running server.</summary>
    internal ArchivistPl(string dir) {
      _dir = dir;
      _q = new ConcurrentQueue<Perform>();
      _tick = new AutoResetEvent(false);
    }

    #region IPlugModul Members
    public void Init() {
      _owner = Topic.root.Get(OWNER_PATH, true);
      _store = new ArchStore(Path.GetFullPath(_dir));
    }

    public void Start() {
      _terminate = false;
      _tr = new Thread(new ThreadStart(ThreadM)) {
        IsBackground = true,
        Name = "Archivist",
        Priority = ThreadPriority.BelowNormal
      };
      _nextHotRebuild = DateTime.UtcNow.AddHours(1);
      _nextRawRebuild = NextNightly(DateTime.UtcNow);
      _tr.Start();
      _tick.WaitOne();   // the store is open before the first sample can arrive
      SeedVerbose();
      Topic.Subscribe(SubFunc);
      // Bound here rather than in the constructor: MEF does not order construction, but it does
      // order Start by priority, so this reliably takes over from the state store.
      JsExtLib.AQuery = AQuery;
    }

    public void Tick() {
      if(!_q.IsEmpty) {
        _tick.Set();
      }
    }

    public void Stop() {
      _terminate = true;
      _tick.Set();
      if(_tr != null && !_tr.Join(5000)) {
        _tr.Abort();
      }
      var s = Interlocked.Exchange(ref _store, null);
      if(s != null) {
        s.Dispose();
      }
      _tick.Dispose();
    }

    public bool enabled {
      get {
        var en = Topic.root.Get(OWNER_PATH, true);
        // Deliberately a raw ValueType test, NOT AsBool/AsString: this decides whether the config
        // topic has to be CREATED and seeded. A reader with a default cannot tell "not set yet" from
        // "set to the default", so the topic would never be created.
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        Topic.root.Get(OWNER_PATH, true).SetState(value);
      }
    }
    /// <summary>Set /$YS/Archivist/verbose to have every query logged with its cost.</summary>
    /// <remarks>Read through a subscription rather than on each query: AQuery runs on pool threads,
    /// and walking the tree for a config flag on every request is exactly the sort of per-request
    /// work this rework exists to remove.
    /// <para>As&lt;bool&gt;() and not AsBool(false) - JS truthiness, the same choice the other plugins
    /// make for their verbose flags, so a flag set to 1 from a script is not silently ignored.</para></remarks>
    internal bool verbose;
    private SubRec _verboseSR;

    private void SeedVerbose() {
      var vT = _owner.Get("verbose", true, _owner);
      // Deliberately a raw ValueType test: this decides whether the topic has to be CREATED, and a
      // reader with a default cannot tell "not set yet" from "set to the default".
      if(vT.GetState().ValueType != JSC.JSValueType.Boolean) {
        vT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        vT.SetState(false);
      }
      _verboseSR = vT.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value,
        (p, s) => verbose = _verboseSR.setTopic != null && _verboseSR.setTopic.GetState().As<bool>());
    }

    #endregion IPlugModul Members

    /// <summary>Engine-thread filter: only the changes this plugin can ever store reach the queue.</summary>
    /// <remarks>PersistentStorage decided this on its worker thread, but it had to visit every topic
    /// anyway to save state. Here the test is the whole job, so it belongs where it keeps the queue
    /// short. Arch.enable is read with As&lt;bool&gt;() - JS truthiness - on purpose, exactly as the
    /// old code did: the field is user-editable, so enable set to 1 or to a non-empty string still
    /// means yes. AsBool is strict and would silently turn those off.</remarks>
    private void SubFunc(Perform p) {
      if(p.Art != Perform.E_Art.changedState || p.src == null || p.Prim == _owner) {
        return;
      }
      if(!p.src.GetField("Arch.enable").As<bool>()) {
        return;
      }
      _q.Enqueue(p);
    }

    private void ThreadM() {
      try {
        _store.Open();
      }
      catch(Exception ex) {
        Log.Error("Archivist.Open({0}) - {1}", _dir, ex);
      }
      _tick.Set();
      do {
        if(_tick.WaitOne(15)) {
          while(_q.TryDequeue(out Perform p)) {
            try {
              StoreOne(p);
            }
            catch(Exception ex) {
              Log.Warning("Archivist({0}) - {1}", p == null ? "null" : p.ToString(), ex.Message);
            }
          }
        } else {
          IdleTask();
        }
      } while(!_terminate);
      var s = _store;
      if(s != null) {
        s.Close();
      }
    }

    private void StoreOne(Perform p) {
      var st = _store;
      if(st == null || !st.IsOpen) {
        return;
      }
      double v = p.src.GetState().AsDouble(double.NaN);
      if(double.IsNaN(v) || double.IsInfinity(v)) {
        return;                                     // nothing numeric to archive
      }
      st.Append(st.Resolve(p.src), v);
    }

    /// <summary>One unit of background work per idle pass, round-robin over the topics.</summary>
    /// <remarks>The pass fires whenever the queue is empty - up to sixty times a second - so the
    /// unit has to be small. One folded hour is that unit.</remarks>
    private void IdleTask() {
      var st = _store;
      if(st == null || !st.IsOpen) {
        return;
      }
      var all = st.Topics;
      if(all.Length == 0) {
        return;
      }
      if(_rrIdx >= all.Length) {
        _rrIdx = 0;
        return;
      }
      var at = all[_rrIdx++];
      try {
        // Folding first: a bucket has to exist before the raw underneath it may be swept away.
        if(!ArchRollup.FoldHour(st, at)) {
          ArchRetention.Step(st, at);
        }
      }
      catch(Exception ex) {
        Log.Warning("Archivist.Maintain({0}) - {1}", at.Path, ex.Message);
      }
      Reclaim(st);
    }

    /// <summary>Returns the space deletions leave behind, on a schedule per file.</summary>
    /// <remarks>The ring-buffer file is rewritten hourly because its whole content turns over in
    /// minutes; the history file daily, because there the churn is retention rather than lifecycle.
    /// Both give up immediately if a query holds the gate - the old code rebuilt a 909 MB file while
    /// pool threads were reading it.</remarks>
    /// <summary>Bytes one stored sample occupies, measured twice on the live archive.</summary>
    /// <remarks>415 793 152 bytes over 3 233 472 rows is 128.6, and a synthetic run of the same
    /// document shape gave the same figure. It is used only to estimate what the file WOULD weigh
    /// with nothing dead in it, so a few bytes either way change nothing.</remarks>
    internal const int DOC_BYTES = 129;
    /// <summary>How much dead weight is tolerated before a rebuild is worth its cost.</summary>
    internal const double SLACK = 1.5;
    /// <summary>Below this a file has nothing worth reclaiming, whatever the ratio says.</summary>
    internal const long MIN_REBUILD_BYTES = 4 * 1024 * 1024;

    /// <summary>Would rebuilding this file actually give anything back?</summary>
    /// <remarks>LiteDB keeps a free-page list and reuses freed pages on the next insert, so a store
    /// whose retention removes about as much as arrives settles at a size and stays there. Rebuilding
    /// it then returns nothing and merely rewrites the whole file - twenty-four times a day for the
    /// ring buffer under the old schedule. On the SD card of a Raspberry Pi that is pure wear.
    /// <para>What a rebuild is actually for is a BULK deletion: the migration lands nineteen months
    /// of history and retention then removes everything past each topic's keep, leaving a 416 MB
    /// file holding a fraction of that. Comparing the file against what its live rows should weigh
    /// distinguishes the two cases without asking the engine anything.</para></remarks>
    internal static bool WorthRebuilding(long fileBytes, long rows) {
      return fileBytes >= MIN_REBUILD_BYTES && fileBytes > rows * DOC_BYTES * SLACK;
    }

    /// <summary>The next 03:45 local, in UTC.</summary>
    /// <remarks>Anchored to the clock rather than to "a day after startup", which put a 416 MB
    /// rewrite at whatever hour the server happened to be started - as likely the middle of the
    /// evening as the middle of the night.</remarks>
    internal static DateTime NextNightly(DateTime nowUtc) {
      DateTime local = nowUtc.ToLocalTime();
      DateTime at = local.Date.AddHours(3.75);
      if(at <= local) {
        at = at.AddDays(1);
      }
      return at.ToUniversalTime();
    }

    private void Reclaim(ArchStore st) {
      DateTime now = DateTime.UtcNow;
      // The timers say when to LOOK; the estimate says whether to act. Checking costs a file stat
      // and a count out of the collection header, so it stays on the schedule rather than running
      // on every idle pass.
      if(now >= _nextHotRebuild) {
        if(!WorthRebuilding(st.FileBytes(true), st.RawCount(true)) || st.TryRebuild(true)) {
          _nextHotRebuild = now.AddHours(1);
        }
      } else if(now >= _nextRawRebuild) {
        if(!WorthRebuilding(st.FileBytes(false), st.RawCount(false)) || st.TryRebuild(false)) {
          _nextRawRebuild = NextNightly(now);
        }
      }
    }

    #region AQuery

    /// <summary>Answers a range request. Runs on a pool thread, via JsExtLib.</summary>
    /// <remarks>With verbose set, every request is logged with what it asked for, which rung of the
    /// granularity ladder answered, how many rows that cost and how long it took. Off by default and
    /// not merely for tidiness: graph.js issues a request every 50 ms while a chart is being panned,
    /// multiplied by the number of charts on the page, so this is a diagnostic to switch on for a
    /// while and switch off again, not a running commentary.</remarks>
    private JSL.Array AQuery(string[] topics, DateTime begin, int count, DateTime end) {
      var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
      try {
        ArchQueryStat stat;
        var rez = ArchQuery.Run(_store, topics, begin, count, end, out stat);
        if(sw != null) {
          sw.Stop();
          Log.Info("{0}", Describe(topics, begin, count, end, stat, (int)rez.length, sw.Elapsed.TotalMilliseconds));
        }
        return rez;
      }
      catch(Exception ex) {
        Log.Warning("Archivist.AQuery([{0}], {1:yyMMdd'T'HHmmss}, {2}) - {3}",
          string.Join(", ", topics ?? new string[0]), begin, count, ex.Message);
        return new JSL.Array();
      }
    }

    /// <summary>One log line describing a query and what it cost.</summary>
    /// <remarks>Separate from the call site so the format string can be exercised by a test. A
    /// composite format is checked only when it runs, and this one runs only with verbose set - the
    /// one setting nobody has on while the tests are green.
    /// <para>The window is printed as the caller gave it, in local time: that is what the chart
    /// asked for, and it is what has to be matched against a screenshot. The topic list comes last
    /// because it is the part that runs long.</para></remarks>
    internal static string Describe(string[] topics, DateTime begin, int count, DateTime end,
                                    ArchQueryStat stat, int points, double ms) {
      var names = topics ?? new string[0];
      return string.Format(
        "Archivist.AQuery {0} topic(s) [{1:yyMMdd'T'HHmmss} .. {2:yyMMdd'T'HHmmss}] count={3}"
        + " -> {4}, {5:n0} row(s), {6:n0} point(s), {7:n1} ms  [{8}]",
        names.Length, begin, end == DateTime.MinValue ? begin : end, count,
        stat.Level, stat.Rows, points, ms, string.Join(", ", names));
    }

    #endregion AQuery
  }
}
