using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Plc;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class LplcBme280Tests : RepoTestBase {
    private const string Type = "LPLC/TWI/BME280";
    private JSC.JSValue _template;

    private const string Firmware = @"
var now = 0, expectedBus = 0, expectedAddress = 118, active = null, calls = [], writes = [], registers = [];
var polls = 0, gets = 0, delay = 1, failReg = -1, failCode = 0x4000, shortRead = false;
var sensorBusy = 0, copying = 0, failLogs = false, logCalls = [], lines = [], line = '';
var LOG_INFO = 0xFFE1, LOG_WARN = 0xFFE2;
function Float(v) { return NativeFloat(v); }
function Int32(v) { return v | 0; }
function getMilliseconds() { return now; }
function checkBus(bus) { if(bus !== expectedBus) throw new Error('wrong bus'); }
function SendByte(stream, value) {
  logCalls.push([stream,value]);
  if(value === 0) { if(line) lines.push(line); line = ''; return true; }
  if(failLogs) return false;
  line += String.fromCharCode(value); return true;
}
function PrintF(stream, format) {
  var values=arguments, index=2;
  var text=format.replace(/%d/g, function() { return String(values[index++]); });
  logCalls.push([stream,text]);
  if(failLogs) return false;
  for(var i=0; i<text.length; i++) {
    if(text.charCodeAt(i) === 0) { if(line) lines.push(line); line=''; }
    else line += text[i];
  }
  return true;
}
function TwiBusControl(bus, ctrl) {
  checkBus(bus);
  if(active !== null) throw new Error('overlapping transaction');
  var address = ctrl & 255, write = (ctrl >>> 16) & 255, read = (ctrl >>> 24) & 255;
  if(address !== expectedAddress || write < 1 || write > 2 || read > 32) throw new Error('invalid control');
  calls.push([address,write,read]);
  active = {address:address,write:write,read:read,data:[],bytes:[],busy:delay,reg:-1,pos:0};
}
function TwiBusPutByte(bus, value) {
  checkBus(bus);
  if(active === null || active.data.length >= active.write) throw new Error('unexpected write');
  active.data.push(value & 255);
  if(active.data.length !== active.write) return;
  active.reg = active.data[0];
  writes.push(active.data.slice());
  if(active.write === 2) registers[active.reg] = active.data[1];
  for(var i=0; i<active.read; i++) active.bytes.push(registers[active.reg+i] || 0);
  if(active.reg === 0xF3) active.bytes[0] = (copying-- > 0 ? 1 : 0) | (sensorBusy-- > 0 ? 8 : 0);
}
function TwiBusStatus(bus) {
  checkBus(bus); polls++;
  if(active === null) return 0;
  if(active.data.length !== active.write) throw new Error('incomplete write');
  if(active.busy-- > 0) return active.address | 0x0800;
  if(active.reg === failReg) { var e = active.address | failCode; active = null; return e; }
  var read = active.read;
  if(shortRead && active.reg === 0xF7) { read--; active.bytes.pop(); active.read = read; shortRead = false; }
  var result = active.address | 0x1000 | (read << 24);
  if(read === 0) active = null;
  return result;
}
function TwiBusGetByte(bus) {
  checkBus(bus); gets++;
  if(active === null || active.busy >= 0 || active.pos >= active.read) throw new Error('unexpected read');
  var result = active.bytes[active.pos++];
  if(active.pos === active.read) active = null;
  return result;
}
function word(reg,value) { registers[reg]=value & 255; registers[reg+1]=(value >> 8) & 255; }
function raw20(reg,value) { registers[reg]=(value >> 12) & 255; registers[reg+1]=(value >> 4) & 255; registers[reg+2]=(value << 4) & 240; }
function humidityCalibration(h1,h2,h3,h4,h5,h6) {
  registers[0xA1]=h1; word(0xE1,h2); registers[0xE3]=h3;
  registers[0xE4]=(h4 >> 4) & 255; registers[0xE5]=(h4 & 15) | ((h5 & 15) << 4);
  registers[0xE6]=(h5 >> 4) & 255; registers[0xE7]=h6 & 255;
}
function sample(t,p,h) { raw20(0xFA,t); raw20(0xF7,p); registers[0xFD]=(h >> 8) & 255; registers[0xFE]=h & 255; }
word(0x88,27504); word(0x8A,26435); word(0x8C,-1000);
word(0x8E,36477); word(0x90,-10685); word(0x92,3024); word(0x94,2855);
word(0x96,140); word(0x98,-7); word(0x9A,15500); word(0x9C,-14600); word(0x9E,6000);
humidityCalibration(75,362,0,334,50,30);
sample(519888,415148,32257);
function step() {
  var beforePolls = polls, beforeCalls = calls.length;
  sensor.Tick();
  if(polls-beforePolls > 1 || calls.length-beforeCalls > 1) throw new Error('unbounded tick');
  now = (now+10) | 0;
}
function run(n) { for(var i=0; i<n; i++) step(); }
function untilValid() { for(var i=0; i<300 && !sensor.Valid; i++) step(); }
";

    [SetUp]
    public void ImportCatalog() {
      LP_Tree.ImportTypes();
      using(var stream = typeof(LplcBme280Tests).Assembly.GetManifestResourceStream("X13.Tests.BME280.xst")) {
        Assert.That(stream, Is.Not.Null);
        using(var reader = new StreamReader(stream)) Xst.Import(reader, null);
      }
      Tick();
      _template = root.Get("/$YS/TYPES/" + Type, false).GetState();
    }

    private JSC.Context Runtime(int chip = 0x60, int bus = 0, int address = 118, int start = 0) {
      var context = new JSC.Context();
      context.DefineVariable("NativeFloat").Assign(new JSC.Functions.ExternalFunction((self, args) => new NiL.JS.BaseLibrary.Number((double)args[0])));
      string source = Regex.Replace(_template.Field("LPLC.src").AsString(null), @"new (Float|Int32)\(([^()]*)\)", "$1($2)");
      context.Eval(Firmware + source + "\nnow=" + start + "; expectedBus=" + bus + "; expectedAddress=" + address
        + "; registers[0xD0]=" + chip + "; var sensor = new BME280(expectedBus,expectedAddress,1000);");
      return context;
    }
    private static double Number(JSC.Context context, string code) { return (double)context.Eval(code); }

    [Test]
    public void CatalogUsesSharedConstructorChoicesAndTypedOutputs() {
      var schema = ConstructorSchema.Read(_template);
      Assert.That(schema.Errors, Is.Empty);
      Assert.That(schema.Parameters, Is.EqualTo(new[] { "TwiNr", "Address", "Period" }));
      Assert.That(schema.Find("TwiNr").Choices.Scope, Is.EqualTo("configured"));
      Assert.That(schema.Find("Address").Choices.Values.Select(v => v.AsInt(0)), Is.EqualTo(new[] { 118, 119 }));
      Assert.That(schema.Find("Address").Format, Is.EqualTo("0x{0:X2}"));
      Assert.That(schema.Find("Period").Choices.Values.Select(v => v.AsInt(0)), Does.Contain(1000));
      Assert.That(_template.Field("manifest.LPLC.out").Select(v => v.Key), Is.EqualTo(new[] { "T", "P", "H", "Valid", "Status", "Chip" }));
      Assert.That(ConstructorSchema.IsArray(_template.Field("LPLC.in")), Is.True);
    }

    [TestCase(0, 118)]
    [TestCase(0, 119)]
    [TestCase(1, 118)]
    [TestCase(1, 119)]
    public void CatalogCompilesWithConfiguredMaster(int bus, int address) {
      var program = root.Get("/dev/node/custom-program", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var sensor = Topic.Declare(program, "Sensor");
      Topic.Fill(sensor, JSC.JSValue.Null, JsLib.Clone(_template.Field("manifest")), null);
      sensor.SetField("LPLC.args.TwiNr", bus);
      sensor.SetField("LPLC.args.Address", address);
      Tick();
      var diagram = LP_Tree.Read(program);
      diagram.AddTwiMaster("Master", bus.ToString(), "21", "22");
      var result = LP_DiagramCompiler.Compile(diagram, EP_Target.EspNode, DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems));
      Assert.That(result.Warnings, Is.Empty, string.Join("\n", result.Warnings));
      foreach(string name in new[] { "T", "P", "H" }) Assert.That(result.Variables["Sensor." + name], Does.StartWith("Mf"));
      Assert.That(result.Variables["Sensor.Valid"], Does.StartWith("Mz"));
      var ops = DevicePlcFloatTests.Disassemble(result.Hex);
      var apis = ops.Where(i => i.Code == EP_InstCode.API).Select(i => i.Arg[0]).Distinct();
      Assert.That(apis, Is.EquivalentTo(new byte[] { 6, 26, 31, 40, 41, 42, 43 }));
      Assert.That(ops.Any(i => i.Code == EP_InstCode.FDIV), Is.True);
      Assert.That(ops.Any(i => i.Code == EP_InstCode.DIV), Is.False, "compensation must not use integer division");
      Assert.That(result.Source.IndexOf("var _b2 ="), Is.LessThan(result.Source.IndexOf("var _b1 =")));
      Assert.That(LP_DiagramCompiler.Compile(diagram, EP_Target.X13, null).Ok, Is.False);
    }

    [TestCase(0x60, 0, 118)]
    [TestCase(0x58, 0, 119)]
    [TestCase(0x60, 1, 119)]
    [TestCase(0x58, 1, 118)]
    public void ReferenceSampleAndInitializationWorkForBothChips(int chip, int bus, int address) {
      var context = Runtime(chip, bus, address);
      context.Eval("untilValid();");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
      Assert.That(Number(context, "sensor.T"), Is.EqualTo(25.08248).Within(0.002));
      Assert.That(Number(context, "sensor.P"), Is.EqualTo(1006.5327).Within(0.03));
      if(chip == 0x60) Assert.That(Number(context, "sensor.H"), Is.InRange(0.0, 100.0));
      else Assert.That(Number(context, "sensor.H"), Is.EqualTo(-1.0));
      Assert.That(context.Eval("sensor.Chip").AsInt(0), Is.EqualTo(chip));
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(1));
      Assert.That(context.Eval("JSON.stringify(writes.filter(function(w){return w.length===2;}))").AsString(null),
        Is.EqualTo(chip == 0x60 ? "[[224,182],[242,1],[245,0],[244,37]]" : "[[224,182],[245,0],[244,37]]"));
      Assert.That(context.Eval("active === null").AsBool(false), Is.True);
      Assert.That(context.Eval("lines.length").AsInt(0), Is.EqualTo(1));
    }

    [Test]
    public void HumidityCoefficientsAreSignedAndCompensationIncludesH3() {
      var context = Runtime();
      context.Eval("humidityCalibration(75,362,7,-334,-50,-30); untilValid();");
      Assert.That(context.Eval("sensor.H4").AsInt(0), Is.EqualTo(-334));
      Assert.That(context.Eval("sensor.H5").AsInt(0), Is.EqualTo(-50));
      Assert.That(context.Eval("sensor.H6").AsInt(0), Is.EqualTo(-30));
      context.Eval("sensor.H4=334; sensor.H5=50; sensor.H6=30; sensor.Compensate(519888,415148,32257);");
      // Независимый целочисленный вариант компенсации из формулы Bosch; long исключает переполнение теста.
      long fine = 128422, delta = fine - 76800;
      long a = ((32257L << 14) - (334L << 20) - 50 * delta + 16384) >> 15;
      long b = (((((delta * 30) >> 10) * (((delta * 7) >> 11) + 32768)) >> 10) + 2097152) * 362 + 8192;
      long h = a * (b >> 14);
      h -= (((((h >> 15) * (h >> 15)) >> 7) * 75) >> 4);
      double expected = System.Math.Max(0, System.Math.Min(419430400L, h)) / 4194304.0;
      Assert.That(Number(context, "sensor.H"), Is.EqualTo(expected).Within(0.02));
    }

    [TestCase(0)]
    [TestCase(65535)]
    public void InvalidPressureCalibrationIsRejected(int p1) {
      var context = Runtime();
      context.Eval("word(0x8E," + p1 + "); run(70);");
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(-5));
      Assert.That(context.Eval("sensor.Valid").AsBool(true), Is.False);
      Assert.That(context.Eval("writes.some(function(w){return w[0]===0xF4;})").AsBool(true), Is.False);
    }

    [TestCase(0)]
    [TestCase(0x56)]
    [TestCase(0x61)]
    [TestCase(0xFF)]
    public void UnsupportedChipIsNotConfigured(int chip) {
      var context = Runtime(chip);
      context.Eval("run(10);");
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(-4));
      Assert.That(context.Eval("writes.length").AsInt(0), Is.EqualTo(1));
    }

    [TestCase(0x4000, -1)]
    [TestCase(0x2000, -2)]
    [TestCase(0x8000, -3)]
    public void BusErrorsRetryWithBackoffAndLogOnlyChanges(int flags, int status) {
      var context = Runtime();
      context.Eval("failReg=0xD0; failCode=" + flags + "; run(300);");
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(status));
      Assert.That(context.Eval("sensor.Valid").AsBool(true), Is.False);
      Assert.That(context.Eval("calls.length").AsInt(0), Is.InRange(2, 3));
      Assert.That(context.Eval("lines.length").AsInt(0), Is.EqualTo(1));
      context.Eval("failReg=-1; untilValid();");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
      Assert.That(context.Eval("lines.length").AsInt(0), Is.EqualTo(2));
    }

    [Test]
    public void InvalidSampleRetainsAllLastGoodReadingsAndRecovers() {
      var context = Runtime();
      context.Eval("untilValid(); var previous=JSON.stringify([sensor.T,sensor.P,sensor.H]); sample(0x80000,415148,32257); run(120);");
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(-7));
      Assert.That(context.Eval("sensor.Valid").AsBool(true), Is.False);
      Assert.That(context.Eval("JSON.stringify([sensor.T,sensor.P,sensor.H])===previous").AsBool(false), Is.True);
      context.Eval("sample(519888,415148,32257); untilValid();");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
    }

    [Test]
    public void ShortReadIsDrainedBeforeRetry() {
      var context = Runtime();
      context.Eval("shortRead=true; run(70);");
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(-3));
      Assert.That(context.Eval("active === null").AsBool(false), Is.True);
      context.Eval("untilValid();");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SensorStatusWaitHasABoundedTimeout(bool calibration) {
      var context = Runtime();
      context.Eval(calibration ? "copying=1000; run(70);" : "sensorBusy=1000; run(90);");
      Assert.That(context.Eval("sensor.Status").AsInt(0), Is.EqualTo(-6));
      Assert.That(context.Eval("sensor.Valid").AsBool(true), Is.False);
      Assert.That(context.Eval("active === null").AsBool(false), Is.True);
    }

    [TestCase(2147483500)]
    [TestCase(-100)]
    public void SamplingContinuesAcrossClockWrap(int start) {
      var context = Runtime(start: start);
      context.Eval("run(330);");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
      Assert.That(context.Eval("writes.filter(function(w){return w[0]===0xF4;}).length").AsInt(0), Is.InRange(3, 4));
      Assert.That(context.Eval("lines.length").AsInt(0), Is.EqualTo(1));
    }

    [Test]
    public void BusyTransferAndFullLogQueueDoNotRepeatRequests() {
      var context = Runtime();
      context.Eval("delay=4; failLogs=true; untilValid();");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
      Assert.That(context.Eval("writes.filter(function(w){return w[0]===0xF4;}).length").AsInt(0), Is.EqualTo(1));
      Assert.That(context.Eval("lines.length").AsInt(-1), Is.Zero);
      context.Eval("run(100);");
      Assert.That(context.Eval("logCalls.length").AsInt(0), Is.EqualTo(1));
    }

    [Test]
    public void BusyBusIsNotOverwrittenOrReadAsOwnResponse() {
      var context = Runtime();
      context.Eval("active={address:42,write:1,read:1,data:[0],bytes:[99],busy:20,reg:0,pos:0}; run(10);");
      Assert.That(context.Eval("calls.length").AsInt(-1), Is.Zero);
      Assert.That(context.Eval("gets").AsInt(-1), Is.Zero);
      context.Eval("active=null; untilValid();");
      Assert.That(context.Eval("sensor.Valid").AsBool(false), Is.True);
    }

    [TestCase(400000, 415148, 28000)]
    [TestCase(519888, 415148, 32257)]
    [TestCase(600000, 450000, 38000)]
    public void SinglePrecisionCompensationAgreesWithRuntime(int rawT, int rawP, int rawH) {
      var context = Runtime();
      context.Eval("untilValid(); sensor.H3=7;");
      Assert.That(context.Eval("sensor.Compensate(" + rawT + "," + rawP + "," + rawH + ")").AsBool(false), Is.True);
      float a = rawT / 16384f - 27504f / 1024f;
      float b = rawT / 131072f - 27504f / 8192f;
      float fine = a * 26435f + b * b * -1000f;
      float t = fine / 5120f;
      fine = (int)fine;
      a = fine / 2f - 64000f;
      b = a * a * -7f / 32768f + a * 140f * 2f;
      b = b / 4f + 2855f * 65536f;
      float scale = (3024f * a * a / 524288f + -10685f * a) / 524288f;
      scale = (1f + scale / 32768f) * 36477f;
      float p = (1048576f - rawP - b / 4096f) * 6250f / scale;
      p = (p + (6000f * p * p / 2147483648f + p * -14600f / 32768f + 15500f) / 16f) / 100f;
      float delta = fine - 76800f;
      float curve = 1f + 7f * delta / 67108864f;
      float h = (rawH - (334f * 64f + 50f * delta / 16384f)) * (362f / 65536f) * (1f + 30f * delta * curve / 67108864f);
      h *= 1f - 75f * h / 524288f;
      h = System.Math.Max(0f, System.Math.Min(100f, h));
      Assert.That(Number(context, "sensor.T"), Is.EqualTo(t).Within(0.002));
      Assert.That(Number(context, "sensor.P"), Is.EqualTo(p).Within(0.03));
      Assert.That(Number(context, "sensor.H"), Is.EqualTo(h).Within(0.02));
    }

    [Test]
    public void FailedCompensationNeverPartiallyUpdatesOutputs() {
      var context = Runtime();
      context.Eval("untilValid(); var previous=JSON.stringify([sensor.T,sensor.P,sensor.H]); sensor.P1=0;");
      Assert.That(context.Eval("sensor.Compensate(600000,415148,32257)").AsBool(true), Is.False);
      Assert.That(context.Eval("JSON.stringify([sensor.T,sensor.P,sensor.H])===previous").AsBool(false), Is.True);
    }

    [TestCase(400000, 415148, 28000)]
    [TestCase(519888, 415148, 32257)]
    [TestCase(600000, 450000, 38000)]
    public void ActualCompensationBytecodeMatchesReference(int rawT, int rawP, int rawH) {
      var source = _template.Field("LPLC.src").AsString(null) + @"
var sensor = new BME280(0,118,1000);
sensor.Chip=96;
sensor.T1=27504; sensor.T2=26435; sensor.T3=-1000;
sensor.P1=36477; sensor.P2=-10685; sensor.P3=3024; sensor.P4=2855;
sensor.P5=140; sensor.P6=-7; sensor.P7=15500; sensor.P8=-14600; sensor.P9=6000;
sensor.H1=75; sensor.H2=362; sensor.H3=7; sensor.H4=334; sensor.H5=50; sensor.H6=30;
" + "sensor.Valid=sensor.Compensate(" + rawT + "," + rawP + "," + rawH + ");";
      var compiler = new EP_Compiler(EP_Target.EspNode);
      Assert.That(compiler.Parse(source), Is.True, compiler.Error);
      var vm = new DevicePlcTestVm(compiler.Hex) {
        Api = (machine, api) => { Assert.That(api, Is.EqualTo(6)); machine.Push(0); }
      };
      vm.Cycle(true); vm.Cycle();
      Assert.That(vm.Value(compiler.varList["sensor.Valid"]), Is.EqualTo(1));
      var context = Runtime();
      context.Eval("untilValid(); sensor.H3=7; sensor.Compensate(" + rawT + "," + rawP + "," + rawH + ");");
      Assert.That(vm.Value(compiler.varList["sensor.T"]), Is.EqualTo(Number(context, "sensor.T")).Within(0.002));
      Assert.That(vm.Value(compiler.varList["sensor.P"]), Is.EqualTo(Number(context, "sensor.P")).Within(0.03));
      Assert.That(vm.Value(compiler.varList["sensor.H"]), Is.EqualTo(Number(context, "sensor.H")).Within(0.02));
    }

    [TestCase(0x60, 0, 118)]
    [TestCase(0x58, 0, 119)]
    [TestCase(0x60, 1, 119)]
    [TestCase(0x58, 1, 118)]
    public void CompiledDriverSamplesRetainsAndRecovers(int chip, int bus, int address) {
      var firmware = Runtime(chip, bus, address);
      var compiler = new EP_Compiler(EP_Target.EspNode);
      Assert.That(compiler.Parse(_template.Field("LPLC.src").AsString(null) + "\nvar sensor=new BME280(" + bus + "," + address + ",1000); sensor.Tick();"), Is.True, compiler.Error);
      var vm = new DevicePlcTestVm(compiler.Hex) {
        Api = (machine, api) => {
          if(api == 26) {
            var call = PrintFTestCall.Read(machine);
            string arguments = call.Stream + "," + JsLib.Stringify(new NiL.JS.BaseLibrary.String(call.Format));
            if(call.Values.Length > 0) arguments += "," + string.Join(",", call.Values);
            machine.Push(firmware.Eval("PrintF(" + arguments + ")").AsBool(false) ? 1 : 0);
            return;
          }
          string name;
          int count;
          switch(api) {
          case 6: name = "getMilliseconds"; count = 0; break;
          case 23: name = "SendByte"; count = 2; break;
          case 40: name = "TwiBusControl"; count = 2; break;
          case 41: name = "TwiBusStatus"; count = 1; break;
          case 42: name = "TwiBusPutByte"; count = 2; break;
          case 43: name = "TwiBusGetByte"; count = 1; break;
          default: throw new System.NotSupportedException("Unexpected driver API " + api);
          }
          var args = new int[count];
          for(int i = 0; i < count; i++) args[i] = machine.Pop();
          var result = firmware.Eval(name + "(" + string.Join(",", args) + ")");
          if(api == 23) machine.Push(result.AsBool(false) ? 1 : 0);
          else if(api != 40 && api != 42) machine.Push(result.AsInt(0));
        }
      };
      System.Action<int> run = ticks => {
        for(int i = 0; i < ticks; i++) {
          double polls = Number(firmware, "polls"), calls = Number(firmware, "calls.length");
          vm.Cycle();
          Assert.That(Number(firmware, "polls") - polls, Is.LessThanOrEqualTo(1));
          Assert.That(Number(firmware, "calls.length") - calls, Is.LessThanOrEqualTo(1));
          firmware.Eval("now=(now+10)|0;");
        }
      };
      vm.Cycle(true);
      run(90);
      Assert.That(vm.Value(compiler.varList["sensor.Valid"]), Is.EqualTo(1));
      Assert.That(vm.Value(compiler.varList["sensor.Status"]), Is.EqualTo(1));
      Assert.That(vm.Value(compiler.varList["sensor.Chip"]), Is.EqualTo(chip));
      Assert.That(vm.Value(compiler.varList["sensor.T"]), Is.EqualTo(25.08248).Within(0.002));
      Assert.That(vm.Value(compiler.varList["sensor.P"]), Is.EqualTo(1006.5327).Within(0.03));
      var pins = new[] { "T", "P", "H" };
      var readings = pins.Select(pin => vm.Value(compiler.varList["sensor." + pin])).ToArray();
      if(chip == 0x58) Assert.That(readings[2], Is.EqualTo(-1));
      else Assert.That(readings[2], Is.InRange(0.0, 100.0));
      firmware.Eval("sample(0x80000,415148,32257);");
      run(120);
      Assert.That(vm.Value(compiler.varList["sensor.Valid"]), Is.Zero);
      Assert.That(vm.Value(compiler.varList["sensor.Status"]), Is.EqualTo(-7));
      Assert.That(pins.Select(pin => vm.Value(compiler.varList["sensor." + pin])), Is.EqualTo(readings));
      firmware.Eval("sample(519888,415148,32257);");
      run(120);
      Assert.That(vm.Value(compiler.varList["sensor.Valid"]), Is.EqualTo(1));
      Assert.That(vm.Value(compiler.varList["sensor.Status"]), Is.EqualTo(1));
    }
  }
}
