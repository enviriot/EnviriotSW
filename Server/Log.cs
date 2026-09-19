///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace X13 {
  /// <summary>Журнал: консоль, файл и подписчики - панель лога в IDE и история в базе.</summary>
  /// <remarks>Записи доставляются асинхронно, с потока пула, поэтому запись, сделанная раньше,
  /// может приехать позже - тест, ждущий строку, обязан ждать нужную, а не любую. 
  /// Finish() необратим: дескриптор ожидания взводится только в статическом конструкторе.</remarks>
  public static class Log {
    private static readonly bool _useDiagnostic;
    private static readonly bool _useConsole;
    private static readonly AutoResetEvent _kickEv;
    private static readonly RegisteredWaitHandle _wh;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<LogRecord> _records;
    private static readonly string _lfMask;
    public static bool useFile;
    private static string _lfPath;
    private static DateTime _firstDT;
    private static int _busy;

    static Log() {
      _useDiagnostic = System.Diagnostics.Debugger.IsAttached;
      try { int window_height = Console.WindowHeight; _useConsole = true; }
      catch { _useConsole = false; }
      if (!Directory.Exists("../log")) {
        Directory.CreateDirectory("../log");
      }
      useFile = true;
      _lfMask = "../log/{0}_" + Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetCallingAssembly().Location) + ".log";
      _records = new System.Collections.Concurrent.ConcurrentQueue<LogRecord>();
      _kickEv = new AutoResetEvent(false);
      _busy = 1;
      _wh = ThreadPool.RegisterWaitForSingleObject(_kickEv, Process, null, -1, false);
    }
    public static void Debug(string format, params object[] arg) {
      onWrite(LogLevel.Debug, format, arg);
    }
    public static void Info(string format, params object[] arg) {
      onWrite(LogLevel.Info, format, arg);
    }
    public static void Warning(string format, params object[] arg) {
      onWrite(LogLevel.Warning, format, arg);
    }
    public static void Error(string format, params object[] arg) {
      onWrite(LogLevel.Error, format, arg);
    }
    public static void onWrite(LogLevel ll, string format, params object[] arg) {
      _records.Enqueue(new LogRecord() { ll = ll, dt = DateTime.Now, format = format, args = arg });
      _kickEv.Set();
    }
    public static void AddEntry(LogLevel ll, DateTime dt, string msg) {
      Publish(ll, dt, msg, false);
    }

    /// <summary>Вызывает каждого подписчика Write изолированно от остальных.</summary>
    /// <remarks>Подписчики записывают в сокеты, временем жизни которых они не управляют. Сеанс WebUI
    /// (WebUI/Host/LogHandler.cs) отправляет данные непосредственно клиенту, который уже мог отключиться, и ни один из них не защищает вызов.
    /// Ничто здесь не должно повторно направляться через Log.*: это вновь входит в ту же очередь.</remarks>
    private static void Publish(LogLevel ll, DateTime dt, string msg, bool live) {
      Action<LogLevel, DateTime, string, bool> handlers = Write;
      if (handlers == null) {
        return;
      }
      foreach (Delegate handler in handlers.GetInvocationList()) {
        try {
          ((Action<LogLevel, DateTime, string, bool>)handler)(ll, dt, msg, live);
        }
        catch (Exception ex) {
          ReportDirect("Log subscriber failed - " + ex.ToString());
        }
      }
    }

    // Намеренно обходит очередь. См. Publish.
    private static void ReportDirect(string text) {
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
    public static event Action<LogLevel, DateTime, string, bool> Write;
    public static Func<DateTime, int, IEnumerable<Log.LogRecord>> History;
    /// <summary>Записывает всё содержимое очереди и отключает обработчик. Вызывается один раз при завершении.</summary>
    /// <remarks>Ожидание выполняется не ради самого fin: Unregister устанавливает его после завершения
    /// зарегистрированного ожидания, благодаря чему определяется окончание последнего вызова Process.
    /// Это локальный дескриптор, который должен освобождаться так же, как любой другой. Ранее его освобождение
    /// просто оставлялось финализатору.</remarks>
    public static void Finish() {
      _kickEv.Set();
      using(AutoResetEvent fin = new AutoResetEvent(false)) {
        _wh.Unregister(fin);
        fin.WaitOne(400);
      }
    }

    private static void Process(object o, bool to) {
      if (Interlocked.CompareExchange(ref _busy, 2, 1) != 1) {
        return;
      }
      LogRecord r;
      string msg;
      FileStream fs = null;
      try {
        while (_records.TryDequeue(out r)) {
          try {
            msg = string.Format(r.format, r.args);
          }
          catch (Exception) {
            r.ll = LogLevel.Error;
            msg = "Bad format: " + r.format;
          }

          Publish(r.ll, r.dt, msg, true);
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
          if (useFile) {
            if (_lfPath == null || _firstDT != r.dt.Date) {
              _firstDT = r.dt.Date;
              try {
                string m1 = string.Format(_lfMask, "*");
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(m1), Path.GetFileName(m1), SearchOption.TopDirectoryOnly)) {
                  if (File.GetLastWriteTime(f).AddDays(20) < _firstDT)
                    File.Delete(f);
                }
              }
              catch (System.IO.IOException) {
              }
              _lfPath = string.Format(_lfMask, _firstDT.ToString("yyMMdd"));
              // дата сменилась либо это первая запись пакета: открытый дескриптор, если он есть, указывает на неверный файл
              fs?.Dispose();
              fs = null;
            }
            byte[] ba = Encoding.UTF8.GetBytes(msgA + "\r\n");
            for (int i = 2; i >= 0; i--) {
              try {
                if (fs == null) {
                  fs = File.Open(_lfPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                  fs.Seek(0, SeekOrigin.End);
                }
                fs.Write(ba, 0, ba.Length);
                break;
              }
              catch (System.IO.IOException) {
                fs?.Dispose();
                fs = null;
                Thread.Sleep(15);
              }
            }
          }
        }
      }
      finally {
        // пакет завершён либо Process повторно вызван следующим сигналом: не сохраняем дескриптор между callback ThreadPool
        fs?.Dispose();
        // Внутри finally, а не после него: иначе исключение из цикла оставило бы _busy равным 2,
        // и каждый последующий вызов Process завершался бы на CompareExchange выше.
        _busy = 1;
      }
    }
    public class LogRecord {
      public LogLevel ll;
      public DateTime dt;
      public string format;
      public object[] args;
    }
  }
  public enum LogLevel {
    Debug,
    Info,
    Warning,
    Error
  }
}
