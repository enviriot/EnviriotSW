///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime;
using System.Threading;
using X13.Repository;
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Archivist {
  /// <summary>Организует запись, фоновое обслуживание и чтение истории топиков.</summary>
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 3)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Archivist")]
  internal class ArchivistPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/Archivist";
    private const string DEFAULT_DIR = "../data";

    private readonly string _dir;
    private readonly ConcurrentQueue<TopicEvent> _q;
    /// <summary>Есть работа в очереди, либо пора завершаться.</summary>
    private readonly AutoResetEvent _tick;
    /// <summary>Попытка открытия завершена; результат проверяется через _startErr.</summary>
    private readonly ManualResetEvent _loaded;
    private ArchStore _store;
    private Topic _owner;
    private IDisposable _allSub;
    private Func<string[], DateTime, int, DateTime, JSL.Array> _aQuery;
    private Thread _tr;
    private volatile bool _terminate;
    /// <summary>Ошибка открытия хранилища, передаваемая из рабочего потока в Start().</summary>
    /// <remarks>Записывается до _loaded.Set() и читается после _loaded.WaitOne(). Start() сообщает хосту об отказе плагина через исключение.</remarks>
    private volatile Exception _startErr;
    private int _rrIdx;
    private DateTime _nextHotRebuild;
    private DateTime _nextRawRebuild;
    /// <summary>Raw-файл признан стоящим перестроения, но исключительная блокировка была занята.</summary>
    private bool _rawRebuildPending;
    /// <summary>Флаг диагностики запросов из /$YS/Archivist/verbose.</summary>
    internal bool verbose;
    private SubRec _verboseSR;

    public ArchivistPl() : this(DEFAULT_DIR) {
    }
    internal ArchivistPl(string dir) {
      _dir = dir;
      _q = new ConcurrentQueue<TopicEvent>();
      _tick = new AutoResetEvent(false);
      _loaded = new ManualResetEvent(false);
    }

    #region IPlugModul Members
    public void Init() {
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
      _loaded.WaitOne();   // До подключения входов нужно дождаться открытия и проверить ошибку.
      var err = _startErr;
      if(err != null) {
        // Обёртка, а не проброс оригинала: исключение поймано в другом потоке, повторный throw затёр бы его стек.
        throw new InvalidOperationException("Archivist.Open(" + _dir + ") failed", err);
      }
      _verboseSR =JsExtLib.EnsureCfg(Owner, "verbose", Topic.Attribute.Required | Topic.Attribute.Config, v => verbose = v, false);
      _allSub = Topic.Subscribe(SubFunc);
      // Поставщик регистрируется после открытия хранилища, а не в конструкторе: хост упорядочивает Start по приоритету, но не создание экземпляров через MEF.
      // Сохраняем именно этот экземпляр делегата, чтобы Stop распознал его по ссылке.
      _aQuery = AQuery;
      JsExtLib.AQuery = _aQuery;
    }

    public void Tick() {
      if(!_q.IsEmpty) {
        _tick.Set();
      }
    }

    /// <summary>Отключает входы плагина перед остановкой потока и закрытием хранилища.</summary>
    /// <remarks>SubFunc вызывается Repository на потоке движка, AQuery — на потоке запроса. Подписку и общего поставщика нужно снять до закрытия базы. 
    /// Это не отменяет уже начатые запросы и не гарантирует опустошение очереди записи.</remarks>
    public void Stop() {
      IDisposable allSub = _allSub;
      _allSub = null;
      allSub?.Dispose();
      // Сбрасываем только свой делегат: другой поставщик уже мог заменить его.
      if(_aQuery != null && ReferenceEquals(JsExtLib.AQuery, _aQuery)) {
        JsExtLib.AQuery = null;
      }
      _aQuery = null;
      // Подпиской EnsureCfg владеет вызывающая сторона.
      _verboseSR?.Dispose();
      _verboseSR = null;
      _terminate = true;
      _tick.Set();
      if(_tr != null && !_tr.Join(5000)) {
        // Принудительное прерывание внутри LiteDB может повредить файл. 
        // Пока поток работает, нельзя освобождать хранилище и объекты ожидания; при выходе ThreadM сам закроет базу.
        Log.Error("Archivist worker did not stop within 5 s; leaving the store open - it will be recovered on the next start");
        return;
      }
      var s = Interlocked.Exchange(ref _store, null);
      s?.Dispose();
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

    /// <summary>Отбирает изменения для очереди архива на потоке движка.</summary>
    /// <remarks>В очередь передаётся событие со ссылкой на Topic, не снимок нового State. Числовое значение проверяется позже, в StoreOne.</remarks>
    private void SubFunc(TopicEvent p) {
      if(p.Kind != EventKind.StateChanged || p.Source == null || p.Author == Owner) {
        return;
      }
      if(!p.Source.GetField("Arch.enable").As<bool>()) {
        return;
      }
      _q.Enqueue(p);
    }

    private void ThreadM() {
      try {
        _store.Open();
      }
      catch(Exception ex) {
        _startErr = ex;
        _terminate = true;
      }
      finally {
        _loaded.Set();
      }
      while(!_terminate) {
        if(_tick.WaitOne(15)) {
          while(_q.TryDequeue(out TopicEvent p)) {
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
      }
      var s = _store;
      s?.Close();
    }

    private void StoreOne(TopicEvent p) {
      var st = _store;
      if(st == null || !st.IsOpen) return;
      double v = p.Source.GetState().AsDouble(double.NaN);
      if(double.IsNaN(v) || double.IsInfinity(v)) return;
      st.Append(st.Resolve(p.Source), v);
    }

    /// <summary>Выполняет шаг фонового обслуживания, обходя топики по очереди.</summary>
    /// <remarks>Вызывается при истечении ожидания сигнала. Свёртка ограничена одним часом ряда за проход, чтобы обслуживание не задерживало запись надолго.</remarks>
    private void IdleTask() {
      var st = _store;
      if(st == null || !st.IsOpen) return;
      var all = st.Topics;
      if(all.Length == 0) return;
      if(_rrIdx >= all.Length) {
        _rrIdx = 0;
        return;
      }
      var at = all[_rrIdx++];
      try {
        // Сначала пробуем свёртку; если непустой час не записан, выполняем шаг очистки.
        if(!ArchRollup.FoldHour(st, at)) {
          ArchRetention.Step(st, at);
        }
      }
      catch(Exception ex) {
        Log.Warning("Archivist.Maintain({0}) - {1}", at.Path, ex.Message);
      }
      Reclaim(st);
    }

    /// <summary>Оценка размера исходного отсчёта в байтах для решения о перестроении файла.</summary>
    /// <remarks>Эмпирическая оценка живых данных, не точный размер записи в формате хранения.</remarks>
    internal const int DOC_BYTES = 129;
    /// <summary>Допустимое отношение размера файла к оценке объёма живых данных.</summary>
    internal const double SLACK = 1.5;
    /// <summary>Минимальный размер файла, при котором рассматривается перестроение.</summary>
    internal const long MIN_REBUILD_BYTES = 4 * 1024 * 1024;

    /// <summary>Оценивает, достаточно ли лишнего места в файле для перестроения.</summary>
    /// <remarks>LiteDB переиспользует свободные страницы, поэтому обычная очистка не требует переписывать файл. 
    /// Сравнение с оценкой живых данных позволяет пропускать лишнюю работу и возвращать место после значительного удаления истории.</remarks>
    internal static bool WorthRebuilding(long fileBytes, long rows) {
      return fileBytes >= MIN_REBUILD_BYTES && fileBytes > rows * DOC_BYTES * SLACK;
    }

    /// <summary>Возвращает ближайшие будущие 03:45 по местному времени в представлении UTC.</summary>
    /// <remarks>Ночная проверка привязана к часам, а не к суткам после запуска сервера.</remarks>
    internal static DateTime NextNightly(DateTime nowUtc) {
      DateTime local = nowUtc.ToLocalTime();
      DateTime at = local.Date.AddHours(3.75);
      if(at <= local) {
        at = at.AddDays(1);
      }
      return at.ToUniversalTime();
    }

    /// <summary>Проверяет по расписанию необходимость перестроения файлов отсчётов.</summary>
    /// <remarks>Для hot проверка назначается через час, для raw — на ближайшие 03:45.
    /// Если исключительная блокировка занята, попытка откладывается до следующего прохода. Для raw при этом
    /// повторяется только перестроение: подсчёт строк обходит весь файл, и повторять его на каждом проходе незачем.</remarks>
    private void Reclaim(ArchStore st) {
      DateTime now = DateTime.UtcNow;
      // Расписание задаёт время проверки, оценка — необходимость перестроения. Размер файла и число записей запрашиваем по расписанию, не в каждом свободном проходе.
      if(now >= _nextHotRebuild) {
        if(!WorthRebuilding(st.FileBytes(true), st.RawCount(true)) || st.TryRebuild(true)) {
          _nextHotRebuild = now.AddHours(1);
        }
      } else if(now >= _nextRawRebuild) {
        if(!_rawRebuildPending && !WorthRebuilding(st.FileBytes(false), st.RawCount(false))) {
          _nextRawRebuild = NextNightly(now);
        } else {
          _rawRebuildPending = true;
          if(st.TryRebuild(false)) {
            _rawRebuildPending = false;
            _nextRawRebuild = NextNightly(now);
            CompactLargeObjectHeap();
          }
        }
      }
    }

    /// <summary>Возвращает системе память, через которую прошло перестроение raw-файла.</summary>
    /// <remarks>Rebuild переписывает базу одной транзакцией временного движка LiteDB: все страницы файла проходят через
    /// память сегментами по 8 МБ в куче больших объектов (LOH), после чего становятся мусором. LOH сама не уплотняется,
    /// и процесс сохранял бы выделенные дыры размером с базу. Hot-файл мал, поэтому после его перестроения сборка не нужна.</remarks>
    private static void CompactLargeObjectHeap() {
      GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
      GC.Collect();
    }
    #region AQuery

    /// <summary>Обрабатывает запрос истории от WebUI или серверного JavaScript.</summary>
    /// <remarks>Выполняется на потоке вызывающей стороны, не в рабочем потоке записи.
    /// При verbose журналирует параметры, выбранный уровень, объём чтения и длительность.
    /// Исключение превращается в предупреждение и пустой массив: это не доказательство отсутствия истории.</remarks>
    private JSL.Array AQuery(string[] topics, DateTime begin, int count, DateTime end) {
      var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
      try {
        var rez = ArchQuery.Run(_store, topics, begin, count, end, out ArchQueryStat stat);
        if (sw != null) {
          sw.Stop();
          Log.Info("{0}", Describe(topics, begin, count, end, stat, (int)rez.length, sw.Elapsed.TotalMilliseconds));
        }
        return rez;
      }
      catch(Exception ex) {
        Log.Warning("Archivist.AQuery([{0}], {1:yyMMdd'T'HHmmss}, {2}) - {3}", string.Join(", ", topics ?? new string[0]), begin, count, ex.Message);
        return new JSL.Array();
      }
    }
    
    /// <summary>Формирует строку журнала с параметрами и затратами запроса.</summary>
    /// <remarks>Окно выводится в том виде, в котором его передал клиент, до выравнивания на сетку. 
    /// Список топиков помещён в конец, чтобы длинные пути не скрывали показатели.
    /// Форматирование отделено от запроса и не требует обращения к хранилищу.</remarks>
    internal static string Describe(string[] topics, DateTime begin, int count, DateTime end, ArchQueryStat stat, int points, double ms) {
      var names = topics ?? new string[0];
      return string.Format("Archivist.AQuery {0} topic(s) [{1:yyMMdd'T'HHmmss} .. {2:yyMMdd'T'HHmmss}] count={3} -> {4}, {5:n0} row(s), {6:n0} point(s), {7:n1} ms  [{8}]",
        names.Length, begin, end == DateTime.MinValue ? begin : end, count, stat.Level, stat.Rows, points, ms, string.Join(", ", names));
    }
    #endregion AQuery
  }
}
