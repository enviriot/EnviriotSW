///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using NiL.JS.BaseLibrary;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  /// <summary>Описание пинов и режимов узла (поле PLC типа устройства): разбор и проверки.</summary>
  [TestFixture]
  public class DevicePlcDeviceTests {
    /// <summary>Подмножество E1S10: ESP32 с матрицей, группы по возможностям.</summary>
    internal const string Esp32 = @"{
      'groups': {
        'Inputs':   { 'modes': { 'OFF': { 'v': 0 },
                                 'IN':  { 'v': 1, 'io': 'in', 'opt': { 'hint': 'debounce, ms', 'min': 0, 'max': 60000, 'default': 0 } } },
                      'pins': [0, 2, 4, 5, 14, 21, 22, 25, 34] },
        'Pullups':  { 'modes': { 'IN_INV_PU': { 'v': 4, 'io': 'in', 'opt': { 'min': 0, 'max': 60000, 'default': 0 } } },
                      'pins': [0, 2, 4, 5, 14, 21, 22, 25] },
        'Outputs':  { 'modes': { 'OUT': { 'v': 5, 'io': 'out' },
                                 'PWM': { 'v': 11, 'io': 'out', 'opt': { 'hint': 'frequency, Hz', 'min': 1, 'max': 40000000, 'default': 1000 } } },
                      'pins': [0, 2, 4, 5, 14, 21, 22, 25] },
        'Counters': { 'modes': { 'CNT_RISE': { 'v': 13, 'io': 'io', 'opt': { 'min': 0, 'max': 12700, 'default': 0 } } },
                      'pins': [0, 2, 4, 5, 14, 21, 22, 25, 34] },
        'ADC1':     { 'modes': { 'ANALOG': { 'v': 9, 'io': 'in', 'opt': { 'default': 3, 'values': { '0 dB': 0, '2.5 dB': 1, '6 dB': 2, '12 dB': 3 } } } },
                      'pins': [34] },
        'TWI':      { 'modes': { 'TWI0_SDA': { 'v': 16 } },
                      'pins': [0, 2, 4, 5, 14, 21, 22, 25] },
        'UART RX':  { 'modes': { 'UART2_RX': { 'v': 24, 'opt': { 'default': 0, 'values': { '8N1': 0, '8E1': 1 } } } },
                      'pins': [0, 2, 4, 5, 14, 21, 22, 25, 34] }
      }
    }";
    /// <summary>Контроллер без матрицы: пины по именам, ШИМ в группе каждого таймера.</summary>
    internal const string Stm = @"{
      'names': { 'PA0': 0, 'PA1': 1, 'PA4': 4, 'PA5': 5, 'PA6': 6, 'PB0': 16 },
      'groups': {
        'GPIO': { 'modes': { 'OUT': { 'v': 5, 'io': 'out' } }, 'pins': ['PA0', 'PA1', 'PA4', 'PA5', 'PA6', 'PB0'] },
        'TIM2': { 'modes': { 'PWM': { 'v': 11, 'io': 'out' } }, 'pins': ['PA0', 'PA1'] },
        'TIM3': { 'modes': { 'PWM': { 'v': 11, 'io': 'out' } }, 'pins': ['PA6', 'PB0'] },
        'ADC1': { 'modes': { 'ANALOG': { 'v': 9, 'io': 'in' } }, 'pins': ['PA0', 'PA4'] }
      }
    }";

    /// <summary>Описание из JSON, где кавычки - одинарные: так читаются строки в тестах.</summary>
    internal static EP_Device Device(string json) {
      return EP_Device.Parse(JSON.parse(json.Replace('\'', '"')));
    }

    [Test]
    public void Groups_GiveConstantsAndPins() {
      var d = Device(Stm);
      int pwm, pb0;
      using (Assert.EnterMultipleScope()) {
        Assert.That(d.TryConst("PWM", out pwm), Is.True);
        Assert.That(pwm, Is.EqualTo(11));
        Assert.That(d.TryConst("PB0", out pb0), Is.True);
        Assert.That(pb0, Is.EqualTo(16));
        Assert.That(d.TryConst("IN", out _), Is.False);
        // PWM в двух группах: PA0 - TIM2, PA6 - TIM3; на PA4 таймера нет
        Assert.That(d.Allows(0, d.ModeOf(11)), Is.True);
        Assert.That(d.Allows(6, d.ModeOf(11)), Is.True);
        Assert.That(d.Allows(4, d.ModeOf(11)), Is.False);
        Assert.That(d.Allows(4, d.ModeOf(9)), Is.True);
      }
    }

    [Test]
    public void Option_RangeValuesAndDefault() {
      var d = Device(Esp32);
      var pwm = d.ModeOf(11);
      var adc = d.ModeOf(9);
      var outp = d.ModeOf(5);
      using (Assert.EnterMultipleScope()) {
        Assert.That(d.CheckOption(pwm, 40000000), Is.True);
        Assert.That(d.CheckOption(pwm, 40000001), Is.False);
        Assert.That(d.CheckOption(pwm, 0), Is.False);   // 0 - «по умолчанию» прошивки, в описании min 1
        Assert.That(d.DefaultOption(pwm), Is.EqualTo(1000));
        Assert.That(d.CheckOption(adc, 3), Is.True);
        Assert.That(d.CheckOption(adc, 4), Is.False);
        Assert.That(d.DefaultOption(adc), Is.EqualTo(3));
        Assert.That(d.CheckOption(outp, 0), Is.True);   // без opt параметра нет
        Assert.That(d.CheckOption(outp, 5), Is.False);
        Assert.That(d.DefaultOption(outp), Is.Zero);
      }
    }

    [TestCase("{'groups': {'A': {'modes': {'OUT': {'v': 5, 'io': 'out'}}}}}", "PLC.groups.A.pins", TestName = "Group without pins")]
    [TestCase("{'groups': {'A': {'modes': {'OUT': {'v': 5, 'io': 'out'}}, 'pins': []}}}", "PLC.groups.A.pins", TestName = "Group with empty pins")]
    [TestCase("{'groups': {'A': {'modes': {'PWM': {'v': 11, 'io': 'out'}}, 'pins': [1]}, 'B': {'modes': {'PWM': {'v': 12, 'io': 'out'}}, 'pins': [2]}}}",
      "PLC.groups.B.modes.PWM", TestName = "Mode v differs between groups")]
    [TestCase("{'groups': {'A': {'modes': {'PWM': {'v': 11, 'io': 'out', 'opt': {'min': 1, 'max': 9, 'default': 5}}}, 'pins': [1]}, 'B': {'modes': {'PWM': {'v': 11, 'io': 'out', 'opt': {'min': 1, 'max': 9, 'default': 6}}}, 'pins': [2]}}}",
      "PLC.groups.B.modes.PWM", TestName = "Mode opt differs between groups")]
    [TestCase("{'groups': {'A': {'modes': {'IN': {'v': 1, 'io': 'in', 'opt': {'default': 0}}}, 'pins': [1]}}}", "PLC.groups.A.modes.IN.opt", TestName = "Option without range or values")]
    [TestCase("{'groups': {'A': {'modes': {'IN': {'v': 1, 'io': 'in', 'opt': {'min': 0, 'max': 10}}}, 'pins': [1]}}}", "PLC.groups.A.modes.IN.opt.default", TestName = "Option without default")]
    [TestCase("{'groups': {'A': {'modes': {'IN': {'v': 1, 'io': 'in', 'opt': {'min': 1, 'max': 10, 'default': 0}}}, 'pins': [1]}}}", "PLC.groups.A.modes.IN.opt.default", TestName = "Option default out of range")]
    [TestCase("{'groups': {'A': {'modes': {'OUT': {'v': 5, 'io': 'out'}}, 'pins': ['PZ9']}}}", "PLC.groups.A.pins[0]", TestName = "Unknown pin name")]
    public void BadDescription_IsRejected(string json, string where) {
      Action act = () => Device(json);
      Assert.That(act, Throws.InstanceOf<FormatException>().With.Message.StartsWith(where + ":"));
    }
  }
}
