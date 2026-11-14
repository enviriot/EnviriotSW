///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Periphery;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  /// <summary>Диаграмма LPLC в дереве: блоки под pa1, переменные в устройстве, скрытые результаты.</summary>
  /// <remarks>Своё дерево на каждый тест (RepoTestBase): кнопка - фильтр - ШИМ, уставка K, приёмник Out и
  /// наблюдатель Filter_M. Описание устройства - подмножество E1S10 из DevicePlcDeviceTests.</remarks>
  [TestFixture]
  public class LplcTreeTests : RepoTestBase {
    private Topic _dev, _pa1, _filter;

    private static JSC.JSValue Json(string text) {
      return JsLib.ParseJson(text.Replace('\'', '"'));
    }
    private Topic Block(string name, string type, string lplc, string src = null) {
      var t = _pa1.Get(name, true);
      t.SetField("type", type);
      t.SetField(LP_Tree.FLD, Json(lplc));
      if(src != null) {
        t.SetState(Str(src));
      }
      return t;
    }
    private Topic Var(string name, string tag, string lplc = null) {
      var t = _dev.Get(name, true);
      t.SetField("MQTT-SN.tag", tag);
      if(lplc != null) {
        t.SetField(LP_Tree.FLD, Json(lplc));
      }
      return t;
    }
    private static void ImportTypes() {
      using (var rs = typeof(LP_Tree).Assembly.GetManifestResourceStream(LP_Tree.TYPES_RESOURCE)) {
        Assert.That(rs, Is.Not.Null, "ресурс");
        using (var reader = new System.IO.StreamReader(rs)) {
          Xst.Import(reader, null);
        }
      }
    }
    /// <summary>Топик из пункта меню Add - как его создаёт IDE (TopicRpcDispatcher.ExecuteAdd).</summary>
    private static Topic Add(Topic home, string name, JSC.JSValue item) {
      var t = Topic.Declare(home, name);
      Topic.Fill(t, item["default"].Defined ? JsLib.Clone(item["default"]) : JSC.JSValue.Null, JsLib.Clone(item["manifest"]), null);
      return t;
    }
    private static bool Saved(Topic t) {
      return t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB);
    }
    private LP_DiagramCompiler Compile() {
      var r = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), EP_Target.EspNode, DevicePlcDeviceTests.Device(DevicePlcDeviceTests.Esp32));
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems) + "\n" + r.Source);
      return r;
    }

    [SetUp]
    public void Tree() {
      _dev = root.Get("/dev/node1", true);
      _pa1 = _dev.Get("pa1", true);
      Block("Button", LP_Tree.PIN, "{'pos':{'top':0,'left':0},'args':{'pin':0,'mode':'IN','option':20},'in':{},'run':{}}");
      _filter = Block("Filter", LP_Tree.FUNC, "{'pos':{'top':0,'left':3},'in':{'A':'Button.value','B':'K','C':0.5},'run':{}}",
        "this.M = this.M*0.75 + B*0.25;\nreturn this.M/(A+1);");
      Block("Led", LP_Tree.PIN, "{'pos':{'top':0,'left':6},'args':{'pin':4,'mode':'PWM','option':1000},'in':{'x':'Filter.Q'},'run':{}}");
      Var("K", "Mf");                                   // уставка: адрес ещё не назначен
      Var("Out", "Md", "{'in':{'x':'Filter.Q'}}");     // приёмник
      Var("Filter_M", "Mf7");                           // наблюдатель за полем Filter.M
      Tick();
    }

    [Test]
    public void Read_GivesBlocksVariablesAndLinks() {
      var d = LP_Tree.Read(_pa1);
      var links = d.Links.Select(z => (z.From ?? "=" + z.Const) + ">" + z.To).ToArray();
      using (Assert.EnterMultipleScope()) {
        Assert.That(d.Blocks.Select(z => z.Name), Is.EqualTo(new[] { "Button", "Filter", "Led" }), "по имени");
        Assert.That(d.Blocks.Select(z => z.Tag), Is.All.InstanceOf<Topic>());
        Assert.That(d.Variables.Select(z => z.Name + ":" + z.Type), Is.EqualTo(new[] { "Filter_M:Float", "K:Float", "Out:Int32" }));
        Assert.That(links, Is.EquivalentTo(new[] { "Button.value>Filter.A", "K>Filter.B", "=0.5>Filter.C", "Filter.Q>Led.x", "Filter.Q>Out" }));
      }
    }

    [Test]
    public void Attach_PutsTheResultIntoTheBlock() {
      var r = Compile();
      string fp = LP_Tree.Fingerprint(r.Hex);
      LP_Tree.Attach(r, fp);
      var info = LP_Tree.Info(_filter);
      var observer = r.Elements.Single(z => z.Name == "Filter_M");
      using (Assert.EnterMultipleScope()) {
        Assert.That(info, Is.Not.Null);
        Assert.That(info.Program, Is.EqualTo(fp));
        Assert.That(info.Element.Name, Is.EqualTo("Filter"));
        Assert.That(info.Element.Merkers.Keys, Is.SupersetOf(new[] { "Q", "M" }));
        Assert.That(observer.Merkers["value"], Is.EqualTo(info.Element.Merkers["M"]), "наблюдатель - меркер поля");
      }
    }

    /// <summary>Запись в LPLC.pos переносит контейнер по ссылке; перезапись самого LPLC.run его теряет.</summary>
    [Test]
    public void Result_SurvivesASiblingWriteOnly() {
      var r = Compile();
      LP_Tree.Attach(r, LP_Tree.Fingerprint(r.Hex));
      _filter.SetField("LPLC.pos.top", Num(5));
      _filter.SetField("LPLC.in.C", Num(0.25));
      Tick();
      Assert.That(LP_Tree.Info(_filter), Is.Not.Null, "после записи в pos и in");
      _filter.SetField("LPLC.run", JSC.JSObject.CreateObject());
      Tick();
      Assert.That(LP_Tree.Info(_filter), Is.Null, "после перезаписи run");
    }

    [Test]
    public void Result_IsNotSerialized() {
      var r = Compile();
      LP_Tree.Attach(r, LP_Tree.Fingerprint(r.Hex));
      string json = JsLib.Stringify(_filter.GetField(LP_Tree.FLD));
      using (Assert.EnterMultipleScope()) {
        Assert.That(json, Does.Contain("\"run\":{}"));
        Assert.That(json, Does.Not.Contain("$plc"));
      }
    }

    [Test]
    public void Attach_CreatesAMissingContainer() {
      _filter.SetField("LPLC.run", JSC.JSValue.Null);
      Tick();
      Assert.That(LP_Tree.Info(_filter), Is.Null);
      var r = Compile();
      LP_Tree.Attach(r, LP_Tree.Fingerprint(r.Hex));
      Tick();
      Assert.That(LP_Tree.Info(_filter), Is.Not.Null);
    }

    /// <summary>PC из STOP_RESP - блок и строка src, если результат той же программы, что на узле.</summary>
    [Test]
    public void Locate_FindsTheBlockOfTheLoadedProgram() {
      var r = Compile();
      string fp = LP_Tree.Fingerprint(r.Hex);
      LP_Tree.Attach(r, fp);
      var div = DevicePlcFloatTests.Disassemble(r.Hex).First(z => z.Code == EP_InstCode.FDIV);
      int line;
      bool known;
      var at = LP_Tree.Locate(_pa1, div.Addr, fp, out line, out known);
      using (Assert.EnterMultipleScope()) {
        Assert.That(known, Is.True);
        Assert.That(at, Is.SameAs(_filter));
        Assert.That(line, Is.EqualTo(2));
      }
      LP_Tree.Locate(_pa1, div.Addr, "other program", out line, out known);
      Assert.That(known, Is.False, "результат другой программы - не основание");
    }

    /// <summary>Встроенный LPLC.xst разбирается и даёт типы блоков с шаблоном манифеста для меню Add.</summary>
    [Test]
    public void Types_AreImportedFromTheResource() {
      ImportTypes();
      Tick();
      var func = root.Get("/$YS/TYPES/LPLC/Func", false);
      var pin = root.Get("/$YS/TYPES/LPLC/Pin", false);
      using (Assert.EnterMultipleScope()) {
        Assert.That(func, Is.Not.Null);
        Assert.That(func.GetState().AsString("editor", null), Is.EqualTo("JS"));
        Assert.That(func.GetState().AsString("manifest.type", null), Is.EqualTo(LP_Tree.FUNC));
        Assert.That(func.GetState().Field("manifest.LPLC.run").IsObject(), Is.True, "контейнер в шаблоне");
        Assert.That(func.GetState().Field("mi.LPLC.mi.in.mi.H").IsObject(), Is.True, "схема входов для Add");
        Assert.That(pin.GetState().AsString("manifest.LPLC.args.mode", null), Is.EqualTo("IN"));
        Assert.That(root.Get("/$YS/TYPES/LPLC/Math/ADD", false).GetState().AsString("LPLC.op", null), Is.EqualTo("+"));
      }
    }

    /// <summary>ADD берёт операцию и входы A..H из своего типа; в программу - только подключённые входы,
    /// результат - в его LPLC.run, как у других блоков.</summary>
    [Test]
    public void ExprBlock_TakesTheOperationFromItsType() {
      ImportTypes();
      var sum = Block("Sum", "LPLC/Math/ADD", "{'pos':{'top':0,'left':9},'in':{'A':'Button.value','D':'K'},'run':{}}");
      Tick();
      var b = LP_Tree.Read(_pa1).Blocks.Single(z => z.Name == "Sum");
      var r = Compile();
      LP_Tree.Attach(r, LP_Tree.Fingerprint(r.Hex));
      using (Assert.EnterMultipleScope()) {
        Assert.That(b.Kind, Is.EqualTo(LP_Diagram.BlockKind.Expr));
        Assert.That(b.Op, Is.EqualTo("+"));
        Assert.That(b.Inputs, Is.EqualTo(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }));
        Assert.That(r.Source, Does.Contain("_x.e1 = ((_p1.value) + (_v1));"));
        Assert.That(r.Variables["Sum.Q"], Does.StartWith("Mf"), "K - Float");
        Assert.That(LP_Tree.Info(sum), Is.Not.Null);
      }
    }

    /// <summary>Каждый блок каталога LPLC.xst компилируется и даёт свою строку цикла; Button.value - целое, K - float.</summary>
    /// <remarks>Div берёт входы целыми и делит нацело даже от float K, fDiv - наоборот; lAND и lOR дают 1/0.</remarks>
    [TestCase("Math/ADD", "_x.e1 = ((_p1.value) + (_v1));", "Mf", null)]
    [TestCase("Math/Mul", "_x.e1 = ((_p1.value) * (_v1));", "Mf", null)]
    [TestCase("Math/SUB", "_x.e1 = ((_p1.value) - (_v1));", "Mf", "FSUB")]
    [TestCase("Math/Div", "_x.e1 = (Int32((_p1.value)) / Int32((_v1)));", "Md", "DIV")]
    [TestCase("Math/fDiv", "_x.e1 = (Float((_p1.value)) / Float((_v1)));", "Mf", "FDIV")]
    [TestCase("Binary/bAND", "_x.e1 = ((_p1.value) & (_v1));", "Md", "AND")]
    [TestCase("Binary/bOR", "_x.e1 = ((_p1.value) | (_v1));", "Md", "OR")]
    [TestCase("Binary/bXOR", "_x.e1 = ((_p1.value) ^ (_v1));", "Md", "XOR")]
    [TestCase("Binary/bNOT", "_x.e1 = (~(_v1));", "Md", "NOT")]
    [TestCase("Logic/lAND", "_x.e1 = (!!((_p1.value) && (_v1)));", "Md", "CZE")]
    [TestCase("Logic/lOR", "_x.e1 = (!!((_p1.value) || (_v1)));", "Md", "CZE")]
    [TestCase("Logic/lNOT", "_x.e1 = (!(_v1));", "Md", "FCZE")]
    [TestCase("Logic/COMP_LT", "_x.e1 = ((_p1.value) < (_v1));", "Md", "FCLT")]
    [TestCase("Logic/COMP_LE", "_x.e1 = ((_p1.value) <= (_v1));", "Md", "FCLE")]
    [TestCase("Logic/COMP_EQ", "_x.e1 = ((_p1.value) == (_v1));", "Md", "FCEQ")]
    [TestCase("Logic/COMP_NE", "_x.e1 = ((_p1.value) != (_v1));", "Md", "FCNE")]
    [TestCase("Logic/COMP_GT", "_x.e1 = ((_p1.value) > (_v1));", "Md", "FCGT")]
    [TestCase("Logic/COMP_GE", "_x.e1 = ((_p1.value) >= (_v1));", "Md", "FCGE")]
    public void CatalogBlock_Compiles(string type, string line, string merker, string opName) {
      ImportTypes();
      bool unary = type.EndsWith("NOT", System.StringComparison.Ordinal);
      Block("X", "LPLC/" + type, unary ? "{'in':{'A':'K'},'run':{}}" : "{'in':{'A':'Button.value','B':'K'},'run':{}}");
      Tick();
      var r = Compile();
      var x = r.Elements.Single(z => z.Name == "X");
      var ops = DevicePlcFloatTests.Disassemble(r.Hex).Where(o => x.Code.Any(c => c.From <= o.Addr && o.Addr < c.To)).Select(o => o.Code).ToList();
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("\n" + line + "\n"));
        Assert.That(r.Variables["X.Q"], Does.StartWith(merker));
        if(opName != null) {
          Assert.That(ops, Does.Contain((EP_InstCode)System.Enum.Parse(typeof(EP_InstCode), opName)), "команда блока");
        }
        if(type == "Math/Div") {
          Assert.That(ops, Does.Not.Contain(EP_InstCode.FDIV), "деление нацело");
        }
      }
    }

    /// <summary>Меню Add у pa1 - от его типа: папки каталога - подменю, Func и Pin - сразу в Add.</summary>
    [Test]
    public void AddMenu_GroupsTheCatalogByFolder() {
      ImportTypes();
      _pa1.SetField("type", LP_Schema.PLC_TYPE);
      Tick();
      var add = X13.WebUI.MenuBuilder.Build(_pa1).Single(z => z.Text == "Add");
      System.Func<string, string[]> group = g => add.Children.Single(z => z.Text == g).Children.Select(z => z.Text).ToArray();
      using (Assert.EnterMultipleScope()) {
        Assert.That(group("Math"), Is.EquivalentTo(new[] { "ADD", "Div", "fDiv", "Mul", "SUB" }));
        Assert.That(group("Binary"), Is.EquivalentTo(new[] { "bAND", "bNOT", "bOR", "bXOR", "Trigger" }));
        Assert.That(group("Logic"), Is.EquivalentTo(new[] { "lAND", "lNOT", "lOR", "COMP_LT", "COMP_LE", "COMP_EQ", "COMP_NE", "COMP_GT", "COMP_GE" }));
        Assert.That(group("TWI"), Is.EqualTo(new[] { "SCAN_BUS" }));
        Assert.That(add.Children.Where(z => z.Cmd != null).Select(z => z.Text), Is.EquivalentTo(new[] { "Func", "Pin", "SerialPort", "TwiMaster" }));
        Assert.That(add.Children.Single(z => z.Text == "Math").Children.Single(z => z.Text == "ADD").Cmd, Is.EqualTo("add:ADD"));
      }
    }

    [TestCase("COMP_LT", true, false, false)]
    [TestCase("COMP_LE", true, true, false)]
    [TestCase("COMP_EQ", false, true, false)]
    [TestCase("COMP_NE", true, false, true)]
    [TestCase("COMP_GT", false, false, true)]
    [TestCase("COMP_GE", false, true, true)]
    public void Comparisons_PreserveOrderEqualityAndFractions(string type, bool less, bool equal, bool greater) {
      ImportTypes();
      Tick();
      string expr = root.Get("/$YS/TYPES/LPLC/Logic/" + type, false).GetState().AsString("LPLC.expr", null);
      var d = new LP_Diagram().AddExpr("Compare", expr, new[] { "A", "B" }).AddVar("X", LP_Diagram.VarType.Float)
        .AddVar("Y", LP_Diagram.VarType.Float).Connect("X", "Compare.A").Connect("Y", "Compare.B");
      var r = LP_DiagramCompiler.Compile(d, EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems));
      double[] a = { -2.5, 0.25, 3.75 }, b = { -1.5, 0.25, 2.5 };
      bool[] expected = { less, equal, greater };
      for(int i = 0; i < a.Length; i++) {
        string source = r.Source.Replace("new Float()", "0");
        string tick = source.Substring(source.IndexOf("_x.e1 =", System.StringComparison.Ordinal));
        string values = "\n_v1 = " + a[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)
          + "; _v2 = " + b[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";\n";
        Assert.That((bool)new JSC.Context().Eval(source + values + tick + "\n_x.e1;"), Is.EqualTo(expected[i]));
      }
    }

    [Test]
    public void Trigger_IsAClassWithConstructorAndPersistentIndependentState() {
      ImportTypes();
      Tick();
      var program = root.Get("/dev/triggers/Logic", true);
      var template = root.Get("/$YS/TYPES/LPLC/Binary/Trigger", false).GetState();
      var first = Add(program, "T1", template);
      Add(program, "T2", template);
      Tick();
      var d = LP_Tree.Read(program);
      Assert.That(d.Blocks[0].Kind, Is.EqualTo(LP_Diagram.BlockKind.Block));
      Assert.That(d.Blocks[0].Inputs, Is.EqualTo(new[] { "S", "D", "C", "R" }));
      var r = LP_DiagramCompiler.Compile(d, EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems) + "\n" + r.Source);
      Assert.That(r.Source, Does.Contain("Tick(S, D, C, R)"));
      Assert.That(r.Source, Does.Not.Contain(".Q = _b1.Tick("));
      Assert.That(r.Source, Does.Not.Contain("return this.Q"));
      Assert.That(r.Variables.Keys, Is.SupersetOf(new[] { "T1.Q", "T1.NQ", "T2.Q", "T2.NQ" }));
      Assert.That(r.Variables["T1.Q"], Is.Not.EqualTo(r.Variables["T2.Q"]));
      string init = r.Source.Substring(0, r.Source.IndexOf("_b1.Tick(", System.StringComparison.Ordinal));
      Assert.That(new JSC.Context().Eval(init + "\nJSON.stringify([_b1.Q, _b1.NQ, _b1.CO, _b2.Q, _b2.NQ, _b2.CO]);").AsString(null),
        Is.EqualTo("[false,true,false,false,true,false]"));
      string sequence = @"
var states = [];
function sample(s, d, c, r) {
  _b1.Tick(s, d, c, r);
  states.push([_b1.Q ? 1 : 0, _b1.NQ ? 1 : 0]);
}
sample(0, 0, 0, 0);
sample(1, 0, 0, 0);
sample(0, 0, 0, 0);
sample(1, 1, 1, 1);
sample(0, 1, 1, 0);
sample(0, 1, 0, 0);
sample(0, 1, 1, 0);
sample(0, 0, 1, 0);
sample(0, 0, 0, 0);
sample(0, 0, 1, 0);
sample(1, 0, 0, 0);
sample(0, 0, 0, 1);
sample(0, 1, 1, 0);
states.push([_b2.Q ? 1 : 0, _b2.NQ ? 1 : 0]);
JSON.stringify(states);";
      Assert.That(new JSC.Context().Eval(r.Source + sequence).AsString(null),
        Is.EqualTo("[[0,1],[1,0],[1,0],[0,1],[0,1],[0,1],[1,0],[1,0],[1,0],[0,1],[1,0],[0,1],[1,0],[0,1]]"));
      LP_Tree.Attach(r, LP_Tree.Fingerprint(r.Hex));
      Tick();
      Assert.That(LP_Tree.Info(first), Is.Not.Null);
    }

    [Test]
    public void CatalogBlockSourceComesFromTheTypeNotInstanceState() {
      ImportTypes();
      var block = Block("T1", "LPLC/Binary/Trigger", "{'in':{'S':1,'D':0,'C':0,'R':0}}", "return 123;");
      Tick();
      Assert.That(LP_Tree.Read(_pa1).Blocks.Single(b => b.Tag == block).Src, Does.Contain("this.NQ"));
      var type = root.Get("/$YS/TYPES/LPLC/Binary/Trigger", false);
      var state = JsLib.Clone(type.GetState());
      const string src = "class Trigger { constructor() { this.Q = false; } Tick(S, D, C, R) { this.Q = !!S; } }";
      state["LPLC"]["src"] = src;
      type.SetState(state);
      Tick();
      var def = LP_Tree.Read(_pa1).Blocks.Single(b => b.Tag == block);
      Assert.That(def.Src, Is.EqualTo(src));
      Assert.That(def.Kind, Is.EqualTo(LP_Diagram.BlockKind.Block));
    }

    [TestCase("S", "S")]
    [TestCase("S", "bad-name")]
    [TestCase("S", null)]
    public void CatalogBlockRejectsInvalidInputNames(string first, string second) {
      var d = new LP_Diagram().AddBlock("F", "class B { constructor() {} Tick(S) {} }", new[] { first, second });
      var r = LP_DiagramCompiler.Compile(d, EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.False);
      Assert.That(r.Problems.Any(p => p.Message.StartsWith("block inputs must be distinct names:")), Is.True);
    }

    [Test]
    public void BlockTickDoesNotAssignItsReturnValueOrCreateQ() {
      const string src = "/* class OUT */\nclass /* name */ OUT {\n  constructor() { this.State = 7; }\n  Tick(Step) { this.State += Step; return 999; }\n}";
      var d = new LP_Diagram().AddFunc("F", "return A;")
        .AddBlock("B", src, new[] { "Step" })
        .AddBlock("Other", src, new[] { "Step" })
        .AddVar("B_State", LP_Diagram.VarType.Int32)
        .SetConst("B.Step", "2")
        .Connect("B.State", "F.A");
      var r = LP_DiagramCompiler.Compile(d, EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems) + "\n" + r.Source);
      Assert.That(r.Source, Does.Contain("/* class OUT */\nclass /* name */ _c2"));
      Assert.That(r.Source, Does.Contain("_b2.Tick(2);\n_b1.Q = _b1.Tick(_b2.State);"));
      Assert.That(r.Variables.ContainsKey("B.Q"), Is.False);
      Assert.That(r.Variables["B.State"], Is.EqualTo(r.Variables["B_State"]));
      Assert.That(r.Variables["B.State"], Is.Not.EqualTo(r.Variables["Other.State"]));
      Assert.That(new JSC.Context().Eval(r.Source + "\n_b2.Tick(2); _b2.Tick(2); JSON.stringify([_b2.State, _b3.State, _b1.Q]);").AsString(null),
        Is.EqualTo("[13,7,9]"));
    }

    [TestCase("return S;", "Invalid use of return")]
    [TestCase("class B { Tick(S) {} }", "explicit constructor")]
    [TestCase("/* constructor() {} */ class B { Tick(S) {} }", "explicit constructor")]
    [TestCase("class B { Tick(S) { let text = 'constructor() {}'; } }", "explicit constructor")]
    [TestCase("class B { Tick(S) { class Nested { constructor() {} } } }", "explicit constructor")]
    [TestCase("class B { constructor(S) {} Tick(S) {} }", "constructor parameters must match")]
    [TestCase("class B { constructor() {} }", "instance Tick")]
    [TestCase("class B { constructor() {} static Tick(S) {} }", "instance Tick")]
    [TestCase("class B { constructor() {} Tick(A) {} }", "parameters must match")]
    [TestCase("class B { constructor() {} Tick(S) {} } var extra = 1;", "single named class")]
    public void BlockRejectsInvalidClassContracts(string src, string error) {
      var r = LP_DiagramCompiler.Compile(new LP_Diagram().AddBlock("B", src, new[] { "S" }, "block"), EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.False);
      Assert.That(r.Problems.Any(p => p.Element.Tag.Equals("block") && p.Message.Contains(error)), Is.True, string.Join("\n", r.Problems));
    }

    [TestCase("class B { constructor() {} Tick() {} }")]
    [TestCase("class B { Tick() {} constructor() {} }")]
    [TestCase("/* leading trivia */ class B { Tick() {} constructor /* gap */ () {} }")]
    public void BlockAcceptsExplicitEmptyConstructor(string src) {
      var r = LP_DiagramCompiler.Compile(new LP_Diagram().AddBlock("B", src, new string[0], "block"), EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems));
    }

    [TestCase("class B {\n constructor() {\n  this.Q = nope;\n }\n Tick() {}\n}", 3)]
    [TestCase("class B {\n constructor() { this.Q = 0; }\n Tick() {\n  this.Q = nope;\n }\n}", 4)]
    [TestCase("class B {\n constructor() {}\n Tick() {\n  if(true { }\n }\n}", 4)]
    public void BlockErrorsPointAtClassSourceLines(string src, int line) {
      var r = LP_DiagramCompiler.Compile(new LP_Diagram().AddBlock("B", src, new string[0], "block"), EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.False);
      Assert.That(r.Problems.Any(p => p.Element != null && p.Element.Tag.Equals("block") && p.Line == line), Is.True, string.Join("\n", r.Problems));
    }

    [Test]
    public void BlockParserFailureIsADiagnostic() {
      var d = new LP_Diagram().AddBlock("B", "class B { constructor() {} Tick() { if( { } } }", new string[0], "block");
      var r = LP_DiagramCompiler.Compile(d, EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.False);
      Assert.That(r.Problems.Single().Element.Tag, Is.EqualTo("block"));
      Assert.That(r.Problems.Single().Message, Does.StartWith("invalid Block src:"));
    }

    [Test]
    public void BlockConstructorAndTickHaveSourceCodeMappings() {
      const string src = "class B {\n constructor() {\n  this.Q = 7;\n }\n Tick(S) {\n  if(S) return;\n  this.Q = this.Q / 2;\n }\n}";
      var r = LP_DiagramCompiler.Compile(new LP_Diagram().AddBlock("B", src, new[] { "S" }, "block"), EP_Target.EspNode, null);
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems));
      Assert.That(r.Source, Does.Contain("if(S) return;"));
      var element = r.Elements.Single();
      Assert.That(element.Code.Any(c => c.Line == 3), Is.True, "constructor");
      var div = DevicePlcFloatTests.Disassemble(r.Hex).Single(op => op.Code == EP_InstCode.DIV);
      int line;
      Assert.That(r.ElementAt(div.Addr, out line), Is.SameAs(element));
      Assert.That(line, Is.EqualTo(7));
      Assert.That((double)new JSC.Context().Eval(r.Source + "\n_b1.Tick(1); _b1.Q;"), Is.EqualTo(3.5));
    }

    /// <summary>Тип LPLC/* без выражения - проблема самого блока, а не молча пропущенный топик.</summary>
    [Test]
    public void UnknownBlockType_IsAProblemOfTheBlock() {
      var t = Block("X", "LPLC/Nope", "{'in':{},'run':{}}");
      Tick();
      var r = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), EP_Target.EspNode, DevicePlcDeviceTests.Device(DevicePlcDeviceTests.Esp32));
      Assert.That(r.Problems.Select(z => z.Element.Tag), Is.EqualTo(new object[] { t }));
    }

    /// <summary>Тело Func - его State: блок из шаблона должен уходить в базу, иначе src пропадёт при перезапуске.</summary>
    [Test]
    public void Func_FromTheTemplate_IsSaved() {
      ImportTypes();
      Tick();
      var f = Add(_pa1, "F2", root.Get("/$YS/TYPES/LPLC/Func", false).GetState());
      var p = Add(_pa1, "P2", root.Get("/$YS/TYPES/LPLC/Pin", false).GetState());
      Tick();
      using (Assert.EnterMultipleScope()) {
        Assert.That(Saved(f), Is.True, "Func");
        Assert.That(LP_Tree.IsBlock(f) && LP_Tree.IsBlock(p), Is.True);
      }
    }

    /// <summary>Пункты переменных в меню устройства: есть до первого Run, уставка из них сохраняется в базе,
    /// пункт прежней версии заменяется, а чужие пункты остаются.</summary>
    [Test]
    public void VarTemplates_AreSavedAndRefreshed() {
      _dev.SetField("Children.Float", Json("{'manifest':{'MQTT-SN':{'tag':'Mf'}},'default':0,'type':'Double','menu':'LPLC'}"));
      _dev.SetField("Children.Filter_M", Json("{'manifest':{'MQTT-SN':{'tag':'Mf7'}},'default':0,'type':'Double','menu':'plc'}"));
      Tick();
      new X13.Periphery.DevicePLC(_pa1, z => { }, EP_Target.EspNode);   // DevicePLC без пространства имён - это X13.DevicePLC
      Tick();
      var ch = _dev.GetField("Children");
      var k2 = Add(_dev, "K2", ch["Float"]);
      Tick();
      using (Assert.EnterMultipleScope()) {
        foreach (var key in LP_Tree.VarTypes.Values.Select(z => z.ToString())) {
          Assert.That(ch.AsInt(key + ".manifest.attr", -1), Is.EqualTo((int)Topic.Attribute.DB), key);
        }
        Assert.That(Saved(k2), Is.True, "переменная из меню");
        Assert.That(k2.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo("Mf"));
        Assert.That(ch.AsString("Filter_M.menu", null), Is.EqualTo("plc"), "чужой пункт не тронут");
      }
    }

    /// <summary>Сгенерированный JS виден только при включённых журнале сборки и verbose PLC.</summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Build_LogsTheGeneratedJsOnlyWhenVerbose(bool verbose, bool log) {
      root.Get("/$YS/TYPES/MQTT-SN/T1", true).SetState(Json("{'PLC':" + DevicePlcDeviceTests.Esp32 + "}"));
      _dev.SetField("type", "MQTT-SN/T1");
      Tick();
      bool saved = MQTT_SNPl.verbosePlc;
      try {
        MQTT_SNPl.verbosePlc = verbose;
        var lines = PlcDiagnosticsTests.Capture(() => {
          if(log) {
            new X13.Periphery.DevicePLC(_pa1, z => { }, EP_Target.EspNode).Build();
          } else {
            string fingerprint;
            Assert.That(new LP_Program(_pa1, EP_Target.EspNode).Compile(false, out fingerprint), Is.Not.Null);
          }
        });
        var dumps = lines.Where(l => l.Item2.Contains("generated JS")).ToArray();
        Assert.That(dumps, Has.Length.EqualTo(verbose && log ? 1 : 0));
        if(dumps.Length > 0) {
          Assert.That(dumps[0].Item1, Is.EqualTo(LogLevel.Debug));
          Assert.That(dumps[0].Item2, Does.StartWith(_pa1.path + " - generated JS:\n"));
          Assert.That(dumps[0].Item2, Does.Contain("\n  Tick(A, B) {\n"), "Tick фильтра - только A и B");
        }
      }
      finally {
        MQTT_SNPl.verbosePlc = saved;
      }
    }

    [Test]
    public void Fingerprint_IsStableAndFollowsTheSource() {
      string a = LP_Tree.Fingerprint(Compile().Hex);
      string b = LP_Tree.Fingerprint(Compile().Hex);
      _filter.SetState(Str("return A + B;"));
      Tick();
      string c = LP_Tree.Fingerprint(Compile().Hex);
      using (Assert.EnterMultipleScope()) {
        Assert.That(b, Is.EqualTo(a));
        Assert.That(c, Is.Not.EqualTo(a));
      }
    }
  }
}
