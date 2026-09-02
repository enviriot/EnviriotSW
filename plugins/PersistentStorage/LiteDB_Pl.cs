///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using NiL.JS.Extensions;
using LiteDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Policy;

namespace X13.PersistentStorage {
  /// <summary>Stores the repository state - manifests, states and the log - in persist.ldb.</summary>
  /// <remarks>Was two classes until now: an abstract base plus an eighteen-line LiteDB_Pl carrying
  /// the MEF export. The split existed for the Firebird and MySQL backends, which implemented five
  /// abstract archive methods; the archive became the separate Archivist plugin and those two
  /// backends were deleted, leaving a base class with no abstract members and an heir that added
  /// nothing. Nothing outside referenced either name.
  /// <para>The archive is gone from here entirely - ArchStore, ArchRollup and ArchRetention live in
  /// Archivist now, and the thinning does not live anywhere.</para></remarks>
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "LiteDB")]
  internal class LiteDB_Pl : IPlugModul {
    private const string OWNER_PATH = "/$YS/PersistentStorage";
    private Topic _owner;
    private IDisposable _allSub;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Perform> _q;
    private Thread _tr;
    private volatile bool _terminate;
    private readonly AutoResetEvent _tick;
    public LiteDB_Pl() {
      _tick = new AutoResetEvent(false);
      _q = new System.Collections.Concurrent.ConcurrentQueue<Perform>();
    }

    #region IPlugModul Members
    public void Init() {
      var dir = Path.GetDirectoryName(DB_PATH);
      if (!Directory.Exists(dir)) {
        Directory.CreateDirectory(dir);
      }
      bool exist = File.Exists(DB_PATH);
      // Seeded whether or not a database is here yet. It used to be seeded only alongside the
      // startup backup, which runs only when one exists - so a server first started on an empty
      // data directory never got the setting at all, and the nightly Backup() then resolved it to
      // the four-letter string "undefined" and took persistent storage down with it.
      SeedBackupDir(dir);
      if (exist) {
        string bak_dir = BackupDir(dir);
        string fb = bak_dir + (new string(Path.DirectorySeparatorChar, 1)) + DateTime.Now.ToString("yyMMdd_HHmmss") + ".bak";
        File.Copy(DB_PATH, fb);
        Log.Info("backup {0} created", fb);
      }
      _base = new Dictionary<Topic, Stash>();
      _db = new LiteDatabase(new ConnectionString { Upgrade = true, Filename = DB_PATH }) { CheckpointSize = 50 };
      exist = exist && _db.CollectionExists("history");
      _history = _db.GetCollection<BsonDocument>("history");
      if (!exist) {
        _history.EnsureIndex("t");
      }
      Log.History = History;
      Log.Write += Log_Write;
    }
    public void Start() {
      _terminate = false;
      _tr = new Thread(new ThreadStart(ThreadM)) {
        IsBackground = true,
        Name = "PersistentStorage",
        Priority = ThreadPriority.BelowNormal
      };
      _tr.Start();
      _tick.WaitOne();  // wait load
      _allSub = Topic.Subscribe(SubFunc);
      if(_db.UserVersion < 4) {
        _db.UserVersion = 4;
        ImportDefault();
      }
    }
    public void Tick() {
      if(_q.Any()) {
        _tick.Set();
      }
    }
    /// <summary>Teardown in the order the dependencies run, not the order they were created.</summary>
    /// <remarks>Every step here has to happen before the next, and none of them used to happen at
    /// all. The repository callback goes first: while it is attached, any change to any topic -
    /// and Repo.Stop still exports the configuration after this - reaches SubFunc, which enqueues
    /// and calls _tick.Set(). Disposing _tick while that was live is the ObjectDisposedException
    /// the whole ordering exists to avoid. Log.Write and Log.History go next for the same reason
    /// one step down: both reach _history, which belongs to the database about to be closed.
    /// <para>The database is closed here at last. `//Interlocked.Exchange(ref _db, null)?.Dispose()`
    /// had been commented out, so the file was never closed and the last writes reached disk only
    /// through LiteDB's recovery on the next start. The cost is named rather than hidden: log
    /// records produced after this point are no longer persisted, because there is nothing left
    /// open to persist them into.</para></remarks>
    public void Stop() {
      IDisposable allSub = _allSub;
      _allSub = null;
      if(allSub != null) {
        allSub.Dispose();
      }
      Log.Write -= Log_Write;
      Log.History = null;

      _terminate = true;
      _tick.Set();
      if(_tr != null && !_tr.Join(5000)) {
        // Abandoned, not aborted. Thread.Abort lands at an arbitrary instruction, and for this
        // thread that instruction is most likely inside LiteDB - mid transaction, mid page write -
        // which is precisely how a file comes to need recovery. Not stopping in five seconds means
        // it is stuck in a LiteDB call, and there is nothing there to cancel: the only other
        // blocking point is _tick.WaitOne(15).
        // The thread is IsBackground, so the process still exits. What must NOT happen is the
        // cleanup below: disposing the database and the wait handle out from under a thread still
        // using them turns a slow shutdown into an ObjectDisposedException, or worse.
        Log.Error("PersistentStorage worker did not stop within 5 s; leaving the database open - it will be recovered on the next start");
        return;
      }
      var db = Interlocked.Exchange(ref _db, null);
      if(db != null) {
        try {
          db.Dispose();
        }
        catch(Exception ex) {
          Log.Warning("PersistentStorage close - {0}", ex.Message);
        }
      }
      _tick.Dispose();
    }
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        // Is<bool>, NOT AsBool: this decides whether the config topic has to be CREATED and
        // seeded, and a reader with a default cannot tell "not set yet" from "set to the
        // default" - the topic would then never be created.
        if(!Owner.GetState().Is<bool>()) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
      }
    }
    #endregion IPlugModul Members

    #region Persisten Storage Members
    private const string DB_PATH = "../data/persist.ldb";

    private LiteDatabase _db;
    private ILiteCollection<BsonDocument> _objects, _states, _history;
    // Deliberately not sorted: Topic.CompareTo orders by the mutable _path, and Move() rewrites
    // _path in place, which would silently corrupt a comparison-ordered index. Topic does not
    // override Equals/GetHashCode, so this keys on identity and survives a rename.
    private Dictionary<Topic, Stash> _base;

    private class Stash {
      public ObjectId id;
      public BsonDocument bm;
      public JSC.JSValue jm;
      public BsonDocument bs;
      public JSC.JSValue js;
    }

    private static string EscapFieldName(string fn) {
      if (string.IsNullOrEmpty(fn)) {
        throw new ArgumentNullException("PersistentStorage.EscapFieldName()");
      }
      StringBuilder sb = new StringBuilder();

      for (var i = 0; i < fn.Length; i++) {
        var c = fn[i];

        if (char.IsLetterOrDigit(c) || (c == '$' && i == 0) || (c == '-' && i > 0)) {
          sb.Append(c);
        } else {
          sb.Append("_");
          sb.Append(((ushort)c).ToString("X4"));
        }
      }
      return sb.ToString();
    }
    private static string UnescapFieldName(string fn) {
      if (string.IsNullOrEmpty(fn)) {
        throw new ArgumentNullException("PersistentStorage.UnescapFieldName()");
      }
      StringBuilder sb = new StringBuilder();
      for (var i = 0; i < fn.Length; i++) {
        var c = fn[i];
        if (c == '_' && i + 4 < fn.Length && ushort.TryParse(fn.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort cc)) {
          i += 4;
          sb.Append((char)cc);
        } else {
          sb.Append(c);
        }
      }
      return sb.ToString();
    }
    private BsonValue Js2Bs(JSC.JSValue val) {
      if (val == null) {
        return BsonValue.Null;
      }
      switch (val.ValueType) {
        case JSC.JSValueType.NotExists:
        case JSC.JSValueType.NotExistsInObject:
        case JSC.JSValueType.Undefined:
          return BsonValue.Null;
        case JSC.JSValueType.Boolean:
          return new BsonValue((bool)val);
        case JSC.JSValueType.Date: {
            if (val.Value is JSL.Date jsd) {
              return new BsonValue(jsd.ToDateTime().ToUniversalTime());
            }
            return BsonValue.Null;
          }
        case JSC.JSValueType.Double:
          return new BsonValue((double)val);
        case JSC.JSValueType.Integer:
          return new BsonValue((int)val);
        case JSC.JSValueType.String: {
            string s = val.AsString(null);
            if (s != null && s.StartsWith("¤TR")) {
              var t = Topic.I.Get(Topic.root, s.Substring(3), false, null, false, false);
              if (t != null) {
                if (_base.TryGetValue(t, out Stash tu)) {
                  return tu.bm["_id"];
                }
              }
              throw new ArgumentException("TopicRefernce(" + s.Substring(3) + ") NOT FOUND");
            }
            return new BsonValue(s);
          }
        case JSC.JSValueType.Object:
          if (val.IsNull) {
            return BsonValue.Null;
          }
          if (val is JSL.Array arr) {
            var r = new BsonArray();
            foreach (var f in arr) {
              if (int.TryParse(f.Key, out int i)) {
                while (i >= r.Count()) { r.Add(BsonValue.Null); }
                r[i] = Js2Bs(f.Value);
              }
            }
            return r;
          }
          // The dual representation, named once - see ByteArray.IsByteArray.
          if(ByteArray.IsByteArray(val, out ByteArray ba)) {
            return new BsonValue(ba.GetBytes());
          } {
            var r = new BsonDocument();
            foreach (var f in val) {
              r[EscapFieldName(f.Key)] = Js2Bs(f.Value);
            }
            return r;
          }
        default:
          throw new NotImplementedException("js2Bs(" + val.ValueType.ToString() + ")");
      }
    }
    private string Id2Topic(ObjectId id) {
      var d = _objects.FindById(id);
      BsonValue p;
      if (d != null && (p = d["p"]) != null && p.IsString) {
        return p.AsString;
      }
      return null;
    }
    private JSC.JSValue Bs2Js(BsonValue val) {
      if (val == null) {
        return JSC.JSValue.Undefined;
      }
      switch (val.Type) { //-V3002
        case BsonType.ObjectId: {
            var p = Id2Topic(val.AsObjectId);
            if (p != null) {
              return new JSL.String("¤TR" + p);
            } else {
              throw new ArgumentException("Unknown ObjectId: " + val.AsObjectId.ToString());
            }
          }
        case BsonType.Array: {
            var arr = val.AsArray;
            var r = new JSL.Array(arr.Count);
            for (int i = 0; i < arr.Count; i++) {
              if (!arr[i].IsNull) {
                r[i] = Bs2Js(arr[i]);
              }
            }
            return r;
          }
        case BsonType.Boolean:
          return new JSL.Boolean(val.AsBoolean);
        case BsonType.DateTime:
          return X13.JsExtLib.Context.ProxyValue(val.AsDateTime.ToLocalTime());
        case BsonType.Binary:
          return new ByteArray(val.AsBinary);
        case BsonType.Document: {
            var r = JSC.JSObject.CreateObject();
            var o = val.AsDocument;
            foreach (var i in o) {
              r[UnescapFieldName(i.Key)] = Bs2Js(i.Value);
            }
            return r;
          }
        case BsonType.Double: {
            return new JSL.Number(val.AsDouble);
          }
        case BsonType.Int32:
          return new JSL.Number(val.AsInt32);
        case BsonType.Int64:
          return new JSL.Number(val.AsInt64);
        case BsonType.Null:
          return JSC.JSValue.Null;
        case BsonType.String:
          return new JSL.String(val.AsString);
      }
      throw new NotImplementedException("Bs2Js(" + val.Type.ToString() + ")");
    }
    private void SubFunc(Perform p) {
      if (p.Art == Perform.E_Art.subscribe || p.Art == Perform.E_Art.subAck || p.Art == Perform.E_Art.setField || p.Art == Perform.E_Art.setState || p.Art == Perform.E_Art.unsubscribe || p.Prim == Owner) {
        return;
      }
      _q.Enqueue(p);
    }

    private void ThreadM() {
      Load();
      _tick.Set();

      DateTime backupDT;
      backupDT = DateTime.Now.AddDays(1).Date.AddHours(3.25);
      do {
        // The guard covers the whole body, not just Save. It used to sit around Save alone, so a
        // throw anywhere else - _db.BeginTrans() on a null _db, after a failed backup - ended the
        // thread outright. The server survives that, which is the bad part: it keeps running with
        // nothing being written, and the only sign is one line in the log.
        try {
          if (_tick.WaitOne(15)) {
            _db.BeginTrans();
            while (_q.TryDequeue(out Perform p)) {
              try {
                Save(p);
              }
              catch (Exception ex) {
                Log.Warning("PersistentStorage(" + (p == null ? "null" : p.ToString()) + ") - " + ex.ToString());
              }
            }
            _db.Commit();
          } else if (backupDT < DateTime.Now) {
            backupDT = DateTime.Now.AddDays(1).Date.AddHours(3.3);
            Log.Info("Backup started");
            try {
              Backup();
              Log.Info("Backup finished");
            }
            catch (Exception ex) {
              Log.Warning("Backup failed - " + ex.ToString());
            }
          }
        }
        catch (ThreadAbortException) {
          throw;                                  // Stop() is taking the thread down on purpose
        }
        catch (Exception ex) {
          Log.Error("PersistentStorage.ThreadM - " + ex.ToString());
          Thread.Sleep(1000);                     // do not spin on a fault that repeats every pass
        }
      } while (!_terminate);
      var db = Interlocked.Exchange(ref _db, null);
      if (db != null) {
        try {
          db.Commit();
          db.Checkpoint();
        }
        catch (Exception ex) {
          Log.Warning("PersistenStorage.DB.Terminate - {0}", ex);
        }
        db.Dispose();
      }
    }

    private void Load() {
      bool exist = _db.CollectionExists("objects");
      _objects = _db.GetCollection<BsonDocument>("objects");
      _states = _db.GetCollection<BsonDocument>("states");

      if (exist) {
        Topic t;
        Stash a;
        JSC.JSValue jTmp;
        bool saved;
        string sTmp;
        List<string> oldT = new List<string>();
        List<ObjectId> oldId = new List<ObjectId>();

        foreach (var obj in _objects.FindAll().OrderBy(z => z["p"])) {
          sTmp = obj["p"].AsString;
          if (oldT.Any(z => sTmp.StartsWith(z))) {
            oldId.Add(obj["_id"]);
            continue;  // skip load, old version
          }
          t = Topic.I.Get(Topic.root, sTmp, true, Owner, false, false);
          a = new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null };
          // check version
          {
            jTmp = t.GetField("version");

            if ((sTmp = jTmp.AsString(null)) != null && sTmp.StartsWith("¤VR") && Version.TryParse(sTmp.Substring(3), out Version vRepo)) {
              jTmp = a.jm["version"];
              if ((sTmp = jTmp.AsString(null)) == null || !sTmp.StartsWith("¤VR") || !Version.TryParse(sTmp.Substring(3), out Version vDB) || vRepo > vDB) {
                oldT.Add(t.path + "/");
                oldId.Add(a.id);
                continue; // skip load, old version
              }
            }
          }
          // check attribute
          JSC.JSValue attr;
          if (!a.jm.IsObject() || !(attr = a.jm["attr"]).IsNumber) {
            saved = false;
          } else {
            saved = ((int)attr & (int)Topic.Attribute.Saved) == (int)Topic.Attribute.DB;
          }

          if (a.bs != null) {
            if (saved) {
              a.js = Bs2Js(a.bs["v"]);
            } else {
              _states.Delete(obj["_id"]);
              a.bs = null;
            }
          }
          _base.Add(t, a);
          Topic.I.Fill(t, a.js, a.jm, Owner);
        }
        oldT.Clear();
        foreach (var id in oldId) {
          _states.Delete(id);
          _objects.Delete(id);
        }
        oldId.Clear();
      } else {
        _objects.EnsureIndex("p", true);
      }
    }

    private static void ImportDefault() {
      var assembly = typeof(Repo).Assembly;
      using (var rs = assembly.GetManifestResourceStream("X13.Repository.base.xst")) {
        using (var reader = new StreamReader(rs)) {
          Log.Info("Import base.xst");
          Repo.Import(reader, null);
        }
      }
    }

    private void Save(Perform p) {
      Topic t = p.src;
      Stash a;
      JSC.JSValue jTmp;
      bool saveM = false, saveS = false;
      if (!_base.TryGetValue(t, out a)) {
        if (p.Art == Perform.E_Art.remove) {
          return;
        }
        var obj = _objects.FindOne(Query.EQ("p", t.path));
        a = obj != null ? new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null } : new Stash { id = ObjectId.NewObjectId() };
        _base[t] = a;
      }

      if (p.Art == Perform.E_Art.remove) {
        _states.Delete(a.id);
        _objects.Delete(a.id);
        _base.Remove(t);
      } else {   //create, changedField, changedState, move
        // Manifest
        jTmp = t.GetField(null);
        if (!object.ReferenceEquals(jTmp, a.jm)) {
          if (a.bm == null) {
            a.bm = new BsonDocument {
              ["_id"] = a.id,
              ["p"] = t.path
            };
          }
          a.bm["v"] = Js2Bs(jTmp);
          a.jm = jTmp;
          saveM = true;
        }
        // State
        if (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
          saveS = true;
        } else if (a.bs != null) {
          _states.Delete(a.id);
          a.bs = null;
          saveS = false;
        }
        if (saveS) {
          jTmp = t.GetState();
          if (!object.ReferenceEquals(jTmp, a.js)) {
            if (a.bs == null) {
              a.bs = new BsonDocument {
                ["_id"] = a.id
              };
            }
            a.bs["v"] = Js2Bs(jTmp);
            a.js = jTmp;
          } else {
            saveS = false;
          }
        }

        if (p.Art == Perform.E_Art.move) {
          a.bm["p"] = t.path;
          saveM = true;
        }
        if (saveM) {
          _objects.Upsert(a.bm);
        }
        if (saveS && a.bs != null) {
          _states.Upsert(a.bs);
        }
      }
    }
    /// <summary>Creates the backup-directory setting if it is not there yet.</summary>
    /// <remarks>Config, Required and Readonly as before: the owner may retarget it, the tree keeps
    /// it, and nothing else writes it.</remarks>
    private void SeedBackupDir(string dir) {
      Topic bakT;
      if (Owner.Exist("bak", out bakT)) {
        return;
      }
      bakT = Owner.Get("bak", true, Owner);
      bakT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
      bakT.SetState(dir);
    }

    /// <summary>Where backups go, with the data directory as the fallback.</summary>
    /// <remarks>AsString and not ToString(): on an undefined state ToString() returns the four
    /// letters "undefined", which is a perfectly valid relative path as far as Windows is
    /// concerned, so the failure surfaced only as a DirectoryNotFoundException at File.Copy - by
    /// which point Backup had already closed the database. Same family as the As&lt;string&gt;()
    /// defects cleared on 21.08; this site was missed because it does not go through As&lt;string&gt;().
    /// <para>The directory is created here rather than assumed, so a setting pointing somewhere
    /// that does not exist yet is not fatal either.</para></remarks>
    private string BackupDir(string fallback) {
      var bak_dir = Owner.Get("bak", true, Owner).GetState().AsString(null);
      if (string.IsNullOrEmpty(bak_dir)) {
        Log.Warning("PersistentStorage.bak is not set, backing up into {0}", fallback);
        bak_dir = fallback;
      }
      if (!Directory.Exists(bak_dir)) {
        Directory.CreateDirectory(bak_dir);
      }
      return bak_dir;
    }

    /// <summary>Deletes the copy Rebuild leaves behind.</summary>
    /// <remarks>LiteDB.Rebuild renames the old file to "&lt;name&gt;-backup.ldb" and never removes it;
    /// a later rebuild finding that name taken writes "-backup-1", then "-backup-2". Nothing ever
    /// collects them, and Backup() rebuilds once a night, so this is one orphan a day for as long as
    /// the server runs - about 470 MB a year at the live persist.ldb size. Found by an overnight run,
    /// not by reading, and the same defect had already been fixed in Archivist for the sample files.
    /// <para>Distinct from the dated .bak this method creates on purpose: that one is the backup,
    /// pruned by the retention pass below. This is the debris of rewriting the live file.</para>
    /// <para>Deleted rather than kept: the rebuild has completed and the database is open again by
    /// the time this runs, so what is left is a copy of a file already known to be good. Failing to
    /// delete is not worth failing the backup over - the next pass will get it.</para></remarks>
    private static void DropRebuildLeftovers() {
      string dir = Path.GetDirectoryName(Path.GetFullPath(DB_PATH));
      string stem = Path.GetFileNameWithoutExtension(DB_PATH);
      try {
        foreach(var f in Directory.GetFiles(dir, stem + "-backup*" + Path.GetExtension(DB_PATH))) {
          try {
            File.Delete(f);
          }
          catch(IOException) {
          }
        }
      }
      catch(IOException ex) {
        Log.Warning("PersistentStorage could not list rebuild leftovers in {0} - {1}", dir, ex.Message);
      }
    }

    private void Backup() {
      _history.DeleteMany(Query.LT("t", DateTime.Now.AddDays(-36)));
      // Resolved BEFORE anything is closed. Everything between the close and the reopen runs under
      // a database that does not exist, so the less that happens there the better - and a bad
      // setting now fails while the store is still up and usable.
      var bak_dir = BackupDir(Path.GetDirectoryName(DB_PATH));
      string fb = bak_dir + (new string(Path.DirectorySeparatorChar, 1)) + DateTime.Now.ToString("yyMMdd_HHmmss") + ".bak";

      var db = Interlocked.Exchange(ref _db, null);
      if (db != null) {
        db.Commit();
        _history = null;
        _objects = null;
        _states = null;
        db.Checkpoint();
        db.Dispose();
      }
      // The reopen is in a finally because it has to happen even when the copy throws. It did not
      // use to: a failing copy left _db null for good, the next loop pass raised a
      // NullReferenceException on _db.BeginTrans(), and that killed the storage thread - after
      // which the server ran on with nothing being persisted at all, and said so only in the log.
      try {
        File.Copy(DB_PATH, fb);
        Log.Info("backup {0} created", fb);
      }
      finally {
        _db = new LiteDatabase(new ConnectionString { Upgrade = true, Filename = DB_PATH }) { CheckpointSize = 50 };
        _db.Rebuild();
        DropRebuildLeftovers();
        _objects = _db.GetCollection<BsonDocument>("objects");
        _states = _db.GetCollection<BsonDocument>("states");
        _history = _db.GetCollection<BsonDocument>("history");
      }

      // Per file, not per pass. One undeletable backup - held open by a copy in progress, or by a
      // virus scanner - used to abort the whole retention sweep silently, so everything older than
      // it stayed forever and nothing said why.
      try {
        DateTime now = DateTime.Now, fdt;
        foreach (string f in Directory.GetFiles(bak_dir, "??????_??????.bak", SearchOption.TopDirectoryOnly)) {
          fdt = File.GetLastWriteTime(f);
          if (fdt.AddDays(7) > now || (fdt.DayOfWeek == DayOfWeek.Thursday && fdt.Hour == 3 && (fdt.AddMonths(1) > now || (fdt.AddMonths(6) > now && fdt.Day < 8)))) {
            continue;
          }
          try {
            File.Delete(f);
            Log.Info("backup {0} deleted", Path.GetFileName(f));
          }
          catch (IOException ex) {
            Log.Warning("backup {0} not deleted - {1}", Path.GetFileName(f), ex.Message);
          }
          catch (UnauthorizedAccessException ex) {
            Log.Warning("backup {0} not deleted - {1}", Path.GetFileName(f), ex.Message);
          }
        }
      }
      catch (IOException ex) {
        // Listing the directory failed, which is not the same as one file refusing to go.
        Log.Warning("backup retention could not list {0} - {1}", bak_dir, ex.Message);
      }
    }
    #endregion Persisten Storage Members

    #region History
    private void Log_Write(LogLevel ll, DateTime dt, string msg, bool local) {
      if (_history != null && ll != LogLevel.Debug) {
        var d = new BsonDocument {
          ["_id"] = ObjectId.NewObjectId(),
          ["t"] = new BsonValue(dt.ToUniversalTime()),
          ["l"] = new BsonValue((int)ll),
          ["m"] = new BsonValue(msg)
        };
        _history.Insert(d);
      }
    }
    private IEnumerable<Log.LogRecord> History(DateTime dt, int cnt) {
      var t = new BsonValue(dt);
      return _history.Query().Where(z => z["t"] < t).OrderByDescending(z => z["t"]).Limit(cnt).ToEnumerable()
        //Find(Query.And(Query.All("t", Query.Descending), Query.LT("t", t)), 0, cnt)
        .Select(z => new Log.LogRecord {
          dt = z["t"].AsDateTime,
          ll = (LogLevel)z["l"].AsInt32,
          format = z["m"].AsString,
          args = null
        });
    }
    #endregion History

  }
}
