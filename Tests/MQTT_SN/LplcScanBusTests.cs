using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class LplcScanBusTests : RepoTestBase {
    private const string Type = "LPLC/TWI/SCAN_BUS";
    private JSC.JSValue _template;

    // EspNode plc_io.c: zero-length probes, status access flags at bits 8..15,
    // and a terminal status read closes the transaction.
    private const string Firmware = @"
var calls = [], polls = 0, active = null, answers = {}, delay = 1, expectedBus = 1;
var LOG_INFO = 0xFFE1, records = [], line = '', streamCalls = [], room = 100000;
function SendByte(stream, value) {
  if(stream !== LOG_INFO) throw new Error('wrong log stream');
  streamCalls.push(value);
  if(value === 0) { if(line) records.push(line); line = ''; return true; }
  if(room-- <= 0) return false;
  line += String.fromCharCode(value); return true;
}
function PrintF(stream, format) {
  if(stream !== LOG_INFO) throw new Error('wrong log stream');
  var values = arguments, index = 2;
  var text = format.replace(/%d/g, function() { return String(values[index++]); });
  streamCalls.push(text);
  if(room < text.length - 1) return false;
  room -= text.length - 1;
  for(var i=0; i<text.length; i++) {
    if(text.charCodeAt(i) === 0) { if(line) records.push(line); line=''; }
    else line += text[i];
  }
  return true;
}
function TwiBusControl(bus, ctrl) {
  if(bus !== expectedBus) throw new Error('wrong bus');
  if(active !== null) throw new Error('overlapping probe');
  if(ctrl < 8 || ctrl > 119) throw new Error('not an ordinary address-only probe');
  active = { address: ctrl, busy: delay };
  calls.push([bus, ctrl]);
}
function TwiBusStatus(bus) {
  if(bus !== expectedBus) throw new Error('wrong bus');
  polls++;
  if(active === null) return 0;
  if(active.busy-- > 0) return active.address | 0x0800;
  var address = active.address;
  var flags = answers[address] === undefined ? 0x4000 : answers[address];
  active = null;
  return address | flags;
}
";

    [SetUp]
    public void ImportScanner() {
      LP_Tree.ImportTypes();
      Tick();
      _template = root.Get("/$YS/TYPES/" + Type, false).GetState();
    }

    private JSC.Context Runtime() {
      var context = new JSC.Context();
      // Int32 is a PLC field declaration, represented by its numeric value in the JS mock.
      string source = _template.Field("LPLC.src").AsString(null).Replace("new Int32(-1)", "-1");
      context.Eval(Firmware + source + "\nvar scanner = new SCAN_BUS(1); var second = new SCAN_BUS(0);");
      return context;
    }

    [Test]
    public void CatalogDefinesConstructorScanAndAddr() {
      Assert.That(_template.Field("LPLC.args").Select(kv => kv.Value.AsString(null)), Is.EqualTo(new[] { "TwiNr" }));
      Assert.That(_template.Field("LPLC.in").Select(kv => kv.Value.AsString(null)), Is.EqualTo(new[] { "Scan" }));
      Assert.That(_template.Field("manifest.type").AsString(null), Is.EqualTo(Type));
      Assert.That(_template.Field("manifest.LPLC.args.TwiNr").AsInt(-1), Is.Zero);
      Assert.That(_template.Field("manifest.LPLC.in.Scan").AsInt(-1), Is.Zero);
      Assert.That(_template.Field("manifest.LPLC.in.Start").Defined, Is.False);
      Assert.That(_template.Field("manifest.LPLC.out").Select(kv => kv.Key), Is.EqualTo(new[] { "Addr" }));
      Assert.That(_template.Field("mi.LPLC.mi.in.mi").Select(kv => kv.Key), Is.EqualTo(new[] { "Scan" }));
      Assert.That(_template.Field("mi.LPLC.mi.out.mi").Select(kv => kv.Key), Is.EqualTo(new[] { "Addr" }));
      Assert.That(_template.Field("LPLC.src").AsString(null), Does.Not.Contain("LOG_I_ID"));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void CatalogBlockCompilesToBusApisAndFeedsAddr(int bus) {
      var program = root.Get("/dev/node/Logic", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var scanner = Topic.Declare(program, "Scan");
      Topic.Fill(scanner, JSC.JSValue.Null, JsLib.Clone(_template.Field("manifest")), null);
      scanner.SetField("LPLC.args.TwiNr", bus);
      scanner.SetField("LPLC.in.Scan", "Go");
      var go = program.parent.Get("Go", true);
      go.SetField("MQTT-SN.tag", "Mz");
      var address = program.parent.Get("Address", true);
      address.SetField("MQTT-SN.tag", "Md");
      address.SetField("LPLC.in.x", "Scan.Addr");
      Tick();
      var diagram = LP_Tree.Read(program);
      var block = diagram.Blocks.Single();
      Assert.That(block.Kind, Is.EqualTo(LP_Diagram.BlockKind.Block));
      Assert.That(block.Arguments, Is.EqualTo(new[] { bus.ToString() }));
      diagram.AddTwiMaster("Master", bus.ToString(), "21", "22");
      var result = LP_DiagramCompiler.Compile(diagram, EP_Target.EspNode, DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems) + "\n" + result.Source);
      Assert.That(result.Source, Does.Contain("new _c1(" + bus + ")"));
      Assert.That(result.Source, Does.Not.Contain(".Addr = _b1.Tick"));
      Assert.That(result.Variables["Scan.Addr"], Does.StartWith("Md"));
      Assert.That(result.Variables.ContainsKey("Scan.Q"), Is.False);
      var apis = DevicePlcFloatTests.Disassemble(result.Hex).Where(i => i.Code == EP_InstCode.API).Select(i => i.Arg[0]);
      Assert.That(apis.Distinct(), Is.EquivalentTo(new byte[] { 26, 31, 40, 41 }));
      Assert.That(result.Source.IndexOf("var _b2 ="), Is.LessThan(result.Source.IndexOf("var _b1 =")), "master initializes before scanner");
      Assert.That(LP_DiagramCompiler.Compile(diagram, EP_Target.X13, null).Ok, Is.False);
    }

    [Test]
    public void InitializationAndLowScanDoNotTouchTheBus() {
      var context = Runtime();
      Assert.That(context.Eval("JSON.stringify([scanner.Addr, calls.length, polls]);").AsString(null), Is.EqualTo("[-1,0,0]"));
      Assert.That(context.Eval("scanner.Tick(0); scanner.Tick(0); JSON.stringify([scanner.Addr, calls.length, polls]);").AsString(null), Is.EqualTo("[-1,0,0]"));
      Assert.That(context.Eval("streamCalls.length").AsInt(-1), Is.Zero);
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(10)]
    public void FullLogBufferNeverRepeatsHitsOrStallsScanning(int capacity) {
      var context = Runtime();
      context.Eval("room = " + capacity + "; answers[8] = 0x1000; scanner.Tick(1); scanner.Tick(0); scanner.Tick(0);");
      Assert.That(context.Eval("scanner.Addr").AsInt(-1), Is.EqualTo(8));
      Assert.That(context.Eval("records.length").AsInt(-1), Is.Zero);
      Assert.That(context.Eval("streamCalls.length").AsInt(-1), Is.EqualTo(1));
      int streamCalls = context.Eval("streamCalls.length").AsInt(-1);
      Assert.That(context.Eval("scanner.Tick(0); JSON.stringify([scanner.Addr,calls.length,scanner.Address]);").AsString(null), Is.EqualTo("[8,2,9]"));
      Assert.That(context.Eval("streamCalls.length").AsInt(-1), Is.EqualTo(streamCalls));
    }

    [Test]
    public void CompleteScanRetainsLastAddressAndLogsOnlyAcknowledgedAddressesOnce() {
      var context = Runtime();
      string result = context.Eval(@"
answers[8] = 0x1000; answers[64] = 0x1000; answers[119] = 0x1000;
answers[9] = 0x2000; answers[10] = 0x8000; answers[11] = 0x5000;
var found = [], maximumCalls = 0, maximumPolls = 0;
for(var i = 0; i < 500; i++) {
  var beforeCalls = calls.length, beforePolls = polls, beforeAddr = scanner.Addr;
  scanner.Tick(1);
  if(scanner.Addr !== beforeAddr) found.push(scanner.Addr);
  maximumCalls = Math.max(maximumCalls, calls.length - beforeCalls);
  maximumPolls = Math.max(maximumPolls, polls - beforePolls);
}
JSON.stringify([found, calls.length, calls[0], calls[calls.length - 1], scanner.Addr, active, maximumCalls, maximumPolls]);
").AsString(null);
      Assert.That(result, Is.EqualTo("[[8,64,119],112,[1,8],[1,119],119,null,1,1]"));
      Assert.That(context.Eval("JSON.stringify(records)").AsString(null), Is.EqualTo("[\"TWI1 addr=8\",\"TWI1 addr=64\",\"TWI1 addr=119\"]"));
      Assert.That(context.Eval("calls.every(function(c, i) { return c[0] === 1 && c[1] === i + 8; });").AsBool(false), Is.True);
    }

    [Test]
    public void BusyProbeIsNotReissuedAndAddrRemainsOnTheNextTick() {
      var context = Runtime();
      Assert.That(context.Eval(@"
delay = 3; answers[8] = 0x1000;
scanner.Tick(1); scanner.Tick(0); scanner.Tick(0); scanner.Tick(0);
JSON.stringify([calls.length, scanner.Addr]);
").AsString(null), Is.EqualTo("[1,-1]"));
      Assert.That(context.Eval("scanner.Tick(0); JSON.stringify([calls.length, scanner.Addr]);").AsString(null), Is.EqualTo("[1,8]"));
      Assert.That(context.Eval("scanner.Tick(0); JSON.stringify([calls.length, scanner.Addr, calls[1]]);").AsString(null), Is.EqualTo("[2,8,[1,9]]"));
    }

    [Test]
    public void ProbeWaitsForAnExistingTransactionAndDoesNotReportItsResult() {
      var context = Runtime();
      Assert.That(context.Eval(@"
active = { address: 64, busy: 1 }; answers[64] = 0x1000;
scanner.Tick(1); scanner.Tick(0);
JSON.stringify([calls.length, scanner.Addr]);
").AsString(null), Is.EqualTo("[0,-1]"));
      Assert.That(context.Eval("scanner.Tick(0); JSON.stringify([calls, scanner.Addr]);").AsString(null), Is.EqualTo("[[[1,8]],-1]"));
    }

    [Test]
    public void WrongAddressStatusIsNeverReportedAsAHit() {
      var context = Runtime();
      Assert.That(context.Eval(@"
delay = 0; answers[9] = 0x1000;
scanner.Tick(1); active.address = 9; scanner.Tick(0);
JSON.stringify([scanner.Addr, calls.length]);
").AsString(null), Is.EqualTo("[-1,1]"));
    }

    [Test]
    public void HeldScanDoesNotRepeatAndBusyTriggersAreNotQueued() {
      var context = Runtime();
      Assert.That(context.Eval(@"
delay = 0;
scanner.Tick(1); scanner.Tick(0); scanner.Tick(1);
for(var i = 0; i < 300; i++) scanner.Tick(1);
JSON.stringify([calls.length, scanner.Addr]);
").AsString(null), Is.EqualTo("[112,-1]"));
      Assert.That(context.Eval("scanner.Tick(0); scanner.Tick(1); JSON.stringify([calls.length, calls[112]]);").AsString(null), Is.EqualTo("[113,[1,8]]"));
    }

    [Test]
    public void InstancesKeepTheirOwnTriggerAndResultState() {
      var context = Runtime();
      Assert.That(context.Eval(@"
delay = 0; answers[8] = 0x1000;
scanner.Tick(1); scanner.Tick(0);
JSON.stringify([scanner.Addr, second.Addr, second.Address, second.PreviousScan]);
").AsString(null), Is.EqualTo("[8,-1,0,false]"));
    }

    [Test]
    public void LastAddressSurvivesIdleAndEmptyRescanAndSameHitIsLoggedAgain() {
      var context = Runtime();
      Assert.That(context.Eval(@"
delay = 0; answers[118] = 0x1000;
for(var i = 0; i < 300; i++) scanner.Tick(1);
scanner.Tick(0); scanner.Tick(0);
JSON.stringify([scanner.Addr, calls.length, scanner.Address, records.length]);
").AsString(null), Is.EqualTo("[118,112,0,1]"));
      Assert.That(context.Eval(@"
answers = {};
scanner.Tick(1);
var atStart = scanner.Addr;
var changed = false;
for(var i = 0; i < 300; i++) {
  scanner.Tick(0);
  changed = changed || scanner.Addr !== 118;
}
JSON.stringify([atStart, changed, scanner.Addr, calls.length, scanner.Address, records.length]);
").AsString(null), Is.EqualTo("[118,false,118,224,0,1]"));
      Assert.That(context.Eval(@"
answers[118] = 0x1000;
for(var i = 0; i < 300; i++) scanner.Tick(1);
JSON.stringify([scanner.Addr, calls.length, records]);
").AsString(null), Is.EqualTo("[118,336,[\"TWI1 addr=118\",\"TWI1 addr=118\"]]"));
    }
  }
}
