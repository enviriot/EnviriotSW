///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using LiteDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;
using System.IO;

namespace X13.PersistentStorage {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "PersistentStorage")]
  internal class PersistentStoragePl : IPlugModul {
    #region internal Members
    private LiteDatabase _db;
    private LiteCollection<BsonDocument> _objects, _states, _history;
    private Topic _owner;
    private System.Collections.Generic.SortedDictionary<Topic, Stash> _base;

    private static string EscapFieldName(string fn) {
      if(string.IsNullOrEmpty(fn)) {
        throw new ArgumentNullException("PersistentStorage.EscapFieldName()");
      }
      StringBuilder sb = new StringBuilder();

      for(var i = 0; i < fn.Length; i++) {
        var c = fn[i];

        if(char.IsLetterOrDigit(c) || (c == '$' && i == 0) || (c == '-' && i > 0)) {
          sb.Append(c);
        } else {
          sb.Append("_");
          sb.Append(((ushort)c).ToString("X4"));
        }
      }
      return sb.ToString();
    }
    private static string UnescapFieldName(string fn) {
      if(string.IsNullOrEmpty(fn)) {
        throw new ArgumentNullException("PersistentStorage.UnescapFieldName()");
      }
      StringBuilder sb = new StringBuilder();
      ushort cc;
      for(var i = 0; i < fn.Length; i++) {
        var c = fn[i];
        if(c == '_' && i + 4 < fn.Length && ushort.TryParse(fn.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out cc)) {
          i += 4;
          sb.Append((char)cc);
        } else {
          sb.Append(c);
        }
      }
      return sb.ToString();
    }
    private BsonValue Js2Bs(JSC.JSValue val) {
      if(val == null) {
        return BsonValue.Null;
      }
      switch(val.ValueType) {
      case JSC.JSValueType.NotExists:
      case JSC.JSValueType.NotExistsInObject:
      case JSC.JSValueType.Undefined:
        return BsonValue.Null;
      case JSC.JSValueType.Boolean:
        return new BsonValue((bool)val);
      case JSC.JSValueType.Date: {
          var jsd = val.Value as JSL.Date;
          if(jsd != null) {
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
          if(s != null && s.StartsWith("¤TR")) {
            var t = Topic.I.Get(Topic.root, s.Substring(3), false, null, false, false);
            if(t != null) {
              Stash tu;
              if(_base.TryGetValue(t, out tu)) {
                return tu.bm["_id"];
              }
            }
            throw new ArgumentException("TopicRefernce(" + s.Substring(3) + ") NOT FOUND");
          }
          return new BsonValue(s);
        }
      case JSC.JSValueType.Object:
        if(val.IsNull) {
          return BsonValue.Null;
        }
        var arr = val as JSL.Array;
        if(arr != null) {
          var r = new BsonArray();
          int i;
          foreach(var f in arr) {
            if(int.TryParse(f.Key, out i)) {
              while(i >= r.Count()) { r.Add(BsonValue.Null); }
              r[i] = Js2Bs(f.Value);
            }
          }
          return r;
        }
        ByteArray ba = val as ByteArray;
        if(ba != null || (ba = val.Value as ByteArray) != null) {
          return new BsonValue(ba.GetBytes());
        }
          {
          var r = new BsonDocument();
          foreach(var f in val) {
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
      if(d != null && (p = d["p"]) != null && p.IsString) {
        return p.AsString;
      }
      return null;
    }
    private JSC.JSValue Bs2Js(BsonValue val) {
      if(val == null) {
        return JSC.JSValue.Undefined;
      }
      switch(val.Type) {
      case BsonType.ObjectId: {
          var p = Id2Topic(val.AsObjectId);
          if(p != null) {
            return new JSL.String("¤TR" + p);
          } else {
            throw new ArgumentException("Unknown ObjectId: " + val.AsObjectId.ToString());
          }
        }
      case BsonType.Array: {
          var arr = val.AsArray;
          var r = new JSL.Array(arr.Count);
          for(int i = 0; i < arr.Count; i++) {
            if(!arr[i].IsNull) {
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
          foreach(var i in o) {
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
      if(p.art == Perform.Art.subscribe || p.art == Perform.Art.subAck || p.art == Perform.Art.setField || p.art == Perform.Art.setState || p.art == Perform.Art.unsubscribe || p.prim == _owner) {
        return;
      }
      Topic t = p.src;
      Stash a;
      JSC.JSValue jTmp;
      bool saveM = false, saveS = false;
      if(!_base.TryGetValue(t, out a)) {
        if(p.art == Perform.Art.remove) {
          return;
        }
        var obj = _objects.FindOne(Query.EQ("p", t.path));
        if(obj!=null) {
          a = new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null };
          Log.Warning("PersistentStorage. Reload "+t.path);
        } else {
          a = new Stash { id = ObjectId.NewObjectId() };
          Log.Warning("PersistentStorage. Create "+t.path);
        }
        _base[t] = a;
      }

      if(p.art == Perform.Art.remove) {
        _states.Delete(a.id);
        _objects.Delete(a.id);
        _base.Remove(t);
      } else {   //create, changedField, changedState, move
        // Manifest
        jTmp = t.GetField(null);
        if(!object.ReferenceEquals(jTmp, a.jm)) {
          if(a.bm == null) {
            a.bm = new BsonDocument();
            a.bm["_id"] = a.id;
            a.bm["p"] = t.path;
          }
          a.bm["v"] = Js2Bs(jTmp);
          a.jm = jTmp;
          saveM = true;
        }
        // State
        if(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
          jTmp = t.GetState();
          if(!object.ReferenceEquals(jTmp, a.js)) {
            if(a.bs == null) {
              a.bs = new BsonDocument();
              a.bs["_id"] = a.id;
            }
            a.bs["v"] = Js2Bs(jTmp);
            a.js = jTmp;
            saveS = true;
          }
        } else if(a.bs != null) {
          _states.Delete(a.id);
          a.bs = null;
          saveS = false;
        }

        if(p.art == Perform.Art.move) {
          a.bm["p"] = t.path;
          saveM = true;
        }
        if(saveM) {
          _objects.Upsert(a.bm);
        }
        if(saveS && a.bs != null) {
          _states.Upsert(a.bs);
        }
      }
    }
    #endregion internal Members

    public PersistentStoragePl() {
      Log.History = History;
      Log.Write += Log_Write;
    }

    #region History
    private void Log_Write(LogLevel ll, DateTime dt, string msg, bool local) {
      if(_history != null && ll != LogLevel.Debug) {
        var d = new BsonDocument();
        d["_id"] = ObjectId.NewObjectId();
        d["t"] = new BsonValue(dt.ToUniversalTime());
        d["l"] = new BsonValue((int)ll);
        d["m"] = new BsonValue(msg);
        _history.Insert(d);
      }
    }
    private IEnumerable<Log.LogRecord> History(DateTime dt, int cnt) {
      var t = new BsonValue(dt);
      return _history.Find(Query.And(Query.All("t", Query.Descending), Query.LT("t", t)), 0, cnt)
        .Select(z => new Log.LogRecord {
          dt = z["t"].AsDateTime,
          ll = (LogLevel)z["l"].AsInt32,
          format = z["m"].AsString,
          args = null
        });
    }
    #endregion History

    #region IPlugModul Members
    public void Init() {
      _owner = Topic.root.Get("/$YS/PersistentStorage", true);
    }
    public void Start() {
      bool exist = File.Exists("../data/persist.ldb");
      _base = new SortedDictionary<Topic, Stash>();
      _db = new LiteDatabase(new ConnectionString("Filename=../data/persist.ldb") { CacheSize = 500, Mode = LiteDB.FileMode.Exclusive });
      if(exist && !_db.GetCollectionNames().Any(z => z == "objects")) {
        exist = false;
      }
      _objects = _db.GetCollection<BsonDocument>("objects");
      _states = _db.GetCollection<BsonDocument>("states");
      _history = _db.GetCollection<BsonDocument>("history");
      if(!exist) {
        _objects.EnsureIndex("p", true);
        _history.EnsureIndex("t");
      } else {
        Topic t;
        Stash a;
        JSC.JSValue jTmp;
        bool saved;
        string sTmp;
        Version vRepo, vDB;
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

            if(jTmp.ValueType == JSC.JSValueType.String && (sTmp = jTmp.Value as string) != null && sTmp.StartsWith("¤VR") && Version.TryParse(sTmp.Substring(3), out vRepo)) {
              jTmp = a.jm["version"];
              if(jTmp.ValueType != JSC.JSValueType.String || (sTmp = jTmp.Value as string) == null || !sTmp.StartsWith("¤VR") || !Version.TryParse(sTmp.Substring(3), out vDB) || vRepo > vDB) {
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
      Topic.Subscribe(SubFunc);
    }
    public void Tick() {
    }
    public void Stop() {
      var db = Interlocked.Exchange(ref _db, null);
      if(db != null) {
        db.Dispose();
      }
    }

    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/PersistentStorage", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/PersistentStorage", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members

    #region Nested types
    private class Stash {
      public ObjectId id;
      public BsonDocument bm;
      public JSC.JSValue jm;
      public BsonDocument bs;
      public JSC.JSValue js;
    }
    #endregion Nested types
  }
}
