///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Periphery;
using X13.Repository;

namespace X13.Tests {
  [TestFixture]
  public class PlcDiagnosticsTests : RepoTestBase {
    private bool _verbose;

    [SetUp]
    public void SaveVerbose() {
      _verbose = MQTT_SNPl.verbosePlc;
      MQTT_SNPl.verbosePlc = false;
    }

    [TearDown]
    public void RestoreVerbose() {
      MQTT_SNPl.verbosePlc = _verbose;
    }

    // Маркеры ограничивают захват этой сборкой и дожидаются доставки всех её строк журнала.
    internal static List<Tuple<LogLevel, string>> Capture(Action action) {
      var lines = new List<Tuple<LogLevel, string>>();
      string start = "log-start-" + Guid.NewGuid().ToString("N");
      string end = "log-end-" + Guid.NewGuid().ToString("N");
      using(var ready = new ManualResetEventSlim())
      using(var done = new ManualResetEventSlim()) {
        bool collecting = false;
        Action<LogLevel, DateTime, string> handler = (level, time, message) => {
          if(message == start) {
            collecting = true;
            ready.Set();
          } else if(message == end) {
            collecting = false;
            done.Set();
          } else if(collecting) {
            lines.Add(Tuple.Create(level, message));
          }
        };
        Log.Write += handler;
        try {
          Log.Debug("{0}", start);
          Assert.That(ready.Wait(10000), Is.True, "начальный маркер журнала");
          action();
          Log.Debug("{0}", end);
          Assert.That(done.Wait(10000), Is.True, "конечный маркер журнала");
        }
        finally {
          Log.Write -= handler;
        }
      }
      return lines;
    }

    private Topic Diagram(string source) {
      var device = root.Get("/dev/diagnostics", true);
      device.SetField("type", "MQTT-SN/Test");
      root.Get("/$YS/TYPES/MQTT-SN/Test", true).SetState(JsLib.ParseJson(("{\"PLC\":" + DevicePlcDeviceTests.Esp32 + "}").Replace('\'', '"')));
      var program = device.Get("Logic", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var block = program.Get("Filter", true);
      block.SetField("type", LP_Tree.FUNC);
      block.SetState(Str(source));
      Tick();
      return program;
    }

    [Test]
    public void SubscriberOwnsDiagnosticsWithoutRawLogDuplicates() {
      var messages = new List<string>();
      var compiler = new EP_Compiler();
      compiler.CMsg += (level, coords, message) => messages.Add(message);
      var lines = Capture(() => Assert.That(compiler.Parse("function F(args) { return 1; }\nF();"), Is.True));
      Assert.That(messages.Any(m => m.Contains("Unused parameter")), Is.True);
      Assert.That(lines.Any(l => messages.Contains(l.Item2) || l.Item2.Contains("Unused parameter")), Is.False);
    }

    [Test]
    public void StandaloneCompilerStillReportsFailures() {
      var compiler = new EP_Compiler();
      var lines = Capture(() => Assert.That(compiler.Parse("var x = nope;"), Is.False));
      Assert.That(lines.Any(l => l.Item1 == LogLevel.Error && l.Item2.Contains("nope")), Is.True);
      Assert.That(compiler.Error, Does.Contain("nope"));
    }

    [Test]
    public void GeneratedScaffoldingDoesNotLeakDiagnostics() {
      var program = Diagram("return A;");
      var lines = Capture(() => {
        string fingerprint;
        Assert.That(new LP_Program(program, EP_Target.EspNode).Compile(true, out fingerprint), Is.Not.Null);
      });
      Assert.That(lines.Any(l => l.Item2.Contains("Unused parameter") || l.Item2.Contains("Unused variable") || l.Item2.Contains("Unreachable code")), Is.False);
      Assert.That(lines.Any(l => l.Item1 == LogLevel.Error), Is.False);
    }

    [TestCase("return A;\nthis.M = 1;", LogLevel.Warning, "Unreachable code")]
    [TestCase("let a = A;\nreturn a + nope;", LogLevel.Error, "nope")]
    [TestCase("return A;\nif(A { }", LogLevel.Error, "SyntaxError")]
    public void DiagramDiagnosticsIdentifyBlockAndOriginalLine(string source, LogLevel expected, string message) {
      var program = Diagram(source);
      var lines = Capture(() => {
        string fingerprint;
        new LP_Program(program, EP_Target.EspNode).Compile(true, out fingerprint);
      });
      var diagnostics = lines.Where(l => l.Item2.Contains(message)).ToArray();
      Assert.That(diagnostics, Has.Length.EqualTo(1), string.Join("\n", lines.Select(l => l.Item2)));
      Assert.That(diagnostics[0].Item1, Is.EqualTo(expected));
      Assert.That(diagnostics[0].Item2, Does.StartWith(program.path + "/Filter:2 - "));
      Assert.That(lines.Any(l => l.Item2.Contains("generated JS")), Is.False);
    }

    [TestCase("function F(args) { return 1; }\nF();", LogLevel.Info, "Unused parameter")]
    [TestCase("function F() { return 1;\nreturn 2; }\nF();", LogLevel.Warning, "Unreachable code")]
    [TestCase("var x = nope;", LogLevel.Error, "nope")]
    [TestCase("if(true { }", LogLevel.Error, "Unexpected token")]
    public void Pa0DiagnosticsIdentifySourceWithoutDuplicates(string source, LogLevel expected, string message) {
      var program = root.Get("/dev/diagnostics/pa0", true);
      program.Get("src", true).SetState(Str(source));
      Tick();
      var lines = Capture(() => new X13.Periphery.DevicePLC(program, bytes => { }, EP_Target.X13).Build());
      var diagnostics = lines.Where(l => l.Item2.Contains(message)).ToArray();
      Assert.That(diagnostics, Has.Length.EqualTo(1), string.Join("\n", lines.Select(l => l.Item2)));
      Assert.That(diagnostics[0].Item1, Is.EqualTo(expected));
      Assert.That(diagnostics[0].Item2, Does.StartWith(program.path + "/src"));
    }
  }
}
