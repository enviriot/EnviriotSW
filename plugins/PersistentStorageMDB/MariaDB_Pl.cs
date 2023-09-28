///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JSL = NiL.JS.BaseLibrary;
using JSC = NiL.JS.Core;
using NiL.JS.Extensions;
using X13.Repository;
using MySqlConnector;

namespace X13.PersistentStorage {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "MariaDB")]
  internal class MariaDB_Pl : PersistentStorageBase, IPlugModul {
    private const string DB_NAME = "Enviriot";
    public MariaDB_Pl() : base("MariaDB") {
      //_archLst = new List<ArchLog>();
      //JsExtLib.AQuery = this.AQuery;
    }

    //#region Persisten Storage Members

    private MySqlConnection _db;

    protected override void ThreadM() {
      /*
      backupDT = DateTime.Now.AddDays(1).Date.AddHours(3.25);
      backupDT_Arch = DateTime.Now.AddDays(1).Date.AddHours(3.5);
      do {
        if(_tick.WaitOne(15)) {
          _db.BeginTrans();
          while(_q.TryDequeue(out Perform p)) {
            try {
              Save(p);
            }
            catch(Exception ex) {
              Log.Warning("PersistentStorage(" + (p == null ? "null" : p.ToString()) + ") - " + ex.ToString());
            }
          }
          _db.Commit();
        } else if(backupDT < DateTime.Now) {
          backupDT = DateTime.Now.AddDays(1).Date.AddHours(3.3);
          Log.Info("Backup started");
          try {
            Backup();
            Log.Info("Backup finished");
          }
          catch(Exception ex) {
            Log.Warning("Backup failed - " + ex.ToString());
          }
        } else if(backupDT_Arch < DateTime.Now) {
          backupDT_Arch = DateTime.Now.AddDays(1).Date.AddHours(3.5);
          try {
            CompressArch();
          }
          catch(Exception ex) {
            Log.Warning("ShrinkArch failed - " + ex.ToString());
          }
        } else {
          try {
            OptimizeArch();
          }
          catch(Exception ex) {
            Log.Warning("OptimizeArch() - " + ex.ToString());
          }
        }
      } while(!_terminate);
      var dba = Interlocked.Exchange(ref _dba, null);
      if(dba != null) {
        try {
          dba.Commit();
          dba.Checkpoint();
        }
        catch(Exception ex) {
          Log.Warning("PersistenStorage.Arch.Terminate - {0}", ex);
        }
        dba.Dispose();
      }
      var db = Interlocked.Exchange(ref _db, null);
      if(db != null) {
        try {
          db.Commit();
          db.Checkpoint();
        }
        catch(Exception ex) {
          Log.Warning("PersistenStorage.DB.Terminate - {0}", ex);
        }
        db.Dispose();
      }*/
    }
    protected override void Load() {
      string sUrl = "tcp://user:pa$w0rd@localhost/";
      var tUrl = _owner.Get("url", true, _owner);
      if(tUrl.GetState().ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(tUrl.GetState().As<string>())) {
        tUrl.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
        tUrl.SetState(sUrl);
      } else {
        sUrl = tUrl.GetState().As<string>();
      }
      var uri = new Uri(sUrl);
      var builder = new MySqlConnectionStringBuilder { Server = uri.DnsSafeHost };
      if(!string.IsNullOrEmpty(uri.UserInfo)) {
        var items = uri.UserInfo.Split(new[] { ':' });
        if(items.Length > 1 ) {
          builder.UserID = items[0];
          builder.Password = items[1];
        } else {
          builder.UserID = uri.UserInfo;
        }
      }
      if(uri.Port>0) {
        builder.Port = (uint)uri.Port;
      }
      _db = new MySqlConnection(builder.ConnectionString);
      _db.Open();
      bool exist;
      using(var cmd =  _db.CreateCommand()) {
        cmd.CommandText = "SELECT count(*) FROM INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = @name;";
        cmd.Parameters.AddWithValue("name", DB_NAME);
        exist = 1 == (long)cmd.ExecuteScalar();
      }
      if(!exist) {
        using(var cmd = _db.CreateCommand()) {
          cmd.CommandText = "CREATE DATABASE "+ DB_NAME + ";";
          cmd.ExecuteNonQuery();
        }
      }
      _db.Close();
      builder.Database = DB_NAME;
      _db = new MySqlConnection(builder.ConnectionString);
      _db.Open();
      if(!exist) {
        using(var batch = _db.CreateBatch()) {
          batch.BatchCommands.Add(new MySqlBatchCommand("CREATE TABLE PS(id INT AUTO_INCREMENT PRIMARY KEY, p TEXT NOT NULL, m TEXT, s TEXT);"));
          batch.BatchCommands.Add(new MySqlBatchCommand("CREATE TABLE ARCH (id int NOT NULL AUTO_INCREMENT, p int NOT NULL, dt datetime NOT NULL, v double NOT NULL, PRIMARY KEY (id), KEY ARCH_FK (p), KEY ARCH_dt_IDX (dt) USING BTREE, CONSTRAINT ARCH_FK FOREIGN KEY (p) REFERENCES PS(id) ON DELETE CASCADE)"));
          batch.BatchCommands.Add(new MySqlBatchCommand("CREATE TABLE ARCH_W(p INT PRIMARY KEY, dt1 DATETIME, dt2 DATETIME, CONSTRAINT ARCH_W_FK FOREIGN KEY (p) REFERENCES PS(id) ON DELETE CASCADE ON UPDATE RESTRICT);"));
          batch.BatchCommands.Add(new MySqlBatchCommand("CREATE TRIGGER ARCH_DATA AFTER UPDATE ON PS FOR EACH ROW BEGIN IF (NEW.s != OLD.s AND JSON_VALUE(NEW.m, '$.Arch.enable') AND (JSON_TYPE(NEW.s) = 'INTEGER' OR JSON_TYPE(NEW.s) = 'DOUBLE')) THEN INSERT INTO ARCH (p, dt, v) VALUES (NEW.id, NOW(), JSON_VALUE(NEW.s, '$')); END IF; END"));
          batch.ExecuteNonQuery();
        }
      }

      /*
      bool exist = File.Exists(DB_PATH);
      _base = new SortedDictionary<Topic, Stash>();
      _db = new LiteDatabase(new ConnectionString { Upgrade = true, Filename = DB_PATH }) { CheckpointSize = 50 };
      bool exist_h = exist && _db.CollectionExists("history");
      _history = _db.GetCollection<BsonDocument>("history");
      if(!exist_h) {
        _history.EnsureIndex("t");
      }
      Log.History = History;
      Log.Write += Log_Write;

      exist = exist && _db.CollectionExists("objects");
      _objects = _db.GetCollection<BsonDocument>("objects");
      _states = _db.GetCollection<BsonDocument>("states");

      if(!exist) {
        _objects.EnsureIndex("p", true);
      } else {
        Topic t;
        Stash a;
        JSC.JSValue jTmp;
        bool saved;
        string sTmp;
        List<string> oldT = new List<string>();
        List<ObjectId> oldId = new List<ObjectId>();

        foreach(var obj in _objects.FindAll().OrderBy(z => z["p"])) {
          sTmp = obj["p"].AsString;
          if(oldT.Any(z => sTmp.StartsWith(z))) {
            oldId.Add(obj["_id"]);
            continue;  // skip load, old version
          }
          t = Topic.I.Get(Topic.root, sTmp, true, _owner, false, false);
          a = new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null };
          // check version
          {
            jTmp = t.GetField("version");

            if(jTmp.ValueType == JSC.JSValueType.String && (sTmp = jTmp.Value as string) != null && sTmp.StartsWith("¤VR") && Version.TryParse(sTmp.Substring(3), out Version vRepo)) {
              jTmp = a.jm["version"];
              if(jTmp.ValueType != JSC.JSValueType.String || (sTmp = jTmp.Value as string) == null || !sTmp.StartsWith("¤VR") || !Version.TryParse(sTmp.Substring(3), out Version vDB) || vRepo > vDB) {
                oldT.Add(t.path + "/");
                oldId.Add(a.id);
                continue; // skip load, old version
              }
            }
          }
          // check attribute
          JSC.JSValue attr;
          if(a.jm == null || a.jm.ValueType != JSC.JSValueType.Object || a.jm.Value == null || !(attr = a.jm["attr"]).IsNumber) {
            saved = false;
          } else {
            saved = ((int)attr & (int)Topic.Attribute.Saved) == (int)Topic.Attribute.DB;
          }

          if(a.bs != null) {
            if(saved) {
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
        foreach(var id in oldId) {
          _states.Delete(id);
          _objects.Delete(id);
        }
        oldId.Clear();
      }

      exist = File.Exists(DBA_PATH);
      _dba = new LiteDatabase(new ConnectionString { Upgrade = true, Filename = DBA_PATH }) { CheckpointSize = 100 };
      if(exist && !_dba.CollectionExists("archive")) {
        exist = false;
      }
      _archive = _dba.GetCollection<BsonDocument>("archive");
      if(!exist) {
        _archive.EnsureIndex("t", false);
        _archive.EnsureIndex("p", false);
      }
      if(exist && !_dba.CollectionExists("archLog")) {
        exist = false;
      }
      _archLog = _dba.GetCollection<ArchLog>("archLog");
      if(!exist) {
        _archLog.EnsureIndex("p", false);
      }*/
    }
    /*
    private void Save(Perform p) {
      Topic t = p.src;
      Stash a;
      JSC.JSValue jTmp;
      bool saveM = false, saveS = false, saveA;
      if(!_base.TryGetValue(t, out a)) {
        if(p.Art == Perform.E_Art.remove) {
          return;
        }
        var obj = _objects.FindOne(Query.EQ("p", t.path));
        a = obj != null ? new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null } : new Stash { id = ObjectId.NewObjectId() };
        _base[t] = a;
      }

      if(p.Art == Perform.E_Art.remove) {
        _states.Delete(a.id);
        _objects.Delete(a.id);
        _base.Remove(t);
      } else {   //create, changedField, changedState, move
        // Manifest
        jTmp = t.GetField(null);
        if(!object.ReferenceEquals(jTmp, a.jm)) {
          if(a.bm == null) {
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

        if(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
          saveS = true;
        } else if(a.bs != null) {
          _states.Delete(a.id);
          a.bs = null;
          saveS = false;
        }
        if(saveS || saveA) {
          jTmp = t.GetState();
          if(!object.ReferenceEquals(jTmp, a.js)) {
            if(a.bs == null) {
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

        if(p.Art == Perform.E_Art.move) {
          a.bm["p"] = t.path;
          saveM = true;
        }
        if(saveM) {
          _objects.Upsert(a.bm);
        }
        if(saveS && a.bs != null) {
          _states.Upsert(a.bs);
        }
        if(saveA && ((a.js.ValueType == JSC.JSValueType.Double && !double.IsNaN(a.js.As<double>())) || a.js.ValueType == JSC.JSValueType.Integer)) {
          _dba.BeginTrans();
          ExistOrCreate(t);
          _archive.Insert(new BsonDocument { ["_id"] = ObjectId.NewObjectId(), ["t"] = new BsonValue(DateTime.Now), ["p"] = t.path, ["v"] = a.bs["v"] });
          _dba.Commit();
        }
      }
    }
    private void Backup() {
      _history.DeleteMany(Query.LT("t", DateTime.Now.AddDays(-36)));
      var db = Interlocked.Exchange(ref _db, null);
      if(db != null) {
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
        foreach(string f in Directory.GetFiles(Path.GetDirectoryName(DB_PATH), "??????_??????.bak", SearchOption.TopDirectoryOnly)) {
          fdt = File.GetLastWriteTime(f);
          if(fdt.AddDays(7) > now || (fdt.DayOfWeek == DayOfWeek.Thursday && fdt.Hour == 3 && (fdt.AddMonths(1) > now || (fdt.AddMonths(6) > now && fdt.Day < 8)))) {
            continue;
          }
          File.Delete(f);
          Log.Info("backup {0} deleted", Path.GetFileName(f));
        }
      }
      catch(System.IO.IOException) {
      }
    }
    #endregion Persisten Storage Members

    #region History
    private void Log_Write(LogLevel ll, DateTime dt, string msg, bool local) {
      if(_history != null && ll != LogLevel.Debug) {
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
      return null;
    }
    #endregion History

    #region Archivist
    private const string DBA_PATH = "../data/archive.ldb";
    private LiteDatabase _dba;
    private ILiteCollection<BsonDocument> _archive;
    private ILiteCollection<ArchLog> _archLog;
    private readonly List<ArchLog> _archLst;
    private int _archIdx;

    private class ArchLog {
      public const double ARCH_JITTER = 0.1; // 2:24
      public const double ARCH_JITTER2 = 10; // days

      public ArchLog(Topic t, DateTime ct, DateTime at) {
        Id = ObjectId.NewObjectId();
        Path = t.path;
        Ct = ct;
        At = at;
        topic = t;
      }
      [BsonCtor]
      public ArchLog(ObjectId _id, string p, DateTime ct, DateTime at) {
        Id = _id;
        Path = p;
        Ct = ct;
        At = at;
        topic = Topic.root.Get(Path, false);
      }
      public ObjectId Id { get; private set; }
      [BsonField("p")]
      public string Path { get; private set; }
      [BsonField("ct")]
      public DateTime Ct { get; set; }
      [BsonField("at")]
      public DateTime At { get; set; }
      [BsonIgnore]
      public readonly Topic topic;
      [BsonIgnore]
      public double Keep {
        get {
          if(topic == null) {
            return double.NaN;
          }
          var keep = topic.GetField("Arch.keep");
          double k_d;
          if((keep.ValueType != JSC.JSValueType.Double && keep.ValueType != JSC.JSValueType.Integer) || (k_d = keep.As<double>()) <= 0) {
            return ARCH_JITTER2;
          }
          return k_d;
        }
      }
    }

    private ArchLog ExistOrCreate(Topic t) {
      ArchLog al;
      if((al = _archLst.Find(z => z.topic == t)) == null) {
        al = _archLog.FindOne("$.p=@0", t.path);
        if(al == null) {
          var min = _archive.Query().Where("$.p=@0", t.path).OrderBy("$.t").FirstOrDefault();
          al = new ArchLog(t, DateTime.Now.AddDays(-1.5 * ArchLog.ARCH_JITTER), min == null ? DateTime.Now : min["t"].AsDateTime);
          _archLog.Insert(al);
        }
        _archLst.Add(al);
      }
      return al;
    }
    private JSL.Array AQuery(string[] topics, DateTime begin, int count, DateTime end) {
      //var sw = System.Diagnostics.Stopwatch.StartNew();
      var tba = topics.Select(z => new BsonValue(z)).ToArray();

      var rez = new JSL.Array();
      var p1 = new BsonValue(begin);
      var p2 = new BsonArray(tba);
      var p3 = new BsonValue(end);

      if(end <= begin || count == 0) {  // end == MinValue
        ILiteQueryable<BsonDocument> resp1;
        if(end > begin) {
          resp1 = _archive.Query().Where("$.t BETWEEN @0 AND @2 AND $.p IN @1", p1, p2, p3);
        } else {
          resp1 = _archive.Query().Where("$.t < @0 AND $.p IN @1", p1, p2);
        }
        if(count < 0) {
          resp1 = resp1.OrderByDescending("$.t");
        } else if(count >= 0) {
          resp1 = resp1.OrderBy("$.t");
        }
        var resp2 = count != 0 ? resp1.Limit(Math.Abs(count)).ToEnumerable() : resp1.ToEnumerable();
        JSL.Array lo = null;
        foreach(var li in resp2) {
          var p = li["p"];
          int i;
          for(i = 0; p != tba[i]; i++)
            ;
          i++;
          if(lo != null && lo[i].ValueType == JSC.JSValueType.Object && (li["t"].AsDateTime - ((JSL.Date)lo[0].Value).ToDateTime()).TotalSeconds < 15) {  // Null.ValueType==Object
            lo[i] = Bs2Js(li["v"]);
          } else {
            lo = new JSL.Array(tba.Length + 1) {
              [0] = Bs2Js(li["t"])
            };
            for(var j = 1; j <= tba.Length; j++) {
              lo[j] = (i == j) ? Bs2Js(li["v"]) : JSC.JSValue.Null;
            }
            rez.Add(lo);
          }
        }
      } else {
        var step = (end - begin).TotalSeconds / Math.Abs(count);

        DateTime cursor = begin.AddSeconds(step);
        var f_cnt = new int[tba.Length];
        var f_val = new double[tba.Length];
        var l_val = new double[tba.Length];
        var l_delta = new double[tba.Length];
        var t_cnt = 0;
        double t_sum = 0;
        int i;

        for(i = 0; i < tba.Length; i++) {
          f_val[i] = 0;
          f_cnt[i] = 0;
          l_delta[i] = -step;
          var p_i = tba[i];
          var r = _archive.Query().Where("$.t < @1 and $.p = @2", p1, p_i).OrderByDescending("$.t").FirstOrDefault();
          l_val[i] = r != null ? r["v"].AsDouble : double.NaN;
        }
        var resp = _archive.Query().Where("$.t BETWEEN @0 AND @2 AND $.p IN @1", p1, p2, p3).OrderBy("$.t").ToEnumerable();
        foreach(var li in resp) {
          var t_cur = li["t"].AsDateTime;
          if(t_cur >= cursor) {
            AddRecord();
            do {
              cursor = cursor.AddSeconds(step);
            } while(t_cur >= cursor);
          }
          var p = li["p"];
          for(i = 0; i < tba.Length; i++) {
            if(p == tba[i]) {
              var v = li["v"].AsDouble;
              if(!double.IsNaN(v)) {
                var td = (t_cur - cursor).TotalSeconds;
                if(!double.IsNaN(l_val[i])) {
                  f_val[i] += l_val[i] * (td - l_delta[i]) / step;
                  l_delta[i] = td;
                }
                f_cnt[i]++;
                l_val[i] = v;
                t_cnt++;
                t_sum += td;
              }
              break;
            }
          }
        }
        AddRecord();
        void AddRecord() {
          JSL.Array lo = new JSL.Array(tba.Length + 1) {
            [0] = JSC.JSValue.Marshal(cursor.AddSeconds(t_cnt == 1 ? t_sum : (-step / 2)).ToLocalTime()),
          };
          t_cnt = 0;
          t_sum = 0;
          for(i = 0; i < tba.Length; i++) {
            lo[i + 1] = f_cnt[i] > 0 ? new JSL.Number(f_cnt[i] == 1 ? l_val[i] : (f_val[i] + l_val[i] * (-l_delta[i]) / step)) : JSC.JSValue.Null;
            f_val[i] = 0;
            f_cnt[i] = 0;
            l_delta[i] = -step;
          }
          rez.Add(lo);
        }
      }
      //sw.Stop();
      //Log.Debug("AQuery([{0}], {1:yyMMdd'T'HHmmss}, {2}, {3:yyMMdd'T'HHmmss}) {4:0.0} mS", string.Join(", ", topics), begin, count, end, sw.Elapsed.TotalMilliseconds);
      return rez;

    }
    private void OptimizeArch() {
      if(_archIdx >= _archLst.Count) {
        _archIdx = 0;
      } else {
        var al = _archLst[_archIdx++];
        double k_d = al.Keep;
        if(k_d > ArchLog.ARCH_JITTER) {
          if(al.Ct < DateTime.Now.AddDays(-ArchLog.ARCH_JITTER)) {
            al.Ct = ArchCompact0(al.Path, al.Ct, 5.01);
            _archLog.Update(al);
          } else if(al.At < DateTime.Now.AddDays(-ArchLog.ARCH_JITTER2)) {
            al.At = ArchCompact2(al.Path, al.At.AddMinutes(-al.At.Minute).AddSeconds(-al.At.Second), 60);
            _archLog.Update(al);
          }
        } else {
          var k_gr = DateTime.Now.AddDays(-k_d);
          if(al.Ct < k_gr) {
            al.Ct = k_gr;
            _archive.DeleteMany("$.t < @0 AND $.p=@1", k_gr, al.Path);
          }
        }
      }
    }
    /// <summary>наивный вариант</summary>
    private DateTime ArchCompact0(string path, DateTime t0, double interval) {
      //Log.Debug("OptimizeArch({0}, {1}, {2})", path, t0.ToString(), interval);
      var nt = t0.AddMinutes(interval);
      var r = _archive.Query().Where("$.t>=@0 AND $.p=@1", t0, path).OrderBy("$.t").ExecuteReader();
      if(!r.Read()) {
        return DateTime.Now;
      }
      t0 = r.Current["t"].AsDateTime.ToLocalTime();
      if(t0 > nt) {
        r.Dispose();
        return t0;
      }
      var v0 = r.Current["v"].AsDouble;
      //Log.Debug(" ^ {1}, {2}", path, t0.ToLongTimeString(), v0);
      if(!r.Read()) {
        r.Dispose();
        return DateTime.Now;
      }
      var t1 = r.Current["t"].AsDateTime.ToLocalTime();
      if(t1 > nt) {
        r.Dispose();
        return t1;
      }
      var o1 = r.Current;
      DateTime t2;
      while(r.Read()) {
        t2 = r.Current["t"].AsDateTime.ToLocalTime();
        if(t2 > nt) {
          break;
        }
        var ve = v0 + ((o1["v"].AsDouble - v0) / (t1 - t0).TotalSeconds) * (t2 - t0).TotalSeconds;
        if(Math.Abs(r.Current["v"].AsDouble - ve) > Math.Abs(ve * 0.05)) {
          break;
        }
        //Log.Debug(" | {1}, {2}", path, t1.ToLongTimeString(), o1["v"].AsDouble);
        _archive.Delete(o1["_id"]);
        o1 = r.Current;
        t1 = t2;
      }
      //Log.Debug(" v {1}, {2}", path, t1.ToLongTimeString(), o1["v"].AsDouble);
      r.Dispose();
      return t1;
    }
    /// <summary>Линейная регрессия</summary>
    private DateTime ArchCompact1(string path, DateTime t0, double interval) {
      //Log.Debug("OptimizeArch({0}, {1}, {2})", path, t0.ToString(), interval);
      double sx = 0, sy = 0, sxy = 0, sxx = 0, say = 0;
      int n = 0;
      void Summs(double x, double y) {
        sx += x;
        sy += y;
        sxy += x * y;
        sxx += x * x;
        say += Math.Abs(y);
        n++;
      }
      DateTime t1 = DateTime.Now, t2, nt = t0.AddMinutes(interval);

      using(var r = _archive.Query().Where("$.t>=@0 AND $.p=@1", t0, path).OrderBy("$.t").ExecuteReader()) {
        if(!r.Read()) {
          return DateTime.Now;
        }
        t0 = r.Current["t"].AsDateTime.ToLocalTime();
        if(t0 > nt) {
          return t0;
        }
        Summs(0, r.Current["v"].AsDouble);
        //Log.Debug(" ^ {1}, {2}", path, t0.ToLongTimeString(), r.Current["v"].AsDouble);
        BsonValue o1 = null;
        double v2;

        while(r.Read()) {
          t2 = r.Current["t"].AsDateTime.ToLocalTime();
          if(t2 > nt) {
            if(o1 == null) {
              t1 = t2;
            }
            break;
          }
          v2 = r.Current["v"].AsDouble;
          Summs((t2 - t0).TotalSeconds, v2);
          if(o1 != null) {
            var det = sxx * n - sx * sx;                      // Линейная регрессия, Метод наименьших квадратов
            var k = (sxy * n - sx * sy) / det;
            var y0 = (sy - k * sx) / n;
            var ve = y0 + k * (t2 - t0).TotalSeconds;
            var err = (v2 - ve) * 100 * n / say;
            if(Math.Abs(err) > 5) {
              break;
            }
            //Log.Debug(" | {1}, {2}, {3:0.00}%", path, t1.ToLongTimeString(), o1["v"].AsDouble, err);
            _archive.Delete(o1["_id"]);
          }
          o1 = r.Current;
          t1 = t2;
        }
        //Log.Debug(" v {1}, {2}", path, t1.ToLongTimeString(), o1==null ? double.NaN : o1["v"].AsDouble);
      }
      return t1;
    }
    /// <summary>средневзвешенное за период</summary>
    private DateTime ArchCompact2(string path, DateTime bt, double interval) {
      var et = bt.AddMinutes(interval);
      var l_d = _archive.Query().Where("$.t < @1 and $.p = @2", bt, path).OrderByDescending("$.t").FirstOrDefault();
      var l_val = l_d != null ? l_d["v"].AsDouble : double.NaN;

      int f_cnt = 0;
      double f_val = 0;
      double l_delta = 0;

      var r_d = _archive.Query().Where("$.t BETWEEN @0 AND @2 AND $.p IN @1", bt, path, et).OrderBy("$.t").ToEnumerable();
      foreach(var li in r_d) {
        var t_cur = li["t"].AsDateTime;
        var v = li["v"].AsDouble;
        if(!double.IsNaN(v)) {
          var td = (t_cur - bt).TotalMinutes;
          if(!double.IsNaN(l_val)) {
            f_val += l_val * (td - l_delta) / interval;
            l_delta = td;
          }
          f_cnt++;
          l_val = v;
        }
        _archive.Delete(li["_id"]);
      }
      if(f_cnt > 0) {
        var val = f_cnt == 1 ? l_val : (f_val + l_val * (interval - l_delta) / interval);
        _archive.Insert(new BsonDocument {
          ["_id"] = ObjectId.NewObjectId(),
          ["t"] = new BsonValue(bt.AddMinutes(interval / 2)),
          ["p"] = path,
          ["v"] = val
        });
      }
      return et;
    }

    private void CompressArch() {
      _archLst.Clear();
      _archIdx = 0;
      DateTime dte = DateTime.Now.AddDays(1 - ArchLog.ARCH_JITTER2);
      foreach(var p in _archive.Query().GroupBy("p").Select("@key").ToArray().Select(z => z["expr"].AsString)) {
        if(!Topic.root.Exist(p, out var t) || t.disposed || !t.GetField("Arch.enable").As<bool>()) {
          _archive.DeleteMany("$.p=@0", p);
        } else {
          var al = ExistOrCreate(t);
          var k_d = al.Keep;
          if(k_d > ArchLog.ARCH_JITTER) {
            _archive.DeleteMany("$.t < @0 AND $.p=@1", DateTime.Now.AddDays(-k_d), t.path);
          }
        }
      }
      _dba.Commit();
      _dba.Checkpoint();
      _dba.Rebuild();
    }
    #endregion Archivist
    */
  }
}
