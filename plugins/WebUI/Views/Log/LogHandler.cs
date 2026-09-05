///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace X13.WebUI {
  // Log entries have no vid/tree namespace to route by, so this deliberately sits
  // outside the IViewProvider/vid-routed machinery (see ViewSession) - it's a single
  // global stream, handled the same way req.hello is (a fixed handler, no vid).
  internal sealed class LogHandler : IDisposable {
    private const int DefaultCount = 50;
    private const int MaxCount = 200;

    private readonly Action<JSC.JSObject> _send;
    private readonly Action<LogLevel, DateTime, string, bool> _onWrite;
    private readonly Action<string, Action> _post;
    private readonly Action<Action> _query;
    // Set on the engine thread by Dispose, read there by the queued push. Log.Write can fire from
    // any thread right up to the -= below, so a push may still be queued when the session closes.
    private bool _disposed;

    /// <param name="post">Lane A: queues work for the engine thread; null means "run it here, now".</param>
    /// <param name="query">Lane B: runs the history query away from the engine thread. The
    /// default is a pool thread; tests substitute a synchronous one.</param>
    public LogHandler(Action<JSC.JSObject> send, Action<string, Action> post = null, Action<Action> query = null) {
      _send = send;
      _post = post ?? ((what, work) => work());
      _query = query ?? (work => System.Threading.ThreadPool.QueueUserWorkItem(delegate { work(); }));
      _onWrite = OnLogWrite;
      X13.Log.Write += _onWrite;
    }

    /// <summary>Answers a req.log, eventually.</summary>
    /// <remarks>The request is validated here, on the engine thread, so a malformed one is
    /// rejected with the same latency it always had. Only the query itself goes to lane B:
    /// Log.History is a LiteDB scan with an in-memory sort over a collection that grows for the
    /// life of the installation, and it used to run inside the tick.
    /// <para>done is called exactly once, always on the engine thread, and never with null - a
    /// failed query becomes an error result rather than an exception, because an exception
    /// escaping the queued work would be logged by the pump without answering the client, and
    /// ws-client.js has no timeout to rescue the promise.</para></remarks>
    public void BeginHistory(JSC.JSValue request, Action<ViewOpResult> done) {
      DateTime before;
      string beforeText = request.AsString("before", null);
      if(!string.IsNullOrEmpty(beforeText)) {
        // Client always echoes back a "dt" string we previously sent it (see
        // SerializeRecord's "o" format), so RoundtripKind resolves it to the exact
        // same instant regardless of whether it carries a Z or a numeric offset.
        if(!DateTime.TryParse(beforeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out before)) {
          done(ViewOpResult.Error("log_bad_before", "Invalid 'before' timestamp: " + beforeText));
          return;
        }
      }
      else {
        // Local, not UtcNow: records are stamped with DateTime.Now (Log.cs) and LiteDB hands them
        // back as DateTimeKind.Local (see SerializeRecord's remark below), while History's
        // z["t"] < t predicate compares ticks and ignores Kind. A UTC cutoff therefore silently
        // dropped the newest N hours of the first page on a UTC+N host. Ported from ES, which
        // does convert explicitly (ES/UI/uiLog.xaml.cs buHistory_Click); the conversion is what
        // got lost, not the local default.
        before = DateTime.Now;
      }

      int count = request.AsInt("count", DefaultCount);
      if(count < 1) count = 1;
      if(count > MaxCount) count = MaxCount;

      Func<DateTime, int, IEnumerable<X13.Log.LogRecord>> history = X13.Log.History;
      if(history == null) {
        done(ViewOpResult.Error("log_history_unavailable", "Log history is not available"));
        return;
      }

      _query(delegate {
        List<X13.Log.LogRecord> records = null;
        string error = null;
        try {
          // Forcing the enumerable here is the point: Log.History is lazy, so leaving it to the
          // caller would have run the scan back on the engine thread.
          records = new List<X13.Log.LogRecord>(history(before, count));
        }
        catch(Exception ex) {
          error = ex.Message;
        }
        _post("log history", () => CompleteHistory(records, error, done));
      });
    }

    private void CompleteHistory(List<X13.Log.LogRecord> records, string error, Action<ViewOpResult> done) {
      if(_disposed) return;
      if(records == null) {
        X13.Log.Warning("LogHandler.History - {0}", error);
        done(ViewOpResult.Error("log_history_failed", error ?? "Log history failed"));
        return;
      }
      records.Reverse(); // Log.History returns newest-first; the panel wants oldest-first to prepend.

      JSL.Array items = new JSL.Array();
      int index = 0;
      foreach(X13.Log.LogRecord record in records) items[index++] = SerializeRecord(record.ll, record.dt, record.format);

      done(ViewOpResult.Success(items));
    }

    public void Dispose() {
      X13.Log.Write -= _onWrite;
      _disposed = true;
    }

    // Serializing here, on whichever thread logged, and queueing only the send: the record is
    // immutable value data, so it needs no repository access, and doing it now means a burst of
    // log lines costs the tick nothing but the writes. The push is queued rather than sent
    // directly so that the socket is written from one thread only.
    private void OnLogWrite(LogLevel ll, DateTime dt, string msg, bool local) {
      JSC.JSObject evt = SerializeRecord(ll, dt, msg);
      evt["type"] = ViewMessageTypes.EvntLog;
      _post("log push", () => { if(!_disposed) _send(evt); });
    }

    // "o" (round-trip) format always includes an explicit UTC/local offset, which is
    // required here: Log.History's DateTime values come back as DateTimeKind.Local
    // (LiteDB applies ToLocalTime() on read since this repo never sets
    // BsonMapper.Global.UtcDate), so a bare "Z" suffix would misrepresent them.
    private static JSC.JSObject SerializeRecord(LogLevel ll, DateTime dt, string msg) {
      JSC.JSObject dto = JSC.JSObject.CreateObject();
      dto["dt"] = dt.ToString("o", CultureInfo.InvariantCulture);
      dto["ll"] = (int)ll;
      dto["msg"] = msg;
      return dto;
    }
  }
}
