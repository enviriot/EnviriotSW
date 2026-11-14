using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  [TestFixture]
  public class DevicePlcStreamTests {
    [TestCase("SendByte", 23, "LOG_INFO, 118")]
    [TestCase("PrintF", 26, "LOG_WARN, '%d', -118")]
    [TestCase("PrintF", 26, "LOG_ERROR, '%.2f', 21")]
    public void StreamsUseExpectedApiAndDiscardUnusedResults(string name, int api, string args) {
      string source = name + "(" + args + "); " + name + "(" + args + ");";
      foreach(var target in new[] { EP_Target.EspNode, EP_Target.X13 }) {
        var c = new EP_Compiler(target);
        Assert.That(c.Parse(source), Is.True, target + ": " + c.Error);
        var code = DevicePlcFloatTests.Disassemble(c.Hex).ToArray();
        Assert.That(code.Where(i => i.Code == EP_InstCode.API).Select(i => (int)i.Arg[0]), Is.EqualTo(new[] { api, api }), target.ToString());
        Assert.That(code.Any(i => i.Code == EP_InstCode.DROP), Is.True, target.ToString());
        if(api == 26) {
          var variable = new EP_Compiler(target);
          Assert.That(variable.Parse("var v = new Int32(); PrintF(LOG_INFO, '%.2f', v);"), Is.True, target + ": " + variable.Error);
          Assert.That(DevicePlcFloatTests.Disassemble(variable.Hex).Any(i => i.Code == EP_InstCode.S32TOF), Is.True, target.ToString());
        }
      }
    }

    /// <summary>На X13 у PrintF тот же ABI и формат в ROM, что у EspNode; поток - номер UART.</summary>
    [Test]
    public void PrintFOnX13WritesToUartWithTheSameAbi() {
      var c = new EP_Compiler(EP_Target.X13);
      Assert.That(c.Parse("var value = new Int32(215); var result = new Int32(); result = PrintF(1, 'T=%.1f/%04X\\x00', Float(value)/10, value);"), Is.True, c.Error);
      PrintFTestCall request = null;
      var vm = new DevicePlcTestVm(c.Hex) { Api = (machine, api) => {
        Assert.That(api, Is.EqualTo(26));
        request = PrintFTestCall.Read(machine);
        machine.Push(1);
      } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(request, Is.Not.Null);
      Assert.That(request.Stream, Is.EqualTo(1));
      Assert.That(request.Render(), Is.EqualTo(System.Text.Encoding.ASCII.GetBytes("T=21.5/00D7").Concat(new byte[] { 0 })));
      Assert.That(vm.Value(c.varList["result"]), Is.EqualTo(1));
    }
    [Test]
    public void NewNamesRequireExactArityAndLegacyApiStillWorks() {
      Assert.That(new EP_Compiler(EP_Target.EspNode).Parse("PrintF(LOG_INFO);"), Is.False);
      Assert.That(new EP_Compiler(EP_Target.EspNode).Parse("SendByte(LOG_INFO, 1, 2);"), Is.False);
      Assert.That(new EP_Compiler(EP_Target.X13).Parse("UartPutByte(1, 42);"), Is.True);
      Assert.That(new EP_Compiler(EP_Target.EspNode).Parse("var ok = new Int32(); ok = PrintF(LOG_DEBUG, '%d', 42);"), Is.True);
    }

    [TestCase("PrintInt32(2, 1);")]
    [TestCase("PrintFloat(2, 2, 1);")]
    [TestCase("PrintF(2, '%d');")]
    [TestCase("PrintF(2, '', 1);")]
    [TestCase("PrintF(2, '%s', 1);")]
    [TestCase("PrintF(2, '%n', 1);")]
    [TestCase("PrintF(2, '%10d', 1);")]
    [TestCase("PrintF(2, '%.9f', 1);")]
    [TestCase("PrintF(2, '%2c', 1);")]
    [TestCase("PrintF(2, '%.2X', 1);")]
    [TestCase("PrintF(2, '%10X', 1);")]
    [TestCase("PrintF(2, '%');")]
    [TestCase("PrintF(2, '\\u0100');")]
    [TestCase("PrintF(2, '%d', 'text');")]
    [TestCase("var f = new Uint8Array([37,100]); PrintF(2, f, 1);")]
    public void InvalidOrRemovedPrintingCallsFailBuild(string source) {
      var c = new EP_Compiler(EP_Target.EspNode);
      Assert.That(c.Parse(source), Is.False);
      Assert.That(c.Error, Is.Not.Empty);
    }

    [Test]
    public void NativeAbiKeepsTypesBinaryFormatAndTheSurroundingStack() {
      var c = new EP_Compiler(EP_Target.EspNode);
      Assert.That(c.Parse("var result = new Int32(); var value = new Int32(215); result = 40 + PrintF(2, '%c/%d/%08x/%.1f%%\\x00', 176, -2147483648, 0xFFFFFFFF, Float(value)/10);"), Is.True, c.Error);
      PrintFTestCall request = null;
      var vm = new DevicePlcTestVm(c.Hex) { Api = (machine, api) => {
        Assert.That(api, Is.EqualTo(26));
        request = PrintFTestCall.Read(machine);
        machine.Push(1);
      } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(request.Stream, Is.EqualTo(2));
      Assert.That(request.Values.Take(3), Is.EqualTo(new[] { 176, int.MinValue, -1 }));
      Assert.That(BitConverter.ToSingle(BitConverter.GetBytes(request.Values[3]), 0), Is.EqualTo(21.5f));
      Assert.That(request.Render(), Is.EqualTo(new byte[] { 176 }.Concat(System.Text.Encoding.ASCII.GetBytes("/-2147483648/ffffffff/21.5%")).Concat(new byte[] { 0 })));
      Assert.That(vm.Value(c.varList["result"]), Is.EqualTo(41));
      int depth = vm.MaxStackDepth;
      for(int i = 0; i < 50; i++) vm.Cycle();
      Assert.That(vm.MaxStackDepth, Is.EqualTo(depth));
    }

    [Test]
    public void CompilerImageForNativePrintFMatchesTheGoldenPacket() {
      var c = new EP_Compiler(EP_Target.EspNode);
      Assert.That(c.Parse("var value=new Int32(215); var result=new Int32(); result=40+PrintF(2,'%c/%d/%08x/%.1f%%\\x00',176,-2147483648,0xFFFFFFFF,Float(value)/10); PrintF(2,'%d',result);"), Is.True, c.Error);
      var expected = new byte[] { 176 }.Concat(System.Text.Encoding.ASCII.GetBytes("/-2147483648/ffffffff/21.5%"))
        .Concat(new byte[] { 0 }).Concat(System.Text.Encoding.ASCII.GetBytes("41")).ToArray();
      var output = new List<byte>();
      var vm = new DevicePlcTestVm(c.Hex) { Api = (machine, api) => {
        Assert.That(api, Is.EqualTo(26));
        output.AddRange(PrintFTestCall.Read(machine).Render()); machine.Push(1);
      } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(output, Is.EqualTo(expected));
      string directory = Environment.GetEnvironmentVariable("X13_PRINTF_EXPORT");
      if(!string.IsNullOrEmpty(directory)) {
        System.IO.Directory.CreateDirectory(directory);
        var image = new byte[c.Hex.Max(chunk => checked((int)chunk.Key + chunk.Value.Length))];
        foreach(var chunk in c.Hex) Array.Copy(chunk.Value, 0, image, chunk.Key, chunk.Value.Length);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "printf.bin"), image);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "printf.expected"), expected);
      }
    }

    [Test]
    public void BinaryFormatsAreNotOptimizedAsOpcodesAndAddressesRelocate() {
      var c = new EP_Compiler(EP_Target.EspNode);
      string prefix = string.Concat(Enumerable.Repeat("Random();", 100));
      Assert.That(c.Parse(prefix + "PrintF(2, '\\xFF'); PrintF(2, '\\xF3\\x00'); PrintF(2, 'after'); PrintF(2, '');"), Is.True, c.Error);
      var output = new List<byte>();
      var vm = new DevicePlcTestVm(c.Hex) { Api = (machine, api) => {
        if(api == 8) { machine.Push(1); return; }
        Assert.That(api, Is.EqualTo(26));
        var call = PrintFTestCall.Read(machine);
        output.AddRange(call.Render()); machine.Push(1);
      } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(output, Is.EqualTo(new byte[] { 255, 243, 0 }.Concat(System.Text.Encoding.ASCII.GetBytes("after"))));
    }

    [Test]
    public void FormatGrammarPreservesExistingFloatPaddingOptions() {
      var c = new EP_Compiler(EP_Target.EspNode);
      Assert.That(c.Parse("PrintF(2, '%09d|%-5d|%08x|%5.1f|%-5.1f|%f', -7, 3, 42, 2.26, 2.26, -0.0);"), Is.True, c.Error);
      string output = null;
      var vm = new DevicePlcTestVm(c.Hex) { Api = (machine, api) => {
        Assert.That(api, Is.EqualTo(26));
        output = System.Text.Encoding.ASCII.GetString(PrintFTestCall.Read(machine).Render()); machine.Push(1);
      } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(output, Is.EqualTo("-00000007|3    |0000002a|  2.3|2.3  |-0.000000"));
    }

    [TestCase(0, "0|0|00000000|0       ")]
    [TestCase(0xABCD, "abcd|ABCD|0000ABCD|ABCD    ")]
    [TestCase(-1, "ffffffff|FFFFFFFF|FFFFFFFF|FFFFFFFF")]
    [TestCase(int.MinValue, "80000000|80000000|80000000|80000000")]
    public void UppercaseHexUsesUnsignedWordsAndTheSamePaddingAsLowercase(int value, string expected) {
      const string format = "%x|%X|%08X|%-8X";
      Assert.That(EP_PrintF.Arguments(format), Is.All.EqualTo(EP_ValType.UInt));
      var c = new EP_Compiler(EP_Target.EspNode);
      string number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
      Assert.That(c.Parse("PrintF(2, '" + format + "', " + string.Join(",", Enumerable.Repeat(number, 4)) + ");"), Is.True, c.Error);
      string output = null;
      var vm = new DevicePlcTestVm(c.Hex) { Api = (machine, api) => {
        Assert.That(api, Is.EqualTo(26));
        output = System.Text.Encoding.ASCII.GetString(PrintFTestCall.Read(machine).Render()); machine.Push(1);
      } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(output, Is.EqualTo(expected));
    }
  }
}
