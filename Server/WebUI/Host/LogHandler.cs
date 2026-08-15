///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace X13.WebUI.Host {
  // Log entries have no vid/tree namespace to route by, so this deliberately sits
  // outside the IViewProvider/vid-routed machinery (see ViewSession) - it's a single
  // global stream, handled the same way req.hello is (a fixed handler, no vid).
  internal sealed class LogHandler : IDisposable {
    private const int DefaultCount = 50;
    private const int MaxCount = 200;

    private readonly Action<JSC.JSObject> _send;
    private readonly Action<LogLevel, DateTime, string, bool> _onWrite;

    public LogHandler(Action<JSC.JSObject> send) {
      _send = send;
      _onWrite = OnLogWrite;
      X13.Log.Write += _onWrite;
    }

    public ViewOpResult HandleHistory(JSC.JSValue request) {
      DateTime before;
      string beforeText = JsLib.OfString(request["before"], null);
      if(!string.IsNullOrEmpty(beforeText)) {
        // Client always echoes back a "dt" string we previously sent it (see
        // SerializeRecord's "o" format), so RoundtripKind resolves it to the exact
        // same instant regardless of whether it carries a Z or a numeric offset.
        if(!DateTime.TryParse(beforeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out before)) {
          return ViewOpResult.Error("log_bad_before", "Invalid 'before' timestamp: " + beforeText);
        }
      }
      else {
        before = DateTime.UtcNow;
      }

      int count = JsLib.OfInt(request["count"], DefaultCount);
      if(count < 1) count = 1;
      if(count > MaxCount) count = MaxCount;

      if(X13.Log.History == null) {
        return ViewOpResult.Error("log_history_unavailable", "Log history is not available");
      }

      List<X13.Log.LogRecord> records = new List<X13.Log.LogRecord>(X13.Log.History(before, count));
      records.Reverse(); // Log.History returns newest-first; the panel wants oldest-first to prepend.

      JSL.Array items = new JSL.Array();
      int index = 0;
      foreach(X13.Log.LogRecord record in records) items[index++] = SerializeRecord(record.ll, record.dt, record.format);

      return ViewOpResult.Success(items);
    }

    public void Dispose() {
      X13.Log.Write -= _onWrite;
    }

    private void OnLogWrite(LogLevel ll, DateTime dt, string msg, bool local) {
      JSC.JSObject evt = SerializeRecord(ll, dt, msg);
      evt["type"] = ViewMessageTypes.EvntLog;
      _send(evt);
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
