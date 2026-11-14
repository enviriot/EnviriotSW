using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Plc;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class LplcSerialTests : RepoTestBase {
    internal const string Description = @"{
      'names':{'TX':4,'TX_ALIAS':4,'RX':5},
      'groups':{
        'Tx':{'modes':{'UART1_TX':{'v':20,'opt':{'min':1,'max':5000000,'default':9600}},
          'UART2_TX':{'v':23,'opt':{'min':1,'max':5000000,'default':9600}},
          'UART1_DE':{'v':22},'UART2_DE':{'v':25}},'pins':[4,5,18]},
        'Rx':{'modes':{'UART1_RX':{'v':21,'opt':{'default':0,'values':{'8N1':0,'8E1':1,'8O1':2,'8N2':4,'8E2':5,'8O2':6}}},
          'UART2_RX':{'v':24,'opt':{'default':0,'values':{'8N1':0,'8E1':1,'8O1':2,'8N2':4,'8E2':5,'8O2':6}}}},'pins':[4,5,34]}
      }}";
    internal const string Settings = "{'Port':2,'TxPin':4,'RxPin':5,'Baud':9600,'Format':0,'DePin':-1}";
    internal static JSC.JSValue Json(string text) { return JsLib.ParseJson(text.Replace('\'', '"')); }

    [SetUp]
    public void ImportSerial() { LP_Tree.ImportTypes(); Tick(); }

    internal ConstructorInstance Configuration(string name = "Serial", string settings = Settings) {
      return new ConstructorInstance {
        Name = name, Values = Json(settings),
        Schema = ConstructorSchema.Read(root.Get(LP_Tree.TYPES + "/SerialPort", false).GetState())
      };
    }
    internal LP_DiagramCompiler Compile(string source, string settings = Settings, string device = Description) {
      return LP_DiagramCompiler.Compile(new LP_Diagram().AddSerialPort("Serial", source, Configuration(settings: settings)),
        EP_Target.EspNode, DevicePlcDeviceTests.Device(device));
    }
    internal static void Good(LP_DiagramCompiler c) {
      Assert.That(c.Ok, Is.True, string.Join("\n", c.Problems) + "\n" + c.Source);
    }

    internal sealed class SerialVm {
      internal readonly DevicePlcTestVm Vm;
      internal readonly List<byte> Sent = new List<byte>();
      internal readonly List<int[]> Config = new List<int[]>();
      internal readonly Queue<int> Rx = new Queue<int>();
      internal readonly List<int> SentPorts = new List<int>();
      internal int Room, Failures, Attempts;
      private bool _initialized;
      internal SerialVm(LP_DiagramCompiler c) {
        Good(c);
        Vm = new DevicePlcTestVm(c.Hex) { Api = Api };
      }
      internal void Cycle(int freed = 0) {
        Room = Math.Min(256, Room + freed);
        Attempts = Failures = 0;
        if(!_initialized) Vm.Cycle(true);
        _initialized = true;
        Vm.Cycle();
      }
      private void Api(DevicePlcTestVm vm, byte api) {
        if(api == 31) { Config.Add(new[] { vm.Pop(), vm.Pop(), vm.Pop() }); return; }
        int port;
        byte[] bytes;
        if(api == 26) {
          var call = PrintFTestCall.Read(vm);
          port = call.Stream;
          bytes = call.Render();
        } else {
          port = vm.Pop();
          Assert.That(port, Is.AnyOf(1, 2));
          if(api == 21) { vm.Push(Rx.Count); return; }
          if(api == 22) { vm.Push(Rx.Count == 0 ? -1 : Rx.Dequeue()); return; }
          if(api != 23) throw new NotSupportedException("API " + api);
          bytes = new[] { unchecked((byte)vm.Pop()) };
        }
        Assert.That(port, Is.AnyOf(1, 2));
        Attempts++;
        if(bytes.Length > Room) { Failures++; vm.Push(0); }
        else { Sent.AddRange(bytes); SentPorts.AddRange(Enumerable.Repeat(port, bytes.Length)); Room -= bytes.Length; vm.Push(1); }
      }
    }

    [Test]
    public void OpensOnceAndDisabledDeDoesNotNeedAnAvailableRole() {
      var c = Compile("return this.Port;");
      var vm = new SerialVm(c);
      vm.Cycle(); vm.Cycle();
      Assert.That(vm.Config.Select(x => string.Join(",", x)), Is.EqualTo(new[] { "4,23,9600", "5,24,0" }));
      Assert.That(c.Variables.Keys.Any(k => k.Contains("__sp")), Is.False);
      var config = Configuration();
      var capabilities = PlcCapabilities.Read(Json(Description));
      capabilities.Buses.Single(b => b.Number == 2).Roles.Remove("DE");
      Assert.That(new ConstructorResolver(capabilities, new[] { config }).Resolve("Serial").Ok, Is.True);
    }

    [Test]
    public void ConfiguresRs485AndProvidesFrameChoices() {
      var c = Compile("return this.Port;", Settings.Replace("'DePin':-1", "'DePin':18").Replace("'Format':0", "'Format':6"));
      var vm = new SerialVm(c);
      vm.Cycle();
      Assert.That(vm.Config.Select(x => string.Join(",", x)), Is.EqualTo(new[] { "4,23,9600", "5,24,6", "18,25,0" }));
      var config = Configuration();
      var resolver = new ConstructorResolver(PlcCapabilities.Read(Json(Description)), new[] { config });
      Assert.That(resolver.Choices("Serial", "Format").Select(v => v.Label), Does.Contain("8N1"));
      Assert.That(resolver.Choices("Serial", "DePin").Single(v => v.Label == "Disabled").Value.AsInt(0), Is.EqualTo(-1));
    }

    [TestCase("'RxPin':5", "'RxPin':'TX_ALIAS'")]
    [TestCase("'DePin':-1", "'DePin':4")]
    [TestCase("'DePin':-1", "'DePin':5")]
    [TestCase("'DePin':-1", "'DePin':34")]
    [TestCase("'Port':2", "'Port':0")]
    [TestCase("'Format':0", "'Format':3")]
    [TestCase("'Baud':9600", "'Baud':0")]
    [TestCase("'Baud':9600", "'Baud':5000001")]
    public void RejectsInvalidConfiguration(string from, string to) {
      Assert.That(Compile("", Settings.Replace(from, to)).Ok, Is.False);
    }

    [TestCase("this.Port = 1;")]
    [TestCase("this.Port++;")]
    [TestCase("this.Port += 1;")]
    [TestCase("this['Port'] = 1;")]
    [TestCase("function change() { this.Port = 1; }")]
    public void PortRemainsReadOnly(string source) {
      var c = Compile(source);
      Assert.That(c.Ok, Is.False, source);
      Assert.That(c.Problems.Any(p => p.Line == 1 && p.Element.Name == "Serial" && p.Message.Contains("read-only")), Is.True,
        string.Join("\n", c.Problems));
    }

    [Test]
    public void RemovedFormatterAndCompilerHelperAreUnknownFunctions() {
      foreach(string source in new[] { "PrintFormat('%d', A);", "return __SerialInt(A);" }) {
        var c = Compile(source);
        Assert.That(c.Ok, Is.False, source);
        Assert.That(c.Problems.Any(p => p.Message.Contains("Unknown function")), Is.True, string.Join("\n", c.Problems));
      }
    }

    [Test]
    public void RawStreamCallsKeepBytesAndNumbersWithoutGeneratedTxState() {
      var c = Compile("if(!this.Started) { this.Started=true; this.Accepted = SendByte(this.Port, 16)"
        + " && PrintF(this.Port, '%d', 16777217) && SendByte(this.Port, 0)"
        + " && PrintF(this.Port, '%5.1f', 21.5) && SendByte(this.Port, 176); } return this.Accepted;");
      var vm = new SerialVm(c);
      vm.Cycle(256);
      var expected = new byte[] { 16 }.Concat(Encoding.ASCII.GetBytes("16777217")).Concat(new byte[] { 0 })
        .Concat(Encoding.ASCII.GetBytes(" 21.5")).Concat(new byte[] { 176 }).ToArray();
      Assert.That(vm.Sent, Is.EqualTo(expected));
      Assert.That(vm.Vm.Value(c.Variables["Serial.Q"]), Is.EqualTo(1));
      Assert.That(c.Source, Does.Not.Contain("__sp"));
      Assert.That(c.Variables.Keys, Does.Not.Contain("Serial.Busy"));
      Assert.That(c.Variables.Keys, Does.Not.Contain("Serial.Done"));
      vm.Cycle(256);
      Assert.That(vm.Sent, Is.EqualTo(expected));
      Assert.That(vm.Attempts, Is.Zero);
    }

    [Test]
    public void FailedWriteIsNotRetriedUnlessTheScriptRequestsIt() {
      var c = Compile("if(!this.Started) { this.Started=true; this.Accepted=SendByte(this.Port, 65); } return this.Accepted;");
      var vm = new SerialVm(c);
      vm.Cycle();
      Assert.That(vm.Failures, Is.EqualTo(1));
      vm.Cycle(256);
      Assert.That(vm.Sent, Is.Empty);
      Assert.That(vm.Attempts, Is.Zero);
      Assert.That(vm.Vm.Value(c.Variables["Serial.Q"]), Is.Zero);
      var retry = Compile("if(!this.Accepted) this.Accepted=SendByte(this.Port, 65); return this.Accepted;");
      var other = new SerialVm(retry);
      other.Cycle(); other.Cycle(1); other.Cycle(1);
      Assert.That(Encoding.ASCII.GetString(other.Sent.ToArray()), Is.EqualTo("A"));
      Assert.That(other.Vm.Value(retry.Variables["Serial.Q"]), Is.EqualTo(1));
    }

    [Test]
    public void ReceiveKeepsZeroAndFFEvenWhenTheTxBufferIsFull() {
      var c = Compile("this.Accepted=SendByte(this.Port, 65); this.Read=0; for(let i=0; i<3; i++) { let b=UartGetByte(this.Port); if(b<0) break; this.Sum=this.Sum+b; this.Read++; } return this.Read;");
      var vm = new SerialVm(c);
      foreach(int b in new[] { 0, 255, 3, 7 }) vm.Rx.Enqueue(b);
      vm.Cycle();
      Assert.That(vm.Vm.Value(c.Variables["Serial.Accepted"]), Is.Zero);
      Assert.That(vm.Vm.Value(c.Variables["Serial.Read"]), Is.EqualTo(3));
      Assert.That(vm.Vm.Value(c.Variables["Serial.Sum"]), Is.EqualTo(258));
      vm.Cycle(1);
      Assert.That(vm.Vm.Value(c.Variables["Serial.Sum"]), Is.EqualTo(265));
      Assert.That(vm.Vm.Value(c.Variables["Serial.Q"]), Is.EqualTo(1));
      Assert.That(vm.Sent, Is.EqualTo(new byte[] { 65 }));
    }

    [Test]
    public void BusyDoneAndFormerPrivateNamesAreOrdinaryUserFields() {
      var c = Compile("this.Busy=true; if(!this.Done) { this.Done=true; this.Calls++; } this.__spCounter++; return this.Calls;");
      var vm = new SerialVm(c);
      vm.Cycle(); vm.Cycle();
      Assert.That(vm.Vm.Value(c.Variables["Serial.Busy"]), Is.EqualTo(1));
      Assert.That(vm.Vm.Value(c.Variables["Serial.Done"]), Is.EqualTo(1));
      Assert.That(vm.Vm.Value(c.Variables["Serial.__spCounter"]), Is.EqualTo(2));
      Assert.That(vm.Vm.Value(c.Variables["Serial.Q"]), Is.EqualTo(1));
    }

    [Test]
    public void StateSchemaAndModifiedFollowSerialCodeAndArguments() {
      root.Get("/$YS/TYPES/MQTT-SN/SerialTest", true).SetState(Json("{'PLC':" + Description + "}"));
      var device = root.Get("/dev/serial", true);
      device.SetField("type", "MQTT-SN/SerialTest");
      var program = device.Get("pa1", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var serial = program.Get("Port", true);
      serial.SetField("type", PlcScript.SerialPort);
      serial.SetField("LPLC.args", Json(Settings));
      serial.SetState(Str("this.Received = 1; return this.Received;"));
      Tick();
      Assert.That(LP_Schema.SyncFunc(serial, null), Is.True);
      Tick();
      Assert.That(serial.GetField(LP_Schema.OUT_MI + ".Received").Defined, Is.True);
      string fingerprint;
      Assert.That(new LP_Program(program, EP_Target.EspNode).Compile(false, out fingerprint), Is.Not.Null);
      program.SetField(LP_Status.LoadedField, fingerprint);
      Tick();
      using(var watcher = new LP_Status.Watcher(root)) {
        for(int i = 0; i < 3; i++) { Tick(); watcher.Tick(); }
        serial.SetState(Str("this.Received = 2; return this.Received;"));
        for(int i = 0; i < 3; i++) { Tick(); watcher.Tick(); }
        Assert.That(program.GetField(LP_Status.ModifiedField).AsBool(false), Is.True);
        serial.SetState(Str("this.Received = 1; return this.Received;"));
        for(int i = 0; i < 3; i++) { Tick(); watcher.Tick(); }
        Assert.That(program.GetField(LP_Status.ModifiedField).AsBool(true), Is.False);
        serial.SetField("LPLC.args.Baud", 19200);
        for(int i = 0; i < 3; i++) { Tick(); watcher.Tick(); }
        Assert.That(program.GetField(LP_Status.ModifiedField).AsBool(false), Is.True);
      }
    }

    [Test]
    public void MasksModeOptionsAndDuplicateProvidersAreAuthoritative() {
      var config = Configuration();
      var cap = PlcCapabilities.Read(Json(Description), null, Json("2"));
      Assert.That(new ConstructorResolver(cap, new[] { config }).Resolve("Serial").Ok, Is.False);
      cap = PlcCapabilities.Read(Json(Description), Json("[0,0,0,0,0]"));
      Assert.That(new ConstructorResolver(cap, new[] { config }).Resolve("Serial").Ok, Is.False);
      var other = Configuration("Other");
      cap = PlcCapabilities.Read(Json(Description));
      Assert.That(new ConstructorResolver(cap, new[] { config, other }).Resolve("Serial").Ok, Is.False);
      Assert.That(Compile("", device: Description.Replace("'max':5000000", "'max':4800").Replace("'default':9600", "'default':4800")).Ok, Is.False);
    }

    [Test]
    public void CatalogUpgradeDoesNotRewriteLegacyScriptsOrConnections() {
      var type = root.Get(LP_Tree.TYPES + "/SerialPort", false);
      var old = JsLib.SetField(JsLib.Clone(type.GetState()), "default", Str("return this.Busy;"));
      old = JsLib.SetField(old, "manifest.LPLC.out", Json("{'Q':true,'Busy':true,'Done':true}"));
      type.SetState(old);
      type.SetField("version", "¤VR0.5.2611.2001");
      var program = root.Get("/dev/serial/pa1", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var serial = program.Get("Port", true);
      serial.SetField("type", PlcScript.SerialPort);
      serial.SetField("LPLC.args", Json(Settings));
      serial.SetField("LPLC.out.Busy", true);
      serial.SetState(Str("PrintFormat('%d', A);"));
      Tick();
      LP_Tree.ImportTypes();
      Tick();
      Assert.That(type.GetState().AsString("default", null), Is.Empty);
      Assert.That(type.GetState().Field("manifest.LPLC.out").Select(p => p.Key), Is.EqualTo(new[] { "Q" }));
      Assert.That(serial.GetState().AsString(null), Is.EqualTo("PrintFormat('%d', A);"));
      Assert.That(serial.GetField("LPLC.out.Busy").AsBool(false), Is.True);
      Assert.That(LP_DiagramCompiler.Compile(LP_Tree.Read(program), EP_Target.EspNode, DevicePlcDeviceTests.Device(Description)).Ok, Is.False);
    }

    [Test]
    public void UserStatusFieldsEnterTheSchemaButEmptyStateHasOnlyQAndPort() {
      var program = root.Get("/dev/serial/pa1", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var serial = program.Get("Port", true);
      serial.SetField("type", PlcScript.SerialPort);
      serial.SetState(Str("this.Busy = A; this.Done = !A; this.__spCounter++;"));
      Tick();
      LP_Schema.SyncFunc(serial, null);
      Tick();
      Assert.That(serial.GetField(LP_Schema.OUT_MI).Select(p => p.Key), Is.EquivalentTo(new[] { "Busy", "Done", "__spCounter" }));
      var c = Compile("");
      Good(c);
      Assert.That(c.Elements.Single().Merkers.Keys, Is.EquivalentTo(new[] { "Port", "Q" }));
      Assert.That(c.Source, Does.Not.Contain("this.Busy"));
      Assert.That(c.Source, Does.Not.Contain("this.Done"));
    }

    [Test]
    public void TwoPortsExecuteTheirOwnTickAndKeepIndependentState() {
      var d = new LP_Diagram()
        .AddSerialPort("One", "this.Count++; return A ? PrintF(this.Port, '%.2f', 1.25) : false;", Configuration("One"))
        .SetConst("One.A", "1")
        .AddSerialPort("Two", "this.Count += 2; return PrintF(this.Port, '%d', 16777217);",
          Configuration("Two", Settings.Replace("'Port':2", "'Port':1").Replace("'TxPin':4", "'TxPin':18").Replace("'RxPin':5", "'RxPin':34")));
      var c = LP_DiagramCompiler.Compile(d, EP_Target.EspNode, DevicePlcDeviceTests.Device(Description));
      var vm = new SerialVm(c);
      vm.Cycle(256);
      Assert.That(Encoding.ASCII.GetString(vm.Sent.ToArray()), Is.EqualTo("1.2516777217"));
      Assert.That(vm.SentPorts, Is.EqualTo(Enumerable.Repeat(2, 4).Concat(Enumerable.Repeat(1, 8))));
      Assert.That(vm.Vm.Value(c.Variables["One.Count"]), Is.EqualTo(1));
      Assert.That(vm.Vm.Value(c.Variables["Two.Count"]), Is.EqualTo(2));
    }
  }
}
