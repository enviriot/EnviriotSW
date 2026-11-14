///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using VarType = X13.LogramPLC.LP_Diagram.VarType;

namespace X13.Tests {
  /// <summary>Ядро DeviceLogram: диаграмма из Func, Pin и переменных - программа - байткод.</summary>
  /// <remarks>Описание устройства - подмножество E1S10 из DevicePlcDeviceTests. Проверяются текст
  /// программы, результат по элементам (меркеры, адреса, проблемы с Tag элемента) и порядок команд.</remarks>
  [TestFixture]
  public class DeviceDiagramTests {
    /// <summary>Пример пользователя: фильтр с состоянием M, выход - return.</summary>
    private const string Filter = "this.M = this.M*0.75 + B*0.25;\nreturn this.M/(A+1);";

    private static LP_DiagramCompiler Build(LP_Diagram d) {
      return LP_DiagramCompiler.Compile(d, EP_Target.EspNode, DevicePlcDeviceTests.Device(DevicePlcDeviceTests.Esp32));
    }
    private static LP_DiagramCompiler Compile(LP_Diagram d) {
      var r = Build(d);
      Assert.That(r.Ok, Is.True, string.Join("\n", r.Problems) + "\n" + r.Source);
      return r;
    }
    private static string[] Names(LP_DiagramCompiler r) {
      return r.Problems.Select(z => z.Element == null ? null : z.Element.Name).ToArray();
    }
    /// <summary>Кнопка - фильтр - ШИМ, коэффициент - float-переменная K; Tag каждого элемента - строка.</summary>
    private static LP_Diagram Chain() {
      return new LP_Diagram()
        .AddPin("P1", "0", "IN", "20", "tP1")
        .AddVar("K", VarType.Float, "tK")
        .AddFunc("F1", Filter, "tF1")
        .AddPin("P2", "4", "PWM", "1000", "tP2")
        .Connect("P1.value", "F1.A")
        .Connect("K", "F1.B")
        .Connect("F1.Q", "P2.x");
    }

    [Test]
    public void FloatSource_MakesFuncFloat() {
      var r = Compile(Chain());
      var ops = DevicePlcFloatTests.Disassemble(r.Hex);
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Variables["F1.M"], Does.StartWith("Mf"));
        Assert.That(r.Variables["F1.Q"], Does.StartWith("Mf"));
        Assert.That(r.Variables["K"], Does.StartWith("Mf"));
        Assert.That(r.Variables["P1.value"], Does.StartWith("Md"));
        Assert.That(ops.Any(z => z.Code == EP_InstCode.FMUL), Is.True);
        Assert.That(ops.Any(z => z.Code == EP_InstCode.FDIV), Is.True);
      }
    }

    /// <summary>Результат раздаётся по элементам и несёт их Tag - слой дерева положит его в топик.</summary>
    [Test]
    public void Elements_CarryTagMerkersAndCode() {
      var r = Compile(Chain());
      var f1 = r.Elements.Single(z => z.Name == "F1");
      var k = r.Elements.Single(z => z.Name == "K");
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Elements.Select(z => z.Tag), Is.EquivalentTo(new[] { "tP1", "tF1", "tP2", "tK" }));
        Assert.That(f1.Merkers.Keys, Is.EquivalentTo(new[] { "Q", "M" }));
        Assert.That(f1.Merkers["Q"], Is.EqualTo(r.Variables["F1.Q"]));
        Assert.That(k.Merkers["value"], Is.EqualTo(r.Variables["K"]));
        Assert.That(f1.Code, Is.Not.Empty);
        Assert.That(k.Code, Is.Empty, "переменная без начального значения кода не даёт");
      }
    }

    [Test]
    public void TickOrder_FollowsLinksNotListOrder() {
      var d = new LP_Diagram()
        .AddPin("P2", "4", "PWM", "1000")
        .AddFunc("F1", "return A * 2;")
        .AddPin("P1", "0", "IN")
        .Connect("F1.Q", "P2.x")
        .Connect("P1.value", "F1.A");
      var ops = DevicePlcFloatTests.Disassemble(Compile(d).Hex);
      // цикл - первый блок кода, адрес 0: IN, затем вызов Tick блока F1, затем OUT
      int i = ops.FindIndex(z => z.Code == EP_InstCode.IN);
      int call = ops.FindIndex(z => z.Code == EP_InstCode.CALL);
      int o = ops.FindIndex(z => z.Code == EP_InstCode.OUT);
      Assert.That(i, Is.LessThan(call));
      Assert.That(call, Is.LessThan(o));
    }

    [Test]
    public void ReturnAndField_FeedDifferentBlocks() {
      var r = Compile(new LP_Diagram()
        .AddFunc("F1", "this.M = A * 2;\nreturn A + 1;")
        .AddFunc("F2", "return A;")
        .AddFunc("F3", "return A;")
        .Connect("F1.Q", "F2.A")
        .Connect("F1.M", "F3.A"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_b2.Q = _b2.Tick(_b1.Q);"));
        Assert.That(r.Source, Does.Contain("_b3.Q = _b3.Tick(_b1.M);"));
        Assert.That(r.Variables.Keys, Is.SupersetOf(new[] { "F1.Q", "F1.M", "F2.Q", "F3.Q" }));
      }
    }

    /// <summary>return без значения и конец без return оставляют Q прежним, как undefined у серверного Func.</summary>
    [Test]
    public void ValuelessReturn_KeepsQ() {
      var r = Compile(new LP_Diagram().AddFunc("F1", "if(A) return;\nreturn 5;"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("if(A) return this.Q;"));
        Assert.That(r.Source, Does.Contain("return 5;\nreturn this.Q;"));
        Assert.That(r.Warnings, Is.Empty, "недостижимый return this.Q добавил генератор");
      }
    }

    /// <summary>Накопитель: поле, которое только прибавляется, - выход блока, как и записанное через =.</summary>
    [Test]
    public void AccumulatedField_IsAnOutput() {
      var r = Compile(new LP_Diagram()
        .AddPin("P1", "0", "IN")
        .AddVar("V1", VarType.Int32)
        .AddFunc("F1", "this.Sum += A;\nthis.dSum = this.Sum/100;\nreturn this.dSum & 1;")
        .AddPin("P2", "2", "OUT")
        .Connect("P1.value", "F1.A")
        .Connect("F1.dSum", "V1")
        .Connect("F1.Q", "P2.x"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Variables["F1.Sum"], Does.StartWith("Md"));
        Assert.That(r.Variables["F1.dSum"], Does.StartWith("Md"));
        Assert.That(r.Variables["F1.Sum"], Is.Not.EqualTo(r.Variables["F1.dSum"]));
      }
    }

    [Test]
    public void Ring_FirstListedTicksFirst() {
      var r = Compile(new LP_Diagram()
        .AddFunc("F1", "return A + 1;")
        .AddFunc("F2", "return A * 2;")
        .Connect("F2.Q", "F1.A")
        .Connect("F1.Q", "F2.A"));
      int f1 = r.Source.IndexOf("_b1.Q = _b1.Tick(_b2.Q);");
      int f2 = r.Source.IndexOf("_b2.Q = _b2.Tick(_b1.Q);");
      Assert.That(f1, Is.GreaterThanOrEqualTo(0));
      Assert.That(f2, Is.GreaterThan(f1));
    }

    [Test]
    public void Variable_IsSourceAndSink() {
      var r = Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddVar("V2", VarType.Int32)
        .AddFunc("F1", "return A + 1;")
        .Connect("V1", "F1.A")
        .Connect("F1.Q", "V2"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_b1.Q = _b1.Tick(_v1);\n_v2 = _b1.Q;"));
        Assert.That(r.Variables.Keys, Is.SupersetOf(new[] { "V1", "V2", "F1.Q" }));
      }
    }

    [Test]
    public void Constant_IsTickArgument() {
      var r = Compile(new LP_Diagram()
        .AddFunc("F1", "return A * 2;")
        .SetConst("F1.A", "0.5"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_b1.Q = _b1.Tick(0.5);"));
        Assert.That(r.Variables["F1.Q"], Does.StartWith("Mf"), "тип входа - по константе");
      }
    }

    [Test]
    public void Constant_IsANumberOrBoolean() {
      var r = Build(new LP_Diagram().AddFunc("F1", "return A;").SetConst("F1.A", "nope()"));
      Assert.That(r.Ok, Is.False);
    }

    /// <summary>Tick объявляет входы до последнего, который трогает src: каждый лишний параметр - LDI_0 перед
    /// CALL и NIP после.</summary>
    [TestCase("return A;", "Tick(A)", TestName = "Read")]
    [TestCase("return C;", "Tick(A, B, C)", TestName = "Inputs before the last one stay")]
    [TestCase("D = 5;\nreturn 1;", "Tick(A, B, C, D)", TestName = "Write is a use too")]
    [TestCase("var s = \"H\";\nreturn 1; // G", "Tick()", TestName = "Strings and comments are not")]
    [TestCase("if(A) { let B = 2; return B; }\nreturn 0;", "Tick(A)", TestName = "Block let shadows the input")]
    public void FuncTick_DeclaresTheInputsSrcUses(string src, string decl) {
      var r = Build(new LP_Diagram().AddFunc("F1", src));
      Assert.That(r.Source, Does.Contain("\n  " + decl + " {\n"));
    }

    /// <summary>NIP после CALL - по одному на параметр: у фильтра (A, B) их два, у Func без входов - ни одного.</summary>
    [Test]
    public void FuncTick_PopsOnlyItsParameters() {
      var filter = DevicePlcFloatTests.Disassemble(Compile(Chain()).Hex);
      var none = DevicePlcFloatTests.Disassemble(Compile(new LP_Diagram().AddFunc("F1", "this.M = this.M + 1;")).Hex);
      using (Assert.EnterMultipleScope()) {
        Assert.That(NipsAfterCall(filter), Is.EqualTo(2));
        Assert.That(NipsAfterCall(none), Is.Zero);
      }
    }
    private static int NipsAfterCall(System.Collections.Generic.List<DevicePlcFloatTests.Op> ops) {
      int call = ops.FindIndex(z => z.Code == EP_InstCode.CALL);
      Assert.That(call, Is.GreaterThanOrEqualTo(0), "вызов Tick");
      return ops.Skip(call + 1).TakeWhile(z => z.Code == EP_InstCode.NIP).Count();
    }

    /// <summary>Связь на вход, которого src не трогает, - предупреждение блока; в Tick на его месте 0.</summary>
    [Test]
    public void UnusedInput_IsAWarningAndGetsZero() {
      var r = Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddFunc("F1", "return B;", "tF1")
        .Connect("V1", "F1.A"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_b1.Q = _b1.Tick(0, 0);"));
        Assert.That(r.Warnings.Select(z => z.Element.Tag), Is.EqualTo(new[] { "tF1" }));
        Assert.That(r.Warnings.Single().Message, Does.Contain("input A"));
      }
    }

    /// <summary>ADD - выражение прямо в цикле: без Tick и вызова, выход - поле общего объекта _x.</summary>
    [Test]
    public void Expr_IsInlinedIntoTheCycle() {
      var r = Compile(new LP_Diagram()
        .AddPin("P1", "0", "IN", "20")
        .AddVar("K", VarType.Float)
        .AddExpr("S1", "A + B", new[] { "A", "B" }, "tS1")
        .AddPin("P2", "4", "PWM", "1000")
        .AddVar("S1_Q", VarType.Float)
        .Connect("P1.value", "S1.A")
        .Connect("K", "S1.B")
        .Connect("S1.Q", "P2.x"));
      var ops = DevicePlcFloatTests.Disassemble(r.Hex);
      var s1 = r.Elements.Single(z => z.Name == "S1");
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_x.e1 = ((_p1.value) + (_v1));\n_p2.Tick(_x.e1);"));
        Assert.That(ops.Count(z => z.Code == EP_InstCode.CALL), Is.EqualTo(1), "только new _cx() в init");
        Assert.That(r.Variables["S1.Q"], Does.StartWith("Mf"), "тип - по входам");
        Assert.That(s1.Merkers["Q"], Is.EqualTo(r.Variables["S1.Q"]));
        Assert.That(r.Elements.Single(z => z.Name == "S1_Q").Merkers["value"], Is.EqualTo(s1.Merkers["Q"]), "наблюдатель");
        Assert.That(s1.Code, Is.Not.Empty, "команды выражения - на блоке");
      }
    }

    /// <summary>У каждого блока-выражения своё поле _x.eN и свой тип: целый ADD не становится float.</summary>
    [Test]
    public void Expr_EachBlockHasItsOwnType() {
      var r = Compile(new LP_Diagram()
        .AddVar("K", VarType.Float)
        .AddVar("N", VarType.Int32)
        .AddExpr("S1", "A + B", new[] { "A", "B" })
        .AddExpr("S2", "A + B", new[] { "A", "B" })
        .Connect("K", "S1.A")
        .Connect("N", "S2.A"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Variables["S1.Q"], Does.StartWith("Mf"));
        Assert.That(r.Variables["S2.Q"], Does.StartWith("Md"));
        Assert.That(r.Source.Split('\n').Count(z => z.StartsWith("class ")), Is.EqualTo(1), "один общий объект");
      }
    }

    /// <summary>Входы подставляются по дереву разбора: имя в комментарии остаётся, неподключённый вход - 0.</summary>
    [TestCase("A - B /* A */", "_x.e1 = ((_v1) - (0) /* A */);", TestName = "Block comment")]
    [TestCase("A * 2 // A", "_x.e1 = ((_v1) * 2 // A\n);", TestName = "Line comment keeps the parenthesis")]
    [TestCase("A > B ? A : B", "_x.e1 = ((_v1) > (0) ? (_v1) : (0));", TestName = "Every reference")]
    public void Expr_SubstitutesReferencesOnly(string expr, string line) {
      var r = Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddExpr("S1", expr, new[] { "A", "B" })
        .Connect("V1", "S1.A"));
      Assert.That(r.Source, Does.Contain(line));
    }

    [Test]
    public void Expr_UnusedInputIsAWarning() {
      var r = Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddExpr("S1", "A * 2", new[] { "A", "B" }, "tS1")
        .Connect("V1", "S1.B"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Warnings.Select(z => z.Element.Tag), Is.EqualTo(new[] { "tS1" }));
        Assert.That(r.Source, Does.Contain("_x.e1 = ((0) * 2);"));
      }
    }

    private static readonly string[] AtoH = { "A", "B", "C", "D", "E", "F", "G", "H" };

    /// <summary>Блок-операция (ADD): в программу попадают только подключённые входы, по порядку входов.</summary>
    [Test]
    public void Op_TakesOnlyConnectedInputs() {
      var r = Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddVar("V2", VarType.Int32)
        .AddOp("S1", "+", AtoH)
        .Connect("V2", "S1.G")
        .Connect("V1", "S1.C")
        .SetConst("S1.E", "5"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_x.e1 = ((_v1) + (5) + (_v2));"));
        Assert.That(r.Warnings, Is.Empty);
        Assert.That(r.Variables["S1.Q"], Does.StartWith("Md"));
      }
    }

    /// <summary>&& и || дают операнд (5 && 3 - 3), поэтому результат логической операции приводится к 1/0.</summary>
    [Test]
    public void Op_LogicGivesOneOrZero() {
      var r = Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddVar("V2", VarType.Int32)
        .AddOp("L1", "&&", AtoH)
        .AddOp("L2", "+", AtoH)
        .Connect("V1", "L1.A")
        .Connect("V2", "L1.B")
        .Connect("V1", "L2.A"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("_x.e1 = (!!((_v1) && (_v2)));"));
        Assert.That(r.Source, Does.Contain("_x.e2 = ((_v1));"), "не логическая - как есть");
      }
    }

    /// <summary>У "-" первый вход - всегда уменьшаемое: неподключённый A - 0, а не первый подключённый.
    /// Остальные входы, как у других операций, - только подключённые.</summary>
    [TestCase("A,C", "_x.e1 = ((_v1) - (_v2));", TestName = "Unconnected B is dropped")]
    [TestCase("B", "_x.e1 = ((0) - (_v1));", TestName = "Unconnected A is zero")]
    [TestCase("", "_x.e1 = ((0));", TestName = "Nothing connected")]
    public void Op_SubKeepsAAsTheMinuend(string connected, string line) {
      var d = new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddVar("V2", VarType.Int32)
        .AddOp("S1", "-", AtoH);
      var pins = connected.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < pins.Length; i++) {
        d.Connect("V" + (i + 1), "S1." + pins[i]);
      }
      Assert.That(Compile(d).Source, Does.Contain(line));
    }

    /// <summary>Не подключён ни один вход - нейтральное значение операции.</summary>
    [TestCase("+", "_x.e1 = (0);")]
    [TestCase("*", "_x.e1 = (1);")]
    public void Op_WithoutInputsGivesItsIdentity(string op, string line) {
      Assert.That(Compile(new LP_Diagram().AddOp("S1", op, AtoH)).Source, Does.Contain(line));
    }

    /// <summary>Каждую операцию из списка EP_Compiler действительно умеет.</summary>
    [TestCase("+")]
    [TestCase("-")]
    [TestCase("*")]
    [TestCase("&")]
    [TestCase("|")]
    [TestCase("^")]
    [TestCase("&&")]
    [TestCase("||")]
    public void Op_IsSupportedByTheCompiler(string op) {
      Compile(new LP_Diagram()
        .AddVar("V1", VarType.Int32)
        .AddVar("V2", VarType.Int32)
        .AddOp("S1", op, AtoH)
        .Connect("V1", "S1.A")
        .Connect("V2", "S1.B"));
    }

    /// <summary>Операция вне списка - проблема блока: "/" свёрткой неочевидна (Div и fDiv - шаблоны), остальное
    /// и вовсе не операция.</summary>
    [TestCase("/")]
    [TestCase("=")]
    [TestCase("+ 1 +")]
    public void Op_OutsideTheListIsAProblem(string op) {
      var r = Build(new LP_Diagram().AddOp("S1", op, AtoH, "tS1"));
      Assert.That(r.Problems.Select(z => z.Element == null ? null : z.Element.Tag), Is.EqualTo(new object[] { "tS1" }));
    }

    /// <summary>Выражение встаёт в цикл, а не в метод: одно выражение, без this и без записи во вход. Вход
    /// подключён к переменной: запись в него иначе стала бы записью в V1.</summary>
    [TestCase("A = 5", TestName = "Writes an input")]
    [TestCase("A++", TestName = "Increments an input")]
    [TestCase("A); this.Q = (B", TestName = "Not a single expression")]
    [TestCase("this.M + A", TestName = "Uses this")]
    [TestCase("A +", TestName = "Syntax error")]
    [TestCase((string)null, TestName = "No expression")]
    public void Expr_IsASingleExpressionWithoutWrites(string expr) {
      var r = Build(new LP_Diagram()
        .AddVar("V1", VarType.Int32, "tV1")
        .AddExpr("S1", expr, new[] { "A", "B" }, "tS1")
        .Connect("V1", "S1.A"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Problems.Select(z => z.Element == null ? null : z.Element.Tag), Is.EqualTo(new object[] { "tS1" }), string.Join("\n", r.Problems));
      }
    }

    /// <summary>Переменная объявляется без начального значения: START не затирает уставку из Enviriot.</summary>
    [Test]
    public void Variable_HasNoInitialStore() {
      var r = Compile(new LP_Diagram()
        .AddVar("K", VarType.Float)
        .AddFunc("F1", "return A;")
        .Connect("K", "F1.A"));
      var ops = DevicePlcFloatTests.Disassemble(r.Hex);
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Source, Does.Contain("var _v1 = new Float();"));
        Assert.That(ops.Count(z => z.Code == EP_InstCode.STM_S4_C16), Is.Zero, "в меркер K программа не пишет");
      }
    }

    [TestCase("Boolean", "Mz")]
    [TestCase("Int8", "Mb")]
    [TestCase("UInt8", "MB")]
    [TestCase("Int16", "Mw")]
    [TestCase("UInt16", "MW")]
    [TestCase("Int32", "Md")]
    [TestCase("Float", "Mf")]
    public void VariableType_GivesItsMerker(string type, string prefix) {
      var r = Compile(new LP_Diagram().AddVar("V", (VarType)System.Enum.Parse(typeof(VarType), type)));
      Assert.That(r.Variables["V"], Does.StartWith(prefix));
    }

    /// <summary>Наблюдатель F1_M показывает поле F1.M: своего меркера нет, тег - меркер поля.</summary>
    [Test]
    public void Observer_ShowsTheBlockOutput() {
      var r = Compile(new LP_Diagram()
        .AddFunc("F1", "this.M = A * 0.5;\nreturn A;")
        .AddVar("F1_M", VarType.Float, "tObs"));
      var obs = r.Elements.Single(z => z.Name == "F1_M");
      using (Assert.EnterMultipleScope()) {
        Assert.That(obs.Merkers["value"], Is.EqualTo(r.Variables["F1.M"]));
        Assert.That(r.Source, Does.Not.Contain("_v1"), "наблюдатель не объявляется");
      }
    }

    [Test]
    public void Observer_IsNotFed() {
      var r = Build(new LP_Diagram()
        .AddFunc("F1", "this.M = A;\nreturn A;")
        .AddVar("F1_M", VarType.Int32, "tObs")
        .Connect("F1.Q", "F1_M"));
      Assert.That(r.Problems.Single().Element.Tag, Is.EqualTo("tObs"));
    }

    /// <summary>Имена диаграммы в JS не попадают: блок OUT не заслоняет константу режима.</summary>
    [Test]
    public void BlockName_DoesNotClashWithConstants() {
      var r = Compile(new LP_Diagram()
        .AddFunc("OUT", "return A;")
        .AddPin("Op2", "2", "OUT")
        .Connect("OUT.Q", "Op2.x"));
      Assert.That(r.Variables.Keys, Is.SupersetOf(new[] { "OUT.Q", "Op2.value" }));
    }

    [TestCase("F2.Z", "F1.Q", "F2", TestName = "No such input")]
    [TestCase("F2.A", "F1.R", "F1", TestName = "No such output")]
    [TestCase("F2.A", "F9.Q", null, TestName = "No such block")]
    public void BadLink_IsAProblem(string to, string from, string block) {
      var r = Build(new LP_Diagram()
        .AddFunc("F1", "return A;")
        .AddFunc("F2", "return A;")
        .Connect(from, to));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Ok, Is.False);
        Assert.That(Names(r), Does.Contain(block));
      }
    }

    [Test]
    public void InputConnectedTwice_IsAProblemOfTheBlock() {
      var r = Build(new LP_Diagram()
        .AddFunc("F1", "return A;", "tF1")
        .AddFunc("F2", "return A;", "tF2")
        .Connect("F1.Q", "F2.A")
        .Connect("F1.Q", "F2.A"));
      var f2 = r.Elements.Single(z => z.Name == "F2");
      using (Assert.EnterMultipleScope()) {
        Assert.That(f2.Problems.Any(z => z.Message.Contains("twice")), Is.True);
        Assert.That(r.Problems.Single().Element.Tag, Is.EqualTo("tF2"));
      }
    }

    [Test]
    public void SyntaxError_PointsAtSrcLine() {
      var r = Build(new LP_Diagram().AddFunc("F1", "return 1;\nif(A { }"));
      Assert.That(r.Problems.Any(z => z.Element != null && z.Element.Name == "F1" && z.Line == 2), Is.True, string.Join("\n", r.Problems));
    }

    [Test]
    public void CompileError_PointsAtSrcLine() {
      var r = Build(new LP_Diagram().AddFunc("F1", "let a = A;\nreturn a + nope;"));
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Problems.Any(z => z.Element != null && z.Element.Name == "F1" && z.Line == 2), Is.True, string.Join("\n", r.Problems));
      }
    }

    /// <summary>PC из отчёта STOP_RESP - блок и строка src: деление в Tick - строка 2 фильтра, вызов Tick
    /// в цикле - блок без строки src, ConfigPin в init - пин.</summary>
    [Test]
    public void ProgramCounter_LeadsToTheBlockAndLine() {
      var r = Compile(Chain());
      var ops = DevicePlcFloatTests.Disassemble(r.Hex);
      var div = ops.First(z => z.Code == EP_InstCode.FDIV);
      var call = ops.First(z => z.Code == EP_InstCode.CALL);
      var config = ops.First(z => z.Code == EP_InstCode.API && z.Arg[0] == 31);
      int line;
      using (Assert.EnterMultipleScope()) {
        Assert.That(r.ElementAt(div.Addr, out line).Name, Is.EqualTo("F1"));
        Assert.That(line, Is.EqualTo(2));
        Assert.That(r.ElementAt(call.Addr, out line).Name, Is.EqualTo("F1"));
        Assert.That(line, Is.Zero);
        Assert.That(r.ElementAt(config.Addr, out line).Name, Is.EqualTo("P1"));
      }
    }
  }
}
