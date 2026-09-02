///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace X13 {
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

    /// <summary>Invokes each Write subscriber in isolation.</summary>
    /// <remarks>Subscribers write to sockets they do not own the lifetime of - a WebUI session
    /// (WebUI/Host/LogHandler.cs) and an EsBroker connection (EsBroker/EsConnection.cs) both push
    /// straight to a client that may already be gone, and neither guards the call. A plain
    /// multicast invoke made that two separate failures: the exception escaped Process before it
    /// could restore _busy, killing console/file/history logging for the rest of the process
    /// lifetime (and, with no legacyUnhandledExceptionPolicy, taking the process down from a
    /// ThreadPool callback), and it also stopped delivery to every subscriber further down the
    /// invocation list - one dead session starved LiteDB history and all the other sessions.
    /// Nothing in here may route back through Log.*: that re-enters this very queue.</remarks>
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

    // Bypasses the queue on purpose - see Publish.
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
    /// <summary>Flushes what is queued and unhooks the writer. Once, at shutdown.</summary>
    /// <remarks>fin is not the thing being waited for in its own right: Unregister signals it once
    /// the registered wait has finished running, which is how this knows the last Process call is
    /// over. It is a local handle like any other and gets disposed like one - it was simply left
    /// to the finalizer before.</remarks>
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
              // date rolled over (or first write of this batch): the open handle, if any, points at the wrong file
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
        // batch complete (or Process re-entered on the next signal): don't hold the handle across ThreadPool callbacks
        fs?.Dispose();
        // Inside the finally, not after it: anything escaping the loop would otherwise leave
        // _busy at 2 and every later Process call would bail at the CompareExchange above.
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
