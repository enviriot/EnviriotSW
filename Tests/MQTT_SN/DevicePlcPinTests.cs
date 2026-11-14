///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  /// <summary>Блок Pin и константы из описания узла: конструктор в init, Tick на месте вызова, выход value.</summary>
  /// <remarks>Проверяется байткод - поиском последовательности, как в DevicePlcApiTests. Каждая ошибка
  /// компиляции стоит в паре с похожей программой, которая компилируется: иначе отказ мог бы оказаться
  /// ошибкой в самой программе теста.</remarks>
  [TestFixture]
  public class DevicePlcPinTests {
    private const byte API = (byte)EP_InstCode.API;
    private const byte LDI_0 = (byte)EP_InstCode.LDI_0;
    private const byte LDI_1 = (byte)EP_InstCode.LDI_1;
    private const byte LDI_U1 = (byte)EP_InstCode.LDI_U1;
    private const byte LDI_U2 = (byte)EP_InstCode.LDI_U2;
    private const byte DUP = (byte)EP_InstCode.DUP;
    private const byte IN = (byte)EP_InstCode.IN;
    private const byte OUT = (byte)EP_InstCode.OUT;
    private const byte STM_S4_CS8 = (byte)EP_InstCode.STM_S4_CS8;
    private const byte LDM_S4_CS8 = (byte)EP_InstCode.LDM_S4_CS8;

    private static bool Contains(EP_Compiler c, params byte[] seq) {
      return c.Hex.Values.Any(chunk => Enumerable.Range(0, System.Math.Max(0, chunk.Length - seq.Length + 1)).Any(i => chunk.Skip(i).Take(seq.Length).SequenceEqual(seq)));
    }
    private static EP_Compiler Compile(string src, string device = DevicePlcDeviceTests.Esp32) {
      var c = new EP_Compiler(EP_Target.EspNode, DevicePlcDeviceTests.Device(device));
      Assert.That(c.Parse(src), Is.True, "Parse failed:\n" + src);
      return c;
    }
    private static bool Parses(string src, EP_Target target = EP_Target.EspNode, bool withDevice = true) {
      return new EP_Compiler(target, withDevice ? DevicePlcDeviceTests.Device(DevicePlcDeviceTests.Esp32) : null).Parse(src);
    }

    [Test]
    public void Output_ConfiguresInInitAndWritesInTick() {
      var c = Compile("var led = new Pin(2, OUT);\nled.Tick(1);\n");
      using (Assert.EnterMultipleScope()) {
        // ConfigPin(2, OUT, 0): аргументы с последнего
        Assert.That(Contains(c, LDI_0, LDI_U1, 5, LDI_U1, 2, API, 31), Is.True, "init");
        Assert.That(Contains(c, LDI_1, DUP), Is.True, "value");
        // value = 1 в слово 0 экземпляра, затем OUT на GPIO 2: place и type - 0
        Assert.That(Contains(c, STM_S4_CS8, 0, OUT, 0, 0, 2, 0), Is.True, "Tick");
      }
    }

    [Test]
    public void Input_ReadsInTickAndPublishesValue() {
      var c = Compile("var b = new Pin(0, IN, 20);\nvar x = new Int32(0);\nx = b.Tick();\nx = b.value;\n");
      using (Assert.EnterMultipleScope()) {
        Assert.That(Contains(c, LDI_U1, 20, LDI_1, LDI_0, API, 31), Is.True, "init");
        Assert.That(Contains(c, IN, 0, 0, 0, 0, DUP), Is.True, "Tick");
        Assert.That(Contains(c, LDM_S4_CS8, 0), Is.True, "b.value");
        Assert.That(c.varList["b.value"], Does.StartWith("Md"));
      }
    }

    [Test]
    public void OmittedOption_IsTheDescriptionDefault() {
      // PWM без частоты - default 1000 из описания, а не 0 «по умолчанию» прошивки
      var c = Compile("var p = new Pin(4, PWM);\n");
      Assert.That(Contains(c, LDI_U2, 0xE8, 0x03, LDI_U1, 11, LDI_U1, 4, API, 31), Is.True);
    }

    [Test]
    public void PinName_IsAConstant() {
      var c = Compile("var led = new Pin(PA5, OUT);\nled.Tick(1);\n", DevicePlcDeviceTests.Stm);
      Assert.That(Contains(c, OUT, 0, 0, 5, 0), Is.True);
    }

    [Test]
    public void ModeName_IsAConstant() {
      var named = Compile("ConfigPin(4, PWM, 1000);\n");
      var number = Compile("ConfigPin(4, 11, 1000);\n");
      Assert.That(named.Hex.Values.SelectMany(z => z), Is.EqualTo(number.Hex.Values.SelectMany(z => z)));
    }

    /// <summary>ConfigPin - низкоуровневый вызов: параметр по правилам прошивки, где 0 - «по умолчанию».</summary>
    [Test]
    public void ConfigPin_OptionFollowsFirmware() {
      Assert.That(Parses("ConfigPin(4, PWM, 0);\n"), Is.True);
    }

    [TestCase("var b = new Pin(0, IN);\nb.Tick(1);", "var b = new Pin(0, OUT);\nb.Tick(1);", TestName = "Tick(value) on an input")]
    [TestCase("var p = new Pin(34, OUT);", "var p = new Pin(25, OUT);", TestName = "Pin outside the mode groups")]
    [TestCase("var p = new Pin(2, ANALOG);", "var p = new Pin(34, ANALOG);", TestName = "ANALOG outside ADC pins")]
    [TestCase("ConfigPin(34, OUT, 0);", "ConfigPin(25, OUT, 0);", TestName = "ConfigPin outside the mode groups")]
    [TestCase("var b = new Pin(0, IN, 70000);", "var b = new Pin(0, IN, 60000);", TestName = "Option above max")]
    [TestCase("var p = new Pin(4, PWM, 0);", "var p = new Pin(4, PWM, 1);", TestName = "Option below min")]
    [TestCase("var p = new Pin(2, OUT, 5);", "var p = new Pin(2, OUT, 0);", TestName = "Option of a mode without one")]
    [TestCase("var a = new Pin(34, ANALOG, 4);", "var a = new Pin(34, ANALOG, 2);", TestName = "Option not in values")]
    [TestCase("var n = new Int32(2);\nvar p = new Pin(n, OUT);", "var n = new Int32(2);\nvar p = new Pin(2, OUT);", TestName = "Pin number not a constant")]
    [TestCase("var p = new Pin(21, TWI0_SDA);", "ConfigPin(21, TWI0_SDA, 0);", TestName = "Bus mode in Pin")]
    [TestCase("let p = new Pin(2, OUT);", "var p = new Pin(2, OUT);", TestName = "Pin declared with let")]
    [TestCase("function f() { var p = new Pin(2, OUT); }\nf();", "var p = new Pin(2, OUT);\nfunction f() { p.Tick(1); }\nf();", TestName = "Pin declared in a function")]
    [TestCase("var b = new Pin(2, OUT);\nb.value = 1;", "var b = new Pin(2, OUT);\nb.Tick(1);", TestName = "Pin output written directly")]
    [TestCase("var b = new Pin(2, OUT);\nb.Read();", "var b = new Pin(2, OUT);\nb.Tick();", TestName = "Unknown Pin method")]
    public void Mistake_IsACompileError(string bad, string good) {
      using (Assert.EnterMultipleScope()) {
        Assert.That(Parses(good), Is.True, "control");
        Assert.That(Parses(bad), Is.False);
      }
    }

    [Test]
    public void Pin_NeedsEspNodeAndDescription() {
      const string src = "var p = new Pin(2, 5);\n";
      using (Assert.EnterMultipleScope()) {
        Assert.That(Parses(src), Is.True, "control");
        Assert.That(Parses(src, withDevice: false), Is.False, "no description");
        Assert.That(Parses(src, EP_Target.X13), Is.False, "X13");
      }
    }

    /// <summary>Тег X13 на EspNode: буквы ничего не значат, Mz1 молча стал бы IN на GPIO 1.</summary>
    [Test]
    public void X13Tag_IsAnErrorOnEspNode() {
      const string src = "var Op2;\nOp2 = 1;\n";
      using (Assert.EnterMultipleScope()) {
        Assert.That(Parses(src, EP_Target.X13), Is.True, "X13");
        Assert.That(Parses(src), Is.False, "EspNode");
      }
    }
  }
}
