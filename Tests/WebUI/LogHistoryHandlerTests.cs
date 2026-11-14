using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using X13.WebUI;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Tests {
  [TestFixture]
  public class LogHistoryHandlerTests : RepoTestBase {
    [Test]
    public void HistoryUsesBeforeIdAndReturnsOnePostedResponse() {
      var queries = new List<Action>();
      var posted = new List<Action>();
      string before = null;
      int count = 0, responses = 0;
      ViewOpResult result = null;
      var entries = new[] {
        new LogEntry("older", new DateTime(2026, 11, 11, 2, 30, 0), LogLevel.Debug, "one"),
        new LogEntry("newer", new DateTime(2026, 11, 11, 2, 30, 0), LogLevel.Info, "two")
      };
      using(var handler = new LogHandler(_ => { }, (name, work) => { if(name == "log history") posted.Add(work); },
          queries.Add, (id, size) => { before = id; count = size; return entries; })) {
        handler.BeginHistory(JsLib.ParseJson("{\"beforeId\":\"anchor\",\"count\":2}"), value => { result = value; responses++; });
        Assert.That(responses, Is.Zero);
        Assert.That(queries.Count, Is.EqualTo(1));
        queries[0]();
        Assert.That(before, Is.EqualTo("anchor"));
        Assert.That(count, Is.EqualTo(2));
        Assert.That(responses, Is.Zero);
        Assert.That(posted.Count, Is.EqualTo(1));
        posted[0]();
        Assert.That(responses, Is.EqualTo(1));
        Assert.That(result.Ok, Is.True);
        var items = (JSL.Array)result.Data;
        Assert.That((int)items.length, Is.EqualTo(2));
        Assert.That(items[0].AsString("id", null), Is.EqualTo("older"));
        Assert.That(items[1].AsString("id", null), Is.EqualTo("newer"));
        Assert.That(items[0].AsString("dt", null), Is.EqualTo("2026-11-11T02:30:00.000"));
      }
    }

    [TestCase("{\"before\":\"2026-11-11\"}")]
    [TestCase("{\"beforeId\":42}")]
    [TestCase("{\"beforeId\":\"\"}")]
    public void InvalidBoundaryDoesNotStartIo(string json) {
      int queries = 0, responses = 0;
      using(var handler = new LogHandler(_ => { }, null, _ => { queries++; })) {
        handler.BeginHistory(JsLib.ParseJson(json), result => {
          responses++;
          Assert.That(result.Ok, Is.False);
          Assert.That(result.ErrorCode, Is.EqualTo("log_bad_before_id"));
        });
      }
      Assert.That(queries, Is.Zero);
      Assert.That(responses, Is.EqualTo(1));
    }

    [Test]
    public void HistoryResponsesSuppressTransportTrace() {
      JSC.JSObject response = null;
      bool quiet = false;
      using(var signal = new ManualResetEventSlim())
      using(var session = new ViewSession((message, noLog) => {
        if(message.AsString("type", null) != "resp.log") return;
        response = message;
        quiet = noLog;
        signal.Set();
      }, logHistory: (id, count) => new LogEntry[0])) {
        session.HandleLog(JsLib.ParseJson("{\"type\":\"req.log\",\"id\":1}"));
        Assert.That(signal.Wait(5000), Is.True);
        Assert.That(response.AsString("type", null), Is.EqualTo("resp.log"));
        Assert.That(quiet, Is.True);
      }
    }

    [TestCase(0, 1)]
    [TestCase(1000, 200)]
    public void PageSizeRemainsBounded(int requested, int expected) {
      int count = 0;
      using(var handler = new LogHandler(_ => { }, null, work => work(),
          (id, size) => { count = size; return new LogEntry[0]; })) {
        handler.BeginHistory(JsLib.ParseJson("{\"count\":" + requested + "}"), result => {
          Assert.That(result.Ok, Is.True);
          Assert.That((int)((JSL.Array)result.Data).length, Is.Zero);
        });
      }
      Assert.That(count, Is.EqualTo(expected));
    }

    [TestCase("disabled", "log_history_unavailable")]
    [TestCase("boundary", "log_bad_before_id")]
    [TestCase("io", "log_history_failed")]
    public void ReaderErrorsAreNotEmptySuccessfulPages(string fault, string code) {
      ViewOpResult answer = null;
      using(var handler = new LogHandler(_ => { }, (name, work) => { if(name == "log history") work(); }, work => work(),
          (id, size) => {
            if(fault == "disabled") throw new InvalidOperationException("disabled");
            if(fault == "boundary") throw new ArgumentException("expired");
            throw new IOException("unreadable");
          })) {
        handler.BeginHistory(JsLib.ParseJson("{}"), result => answer = result);
      }
      Assert.That(answer.Ok, Is.False);
      Assert.That(answer.ErrorCode, Is.EqualTo(code));
      Assert.That(answer.Data, Is.Null);
    }

    [Test]
    public void ClosedSessionDoesNotReceiveDelayedHistory() {
      Action complete = null;
      int responses = 0;
      var handler = new LogHandler(_ => { }, (name, work) => { if(name == "log history") complete = work; },
        work => work(), (id, size) => new LogEntry[0]);
      try {
        handler.BeginHistory(JsLib.ParseJson("{}"), _ => responses++);
      }
      finally {
        handler.Dispose();
      }
      Assert.That(complete, Is.Not.Null);
      complete();
      Assert.That(responses, Is.Zero);
    }

    [Test]
    public void DisabledFileLoggingStillProducesLiveEventsWithoutFileId() {
      string marker = "live-" + Guid.NewGuid().ToString("N");
      JSC.JSObject received = null;
      using(var signal = new ManualResetEventSlim())
      using(var handler = new LogHandler(evt => {
        if(evt.AsString("msg", null) != marker) return;
        received = evt;
        signal.Set();
      })) {
        Log.Info(marker);
        Assert.That(signal.Wait(5000), Is.True);
        Assert.That(received.Field("id").IsNull, Is.True);
        Assert.That(received.AsString("type", null), Is.EqualTo("evnt.log"));
        Assert.That(received.AsString("dt", null), Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$"));
      }
    }
  }
}
