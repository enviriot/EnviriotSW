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

  /// <summary>Состояние одного архивного ряда в хранилище.</summary>
  internal sealed class ArchTopic {
    internal int Id;
    internal string Path;
    /// <summary>Курсор свёртки в UTC: начало следующего часа для обработки.</summary>
    internal DateTime Rt;
    /// <summary>Курсор очистки в UTC: граница обработанного участка истории.</summary>
    /// <remarks>Позволяет продолжать очистку небольшими шагами, не повторяя уже пройденный участок.</remarks>
    internal DateTime Pt;
    internal double Keep;
    /// <summary>Короткий ряд, новые отсчёты которого направляются в hot-файл.</summary>
    internal bool Hot;
    /// <summary>Предыдущая переданная метка времени ряда и тики выданного для неё ключа.</summary>
    /// <remarks>LastRaw хранит исходную метку, LastTicks — метку после сдвига повторов.
    /// Последовательные повторы распознаются по исходной метке, а следующий ключ получает тик после предыдущего. Состояние ведётся отдельно для каждого ряда.</remarks>
    internal long LastRaw;
    internal long LastTicks;
    /// <summary>Связанный Topic или null, если связь с деревом не установлена.</summary>
    internal Topic T;
  }

  /// <summary>Дисковое хранилище архива: реестр рядов, исходные отсчёты и свёртки.</summary>
  /// <remarks>arch_meta.ldb содержит реестр и свёртки; arch_raw.ldb и arch_hot.ldb — исходные отсчёты.
  /// Отдельный hot-файл изолирует частую запись и очистку коротких рядов от длинной истории; это дисковое хранение, а не только оперативная память.
  /// <para>Обоснование хранения — в <see href="Archivist.md#ключи-хранения-и-временная-модель">общих правилах ключей и времени</see>.</para></remarks>
  internal sealed class ArchStore : IDisposable {
    /// <summary>Верхняя включительная граница положительного срока хранения для hot-ряда, в сутках.</summary>
    internal const double HOT_KEEP_DAYS = 0.1;
    private const string META_FILE = "arch_meta.ldb";
    private const string RAW_FILE = "arch_raw.ldb";
    private const string HOT_FILE = "arch_hot.ldb";
    /// <summary>Глубина поиска начального raw-значения без подходящей часовой корзины.</summary>
    /// <remarks>При наличии корзины поиск начинается после её конца и этим интервалом не ограничивается.</remarks>
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

    /// <summary>Порог пакетной записи исходных отсчётов и фиксации свёрток.</summary>
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

    /// <summary>Открывает три базы и подготавливает их коллекции.</summary>
    /// <remarks>До открытия всех баз ссылки остаются локальными. При отказе освобождаются уже открытые базы; после присвоения полям очистка выполняется через Close().
    /// Ошибка открытия передаётся вызывающей стороне.</remarks>
    internal void Open() {
      if(!Directory.Exists(_dir)) {
        Directory.CreateDirectory(_dir);
      }
      LiteDatabase meta = null, raw = null, hot = null;
      try {
        meta = new LiteDatabase(new ConnectionString { Filename = Path.Combine(_dir, META_FILE), Upgrade = true });
        raw = new LiteDatabase(new ConnectionString { Filename = Path.Combine(_dir, RAW_FILE), Upgrade = true }) { CheckpointSize = 100 };
        hot = new LiteDatabase(new ConnectionString { Filename = Path.Combine(_dir, HOT_FILE), Upgrade = true }) { CheckpointSize = 100 };
      }
      catch {
        DisposeQuietly(hot);
        DisposeQuietly(raw);
        DisposeQuietly(meta);
        throw;
      }
      _meta = meta;
      _raw = raw;
      _hot = hot;
      try {
        Populate();
      }
      catch {
        Close();   // Ссылки уже присвоены полям, поэтому освобождаем базы общим путём.
        throw;
      }
    }

    /// <summary>Подключает коллекции и индексы после открытия всех трёх баз.</summary>
    private void Populate() {
      _topics = _meta.GetCollection<BsonDocument>("topics");
      _topics.EnsureIndex("p", true);
      _rawA = _raw.GetCollection<BsonDocument>("a");
      _hotA = _hot.GetCollection<BsonDocument>("a");
      // Старый индекс удаляем, а не просто перестаём читать:
      // LiteDB продолжает обновлять существующие индексы при записи. Свёртки хранятся отдельно от файлов исходных отсчётов.
      _roll5 = _meta.GetCollection<BsonDocument>("roll_5m");
      _rollH = _meta.GetCollection<BsonDocument>("roll_h");
      _rollD = _meta.GetCollection<BsonDocument>("roll_d");
      foreach(var c in new[] { _roll5, _rollH, _rollD }) {
        try {
          c.DropIndex("k");
        }
        catch(LiteException) {                 // Индекс может отсутствовать; эта ветка не прерывает открытие.
        }
      }

      LoadTopics();
    }

    /// <summary>Загружает реестр рядов при открытии хранилища.</summary>
    /// <remarks>Это позволяет обслуживать существующую историю после перезапуска, не дожидаясь нового изменения State каждого топика.</remarks>
    private void LoadTopics() {
      _byPath.Clear();
      _byId.Clear();
      _nextId = 1;
      foreach(var d in _topics.FindAll()) {
        var at = new ArchTopic {
          Id = d["_id"].AsInt32,
          Path = d["p"].AsString,
          // Курсоры приводим в UTC при загрузке.
          Rt = d["rt"].IsNull ? DateTime.MinValue : d["rt"].AsDateTime.ToUniversalTime(),
          Pt = d["pt"].IsNull ? DateTime.MinValue : d["pt"].AsDateTime.ToUniversalTime(),
          Keep = d["k"].IsNull ? 0 : d["k"].AsDouble
        };
        at.Hot = at.Keep > 0 && at.Keep <= HOT_KEEP_DAYS;
        // Без дерева или при отсутствии пути связь остаётся null. Топик здесь не создаём: фоновая очистка считает ряд без связанного Topic ненужным.
        at.T = Topic.root?.Get(at.Path, false);
        _byPath[at.Path] = at;
        _byId[at.Id] = at;
        if(at.Id >= _nextId) {
          _nextId = at.Id + 1;
        }
      }
    }

    internal void Close() {
      // Завершаем пакет до исключительной блокировки: сброс буферов сам берёт блокировку чтения, а повторный захват запрещён политикой NoRecursion.
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

    /// <summary>Освобождает уже открытую базу при отказе открытия хранилища.</summary>
    /// <remarks>Ошибка освобождения записывается в отладочный журнал, не подменяя исходное исключение открытия.</remarks>
    private static void DisposeQuietly(LiteDatabase db) {
      if(db == null) return;
      try {
        db.Dispose();
      }
      catch(Exception ex) {
        Log.Debug("Archivist.Open cleanup - {0}", ex.Message);
      }
    }

    private static void CommitAndDispose(ref LiteDatabase db) {
      var d = Interlocked.Exchange(ref db, null);
      if(d == null) return;
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

    /// <summary>Находит или регистрирует ряд и обновляет путь после переименования.</summary>
    /// <remarks>Если новый путь не найден, сопоставление по экземпляру Topic сохраняет прежний идентификатор ряда. 
    /// Обновление происходит при следующем вызове Resolve, а не непосредственно по событию перемещения.</remarks>
    internal ArchTopic Resolve(Topic t) {
      if(t == null) return null;
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
      // Для нового архивного ряда курсоры начинаются с текущего часа.
      var now = ArchTime.HourFloor(DateTime.UtcNow);
      at = new ArchTopic { Id = _nextId++, Path = t.path, Rt = now, Pt = now, T = t };
      RefreshKeep(at);
      _byPath[at.Path] = at;
      _byId[at.Id] = at;
      Store(at);
      return at;
    }

    /// <summary>Регистрирует или обновляет ряд по пути без обращения к Repository.</summary>
    /// <remarks>Для внешнего заполнения архива: вызывающая сторона задаёт курсоры в UTC
    /// по фактически подготовленной истории, а не получает текущий час по умолчанию.</remarks>
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
      return _byId.TryGetValue(id, out ArchTopic at) ? at : null;
    }
    internal ArchTopic ByPath(string path) {
      return path != null && _byPath.TryGetValue(path, out ArchTopic at) ? at : null;
    }
    internal ArchTopic[] Topics { get { return _byId.Values.ToArray(); } }

    /// <summary>Перечитывает Arch.keep при разрешении архивного ряда.</summary>
    /// <remarks>При отсутствии, неверном типе, NaN или неположительном значении используются 10 суток.
    /// Смена категории hot направляет новые записи в другой файл, но не переносит старые. Чтение интервала через Stream объединяет оба файла.</remarks>
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

    /// <summary>Добавляет отсчёт с текущим серверным временем UTC; используется рабочим потоком плагина.</summary>
    internal void Append(ArchTopic at, double v) {
      Append(at, DateTime.UtcNow, v);
    }

    /// <summary>Добавляет отсчёт с явно заданным временем для заполнения истории.</summary>
    /// <param name="utc">Ожидается время UTC; перед упаковкой вызывается ToUniversalTime(), учитывающий Kind.</param>
    internal void Append(ArchTopic at, DateTime utc, double v) {
      if(at == null || !IsOpen || double.IsNaN(v) || double.IsInfinity(v)) return;
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

    /// <summary>Включает пакетную запись для массового заполнения истории.</summary>
    /// <remarks>Рабочий поток сервера этот режим не использует: каждый отсчёт вставляется отдельно.
    /// Буферы сбрасываются по достижении порога или через EndBatch; несброшенные записи не видны чтению. Это не общая транзакция трёх файлов.
    /// Весь пакет должен выполняться одним потоком: транзакции LiteDB привязаны к потоку.</remarks>
    internal void BeginBatch() {
      if(!IsOpen || _batch) return;
      _pendRaw = new List<BsonDocument>(BATCH_ROWS);
      _pendHot = new List<BsonDocument>(BATCH_ROWS);
      _metaPend = 0;
      _meta.BeginTrans();
      _batch = true;
    }

    /// <summary>Сбрасывает накопленные записи и завершает пакетный режим.</summary>
    internal void EndBatch() {
      if(!_batch) return;
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
      if(pend == null || pend.Count == 0) return;
      _gate.EnterReadLock();
      try {
        (hot ? _hotA : _rawA).InsertBulk(pend, pend.Count);
      }
      finally {
        _gate.ExitReadLock();
      }
      pend.Clear();
    }

    /// <summary>Периодически фиксирует и начинает заново транзакцию свёрток в пакетном режиме.</summary>
    /// <remarks>Свёртки обновляются через Upsert, а не InsertBulk. 
    /// Порционная фиксация не даёт одной транзакции накапливать изменённые страницы за всю загружаемую историю.</remarks>
    private void MetaWritten() {
      if(_batch && ++_metaPend >= BATCH_ROWS) {
        _meta.Commit();
        _meta.BeginTrans();
        _metaPend = 0;
      }
    }

    #endregion batched writing

    /// <summary>Разводит по тикам последовательные отсчёты ряда с одинаковой переданной меткой.</summary>
    /// <remarks>При повторе исходной метки берётся тик после предыдущего выданного ключа.
    /// Отличающаяся метка сохраняется, даже если время идёт назад: загрузка старых данных не должна переносить их в будущее. 
    /// Уникальность относительно всей базы здесь не проверяется. Состояние ведётся на ряд и рассчитано на один поток записи.</remarks>
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

    /// <summary>Выдаёт исходные отсчёты по времени с индексами рядов из списка запроса.</summary>
    /// <param name="ids">Идентификаторы топиков; позиция в массиве становится ArchSample.Idx.</param>
    /// <param name="limit">Предел числа выданных отсчётов; ноль означает отсутствие ограничения.</param>
    /// <remarks>Границы переводятся в UTC и включаются в диапазон; время отсчётов также UTC.
    /// Ограничение применяется к общему потоку после слияния, не отдельно к каждому топику.</remarks>
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
        // Неизвестный ряд не даёт отсчётов, но позиции остальных рядов не сдвигаются: накопитель должен сохранить запрошенную форму строк ответа.
        if(ids[k] <= 0) continue;
        // Читаем оба файла: после изменения Arch.keep часть истории может оставаться в прежнем файле независимо от текущей категории hot.
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

    /// <summary>Сливает упорядоченные потоки отсчётов в один поток в заданном направлении.</summary>
    private static IEnumerable<ArchSample> MergeAll(List<RawCursor> cur, bool desc) {
      while(true) {
        int best = -1;
        for(int i = 0; i < cur.Count; i++) {
          if(!cur[i].Has) continue;
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

    /// <summary>Читает участок одного ряда из одного файла исходных отсчётов блоками.</summary>
    /// <remarks>Перечисление блока в LiteDB заканчивается до передачи отсчётов потребителю.
    /// Это позволяет не удерживать отдельную транзакцию каждого ряда всё время сборки ответа.
    /// Первичный ключ внутри коллекции уникален, поэтому для продолжения чтения данного топика
    /// достаточно тиков последнего ключа с исключением уже выданной записи.</remarks>
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
        if(_drained) return false;
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
          _drained = true;                    // Блок исчерпал оставшуюся часть диапазона.
        } else {
          _mark = _desc ? last - 1 : last + 1;
        }
        return true;
      }
    }

    /// <summary>Начальные значения рядов для пересчёта окна; NaN, если значение не найдено.</summary>
    /// <param name="gran">Уровень читаемого ряда: исходные отсчёты или свёртки.
    /// Начальное значение для потока средних берётся из того же уровня, не из отдельного raw-отсчёта.</param>
    internal double[] Seed(int[] ids, DateTime begin, int gran) {
      var seed = new double[ids.Length];
      for(int k = 0; k < ids.Length; k++) {
        seed[k] = double.NaN;
      }
      if(!IsOpen) return seed;
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

    /// <summary>Находит значение для продолжения исходного ряда к началу окна.</summary>
    /// <remarks>Берёт Last последней подходящей часовой корзины и проверяет последний отсчёт после её конца; без корзины глубина поиска ограничена SEED_LOOKBACK.
    /// Поиск включает beginUtc и использует только текущий raw- или hot-файл ряда, в отличие от Stream, который объединяет оба файла.</remarks>
    private double SeedRaw(int id, DateTime beginUtc) {
      Bucket b;
      long idx;
      double fromBucket = double.NaN;
      DateTime scanFrom = beginUtc - SEED_LOOKBACK;
      if(TryLastBucket(ArchTime.GRAN_HOUR, id, ArchTime.HourIndex(beginUtc), out b, out idx)) {
        fromBucket = b.Last;
        scanFrom = ArchTime.HourStart(idx + 1);
      }
      if(scanFrom >= beginUtc) return fromBucket;
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
        if(!double.IsNaN(v) && !double.IsInfinity(v)) return v;
      }
      return fromBucket;
    }

    private double SeedRoll(int gran, int id, DateTime beginUtc) {
      return TryLastBucket(gran, id, ArchTime.BucketIndex(beginUtc, gran), out Bucket b) ? b.V : double.NaN;
    }

    #endregion read

    #region rollups

    private ILiteCollection<BsonDocument> Roll(int gran) {
      return gran == ArchTime.GRAN_DAY ? _rollD : gran == ArchTime.GRAN_HOUR ? _rollH : _roll5;
    }

    internal void UpsertBucket(int gran, int topicId, long idx, Bucket b) {
      if(!IsOpen || b.N <= 0) return;
      _gate.EnterReadLock();
      try {
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

    /// <summary>Ищет последнюю корзину с номером строго меньше заданного.</summary>
    internal bool TryLastBucket(int gran, int topicId, long beforeIdx, out Bucket b) {
      return TryLastBucket(gran, topicId, beforeIdx, out b, out _);
    }

    internal bool TryLastBucket(int gran, int topicId, long beforeIdx, out Bucket b, out long foundIdx) {
      b = default(Bucket);
      foundIdx = -1;
      if(!IsOpen || beforeIdx <= 0) return false;
      var d = Roll(gran).Query()
        .Where("$._id BETWEEN @0 AND @1",
               new BsonValue(ArchTime.PackId(topicId, 0)), new BsonValue(ArchTime.PackId(topicId, beforeIdx - 1)))
        .OrderByDescending("$._id")
        .FirstOrDefault();
      if(d == null) return false;
      b = FromDoc(d);
      foundIdx = ArchTime.BucketOf(d["_id"].AsInt64);
      return true;
    }

    internal bool TryBucket(int gran, int topicId, long idx, out Bucket b) {
      b = default(Bucket);
      if(!IsOpen) return false;
      var d = Roll(gran).FindById(ArchTime.PackId(topicId, idx));
      if(d == null) return false;
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

    /// <summary>Выдаёт средние из свёрток по времени с индексами рядов из списка запроса.</summary>
    /// <param name="atStart">При true меткой служит начало корзины, иначе — середина.
    /// Выбор метки влияет на последующее взвешивание по времени, но не меняет само среднее.</param>
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
      // Диапазон задаётся номерами корзин в UTC. Корзины, содержащие begin и end,
      // включаются целиком; их метки могут выходить за запрошенное окно.
      long lo = ArchTime.BucketIndex(begin.ToUniversalTime(), gran);
      long hi = ArchTime.BucketIndex(end.ToUniversalTime(), gran);
      if(hi < lo) {
        yield break;
      }
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

    /// <summary>Сливает упорядоченные потоки корзин в один поток по возрастанию времени.</summary>
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

    /// <summary>Читает корзины одного топика в диапазоне номеров блоками.</summary>
    /// <remarks>Как и RawCursor, завершает перечисление блока в LiteDB до передачи данных потребителю. 
    /// Номер корзины уникален для топика в коллекции выбранного уровня, поэтому следующий блок начинается строго после последнего прочитанного номера.</remarks>
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
        if(_drained) return false;
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

    /// <summary>Минимальный курсор свёртки среди запрошенных рядов.</summary>
    /// <remarks>Общая граница используется для разделения свёрток и сырого хвоста.
    /// Отдельные границы для рядов потребовали бы дополнительного слияния потоков по времени.</remarks>
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

    /// <summary>Перечисляет корзины одного топика с номерами и агрегатами; обе границы включены.</summary>
    /// <remarks>Для дневной свёртки нужны также числа исходных отсчётов, не только средние из потока ArchSample. Минимумы и максимумы в корзинах не хранятся.</remarks>
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

    /// <summary>Удаляет исходные отсчёты одного топика в заданном временном окне.</summary>
    /// <remarks>Границы переводятся в UTC и включаются в диапазон.
    /// Очищаются оба файла: после смены категории hot старые записи могут оставаться не в том файле, куда направляется текущая запись.</remarks>
    internal int PurgeRaw(ArchTopic at, DateTime fromLocal, DateTime toLocal) {
      if(!IsOpen || at == null || toLocal <= fromLocal) return 0;
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

    /// <summary>Удаляет корзины одного топика по включительному диапазону номеров.</summary>
    internal int PurgeRoll(int gran, int topicId, long fromIdx, long toIdx) {
      if(!IsOpen || toIdx < fromIdx) return 0;
      _gate.EnterReadLock();
      try {
        return Roll(gran).DeleteMany("$._id BETWEEN @0 AND @1",
          new BsonValue(ArchTime.PackId(topicId, fromIdx)), new BsonValue(ArchTime.PackId(topicId, toIdx)));
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Полностью удаляет архивный ряд: отсчёты, свёртки и запись реестра.</summary>
    /// <remarks>Удаление остатков не зависит от того, насколько продвинулась предварительная очистка. Связанный Topic в Repository не удаляется. 
    /// Счётчик новых идентификаторов не уменьшается: в пределах текущего открытия хранилища удалённый номер повторно не выдаётся.</remarks>
    internal void DropTopic(ArchTopic at) {
      if(!IsOpen || at == null) return;
      // PurgeRoll сам берёт блокировку; внешний захват нарушил бы политику NoRecursion.
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

    /// <summary>Перестраивает выбранный файл отсчётов для возврата свободного места.</summary>
    /// <remarks>Не ждёт исключительную блокировку: если её нельзя получить сразу, возвращает false. 
    /// Расписание и оценка необходимости перестроения находятся в плагине.</remarks>
    internal bool TryRebuild(bool hot) {
      if(!IsOpen) return false;
      if(!_gate.TryEnterWriteLock(0)) return false;
      try {
        (hot ? _hot : _raw).Rebuild();
      }
      finally {
        _gate.ExitWriteLock();
      }
      DropRebuildLeftovers(hot ? HOT_FILE : RAW_FILE);
      return true;
    }

    /// <summary>Удаляет служебные копии файла, оставленные перестроением LiteDB.</summary>
    /// <remarks>Вызывается после успешного Rebuild: копии с суффиксами -backup остаются от прежних файлов и иначе накапливаются. 
    /// Это уборка после перестроения, не политика резервного копирования; при ошибке удаления копия может остаться.</remarks>
    private void DropRebuildLeftovers(string file) {
      string stem = Path.GetFileNameWithoutExtension(file);
      try {
        foreach(var f in Directory.GetFiles(_dir, stem + "-backup*" + Path.GetExtension(file))) {
          try {
            File.Delete(f);
          }
          catch(IOException) {                 // Копия остаётся до следующего вызова очистки.
          }
        }
      }
      catch(IOException ex) {
        Log.Warning("Archivist could not list rebuild leftovers in {0} - {1}", _dir, ex.Message);
      }
    }

    internal long RawCount(bool hot) {
      if(!IsOpen) return 0;
      _gate.EnterReadLock();
      try {
        return (hot ? _hotA : _rawA).Count();
      }
      finally {
        _gate.ExitReadLock();
      }
    }

    /// <summary>Размер файла отсчётов или ноль при его отсутствии либо IOException.</summary>
    /// <remarks>Для оценки необходимости перестроения используется длина файла, сообщаемая файловой системой, а не число страниц внутри движка.</remarks>
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

    /// <summary>Суммарное число исходных записей в raw- и hot-файлах.</summary>
    /// <remarks>Счётчики относятся ко всему хранилищу и не оценивают число отсчётов в конкретном окне запроса.</remarks>
    internal long ApproxRows() {
      if(!IsOpen) return 0;
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
