using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  /// <summary>Сдвиги DevicePLC: на константу - одна команда, на значение выражения - цикл из сдвигов на 1.</summary>
  [TestFixture]
  public class DevicePlcShiftTests {
    private static EP_Compiler Compile(string source) {
      var compiler = new EP_Compiler(EP_Target.EspNode);
      Assert.That(compiler.Parse(source), Is.True, compiler.Error);
      return compiler;
    }
    private static DevicePlcTestVm Run(EP_Compiler compiler) {
      var vm = new DevicePlcTestVm(compiler.Hex);
      vm.Cycle(true);
      vm.Cycle();
      return vm;
    }
    private static int Result(string source, string name = "result") {
      var compiler = Compile(source);
      return (int)Run(compiler).Value(compiler.varList[name]);
    }

    private static int Js(string op, int a, int n) {
      n &= 31;
      switch(op) {
      case "<<": return a << n;
      case ">>": return a >> n;
      default: return (int)((uint)a >> n);
      }
    }

    [Test]
    public void ShiftByVariable_MatchesJavaScript(
      [Values("<<", ">>", ">>>")] string op,
      [Values(0, 1, -8, 0x12345678, int.MinValue)] int a,
      [Values(0, 1, 4, 31, 32, 33, -1, 255)] int n) {
      string source = "function f(a, n) { return a " + op + " n; }\nvar result = new Int32(0);\nresult = f(" + a + ", " + n + ");\n";
      Assert.That(Result(source), Is.EqualTo(Js(op, a, n)), a + " " + op + " " + n);
    }

    /// <summary>Сдвиг на константу не изменился: одна команда, число бит по модулю 32 в её байте.</summary>
    [TestCase("<<", 3, "LSL", 3)]
    [TestCase(">>", 33, "ASR", 1)]
    [TestCase(">>>", 4, "LSR", 4)]
    public void ShiftByConstant_IsOneInstruction(string op, int n, string command, int encoded) {
      var code = (EP_InstCode)System.Enum.Parse(typeof(EP_InstCode), command);
      var compiler = Compile("var a = new Int32(-100);\nvar result = new Int32(0);\nresult = a " + op + " " + n + ";\n");
      var ops = DevicePlcFloatTests.Disassemble(compiler.Hex);
      var shifts = ops.Where(z => z.Code == EP_InstCode.LSL || z.Code == EP_InstCode.LSR || z.Code == EP_InstCode.ASR).ToList();

      Assert.That(shifts.Select(z => z.Code), Is.EqualTo(new[] { code }));
      Assert.That(shifts[0].Arg, Is.EqualTo(new[] { (byte)encoded }));
      Assert.That(ops.Count(z => z.Code == EP_InstCode.JNZ), Is.Zero);
      Assert.That((int)Run(compiler).Value(compiler.varList["result"]), Is.EqualTo(Js(op, -100, n)));
    }

    [Test]
    public void ShiftByVariable_IsLoopOverSingleBitShift() {
      var compiler = Compile("var a = new Int32(5);\nvar n = new Int32(3);\nvar result = new Int32(0);\nresult = a << n;\n");
      var ops = DevicePlcFloatTests.Disassemble(compiler.Hex);
      var shift = ops.Single(z => z.Code == EP_InstCode.LSL);

      Assert.That(shift.Arg, Is.EqualTo(new byte[] { 1 }));
      Assert.That(ops.Count(z => z.Code == EP_InstCode.JNZ), Is.EqualTo(1));
      Assert.That((int)Run(compiler).Value(compiler.varList["result"]), Is.EqualTo(40));
    }

    /// <summary>Float-операнды приводятся к целому, как ToInt32/ToUint32 в JS.</summary>
    [TestCase("result = a << n;", -7 << 2)]
    [TestCase("result = a >> n;", -7 >> 2)]
    [TestCase("result = a >>> n;", (int)(unchecked((uint)-7) >> 2))]
    public void FloatOperands_AreConvertedToInteger(string statement, int expected) {
      Assert.That(Result("var a = new Float(-7.9);\nvar n = new Float(2.9);\nvar result = new Int32(0);\n" + statement + "\n"), Is.EqualTo(expected));
    }

    [TestCase("x <<= n;", 3 << 4)]
    [TestCase("x >>= n;", -300 >> 4)]
    [TestCase("x >>>= n;", (int)(unchecked((uint)-300) >> 4))]
    public void CompoundAssignment_ShiftsByVariable(string statement, int expected) {
      int init = statement.StartsWith("x <<") ? 3 : -300;
      Assert.That(Result("var x = new Int32(" + init + ");\nvar n = new Int32(4);\n" + statement + "\n", "x"), Is.EqualTo(expected));
    }

    /// <summary>Как в JS, левый операнд вычисляется раньше правого.</summary>
    [Test]
    public void Operands_AreEvaluatedLeftToRight() {
      var compiler = Compile(
        "var s = new Int32(0);\nvar result = new Int32(0);\n" +
        "function g() { s = s * 10 + 1; return 1; }\nfunction h() { s = s * 10 + 2; return 3; }\n" +
        "result = g() << h();\n");
      var vm = Run(compiler);
      Assert.That((int)vm.Value(compiler.varList["result"]), Is.EqualTo(8));
      Assert.That((int)vm.Value(compiler.varList["s"]), Is.EqualTo(12));
    }

    /// <summary>Мантисса и порядок MAX44009 в методе класса: сдвиг на локальную переменную.</summary>
    [TestCase(0x00, 0x01, 1)]
    [TestCase(0x17, 0x0F, 0x7F << 1)]
    [TestCase(0xEF, 0x0F, 0xFF << 14)]
    public void ShiftByLocal_InClassMethod(int high, int low, int expected) {
      var compiler = Compile(
        "class S {\n  constructor() { this.V = new Int32(0); }\n" +
        "  Calc(high, low) { let exponent = high >> 4; let mantissa = ((high & 15) << 4) | (low & 15); this.V = mantissa << exponent; }\n}\n" +
        "var s = new S();\ns.Calc(" + high + ", " + low + ");\n");
      Assert.That((int)Run(compiler).Value(compiler.varList["s.V"]), Is.EqualTo(expected));
    }
  }
}
