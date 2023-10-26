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

namespace X13.PersistentStorage {
  internal abstract class PersistentStorageBase : IPlugModul {
    private const string DISABLE_SIGN = "Disabled";
    private readonly string ENABLE_SIGN;

    protected Topic _owner;
    protected readonly System.Collections.Concurrent.ConcurrentQueue<Perform> _q;
    protected Thread _tr;
    protected bool _terminate;
    protected readonly AutoResetEvent _tick;
    protected PersistentStorageBase(string en) {
      ENABLE_SIGN = en;
      _tick = new AutoResetEvent(false);
      _q = new System.Collections.Concurrent.ConcurrentQueue<Perform>();
      JsExtLib.AQuery = this.AQuery;
    }

    #region IPlugModul Members
    public void Init() {
      _owner = Topic.root.Get("/$YS/PersistentStorage", true);
      var dir = Path.GetDirectoryName(DB_PATH);
      if (!Directory.Exists(dir)) {
        Directory.CreateDirectory(dir);
      }
      bool exist = File.Exists(DB_PATH);
      if (exist) {
        string fb = dir + (new string(Path.DirectorySeparatorChar, 1)) + DateTime.Now.ToString("yyMMdd_HHmmss") + ".bak";
        File.Copy(DB_PATH, fb);
        Log.Info("backup {0} created", fb);
      }
      _base = new SortedDictionary<Topic, Stash>();
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
      Topic.Subscribe(SubFunc);
      if(_db.UserVersion < 3) {
        _db.UserVersion = 3;
        ImportDefault();
      }
    }
    public void Tick() {
      if(_q.Any()) {
        _tick.Set();
      }
    }
    public void Stop() {
      _terminate = true;
      _tick.Set();
      if(!_tr.Join(5000)) {
        _tr.Abort();
      }
      //Interlocked.Exchange(ref _db, null)?.Dispose();
      _tick.Dispose();
    }
    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/PersistentStorage", true);
        if(en.GetState().ValueType == JSC.JSValueType.Boolean) {
          var r = (bool)en.GetState();
          en.SetState(r ? ENABLE_SIGN : DISABLE_SIGN);
          return r;
        } else if(en.GetState().ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(en.GetState().As<string>())) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(ENABLE_SIGN);
          return true;
        }
        return en.GetState().As<string>() == ENABLE_SIGN;
      }
      set {
        var en = Topic.root.Get("/$YS/PersistentStorage", true);
        en.SetState(value ? ENABLE_SIGN : DISABLE_SIGN);
      }
    }
    #endregion IPlugModul Members

    #region Persisten Storage Members
    private const string DB_PATH = "../data/persist.ldb";

    private LiteDatabase _db;
    private ILiteCollection<BsonDocument> _objects, _states, _history;
    private SortedDictionary<Topic, Stash> _base;

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
    protected BsonValue Js2Bs(JSC.JSValue val) {
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
            var s = val.Value as string;
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
          ByteArray ba = val as ByteArray;
          if (ba != null || (ba = val.Value as ByteArray) != null) {
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
    protected JSC.JSValue Bs2Js(BsonValue val) {
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
          return JSC.JSValue.Marshal(val.AsDateTime.ToLocalTime());
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
      if (p.Art == Perform.E_Art.subscribe || p.Art == Perform.E_Art.subAck || p.Art == Perform.E_Art.setField || p.Art == Perform.E_Art.setState || p.Art == Perform.E_Art.unsubscribe || p.Prim == _owner) {
        return;
      }
      _q.Enqueue(p);
    }

    private void ThreadM() {
      Load();
      OpenOrCreateArch();
      _tick.Set();

      DateTime backupDT;
      backupDT = DateTime.Now.AddDays(1).Date.AddHours(3.25);
      do {
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
        } else {
          IdleTaskArch();
        }
      } while (!_terminate);
      CloseArch();
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
          t = Topic.I.Get(Topic.root, sTmp, true, _owner, false, false);
          a = new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null };
          // check version
          {
            jTmp = t.GetField("version");

            if (jTmp.ValueType == JSC.JSValueType.String && (sTmp = jTmp.Value as string) != null && sTmp.StartsWith("¤VR") && Version.TryParse(sTmp.Substring(3), out Version vRepo)) {
              jTmp = a.jm["version"];
              if (jTmp.ValueType != JSC.JSValueType.String || (sTmp = jTmp.Value as string) == null || !sTmp.StartsWith("¤VR") || !Version.TryParse(sTmp.Substring(3), out Version vDB) || vRepo > vDB) {
                oldT.Add(t.path + "/");
                oldId.Add(a.id);
                continue; // skip load, old version
              }
            }
          }
          // check attribute
          JSC.JSValue attr;
          if (a.jm == null || a.jm.ValueType != JSC.JSValueType.Object || a.jm.Value == null || !(attr = a.jm["attr"]).IsNumber) {
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
          Topic.I.Fill(t, a.js, a.jm, _owner);
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
      bool saveM = false, saveS = false, saveA;
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
        saveA = p.Art == Perform.E_Art.changedState && t.GetField("Arch.enable").As<bool>();

        if (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
          saveS = true;
        } else if (a.bs != null) {
          _states.Delete(a.id);
          a.bs = null;
          saveS = false;
        }
        if (saveS || saveA) {
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
            saveA = false;
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
        if (saveA && ((a.js.ValueType == JSC.JSValueType.Double && !double.IsNaN(a.js.As<double>())) || a.js.ValueType == JSC.JSValueType.Integer)) {
          SaveArch(t);
        }
      }
    }
    private void Backup() {
      _history.DeleteMany(Query.LT("t", DateTime.Now.AddDays(-36)));
      var db = Interlocked.Exchange(ref _db, null);
      if (db != null) {
        db.Commit();
        _history = null;
        _objects = null;
        _states = null;
        db.Checkpoint();
        db.Dispose();
      }
      string fb = "../data/" + DateTime.Now.ToString("yyMMdd_HHmmss") + ".bak";
      File.Copy(DB_PATH, fb);
      Log.Info("backup {0} created", fb);
      _db = new LiteDatabase(new ConnectionString { Upgrade = true, Filename = DB_PATH }) { CheckpointSize = 50 };
      _db.Rebuild();
      _objects = _db.GetCollection<BsonDocument>("objects");
      _states = _db.GetCollection<BsonDocument>("states");
      _history = _db.GetCollection<BsonDocument>("history");

      try {
        DateTime now = DateTime.Now, fdt;
        foreach (string f in Directory.GetFiles(Path.GetDirectoryName(DB_PATH), "??????_??????.bak", SearchOption.TopDirectoryOnly)) {
          fdt = File.GetLastWriteTime(f);
          if (fdt.AddDays(7) > now || (fdt.DayOfWeek == DayOfWeek.Thursday && fdt.Hour == 3 && (fdt.AddMonths(1) > now || (fdt.AddMonths(6) > now && fdt.Day < 8)))) {
            continue;
          }
          File.Delete(f);
          Log.Info("backup {0} deleted", Path.GetFileName(f));
        }
      }
      catch (System.IO.IOException) {
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

    #region Archivist
    protected abstract void OpenOrCreateArch();
    protected abstract void SaveArch(Topic t);
    protected abstract JSL.Array AQuery(string[] topics, DateTime begin, int count, DateTime end);
    protected abstract void IdleTaskArch();
    protected abstract void CloseArch();
    #endregion Archivist
  }
}
