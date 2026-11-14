///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace X13.WebUI {
  // Общий поток журнала не относится к пространству vid дерева.
  internal sealed class LogHandler : IDisposable {
    private const int DefaultCount = 50;
    private const int MaxCount = 200;

    private readonly Action<JSC.JSObject> _send;
    private readonly Action<LogEntry> _onWrite;
    private readonly Action<string, Action> _post;
    private readonly Action<Action> _query;
    private readonly Func<string, int, IReadOnlyList<LogEntry>> _history;
    // Уже поставленный push может пережить снятие подписки при закрытии сеанса.
    private bool _disposed;

    /// <param name="post">Передача ответа на поток движка; в тестах может выполняться сразу.</param>
    /// <param name="query">Файловое чтение вне движкового потока.</param>
    public LogHandler(Action<JSC.JSObject> send, Action<string, Action> post = null, Action<Action> query = null,
                      Func<string, int, IReadOnlyList<LogEntry>> history = null) {
      _send = send;
      _post = post ?? ((what, work) => work());
      _query = query ?? (work => System.Threading.ThreadPool.QueueUserWorkItem(delegate { work(); }));
      _history = history ?? X13.Log.ReadHistory;
      _onWrite = OnLogWrite;
      X13.Log.RecordWritten += _onWrite;
    }

    /// <summary>Читает страницу перед файловым ID и возвращает ответ на потоке движка.</summary>
    public void BeginHistory(JSC.JSValue request, Action<ViewOpResult> done) {
      if(request.Field("before").Defined) {
        done(ViewOpResult.Error("log_bad_before_id", "Timestamp paging is no longer supported; use beforeId"));
        return;
      }
      var boundary = request.Field("beforeId");
      if(boundary.Defined && !boundary.IsNull && (!boundary.Is<string>() || string.IsNullOrEmpty(boundary.AsString(null)))) {
        done(ViewOpResult.Error("log_bad_before_id", "beforeId must be a non-empty log record ID"));
        return;
      }
      string beforeId = boundary.AsString(null);

      int count = request.AsInt("count", DefaultCount);
      if(count < 1) count = 1;
      if(count > MaxCount) count = MaxCount;

      _query(delegate {
        IReadOnlyList<LogEntry> records = null;
        string error = null;
        string code = "log_history_failed";
        try {
          records = _history(beforeId, count);
        }
        catch(ArgumentException ex) {
          code = "log_bad_before_id";
          error = ex.Message;
        }
        catch(InvalidOperationException ex) {
          code = "log_history_unavailable";
          error = ex.Message;
        }
        catch(Exception ex) {
          error = ex.Message;
        }
        _post("log history", () => CompleteHistory(records, code, error, done));
      });
    }

    private void CompleteHistory(IReadOnlyList<LogEntry> records, string code, string error, Action<ViewOpResult> done) {
      if(_disposed) return;
      if(records == null) {
        X13.Log.Warning("LogHandler.History - {0}", error);
        done(ViewOpResult.Error(code, error ?? "Log history failed"));
        return;
      }
      JSL.Array items = new JSL.Array();
      int index = 0;
      foreach(LogEntry record in records) items[index++] = SerializeRecord(record);

      done(ViewOpResult.Success(items));
    }

    public void Dispose() {
      X13.Log.RecordWritten -= _onWrite;
      _disposed = true;
    }

    // Данные неизменяемы; отправка сокету выполняется только на потоке движка.
    private void OnLogWrite(LogEntry entry) {
      JSC.JSObject evt = SerializeRecord(entry);
      evt["type"] = ViewMessageTypes.EvntLog;
      _post("log push", () => { if(!_disposed) _send(evt); });
    }

    private static JSC.JSObject SerializeRecord(LogEntry entry) {
      JSC.JSObject dto = JSC.JSObject.CreateObject();
      dto["id"] = entry.Id == null ? JSC.JSValue.Null : new JSL.String(entry.Id);
      dto["dt"] = entry.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
      dto["ll"] = (int)entry.Level;
      dto["msg"] = entry.Message;
      return dto;
    }
  }
}
