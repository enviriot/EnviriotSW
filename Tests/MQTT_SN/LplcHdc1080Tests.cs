using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Plc;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class LplcHdc1080Tests : RepoTestBase {
    private const string Type = "LPLC/TWI/HDC1080";
    private JSC.JSValue _template;

    [SetUp]
    public void ImportCatalog() {
      LP_Tree.ImportTypes();
      using(var stream = typeof(LplcHdc1080Tests).Assembly.GetManifestResourceStream("X13.Tests.HDC1080.xst")) {
        Assert.That(stream, Is.Not.Null);
        using(var reader = new StreamReader(stream)) Xst.Import(reader, null);
      }
      Tick();
      _template = root.Get("/$YS/TYPES/" + Type, false).GetState();
    }

    private Driver Runtime(int bus = 0, int period = 1000, int start = 0) {
      var compiler = new EP_Compiler(EP_Target.EspNode);
      Assert.That(compiler.Parse(_template.Field("LPLC.src").AsString(null)
        + "\nvar sensor=new HDC1080(" + bus + ",64," + period + "); sensor.Tick();"), Is.True, compiler.Error);
      return new Driver(compiler, bus, start);
    }

    [Test]
    public void CatalogUsesSharedConstructorChoicesAndTypedOutputs() {
      var schema = ConstructorSchema.Read(_template);
      Assert.That(schema.Errors, Is.Empty);
      Assert.That(schema.Parameters, Is.EqualTo(new[] { "TwiNr", "Address", "Period" }));
      Assert.That(schema.Find("TwiNr").Choices.Scope, Is.EqualTo("configured"));
      Assert.That(schema.Find("Address").Choices.Values.Select(v => v.AsInt(0)), Is.EqualTo(new[] { 64 }));
      Assert.That(schema.Find("Address").Format, Is.EqualTo("0x{0:X2}"));
      Assert.That(schema.Find("Period").Choices.Values.Select(v => v.AsInt(0)), Is.EqualTo(new[] { 1000, 2000, 5000, 10000, 60000 }));
      Assert.That(_template.Field("manifest.LPLC.out").Select(v => v.Key), Is.EqualTo(new[] { "T", "H", "Valid", "Status" }));
      Assert.That(ConstructorSchema.IsArray(_template.Field("LPLC.in")), Is.True);
    }

    [TestCase(0)]
    [TestCase(1)]
    public void CatalogCompilesWarningFreeWithConfiguredMaster(int bus) {
      var program = root.Get("/dev/node/custom-program", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var sensor = Topic.Declare(program, "Sensor");
      Topic.Fill(sensor, JSC.JSValue.Null, JsLib.Clone(_template.Field("manifest")), null);
      sensor.SetField("LPLC.args.TwiNr", bus);
      Tick();
      var diagram = LP_Tree.Read(program);
      diagram.AddTwiMaster("Master", bus.ToString(), "21", "22");
      var result = LP_DiagramCompiler.Compile(diagram, EP_Target.EspNode, DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems));
      Assert.That(result.Warnings, Is.Empty, string.Join("\n", result.Warnings));
      Assert.That(result.Variables["Sensor.T"], Does.StartWith("Mf"));
      Assert.That(result.Variables["Sensor.H"], Does.StartWith("Mf"));
      Assert.That(result.Variables["Sensor.Valid"], Does.StartWith("Mz"));
      Assert.That(result.Variables["Sensor.Status"], Does.StartWith("Md"));
      var ops = DevicePlcFloatTests.Disassemble(result.Hex);
      Assert.That(ops.Where(i => i.Code == EP_InstCode.API).Select(i => i.Arg[0]).Distinct(),
        Is.EquivalentTo(new byte[] { 6, 26, 31, 40, 41, 42, 43 }));
      Assert.That(ops.Any(i => i.Code == EP_InstCode.FDIV), Is.True);
      Assert.That(ops.Any(i => i.Code == EP_InstCode.DIV), Is.False);
      Assert.That(result.Source.IndexOf("var _b2 ="), Is.LessThan(result.Source.IndexOf("var _b1 =")));
      Assert.That(LP_DiagramCompiler.Compile(diagram, EP_Target.X13, null).Ok, Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    public void InitializationAndSamplingUseTheDatasheetProtocol(int bus) {
      var driver = Runtime(bus);
      Assert.That(driver.Value("Status"), Is.Zero);
      Assert.That(driver.Value("Valid"), Is.Zero);
      driver.UntilValid();
      Assert.That(driver.Hardware.Writes.Select(w => string.Join(",", w)),
        Is.EqualTo(new[] { "254", "255", "2,16,0", "2", "0" }));
      Assert.That(driver.Hardware.Controls, Is.EqualTo(new[] { 0x02010340, 0x02010340, 0x00030140, 0x02010340, 0x00010140, 0x04000240 }));
      Assert.That(driver.Hardware.Configuration, Is.EqualTo(0x1000));
      Assert.That(driver.Value("T"), Is.EqualTo(0x6664 * 165.0 / 65536 - 40).Within(0.0001));
      Assert.That(driver.Value("H"), Is.EqualTo(50));
      Assert.That(driver.Value("Status"), Is.EqualTo(1));
      Assert.That(driver.Hardware.Lines, Is.EqualTo(new[] { "HDC1080 " + bus + "/64:1" }));
      Assert.That(driver.Hardware.Levels, Is.EqualTo(new[] { 0xFFE1 }));
      Assert.That(driver.Hardware.Active, Is.Null);
    }

    [Test]
    public void StartupWaitsForSensorPowerUp() {
      var driver = Runtime();
      driver.Run(2);
      Assert.That(driver.Hardware.Controls, Is.Empty);
      driver.Run(1);
      Assert.That(driver.Hardware.Controls.Count, Is.EqualTo(1));
      driver.UntilValid();
    }

    [TestCase(0, 0)]
    [TestCase(0x4000, 0x4000)]
    [TestCase(0x8000, 0x8000)]
    [TestCase(0xFFFC, 0xFFFC)]
    [TestCase(0x5C18, 0x9ABC)]
    public void ActualBytecodeConvertsBigEndianWordsWithoutRejectingEndpoints(int rawT, int rawH) {
      var driver = Runtime();
      driver.Hardware.RawT = rawT;
      driver.Hardware.RawH = rawH;
      driver.UntilValid();
      Assert.That(driver.Value("T"), Is.EqualTo(rawT * 165.0 / 65536 - 40).Within(0.0001));
      Assert.That(driver.Value("H"), Is.EqualTo(rawH * 100.0 / 65536).Within(0.0001));
    }

    [TestCase(true, 0)]
    [TestCase(true, 0x4954)]
    [TestCase(false, 0x5010)]
    [TestCase(false, 0xFFFF)]
    public void WrongIdentityIsRejectedBeforeConfiguration(bool manufacturer, int id) {
      var driver = Runtime();
      if(manufacturer) driver.Hardware.Manufacturer = id;
      else driver.Hardware.Device = id;
      driver.Run(80);
      Assert.That(driver.Value("Status"), Is.EqualTo(-4));
      Assert.That(driver.Value("Valid"), Is.Zero);
      Assert.That(driver.Hardware.Writes.All(w => w.Length == 1), Is.True);
      Assert.That(driver.Hardware.Triggers, Is.Empty);
    }

    [TestCase(0)]
    [TestCase(0x3000)]
    [TestCase(0x1400)]
    [TestCase(0x1100)]
    public void ConfigurationReadbackRejectsWrongModeHeaterOrResolution(int configuration) {
      var driver = Runtime();
      driver.Hardware.ConfigReadback = configuration;
      driver.Run(80);
      Assert.That(driver.Value("Status"), Is.EqualTo(-5));
      Assert.That(driver.Hardware.Triggers, Is.Empty);
      Assert.That(driver.Value("Valid"), Is.Zero);
    }

    [Test]
    public void BatteryStatusBitDoesNotRejectConfiguration() {
      var driver = Runtime();
      driver.Hardware.ConfigReadback = 0x1800;
      driver.UntilValid();
      Assert.That(driver.Value("Status"), Is.EqualTo(1));
    }

    [TestCase(0x4000, -1)]
    [TestCase(0x2000, -2)]
    [TestCase(0x8000, -3)]
    public void BusErrorsRetryWithBackoffAndLogOnlyChanges(int flags, int expected) {
      var driver = Runtime();
      driver.Hardware.Fault = flags;
      driver.Run(320);
      Assert.That(driver.Value("Status"), Is.EqualTo(expected));
      Assert.That(driver.Value("Valid"), Is.Zero);
      Assert.That(driver.Hardware.Controls.Count, Is.InRange(3, 4));
      Assert.That(driver.Hardware.Lines, Is.EqualTo(new[] { "HDC1080 0/64:" + expected }));
      Assert.That(driver.Hardware.Levels, Is.EqualTo(new[] { 0xFFE2 }));
      driver.Hardware.Fault = 0;
      driver.UntilValid();
      Assert.That(driver.Hardware.Lines.Last(), Is.EqualTo("HDC1080 0/64:1"));
      Assert.That(driver.Hardware.Lines.Count, Is.EqualTo(2));
    }

    [Test]
    public void PowerLossRetainsReadingsAndRecoversWithoutProgramRestart() {
      var driver = Runtime();
      driver.UntilValid();
      double t = driver.Value("T"), h = driver.Value("H");
      driver.Hardware.Present = false;
      driver.Run(320);
      Assert.That(driver.Value("Status"), Is.EqualTo(-1));
      Assert.That(driver.Value("Valid"), Is.Zero);
      Assert.That(driver.Value("T"), Is.EqualTo(t));
      Assert.That(driver.Value("H"), Is.EqualTo(h));
      Assert.That(driver.Hardware.Lines.Count, Is.EqualTo(2));
      driver.Hardware.Configuration = 0;
      driver.Hardware.RawT = 0x8000;
      driver.Hardware.RawH = 0x4000;
      driver.Hardware.Present = true;
      driver.UntilValid();
      Assert.That(driver.Value("T"), Is.EqualTo(42.5));
      Assert.That(driver.Value("H"), Is.EqualTo(25));
      Assert.That(driver.Hardware.Configuration, Is.EqualTo(0x1000));
      Assert.That(driver.Hardware.Lines, Is.EqualTo(new[] { "HDC1080 0/64:1", "HDC1080 0/64:-1", "HDC1080 0/64:1" }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InvalidWordNeverPartiallyUpdatesReadings(bool temperature) {
      var driver = Runtime();
      driver.UntilValid();
      double t = driver.Value("T"), h = driver.Value("H");
      driver.Hardware.RawT = temperature ? 0x8001 : 0x8000;
      driver.Hardware.RawH = temperature ? 0x4000 : 0xFFFF;
      driver.Run(120);
      Assert.That(driver.Value("Status"), Is.EqualTo(-7));
      Assert.That(driver.Value("Valid"), Is.Zero);
      Assert.That(driver.Value("T"), Is.EqualTo(t));
      Assert.That(driver.Value("H"), Is.EqualTo(h));
      driver.Hardware.RawT = 0x8000;
      driver.Hardware.RawH = 0x4000;
      driver.UntilValid();
      Assert.That(driver.Value("T"), Is.EqualTo(42.5));
      Assert.That(driver.Value("H"), Is.EqualTo(25));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MismatchedReadIsDrainedBeforeRecovery(bool wrongAddress) {
      var driver = Runtime();
      if(wrongAddress) driver.Hardware.WrongAddress = true;
      else driver.Hardware.ShortRead = true;
      driver.Run(25);
      Assert.That(driver.Value("Status"), Is.EqualTo(-3));
      Assert.That(driver.Value("Valid"), Is.Zero);
      Assert.That(driver.Hardware.Active, Is.Null);
      Assert.That(driver.Hardware.Gets, Is.EqualTo(6 + (wrongAddress ? 4 : 3)), "three register words plus the drained sample");
      driver.UntilValid();
    }

    [Test]
    public void MissingTransactionIsReportedAsProtocolError() {
      var driver = Runtime();
      driver.Hardware.LoseTransaction = true;
      driver.Run(10);
      Assert.That(driver.Value("Status"), Is.EqualTo(-3));
      Assert.That(driver.Value("Valid"), Is.Zero);
      driver.UntilValid();
    }

    [Test]
    public void ConversionNotReadyNackRetriesInitialization() {
      var driver = Runtime();
      driver.Hardware.ConversionMs = 100;
      driver.Run(60);
      Assert.That(driver.Value("Status"), Is.EqualTo(-1));
      Assert.That(driver.Value("Valid"), Is.Zero);
      driver.Hardware.ConversionMs = 13;
      driver.UntilValid();
    }

    [Test]
    public void BusyTransferAndFullLogQueueDoNotRepeatRequests() {
      var driver = Runtime();
      driver.Hardware.BusyTicks = 5;
      driver.Hardware.FailLogs = true;
      driver.UntilValid();
      Assert.That(driver.Hardware.Controls.Count, Is.EqualTo(6));
      Assert.That(driver.Hardware.LogCalls, Is.EqualTo(1));
      driver.Run(210);
      Assert.That(driver.Hardware.Triggers.Count, Is.EqualTo(3));
      Assert.That(driver.Hardware.LogCalls, Is.EqualTo(1));
      Assert.That(driver.Hardware.Lines, Is.Empty);
    }

    [Test]
    public void BusyBusIsNotOverwrittenOrReadAsOwnResponse() {
      var driver = Runtime();
      driver.Hardware.ForeignBusy = true;
      driver.Run(60);
      Assert.That(driver.Hardware.Controls, Is.Empty);
      Assert.That(driver.Hardware.Gets, Is.Zero);
      driver.Hardware.ForeignBusy = false;
      driver.UntilValid();
    }

    [TestCase(1000, 0)]
    [TestCase(2000, 0)]
    [TestCase(60000, 0)]
    [TestCase(1000, 2147483500)]
    [TestCase(1000, -100)]
    public void SamplingPeriodAndClockWrapAreRespected(int period, int start) {
      var driver = Runtime(0, period, start);
      driver.UntilValid();
      driver.Run(period / 10 + 20);
      Assert.That(driver.Hardware.Triggers.Count, Is.EqualTo(2));
      Assert.That(unchecked(driver.Hardware.Triggers[1] - driver.Hardware.Triggers[0]), Is.EqualTo(period));
      Assert.That(driver.Value("Valid"), Is.EqualTo(1));
      Assert.That(driver.Hardware.Lines.Count, Is.EqualTo(1));
    }

    private sealed class Driver {
      private readonly EP_Compiler _compiler;
      private readonly DevicePlcTestVm _vm;
      internal readonly Firmware Hardware;

      internal Driver(EP_Compiler compiler, int bus, int start) {
        _compiler = compiler;
        Hardware = new Firmware(bus) { Now = start };
        _vm = new DevicePlcTestVm(compiler.Hex) { Api = Hardware.Api };
        _vm.Cycle(true);
      }
      internal double Value(string name) { return _vm.Value(_compiler.varList["sensor." + name]); }
      internal void Run(int ticks) {
        for(int i = 0; i < ticks; i++) {
          int polls = Hardware.Polls, calls = Hardware.Controls.Count;
          _vm.Cycle();
          Assert.That(Hardware.Polls - polls, Is.LessThanOrEqualTo(1));
          Assert.That(Hardware.Controls.Count - calls, Is.LessThanOrEqualTo(1));
          Hardware.Now = unchecked(Hardware.Now + 10);
        }
      }
      internal void UntilValid() {
        for(int i = 0; i < 300 && Value("Valid") == 0; i++) Run(1);
        Assert.That(Value("Valid"), Is.EqualTo(1), "Status=" + Value("Status"));
      }
    }

    private sealed class Transaction {
      internal int Write, Read, Busy, Error, Position, Address = 64;
      internal bool Ready;
      internal readonly List<int> Data = new List<int>();
      internal readonly List<int> Bytes = new List<int>();
    }

    // Модель проверяет протокол датчика и контракт завершения TWI; выполняется настоящий PLC-байткод.
    private sealed class Firmware {
      private readonly int _bus;
      private readonly StringBuilder _line = new StringBuilder();
      private int? _triggerAt;
      private int _triggerCompleted;
      internal int Now, Polls, Gets, LogCalls, Fault, Configuration;
      internal int Manufacturer = 0x5449, Device = 0x1050, RawT = 0x6664, RawH = 0x8000;
      internal int BusyTicks = 1, ConversionMs = 13;
      internal int? ConfigReadback;
      internal bool Present = true, FailLogs, ForeignBusy, ShortRead, WrongAddress, LoseTransaction;
      internal Transaction Active;
      internal readonly List<int> Controls = new List<int>();
      internal readonly List<int[]> Writes = new List<int[]>();
      internal readonly List<int> Triggers = new List<int>();
      internal readonly List<string> Lines = new List<string>();
      internal readonly List<int> Levels = new List<int>();

      internal Firmware(int bus) { _bus = bus; }
      internal void Api(DevicePlcTestVm vm, byte api) {
        if(api == 6) { vm.Push(Now); return; }
        if(api == 26) {
          var call = PrintFTestCall.Read(vm);
          Assert.That(call.Stream, Is.AnyOf(0xFFE1, 0xFFE2));
          LogCalls++;
          if(!FailLogs) foreach(byte value in call.Render()) {
            if(value == 0) {
              if(_line.Length > 0) { Lines.Add(_line.ToString()); Levels.Add(call.Stream); _line.Clear(); }
            } else _line.Append((char)value);
          }
          vm.Push(FailLogs ? 0 : 1);
          return;
        }
        Assert.That(vm.Pop(), Is.EqualTo(_bus), "TWI bus");
        switch(api) {
        case 40: Control(vm.Pop()); break;
        case 41: vm.Push(Status()); break;
        case 42: Put(vm.Pop()); break;
        case 43: vm.Push(Get()); break;
        default: throw new NotSupportedException("Unexpected driver API " + api);
        }
      }
      private void Control(int control) {
        Assert.That(Active, Is.Null, "overlapping transfer");
        Assert.That(ForeignBusy, Is.False);
        Assert.That(control & 255, Is.EqualTo(64));
        int write = (control >> 16) & 255, read = (control >> 24) & 255;
        Assert.That(write, Is.InRange(0, 3));
        Assert.That(read, Is.AnyOf(0, 2, 4));
        Assert.That((control >> 8) & 255, Is.EqualTo((write > 0 ? 1 : 0) | (read > 0 ? 2 : 0)));
        Controls.Add(control);
        Active = new Transaction { Write = write, Read = read, Busy = BusyTicks, Error = Present ? Fault : 0x4000 };
        if(write == 0) {
          Assert.That(read, Is.EqualTo(4));
          Assert.That(_triggerAt, Is.Not.Null, "measurement requires a separate pointer-write trigger");
          Assert.That(unchecked(Now - _triggerCompleted), Is.GreaterThanOrEqualTo(20), "read must wait after trigger completion");
          if(unchecked(Now - _triggerAt.Value) < ConversionMs) Active.Error = 0x4000;
          AddWord(RawT); AddWord(RawH);
          if(ShortRead) { Active.Read = 3; Active.Bytes.RemoveAt(3); ShortRead = false; }
          if(WrongAddress) { Active.Address = 65; WrongAddress = false; }
          _triggerAt = null;
        }
      }
      private void Put(int value) {
        Assert.That(Active, Is.Not.Null);
        Assert.That(Active.Data.Count, Is.LessThan(Active.Write));
        Assert.That(value, Is.InRange(0, 255));
        Active.Data.Add(value);
        if(Active.Data.Count != Active.Write) return;
        Writes.Add(Active.Data.ToArray());
        int reg = Active.Data[0];
        if(Active.Write == 3) {
          Assert.That(reg, Is.EqualTo(2));
          Assert.That(Active.Read, Is.Zero);
          if(Active.Error == 0) Configuration = (Active.Data[1] << 8) | Active.Data[2];
        } else {
          Assert.That(Active.Write, Is.EqualTo(1));
          if(reg == 0) {
            Assert.That(Active.Read, Is.Zero, "trigger needs STOP, not a combined immediate read");
            Assert.That(Configuration, Is.EqualTo(0x1000));
            _triggerAt = Now;
            Triggers.Add(Now);
          } else {
            Assert.That(Active.Read, Is.EqualTo(2));
            switch(reg) {
            case 0xFE: AddWord(Manufacturer); break;
            case 0xFF: AddWord(Device); break;
            case 2: AddWord(ConfigReadback ?? Configuration); break;
            default: throw new InvalidOperationException("Unexpected register " + reg);
            }
          }
        }
      }
      private void AddWord(int value) { Active.Bytes.Add((value >> 8) & 255); Active.Bytes.Add(value & 255); }
      private int Status() {
        Polls++;
        if(ForeignBusy) return 0x0850;
        if(Active == null) return 0;
        Assert.That(Active.Data.Count, Is.EqualTo(Active.Write));
        if(LoseTransaction) { Active = null; LoseTransaction = false; return 0; }
        if(Active.Busy-- > 0) return 64 | 0x0800;
        if(Active.Error != 0) { int status = 64 | Active.Error; Active = null; return status; }
        Active.Ready = true;
        int result = Active.Address | 0x1000 | (Active.Read << 24);
        if(Active.Read == 0) {
          if(Active.Write == 1 && Active.Data[0] == 0) _triggerCompleted = Now;
          Active = null;
        }
        return result;
      }
      private int Get() {
        Assert.That(Active, Is.Not.Null, "read after transaction closed");
        Assert.That(Active.Ready, Is.True, "read before RDY");
        Assert.That(Active.Position, Is.LessThan(Active.Read));
        Gets++;
        int value = Active.Bytes[Active.Position++];
        if(Active.Position == Active.Read) Active = null;
        return value;
      }
    }
  }
}
