///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;
using X13.Repository;
using NiL.JS.Extensions;

namespace X13.Archivist {

  /// <summary>What the store knows about one archived topic.</summary>
  internal sealed class ArchTopic {
    internal int Id;
    internal string Path;
    /// <summary>Rollup cursor: every hour before this one has already been folded into roll_h.</summary>
    internal DateTime Rt;
    /// <summary>Retention cursor: everything before this instant has already been swept.</summary>
    /// <remarks>A cursor rather than a plain "delete everything older than keep" so that the same
    /// already-clean stretch is not walked again on the next pass. It no longer bounds a scan - the
    /// composite key makes the delete an exact range - but it still bounds how much one pass does.</remarks>
    internal DateTime Pt;
    internal double Keep;
    /// <summary>True while Keep is small enough that this is a ring buffer rather than history.</summary>
    internal bool Hot;
    /// <summary>The instant the previous sample of this topic was offered at, and the key it got.</summary>
    /// <remarks>Two fields because they differ exactly when a run of samples shares one instant: the
    /// second and later members of the run borrow the following tick, and the run is recognised by
    /// the offered instant, not by the borrowed one. Per topic rather than per store, because the
    /// key carries the topic and two topics reporting together no longer contend at all - which is
    /// what let the old sequence counter go.</remarks>
    internal long LastRaw;
    internal long LastTicks;
    /// <summary>The live topic, or null when it has been removed but its rows have not been swept yet.</summary>
    internal Topic T;
  }

  /// <summary>The archive on disk: a topic registry plus the sample files.</summary>
  /// <remarks>Three files, all directly in the data directory beside persist.ldb.
  /// <para>arch_meta.ldb holds everything that must outlive a sample file - the id/path registry and
  /// (from the rollup phase) the folded buckets.</para>
  /// <para>arch_raw.ldb holds the samples and carries NO secondary index at all: the primary key is
  /// the topic and the instant together, so both a whole-topic range and a per-topic time range are
  /// primary key ranges. The old archive kept the topic path as a string in every document and
  /// indexed it - that index took eleven hours to build over 3.2 M rows and was most of why the
  /// file was twice the size of the Firebird one.</para>
  /// <para>Topic first, then time, because that is the order the background work reads in. Folding,
  /// seeding and retention all ask for one topic over one interval, which under a time-only key
  /// meant reading every other topic's rows in that interval and discarding them - measured at 85 ms
  /// against 1 ms for the same 261 rows. What it costs is the free cross-topic ordering a time-only
  /// key gave: a chart over several topics merges one ordered stream per topic. Measured, that came
  /// out faster too, 50 ms against 145.</para>
  /// <para>Time lives in the key rather than in a field because LiteDB puts DateTime through local
  /// time end to end. In the hour the clock repeats every autumn, two instants an hour apart read
  /// back identical and a window over them comes back empty - measured, not theorised. Raw bytes
  /// have no such notion.</para>
  /// <para>arch_hot.ldb holds the ring-buffer topics. A Logram Average block sets Arch.keep to its
  /// own interval - sixty seconds by default - and then inserts and deletes continuously. In one
  /// shared file that churn accumulates dead space that only Rebuild returns; on its own it stays
  /// small enough to rebuild in milliseconds. It is a file and not memory because the samples have
  /// to survive a restart.</para></remarks>
  internal sealed class ArchStore : IDisposable {
    /// <summary>Keep below this many days means ring buffer, not history.</summary>
    /// <remarks>Same threshold the old OptimizeArch used to choose hard deletion over thinning.</remarks>
    internal const double HOT_KEEP_DAYS = 0.1;
    private const string META_FILE = "arch_meta.ldb";
    private const string RAW_FILE = "arch_raw.ldb";
    private const string HOT_FILE = "arch_hot.ldb";
    /// <summary>How far back Seed will look for a carry-in value before giving up.</summary>
    /// <remarks>Reached only for the stretch no bucket covers yet; a topic silent for longer starts
    /// its first bucket empty, which the accumulator already handles.</remarks>
    private static readonly TimeSpan SEED_LOOKBACK = TimeSpan.FromDays(2);

    private readonly string _dir;
    private readonly ReaderWriterLockSlim _gate;
    private readonly Dictionary<string, ArchTopic> _byPath;
    private readonly Dictionary<int, ArchTopic> _byId;

    private LiteDatabase _meta;
    private LiteDatabase _raw;
    private LiteDatabase _hot;
    private ILiteCollection<BsonDocument> _topics;
    private ILiteCollection<BsonDocument> _rawA;
    private ILiteCollection<BsonDocument> _hotA;
    private ILiteCollection<BsonDocument> _roll5;
    private ILiteCollection<BsonDocument> _rollH;
    private ILiteCollection<BsonDocument> _rollD;
    private int _nextId;

    /// <summary>Rows per bulk insert while batching. Measured: one at a time costs 1.35 ms a row,
    /// an explicit transaction per 256 brings that to 0.156, and InsertBulk per 256 to 0.046.
    /// Larger blocks do not help - 5000 measured slightly worse than 256.</summary>
    private const int BATCH_ROWS = 256;
    private bool _batch;
    private List<BsonDocument> _pendRaw;
    private List<BsonDocument> _pendHot;
    private int _metaPend;

    internal ArchStore(string dir) {
      _dir = dir;
      _gate = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
      _byPath = new Dictionary<string, ArchTopic>();
      _byId = new Dictionary<int, ArchTopic>();
    }

    internal bool IsOpen { get { return _meta != null; } }

    #region open / close

    internal void Open() {
      if(!Directory.Exists(_dir)) {
        Directory.CreateDirectory(_dir);
      }
      _meta = new LiteDatabase(new ConnectionString { Filename = Path.Combine(_dir, META_FILE), Upgrade = true });
      _raw = new LiteDatabase(new ConnectionString { Filename = Path.Combine(_dir, RAW_FILE), Upgrade = true }) { CheckpointSize = 100 };
      _hot = new LiteDatabase(new ConnectionString { Filename = Path.Combine(_dir, HOT_FILE), Upgrade = true }) { CheckpointSize = 100 };

      _topics = _meta.GetCollection<BsonDocument>("topics");
      _topics.EnsureIndex("p", true);
      // No secondary index at all: the primary key already orders by topic and then by time, which
      // is every order the archive reads in. That removes half the index writes per sample, 54
      // bytes per document for the index, and the topic field the old layout needed beside it.
      _rawA = _raw.GetCollection<BsonDocument>("a");
      _hotA = _hot.GetCollection<BsonDocument>("a");
      // No secondary index here either. The composite _id already orders by topic and then by
      // bucket, which is every order the rollups are read in - a cross-topic chart merges one
      // ordered range per topic rather than scanning a shared time index and discarding what
      // belongs to other topics. Dropped rather than merely unused: LiteDB keeps maintaining an
      // index that exists, so leaving it would cost every write for nothing.
      // Rollups live in the meta file because they must outlive any sweep of the sample files.
      _roll5 = _meta.GetCollection<BsonDocument>("roll_5m");
      _rollH = _meta.GetCollection<BsonDocument>("roll_h");
      _rollD = _meta.GetCollection<BsonDocument>("roll_d");
      foreach(var c in new[] { _roll5, _rollH, _rollD }) {
        try {
          c.DropIndex("k");
        }
        catch(LiteException) {                 // never had one, which is the normal case from now on
        }
      }

      LoadTopics();
    }

    /// <summary>Reads the registry up front, the way the Firebird backend already did.</summary>
    /// <remarks>The old LiteDB path filled its in-memory list lazily from the write path, so after
    /// a restart a topic that was archived but silent got no maintenance at all until it next
    /// published - or until the nightly pass came round.</remarks>
    private void LoadTopics() {
      _byPath.Clear();
      _byId.Clear();
      _nextId = 1;
      foreach(var d in _topics.FindAll()) {
        var at = new ArchTopic {
          Id = d["_id"].AsInt32,
          Path = d["p"].AsString,
          // LiteDB hands every DateTime back as Local; the cursor is compared against UTC bucket
          // boundaries, so it is normalised once here rather than at every use.
          Rt = d["rt"].IsNull ? DateTime.MinValue : d["rt"].AsDateTime.ToUniversalTime(),
          Pt = d["pt"].IsNull ? DateTime.MinValue : d["pt"].AsDateTime.ToUniversalTime(),
          Keep = d["k"].IsNull ? 0 : d["k"].AsDouble
        };
        at.Hot = at.Keep > 0 && at.Keep <= HOT_KEEP_DAYS;
        // No repository when the migration tool opens the store, and none yet if this ever runs
        // before the tree is built. A null topic is already the "gone, not swept yet" case.
        at.T = Topic.root == null ? null : Topic.root.Get(at.Path, false);
        _byPath[at.Path] = at;
        _byId[at.Id] = at;
        if(at.Id >= _nextId) {
          _nextId = at.Id + 1;
        }
      }
    }

    internal void Close() {
      // Before the write lock, not after: EndBatch takes the read lock to flush, and the gate is
      // declared NoRecursion precisely so that holding one while asking for the other is an error
      // rather than a deadlock waiting for the wrong day.
      try {
        EndBatch();
      }
      catch(Exception ex) {
        Log.Warning("Archivist.Close - unflushed batch lost: {0}", ex.Message);
      }
      _gate.EnterWriteLock();
      try {
        CommitAndDispose(ref _raw);
        CommitAndDispose(ref _hot);
        CommitAndDispose(ref _meta);
        _topics = null;
        _rawA = null;
        _hotA = null;
      }
      finally {
        _gate.ExitWriteLock();
      }
    }

    private static void CommitAndDispose(ref LiteDatabase db) {
      var d = Interlocked.Exchange(ref db, null);
      if(d == null) {
        return;
      }
      try {
        d.Commit();
        d.Checkpoint();
      }
      catch(Exception ex) {
        Log.Warning("Archivist.Close - {0}", ex.Message);
      }
      d.Dispose();
    }

    public void Dispose() {
      Close();
      _gate.Dispose();
    }

    #endregion open / close

    #region topics

    /// <summary>Finds or registers the topic, and repairs the registry after a rename.</summary>
    /// <remarks>The old code never updated the stored path, so the next nightly pass could not
    /// resolve it, decided the topic was gone and deleted its whole history. Matching on the live
    /// Topic instance first is what makes a rename look like a rename instead of a new topic.</remarks>
    internal ArchTopic Resolve(Topic t) {
      if(t == null) {
        return null;
      }
      ArchTopic at;
      if(_byPath.TryGetValue(t.path, out at)) {
        at.T = t;
        RefreshKeep(at);
        return at;
      }
      foreach(var known in _byId.Values) {
        if(ReferenceEquals(known.T, t)) {
          _byPath.Remove(known.Path);
          known.Path = t.path;
          _byPath[known.Path] = known;
          RefreshKeep(known);
          Store(known);
          return known;
        }
      }
      // A brand new topic has no history behind it, so folding starts at the current hour.
      var now = ArchTime.HourFloor(DateTime.UtcNow);
      at = new ArchTopic { Id = _nextId++, Path = t.path, Rt = now, Pt = now, T = t };
      RefreshKeep(at);
      _byPath[at.Path] = at;
      _byId[at.Id] = at;
      Store(at);
      return at;
    }

    /// <summary>Registers a topic by path, with no live Topic behind it.</summary>
    /// <remarks>For the migration tool, which has an archive to convert but no repository. The
    /// cursors are supplied rather than defaulted to now: a converted history has already been
    /// folded and swept by the tool, and leaving them at now would make the server refold it.</remarks>
    internal ArchTopic Register(string path, double keep, DateTime rtUtc, DateTime ptUtc) {
      ArchTopic at;
      if(!_byPath.TryGetValue(path, out at)) {
        at = new ArchTopic { Id = _nextId++, Path = path };
        _byPath[path] = at;
        _byId[at.Id] = at;
      }
      at.Keep = keep <= 0 ? 10 : keep;
      at.Hot = at.Keep <= HOT_KEEP_DAYS;
      at.Rt = rtUtc;
      at.Pt = ptUtc;
      Store(at);
      return at;
    }

    internal ArchTopic ById(int id) {
      ArchTopic at;
      return _byId.TryGetValue(id, out at) ? at : null;
    }
    internal ArchTopic ByPath(string path) {
      ArchTopic at;
      return path != null && _byPath.TryGetValue(path, out at) ? at : null;
    }
    internal ArchTopic[] Topics { get { return _byId.Values.ToArray(); } }

    /// <summary>Rereads Arch.keep, which the owner may change at any time.</summary>
    /// <remarks>Absent, non-numeric or non-positive keeps fall back the way ArchLog.Keep did.
    /// A topic whose keep crosses the hot threshold simply starts writing to the other file; its
    /// existing samples age out where they lie, and reads look in both files anyway.</remarks>
    private static void RefreshKeep(ArchTopic at) {
      double k = 0;
      if(at.T != null) {
        k = at.T.GetField("Arch.keep").AsDouble(0);
      }
      if(k <= 0) {
        k = 10;
      }
      at.Keep = k;
      at.Hot = k <= HOT_KEEP_DAYS;
    }

    internal void Store(ArchTopic at) {
      _topics.Upsert(new BsonDocument {
        ["_id"] = at.Id,
        ["p"] = at.Path,
        ["rt"] = at.Rt,
        ["pt"] = at.Pt,
        ["k"] = at.Keep
      });
    }

    #endregion topics

    #region write

    /// <summary>Appends one sample. Called only from the plugin worker thread.</summary>
    internal void Append(ArchTopic at, double v) {
      Append(at, DateTime.UtcNow, v);
    }

    /// <summary>Appends at an explicit instant, so a history can be laid down without waiting for one.</summary>
    /// <param name="utc">Must be UTC. LiteDB normalises by Kind on the way in and hands back Local
    /// on the way out, so passing the wrong Kind here silently shifts the sample by the offset.</param>
    internal void Append(ArchTopic at, DateTime utc, double v) {
      if(at == null || !IsOpen || double.IsNaN(v) || double.IsInfinity(v)) {
        return;
      }
      // No topic field: the key carries it. That is what pays for the wider key.
      var doc = new BsonDocument {
        ["_id"] = ArchTime.PackSample(at.Id, NextTicks(at, utc.ToUniversalTime())),
        ["v"] = v
      };
      if(_batch) {
        var pend = at.Hot ? _pendHot : _pendRaw;
        pend.Add(doc);
        if(pend.Count >= BATCH_ROWS) {
          FlushPending(pend, at.Hot);
        }
        return;
      }
      _gate.EnterReadLock();
      try {
        Target(at).Insert(doc);
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    #region batched writing

    /// <summary>Buffers writes until EndBatch, for laying down a history as fast as the disk allows.</summary>
    /// <remarks>For the migration tool and nothing else, and the running server deliberately does
    /// NOT use it. Measured, the server has no reason to: the live archive takes about one sample a
    /// second, which is a thousandth of what a row-at-a-time write can absorb, so batching there
    /// would buy nothing and cost the one thing that matters - every sample is durable the moment it
    /// is stored, rather than only after a block closes.
    /// <para>While batching, rows that have not been flushed yet are invisible to reads. Nothing in
    /// the migration reads raw samples back - the fold works from its own in-memory buffer - but
    /// that is a property of the caller, not a guarantee of this class.</para>
    /// <para>Single threaded only: LiteDB transactions belong to the thread that opened them.</para></remarks>
    internal void BeginBatch() {
      if(!IsOpen || _batch) {
        return;
      }
      _pendRaw = new List<BsonDocument>(BATCH_ROWS);
      _pendHot = new List<BsonDocument>(BATCH_ROWS);
      _metaPend = 0;
      _meta.BeginTrans();
      _batch = true;
    }

    /// <summary>Writes out whatever is still buffered and leaves batch mode.</summary>
    internal void EndBatch() {
      if(!_batch) {
        return;
      }
      _batch = false;
      FlushPending(_pendRaw, false);
      FlushPending(_pendHot, true);
      _gate.EnterReadLock();
      try {
        _meta.Commit();
      }
      finally {
        _gate.ExitReadLock();
      }
      _pendRaw = null;
      _pendHot = null;
      _metaPend = 0;
    }

    private void FlushPending(List<BsonDocument> pend, bool hot) {
      if(pend == null || pend.Count == 0) {
        return;
      }
      _gate.EnterReadLock();
      try {
        (hot ? _hotA : _rawA).InsertBulk(pend, pend.Count);
      }
      finally {
        _gate.ExitReadLock();
      }
      pend.Clear();
    }

    /// <summary>Closes and reopens the rollup transaction every so often while batching.</summary>
    /// <remarks>One transaction around the whole migration would hold half a million modified pages
    /// before committing any of them, which is how a run that peaks at 21 MB turns into one that
    /// does not finish. Buckets cannot go through InsertBulk - they are upserts - so a periodic
    /// commit is all there is to take.</remarks>
    private void MetaWritten() {
      if(_batch && ++_metaPend >= BATCH_ROWS) {
        _meta.Commit();
        _meta.BeginTrans();
        _metaPend = 0;
      }
    }

    #endregion batched writing

    /// <summary>An instant for this topic that the sample before it did not already take.</summary>
    /// <remarks>On a 100 ns grid a real collision needs one topic to report twice inside the same
    /// tick of the system clock, which cannot happen - the clock is coarser than that by five
    /// orders of magnitude. The guard is here for the case the format does not get to assume: a
    /// migration replaying a coarser history, where consecutive source rows of one topic can share
    /// a millisecond. Borrowing the next tick shifts such a sample by 100 ns, which no chart can
    /// show, whereas a duplicate key is an exception that would abort the run.
    /// <para>It triggers on the offered instant repeating, NOT on the key going backwards. A ratchet
    /// was tried first and was wrong: a caller appending out of order - a backfill, a test, an
    /// import - would have every sample after the newest one silently relocated to just after it.
    /// Two tests caught that, one of them the repeated-hour fold, where alternating between the two
    /// halves of the hour collapsed seven of eight samples into the wrong one.</para>
    /// <para>Per topic, and no interlock: writes come from the worker thread or the migration tool,
    /// both single threaded, and two topics can no longer collide with each other at all.</para></remarks>
    private static long NextTicks(ArchTopic at, DateTime utc) {
      long t = utc.Ticks;
      if(t == at.LastRaw) {
        t = at.LastTicks + 1;
      }
      at.LastRaw = utc.Ticks;
      at.LastTicks = t;
      return t;
    }

    private ILiteCollection<BsonDocument> Target(ArchTopic at) {
      return at.Hot ? _hotA : _rawA;
    }

    #endregion write

    #region read

    /// <summary>Samples for the given topics in time order, mapped onto the caller slot order.</summary>
    /// <param name="ids">Topic ids; the position in this array becomes ArchSample.Idx.</param>
    /// <param name="limit">Hard ceiling on rows read. Zero means no ceiling.</param>
    /// <remarks>Times in and out are LOCAL - LiteDB stores UTC and hands back Local, and the
    /// accumulator has always worked in local time, so the conversion stays where it always was.</remarks>
    internal IEnumerable<ArchSample> Stream(int[] ids, DateTime begin, DateTime end, bool desc, int limit) {
      if(!IsOpen || ids == null || ids.Length == 0) {
        yield break;
      }
      long lo = begin.ToUniversalTime().Ticks, hi = end.ToUniversalTime().Ticks;
      if(hi < lo) {
        yield break;
      }
      var cur = new List<RawCursor>(ids.Length * 2);
      for(int k = 0; k < ids.Length; k++) {
        // A slot whose topic has never been archived stays in the result - the caller asked for
        // that column and the row shape has to match - but contributes no rows.
        if(ids[k] <= 0) {
          continue;
        }
        // Both files, always. A topic whose Arch.keep crossed the ring-buffer threshold has rows on
        // each side; the old code read the hot file only if some topic in the request was currently
        // hot, so a topic that had cooled down lost its recent history from every chart. An empty
        // side costs one primary-key seek that matches nothing.
        cur.Add(new RawCursor(_rawA, ids[k], k, lo, hi, desc));
        cur.Add(new RawCursor(_hotA, ids[k], k, lo, hi, desc));
      }
      if(cur.Count == 0) {
        yield break;
      }
      _gate.EnterReadLock();
      try {
        long n = 0;
        foreach(var s in MergeAll(cur, desc)) {
          yield return s;
          if(limit > 0 && ++n >= limit) {
            Log.Warning("Archivist.Stream stopped at {0} rows - the request outgrew the raw ceiling", limit);
            yield break;
          }
        }
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Several already-ordered streams into one, still ordered.</summary>
    /// <remarks>The composite key orders rows by topic first, so cross-topic time order - which the
    /// accumulator requires - has to be rebuilt here. A linear scan for the smallest head rather
    /// than a heap: the count is the number of requested topics doubled, thirty or sixty in
    /// practice, and a heap would cost more in indirection than it saves in comparisons.</remarks>
    private static IEnumerable<ArchSample> MergeAll(List<RawCursor> cur, bool desc) {
      while(true) {
        int best = -1;
        for(int i = 0; i < cur.Count; i++) {
          if(!cur[i].Has) {
            continue;
          }
          if(best < 0 || (desc ? cur[i].Current.T > cur[best].Current.T : cur[i].Current.T < cur[best].Current.T)) {
            best = i;
          }
        }
        if(best < 0) {
          yield break;
        }
        yield return cur[best].Current;
        cur[best].Step();
      }
    }

    /// <summary>One topic's slice of one sample file, read a block at a time.</summary>
    /// <remarks>Blocks rather than one long-lived cursor, and the reason is a hard engine limit:
    /// LiteDB allows 100 concurrent transactions per file and a lazy query holds one open for as
    /// long as it is being enumerated. One cursor per topic would put thirty of them on a single
    /// chart, and graph.js issues a request every 50 ms while panning, across every chart on the
    /// page - four such charts at once and the archive starts throwing instead of drawing. Reading
    /// a block closes the cursor before returning, so at most one exists at any instant, and a slow
    /// consumer no longer holds a transaction open while it builds its JS array.
    /// <para>The block boundary is carried as ticks rather than as a key, because two samples of one
    /// topic can never share an instant - NextTicks guarantees it - so "everything after the last
    /// tick I read" cannot skip a row.</para></remarks>
    private sealed class RawCursor {
      private const int BLOCK = 2048;

      private readonly ILiteCollection<BsonDocument> _col;
      private readonly int _id;
      private readonly int _slot;
      private readonly bool _desc;
      private readonly long _loTicks;
      private readonly long _hiTicks;
      private readonly List<ArchSample> _buf;
      private long _mark;
      private int _pos;
      private bool _drained;

      internal RawCursor(ILiteCollection<BsonDocument> col, int id, int slot, long loTicks, long hiTicks, bool desc) {
        _col = col;
        _id = id;
        _slot = slot;
        _desc = desc;
        _loTicks = loTicks;
        _hiTicks = hiTicks;
        _buf = new List<ArchSample>();
        _mark = desc ? hiTicks : loTicks;
      }

      internal bool Has { get { return _pos < _buf.Count || Fill(); } }
      internal ArchSample Current { get { return _buf[_pos]; } }
      internal void Step() { _pos++; }

      private bool Fill() {
        if(_drained) {
          return false;
        }
        _buf.Clear();
        _pos = 0;
        long lo = _desc ? _loTicks : _mark;
        long hi = _desc ? _mark : _hiTicks;
        if(lo > hi) {
          _drained = true;
          return false;
        }
        var q = _col.Query().Where("$._id BETWEEN @0 AND @1",
          new BsonValue(ArchTime.PackSample(_id, lo)), new BsonValue(ArchTime.PackSample(_id, hi)));
        q = _desc ? q.OrderByDescending("$._id") : q.OrderBy("$._id");
        foreach(var d in q.Limit(BLOCK).ToEnumerable()) {
          _buf.Add(new ArchSample(_slot, ArchTime.TimeOfSample(d["_id"].AsBinary), d["v"].AsDouble));
        }
        if(_buf.Count == 0) {
          _drained = true;
          return false;
        }
        long last = _buf[_buf.Count - 1].T.Ticks;
        if(_buf.Count < BLOCK || (_desc ? last <= _loTicks : last >= _hiTicks)) {
          _drained = true;                    // the block was the rest of the range
        } else {
          _mark = _desc ? last - 1 : last + 1;
        }
        return true;
      }
    }

    /// <summary>Last value before begin for each topic, or NaN - the carry-in the resampler needs.</summary>
    /// <param name="gran">Which series the caller is about to read, so the seed comes from the same
    /// one: seeding a stream of hourly averages with a single raw sample would misstate the head
    /// of the first bucket.</param>
    internal double[] Seed(int[] ids, DateTime begin, int gran) {
      var seed = new double[ids.Length];
      for(int k = 0; k < ids.Length; k++) {
        seed[k] = double.NaN;
      }
      if(!IsOpen) {
        return seed;
      }
      DateTime beginUtc = begin.ToUniversalTime();
      _gate.EnterReadLock();
      try {
        for(int k = 0; k < ids.Length; k++) {
          if(ids[k] <= 0) {
            continue;
          }
          seed[k] = gran == ArchTime.GRAN_RAW ? SeedRaw(ids[k], beginUtc) : SeedRoll(gran, ids[k], beginUtc);
        }
      }
      finally {
        _gate.ExitReadLock();
      }
      return seed;
    }

    /// <summary>The last raw value before begin.</summary>
    /// <remarks>Tries the rollups first, where the carried last value is a primary-key seek. Only
    /// the tail no bucket covers yet needs to be looked at, and under the composite key that too is
    /// a primary-key range of one topic: the newest row in it is the far end of the range, so the
    /// engine seeks to it instead of walking every other topic's rows backwards.</remarks>
    private double SeedRaw(int id, DateTime beginUtc) {
      Bucket b;
      long idx;
      double fromBucket = double.NaN;
      DateTime scanFrom = beginUtc - SEED_LOOKBACK;
      if(TryLastBucket(ArchTime.GRAN_HOUR, id, ArchTime.HourIndex(beginUtc), out b, out idx)) {
        fromBucket = b.Last;
        scanFrom = ArchTime.HourStart(idx + 1);
      }
      if(scanFrom >= beginUtc) {
        return fromBucket;
      }
      var at = ById(id);
      var col = at != null && at.Hot ? _hotA : _rawA;
      var d = col.Query()
                 .Where("$._id BETWEEN @0 AND @1",
                        new BsonValue(ArchTime.PackSample(id, scanFrom.Ticks)),
                        new BsonValue(ArchTime.PackSample(id, beginUtc.Ticks)))
                 .OrderByDescending("$._id")
                 .FirstOrDefault();
      if(d != null) {
        double v = d["v"].AsDouble;
        if(!double.IsNaN(v) && !double.IsInfinity(v)) {
          return v;
        }
      }
      return fromBucket;
    }

    private double SeedRoll(int gran, int id, DateTime beginUtc) {
      Bucket b;
      return TryLastBucket(gran, id, ArchTime.BucketIndex(beginUtc, gran), out b) ? b.V : double.NaN;
    }

    #endregion read

    #region rollups

    private ILiteCollection<BsonDocument> Roll(int gran) {
      return gran == ArchTime.GRAN_DAY ? _rollD : gran == ArchTime.GRAN_HOUR ? _rollH : _roll5;
    }

    internal void UpsertBucket(int gran, int topicId, long idx, Bucket b) {
      if(!IsOpen || b.N <= 0) {
        return;
      }
      _gate.EnterReadLock();
      try {
        // Neither the topic nor the bucket number is stored beside the key: the key is both of
        // them. They used to be, because a cross-topic index needed something to order by, and
        // that index is gone. Documents written earlier still carry the two fields; nothing reads
        // them, and they cost their bytes until the next rebuild rewrites those rows.
        Roll(gran).Upsert(new BsonDocument {
          ["_id"] = ArchTime.PackId(topicId, idx),
          ["v"] = b.V,
          ["n"] = b.N,
          ["l"] = b.Last
        });
        MetaWritten();
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>The newest bucket strictly before the given index, if there is one.</summary>
    /// <remarks>A range over the composite key, which is the primary index - one seek, no scan.
    /// This is what the per-topic index the raw file deliberately lacks would otherwise be for.</remarks>
    internal bool TryLastBucket(int gran, int topicId, long beforeIdx, out Bucket b) {
      long idx;
      return TryLastBucket(gran, topicId, beforeIdx, out b, out idx);
    }

    internal bool TryLastBucket(int gran, int topicId, long beforeIdx, out Bucket b, out long foundIdx) {
      b = default(Bucket);
      foundIdx = -1;
      if(!IsOpen || beforeIdx <= 0) {
        return false;
      }
      var d = Roll(gran).Query()
        .Where("$._id BETWEEN @0 AND @1",
               new BsonValue(ArchTime.PackId(topicId, 0)), new BsonValue(ArchTime.PackId(topicId, beforeIdx - 1)))
        .OrderByDescending("$._id")
        .FirstOrDefault();
      if(d == null) {
        return false;
      }
      b = FromDoc(d);
      foundIdx = ArchTime.BucketOf(d["_id"].AsInt64);
      return true;
    }

    internal bool TryBucket(int gran, int topicId, long idx, out Bucket b) {
      b = default(Bucket);
      if(!IsOpen) {
        return false;
      }
      var d = Roll(gran).FindById(ArchTime.PackId(topicId, idx));
      if(d == null) {
        return false;
      }
      b = FromDoc(d);
      return true;
    }

    private static Bucket FromDoc(BsonDocument d) {
      return new Bucket {
        N = d["n"].AsInt32,
        V = d["v"].AsDouble,
        Last = d["l"].AsDouble
      };
    }

    /// <summary>Folded buckets for the given topics, in time order, mapped onto the caller slots.</summary>
    /// <param name="atStart">Stamp each bucket at the instant its interval begins instead of at its
    /// midpoint. The stored convention is the midpoint, which is what ArchCompact2 always wrote and
    /// what every chart is drawn from - but held forward from the midpoint the end buckets of a
    /// window are misweighted, so folding one granularity into a coarser one asks for the start.</param>
    internal IEnumerable<ArchSample> RollStream(int gran, int[] ids, DateTime begin, DateTime end, bool atStart) {
      if(!IsOpen || ids == null || ids.Length == 0) {
        yield break;
      }
      var slot = new Dictionary<int, int>(ids.Length);
      for(int k = 0; k < ids.Length; k++) {
        if(ids[k] > 0) {
          slot[ids[k]] = k;
        }
      }
      if(slot.Count == 0) {
        yield break;
      }
      // Bucket numbers rather than instants: integer arithmetic, exact through the repeated hour.
      // The boundary buckets are the ones containing begin and end, inclusive on both sides - the
      // caller wants whatever overlaps its window.
      long lo = ArchTime.BucketIndex(begin.ToUniversalTime(), gran);
      long hi = ArchTime.BucketIndex(end.ToUniversalTime(), gran);
      if(hi < lo) {
        yield break;
      }
      // One exact primary-key range per topic, exactly as the raw side does, rather than one range
      // over a cross-topic index filtered down afterwards. The old form read every topic's buckets
      // inside the window and threw most away, so its cost followed the archive rather than the
      // request: measured on the live copy, four topics over thirteen days cost MORE than thirty
      // over the same window. Same 13 420 rows, 700 ms that way against 124 ms this way.
      var cur = new List<BucketCursor>(slot.Count);
      foreach(var kv in slot) {
        cur.Add(new BucketCursor(Roll(gran), gran, kv.Key, kv.Value, lo, hi, atStart));
      }
      _gate.EnterReadLock();
      try {
        foreach(var s in MergeBuckets(cur)) {
          yield return s;
        }
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Several ordered bucket streams into one, still ordered by time.</summary>
    private static IEnumerable<ArchSample> MergeBuckets(List<BucketCursor> cur) {
      while(true) {
        int best = -1;
        for(int i = 0; i < cur.Count; i++) {
          if(cur[i].Has && (best < 0 || cur[i].Current.T < cur[best].Current.T)) {
            best = i;
          }
        }
        if(best < 0) {
          yield break;
        }
        yield return cur[best].Current;
        cur[best].Step();
      }
    }

    /// <summary>One topic's buckets over an index range, read a block at a time.</summary>
    /// <remarks>The same shape as RawCursor and for the same reason: LiteDB caps a file at 100
    /// concurrent transactions and a lazy query holds one open while it is enumerated, so a chart
    /// over thirty topics must not keep thirty of them alive. Reading a block closes the cursor
    /// before returning.
    /// <para>The block boundary is the bucket index, which is unique per topic, so "everything after
    /// the last index I read" cannot skip a bucket.</para></remarks>
    private sealed class BucketCursor {
      private const int BLOCK = 2048;

      private readonly ILiteCollection<BsonDocument> _col;
      private readonly int _gran;
      private readonly int _id;
      private readonly int _slot;
      private readonly long _hi;
      private readonly bool _atStart;
      private readonly List<ArchSample> _buf;
      private long _mark;
      private int _pos;
      private bool _drained;

      internal BucketCursor(ILiteCollection<BsonDocument> col, int gran, int id, int slot,
                            long lo, long hi, bool atStart) {
        _col = col;
        _gran = gran;
        _id = id;
        _slot = slot;
        _hi = hi;
        _atStart = atStart;
        _buf = new List<ArchSample>();
        _mark = lo;
      }

      internal bool Has { get { return _pos < _buf.Count || Fill(); } }
      internal ArchSample Current { get { return _buf[_pos]; } }
      internal void Step() { _pos++; }

      private bool Fill() {
        if(_drained) {
          return false;
        }
        _buf.Clear();
        _pos = 0;
        if(_mark > _hi) {
          _drained = true;
          return false;
        }
        long last = _mark;
        foreach(var d in _col.Query()
            .Where("$._id BETWEEN @0 AND @1",
                   new BsonValue(ArchTime.PackId(_id, _mark)), new BsonValue(ArchTime.PackId(_id, _hi)))
            .OrderBy("$._id").Limit(BLOCK).ToEnumerable()) {
          // The index comes out of the key, not out of a field beside it: the key always has it,
          // and that is what lets the "k" field and its index go away entirely.
          last = ArchTime.BucketOf(d["_id"].AsInt64);
          _buf.Add(new ArchSample(_slot,
            _atStart ? ArchTime.BucketStart(last, _gran) : ArchTime.BucketMid(last, _gran),
            d["v"].AsDouble));
        }
        if(_buf.Count == 0) {
          _drained = true;
          return false;
        }
        if(_buf.Count < BLOCK || last >= _hi) {
          _drained = true;
        } else {
          _mark = last + 1;
        }
        return true;
      }
    }

    /// <summary>Oldest rollup frontier across the given topics - where folded data stops and raw begins.</summary>
    /// <remarks>One boundary for the whole request, not one per topic: per-topic frontiers would
    /// interleave one series of folded buckets with another series of raw samples, and the
    /// accumulator needs the stream it is handed to be monotone in time.</remarks>
    internal DateTime RollupHorizon(int[] ids) {
      var min = DateTime.MaxValue;
      foreach(var id in ids) {
        var at = ById(id);
        if(at == null) {
          continue;
        }
        if(at.Rt < min) {
          min = at.Rt;
        }
      }
      return min;
    }

    /// <summary>Every stored bucket of one topic between two indices, with its aggregates.</summary>
    /// <remarks>A range over the composite primary key. Folding a day out of its hours needs the
    /// counts and extremes as well as the averages, which the sample stream does not carry.</remarks>
    internal IEnumerable<KeyValuePair<long, Bucket>> Buckets(int gran, int topicId, long fromIdx, long toIdx) {
      if(!IsOpen || toIdx < fromIdx) {
        yield break;
      }
      _gate.EnterReadLock();
      try {
        var q = Roll(gran).Query()
          .Where("$._id BETWEEN @0 AND @1",
                 new BsonValue(ArchTime.PackId(topicId, fromIdx)), new BsonValue(ArchTime.PackId(topicId, toIdx)))
          .OrderBy("$._id");
        foreach(var d in q.ToEnumerable()) {
          yield return new KeyValuePair<long, Bucket>(ArchTime.BucketOf(d["_id"].AsInt64), FromDoc(d));
        }
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    internal long RollCount(int gran) {
      return IsOpen ? Roll(gran).Count() : 0;
    }

    #endregion rollups

    #region retention

    /// <summary>Deletes one topic's samples inside a time window.</summary>
    /// <remarks>An exact primary-key range now, in both files. Two things change from the old
    /// layout. The window no longer exists to keep a scan affordable - it only bounds how much one
    /// pass does - because the range touches no other topic's rows at all. And both files are
    /// swept, not just the one the topic currently writes to: a topic whose Arch.keep crossed the
    /// ring-buffer threshold left rows on the other side, and nothing was ever deleting them.</remarks>
    internal int PurgeRaw(ArchTopic at, DateTime fromLocal, DateTime toLocal) {
      if(!IsOpen || at == null || toLocal <= fromLocal) {
        return 0;
      }
      var lo = new BsonValue(ArchTime.PackSample(at.Id, fromLocal.ToUniversalTime().Ticks));
      var hi = new BsonValue(ArchTime.PackSample(at.Id, toLocal.ToUniversalTime().Ticks));
      _gate.EnterReadLock();
      try {
        return _rawA.DeleteMany("$._id BETWEEN @0 AND @1", lo, hi)
             + _hotA.DeleteMany("$._id BETWEEN @0 AND @1", lo, hi);
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Deletes one topic's buckets by index range - a primary-key range, so exact.</summary>
    internal int PurgeRoll(int gran, int topicId, long fromIdx, long toIdx) {
      if(!IsOpen || toIdx < fromIdx) {
        return 0;
      }
      _gate.EnterReadLock();
      try {
        return Roll(gran).DeleteMany("$._id BETWEEN @0 AND @1",
          new BsonValue(ArchTime.PackId(topicId, fromIdx)), new BsonValue(ArchTime.PackId(topicId, toIdx)));
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Forgets a topic entirely: samples, rollups and the registry row.</summary>
    /// <remarks>The retention sweep has normally taken the samples already, but the whole-topic key
    /// range makes saying so unnecessary - deleting them outright is one seek per file. The id is
    /// not reused: a stale sample that somehow outlived the sweep must never be able to reappear
    /// under a different topic.</remarks>
    internal void DropTopic(ArchTopic at) {
      if(!IsOpen || at == null) {
        return;
      }
      // Each of these takes the gate itself, and it is declared non-recursive on purpose - holding
      // it across a call that reacquires it is exactly the deadlock that policy exists to catch.
      foreach(var g in ArchTime.LEVELS) {
        PurgeRoll(g, at.Id, 0, uint.MaxValue);
      }
      var lo = new BsonValue(ArchTime.TopicFloor(at.Id));
      var hi = new BsonValue(ArchTime.TopicCeil(at.Id));
      _gate.EnterReadLock();
      try {
        _rawA.DeleteMany("$._id BETWEEN @0 AND @1", lo, hi);
        _hotA.DeleteMany("$._id BETWEEN @0 AND @1", lo, hi);
        _topics.Delete(at.Id);
      }
      finally {
        _gate.ExitReadLock();
      }
      _byId.Remove(at.Id);
      if(_byPath.ContainsKey(at.Path) && ReferenceEquals(_byPath[at.Path], at)) {
        _byPath.Remove(at.Path);
      }
    }

    /// <summary>Rewrites a sample file, returning the space its deletions left behind.</summary>
    /// <remarks>Takes the gate exclusively and gives up rather than waiting: a query in flight is
    /// short and the next idle pass is milliseconds away, whereas the old code rebuilt a 909 MB
    /// file while pool threads were reading it.</remarks>
    internal bool TryRebuild(bool hot) {
      if(!IsOpen) {
        return false;
      }
      if(!_gate.TryEnterWriteLock(0)) {
        return false;
      }
      try {
        (hot ? _hot : _raw).Rebuild();
      }
      finally {
        _gate.ExitWriteLock();
      }
      DropRebuildLeftovers(hot ? HOT_FILE : RAW_FILE);
      return true;
    }

    /// <summary>Deletes the copy Rebuild leaves behind.</summary>
    /// <remarks>LiteDB.Rebuild renames the old file to "&lt;name&gt;-backup.ldb" and never removes
    /// it; a second rebuild finding that name taken writes "-backup-1", then "-backup-2", and so on.
    /// Nothing ever collects them. An overnight run left seven copies of the ring-buffer file after
    /// six hours - harmless at 128 KB each, but the history file is rebuilt daily and weighs 396 MB,
    /// so the same mechanism would have shed twelve gigabytes a month.
    /// <para>Deleted rather than rotated because the rebuild has already completed and the file has
    /// already been reopened by the time this runs: what is left is the pre-rebuild copy of a
    /// database that is now known to be good. A failure to delete is not worth failing the rebuild
    /// over - the space comes back on the next pass.</para></remarks>
    private void DropRebuildLeftovers(string file) {
      string stem = Path.GetFileNameWithoutExtension(file);
      try {
        foreach(var f in Directory.GetFiles(_dir, stem + "-backup*" + Path.GetExtension(file))) {
          try {
            File.Delete(f);
          }
          catch(IOException) {                 // still held by something; next pass will get it
          }
        }
      }
      catch(IOException ex) {
        Log.Warning("Archivist could not list rebuild leftovers in {0} - {1}", _dir, ex.Message);
      }
    }

    internal long RawCount(bool hot) {
      if(!IsOpen) {
        return 0;
      }
      _gate.EnterReadLock();
      try {
        return (hot ? _hotA : _rawA).Count();
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Size of a sample file on disk, or zero if it is not there.</summary>
    /// <remarks>Read from the filesystem rather than from the engine: what matters for deciding
    /// whether a rebuild would give anything back is what the file occupies, which is exactly the
    /// number the operating system reports and not any internal page count.</remarks>
    internal long FileBytes(bool hot) {
      try {
        var fi = new FileInfo(Path.Combine(_dir, hot ? HOT_FILE : RAW_FILE));
        return fi.Exists ? fi.Length : 0;
      }
      catch(IOException) {
        return 0;
      }
    }

    #endregion retention

    #region sizing

    /// <summary>Rough row count of the sample files, for deciding whether a raw read is sane.</summary>
    /// <remarks>LiteDB keeps the count in the collection header, so this costs nothing; it is an
    /// estimate of the whole file rather than of the requested range, which is all the safety
    /// check needs - it only has to notice when a request is orders of magnitude too big.</remarks>
    internal long ApproxRows() {
      if(!IsOpen) {
        return 0;
      }
      _gate.EnterReadLock();
      try {
        return (long)_rawA.Count() + _hotA.Count();
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    #endregion sizing
  }
}
