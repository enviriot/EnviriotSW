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
  /// <summary>Сохраняет манифесты и состояния в persist.ldb и восстанавливает дерево при запуске.</summary>
  /// <remarks>Историю значений обслуживает отдельный плагин Archivist.
  /// Здесь хранятся последние сохраняемые состояния, а не последовательность их изменений.</remarks>
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 2)]
  [System.ComponentModel.Composition.ExportMetadata("name", "LiteDB")]
  internal class LiteDB_Pl : IPlugModul {
    private const string OWNER_PATH = "/$YS/PersistentStorage";
    private Topic _owner;
    private IDisposable _allSub;
    private readonly System.Collections.Concurrent.ConcurrentQueue<TopicEvent> _q;
    private Thread _tr;
    private volatile bool _terminate;
    /// <summary>Ошибка загрузки базы, передаваемая из рабочего потока в Start().</summary>
    /// <remarks>Записывается до _loaded.Set() и читается после _loaded.WaitOne().
    /// Start() сообщает хосту об отказе плагина через исключение.</remarks>
    private volatile Exception _startErr;
    /// <summary>Есть работа в очереди, либо пора завершаться.</summary>
    private readonly AutoResetEvent _tick;
    private readonly ManualResetEvent _loaded;
    public LiteDB_Pl() {
      _tick = new AutoResetEvent(false);
      _loaded = new ManualResetEvent(false);
      _q = new System.Collections.Concurrent.ConcurrentQueue<TopicEvent>();
    }

    #region IPlugModul Members
    public void Init() {
      var dir = Path.GetDirectoryName(DB_PATH);
      if (!Directory.Exists(dir)) {
        Directory.CreateDirectory(dir);
      }
      bool exist = File.Exists(DB_PATH);
      // Настройка нужна и для новой базы: первое ночное копирование не должно зависеть
      // от того, существовал ли файл при запуске.
      SeedBackupDir(dir);
      if (exist) {
        string bak_dir = BackupDir(dir);
        string fb = bak_dir + (new string(Path.DirectorySeparatorChar, 1)) + DateTime.Now.ToString("yyMMdd_HHmmss") + ".bak";
        File.Copy(DB_PATH, fb);
        Log.Info("backup {0} created", fb);
      }
      _base = new Dictionary<Topic, Stash>();
      _db = new LiteDatabase(new ConnectionString { Upgrade = true, Filename = DB_PATH }) { CheckpointSize = 50 };
      // Прежняя история удаляется только после стартовой копии; новые записи принадлежат Log.
      _db.DropCollection("history");
    }
    public void Start() {
      _terminate = false;
      _tr = new Thread(new ThreadStart(ThreadM)) {
        IsBackground = true,
        Name = "PersistentStorage",
        Priority = ThreadPriority.BelowNormal
      };
      _tr.Start();
      _loaded.WaitOne();  // Ожидаем завершения попытки загрузки, затем проверяем ошибку.
      var err = _startErr;
      if(err != null) {
        throw new InvalidOperationException("PersistentStorage.Load(" + DB_PATH + ") failed", err);
      }
      _allSub = Topic.Subscribe(SubFunc);
      // UserVersion разрешает импорт ресурса, а ver элемента отдельно ограничивает замену
      // установленного поддерева в Xst.Prepare. Обновлять нужно оба уровня.
      // TODO: заменить ручной порог UserVersion отпечатком ресурса. Это не отменит проверку ver
      // и потребует повторного прохода импорта для существующих баз.
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
    /// <summary>Отключает входы плагина перед остановкой рабочего потока и закрытием базы.</summary>
    /// <remarks>Сначала снимается подписка Repository. Ожидание рабочего потока
    /// не является гарантией опустошения очереди событий.</remarks>
    public void Stop() {
      IDisposable allSub = _allSub;
      _allSub = null;
      allSub?.Dispose();

      _terminate = true;
      _tick.Set();
      if(_tr != null && !_tr.Join(5000)) {
        // Не прерываем фоновый поток посреди операции LiteDB и не освобождаем используемые им
        // ресурсы. При выходе ThreadM сам закроет базу; IsBackground не удерживает процесс.
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
      _loaded.Dispose();
    }
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
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
    private ILiteCollection<BsonDocument> _objects, _states;
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
        case JSC.JSValueType.String:
          return new BsonValue(val.AsString(null));
        case JSC.JSValueType.Object:
          if (val.IsNull) {
            return BsonValue.Null;
          }
          var arr = val as JSL.Array ?? val.Value as JSL.Array;
          if (arr != null) {
            var r = new BsonArray();
            foreach (var f in arr) {
              if (int.TryParse(f.Key, out int i)) {
                while (i >= r.Count()) { r.Add(BsonValue.Null); }
                r[i] = Js2Bs(f.Value);
              }
            }
            return r;
          }
          // Прямое и обёрнутое представления проверяет общий помощник ByteArray.IsByteArray.
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
    private JSC.JSValue Bs2Js(BsonValue val) {
      if (val == null) {
        return JSC.JSValue.Undefined;
      }
      switch (val.Type) { //-V3002
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
    private void SubFunc(TopicEvent p) {
      // Snapshot и Ready относятся к подписке, а не к новому изменению дерева.
      // Собственные изменения при восстановлении не нужно записывать обратно.
      if (p.Kind == EventKind.Snapshot || p.Kind == EventKind.Ready || p.Author == Owner) return;
      _q.Enqueue(p);
    }

    private void ThreadM() {
      try {
        Load();
      }
      catch(Exception ex) {
        _startErr = ex;
        _terminate = true;
      }
      finally {
        _loaded.Set();
      }

      DateTime backupDT;
      backupDT = DateTime.Now.AddDays(1).Date.AddHours(3.25);
      while (!_terminate) {
        // Обрабатываем также сбои вне Save, чтобы ошибка транзакции или обслуживания
        // не завершала рабочий поток. Повторяется проход, не уже извлечённое событие.
        try {
          if (_tick.WaitOne(15)) {
            _db.BeginTrans();
            while (_q.TryDequeue(out TopicEvent p)) {
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
          throw;                                  // Прерывание потока не превращаем в повтор прохода.
        }
        catch (Exception ex) {
          Log.Error("PersistentStorage.ThreadM - " + ex.ToString());
          Thread.Sleep(1000);                     // Пауза при сбое, повторяющемся в каждом проходе.
        }
      }
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

        // Родители должны предшествовать детям для отсечения устаревших ветвей по префиксу.
        foreach (var obj in _objects.FindAll().OrderBy(z => z["p"].AsString, StringComparer.Ordinal)) {
          sTmp = obj["p"].AsString;
          if (oldT.Any(z => sTmp.StartsWith(z))) {
            oldId.Add(obj["_id"]);
            continue;  // Потомок уже отвергнутой версии поддерева.
          }
          t = Topic.Declare(Topic.root, sTmp, Owner);
          a = new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null };
          // Сравниваем сохранённую версию с уже установленной в дереве.
          {
            jTmp = t.GetField("version");

            if ((sTmp = jTmp.AsString(null)) != null && sTmp.StartsWith("¤VR") && Version.TryParse(sTmp.Substring(3), out Version vRepo)) {
              jTmp = a.jm["version"];
              if ((sTmp = jTmp.AsString(null)) == null || !sTmp.StartsWith("¤VR") || !Version.TryParse(sTmp.Substring(3), out Version vDB) || vRepo > vDB) {
                oldT.Add(t.path + "/");
                oldId.Add(a.id);
                continue; // Сохранённая версия не должна заменять установленную.
              }
            }
          }
          // Решение о восстановлении State принимается по сохранённому манифесту.
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
          Topic.Fill(t, a.js, a.jm, Owner);
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
          Xst.Import(reader, null);
        }
      }
    }

    private void Save(TopicEvent p) {
      Topic t = p.Source;
      Stash a;
      JSC.JSValue jTmp;
      bool saveM = false, saveS = false;
      if (!_base.TryGetValue(t, out a)) {
        if (p.Kind == EventKind.Removed) return;
        var obj = _objects.FindOne(Query.EQ("p", t.path));
        a = obj != null ? new Stash { id = obj["_id"], bm = obj, jm = Bs2Js(obj["v"]), bs = _states.FindById(obj["_id"]), js = null } : new Stash { id = ObjectId.NewObjectId() };
        _base[t] = a;
      }

      if (p.Kind == EventKind.Removed) {
        _states.Delete(a.id);
        _objects.Delete(a.id);
        _base.Remove(t);
      } else {   // Создание, изменение манифеста или состояния, перемещение.
        // Манифест.
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
        // Состояние.
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

        if (p.Kind == EventKind.Moved) {
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
    /// <summary>Создаёт настройку каталога резервных копий, если топика ещё нет.</summary>
    private void SeedBackupDir(string dir) {
      Topic bakT;
      if (Owner.Exist("bak", out bakT)) return;
      bakT = Owner.Get("bak", true, Owner);
      bakT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
      bakT.SetState(dir);
    }

    /// <summary>Разрешает каталог копий, используя каталог данных при отсутствии корректной строки.</summary>
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

    /// <summary>Удаляет служебные копии файла, оставленные перестроением LiteDB.</summary>
    /// <remarks>Вызывается после Rebuild. Файлы с суффиксами -backup отличаются от датированных .bak, которые сохраняются по правилам ротации. 
    /// При IOException отдельная служебная копия может остаться до следующего вызова очистки.</remarks>
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
      // Каталог разрешается до закрытия базы, чтобы ошибка настройки не оставила её закрытой.
      var bak_dir = BackupDir(Path.GetDirectoryName(DB_PATH));
      string fb = bak_dir + (new string(Path.DirectorySeparatorChar, 1)) + DateTime.Now.ToString("yyMMdd_HHmmss") + ".bak";

      var db = Interlocked.Exchange(ref _db, null);
      if (db != null) {
        db.Commit();
        _objects = null;
        _states = null;
        db.Checkpoint();
        db.Dispose();
      }
      // После попытки копирования повторно открываем базу даже при ошибке File.Copy.
      // Этот finally не охватывает ошибки закрытия выше или самого повторного открытия.
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
      }

      // Отказ удаления одной копии не должен прерывать очистку остальных файлов.
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
        // Ошибка перечисления каталога обрабатывается отдельно от отказа удаления файла.
        Log.Warning("backup retention could not list {0} - {1}", bak_dir, ex.Message);
      }
    }
    #endregion Persisten Storage Members

  }
}
