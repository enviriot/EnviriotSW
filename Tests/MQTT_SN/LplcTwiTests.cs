using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class LplcTwiTests : RepoTestBase {
    [SetUp]
    public void ImportCatalog() { LP_Tree.ImportTypes(); Tick(); }
    internal const string DeviceDescription = @"{
      'names': { 'SDA': 21, 'SDA_ALIAS': 21, 'SCL': 22 },
      'groups': {
        'Data': { 'modes': { 'TWI0_SDA': { 'v': 16, 'defaultPin': 'SDA' }, 'TWI1_SDA': { 'v': 38 } }, 'pins': ['SDA', 0, 4] },
        'Clock': { 'modes': { 'TWI0_SCL': { 'v': 19, 'defaultPin': 22 }, 'TWI1_SCL': { 'v': 42 } }, 'pins': ['SCL', 'SDA', 0, 4] },
        'MoreData': { 'modes': { 'TWI0_SDA': { 'v': 16 } }, 'pins': [25] },
        'Incomplete': { 'modes': { 'TWI2_SDA': { 'v': 52 } }, 'pins': [34] }
      }
    }";

    private static EP_Device Device() {
      return DevicePlcDeviceTests.Device(DeviceDescription);
    }
    private static LP_DiagramCompiler Compile(LP_Diagram diagram) {
      return LP_DiagramCompiler.Compile(diagram, EP_Target.EspNode, Device());
    }
    private static void AssertCompiled(LP_DiagramCompiler result) {
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems) + "\n" + result.Source);
    }

    [Test]
    public void BusDiscoveryPairsModesAndPreservesFirmwareValues() {
      var buses = Device().TwiBuses();
      Assert.That(buses.Select(b => b.Number), Is.EqualTo(new[] { 0, 1 }));
      Assert.That(buses.Select(b => b.Sda.Value), Is.EqualTo(new[] { 16, 38 }));
      Assert.That(buses.Select(b => b.Scl.Value), Is.EqualTo(new[] { 19, 42 }));
      Assert.That(buses[0].Sda.Pins, Does.Contain(25));
    }

    [Test]
    public void OneBasedDescriptorMapsToBusZero() {
      var device = DevicePlcDeviceTests.Device("{'groups':{'TWI':{'modes':{'TWI1_SDA':{'v':73},'TWI1_SCL':{'v':91}},'pins':[0,4]}}}");
      Assert.That(device.TwiBuses().Single().Number, Is.Zero);
      var result = LP_DiagramCompiler.Compile(new LP_Diagram().AddTwiMaster("TWI0", "0", "0", "4"), EP_Target.EspNode, device);
      AssertCompiled(result);
      Assert.That(result.Source, Does.Contain("new _c1(0, 73, 4, 91)"));
      Assert.That(result.Source, Does.Contain("ConfigPin(SdaPin, SdaMode, 0)"));
      Assert.That(result.Source, Does.Contain("ConfigPin(SclPin, SclMode, 0)"));
    }

    [Test]
    public void ConstructorConfiguresOnceAndTickHasNoPortsOrWork() {
      var result = Compile(new LP_Diagram().AddTwiMaster("TWI0", "0", "21", "22", "master"));
      AssertCompiled(result);
      Assert.That(result.Source, Does.Contain("constructor(SdaPin, SdaMode, SclPin, SclMode)"));
      Assert.That(result.Source, Does.Contain("new _c1(21, 16, 22, 19)"));
      Assert.That(result.Source, Does.Not.Contain("TwiNr"));
      Assert.That(result.Source, Does.Contain("Tick() {}"));
      Assert.That(result.Variables, Is.Empty);
      Assert.That(result.Elements.Single().Tag, Is.EqualTo("master"));
      var calls = new JSC.Context().Eval("var calls = []; function ConfigPin(pin, mode, option) { calls.push([pin, mode, option]); }\n"
        + result.Source + "\n_b1.Tick(); _b1.Tick(); JSON.stringify(calls);").AsString(null);
      Assert.That(calls, Is.EqualTo("[[21,16,0],[22,19,0]]"));
      var instructions = DevicePlcFloatTests.Disassemble(result.Hex);
      Assert.That(instructions.Where(i => i.Code == EP_InstCode.API).Select(i => i.Arg[0]), Is.EqualTo(new byte[] { 31, 31 }));
      Assert.That(result.Elements.Single().Code.Any(c => c.Line == 3), Is.True);
      Assert.That(result.Elements.Single().Code.Any(c => c.Line == 4), Is.True);
    }

    [Test]
    public void AliasesCompileLikeNumericPins() {
      var named = Compile(new LP_Diagram().AddTwiMaster("Master", "0", "SDA_ALIAS", "SCL"));
      var numeric = Compile(new LP_Diagram().AddTwiMaster("Master", "0", "21", "22"));
      AssertCompiled(named);
      AssertCompiled(numeric);
      Assert.That(LP_Tree.Fingerprint(named.Hex), Is.EqualTo(LP_Tree.Fingerprint(numeric.Hex)));
    }

    [TestCase("-1", "21", "22", "TwiNr")]
    [TestCase("2", "21", "22", "TwiNr")]
    [TestCase("3", "21", "22", "TwiNr")]
    [TestCase("0", null, "22", "SdaPin")]
    [TestCase("0", "21", null, "SclPin")]
    [TestCase("0", "34", "22", "SdaPin")]
    [TestCase("0", "21", "25", "SclPin")]
    [TestCase("0", "SDA", "SDA_ALIAS", "distinct")]
    [TestCase("0", "0", "0", "distinct")]
    [TestCase("0", "TWI0_SDA", "22", "SdaPin")]
    public void InvalidConfigurationIsABlockDiagnostic(string number, string sda, string scl, string error) {
      var result = Compile(new LP_Diagram().AddTwiMaster("Master", number, sda, scl, "master"));
      Assert.That(result.Ok, Is.False);
      Assert.That(result.Problems.Any(p => p.Element != null && Equals(p.Element.Tag, "master") && p.Message.Contains(error)), Is.True);
      Assert.That(result.Hex, Is.Null);
    }

    [Test]
    public void DuplicateBusIsRejectedEvenWithDifferentBlockNames() {
      var result = Compile(new LP_Diagram().AddTwiMaster("A", "0", "21", "22").AddTwiMaster("B", "0", "0", "4"));
      Assert.That(result.Ok, Is.False);
      Assert.That(result.Problems.Any(p => p.Element.Name == "B" && p.Message.Contains("already has a master")), Is.True);
      var separate = Compile(new LP_Diagram().AddTwiMaster("A", "0", "21", "22").AddTwiMaster("B", "1", "0", "4"));
      AssertCompiled(separate);
      Assert.That(separate.Source, Does.Contain("new _c1(21, 16, 22, 19)"));
      Assert.That(separate.Source, Does.Contain("new _c2(0, 38, 4, 42)"));
    }

    [Test]
    public void MasterRequiresDeviceDescriptionAndEspNode() {
      var diagram = new LP_Diagram().AddTwiMaster("Master", "0", "21", "22");
      var absent = LP_DiagramCompiler.Compile(diagram, EP_Target.EspNode, null);
      Assert.That(absent.Ok, Is.False);
      Assert.That(absent.Problems.Any(p => p.Message.Contains("TwiNr")), Is.True);
      Assert.That(LP_DiagramCompiler.Compile(diagram, EP_Target.X13, Device()).Ok, Is.False);
    }

    [Test]
    public void TreeReadsConstructorValuesFromManifestNotStateOrTopicName() {
      LP_Tree.ImportTypes();
      var program = root.Get("/dev/node/custom-program", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var block = program.Get("RenamedMaster", true);
      block.SetField("type", LP_Tree.TWI);
      block.SetField("LPLC.args", JsLib.ParseJson("{\"TwiNr\":0,\"SdaPin\":21,\"SclPin\":22}"));
      block.SetState(Str("not source"));
      Tick();
      var diagram = LP_Tree.Read(program);
      Assert.That(diagram.Links, Is.Empty);
      Assert.That(diagram.Blocks.Single().Arguments, Is.EqualTo(new[] { "21", null, "22", null }));
      Assert.That(diagram.Blocks.Single().Configuration.Values.Field("TwiNr").AsInt(-1), Is.Zero);
      var compiled = Compile(diagram);
      AssertCompiled(compiled);
      Assert.That(compiled.Source, Does.Contain("new _c1(21, 16, 22, 19)"));
      Assert.That(block.GetField("LPLC.args").Select(kv => kv.Key), Is.EquivalentTo(new[] { "TwiNr", "SdaPin", "SclPin" }));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void BusDiscoveryPreservesGapsInEitherDescriptorNumbering(int first) {
      string description = "{'groups':{'TWI':{'modes':{'TWI" + first + "_SDA':{'v':16},'TWI" + first
        + "_SCL':{'v':19},'TWI" + (first + 2) + "_SDA':{'v':38},'TWI" + (first + 2) + "_SCL':{'v':42}},'pins':[0,4]}}}";
      var device = DevicePlcDeviceTests.Device(description);
      Assert.That(device.TwiBuses().Select(b => b.Number), Is.EqualTo(new[] { 0, 2 }));
      var result = LP_DiagramCompiler.Compile(new LP_Diagram().AddTwiMaster("Master", "2", "0", "4"), EP_Target.EspNode, device);
      AssertCompiled(result);
      Assert.That(result.Source, Does.Contain("new _c1(0, 38, 4, 42)"));
    }

    [Test]
    public void DerivedModesRefreshWithoutMutatingSavedConfiguration() {
      var diagram = new LP_Diagram().AddTwiMaster("Master", "0", "21", "22");
      var first = Compile(diagram);
      AssertCompiled(first);
      var device = DevicePlcDeviceTests.Device(DeviceDescription.Replace("'v': 16", "'v': 73").Replace("'v': 19", "'v': 91"));
      var second = LP_DiagramCompiler.Compile(diagram, EP_Target.EspNode, device);
      AssertCompiled(second);
      Assert.That(first.Source, Does.Contain("new _c1(21, 16, 22, 19)"));
      Assert.That(second.Source, Does.Contain("new _c1(21, 73, 22, 91)"));
      Assert.That(diagram.Blocks.Single().Arguments, Is.EqualTo(new[] { "0", "21", "22" }));
      Assert.That(diagram.Blocks.Single().Parameters, Is.EqualTo(new[] { "SdaPin", "SdaMode", "SclPin", "SclMode" }));
      Assert.That(LP_Tree.Fingerprint(Compile(diagram).Hex), Is.EqualTo(LP_Tree.Fingerprint(first.Hex)));
    }

    [Test]
    public void CatalogConstructorArgumentsKeepTheirOrderAndStaySeparateFromTick() {
      const string source = "class B { constructor(First, Second) { this.Q = First * 10 + Second; } Tick(Step) { this.Q += Step; } }";
      var type = root.Get("/$YS/TYPES/LPLC/Configured", true);
      var state = JsLib.ParseJson("{\"LPLC\":{\"args\":[\"First\",\"Second\"],\"in\":[\"Step\"]}}");
      state["LPLC"]["src"] = source;
      type.SetState(state);
      var program = root.Get("/dev/node/program", true);
      var block = program.Get("B", true);
      block.SetField("type", "LPLC/Configured");
      block.SetField("LPLC", JsLib.ParseJson("{\"args\":{\"Second\":2,\"First\":4},\"in\":{\"Step\":3}}"));
      Tick();
      var diagram = LP_Tree.Read(program);
      Assert.That(diagram.Links, Has.Count.EqualTo(1));
      var result = Compile(diagram);
      AssertCompiled(result);
      Assert.That(result.Source, Does.Contain("new _c1(4, 2)"));
      Assert.That(result.Source, Does.Contain("_b1.Tick(3)"));
      Assert.That((double)new JSC.Context().Eval(result.Source + "\n_b1.Q;"), Is.EqualTo(45));
    }

    [TestCase(null)]
    [TestCase("1 + 2")]
    [TestCase("unknown")]
    [TestCase("1e309")]
    [TestCase("0); ConfigPin(0,0,0);")]
    public void InvalidConstructorValueIsRejectedBeforeGeneration(string value) {
      var diagram = new LP_Diagram().AddBlock("B", "class B { constructor(X) {} Tick() {} }", new string[0],
        parameters: new[] { "X" }, arguments: new[] { value });
      var result = Compile(diagram);
      Assert.That(result.Ok, Is.False);
      Assert.That(result.Source, Is.Null);
      Assert.That(result.Problems.Any(p => p.Message.Contains("constructor argument")), Is.True);
    }

    [Test]
    public void CatalogReimportOwnsMasterSourceAndPreservesOldInstances() {
      var program = root.Get("/dev/node/custom", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var block = program.Get("Renamed", true);
      block.SetField("type", LP_Tree.TWI);
      block.SetField("LPLC.args", JsLib.ParseJson("{\"TwiNr\":0,\"SdaPin\":21,\"SclPin\":22,\"SdaMode\":999}"));
      block.SetState("invalid instance source must be ignored");
      var type = root.Get(LP_Tree.TYPES + "/TwiMaster", false);
      type.SetState(JsLib.ParseJson("{\"LPLC\":{\"args\":[\"TwiNr\",\"SdaPin\",\"SclPin\"]}}"));
      type.SetField("version", "¤VR0.5.2610.27003");
      Tick();
      LP_Tree.ImportTypes(); Tick();
      var result = Compile(LP_Tree.Read(program));
      AssertCompiled(result);
      Assert.That(result.Source, Does.Contain("new _c1(21, 16, 22, 19)"));
      Assert.That(block.GetField("LPLC.args.SdaMode").AsInt(-1), Is.EqualTo(999));
      var state = JsLib.Clone(type.GetState());
      state = JsLib.SetField(state, "LPLC.src", state.Field("LPLC.src").AsString(null).Replace("Tick() {}", "Tick() { this.Q = 7; }"));
      type.SetState(state); Tick();
      result = Compile(LP_Tree.Read(program));
      AssertCompiled(result);
      Assert.That(result.Source, Does.Contain("this.Q = 7"));
    }
  }
}
