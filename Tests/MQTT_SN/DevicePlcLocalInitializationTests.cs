using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;

namespace X13.Tests {
  [TestFixture]
  public class DevicePlcLocalInitializationTests {
    private static EP_Compiler Compile(string source) {
      var compiler = new EP_Compiler(EP_Target.EspNode);
      Assert.That(compiler.Parse(source), Is.True, compiler.Error);
      return compiler;
    }

    [Test]
    public void TestVmExecutesCallsLocalsAndFloatStores() {
      var compiler = Compile("function f(v) { let a = Float(v) / 4; return a; } var result = new Float(0); result = f(7);");
      var vm = new DevicePlcTestVm(compiler.Hex);
      vm.Cycle(true); vm.Cycle();
      Assert.That(vm.Value(compiler.varList["result"]), Is.EqualTo(1.75));
    }

    [Test]
    public void InitializersKeepApiCallsInSourceOrder() {
      var compiler = Compile("function f() { let a = getSeconds(); PrintF(LOG_INFO,'%d',a); let b = getMilliseconds(); return b; } f();");
      var apis = DevicePlcFloatTests.Disassemble(compiler.Hex).Where(i => i.Code == EP_InstCode.API).Select(i => i.Arg[0]);
      Assert.That(apis, Is.EqualTo(new byte[] { 7, 26, 6 }));
    }

    [TestCase("let a = 2; a = 9; let b = a * 3; return b;", 27)]
    [TestCase("let a = Float(8); a = a / 4; let b = a * 3; return b;", 6)]
    [TestCase("let a = 2; if(v) a = 9; let b = a * 3; return b;", 27)]
    [TestCase("let a = 2; while(a < 5) a++; let b = a * 3; return b;", 15)]
    [TestCase("let a = 2; { let b = 3; a += b; } let c = a * 3; return c;", 15)]
    [TestCase("let a = 2, b = a * 3; return b;", 6)]
    [TestCase("let a = 0; while(a < 3) { let b = a + 1; a = b; } let c = a * 3; return c;", 9)]
    [TestCase("let a = 2; { let a = 9; v = a; } let c = a + v; return c;", 11)]
    [TestCase("let a = new Float(8); a = a / 4; let b = a * 3; return b;", 6)]
    [TestCase("let a = 2; if(v) { let b = a + 5; return b; } let c = a * 3; return c;", 7)]
    public void LaterInitializerObservesEarlierUpdates(string body, double expected) {
      var compiler = Compile("function f(v) { " + body + " } var result = new Float(0); result = f(1);");
      var vm = new DevicePlcTestVm(compiler.Hex);
      vm.Cycle(true); vm.Cycle();
      Assert.That(vm.Value(compiler.varList["result"]), Is.EqualTo(expected));
    }

    [TestCase("while(a < v) { let b = a + 1; a = b; }")]
    [TestCase("while(a < v) { let b = a + 1; a = b; continue; }")]
    [TestCase("while(true) { let b = a + 1; a = b; if(a >= v) break; }")]
    public void RepeatedBlocksReleaseSlotsOnEveryExit(string loop) {
      var compiler = Compile("function f(v) { let a = 0; " + loop + " return a; } var result = new Int32(0); result=f(300);");
      var vm = new DevicePlcTestVm(compiler.Hex);
      vm.Cycle(true); vm.Cycle();
      Assert.That(vm.Value(compiler.varList["result"]), Is.EqualTo(300));
      Assert.That(vm.MaxStackDepth, Is.LessThan(32), "block-local stack slots must not accumulate across iterations");
    }

    [Test]
    public void EarlyReturnDoesNotRunLaterInitializer() {
      var compiler = Compile("function f(v) { if(v) return 7; let t = getSeconds(); return t; } var result = new Int32(0); result=f(1);");
      var calls = new List<byte>();
      var vm = new DevicePlcTestVm(compiler.Hex) { Api = (machine, api) => { calls.Add(api); machine.Push(42); } };
      vm.Cycle(true); vm.Cycle();
      Assert.That(vm.Value(compiler.varList["result"]), Is.EqualTo(7));
      Assert.That(calls, Is.Empty);
    }
  }
}
