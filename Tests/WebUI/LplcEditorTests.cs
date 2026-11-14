///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.LogramPLC;
using X13.Repository;
using X13.WebUI;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  /// <summary>Редактор LPLC (view "lplc"): что видит браузер, что предлагают меню и что пишут команды.</summary>
  /// <remarks>Типы блоков - настоящий LPLC.xst из сборки MQTT_SN, пункты переменных - те, что пишет
  /// LP_Schema.EnsureVarTemplates: WebUI сам MQTT_SN не знает и читает только манифесты, поэтому тест
  /// проверяет именно этот контракт. Наблюдатель MQTT_SN не запущен - Func-выходы здесь не синхронизируются.
  /// <para>Схема: сумматор A1 (M1 + 5) питает Func F1, выход F1 - переменная M1; M2 ни к чему не подключена;
  /// In0 - ребёнок устройства, но не переменная LPLC.</para></remarks>
  [TestFixture]
  public class LplcEditorTests : RepoTestBase {
    private const string Dev = "/dev/node1";
    private const string Pa1 = Dev + "/pa1";
    private readonly List<JSC.JSObject> _sent = new List<JSC.JSObject>();
    private Topic _dev, _pa1, _a1, _f1, _m1, _m2;
    private LplcGraphController _graph;
    private LplcViewProvider _provider;

    private static JSC.JSValue Json(string text) {
      return JsLib.ParseJson(text.Replace('\'', '"'));
    }
    private static void ImportTypes() {
      using(var rs = typeof(LP_Tree).Assembly.GetManifestResourceStream(LP_Tree.TYPES_RESOURCE)) {
        using(var reader = new System.IO.StreamReader(rs)) {
          Xst.Import(reader, null);
        }
      }
    }
    private Topic Block(string name, string type, string lplc) {
      Topic t = _pa1.Get(name, true);
      t.SetField("type", type);
      t.SetField("LPLC", Json(lplc));
      return t;
    }
    private Topic Var(string name, string tag, string lplc = null) {
      Topic t = _dev.Get(name, true);
      t.SetField("MQTT-SN.tag", tag);
      if(lplc != null) t.SetField("LPLC", Json(lplc));
      return t;
    }

    [SetUp]
    public void LplcEditorTestsSetUp() {
      _sent.Clear();
      ImportTypes();
      _dev = root.Get(Dev, true);
      _pa1 = _dev.Get("pa1", true);
      _pa1.SetField("type", LplcModel.ProgramType);
      LP_Schema.EnsureVarTemplates(_dev, null);
      _a1 = Block("A1", "LPLC/Math/ADD", "{'pos':{'top':1,'left':2},'in':{'A':'M1','B':5},'out':{'Q':true}}");
      _f1 = Block("F1", LplcModel.FuncType, "{'pos':{'top':1,'left':12},'in':{'A':'A1.Q','B':''},'out':{'Q':true}}");
      _f1.SetState(Str("return A;"));
      _m1 = Var("M1", "Md0", "{'pos':{'top':8,'left':2},'in':{'x':'F1.Q'}}");
      _m2 = Var("M2", "Mz1");
      Var("In0", "In0");
      Tick();
      _provider = new LplcViewProvider(_sent.Add);
    }

    [TearDown]
    public void LplcEditorTestsTearDown() {
      PendingRpc.Reset();
      if(_graph != null) {
        _graph.Dispose();
        _graph = null;
      }
      if(_provider != null) {
        _provider.Dispose();
        _provider = null;
      }
    }

    // === Модель ===

    [Test]
    public void Resolve_TellsEveryPartOfTheProgramApart() {
      using(Assert.EnterMultipleScope()) {
        Assert.That(LplcModel.Resolve(Vid(Pa1)).Kind, Is.EqualTo(LplcModel.TargetKind.Program));
        Assert.That(LplcModel.Resolve(Vid(Pa1 + "/A1")).Kind, Is.EqualTo(LplcModel.TargetKind.Block));
        LplcModel.Target pin = LplcModel.Resolve(Vid(Pa1 + "/A1/in.B"));
        Assert.That(pin.Kind, Is.EqualTo(LplcModel.TargetKind.InputPin));
        Assert.That(pin.Topic, Is.SameAs(_a1));
        Assert.That(pin.Pin, Is.EqualTo("B"));
        Assert.That(LplcModel.Resolve(Vid(Pa1 + "/F1/out.Q")).Kind, Is.EqualTo(LplcModel.TargetKind.OutputPin));
        LplcModel.Target variable = LplcModel.Resolve(Vid(Dev + "/M1"));
        Assert.That(variable.Kind, Is.EqualTo(LplcModel.TargetKind.Variable));
        Assert.That(variable.Program, Is.SameAs(_pa1));
        Assert.That(LplcModel.Resolve(Vid(Dev + "/In0")), Is.Null, "не переменная LPLC");
        Assert.That(LplcModel.Resolve("logram#" + Pa1), Is.Null, "чужой view");
      }
    }

    /// <summary>pa1 узнаётся по типу: и автооткрытие, и кнопка Inspector'а ведут в lplc, pa0 - нет.</summary>
    [Test]
    public void AltView_IsLplcOnlyForTheProgramType() {
      Topic pa0 = _dev.Get("pa0", true);
      pa0.SetField("type", "Ext/DevicePLC");
      Tick();
      using(Assert.EnterMultipleScope()) {
        Assert.That(RowProjector.ResolveAltView(_pa1), Is.EqualTo(LplcModel.ViewName));
        Assert.That(RowProjector.ResolveAltView(pa0), Is.Null);
      }
    }

    // === Снимок ===

    [Test]
    public void Snapshot_DrawsBlocksVariablesAndPins() {
      Open();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Text(Added(Vid(Pa1)), "editor"), Is.EqualTo("LogramCanvas"));
        JSC.JSObject a1 = Added(Vid(Pa1 + "/A1"));
        Assert.That(Text(a1, "editor"), Is.EqualTo("LogramBlock"));
        Assert.That((int)a1["x"], Is.EqualTo(2));
        Assert.That((int)a1["y"], Is.EqualTo(1));
        Assert.That(Text(Added(Vid(Dev + "/M1")), "editor"), Is.EqualTo("LogramVariable"));
        Assert.That(Added(Vid(Dev + "/M2")), Is.Not.Null, "без LPLC.pos - тоже на схеме");
        Assert.That(Added(Vid(Dev + "/In0")), Is.Null);
        Assert.That(Text(Added(Vid(Pa1 + "/A1/in.A")), "pinDirection"), Is.EqualTo("in"));
        Assert.That(Text(Added(Vid(Pa1 + "/A1/out.Q")), "pinDirection"), Is.EqualTo("out"));
        Assert.That(Added(Vid(Pa1 + "/A1/in.C")), Is.Null, "в каталоге, но не показан");
      }
    }

    /// <summary>Константа на входе - не провод, а подпись pin-а.</summary>
    [Test]
    public void Snapshot_ConstantInputCarriesItsLabel() {
      Open();
      JSC.JSObject pin = Added(Vid(Pa1 + "/A1/in.B"));
      Assert.That(Text(pin, "displayValue"), Is.EqualTo("5"));
      Assert.That(Flag(pin, "constantEditable"), Is.True);
      Assert.That(Flag(pin, "trace"), Is.False);
    }

    [Test]
    public void Snapshot_OnlyUnlinkedBlockInputsOfferConstantEditing() {
      _f1.SetField("LPLC.in.C", "Nope.Q");
      _f1.SetField("LPLC.in.D", false);
      Tick();
      Open();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.B")), "constantEditable"), Is.True);
        Assert.That(Text(Added(Vid(Pa1 + "/F1/in.B")), "displayValue"), Is.EqualTo(string.Empty));
        Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo(string.Empty));
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.D")), "constantEditable"), Is.True);
        Assert.That(Text(Added(Vid(Pa1 + "/F1/in.D")), "displayValue"), Is.EqualTo("false"));
        Assert.That(Flag(Added(Vid(Pa1 + "/A1/in.A")), "constantEditable"), Is.False);
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.A")), "constantEditable"), Is.False);
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.C")), "constantEditable"), Is.False);
        Assert.That(Text(Added(Vid(Pa1 + "/F1/in.C")), "displayValue"), Is.Null);
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/out.Q")), "constantEditable"), Is.False);
        Assert.That(Added(Vid(Pa1 + "/A1/in.C")), Is.Null);
        Assert.That(Added(Vid(Dev + "/M2"))["constantEditable"].Defined, Is.False);
      }
    }

    [TestCase("0", 0.0)]
    [TestCase("-17", -17.0)]
    [TestCase("3.5", 3.5)]
    [TestCase("-2.5e-3", -0.0025)]
    public void Commit_InputConstantStoresANumber(string json, double expected) {
      string vid = Vid(Pa1 + "/F1/in.B");
      Open();
      _sent.Clear();
      AssertOk(_provider.Commit(vid, Json(json)));
      Tick();
      using(Assert.EnterMultipleScope()) {
        Assert.That(_f1.GetField("LPLC.in.B").IsNumber, Is.True);
        Assert.That((double)_f1.GetField("LPLC.in.B"), Is.EqualTo(expected));
        Assert.That(_f1.GetField("LPLC.in.A").AsString(null), Is.EqualTo("A1.Q"));
        Assert.That(_f1.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(12));
        Assert.That(Flag(Added(vid), "constantEditable"), Is.True);
        Assert.That(Text(Added(vid), "displayValue"), Is.Not.Empty);
        Assert.That(Flag(Added(vid), "trace"), Is.False);
        Assert.That(Text(Updated(vid), "sourceVid"), Is.EqualTo(string.Empty));
      }
    }

    [TestCase("true")]
    [TestCase("'7'")]
    [TestCase("null")]
    [TestCase("{}")]
    public void Commit_InputConstantRejectsNonNumbers(string json) {
      Assert.That(_provider.Commit(Vid(Pa1 + "/A1/in.B"), Json(json)).ErrorCode, Is.EqualTo("commit_value_invalid"));
      Tick();
      Assert.That(_a1.GetField("LPLC.in.B").AsInt(-1), Is.EqualTo(5));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    public void Commit_InputConstantRejectsNonFiniteNumbers(double value) {
      Assert.That(_provider.Commit(Vid(Pa1 + "/A1/in.B"), new NiL.JS.BaseLibrary.Number(value)).ErrorCode, Is.EqualTo("commit_value_invalid"));
      Tick();
      Assert.That(_a1.GetField("LPLC.in.B").AsInt(-1), Is.EqualTo(5));
    }

    [TestCase("A1.Q")]
    [TestCase("M1")]
    [TestCase("Nope.Q")]
    public void Commit_InputConstantDoesNotOverwriteANewLink(string link) {
      Open();
      Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.B")), "constantEditable"), Is.True);
      _f1.SetField("LPLC.in.B", link);
      Tick();
      Assert.That(_provider.Commit(Vid(Pa1 + "/F1/in.B"), Json("7")).ErrorCode, Is.EqualTo("commit_input_linked"));
      Tick();
      Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo(link));
    }

    [TestCase(Pa1 + "/F1/in.C")]
    [TestCase(Pa1 + "/F1/out.Q")]
    [TestCase(Pa1 + "/F1")]
    [TestCase(Dev + "/M2")]
    public void Commit_ConstantRejectsNonInputAndHiddenTargets(string path) {
      Assert.That(_provider.Commit(Vid(path), Json("7")).Ok, Is.False);
      Tick();
      using(Assert.EnterMultipleScope()) {
        Assert.That(_f1.GetField("LPLC.in.C").Defined, Is.False);
        Assert.That(_m2.GetField(LplcModel.VarInputField).Defined, Is.False);
        Assert.That(_f1.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(12));
      }
    }

    [Test]
    public void Commit_ConstantDoesNotApplyToVariableStylePin() {
      DeviceType(DevicePlcDeviceTests.Stm);
      AssertOk(Rpc(Pa1, "add-pin:OUT:PA5"));
      Open();
      string path = Pa1 + "/OUT_PA5";
      using(Assert.EnterMultipleScope()) {
        Assert.That(Added(Vid(path))["constantEditable"].Defined, Is.False);
        Assert.That(_provider.Commit(Vid(path), Json("7")).Ok, Is.False);
        Assert.That(_provider.Commit(Vid(path + "/in.x"), Json("7")).ErrorCode, Is.EqualTo("commit_target_invalid"));
      }
      Tick();
      Assert.That(_pa1.Get("OUT_PA5", false).GetField("LPLC.in.x").AsInt(-1), Is.Zero);
    }

    [Test]
    public void Change_LinkAndUnlinkRefreshConstantEditing() {
      string path = Pa1 + "/F1/in.B";
      string vid = Vid(path);
      Open();
      _sent.Clear();
      AssertOk(Rpc(path, "bind", "{'source':'" + Vid(Pa1 + "/A1/out.Q") + "'}"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Added(vid), Is.Not.Null);
        Assert.That(Flag(Added(vid), "constantEditable"), Is.False);
        Assert.That(Text(Added(vid), "displayValue"), Is.Null);
        Assert.That(Text(Updated(vid), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/A1/out.Q")));
      }
      _sent.Clear();
      AssertOk(Rpc(path, "unbind"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(vid), "constantEditable"), Is.True);
        Assert.That(Text(Added(vid), "displayValue"), Is.EqualTo(string.Empty));
        Assert.That(Text(Updated(vid), "sourceVid"), Is.EqualTo(string.Empty));
        Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo(string.Empty));
      }
    }

    [Test]
    public void Commit_StaleShownInputKeepsItsError() {
      _f1.SetField("LPLC.in.Legacy", "");
      Tick();
      Open();
      string vid = Vid(Pa1 + "/F1/in.Legacy");
      _sent.Clear();
      AssertOk(_provider.Commit(vid, Json("3.5")));
      Tick();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(vid), "constantEditable"), Is.True);
        Assert.That(Flag(Added(vid), "pinError"), Is.True);
        Assert.That(Text(Added(vid), "displayValue"), Is.EqualTo("3.5"));
      }
    }

    [Test]
    public void Snapshot_LinksBecomeWiresFromTheirSources() {
      Open();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Text(Updated(Vid(Pa1 + "/F1/in.A")), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/A1/out.Q")));
        Assert.That(Text(Updated(Vid(Pa1 + "/A1/in.A")), "sourceVid"), Is.EqualTo(Vid(Dev + "/M1")));
        Assert.That(Text(Updated(Vid(Dev + "/M1")), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/F1/out.Q")));
      }
    }

    /// <summary>Ссылка на блок, которого нет, - серый pin, а не провод в никуда.</summary>
    [Test]
    public void Snapshot_LinkToAMissingBlockIsNotLocal() {
      _f1.SetField("LPLC.in.B", "Nope.Q");
      Tick();
      Open();
      JSC.JSObject wire = Updated(Vid(Pa1 + "/F1/in.B"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Text(wire, "sourceVid"), Is.EqualTo(Vid(Pa1 + "/Nope/out.Q")));
        Assert.That(Flag(wire, "sourceLocal"), Is.False);
      }
    }

    /// <summary>Выход, на который ссылаются, виден, даже если LPLC.out его не называет.</summary>
    [Test]
    public void Snapshot_ReferencedOutputStaysShown() {
      _a1.SetField("LPLC.out", Json("{}"));
      Tick();
      Open();
      Assert.That(Added(Vid(Pa1 + "/A1/out.Q")), Is.Not.Null);
    }

    [Test]
    public void Snapshot_PinErrorsUseDirectionSpecificCatalogs() {
      _f1.SetField("LPLC.in.Q", "");
      _f1.SetField("LPLC.out.A", true);
      Tick();
      Open();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.Q")), "pinError"), Is.True);
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/out.A")), "pinError"), Is.True);
        Assert.That(Added(Vid(Pa1 + "/F1/in.A"))["pinError"].Defined, Is.True);
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/in.A")), "pinError"), Is.False);
        Assert.That(Added(Vid(Pa1 + "/F1/out.Q"))["pinError"].Defined, Is.True);
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/out.Q")), "pinError"), Is.False);
      }
    }

    [Test]
    public void Snapshot_ReferencedStaleOutputKeepsItsWire() {
      _m1.SetField(LplcModel.VarInputField, "F1.M");
      Tick();
      Open();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/out.M")), "pinError"), Is.True);
        Assert.That(Text(Updated(Vid(Dev + "/M1")), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/F1/out.M")));
        Assert.That(Flag(Updated(Vid(Dev + "/M1")), "sourceLocal"), Is.True);
        Assert.That(_m1.GetField(LplcModel.VarInputField).AsString(null), Is.EqualTo("F1.M"));
        Assert.That(_f1.GetField("LPLC.out.M").Defined, Is.False);
      }
    }

    [Test]
    public void AddOutput_WithoutSourceAssignmentIsMarkedAsError() {
      Open();
      _sent.Clear();
      AssertOk(Rpc(Pa1 + "/F1", LplcPaletteBuilder.AddOutCmd, "{'name':'M'}"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(Vid(Pa1 + "/F1/out.M")), "pinError"), Is.True);
        Assert.That(Keys(_f1.GetField("LPLC.out")), Is.EqualTo(new[] { "Q", "M" }));
        Assert.That(LplcModel.OutputCatalog(_f1), Does.Not.Contain("M"));
      }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Change_InputCatalogResendsErrorStateAndWire(bool valid) {
      string vid = Vid(Pa1 + "/F1/in.Legacy");
      string catalogField = "mi.LPLC.mi.in.mi.Legacy";
      _f1.SetField("LPLC.in.Legacy", "A1.Q");
      if(!valid) _f1.SetField(catalogField, Json("{'default':0}"));
      Tick();
      Open();
      Assert.That(Added(vid), Is.Not.Null);
      Assert.That(Flag(Added(vid), "pinError"), Is.EqualTo(valid));
      _sent.Clear();

      _f1.SetField(catalogField, valid ? Json("{'default':0}") : JSC.JSValue.Null);
      Tick();
      JSC.JSObject pin = Added(vid);
      Assert.That(pin, Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(pin["pinError"].Defined, Is.True);
        Assert.That(Flag(pin, "pinError"), Is.EqualTo(!valid));
        Assert.That(Text(Updated(vid), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/A1/out.Q")));
        Assert.That(Flag(Updated(vid), "sourceLocal"), Is.True);
        Assert.That(_f1.GetField("LPLC.in.Legacy").AsString(null), Is.EqualTo("A1.Q"));
        Assert.That(Frame(ViewMessageTypes.EvntDel, vid), Is.Null);
      }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Change_FuncOutputCatalogUpdatesErrorsWithoutRemovingLinks(bool valid) {
      string vid = Vid(Pa1 + "/F1/out.M");
      _f1.SetField("LPLC.out.M", true);
      _f1.SetState(Str(valid ? "return A;" : "this.M = A; return A;"));
      _m1.SetField(LplcModel.VarInputField, "F1.M");
      Tick();
      LP_Schema.SyncFunc(_f1, null);
      Tick();
      Open();
      Assert.That(Added(vid), Is.Not.Null);
      Assert.That(Flag(Added(vid), "pinError"), Is.EqualTo(valid));

      _f1.SetState(Str(valid ? "this.M = A; return A;" : "return A;"));
      Tick();
      _sent.Clear();
      Assert.That(LP_Schema.SyncFunc(_f1, null), Is.True);
      Tick();
      JSC.JSObject pin = Added(vid);
      Assert.That(pin, Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(pin["pinError"].Defined, Is.True);
        Assert.That(Flag(pin, "pinError"), Is.EqualTo(!valid));
        Assert.That(Keys(_f1.GetField("LPLC.out")), Is.EqualTo(new[] { "Q", "M" }));
        Assert.That(_m1.GetField(LplcModel.VarInputField).AsString(null), Is.EqualTo("F1.M"));
        Assert.That(Frame(ViewMessageTypes.EvntDel, vid), Is.Null);
      }
    }

    [Test]
    public void Change_BlockTypePreservesStaleConstantInput() {
      string vid = Vid(Pa1 + "/A1/in.B");
      Open();
      _sent.Clear();
      _a1.SetField("type", "LPLC/Logic/lNOT");
      Tick();
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(vid), "pinError"), Is.True);
        Assert.That(Text(Added(vid), "displayValue"), Is.EqualTo("5"));
        Assert.That(_a1.GetField("LPLC.in.B").AsInt(-1), Is.EqualTo(5));
        Assert.That(Frame(ViewMessageTypes.EvntDel, vid), Is.Null);
      }

      _sent.Clear();
      _a1.SetField("type", "LPLC/Math/ADD");
      Tick();
      Assert.That(Added(vid), Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(Flag(Added(vid), "pinError"), Is.False);
        Assert.That(Text(Added(vid), "displayValue"), Is.EqualTo("5"));
        Assert.That(_a1.GetField("LPLC.in.B").AsInt(-1), Is.EqualTo(5));
      }
    }

    [Test]
    public void Change_ShownInputReachesTheClient() {
      Open();
      _sent.Clear();
      _a1.SetField("LPLC.in.C", "");
      Tick();
      Assert.That(Added(Vid(Pa1 + "/A1/in.C")), Is.Not.Null);
    }

    [Test]
    public void RemovedProgram_DeletesTheCanvas() {
      Open();
      _sent.Clear();
      _pa1.Remove();
      Tick();
      Assert.That(Frame(ViewMessageTypes.EvntDel, Vid(Pa1)), Is.Not.Null);
    }

    // === Меню ===

    [Test]
    public void SerialPortIconIsValidSvgAndAppearsInMenuAndOnCanvas() {
      DeviceType(LplcSerialTests.Description);
      string icon = root.Get(LP_Tree.TYPES + "/SerialPort", false).GetState().AsString("icon", null);
      const string prefix = "data:image/svg+xml;base64,";
      Assert.That(icon, Does.StartWith(prefix));
      byte[] content = System.Convert.FromBase64String(icon.Substring(prefix.Length));
      var svg = new System.Xml.XmlDocument { XmlResolver = null };
      svg.LoadXml(System.Text.Encoding.UTF8.GetString(content));
      Assert.That(svg.DocumentElement.LocalName, Is.EqualTo("svg"));
      Assert.That(svg.DocumentElement.NamespaceURI, Is.EqualTo("http://www.w3.org/2000/svg"));
      Assert.That(svg.DocumentElement.GetAttribute("viewBox"), Is.EqualTo("0 0 16 16"));
      const string url = "/api/icons/LPLC/SerialPort.svg";
      Assert.That(Flatten(Menu(Pa1)).Single(i => i.Text == "SerialPort").Icon, Is.EqualTo(url));
      Assert.That(IconResource.TryGetIconContent(url), Is.EqualTo(content));
      AssertOk(Rpc(Pa1, "add-serial:2"));
      var serial = _pa1.children.Single(t => t.GetField("type").AsString(null) == X13.Plc.PlcScript.SerialPort);
      Open();
      Assert.That(Text(Added(Vid(serial.path)), "icon"), Is.EqualTo(url));
    }

    [Test]
    public void SerialPortCreationSettingsStatePinsAndTraceUseTheExistingEditor() {
      DeviceType(LplcSerialTests.Description);
      Assert.That(Flatten(Menu(Pa1)).Where(i => i.Cmd != null && i.Cmd.StartsWith("add-serial:")).Select(i => i.Cmd),
        Is.EquivalentTo(new[] { "add-serial:1", "add-serial:2" }));
      AssertOk(Rpc(Pa1, "add-serial:2"));
      var serial = _pa1.children.Single(t => t.GetField("type").AsString(null) == X13.Plc.PlcScript.SerialPort);
      Assert.That(serial.GetState().AsString(null), Is.Empty);
      Assert.That(serial.GetField("LPLC.out.Busy").Defined, Is.False);
      Assert.That(serial.GetField("LPLC.out.Done").Defined, Is.False);
      Assert.That(serial.GetField("LPLC.args.DePin").AsInt(0), Is.EqualTo(-1));
      Assert.That(serial.GetField("LPLC.args.TxPin").Defined, Is.False);
      AssertOk(Rpc(serial.path, "set-arg", "{'name':'TxPin','value':4}"));
      AssertOk(Rpc(serial.path, "set-arg", "{'name':'RxPin','value':5}"));
      Assert.That(Rpc(serial.path, "set-arg", "{'name':'DePin','value':5}").ErrorCode, Is.EqualTo("lplc_argument_invalid"));
      AssertOk(Rpc(serial.path, "set-arg", "{'name':'DePin','value':18}"));
      AssertOk(Rpc(serial.path, "set-arg", "{'name':'DePin','value':-1}"));
      AssertOk(Rpc(serial.path, "set-arg", "{'name':'Format','value':6}"));
      Assert.That(Rpc(serial.path, "set-arg", "{'name':'TxMode','value':20}").ErrorCode, Is.EqualTo("lplc_argument_readonly"));
      Assert.That(Rpc(Pa1, "add-serial:2").ErrorCode, Is.EqualTo("add_target_exists"));
      Assert.That(Flatten(Menu(Pa1)).Single(i => i.Cmd == "add-serial:2").Enabled, Is.False);
      serial.SetState(Str("this.Received = A; return this.Received;"));
      Tick();
      LP_Schema.SyncFunc(serial, null);
      Tick();
      AssertOk(Rpc(serial.path, "add-out", "{'name':'Received'}"));
      AssertOk(Rpc(serial.path, "show-out:Port"));
      Open();
      Assert.That(Added(Vid(serial.path + "/in.A")), Is.Not.Null);
      Assert.That(Added(Vid(serial.path + "/out.Port")), Is.Not.Null);
      Assert.That(Added(Vid(serial.path + "/out.Received")), Is.Not.Null);
      AssertOk(Rpc(serial.path + "/out.Received", "trace"));
      Assert.That(_dev.Get(serial.name + "_Received", false), Is.Not.Null);
    }

    [Test]
    public void SerialMenuUsesPinDefaultsAndDoesNotInventPorts() {
      DeviceType(LplcSerialTests.Description.Replace("'v':23,", "'v':23,'defaultPin':'TX',")
        .Replace("'v':24,", "'v':24,'defaultPin':'RX',"));
      _dev.SetField(X13.Plc.PlcCapabilities.UartsField, 4);
      Tick();
      Assert.That(Flatten(Menu(Pa1)).Where(i => i.Cmd != null && i.Cmd.StartsWith("add-serial:")).Select(i => i.Cmd),
        Is.EqualTo(new[] { "add-serial:2" }));
      Assert.That(Rpc(Pa1, "add-serial:1").ErrorCode, Is.EqualTo("lplc_serial_not_found"));
      AssertOk(Rpc(Pa1, "add-serial:2"));
      var serial = _pa1.children.Single(t => t.GetField("type").AsString(null) == X13.Plc.PlcScript.SerialPort);
      Assert.That(serial.GetField("LPLC.args.TxPin").AsString(null), Is.EqualTo("TX"));
      Assert.That(serial.GetField("LPLC.args.RxPin").AsString(null), Is.EqualTo("RX"));
    }

    /// <summary>Канва: блоки каталога типа по папкам, переменные - по шаблонам устройства, с запросом имени.</summary>
    [Test]
    public void Menu_CanvasOffersBlocksAndVariables() {
      List<MenuItemDto> menu = Menu(Pa1);
      MenuItemDto add = Flatten(menu).Single(i => i.Cmd == "add-block:LPLC/Math/ADD");
      MenuItemDto variable = Flatten(menu).Single(i => i.Cmd == "add-var:Int32");
      using(Assert.EnterMultipleScope()) {
        Assert.That(menu.Single(i => i.Text == "Math").Children, Does.Contain(add));
        Assert.That(add.Hint, Does.StartWith("LPLC addition"), "подсказка, а не группа меню");
        Assert.That(add.Willful, Is.False);
        Assert.That(menu.Single(i => i.Text == "Variable").Children, Does.Contain(variable));
        Assert.That(variable.Willful, Is.True);
        Assert.That(variable.Hint, Is.EqualTo("New LPLC variable Int32 (Md)"));
        Assert.That(Flatten(menu).Any(i => i.Cmd != null && i.Cmd.StartsWith("add:")), Is.False, "без запасного каталога Core");
      }
    }

    [Test]
    public void Menu_BlockTogglesItsPins() {
      List<MenuItemDto> menu = Menu(Pa1 + "/A1");
      string[] ins = menu.Single(i => i.Text == "Inputs").Children.Select(i => i.Cmd).ToArray();
      string[] outs = menu.Single(i => i.Text == "Outputs").Children.Select(i => i.Cmd).ToArray();
      using(Assert.EnterMultipleScope()) {
        Assert.That(ins, Is.EqualTo(new[] { "hide-in:A", "hide-in:B", "show-in:C", "show-in:D", "show-in:E", "show-in:F", "show-in:G", "show-in:H" }));
        Assert.That(outs, Is.EqualTo(new[] { "hide-out:Q" }), "Add output - только у Func");
        Assert.That(menu.Any(i => i.Cmd == "delete"), Is.True);
      }
    }

    [Test]
    public void Menu_FuncAddsOutputsByName() {
      MenuItemDto addOut = Flatten(Menu(Pa1 + "/F1")).Single(i => i.Cmd == LplcPaletteBuilder.AddOutCmd);
      Assert.That(addOut.Willful, Is.True);
    }

    /// <summary>Disconnect доступен только подключённому: константа - тоже подключение, "" - нет.</summary>
    [Test]
    public void Menu_DisconnectNeedsAConnection() {
      using(Assert.EnterMultipleScope()) {
        Assert.That(Item(Menu(Dev + "/M1"), "unbind").Enabled, Is.True);
        Assert.That(Item(Menu(Dev + "/M2"), "unbind").Enabled, Is.False);
        Assert.That(Item(Menu(Pa1 + "/A1/in.B"), "unbind").Enabled, Is.True, "константа");
        Assert.That(Item(Menu(Pa1 + "/F1/in.B"), "unbind").Enabled, Is.False, "\"\"");
      }
    }

    // === Команды ===

    [Test]
    public void Bind_InputToAVariable() {
      AssertOk(Rpc(Pa1 + "/F1/in.B", "bind", "{'source':'" + Vid(Dev + "/M2") + "'}"));
      Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo("M2"));
    }

    [Test]
    public void Bind_VariableToAnOutput() {
      AssertOk(Rpc(Dev + "/M2", "bind", "{'source':'" + Vid(Pa1 + "/A1/out.Q") + "'}"));
      Assert.That(_m2.GetField(LplcModel.VarInputField).AsString(null), Is.EqualTo("A1.Q"));
    }

    [Test]
    public void Bind_RefusesAnInputAsTheSource() {
      ViewOpResult r = Rpc(Pa1 + "/F1/in.B", "bind", "{'source':'" + Vid(Pa1 + "/A1/in.A") + "'}");
      Assert.That(r.ErrorCode, Is.EqualTo("bind_source_invalid"));
    }

    /// <summary>Отключённый вход остаётся показанным - как "".</summary>
    [Test]
    public void Unbind_KeepsTheInputShown() {
      AssertOk(Rpc(Pa1 + "/A1/in.A", "unbind"));
      Assert.That(_a1.GetField("LPLC.in.A").AsString(null), Is.EqualTo(string.Empty));
    }

    [Test]
    public void ShowAndHideInput() {
      AssertOk(Rpc(Pa1 + "/A1", "show-in:C"));
      AssertOk(Rpc(Pa1 + "/A1", "hide-in:B"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Keys(_a1.GetField("LPLC.in")), Is.EqualTo(new[] { "A", "C" }));
        Assert.That(Rpc(Pa1 + "/A1", "show-in:Z").ErrorCode, Is.EqualTo("pin_not_found"));
      }
    }

    /// <summary>Скрытый выход отключает всё, что он питал.</summary>
    [Test]
    public void HideOutput_DisconnectsItsSinks() {
      AssertOk(Rpc(Pa1 + "/F1", "hide-out:Q"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Keys(_f1.GetField("LPLC.out")), Is.Empty);
        Assert.That(_m1.GetField(LplcModel.VarInputField).Defined, Is.False);
      }
    }

    /// <summary>Без LPLC.out показан весь каталог: скрытие пишет список явно, без скрытого.</summary>
    [Test]
    public void HideOutput_WithoutAListWritesOne() {
      Topic n1 = Block("N1", "LPLC/Logic/lNOT", "{'pos':{'top':12,'left':2},'in':{'A':''}}");
      Tick();
      AssertOk(Rpc(Pa1 + "/N1", "hide-out:Q"));
      JSC.JSValue outs = n1.GetField("LPLC.out");
      using(Assert.EnterMultipleScope()) {
        Assert.That(outs.IsObject(), Is.True);
        Assert.That(Keys(outs), Is.Empty);
      }
    }

    [Test]
    public void AddOutput_ShowsANamedFuncOutput() {
      AssertOk(Rpc(Pa1 + "/F1", LplcPaletteBuilder.AddOutCmd, "{'name':'M'}"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Keys(_f1.GetField("LPLC.out")), Is.EqualTo(new[] { "Q", "M" }));
        Assert.That(Rpc(Pa1 + "/F1", LplcPaletteBuilder.AddOutCmd, "{'name':'1x'}").ErrorCode, Is.EqualTo("add_name_invalid"));
        Assert.That(Rpc(Pa1 + "/A1", LplcPaletteBuilder.AddOutCmd, "{'name':'M'}").ErrorCode, Is.EqualTo("add_output_unsupported"));
      }
    }

    /// <summary>Без LPLC.out новый выход дописывается к каталогу, а не заменяет его.</summary>
    [Test]
    public void AddOutput_WithoutAListKeepsTheCatalog() {
      Topic g1 = Block("G1", LplcModel.FuncType, "{'pos':{'top':12,'left':12},'in':{'A':''}}");
      Tick();
      AssertOk(Rpc(Pa1 + "/G1", LplcPaletteBuilder.AddOutCmd, "{'name':'M'}"));
      Assert.That(Keys(g1.GetField("LPLC.out")), Is.EqualTo(new[] { "Q", "M" }));
    }

    /// <summary>Удаление блока отключает входы, которые питали его выходы, и не трогает остальные.</summary>
    [Test]
    public void DeleteBlock_DisconnectsItsSinks() {
      AssertOk(Rpc(Pa1 + "/A1", "delete"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(_a1.disposed, Is.True);
        Assert.That(_f1.GetField("LPLC.in.A").AsString(null), Is.EqualTo(string.Empty));
        Assert.That(_m1.GetField(LplcModel.VarInputField).AsString(null), Is.EqualTo("F1.Q"));
      }
    }

    [Test]
    public void DeleteVariable_DisconnectsItsSinks() {
      AssertOk(Rpc(Dev + "/M1", "delete"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(_m1.disposed, Is.True);
        Assert.That(_a1.GetField("LPLC.in.A").AsString(null), Is.EqualTo(string.Empty));
      }
    }

    /// <summary>Блок - по типу каталога, с умолчаниями типа, в нажатой клетке, но не левее колонки входов.</summary>
    [Test]
    public void AddBlock_FromTheCatalogAtTheCell() {
      AssertOk(Rpc(Pa1, "add-block:LPLC/Math/ADD", "{'top':3,'left':0}"));
      Topic added = _pa1.Get("ADD01", false);
      Assert.That(added, Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(added.GetField("type").AsString(null), Is.EqualTo("LPLC/Math/ADD"));
        Assert.That(added.GetField("LPLC.pos.top").AsInt(-1), Is.EqualTo(3));
        Assert.That(added.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(LogramGraphController.MinLeftCell));
        Assert.That(Keys(added.GetField("LPLC.in")), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(added.GetField("LPLC.in.A").AsInt(-1), Is.Zero);
        Assert.That(added.GetField("LPLC.in.B").AsInt(-1), Is.Zero);
      }
    }

    [TestCase("Logic/COMP_LT")]
    [TestCase("Logic/COMP_LE")]
    [TestCase("Logic/COMP_EQ")]
    [TestCase("Logic/COMP_NE")]
    [TestCase("Logic/COMP_GT")]
    [TestCase("Logic/COMP_GE")]
    [TestCase("Binary/Trigger")]
    public void AddBlock_NewCatalogTypesExposeEditableZeroInputs(string type) {
      string cmd = "add-block:LPLC/" + type;
      Assert.That(Flatten(Menu(Pa1)).Any(i => i.Cmd == cmd), Is.True);
      AssertOk(Rpc(Pa1, cmd, "{'name':'NewBlock'}"));
      var block = _pa1.children.Single(t => t.GetField("type").AsString(null) == "LPLC/" + type);
      Open();
      foreach(var input in block.GetField("LPLC.in")) {
        var row = Added(Vid(block.path + "/in." + input.Key));
        Assert.That(input.Value.IsNumber, Is.True);
        Assert.That(Text(row, "displayValue"), Is.EqualTo("0"));
        Assert.That(Flag(row, "constantEditable"), Is.True);
        Assert.That(Flag(row, "pinError"), Is.False);
      }
      if(type == "Binary/Trigger") {
        Assert.That(Keys(block.GetField("LPLC.in")), Is.EqualTo(new[] { "S", "D", "C", "R" }));
        Assert.That(Added(Vid(block.path + "/out.NQ")), Is.Not.Null);
        Assert.That(Flag(Added(Vid(block.path + "/out.NQ")), "pinError"), Is.False);
        AssertOk(Rpc(Pa1 + "/F1/in.B", "bind", "{'source':'" + Vid(block.path + "/out.NQ") + "'}"));
        Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo(block.name + ".NQ"));
      }
    }

    [Test]
    public void AddVariable_ByNameFromTheTemplate() {
      AssertOk(Rpc(Pa1, "add-var:Int32", "{'name':'Cnt','top':2,'left':4}"));
      Topic added = _dev.Get("Cnt", false);
      Assert.That(added, Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(added.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo("Md"));
        Assert.That(added.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(4));
        Assert.That(LplcModel.Resolve(Vid(Dev + "/Cnt")).Kind, Is.EqualTo(LplcModel.TargetKind.Variable));
        Assert.That(Rpc(Pa1, "add-var:Int32", "{'name':'M1'}").ErrorCode, Is.EqualTo("add_target_exists"));
      }
    }

    [Test]
    public void Commit_MovesWithinTheCanvas() {
      AssertOk(_provider.Commit(Vid(Pa1 + "/A1"), Json("{'top':7,'left':0}")));
      Tick();
      using(Assert.EnterMultipleScope()) {
        Assert.That(_a1.GetField("LPLC.pos.top").AsInt(-1), Is.EqualTo(7));
        Assert.That(_a1.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(LogramGraphController.MinLeftCell));
      }
    }

    // === Pin ===

    /// <summary>Pin в меню канвы: подменю режимов описания устройства, в них - доступные пины по имени, без иконок.</summary>
    [Test]
    public void Menu_PinIsModeThenPin() {
      DeviceType(DevicePlcDeviceTests.Stm);
      MenuItemDto pin = PinMenu();
      MenuItemDto pwm = pin.Children.Single(i => i.Text == "PWM");
      MenuItemDto leaf = Flatten(pin.Children).Single(i => i.Cmd == "add-pin:OUT:PA5");
      using(Assert.EnterMultipleScope()) {
        Assert.That(pin.Cmd, Is.Null, "только подменю");
        Assert.That(pin.Children.Select(i => i.Text), Is.EqualTo(new[] { "OUT", "PWM", "ANALOG" }));
        Assert.That(pwm.Children.Select(i => i.Text), Is.EqualTo(new[] { "PA0", "PA1", "PA6", "PB0" }), "пины всех групп режима");
        Assert.That(leaf.Text, Is.EqualTo("PA5"));
        Assert.That(leaf.Icon, Is.Null);
        Assert.That(leaf.Willful, Is.False);
        Assert.That(leaf.Enabled, Is.True);
        Assert.That(Flatten(Menu(Pa1)).Any(i => i.Cmd == "add-block:" + LplcModel.PinType), Is.False);
      }
    }

    /// <summary>Режим без io - только для ConfigPin и в меню не попадает; пин без имени - своим номером.</summary>
    [Test]
    public void Menu_PinLeavesOutConfigOnlyModes() {
      DeviceType(DevicePlcDeviceTests.Esp32);
      MenuItemDto pin = PinMenu();
      using(Assert.EnterMultipleScope()) {
        Assert.That(pin.Children.Select(i => i.Text), Is.EqualTo(new[] { "IN", "IN_INV_PU", "OUT", "PWM", "CNT_RISE", "ANALOG" }));
        Assert.That(pin.Children.Single(i => i.Text == "ANALOG").Children.Select(i => i.Cmd), Is.EqualTo(new[] { "add-pin:ANALOG:34" }));
      }
    }

    [Test]
    public void Menu_PinWithoutADescriptionIsDisabled() {
      MenuItemDto pin = PinMenu();
      using(Assert.EnterMultipleScope()) {
        Assert.That(pin.Enabled, Is.False);
        Assert.That(pin.Children, Is.Empty);
      }
    }

    /// <summary>Новый Pin: mode и pin заполнены, имя mode_pin, вход x у режима выхода.</summary>
    [Test]
    public void AddPin_FillsModeAndPin() {
      DeviceType(DevicePlcDeviceTests.Stm);
      AssertOk(Rpc(Pa1, "add-pin:OUT:PA5", "{'top':2,'left':0}"));
      Topic added = _pa1.Get("OUT_PA5", false);
      Assert.That(added, Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(added.GetField("type").AsString(null), Is.EqualTo(LplcModel.PinType));
        Assert.That(added.GetField("LPLC.args.mode").AsString(null), Is.EqualTo("OUT"));
        Assert.That(added.GetField("LPLC.args.pin").AsString(null), Is.EqualTo("PA5"));
        Assert.That(Keys(added.GetField("LPLC.in")), Is.EqualTo(new[] { "x" }));
        Assert.That(added.GetField("LPLC.pos.top").AsInt(-1), Is.EqualTo(2));
        Assert.That(added.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(0), "как переменная - у края");
        Assert.That(Rpc(Pa1, "add-pin:OUT:PA5").ErrorCode, Is.EqualTo("add_target_exists"));
        Assert.That(Flatten(PinMenu().Children).Single(i => i.Cmd == "add-pin:OUT:PA5").Enabled, Is.False, "имя занято");
      }
    }

    /// <summary>Пин без имени пишется числом; у режима входа нет входа x.</summary>
    [Test]
    public void AddPin_InputModeHasNoInput() {
      DeviceType(DevicePlcDeviceTests.Esp32);
      AssertOk(Rpc(Pa1, "add-pin:IN:2", "{'top':2,'left':4}"));
      Topic added = _pa1.Get("IN_2", false);
      Assert.That(added, Is.Not.Null);
      using(Assert.EnterMultipleScope()) {
        Assert.That(added.GetField("LPLC.args.mode").AsString(null), Is.EqualTo("IN"));
        Assert.That(added.GetField("LPLC.args.pin").IsNumber, Is.True);
        Assert.That(added.GetField("LPLC.args.pin").AsInt(-1), Is.EqualTo(2));
        Assert.That(added.GetField("LPLC.in").Defined, Is.False);
        Assert.That(Keys(added.GetField("LPLC.out")), Is.EqualTo(new[] { "value" }));
      }
    }

    [Test]
    public void AddPin_RefusesWhatTheDeviceDoesNotOffer() {
      DeviceType(DevicePlcDeviceTests.Esp32);
      using(Assert.EnterMultipleScope()) {
        Assert.That(Rpc(Pa1, "add-pin:ANALOG:2").ErrorCode, Is.EqualTo("lplc_pin_not_found"));
        Assert.That(Rpc(Pa1, "add-pin:OFF:2").ErrorCode, Is.EqualTo("lplc_pin_mode_not_found"), "только ConfigPin");
        Assert.That(Rpc(Pa1, "add-block:" + LplcModel.PinType).ErrorCode, Is.EqualTo("lplc_pin_args_required"));
        Assert.That(_pa1.children.Any(t => LplcModel.IsPin(t)), Is.False);
      }
    }

    /// <summary>Pin на схеме - как переменная: своё имя, без иконки и строк входов/выходов; провода - к нему самому.</summary>
    [Test]
    public void Snapshot_PinIsDrawnAsAVariable() {
      DeviceType(DevicePlcDeviceTests.Stm);
      Block("OUT_PA5", LplcModel.PinType, "{'pos':{'top':5,'left':20},'args':{'pin':'PA5','mode':'OUT'},'in':{'x':'A1.Q'},'out':{'value':true}}");
      Block("ANALOG_PA0", LplcModel.PinType, "{'pos':{'top':5,'left':0},'args':{'pin':'PA0','mode':'ANALOG'},'out':{'value':true}}");
      _f1.SetField("LPLC.in.B", "ANALOG_PA0.value");
      Tick();
      Open();
      JSC.JSObject pin = Added(Vid(Pa1 + "/OUT_PA5"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(Text(pin, "editor"), Is.EqualTo("LogramVariable"));
        Assert.That(Text(pin, "name"), Is.EqualTo("OUT_PA5"));
        Assert.That(Text(pin, "icon"), Is.Empty);
        Assert.That((int)pin["height"], Is.EqualTo(1));
        Assert.That(Added(Vid(Pa1 + "/OUT_PA5/in.x")), Is.Null);
        Assert.That(Added(Vid(Pa1 + "/OUT_PA5/out.value")), Is.Null);
        Assert.That(Text(Updated(Vid(Pa1 + "/OUT_PA5")), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/A1/out.Q")));
        Assert.That(Flag(Updated(Vid(Pa1 + "/OUT_PA5")), "sourceLocal"), Is.True);
        Assert.That(Text(Updated(Vid(Pa1 + "/F1/in.B")), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/ANALOG_PA0")));
        Assert.That(Flag(Updated(Vid(Pa1 + "/F1/in.B")), "sourceLocal"), Is.True);
      }
    }

    /// <summary>Pin связывается как переменная: источник - Pin.value, приёмник - его x, если режим его имеет.</summary>
    [Test]
    public void Bind_PinIsASinkAndASource() {
      DeviceType(DevicePlcDeviceTests.Stm);
      AssertOk(Rpc(Pa1, "add-pin:OUT:PA5"));
      AssertOk(Rpc(Pa1, "add-pin:ANALOG:PA0"));
      AssertOk(Rpc(Pa1 + "/OUT_PA5", "bind", "{'source':'" + Vid(Pa1 + "/A1/out.Q") + "'}"));
      AssertOk(Rpc(Pa1 + "/F1/in.B", "bind", "{'source':'" + Vid(Pa1 + "/ANALOG_PA0") + "'}"));
      Topic out5 = _pa1.Get("OUT_PA5", false);
      List<MenuItemDto> menu = Menu(Pa1 + "/OUT_PA5");
      using(Assert.EnterMultipleScope()) {
        Assert.That(out5.GetField("LPLC.in.x").AsString(null), Is.EqualTo("A1.Q"));
        Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo("ANALOG_PA0.value"));
        Assert.That(Rpc(Pa1 + "/ANALOG_PA0", "bind", "{'source':'" + Vid(Pa1 + "/A1/out.Q") + "'}").ErrorCode, Is.EqualTo("bind_target_invalid"));
        Assert.That(menu.Any(i => i.Text == "Inputs" || i.Text == "Outputs"), Is.False);
        Assert.That(Item(menu, "unbind").Enabled, Is.True);
        Assert.That(menu.Any(i => i.Cmd == "delete"), Is.True);
      }
      AssertOk(Rpc(Pa1 + "/OUT_PA5", "unbind"));
      AssertOk(Rpc(Pa1 + "/ANALOG_PA0", "delete"));
      using(Assert.EnterMultipleScope()) {
        Assert.That(out5.GetField("LPLC.in.x").AsString(null), Is.EqualTo(string.Empty));
        Assert.That(_f1.GetField("LPLC.in.B").AsString(null), Is.EqualTo(string.Empty), "удалённый Pin ничего не питает");
      }
    }

    [TestCase(null)]
    [TestCase(false)]
    [TestCase(true)]
    public void Snapshot_CanvasCarriesPlcStateAndNullableComparison(bool? modified) {
      _pa1.SetState(2);
      if(modified.HasValue) _pa1.SetField("LPLC.modified", modified.Value);
      Tick();
      Open();
      var canvas = Added(Vid(Pa1));
      Assert.That((int)canvas["value"], Is.EqualTo(2));
      if(modified.HasValue) {
        Assert.That(canvas["plcModified"].ValueType, Is.EqualTo(JSC.JSValueType.Boolean));
        Assert.That((bool)canvas["plcModified"], Is.EqualTo(modified.Value));
      }
      else Assert.That(canvas["plcModified"].IsNull, Is.True);
    }

    [Test]
    public void Change_PlcStatusResendsOnlyTheCanvas() {
      Open();
      Tick();
      foreach(JSC.JSValue modified in new[] { (JSC.JSValue)false, true, JSC.JSValue.Null }) {
        _sent.Clear();
        _pa1.SetField("LPLC.modified", modified);
        Tick();
        Assert.That(_sent, Has.Count.EqualTo(1));
        Assert.That(Text(_sent[0], "vid"), Is.EqualTo(Vid(Pa1)));
        Assert.That(JsLib.Stringify(_sent[0]["plcModified"]), Is.EqualTo(JsLib.Stringify(modified)));
      }
      _sent.Clear();
      _pa1.SetState(1);
      Tick();
      Assert.That(_sent, Has.Count.EqualTo(1));
      Assert.That((int)Added(Vid(Pa1))["value"], Is.EqualTo(1));
      _sent.Clear();
      _m1.SetState(42);
      _f1.SetState(Str("return A + 1;"));
      _pa1.SetField("LPLC.loaded", "confirmed");
      Tick();
      Assert.That(_sent, Is.Empty, "source/loaded changes are represented by the backend comparison, not raw UI guesses");
    }

    [TestCase("Build")]
    [TestCase("Run")]
    [TestCase("Start")]
    [TestCase("Stop")]
    public void Actions_ProgramTypeDeclaresAllPa0Commands(string command) {
      _pa1.Move(null, "Logic");
      Tick();
      JSC.JSValue descriptor;
      Assert.That(MenuBuilder.ResolveActionDescriptor(_pa1, "MQTT_SN.PLC." + command, out descriptor), Is.True);
      Assert.That(descriptor.AsString("name", null), Is.EqualTo("MQTT_SN.PLC." + command));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Actions_DeclaredHandlerReceivesProgramAndArguments(bool asynchronous) {
      string action = "Test.Lplc." + System.Guid.NewGuid().ToString("N");
      root.Get("/$YS/TYPES/Ext/LogramPLC", false).SetState(Json("{'Action':[{'name':'" + action + "'}]}"));
      _pa1.Move(null, "Logic");
      Tick();
      Topic target = null;
      JSC.JSValue argument = null;
      System.Action<JSC.JSValue> reply = null;
      RPC.Register(action, (t, a, done) => {
        target = t;
        argument = a;
        if(asynchronous) reply = done;
        else done(JSC.JSValue.Undefined);
      });
      ViewOpResult pending = Rpc(_pa1.path, "action:" + action, "{'test':3}");
      Assert.That(target, Is.SameAs(_pa1));
      Assert.That((int)argument["test"], Is.EqualTo(3));
      Assert.That(pending.Continuation, Is.Not.Null);
      var replies = new List<ViewOpResult>();
      pending.Continuation(replies.Add);
      if(asynchronous) {
        Assert.That(replies, Is.Empty);
        reply(Json("{'error':'device_busy','message':'Busy'}"));
        reply(JSC.JSValue.Undefined);
        Assert.That(replies, Has.Count.EqualTo(1));
        Assert.That(replies[0].ErrorCode, Is.EqualTo("device_busy"));
      }
      else {
        Assert.That(replies, Has.Count.EqualTo(1));
        AssertOk(replies[0]);
      }
    }

    [Test]
    public void Actions_RejectUndeclaredNamesAndReportMissingHandlers() {
      string action = "Test.Lplc." + System.Guid.NewGuid().ToString("N");
      bool called = false;
      RPC.Register(action, (t, a) => called = true);
      Assert.That(Rpc(Pa1, "action:" + action).ErrorCode, Is.EqualTo("action_not_found"));
      Assert.That(called, Is.False);
      string missing = action + ".Missing";
      _pa1.SetField("Action", Json("[{'name':'" + missing + "'}]"));
      Tick();
      Assert.That(Rpc(Pa1, "action:" + missing).ErrorCode, Is.EqualTo("action_no_handler"));
    }

    [TestCase(Pa1 + "/F1")]
    [TestCase(Pa1 + "/F1/in.A")]
    [TestCase(Pa1 + "/F1/out.Q")]
    [TestCase(Dev + "/M1")]
    public void Actions_RejectNonProgramTargets(string path) {
      Assert.That(Rpc(path, "action:MQTT_SN.PLC.Run").ErrorCode, Is.EqualTo("rpc_command_unknown"));
    }

    // === Trace ===

    [Test]
    public void TraceMenuIsAvailableOnlyForOutputsAndHardwarePins() {
      DeviceType(DevicePlcDeviceTests.Stm);
      AssertOk(Rpc(Pa1, "add-pin:ANALOG:PA0"));
      Assert.That(Item(Menu(Pa1 + "/A1/out.Q"), "trace").Enabled, Is.True);
      Assert.That(Item(Menu(Pa1 + "/ANALOG_PA0"), "trace").Enabled, Is.True);
      Assert.That(Item(Menu(Pa1 + "/F1/out.Missing"), "trace").Enabled, Is.False);
      foreach(string path in new[] { Pa1, Pa1 + "/A1", Pa1 + "/A1/in.A", Dev + "/M1" }) {
        Assert.That(Flatten(Menu(path)).Any(i => i.Cmd == "trace"), Is.False, path);
        Assert.That(Rpc(path, "trace").ErrorCode, Is.EqualTo("rpc_command_unknown"), path);
      }
    }

    [Test]
    public void TraceCreatesUnboundObserverWithoutAnotherCanvasVariable() {
      Open();
      _sent.Clear();
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      Topic observer = _dev.Get("A1_Q", false);
      Assert.That(observer, Is.Not.Null);
      Assert.That(observer.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo("Md"));
      Assert.That(observer.GetState().IsNullOrUndefined(), Is.True);
      Assert.That(observer.GetField(LplcModel.ObserverField).AsBool(false), Is.True);
      Assert.That(observer.GetField(LplcModel.TraceField).AsBool(false), Is.True);
      Assert.That(observer.GetField("LPLC.pos").Defined, Is.False);
      Assert.That(observer.GetField(LplcModel.VarInputField).Defined, Is.False);
      Assert.That(Added(Vid(observer.path)), Is.Null);
      Assert.That(Flag(Updated(Vid(Pa1 + "/A1/out.Q")), "trace"), Is.True);
      Assert.That(Text(Updated(Vid(Pa1 + "/A1/out.Q")), "displayValue"), Is.Empty);
      Assert.That(Item(Menu(Pa1 + "/A1/out.Q"), "trace").Checked, Is.True);
      Assert.That(LP_Tree.Info(_a1), Is.Null, "Trace не компилирует");
      Assert.That(LplcModel.Variables(_pa1), Does.Contain(observer), "наблюдатель остаётся входом модели компилятора");
    }

    [Test]
    public void TraceUsesCompiledTemplateAndReusesExistingObserverState() {
      _dev.SetField("Children.A1_Q", Json("{'menu':'plc','default':0,'manifest':{'MQTT-SN':{'tag':'Mf24'}}}"));
      Tick();
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      var observer = _dev.Get("A1_Q", false);
      Assert.That(observer.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo("Mf24"));
      observer.SetState(12.5);
      Tick();
      Open();
      Assert.That(Text(Added(Vid(Pa1 + "/A1/out.Q")), "displayValue"), Is.EqualTo("12.5"));
      Assert.That(Added(Vid(observer.path)), Is.Null);
      _sent.Clear();
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      Assert.That(Flag(Updated(Vid(Pa1 + "/A1/out.Q")), "trace"), Is.False);
      Assert.That(Updated(Vid(Pa1 + "/A1/out.Q"))["trace"].Defined, Is.True);
      Assert.That(Item(Menu(Pa1 + "/A1/out.Q"), "trace").Checked, Is.False);
      Assert.That(Added(Vid(observer.path)), Is.Null);
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      Assert.That(_dev.Get("A1_Q", false), Is.SameAs(observer));
      Assert.That((double)observer.GetState(), Is.EqualTo(12.5));
      Assert.That(Text(Updated(Vid(Pa1 + "/A1/out.Q")), "displayValue"), Is.EqualTo("12.5"));
    }

    [Test]
    public void TraceLiveValuesUsePatchesAndStopWhenDisabled() {
      Open();
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      var observer = _dev.Get("A1_Q", false);
      _sent.Clear();
      observer.SetState(1.234561);
      Tick();
      Assert.That(_sent, Has.Count.EqualTo(1));
      Assert.That(Text(Updated(Vid(Pa1 + "/A1/out.Q")), "displayValue"), Is.EqualTo("1.235"));
      Assert.That(Added(Vid(Pa1 + "/A1/out.Q")), Is.Null);
      Assert.That(Updated(Vid(Pa1 + "/F1/in.A")), Is.Null, "провод не пересылается");
      _sent.Clear();
      observer.SetState(1.234562);
      Tick();
      Assert.That(_sent, Is.Empty, "форматированное значение не изменилось");
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      _sent.Clear();
      observer.SetState(7);
      Tick();
      Assert.That(_sent, Is.Empty);
    }

    [Test]
    public void TraceReusesExistingAliasWithoutChangingTagOrValue() {
      var observer = Var("A1_Q", "Mf16");
      observer.SetState(3.5);
      Tick();
      Open();
      Assert.That(Added(Vid(observer.path)), Is.Not.Null);
      _sent.Clear();
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      Assert.That(_dev.Get("A1_Q", false), Is.SameAs(observer));
      Assert.That(observer.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo("Mf16"));
      Assert.That((double)observer.GetState(), Is.EqualTo(3.5));
      Assert.That(Frame(ViewMessageTypes.EvntDel, Vid(observer.path)), Is.Not.Null);
      Assert.That(Text(Updated(Vid(Pa1 + "/A1/out.Q")), "displayValue"), Is.EqualTo("3.5"));
    }

    [Test]
    public void TraceHardwarePinValueUsesThePinRowAndKeepsItsWire() {
      DeviceType(DevicePlcDeviceTests.Stm);
      AssertOk(Rpc(Pa1, "add-pin:OUT:PA5"));
      AssertOk(Rpc(Pa1 + "/OUT_PA5", "bind", "{'source':'" + Vid(Pa1 + "/A1/out.Q") + "'}"));
      Open();
      AssertOk(Rpc(Pa1 + "/OUT_PA5", "trace"));
      var observer = _dev.Get("OUT_PA5_value", false);
      _sent.Clear();
      observer.SetState(17);
      Tick();
      Assert.That(Text(Updated(Vid(Pa1 + "/OUT_PA5")), "displayValue"), Is.EqualTo("17"));
      Assert.That(Updated(Vid(Pa1 + "/OUT_PA5"))["sourceVid"].Defined, Is.False);
      Assert.That(Added(Vid(observer.path)), Is.Null);
      Assert.That(Added(Vid(Pa1 + "/OUT_PA5/out.value")), Is.Null);
      _sent.Clear();
      _pa1.Get("OUT_PA5", false).SetField("LPLC.pos.left", 20);
      Tick();
      Assert.That(Flag(Added(Vid(Pa1 + "/OUT_PA5")), "trace"), Is.True);
      Assert.That(Text(Added(Vid(Pa1 + "/OUT_PA5")), "displayValue"), Is.EqualTo("17"));
      Assert.That(Text(Updated(Vid(Pa1 + "/OUT_PA5")), "sourceVid"), Is.EqualTo(Vid(Pa1 + "/A1/out.Q")));
    }

    [Test]
    public void TraceDeletedObserverClearsLabelAndCanBeRecreated() {
      Open();
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      _sent.Clear();
      _dev.Get("A1_Q", false).Remove();
      Tick();
      Assert.That(Updated(Vid(Pa1 + "/A1/out.Q"))["trace"].Defined, Is.True);
      Assert.That(Flag(Updated(Vid(Pa1 + "/A1/out.Q")), "trace"), Is.False);
      Assert.That(Item(Menu(Pa1 + "/A1/out.Q"), "trace").Checked, Is.False);
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      Assert.That(_dev.Get("A1_Q", false), Is.Not.Null);
    }

    [Test]
    public void TraceSurvivesHideShowAndRenamedProgramWithoutExtraVariable() {
      AssertOk(Rpc(Pa1 + "/A1/out.Q", "trace"));
      var observer = _dev.Get("A1_Q", false);
      observer.SetState(8);
      _pa1.Move(null, "Logic");
      Tick();
      Open();
      string output = _a1.path + "/out.Q";
      Assert.That(Text(Added(Vid(output)), "displayValue"), Is.EqualTo("8"));
      AssertOk(Rpc(output, "hide"));
      _sent.Clear();
      AssertOk(Rpc(_a1.path, "show-out:Q"));
      Assert.That(Flag(Added(Vid(output)), "trace"), Is.True);
      Assert.That(Text(Added(Vid(output)), "displayValue"), Is.EqualTo("8"));
      Assert.That(Added(Vid(observer.path)), Is.Null);
      _graph.Dispose();
      _sent.Clear();
      observer.SetState(9);
      Tick();
      Assert.That(_sent, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TraceRejectsUnrelatedTopicsAndInputSinks(bool variable) {
      var existing = variable ? Var("A1_Q", "Md", "{'in':{'x':'F1.Q'}}") : _dev.Get("A1_Q", true);
      existing.SetState(42);
      Tick();
      Assert.That(Item(Menu(Pa1 + "/A1/out.Q"), "trace").Enabled, Is.False);
      Assert.That(Rpc(Pa1 + "/A1/out.Q", "trace").ErrorCode, Is.EqualTo("trace_target_invalid"));
      Assert.That(_dev.Get("A1_Q", false), Is.SameAs(existing));
      Assert.That(existing.GetState().AsInt(0), Is.EqualTo(42));
      Assert.That(existing.GetField(LplcModel.ObserverField).Defined, Is.False);
    }

    [Test]
    public void TraceRejectsStaleOutputsAndAmbiguousObserverNames() {
      Assert.That(Rpc(Pa1 + "/A1/out.Missing", "trace").ErrorCode, Is.EqualTo("trace_target_invalid"));
      var first = Block("A_B", LplcModel.FuncType, "{'out':{'C':true}}");
      first.SetField("mi.LPLC.mi.out.mi.C.default", true);
      var second = Block("A", LplcModel.FuncType, "{'out':{'B_C':true}}");
      second.SetField("mi.LPLC.mi.out.mi.B_C.default", true);
      Tick();
      Assert.That(Rpc(first.path + "/out.C", "trace").ErrorCode, Is.EqualTo("trace_target_invalid"));
      Assert.That(Rpc(second.path + "/out.B_C", "trace").ErrorCode, Is.EqualTo("trace_target_invalid"));
      Assert.That(_dev.Get("A_B_C", false), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TraceObserverUsesExistingOutputMerkerWithoutChangingBytecode(bool hardwarePin) {
      DeviceType(DevicePlcDeviceTests.Stm);
      if(hardwarePin) AssertOk(Rpc(Pa1, "add-pin:ANALOG:PA0"));
      else _f1.SetState(Str("return A * 0.5;"));
      Tick();
      var device = DevicePlcDeviceTests.Device(DevicePlcDeviceTests.Stm);
      var before = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), X13.DevicePLC.EP_Target.EspNode, device);
      Assert.That(before.Ok, Is.True, string.Join("\n", before.Problems));
      string block = hardwarePin ? "ANALOG_PA0" : "F1";
      string pin = hardwarePin ? "value" : "Q";
      AssertOk(Rpc(Pa1 + "/" + block + (hardwarePin ? "" : "/out.Q"), "trace"));
      var observer = _dev.Get(block + "_" + pin, false);
      var after = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), X13.DevicePLC.EP_Target.EspNode, device);
      Assert.That(after.Ok, Is.True, string.Join("\n", after.Problems));
      Assert.That(after.Variables[observer.name], Is.EqualTo(after.Variables[block + "." + pin]));
      Assert.That(after.StackBottom, Is.EqualTo(before.StackBottom));
      Assert.That(LP_Tree.Fingerprint(after.Hex), Is.EqualTo(LP_Tree.Fingerprint(before.Hex)));
      new LP_Program(_pa1, X13.DevicePLC.EP_Target.EspNode).Retag(after);
      Tick();
      Assert.That(observer.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo(after.Variables[block + "." + pin]));
      Open();
      Assert.That(Added(Vid(observer.path)), Is.Null);
    }

    [Test]
    public void TwiMenuOffersOnlyCompleteBusesAndKeepsPinMenuSeparate() {
      DeviceType(LplcTwiTests.DeviceDescription);
      var menu = Flatten(Menu(Pa1)).Single(i => i.Text == "TWI");
      Assert.That(menu.Children.Select(i => i.Text), Is.EqualTo(new[] { "TWI0", "TWI1", "SCAN_BUS" }));
      Assert.That(menu.Children.Select(i => i.Cmd), Is.EqualTo(new[] { "add-twi:0", "add-twi:1", "add-block:LPLC/TWI/SCAN_BUS" }));
      Assert.That(Flatten(Menu(Pa1)).Any(i => i.Text == "TWI Master"), Is.False);
      Assert.That(PinMenu().Enabled, Is.False);
      Assert.That(Flatten(Menu(Pa1)).Any(i => i.Cmd == "add-block:" + LplcModel.TwiType), Is.False);
      Assert.That(_pa1.children.Any(LplcModel.IsTwi), Is.False);
      var compiledBuses = DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription).TwiBuses();
      Assert.That(LplcModel.TwiBuses(_pa1).Select(b => b.Number), Is.EqualTo(compiledBuses.Select(b => b.Number)));
      Assert.That(LplcModel.TwiBuses(_pa1)[0].Sda.Pins.Select(p => p.Number), Is.EquivalentTo(compiledBuses[0].Sda.Pins));
    }

    [Test]
    public void TwiMenuWithoutCompleteDescriptionStillOffersScanner() {
      var menu = Flatten(Menu(Pa1)).Single(i => i.Text == "TWI");
      Assert.That(menu.Enabled, Is.True);
      Assert.That(menu.Children.Select(i => i.Text), Is.EqualTo(new[] { "SCAN_BUS" }));
      Assert.That(menu.Hint, Is.Null);
      DeviceType(DevicePlcDeviceTests.Esp32);
      menu = Flatten(Menu(Pa1)).Single(i => i.Text == "TWI");
      Assert.That(menu.Enabled, Is.True);
      Assert.That(menu.Children.Select(i => i.Cmd), Is.EqualTo(new[] { "add-block:LPLC/TWI/SCAN_BUS" }));
      Assert.That(Rpc(Pa1, "add-twi:0").ErrorCode, Is.EqualTo("lplc_twi_not_found"));
    }

    [Test]
    public void TwiScannerCreationShowsConstructorScanAndAddrAndCompilesWithMaster() {
      DeviceType(LplcTwiTests.DeviceDescription);
      AssertOk(Rpc(Pa1, "add-twi:0"));
      var action = Item(Menu(Pa1), "add-block:LPLC/TWI/SCAN_BUS");
      Assert.That(action.Enabled, Is.True);
      Assert.That(action.Willful, Is.False);
      AssertOk(Rpc(Pa1, action.Cmd, "{'name':'Scan0','top':5,'left':8}"));
      var block = _pa1.Get("Scan0", false);
      Assert.That(block.GetField("type").AsString(null), Is.EqualTo("LPLC/TWI/SCAN_BUS"));
      Assert.That(block.GetField("LPLC.args.TwiNr").AsInt(-1), Is.Zero);
      Assert.That(block.GetField("LPLC.in.Scan").AsInt(-1), Is.Zero);
      Assert.That(Keys(block.GetField("LPLC.in")), Is.EqualTo(new[] { "Scan" }));
      Assert.That(Keys(block.GetField("LPLC.out")), Is.EqualTo(new[] { "Addr" }));
      Assert.That(block.GetField("LPLC.pos.top").AsInt(-1), Is.EqualTo(5));
      Assert.That(block.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(8));
      Assert.That(LplcModel.IsTwi(block), Is.False, "scanner is not a master");
      Open();
      var row = Added(Vid(block.path));
      Assert.That(ConfigRow(row, "TwiNr").AsString("text", null), Is.EqualTo("0"));
      Assert.That(row["twi"].Defined, Is.False);
      Assert.That((int)row["height"], Is.EqualTo(3));
      Assert.That(Added(Vid(block.path + "/in.Scan"))["pinIndex"].AsInt(-1), Is.EqualTo(2));
      Assert.That(Added(Vid(block.path + "/out.Addr"))["pinIndex"].AsInt(-1), Is.EqualTo(2));
      Assert.That(Added(Vid(block.path + "/in.Start")), Is.Null);
      Assert.That(Added(Vid(block.path + "/out.Q")), Is.Null);
      Assert.That(Menu(block.path).Any(i => i.Text == "Inputs"), Is.True);
      Assert.That(Menu(block.path).Any(i => i.Text == "Outputs"), Is.True);
      AssertOk(Rpc(block.path + "/in.Scan", "bind", "{'source':'" + Vid(_m2.path) + "'}"));
      AssertOk(Rpc(_m1.path, "bind", "{'source':'" + Vid(block.path + "/out.Addr") + "'}"));
      var result = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), X13.DevicePLC.EP_Target.EspNode,
        DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems) + "\n" + result.Source);
      Assert.That(result.Variables["Scan0.Addr"], Does.StartWith("Md"));
    }

    [Test]
    public void TwiCreationUsesZeroBasedIdentityAndOnlyExplicitDefaults() {
      DeviceType(LplcTwiTests.DeviceDescription);
      AssertOk(Rpc(Pa1, "add-twi:0", "{'top':3,'left':0,'name':'Ignored'}"));
      var first = _pa1.Get("TWI0", false);
      Assert.That(first.GetField("type").AsString(null), Is.EqualTo(LplcModel.TwiType));
      Assert.That(first.GetField("LPLC.args.TwiNr").AsInt(-1), Is.Zero);
      Assert.That(first.GetField("LPLC.args.SdaPin").AsString(null), Is.EqualTo("SDA"));
      Assert.That(first.GetField("LPLC.args.SclPin").AsString(null), Is.EqualTo("SCL"));
      Assert.That(first.GetField("LPLC.pos.top").AsInt(-1), Is.EqualTo(3));
      Assert.That(first.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(1));
      Assert.That(Keys(first.GetField("LPLC.args")), Is.EquivalentTo(new[] { "TwiNr", "SdaPin", "SclPin" }));
      AssertOk(Rpc(Pa1, "add-twi:1"));
      var second = _pa1.Get("TWI1", false);
      Assert.That(second.GetField("LPLC.args.TwiNr").AsInt(-1), Is.EqualTo(1));
      Assert.That(second.GetField("LPLC.args.SdaPin").Defined, Is.False);
      Assert.That(second.GetField("LPLC.args.SclPin").Defined, Is.False);
      Assert.That(Rpc(Pa1, "add-twi:-1").ErrorCode, Is.EqualTo("lplc_twi_not_found"));
      Assert.That(Rpc(Pa1, "add-twi:2").ErrorCode, Is.EqualTo("lplc_twi_not_found"));
      Assert.That(Rpc(Pa1, "add-twi:3").ErrorCode, Is.EqualTo("lplc_twi_not_found"));
      Assert.That(Rpc(Pa1, "add-block:" + LplcModel.TwiType).ErrorCode, Is.EqualTo("lplc_twi_args_required"));
      first.Move(null, "Renamed");
      Tick();
      Assert.That(Item(Menu(Pa1), "add-twi:0").Enabled, Is.False);
      Assert.That(Rpc(Pa1, "add-twi:0").ErrorCode, Is.EqualTo("add_target_exists"));
    }

    [TestCase("'defaultPin': 22", "'defaultPin': 99", true)]
    [TestCase("'defaultPin': 22", "'defaultPin': 'SDA_ALIAS'", false)]
    [TestCase("'v': 16 }", "'v': 16, 'defaultPin': 25 }", false)]
    public void TwiInvalidOrConflictingDefaultsStayUnset(string oldText, string newText, bool sdaPresent) {
      DeviceType(LplcTwiTests.DeviceDescription.Replace(oldText, newText));
      AssertOk(Rpc(Pa1, "add-twi:0"));
      var first = _pa1.Get("TWI0", false);
      Assert.That(first.GetField("LPLC.args.SdaPin").Defined, Is.EqualTo(sdaPresent));
      if(oldText.Contains("22")) Assert.That(first.GetField("LPLC.args.SclPin").Defined, Is.False);
    }

    [Test]
    public void TwiPinSelectionValidatesRolesAliasesAndCommands() {
      DeviceType(LplcTwiTests.DeviceDescription);
      AssertOk(Rpc(Pa1, "add-twi:0", "{'top':3,'left':5}"));
      string path = Pa1 + "/TWI0";
      var block = _pa1.Get("TWI0", false);
      Assert.That(Menu(path).Any(i => i.Text == "Inputs" || i.Text == "Outputs"), Is.False);
      Assert.That(ArgItem(Menu(path), "SdaPin", "SDA").Checked, Is.True);
      Assert.That(ArgItem(Menu(path), "SclPin", "SDA").Enabled, Is.False);
      Assert.That(SetArg(path, "SdaPin", "'SCL'").ErrorCode, Is.EqualTo("lplc_argument_invalid"));
      Assert.That(SetArg(path, "SclPin", "'SDA'").ErrorCode, Is.EqualTo("lplc_argument_invalid"));
      Assert.That(SetArg(path, "TwiNr", "2").ErrorCode, Is.EqualTo("lplc_argument_invalid"));
      Assert.That(SetArg(path, "SdaMode", "16").ErrorCode, Is.EqualTo("lplc_argument_readonly"));
      Assert.That(SetArg(path, "SclMode", "19").ErrorCode, Is.EqualTo("lplc_argument_readonly"));
      Assert.That(SetArg(path, "SdaPin.x", "0").Ok, Is.False);
      Assert.That(SetArg(Pa1 + "/A1", "SdaPin", "0").Ok, Is.False);
      Assert.That(Rpc(path + "/in.SdaPin", "bind", "{'source':'" + Vid(Pa1 + "/A1/out.Q") + "'}").Ok, Is.False);
      Assert.That(LplcModel.Resolve(Vid(path + "/out.Q")), Is.Null);
      block.SetField("LPLC.args.SdaPin", "SDA_ALIAS");
      Tick();
      Assert.That(SetArg(path, "SclPin", "'SDA'").ErrorCode, Is.EqualTo("lplc_argument_invalid"));
      AssertOk(SetArg(path + "/args.SdaPin", "SdaPin", "0"));
      Assert.That(block.GetField("LPLC.args.SdaPin").AsInt(-1), Is.Zero);
      Assert.That(block.GetField("LPLC.args.SclPin").AsString(null), Is.EqualTo("SCL"));
      Assert.That(block.GetField("LPLC.pos.left").AsInt(-1), Is.EqualTo(5));
      Assert.That(Rpc(path, "set-arg", "{'name':'SdaPin','unset':true}").Ok, Is.False, "required setting cannot be cleared");
      Assert.That(block.GetField("LPLC.args.SdaPin").AsInt(-1), Is.Zero);
    }

    [Test]
    public void TwiSnapshotShowsHeaderAndThreeConfigurationRowsAndRefreshesAfterPinEdits() {
      DeviceType(LplcTwiTests.DeviceDescription);
      AssertOk(Rpc(Pa1, "add-twi:1"));
      string path = Pa1 + "/TWI1";
      Open();
      var row = Added(Vid(path));
      Assert.That(ConfigRow(row, "TwiNr").AsString("text", null), Is.EqualTo("1"));
      Assert.That(ConfigRow(row, "SdaPin").AsString("text", null), Is.EqualTo("<unset>"));
      Assert.That(ConfigRow(row, "SclPin").AsString("text", null), Is.EqualTo("<unset>"));
      Assert.That(row["constructorArgs"].Defined, Is.False);
      Assert.That((int)row["height"], Is.EqualTo(4));
      Assert.That((int)row["width"], Is.GreaterThanOrEqualTo(5));
      Assert.That(root.Get("/$YS/TYPES/" + LplcModel.TwiType, false).GetState().Field("icon").AsString(null),
        Does.StartWith("data:image/svg+xml;base64,"));
      Assert.That(Text(row, "icon"), Is.EqualTo("/api/icons/LPLC/TwiMaster.svg"));
      var icon = new System.Xml.XmlDocument();
      icon.LoadXml(System.Text.Encoding.UTF8.GetString(IconResource.TryGetIconContent(Text(row, "icon"))));
      Assert.That(icon.DocumentElement.LocalName, Is.EqualTo("svg"));
      Assert.That(_sent.Any(r => Text(r, "editor") == "LogramPin" && Text(r, "vid").StartsWith(Vid(path) + "/")), Is.False);
      _sent.Clear();
      AssertOk(SetArg(path, "SdaPin", "0"));
      Assert.That(ConfigRow(Added(Vid(path)), "SdaPin").AsString("text", null), Is.EqualTo("0"));
      AssertOk(SetArg(path, "SclPin", "4"));
      var compiled = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), X13.DevicePLC.EP_Target.EspNode,
        DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(compiled.Ok, Is.True, string.Join("\n", compiled.Problems));
      _graph.Dispose();
      _sent.Clear();
      Open();
      Assert.That(ConfigRow(Added(Vid(path)), "SclPin").AsString("text", null), Is.EqualTo("4"));
      Assert.That(ArgItem(Menu(path), "SclPin", "4").Checked, Is.True);
      _sent.Clear();
      _pa1.Get("TWI1", false).SetField("LPLC.args.SdaPin", JSC.JSValue.Null);
      Tick();
      Assert.That(ConfigRow(Added(Vid(path)), "SdaPin").AsString("text", null), Is.EqualTo("<unset>"));
    }

    [Test]
    public void TwiLayoutUsesTypeAndFitsPinAliasesAfterRenaming() {
      const string alias = "SDA_ALIAS_WITH_A_LONG_DEVICE_PIN_NAME";
      DeviceType(LplcTwiTests.DeviceDescription.Replace("SDA_ALIAS", alias));
      var block = Block("TWI1", LplcModel.TwiType, "{'pos':{'top':3,'left':5},'args':{'TwiNr':1,'SdaPin':'SDA','SclPin':0},'in':{},'out':{}}");
      var ordinary = Block("TWI9", "LPLC/Math/ADD", "{'in':{'A':0,'B':0},'out':{'Q':true}}");
      Tick();
      Open();
      var row = Added(Vid(block.path));
      Assert.That(ConfigRow(row, "SdaPin").AsString("text", null), Is.EqualTo("SDA"));
      Assert.That(ConfigRow(row, "SclPin").AsString("text", null), Is.EqualTo("0"));
      Assert.That((int)row["width"], Is.GreaterThanOrEqualTo(4));
      Assert.That(Added(Vid(ordinary.path))["twi"].Defined, Is.False);
      Assert.That((int)Added(Vid(ordinary.path))["height"], Is.EqualTo(2));
      _sent.Clear();
      block.SetField("LPLC.args.TwiNr", 0);
      Tick();
      Assert.That(ConfigRow(Added(Vid(block.path)), "TwiNr").AsString("text", null), Is.EqualTo("0"), "same-width value changes still refresh");
      block.Move(null, "BusController");
      block.SetField("LPLC.args.SdaPin", alias);
      Tick();
      row = Added(Vid(block.path));
      Assert.That(Text(row, "name"), Is.EqualTo("BusController"));
      Assert.That(ConfigRow(row, "TwiNr").AsString("text", null), Is.EqualTo("0"));
      Assert.That(ConfigRow(row, "SdaPin").AsString("text", null), Is.EqualTo(alias));
      Assert.That((int)row["height"], Is.EqualTo(4));
      Assert.That((int)row["width"] * 18, Is.GreaterThanOrEqualTo((3 + alias.Length) * 6 + 18));
      Assert.That(_sent.Any(r => Text(r, "editor") == "LogramPin" && Text(r, "vid").StartsWith(Vid(block.path) + "/")), Is.False);
    }

    [Test]
    public void ConstructorLabelsReserveRowsBeforeTickPinsAndRoutes() {
      var type = Json("{'LPLC':{'args':['Seed']}}");
      type = JsLib.SetField(type, "mi.LPLC.mi.in.mi.A", JSC.JSObject.CreateObject());
      type = JsLib.SetField(type, "mi.LPLC.mi.out.mi.Q", JSC.JSObject.CreateObject());
      root.Get("/$YS/TYPES/LPLC/Configured", true).SetState(type);
      var block = Block("Configured", "LPLC/Configured", "{'pos':{'top':3,'left':10},'args':{'Seed':7},'in':{'A':'A1.Q'},'out':{'Q':true}}");
      Tick();
      Open();
      var row = Added(Vid(block.path));
      var pin = Added(Vid(block.path + "/in.A"));
      Assert.That((int)row["height"], Is.EqualTo(3));
      Assert.That((int)pin["pinIndex"], Is.EqualTo(2));
      Assert.That(Updated(Vid(block.path + "/in.A"))["sourcePath"]["0"]["y"].AsInt(-1), Is.EqualTo(5));
    }

    [Test]
    public void TwiUsesProgramTypeAndRejectsStaleDeviceChoices() {
      DeviceType("{'groups':{'TWI':{'modes':{'TWI1_SDA':{'v':70},'TWI1_SCL':{'v':90}},'pins':[0,4]}}}");
      _pa1.Move(null, "Logic");
      var pa0 = _dev.Get("pa0", true);
      pa0.SetField("type", "Ext/DevicePLC");
      Tick();
      AssertOk(Rpc(_pa1.path, "add-twi:0"));
      Assert.That(Rpc(pa0.path, "add-twi:0").Ok, Is.False);
      string path = _pa1.path + "/TWI0";
      AssertOk(SetArg(path, "SdaPin", "0"));
      DeviceType(DevicePlcDeviceTests.Esp32);
      Assert.That(SetArg(path, "SclPin", "4").ErrorCode, Is.EqualTo("lplc_argument_invalid"));
      Assert.That(_pa1.Get("TWI0", false).GetField("LPLC.args.SdaPin").AsInt(-1), Is.Zero);
    }

    [TestCase(0)]
    [TestCase(1)]
    public void TwiMenuMatchesCompilerWithDescriptorGaps(int first) {
      string description = "{'groups':{'TWI':{'modes':{'TWI" + first + "_SDA':{'v':16},'TWI" + first
        + "_SCL':{'v':19},'TWI" + (first + 2) + "_SDA':{'v':38},'TWI" + (first + 2) + "_SCL':{'v':42}},'pins':[0,4]}}}";
      DeviceType(description);
      var menu = Flatten(Menu(Pa1)).Single(i => i.Text == "TWI");
      Assert.That(menu.Children.Select(i => i.Text), Is.EqualTo(new[] { "TWI0", "TWI2", "SCAN_BUS" }));
      Assert.That(menu.Children.Select(i => i.Cmd), Is.EqualTo(new[] { "add-twi:0", "add-twi:2", "add-block:LPLC/TWI/SCAN_BUS" }));
      Assert.That(LplcModel.TwiBuses(_pa1).Select(b => b.Number),
        Is.EqualTo(DevicePlcDeviceTests.Device(description).TwiBuses().Select(b => b.Number)));
      AssertOk(Rpc(Pa1, "add-twi:0"));
      Assert.That(Rpc(Pa1, "add-twi:1").ErrorCode, Is.EqualTo("lplc_twi_not_found"));
      AssertOk(Rpc(Pa1, "add-twi:2"));
    }

    // === Помощники ===

    /// <summary>Тип устройства с описанием пинов и режимов - там, где его ищет MQTT_SN (LP_Program.LoadDevice).</summary>
    private void DeviceType(string plc) {
      root.Get("/$YS/TYPES/MQTT-SN/T1", true).SetState(Json("{'PLC':" + plc + "}"));
      _dev.SetField("type", "MQTT-SN/T1");
      Tick();
    }
    private MenuItemDto PinMenu() {
      return Flatten(Menu(Pa1)).Single(i => i.Text == "Pin");
    }
    [Test]
    public void ListDriverUsesTypedRowMenusAndTypeConstraintsOnCommitAndCompile() {
      DeviceType(LplcTwiTests.DeviceDescription);
      var type = root.Get("/$YS/TYPES/LPLC/SensorFixture", true);
      var state = Json("{'LPLC':{'args':['Address'],'in':[],'src':'class Sensor { constructor(Address) {} Tick() {} }'},'manifest':{'type':'LPLC/SensorFixture','LPLC':{'in':{},'out':{}}}}");
      state = JsLib.SetField(state, "mi.LPLC.mi.args.mi.Address", Json("{'cdr':1,'format':'0x{0:X2}','default':118,'choices':{'provider':'list','values':[118,119]}}"));
      type.SetState(state);
      Tick();
      AssertOk(Rpc(Pa1, "add-block:LPLC/SensorFixture", "{'name':'Sensor'}"));
      string path = Pa1 + "/Sensor";
      var block = _pa1.Get("Sensor", false);
      Assert.That(block.GetField("LPLC.args.Address").AsInt(-1), Is.EqualTo(118));
      Open();
      var row = ConfigRow(Added(Vid(path)), "Address");
      Assert.That(row.AsString("text", null), Is.EqualTo("0x76"));
      Assert.That(row.Field("value").IsNumber, Is.True);
      var choices = Menu(path + "/args.Address");
      Assert.That(choices.Select(c => c.Text), Is.EqualTo(new[] { "0x76", "0x77" }));
      Assert.That(choices[0].Checked, Is.True);
      Assert.That(choices[1].Args.Field("value").IsNumber, Is.True);
      AssertOk(SetArg(path + "/args.Address", "Address", "119"));
      Assert.That(SetArg(path + "/args.Address", "Other", "118").Ok, Is.False);
      Assert.That(SetArg(path, "Address", "'118'").Ok, Is.False);
      block.SetField("mi.LPLC.mi.args.mi.Address.choices", Json("{'provider':'list','values':[120]}"));
      Tick();
      Assert.That(SetArg(path, "Address", "120").Ok, Is.False, "instance overlay cannot weaken the type");
      block.SetField("LPLC.args.Address", 120);
      Tick();
      Assert.That(ConfigRow(Added(Vid(path)), "Address").Field("error").Defined, Is.True);
      var result = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), X13.DevicePLC.EP_Target.EspNode, DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(result.Ok, Is.False);
      Assert.That(result.Problems.Any(p => p.Element != null && p.Element.Name == "Sensor" && p.Message.Contains("Address")), Is.True);
    }

    [Test]
    public void MaskAndProviderChangesRefreshChoicesWithoutChangingSavedValues() {
      DeviceType(LplcTwiTests.DeviceDescription);
      AssertOk(Rpc(Pa1, "add-twi:0"));
      AssertOk(Rpc(Pa1, "add-block:LPLC/TWI/SCAN_BUS", "{'name':'Scan'}"));
      Open();
      string path = Pa1 + "/Scan";
      Assert.That(Menu(path + "/args.TwiNr").Count, Is.EqualTo(1));
      _dev.SetField(X13.Plc.PlcCapabilities.PinsField, new ByteArray(new byte[5]));
      Tick();
      Assert.That(Menu(path + "/args.TwiNr"), Is.Empty);
      Assert.That(ConfigRow(Added(Vid(path)), "TwiNr").Field("error").Defined, Is.True);
      Assert.That(_pa1.Get("Scan", false).GetField("LPLC.args.TwiNr").AsInt(-1), Is.Zero);
      Assert.That(SetArg(Pa1 + "/TWI0", "SdaPin", "21").Ok, Is.False);
      _dev.SetField(X13.Plc.PlcCapabilities.PinsField, JSC.JSValue.Null);
      Tick();
      Assert.That(Menu(path + "/args.TwiNr").Count, Is.EqualTo(1));
      _pa1.Get("TWI0", false).Remove();
      Tick();
      Assert.That(Menu(path + "/args.TwiNr"), Is.Empty);
      Assert.That(ConfigRow(Added(Vid(path)), "TwiNr").Field("error").Defined, Is.True);
    }

    [Test]
    public void TwoNodesShareTypeButNotHighPinAvailability() {
      DeviceType("{'names':{'HIGH':32},'groups':{'T':{'modes':{'TWI0_SDA':{'v':16},'TWI0_SCL':{'v':19}},'pins':['HIGH',39]}}}");
      var other = root.Get("/dev/other-node", true);
      other.SetField("type", _dev.GetField("type"));
      var program = other.Get("CustomProgram", true);
      program.SetField("type", "Ext/LogramPLC");
      _dev.SetField(X13.Plc.PlcCapabilities.PinsField, new ByteArray(new byte[] { 0, 0, 0, 0, 129 }));
      other.SetField(X13.Plc.PlcCapabilities.PinsField, new ByteArray(new byte[5]));
      Tick();
      Assert.That(LplcModel.TwiBuses(_pa1).Single().Sda.Pins.Select(p => p.Number), Is.EqualTo(new[] { 32, 39 }));
      Assert.That(LplcModel.TwiBuses(program), Is.Empty);
      var plc = root.Get("/$YS/TYPES/" + _dev.GetField("type").AsString(null), false).GetState().Field("PLC");
      var first = X13.DevicePLC.EP_Device.Parse(plc, _dev.GetField(X13.Plc.PlcCapabilities.PinsField));
      var second = X13.DevicePLC.EP_Device.Parse(plc, other.GetField(X13.Plc.PlcCapabilities.PinsField));
      Assert.That(first.Allows(32, first.ModeOf(16)), Is.True);
      Assert.That(second.Allows(32, second.ModeOf(16)), Is.False);
      Assert.That(first.Allows(39, first.ModeOf(19)), Is.True);
    }

    private ViewOpResult SetArg(string path, string name, string value) {
      return Rpc(path, "set-arg", "{'name':'" + name + "','value':" + value + "}");
    }
    private static MenuItemDto ArgItem(List<MenuItemDto> menu, string name, string text) {
      return Flatten(menu).Single(i => i.Cmd == "set-arg" && i.Args.AsString("name", null) == name && i.Text == text);
    }
    private static JSC.JSValue ConfigRow(JSC.JSValue row, string name) {
      return row.Field("constructorRows").Single(a => a.Value.AsString("name", null) == name).Value;
    }

    private void Open() {
      _graph = new LplcGraphController(_sent.Add, _pa1);
      _graph.Open();
    }
    private ViewOpResult Rpc(string path, string cmd, string args = null) {
      ViewOpResult r = _provider.ExecuteRpc(Vid(path), cmd, args == null ? null : Json(args));
      Tick();
      return r;
    }
    private List<MenuItemDto> Menu(string path) {
      List<MenuItemDto> items;
      AssertOk(_provider.BuildMenu(Vid(path), out items));
      return items;
    }
    private static MenuItemDto Item(List<MenuItemDto> menu, string cmd) {
      return Flatten(menu).Single(i => i.Cmd == cmd);
    }
    private static IEnumerable<MenuItemDto> Flatten(IEnumerable<MenuItemDto> items) {
      foreach(MenuItemDto i in items) {
        yield return i;
        if(i.Children == null) continue;
        foreach(MenuItemDto c in Flatten(i.Children)) yield return c;
      }
    }
    private static void AssertOk(ViewOpResult r) {
      Assert.That(r.Ok, Is.True, r.ErrorCode + ": " + r.ErrorMessage);
    }
    private static string[] Keys(JSC.JSValue o) {
      return o.IsObject() ? o.Select(kv => kv.Key).ToArray() : new string[0];
    }
    private static string Vid(string path) {
      return LplcModel.ViewName + "#" + path;
    }
    private JSC.JSObject Frame(string type, string vid) {
      return _sent.LastOrDefault(f => Text(f, "type") == type && Text(f, "vid") == vid);
    }
    private JSC.JSObject Added(string vid) {
      return Frame(ViewMessageTypes.EvntAdd, vid);
    }
    private JSC.JSObject Updated(string vid) {
      return Frame(ViewMessageTypes.EvntUpd, vid);
    }
    private static string Text(JSC.JSObject frame, string key) {
      if(frame == null) return null;
      JSC.JSValue v = frame[key];
      return v == null ? null : v.Value as string;
    }
    private static bool Flag(JSC.JSObject frame, string key) {
      JSC.JSValue v = frame == null ? null : frame[key];
      return v != null && v.Defined && (bool)v;
    }
  }
}
