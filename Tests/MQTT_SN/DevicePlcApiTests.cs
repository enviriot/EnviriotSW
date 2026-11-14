///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  /// <summary>API-функции DevicePLC: номер функции, порядок аргументов и узел, у которого функция есть, -
  /// контракт с прошивкой.</summary>
  /// <remarks>Аргументы кладутся на стек с последнего, затем <c>API &lt;номер&gt;</c>; прошивка снимает
  /// их по порядку. Проверяется сам байткод - разбор на команды здесь не нужен.</remarks>
  [TestFixture]
  public class DevicePlcApiTests {
    private const byte API = (byte)EP_InstCode.API;
    private const byte LDI_0 = (byte)EP_InstCode.LDI_0;
    private const byte LDI_1 = (byte)EP_InstCode.LDI_1;
    private const byte LDI_U1 = (byte)EP_InstCode.LDI_U1;
    private const byte LDI_U2 = (byte)EP_InstCode.LDI_U2;

    private static bool Contains(EP_Compiler c, byte[] seq) {
      return c.Hex.Values.Any(chunk => Enumerable.Range(0, System.Math.Max(0, chunk.Length - seq.Length + 1)).Any(i => chunk.Skip(i).Take(seq.Length).SequenceEqual(seq)));
    }
    private static EP_Compiler Compile(string src) {
      var c = new EP_Compiler(EP_Target.EspNode);
      Assert.That(c.Parse(src), Is.True, "Parse failed:\n" + src);
      return c;
    }

    [TestCase("ConfigPin(4, 5, 0);", new byte[] { LDI_0, LDI_U1, 5, LDI_U1, 4, API, 31 }, TestName = "ConfigPin(pin, mode, zero option)")]
    [TestCase("ConfigPin(4, 11, 1000);", new byte[] { LDI_U2, 0xE8, 0x03, LDI_U1, 11, LDI_U1, 4, API, 31 }, TestName = "ConfigPin(pin, mode, option)")]
    [TestCase("TwiBusControl(1, 0x76);", new byte[] { LDI_U1, 0x76, LDI_1, API, 40 }, TestName = "TwiBusControl(bus, ctrl)")]
    [TestCase("TwiBusPutByte(1, 0xF4);", new byte[] { LDI_U1, 0xF4, LDI_1, API, 42 }, TestName = "TwiBusPutByte(bus, data)")]
    public void VoidFunction_PushesArgumentsLastFirst(string call, byte[] expected) {
      // Второй такой же вызов вплотную за первым: будь у функции результат, между ними встал бы DROP.
      // Одного вызова мало - DROP перед RET убирает оптимизатор.
      var c = Compile(call + "\n" + call + "\n");
      Assert.That(Contains(c, expected.Concat(expected).ToArray()), Is.True);
    }

    [TestCase("TwiBusStatus", 41)]
    [TestCase("TwiBusGetByte", 43)]
    public void BusFunction_ReturnsValue(string fn, int api) {
      // Неиспользованный результат оператор снимает DROP - он и показывает, что результат есть.
      // Присваивание этого не проверит: Visit(Assignment) заранее учитывает на стеке свой DUP.
      var c = Compile(fn + "(1);\n" + fn + "(1);\n");
      var call = new byte[] { LDI_1, API, (byte)api };
      Assert.That(Contains(c, call.Concat(new[] { (byte)EP_InstCode.DROP }).Concat(call).ToArray()), Is.True);
    }

    /// <summary>Функция другого узла - ошибка компиляции, а не остановка узла с 0x88 на первом вызове.</summary>
    /// <remarks>Программа компилируется для обоих узлов: успех на своём показывает, что отказ на чужом вызван
    /// узлом, а не ошибкой в самой программе.</remarks>
    [TestCase("UartInit(2, 9600);", true, false)]
    [TestCase("ConfigPin(4, 11, 1000);", false, true)]
    [TestCase("TwiBusControl(1, 0x76);", false, true)]
    [TestCase("TwiBusStatus(1);", false, true)]
    [TestCase("TwiBusPutByte(1, 0xF4);", false, true)]
    [TestCase("TwiBusGetByte(1);", false, true)]
    [TestCase("var t = new Int32(0);\nt = NowSeconds() + Today();", true, true)]
    [TestCase("TwiControl(0x76);\nUartPutByte(1, 0x55);", true, true)]
    [TestCase("PrintF(1, '%d', 42);", true, true)]
    [TestCase("SendByte(1, 0x55);", true, true)]
    [TestCase("SendByte(LOG_DEBUG, 0x55);\nPrintF(LOG_INFO, '%d', 42);\nPrintF(LOG_WARN, '');\nPrintF(LOG_ERROR, '');", true, true)]
    public void Function_IsAvailableOnItsNode(string src, bool onX13, bool onEspNode) {
      using (Assert.EnterMultipleScope()) {
        Assert.That(new EP_Compiler(EP_Target.X13).Parse(src), Is.EqualTo(onX13), "X13");
        Assert.That(new EP_Compiler(EP_Target.EspNode).Parse(src), Is.EqualTo(onEspNode), "EspNode");
      }
    }

    [TestCase("ConfigPin1(4, 5);")]
    [TestCase("ConfigPin2(4, 11, 1000);")]
    public void RemovedPinApi_IsRejected(string src) {
      Assert.That(new EP_Compiler(EP_Target.EspNode).Parse(src), Is.False);
      Assert.That(new EP_Compiler(EP_Target.X13).Parse(src), Is.False);
    }

    [Test]
    public void ControlObject_SelectsNode() {
      using (Assert.EnterMultipleScope()) {
        Assert.That(X13.Periphery.DevicePLC.TargetOf("pa0"), Is.EqualTo(EP_Target.X13));
        Assert.That(X13.Periphery.DevicePLC.TargetOf("pa1"), Is.EqualTo(EP_Target.EspNode));
        Assert.That(X13.Periphery.DevicePLC.TargetOf("pa2"), Is.Null);
        Assert.That(X13.Periphery.DevicePLC.TargetOf("Ta0"), Is.Null);
      }
    }

    [Test]
    public void LegacyApiAndDataLayoutBytecodeRemainsCompatible() {
      string source = @"
var result = new Int32();
var words = new Uint16Array([0x1234, 0xABCD]);
class Counter {
  constructor() { this.Value = 1; }
  Tick() { this.Value++; }
}
var counter = new Counter();
counter.Tick();
result = words[1];
TwiControl(0x76); result = TwiStatus();
TwiPutByte(4); result = TwiGetByte();
result = NodeStatus(); result = getMilliseconds(); result = getSeconds();
result = Random(); result = NowSeconds(); result = Today();
result = UartBytesToRead(2); result = UartGetByte(2); UartPutByte(2, result);
TwiControl(); TwiPutByte(); UartBytesToRead(); UartPutByte(2);
UartPutByte(2, result, Random()); TwiStatus(Random());
";
      source += "UartInit(2, 9600);";
      var compiler = new EP_Compiler(EP_Target.X13);
      Assert.That(compiler.Parse(source), Is.True, compiler.Error);
      string expected = "0000:APOAAD734AADPv0CAM2gALsAADx2xgHGArsAADwExgPGBLsAAMYFuwAAxga7AADGB7sAAMYIuwAAxgm7AADGCrsAADwCxhW7AAA8AsYWuwAAmwAAPALGFwM4xgE4xgM4xhUDODwCxhcDmwAAPALGFwPGAgM9gCU8AsYU/w==;0080:PvfAAP8=;00A0:NBLNq/8=;00C0:PkCrAP8=;00E0:QIsAFUCrAP8=";
      Assert.That(string.Join(";", compiler.Hex.Select(chunk => chunk.Key.ToString("X4") + ":" + System.Convert.ToBase64String(chunk.Value))), Is.EqualTo(expected));
      Assert.That(compiler.StackBottom, Is.EqualTo(8));
    }

    [TestCase("TwiControl", 1, "I", false)]
    [TestCase("TwiStatus", 2, "", true)]
    [TestCase("TwiPutByte", 3, "I", false)]
    [TestCase("TwiGetByte", 4, "", true)]
    [TestCase("NodeStatus", 5, "", true)]
    [TestCase("getMilliseconds", 6, "", true)]
    [TestCase("getSeconds", 7, "", true)]
    [TestCase("Random", 8, "", true)]
    [TestCase("NowSeconds", 9, "", true)]
    [TestCase("Today", 10, "", true)]
    [TestCase("UartInit", 20, "II", false)]
    [TestCase("UartBytesToRead", 21, "I", true)]
    [TestCase("UartGetByte", 22, "I", true)]
    [TestCase("UartPutByte", 23, "II", true)]
    [TestCase("SendByte", 23, "II", true)]
    [TestCase("PrintF", 26, "IIII", true)]
    [TestCase("ConfigPin", 31, "III", false)]
    [TestCase("TwiBusControl", 40, "II", false)]
    [TestCase("TwiBusStatus", 41, "I", true)]
    [TestCase("TwiBusPutByte", 42, "II", false)]
    [TestCase("TwiBusGetByte", 43, "I", true)]
    public void NativeApiHasAnExplicitTypedSignature(string name, int number, string parameters, bool returnsValue) {
      var c = new EP_Compiler(name == "UartInit" ? EP_Target.X13 : EP_Target.EspNode);
      Assert.That(c.Parse(name == "PrintF" ? "PrintF(2, '');" : name + "(" + string.Join(", ", parameters.Select(p => "0")) + ");"), Is.True, c.Error);
      var api = c.global.memory.Single(m => m.type == EP_Type.API && m.vd.Name == name);
      Assert.That(api.Addr, Is.EqualTo(number));
      Assert.That(api.ParameterTypes, Is.EqualTo(parameters.Select(p => p == 'F' ? EP_ValType.Float : EP_ValType.Int)));
      Assert.That(api.ResultType, Is.EqualTo(returnsValue ? (EP_ValType?)EP_ValType.Int : null));
      Assert.That(api.ElementCount, Is.Zero);
      Assert.That(api.InstanceWords, Is.Zero);
    }

    [Test]
    public void ApiConversionsAndResultTypingFollowMetadataNotTheFunctionNumber() {
      var module = new NiL.JS.Module("UartGetByte(value);");
      var call = (NiL.JS.Expressions.Call)module.Script.Root.Body.Single();
      var c = Compile("UartGetByte(0);");
      var api = c.GetMerker(((NiL.JS.Expressions.Variable)call.LeftOperand).Descriptor);
      var value = c.DefineMerker(((NiL.JS.Expressions.Variable)call.Arguments[0]).Descriptor, EP_Type.SINT32);
      value.Addr = 0;
      // Синтетическая сигнатура проверяет общий генератор; реальный API 22 не меняется.
      api.ParameterTypes = new[] { EP_ValType.Float };
      api.ResultType = EP_ValType.Float;
      Assert.That(c.TypeOf(call), Is.EqualTo(EP_ValType.Float));
      c._sp = new System.Collections.Generic.Stack<EP_Compiler.Instruction>();

      System.Action emit = () => {
        c.cur.code.Clear();
        c._sp.Clear();
        call.Visit(new EP_VP2(c));
      };
      emit();
      Assert.That(c.cur.code.Select(i => (EP_InstCode)i._code[0]),
        Is.EqualTo(new[] { EP_InstCode.LDM_S4_C16, EP_InstCode.S32TOF, EP_InstCode.API }));
      Assert.That(c.cur.code.Last()._code[1], Is.EqualTo(22));
      Assert.That(c._sp.Count, Is.EqualTo(1));

      api.ResultType = null;
      emit();
      Assert.That(c._sp, Is.Empty);

      value.type = EP_Type.FLOAT;
      api.ParameterTypes = new[] { EP_ValType.Int };
      emit();
      Assert.That(c.cur.code.Any(i => i._code[0] == (byte)EP_InstCode.FTOS32), Is.True);

      api.ParameterTypes = new[] { EP_ValType.UInt };
      api.ResultType = EP_ValType.UInt;
      emit();
      Assert.That(c.cur.code.Any(i => i._code[0] == (byte)EP_InstCode.FTOU32), Is.True);
      Assert.That(c.TypeOf(call), Is.EqualTo(EP_ValType.UInt));
      Assert.That(c._sp.Count, Is.EqualTo(1));
    }
  }
}
