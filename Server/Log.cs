///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace X13 {
  /// <summary>Журнал: консоль, суточные файлы, файловая история и живые подписчики.</summary>
  /// <remarks>Статическая часть - публичный API и ссылка на журнал процесса; всё остальное принадлежит
  /// экземпляру: очередь, потребитель, файл, подписчики, часы и ограничение повторяющихся сбоев. Журнал
  /// процесса - один экземпляр за этим API, а тест создаёт собственный, со своими часами, очередью и
  /// подписчиками, и двигает время, не задевая журнал процесса.
  /// <para>Записи доставляются асинхронно, с потока пула, поэтому запись, сделанная раньше, может приехать
  /// позже - тест, ждущий строку, обязан ждать нужную, а не любую. Потребитель просыпается и без записей
  /// (idleWakeMs) и на каждом проходе сбрасывает удержанные сбои. Finish() необратим: ожидание журнала
  /// процесса регистрируется один раз, в его конструкторе.</para></remarks>
  public sealed class Log {
    private static readonly Log _default;

    static Log() {
      _default = new Log("../log", null, true, 30000);
    }
    public static void Debug(string format, params object[] arg) {
      _default.Enqueue(LogLevel.Debug, format, arg);
    }
    public static void Info(string format, params object[] arg) {
      _default.Enqueue(LogLevel.Info, format, arg);
    }
    public static void Warning(string format, params object[] arg) {
      _default.Enqueue(LogLevel.Warning, format, arg);
    }
    public static void Error(string format, params object[] arg) {
      _default.Enqueue(LogLevel.Error, format, arg);
    }
    /// <summary>Сбой, который может повторяться. Пишется не чаще раза в 30 секунд на место и тип исключения.</summary>
    /// <param name="ll">Уровень</param>
    /// <param name="where">Место в коде.</param>
    /// <param name="subject">О чём был сбой, или null.</param>
    public static void Fault(LogLevel ll, string where, object subject, Exception ex) {
      _default.ReportFault(ll, where, subject, ex);
    }
    public static void OnWrite(LogLevel ll, string format, params object[] arg) {
      _default.Enqueue(ll, format, arg);
    }
    public static event Action<LogLevel, DateTime, string> Write {
      add { _default.Written += value; }
      remove { _default.Written -= value; }
    }
    public static event Action<LogEntry> RecordWritten {
      add { _default.EntryWritten += value; }
      remove { _default.EntryWritten -= value; }
    }
    public static IReadOnlyList<LogEntry> ReadHistory(string beforeId, int count) {
      return _default.ReadFileHistory(beforeId, count);
    }
    /// <summary>Писать ли журнал процесса в файл.</summary>
    public static bool UseFile {
      get { return _default._useFile; }
      set { _default._useFile = value; }
    }
    /// <summary>Закрывает журнал процесса, см. Close. Вызывается один раз при завершении.</summary>
    public static void Finish() {
      _default.Close();
    }
    public class LogRecord {
      public LogLevel ll;
      public DateTime dt;
      public string format;
      public object[] args;
    }

    /// <summary>Самый долгий сон потребителя без записей и окно ограничения сбоев, в миллисекундах.</summary>
    /// <remarks>Не бесконечность, по двум причинам. Хвост затихшей серии сбоев выписывается как раз тогда,
    /// когда записей нет, и без тайм-аута сбрасывать удержанные сбои было бы некому. А запись, поставленная
    /// в очередь между концом выборки и _busy = 1, отдаёт свой сигнал вызову, который тут же выходит на
    /// CompareExchange, - и раньше ждала следующей записи, сколь угодно долго.
    /// <para>Окно равно сну: хвост выписывается на первом проходе после конца окна, поэтому окно короче сна
    /// ничего не ускорило бы, а выписан хвост будет через одно-два окна после строки серии.</para></remarks>
    private readonly int _idleWakeMs;
    private readonly bool _useDiagnostic;
    private readonly bool _useConsole;
    private readonly Func<DateTime> _clock;
    private readonly FileLogHistory _files;
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogRecord> _records;
    private readonly AutoResetEvent _kickEv;
    private readonly RegisteredWaitHandle _wh;
    private volatile bool _useFile;
    private DateTime _lastPruned;
    private bool _fileError;
    private int _busy;

    /// <param name="directory">Каталог суточных файлов. При null файлового источника нет.</param>
    /// <param name="clock">Часы журнала: метки записей, а через них и смена суточного файла, и сутки
    /// удержанных сбоев; null - DateTime.Now. Файловые тесты используют отдельный временный каталог.
    /// Удаление старых файлов этих часов не слушает, см. Process.</param>
    /// <param name="useFile">Писать ли в файл: у журнала процесса true, у тестового - false.</param>
    /// <param name="idleWakeMs">Сон потребителя и окно ограничения сбоев, см. _idleWakeMs. У журнала процесса
    /// 30 секунд; тестовому экземпляру короткое окно нужно, чтобы хвост, выписанный без FlushFaults, не
    /// заставлял тест ждать полминуты.</param>
    internal Log(string directory, Func<DateTime> clock, bool useFile, int idleWakeMs) {
      _useDiagnostic = System.Diagnostics.Debugger.IsAttached;
      try { int window_height = Console.WindowHeight; _useConsole = true; }
      catch { _useConsole = false; }
      _files = directory == null ? null : new FileLogHistory(directory);
      _useFile = useFile && _files != null;
      _idleWakeMs = idleWakeMs;
      _clock = clock ?? (() => DateTime.Now);
      _records = new System.Collections.Concurrent.ConcurrentQueue<LogRecord>();
      _kickEv = new AutoResetEvent(false);
      _busy = 1;
      // Последним: с регистрации Process может быть вызван в любой момент.
      _wh = ThreadPool.RegisterWaitForSingleObject(_kickEv, Process, null, _idleWakeMs, false);
    }

    /// <summary>Подписчики этого экземпляра; у журнала процесса - через Log.Write.</summary>
    internal event Action<LogLevel, DateTime, string> Written;
    internal event Action<LogEntry> EntryWritten;

    internal IReadOnlyList<LogEntry> ReadFileHistory(string beforeId, int count) {
      if(!_useFile || _files == null) {
        throw new InvalidOperationException("File logging is disabled");
      }
      return _files.Read(beforeId, count);
    }

    /// <summary>Ставит запись в очередь; у журнала процесса - через Log.OnWrite и соседей.</summary>
    internal void Enqueue(LogLevel ll, string format, params object[] arg) {
      if(!Enum.IsDefined(typeof(LogLevel), ll)) throw new ArgumentOutOfRangeException(nameof(ll));
      _records.Enqueue(new LogRecord() { ll = ll, dt = _clock(), format = format, args = arg });
      _kickEv.Set();
    }

    /// <summary>Выписывает удержанные хвосты сбоев, записывает всё содержимое очереди и отключает обработчик.</summary>
    /// <remarks>Ожидание выполняется не ради самого fin: Unregister устанавливает его после завершения
    /// зарегистрированного ожидания, благодаря чему определяется окончание последнего вызова Process.
    /// Это локальный дескриптор, который должен освобождаться так же, как любой другой. Ранее его освобождение
    /// просто оставлялось финализатору.</remarks>
    internal void Close() {
      // Хвосты, окно которых ещё не истекло: при остановке сбоев больше всего, а следующего сброса уже не будет.
      FlushFaults(true);
      _kickEv.Set();
      using(AutoResetEvent fin = new AutoResetEvent(false)) {
        _wh.Unregister(fin);
        fin.WaitOne(400);
      }
    }

    /// <summary>Вызывает каждого подписчика Write изолированно от остальных.</summary>
    /// <remarks>Подписчики записывают в сокеты, временем жизни которых они не управляют. Сеанс WebUI
    /// (WebUI/Host/LogHandler.cs) отправляет данные непосредственно клиенту, который уже мог отключиться, и ни один из них не защищает вызов.
    /// Ничто здесь не должно повторно направляться через Log.*: это вновь входит в ту же очередь.</remarks>
    private void Publish(LogLevel ll, DateTime dt, string msg) {
      Action<LogLevel, DateTime, string> handlers = Written;
      if (handlers == null) {
        return;
      }
      foreach (Delegate handler in handlers.GetInvocationList()) {
        try {
          ((Action<LogLevel, DateTime, string>)handler)(ll, dt, msg);
        }
        catch (Exception ex) {
          ReportDirect("Log subscriber failed - " + ex.ToString());
        }
      }
    }

    private void PublishEntry(LogEntry entry) {
      var handlers = EntryWritten;
      if(handlers == null) return;
      foreach(Delegate handler in handlers.GetInvocationList()) {
        try {
          ((Action<LogEntry>)handler)(entry);
        }
        catch(Exception ex) {
          ReportDirect("Log subscriber failed - " + ex);
        }
      }
    }

    private void FileDiagnostic(string message) {
      ReportDirect(message);
      // Не возвращаем ошибку файловой записи в очередь того же неисправного файла.
      var entry = new LogEntry(null, _clock(), LogLevel.Error, message);
      Publish(entry.Level, entry.Timestamp, entry.Message);
      PublishEntry(entry);
    }

    // Намеренно обходит очередь. См. Publish.
    private void ReportDirect(string text) {
      try {
        if (_useDiagnostic) {
          System.Diagnostics.Debug.WriteLine(text);
        }
        if (_useConsole) {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine(text);
        }
      }
      catch (Exception) {
      }
    }

    private void Process(object o, bool to) {
      if (Interlocked.CompareExchange(ref _busy, 2, 1) != 1) {
        return;
      }
      LogRecord r;
      string msg;
      try {
        // На каждом вызове, а не только по тайм-ауту: при непрерывной записи тайм-аут не наступает вовсе.
        // До выборки - чтобы строки, выписанные сбросом, ушли этим же проходом.
        FlushFaults();
        while (_records.TryDequeue(out r)) {
          try {
            msg = string.Format(r.format, r.args);
          }
          catch (Exception) {
            r.ll = LogLevel.Error;
            msg = "Bad format: " + r.format;
          }

          string msgA;
          ConsoleColor cc;
          switch (r.ll) {
          case LogLevel.Info:
            cc = ConsoleColor.White;
            msgA = r.dt.ToString("HH:mm:ss.ff") + "[I] " + msg;
            break;
          case LogLevel.Warning:
            cc = ConsoleColor.Yellow;
            msgA = r.dt.ToString("HH:mm:ss.ff") + "[W] " + msg;
            break;
          case LogLevel.Error:
            cc = ConsoleColor.Red;
            msgA = r.dt.ToString("HH:mm:ss.ff") + "[E] " + msg;
            break;
          default:
            msgA = r.dt.ToString("HH:mm:ss.ff") + "[D] " + msg;
            cc = ConsoleColor.Gray;
            break;
          }
          if (_useDiagnostic) {
            System.Diagnostics.Debug.WriteLine(msgA);
          }
          if (_useConsole) {
            Console.ForegroundColor = cc;
            Console.WriteLine(msgA);
          }
          var entry = new LogEntry(null, r.dt, r.ll, msg);
          if(_useFile && _files != null) {
            if(_lastPruned != r.dt.Date) {
              _lastPruned = r.dt.Date;
              _files.Prune(DateTime.Today, FileDiagnostic);
            }
            Exception failure = null;
            for(int attempt = 0; attempt < 3; attempt++) {
              try {
                entry = _files.Append(r.ll, r.dt, msg);
                failure = null;
                _fileError = false;
                break;
              }
              catch(InvalidDataException ex) {
                failure = ex;
                break;
              }
              catch(IOException ex) {
                failure = ex;
                Thread.Sleep(15);
              }
              catch(UnauthorizedAccessException ex) {
                failure = ex;
                break;
              }
            }
            if(failure != null && !_fileError) {
              _fileError = true;
              FileDiagnostic("Log file write failed: " + failure.Message);
            }
          }
          Publish(r.ll, r.dt, msg);
          PublishEntry(entry);
        }
      }
      finally {
        // Внутри finally, а не после него: иначе исключение из цикла оставило бы _busy равным 2,
        // и каждый последующий вызов Process завершался бы на CompareExchange выше.
        _busy = 1;
      }
    }

    #region Повторяющиеся сбои
    private readonly object _faultSync = new object();
    private readonly Dictionary<string, FaultEntry> _faults = new Dictionary<string, FaultEntry>();
    private int _faultsHeld;
    private DateTime _faultsDay;

    /// <summary>Пишет сбой либо удерживает его и запоминает, что удержал; у журнала процесса - через Log.Fault.</summary>
    /// <remarks>Сбои приходят из циклов, которые оборачиваются около шестидесяти раз в секунду, поэтому сбой
    /// на каждом обороте - плагин, бросающий исключение на каждом тике, подписчик, бросающий на каждом
    /// значении, - записывался бы шестьдесят раз в секунду и каждый раз с полным стеком, погребая тот самый
    /// журнал, который должен был его объяснить.
    /// <para>Бюджет выделяется на пару (место, тип исключения), а не на место. С одним бюджетом на место
    /// постоянный сбой расходует его целиком, и ДРУГОЙ сбой из того же места - более редкий и более
    /// интересный - не записывается вовсе.</para></remarks>
    internal void ReportFault(LogLevel ll, string where, object subject, Exception ex) {
      lock(_faultSync) {
        string key = where + "|" + (ex == null ? "?" : ex.GetType().Name);
        FaultEntry e;
        if(!_faults.TryGetValue(key, out e)) {
          e = new FaultEntry();
          _faults[key] = e;
        }
        e.ll = ll;
        e.where = where;
        e.what = ex == null ? "?" : ex.GetType().Name + ": " + ex.Message;
        e.count++;
        e.total++;
        DateTime now = _clock();
        if(now < e.next) {
          _faultsHeld++;
          return;
        }
        EmitFault(e, now, subject == null ? "-" : subject.ToString(), ex == null ? "?" : ex.ToString());
      }
    }
    /// <summary>Выписывает удержанное, а раз в сутки - всё, что случилось.</summary>
    /// <remarks>Без первой половины хвост серии не учитывается никогда: удержанное число переносится только
    /// в СЛЕДУЮЩУЮ строку по тому же ключу, а у прекратившегося сбоя следующей строки нет. Сбой, случившийся
    /// трижды и прекратившийся, всё равно должен читаться как три.
    /// <para>Вторая половина нужна потому, что ограничение - это ещё и способ что-то пропустить. Сбой,
    /// который капает - раз в минуту, всю ночь, - получает одну строку, а дальше тишину, и никто не листает
    /// журнал назад, чтобы их пересчитать. Итог, записанный при смене даты, - строка, которую действительно
    /// читают, и ложится она рядом с баннером даты, который пишет цикл движка.</para>
    /// <para>Вызывает потребитель на каждом проходе, а проходы идут не реже раза в _idleWakeMs,
    /// поэтому тем, кто сообщает о сбое, сбрасывать ничего не нужно.</para></remarks>
    /// <param name="all">Выписать удержанные хвосты, не дожидаясь окна: для Close, после которого
    /// следующего сброса не будет.</param>
    internal void FlushFaults(bool all = false) {
      lock(_faultSync) {
        DateTime now = _clock();
        if(_faultsDay != now.Date) {
          if(_faultsDay != default(DateTime)) {
            SummariseFaults();
          }
          _faultsDay = now.Date;
        }
        if(_faultsHeld == 0) {
          return;
        }
        _faultsHeld = 0;
        foreach(var kv in _faults) {
          FaultEntry e = kv.Value;
          if(e.count > 0 && (all || now >= e.next)) {
            EmitFault(e, now, "-", e.what + ", no longer occurring");
          } else if(e.count > 0) {
            _faultsHeld++;   // окно ещё не истекло; смотрим снова на следующем проходе
          }
        }
      }
    }

    /// <summary>По строке на каждый вид сбоя: сколько их было и каким был последний.</summary>
    /// <remarks>Заодно забывает ключи, которым больше нечего выписать. Запись нужна, только пока у неё есть
    /// удержанный хвост или несведённый итог, а итог к этому месту уже записан. Без удаления таблица росла
    /// бы всё время жизни процесса: Log.Fault публичный, и ключи в нём не обязаны быть местами в коде.
    /// Цена - одна лишняя строка после полуночи у ключа, выписанного в последнее окно перед ней:
    /// окно уходит вместе с записью.</remarks>
    private void SummariseFaults() {
      List<string> idle = null;
      foreach(var kv in _faults) {
        FaultEntry e = kv.Value;
        if(e.total > 0) {
          Enqueue(e.ll, "{0} - {1} failures on {2:yyyy-MM-dd}, last was {3}", e.where, e.total, _faultsDay, e.what);
          e.total = 0;
        }
        if(e.count == 0) {
          (idle ?? (idle = new List<string>())).Add(kv.Key);   // из Dictionary нельзя удалять во время обхода
        }
      }
      if(idle != null) {
        foreach(string key in idle) {
          _faults.Remove(key);
        }
      }
    }

    private void EmitFault(FaultEntry e, DateTime now, string subject, string detail) {
      int n = e.count;
      e.count = 0;
      e.next = now.AddMilliseconds(_idleWakeMs);
      string more = n > 1 ? string.Format(" [and {0} more]", n - 1) : string.Empty;
      Enqueue(e.ll, "{0}({1}){2} - {3}", e.where, subject, more, detail);
    }

    private sealed class FaultEntry {
      public DateTime next;
      public int count;
      public int total;   // с последнего суточного итога; запись строки его не сбрасывает
      public LogLevel ll;
      public string where;
      public string what;
    }
    #endregion Повторяющиеся сбои
  }

  public enum LogLevel {
    Debug,
    Info,
    Warning,
    Error
  }
}
