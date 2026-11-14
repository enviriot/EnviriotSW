///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  /// <summary>Float в компиляторе DevicePLC: меркер Mf, выбор float-команд и приведения int/float.</summary>
  /// <remarks>Проверяется байткод: программа компилируется, Hex разбирается обратно в команды по их
  /// длинам. Раскладка адресов тестам не важна - только какие команды и константы получились.</remarks>
  [TestFixture]
  public class DevicePlcFloatTests {
    internal sealed class Op {
      public uint Addr;
      public EP_InstCode Code;
      public byte[] Arg;
      /// <summary>Значение LDI_* - для float-константы это её битовый шаблон.</summary>
      public int Imm {
        get {
          switch (Code) {
          case EP_InstCode.LDI_0: return 0;
          case EP_InstCode.LDI_1: return 1;
          case EP_InstCode.LDI_M1: return -1;
          case EP_InstCode.LDI_S1: return (sbyte)Arg[0];
          case EP_InstCode.LDI_U1: return Arg[0];
          case EP_InstCode.LDI_S2: return BitConverter.ToInt16(Arg, 0);
          case EP_InstCode.LDI_U2: return BitConverter.ToUInt16(Arg, 0);
          case EP_InstCode.LDI_S4: return BitConverter.ToInt32(Arg, 0);
          default: throw new InvalidOperationException(Code + " is not LDI");
          }
        }
      }
      public override string ToString() {
        return Code + (Arg.Length > 0 ? " " + BitConverter.ToString(Arg) : string.Empty);
      }
    }

    private static int Bits(float f) {
      return BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    }

    /// <summary>Длина команды - как её кодирует EP_Compiler.Instruction.Prepare.</summary>
    private static int Length(EP_InstCode c) {
      string n = c.ToString();
      switch (c) {
      case EP_InstCode.LSL:
      case EP_InstCode.LSR:
      case EP_InstCode.ASR:
      case EP_InstCode.API:
      case EP_InstCode.LDI_S1:
      case EP_InstCode.LDI_U1:
        return 2;
      case EP_InstCode.CALL:
      case EP_InstCode.LDI_S2:
      case EP_InstCode.LDI_U2:
      case EP_InstCode.JZ:
      case EP_InstCode.JNZ:
      case EP_InstCode.JMP:
      case EP_InstCode.CHECK_IDX:
        return 3;
      case EP_InstCode.LDI_S4:
      case EP_InstCode.IN:
      case EP_InstCode.OUT:
        return 5;
      }
      if (n.EndsWith("_CS8")) {
        return 2;
      }
      if (n.EndsWith("_CS16") || n.EndsWith("_C16") || n.StartsWith("LPM_")) {
        return 3;
      }
      return 1;
    }

    private static EP_Compiler _last;

    private static List<Op> Compile(string src) {
      var c = new EP_Compiler();
      Assert.That(c.Parse(src), Is.True, "Parse failed:\n" + src);
      _last = c;
      return Disassemble(c);
    }
    /// <summary>Hex программы - команды по порядку адресов.</summary>
    internal static List<Op> Disassemble(EP_Compiler c) {
      return Disassemble(c.Hex);
    }
    internal static List<Op> Disassemble(SortedList<uint, byte[]> hex) {
      var ops = new List<Op>();
      foreach (var kv in hex) {
        var chunk = kv.Value;
        for (int i = 0; i < chunk.Length;) {
          var code = (EP_InstCode)chunk[i];
          Assert.That(Enum.IsDefined(typeof(EP_InstCode), code), Is.True, "unknown opcode 0x" + chunk[i].ToString("X2"));
          int len = Length(code);
          ops.Add(new Op { Addr = kv.Key + (uint)i, Code = code, Arg = chunk.Skip(i + 1).Take(len - 1).ToArray() });
          i += len;
        }
      }
      return ops;
    }
    private static int Count(List<Op> ops, EP_InstCode c) {
      return ops.Count(z => z.Code == c);
    }
    private static bool LoadsBits(List<Op> ops, int bits) {
      return ops.Any(z => z.Code.ToString().StartsWith("LDI_") && z.Code != EP_InstCode.LDI_MIN && z.Imm == bits);
    }
    /// <summary>Индекс первой команды c, начиная с from; -1 - нет.</summary>
    private static int IndexOf(List<Op> ops, EP_InstCode c, int from = 0) {
      return ops.FindIndex(from, z => z.Code == c);
    }

    [Test]
    public void FloatDeclaration_AllocatesMfAndLoadsFloatBits() {
      var ops = Compile("var t = new Float(1.5);\nvar k = 2.5;\n");
      Assert.That(_last.varList["t"], Does.StartWith("Mf"));
      Assert.That(_last.varList["k"], Does.StartWith("Mf"));
      Assert.That(LoadsBits(ops, Bits(1.5f)), Is.True);
      Assert.That(LoadsBits(ops, Bits(2.5f)), Is.True);
      Assert.That(Count(ops, EP_InstCode.STM_S4_C16), Is.EqualTo(2));
    }

    [Test]
    public void IntegerLiteralInFloatContext_IsFoldedAtCompileTime() {
      var ops = Compile("var t = new Float(1.5);\nt = t * 2;\n");
      Assert.That(Count(ops, EP_InstCode.FMUL), Is.EqualTo(1));
      Assert.That(Count(ops, EP_InstCode.MUL), Is.Zero);
      Assert.That(LoadsBits(ops, Bits(2f)), Is.True);
      Assert.That(Count(ops, EP_InstCode.S32TOF), Is.Zero);
    }

    [Test]
    public void AssignmentBetweenIntAndFloat_Converts() {
      var ops = Compile("var t = new Float(1.5);\nvar i = new Int32(3);\ni = t;\n");
      using (Assert.EnterMultipleScope()) {
        Assert.That(Count(ops, EP_InstCode.FTOS32), Is.EqualTo(1));
        Assert.That(Count(ops, EP_InstCode.S32TOF), Is.Zero);
      }

      ops = Compile("var t = new Float(1.5);\nvar i = new Int32(3);\nt = i;\n");
      using (Assert.EnterMultipleScope()) {
        Assert.That(Count(ops, EP_InstCode.S32TOF), Is.EqualTo(1));
        Assert.That(Count(ops, EP_InstCode.FTOS32), Is.Zero);
      }
    }

    [Test]
    public void MixedComparison_ConvertsIntOperandAndUsesFloatCompare() {
      var ops = Compile("var t = new Float(1.5);\nvar i = new Int32(3);\nif(t > i) { i = 1; }\n");
      int cvt = IndexOf(ops, EP_InstCode.S32TOF);
      using (Assert.EnterMultipleScope()) {
        Assert.That(cvt, Is.GreaterThanOrEqualTo(0));
        Assert.That(IndexOf(ops, EP_InstCode.FCGT, cvt), Is.GreaterThan(cvt));
        Assert.That(Count(ops, EP_InstCode.CGT), Is.Zero);
      }
    }

    [Test]
    public void IntDivision_StaysInteger() {
      var ops = Compile("var i = new Int32(7);\ni = i / 2;\n");
      Assert.That(Count(ops, EP_InstCode.DIV), Is.EqualTo(1));
      Assert.That(Count(ops, EP_InstCode.FDIV), Is.Zero);
    }

    [Test]
    public void IntegerProgram_HasNoFloatCommands() {
      var ops = Compile("var i = new Int32(0);\nfunction f(x) { return x * 2 + 1; }\ni = f(3) / 2;\nif(i > 3) { i++; }\n");
      Assert.That(ops.Where(z => (byte)z.Code >= 0x18 && (byte)z.Code <= 0x1F || (byte)z.Code >= 0x28 && (byte)z.Code <= 0x37), Is.Empty);
    }

    [Test]
    public void Builtins_MapToFloatCommands() {
      var ops = Compile("var t = new Float(1.5);\nvar i = new Int32(3);\nt = Math.min(t, 3, i);\ni = isNaN(t);\nt = Math.abs(Math.round(t));\n");
      Assert.That(Count(ops, EP_InstCode.FMIN), Is.EqualTo(2));
      Assert.That(Count(ops, EP_InstCode.FISNAN), Is.EqualTo(1));
      Assert.That(Count(ops, EP_InstCode.FABS), Is.EqualTo(1));
      Assert.That(Count(ops, EP_InstCode.FROUND), Is.EqualTo(1));
      Assert.That(Count(ops, EP_InstCode.SCALL), Is.Zero);
    }

    [Test]
    public void FloatIncrement_AddsOneAsFloat() {
      var ops = Compile("var t = new Float(1.5);\nt++;\n");
      Assert.That(Count(ops, EP_InstCode.FADD), Is.EqualTo(1));
      Assert.That(Count(ops, EP_InstCode.INC), Is.Zero);
      Assert.That(LoadsBits(ops, Bits(1f)), Is.True);
    }

    [Test]
    public void ClassFloatProperty_IsMf() {
      Compile("class A {\n  constructor() { this.k = new Float(0.5); }\n}\nvar a = new A();\n");
      Assert.That(_last.varList["a.k"], Does.StartWith("Mf"));
    }

    /// <summary>Тип возврата выводится по return, в том числе через вызов функции, объявленной ниже.</summary>
    [Test]
    public void ReturnType_IsInferredThroughCalls() {
      var ops = Compile(
        "var t = new Float(0);\nvar i = new Int32(0);\n" +
        "function g() { return scale(1); }\n" +
        "function scale(a) { if(a < 0) { return 0; } return a * 1.5; }\n" +
        "t = scale(4);\ni = scale(4);\nt = g();\n");
      // S32TOF - только a * 1.5 в scale (a целый); t = scale(4), t = g() и return scale(1) в g без приведений
      Assert.That(Count(ops, EP_InstCode.S32TOF), Is.EqualTo(1));
      // FTOS32 - только i = scale(4); будь g целой, return scale(1) дал бы второй
      Assert.That(Count(ops, EP_InstCode.FTOS32), Is.EqualTo(1));
    }

    /// <summary>Параметр float, если float аргумент вызова: тип входа блока Func не объявляется.</summary>
    /// <remarks>Прежде аргумент усекался до целого FTOS32 на входе в функцию.</remarks>
    [Test]
    public void FloatArgument_MakesParameterFloat() {
      var ops = Compile("function f(x) { return x * 2; }\nvar t = new Float(1.5);\nvar r = new Float(0);\nr = f(t);\n");
      using (Assert.EnterMultipleScope()) {
        Assert.That(Count(ops, EP_InstCode.FMUL), Is.EqualTo(1));
        Assert.That(Count(ops, EP_InstCode.MUL), Is.Zero);
        Assert.That(Count(ops, EP_InstCode.FTOS32), Is.Zero);
        Assert.That(Count(ops, EP_InstCode.S32TOF), Is.Zero);
      }
    }

    [Test]
    public void IntegerArgument_KeepsParameterInt() {
      var ops = Compile("function f(x) { return x * 2; }\nvar r = f(3);\n");
      using (Assert.EnterMultipleScope()) {
        Assert.That(_last.varList["r"], Does.StartWith("Md"));
        Assert.That(Count(ops, EP_InstCode.MUL), Is.EqualTo(1));
      }
    }

    /// <summary>Тип идёт по кругу: a передаёт b float, b возвращает a - сходится, а не зацикливается.</summary>
    [Test]
    public void ArgumentTypes_ConvergeOnMutualRecursion() {
      Compile("function a(x) { if(x > 10) { return x; } return b(x * 1.5); }\nfunction b(y) { return a(y + 1); }\nvar r = a(1);\n");
      Assert.That(_last.varList["r"], Does.StartWith("Mf"));
    }

    [Test]
    public void FloatParameterDefault_MakesParameterFloat() {
      var ops = Compile("var t = new Float(0);\nfunction h(a = Float(0)) { return a; }\nt = h(2);\n");
      Assert.That(LoadsBits(ops, Bits(2f)), Is.True);
      Assert.That(Count(ops, EP_InstCode.S32TOF), Is.Zero);
      Assert.That(Count(ops, EP_InstCode.FTOS32), Is.Zero);
    }

    /// <summary>FMOD нет - % с float отвергается, а не усекается молча.</summary>
    [Test]
    public void FloatModulo_IsRejected() {
      var c = new EP_Compiler();
      Assert.That(c.Parse("var t = new Float(1.5);\nvar i = new Int32(0);\ni = t % 2;\n"), Is.False);
    }
    /// <summary>Тип переменной выводится из полного выражения инициализатора. Результат Math.round(...)/10 остаётся float и не преобразуется в SINT32. </summary>
    [Test]
    public void FloatExpressionInitializer_InfersFloatType() {
      var ops = Compile(
        "var a = -1.5;\n" +
        "var b = Math.round(a * 10) / 10;\n");

      using (Assert.EnterMultipleScope()) {
        Assert.That(_last.varList["a"], Does.StartWith("Mf"));
        Assert.That(_last.varList["b"], Does.StartWith("Mf"));
        Assert.That(Count(ops, EP_InstCode.FMUL), Is.EqualTo(1));
        Assert.That(Count(ops, EP_InstCode.FROUND), Is.EqualTo(1));
        Assert.That(Count(ops, EP_InstCode.FDIV), Is.EqualTo(1));
        Assert.That(Count(ops, EP_InstCode.FTOS32), Is.Zero);
        Assert.That(Count(ops, EP_InstCode.FTOU32), Is.Zero);
      }
    }
    [TestCase("var a = -1.5;\nvar b = Math.round(a * 10) / 10;\n", "Mf", 0, 0, TestName = "Float expression infers Float")]
    [TestCase("var a = -1.5;\nvar b = a | 0;\n", "Md", 1, 0, TestName = "Bitwise OR zero converts to Int32")]
    [TestCase("var a = -1.5;\nvar b = a >> 0;\n", "Md", 1, 0, TestName = "Signed shift zero converts to Int32")]
    [TestCase("var a = -1.5;\nvar b = a >>> 0;\n", "Md", 0, 1, TestName = "Unsigned shift zero converts to UInt32")]
    public void ExpressionInitializer_InfersResultType(string source, string expectedMarker, int expectedFloatToInt, int expectedFloatToUInt) {
      var ops = Compile(source);

      using (Assert.EnterMultipleScope()) {
        Assert.That(_last.varList["b"], Does.StartWith(expectedMarker));
        Assert.That(Count(ops, EP_InstCode.FTOS32), Is.EqualTo(expectedFloatToInt));
        Assert.That(Count(ops, EP_InstCode.FTOU32), Is.EqualTo(expectedFloatToUInt));
      }
    }
  }
}
