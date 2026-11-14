///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class LplcStatusTests : RepoTestBase {
    private Topic _device, _program, _func, _output, _deviceType;
    private LP_Status.Watcher _watcher;
    private string _loaded;

    private static JSC.JSValue Json(string text) {
      return JsLib.ParseJson(text.Replace('\'', '"'));
    }

    [SetUp]
    public void SetUpStatus() {
      LP_Tree.ImportTypes();
      _deviceType = root.Get("/$YS/TYPES/MQTT-SN/StatusTest", true);
      _deviceType.SetState(Json("{'PLC':" + DevicePlcDeviceTests.Stm + "}"));
      _device = root.Get("/dev/node", true);
      _device.SetField("type", "MQTT-SN/StatusTest");
      _program = _device.Get("Logic", true);
      _program.SetField("type", LP_Schema.PLC_TYPE);
      _func = _program.Get("F1", true);
      _func.SetField("type", LP_Tree.FUNC);
      _func.SetField("LPLC", Json("{'in':{'A':2},'out':{'Q':true},'pos':{'top':1,'left':2}}"));
      _func.SetState(Str("return A + 1;"));
      _output = _device.Get("M1", true);
      _output.SetField("MQTT-SN.tag", "Md");
      _output.SetField("LPLC.in.x", "F1.Q");
      Tick();
      var compiled = LP_DiagramCompiler.Compile(LP_Tree.Read(_program), EP_Target.EspNode, DevicePlcDeviceTests.Device(DevicePlcDeviceTests.Stm));
      Assert.That(compiled.Ok, Is.True);
      _loaded = LP_Tree.Fingerprint(compiled.Hex);
      _program.SetField(LP_Status.LoadedField, _loaded);
      Tick();
      _watcher = new LP_Status.Watcher(root);
      Pump();
      Assert.That(_program.GetField(LP_Status.ModifiedField).Defined, Is.False, "startup does not build");
      Assert.That(LP_Tree.Info(_func), Is.Null);
      Fingerprint();
      Pump();
      AssertModified(false);
    }

    [TearDown]
    public void TearDownStatus() {
      _watcher?.Dispose();
      _watcher = null;
    }

    private void Pump() {
      for(int i = 0; i < 3; i++) {
        Tick();
        _watcher?.Tick();
      }
      Tick();
    }

    private string Fingerprint() {
      string fingerprint;
      Assert.That(new LP_Program(_program, EP_Target.EspNode).Compile(false, out fingerprint), Is.Not.Null);
      Assert.That(fingerprint, Is.Not.Null);
      return fingerprint;
    }

    private void AssertModified(bool expected) {
      JSC.JSValue value = _program.GetField(LP_Status.ModifiedField);
      Assert.That(value.ValueType, Is.EqualTo(JSC.JSValueType.Boolean));
      Assert.That((bool)value, Is.EqualTo(expected));
    }

    [Test]
    public void MissingBaselineIsUnknownEvenWithCachedStatus() {
      _program.SetField(LP_Status.LoadedField, JSC.JSValue.Null);
      _program.SetField(LP_Status.ModifiedField, true);
      Pump();
      Assert.That(_program.GetField(LP_Status.ModifiedField).Defined, Is.False);
    }

    [Test]
    public void SourceEditsNeedBuildToConfirmEquivalentBytes() {
      _func.SetState(Str("return A + 2;"));
      Pump();
      AssertModified(true);
      _func.SetState(Str("return A + 1; // no bytecode change"));
      Pump();
      AssertModified(true);
      Fingerprint();
      Pump();
      AssertModified(false);
      Assert.That(_program.GetField(LP_Status.LoadedField).AsString(null), Is.EqualTo(_loaded));
    }

    [Test]
    public void ConstantAndVariableLinkChangesAreObserved() {
      _func.SetField("LPLC.in.A", 3);
      Pump();
      AssertModified(true);
      _func.SetField("LPLC.in.A", 2);
      Pump();
      AssertModified(false);
      _output.SetField("LPLC.in.x", 7);
      Pump();
      AssertModified(true);
      _output.SetField("LPLC.in.x", "F1.Q");
      Pump();
      AssertModified(false);
      _output.SetField("MQTT-SN.tag", "Mf");
      Pump();
      AssertModified(true);
    }

    [Test]
    public void LayoutVisibilityRuntimeAndLiveValuesDoNotChangeTheStatus() {
      ClearEvents();
      _func.SetField("LPLC.pos.left", 10);
      _func.SetField("LPLC.out", Json("{}"));
      _func.SetField("mi.LPLC.mi.out.mi.Extra.default", true);
      _func.SetField("LPLC.run", Json("{}"));
      _output.SetField("LPLC.pos.top", 9);
      _output.SetState(23);
      _program.SetState(1);
      Pump();
      AssertModified(false);
      Assert.That(events.Any(e => e.Source == _program && e.Kind == EventKind.FieldChanged && e.FieldPath == LP_Status.ModifiedField), Is.False);
    }

    [Test]
    public void InvalidSourceNeverMatchesAKnownUpload() {
      _func.SetState(Str("return ("));
      Pump();
      AssertModified(true);
      string fingerprint;
      Assert.That(new LP_Program(_program, EP_Target.EspNode).Compile(false, out fingerprint), Is.Null);
      Pump();
      AssertModified(true);
      Assert.That(_program.GetField(LP_Status.LoadedField).AsString(null), Is.EqualTo(_loaded));
    }

    [Test]
    public void ChecksDoNotReplaceRuntimeAttachmentsOrRetagVariables() {
      var program = new LP_Program(_program, EP_Target.EspNode);
      string compiled;
      Assert.That(program.Compile(false, out compiled), Is.Not.Null);
      Pump();
      LP_Tree.Compiled attachment = LP_Tree.Info(_func);
      Assert.That(attachment, Is.Not.Null);
      _func.SetState(Str("return A + 2;"));
      Pump();
      using(Assert.EnterMultipleScope()) {
        Assert.That(LP_Tree.Info(_func), Is.SameAs(attachment));
        Assert.That(_output.GetField("MQTT-SN.tag").AsString(null), Is.EqualTo("Md"));
        Assert.That(_program.GetField(LP_Status.LoadedField).AsString(null), Is.EqualTo(_loaded));
      }
      AssertModified(true);
    }

    [Test]
    public void BuildDoesNotChangeTheAcknowledgedBaseline() {
      _func.SetState(Str("return A + 2;"));
      Pump();
      var program = new LP_Program(_program, EP_Target.EspNode);
      string fingerprint;
      Assert.That(program.Compile(false, out fingerprint), Is.Not.Null);
      Pump();
      AssertModified(true);
      Assert.That(_program.GetField(LP_Status.LoadedField).AsString(null), Is.EqualTo(_loaded));
      program.MarkLoaded(fingerprint);
      Pump();
      AssertModified(false);
    }

    [Test]
    public void AcknowledgingAnOlderUploadDoesNotHideNewerEdits() {
      _func.SetState(Str("return A + 2;"));
      Pump();
      string uploading = Fingerprint();
      _func.SetState(Str("return A + 3;"));
      Pump();
      new LP_Program(_program, EP_Target.EspNode).MarkLoaded(uploading);
      Pump();
      AssertModified(true);
      Assert.That(_program.GetField(LP_Status.LoadedField).AsString(null), Is.EqualTo(uploading));
      _func.SetState(Str("return A + 2;"));
      Pump();
      AssertModified(false);
    }

    [Test]
    public void SharedBlockDefinitionChangesAreObserved() {
      Topic block = _program.Get("Sum", true);
      block.SetField("type", "LPLC/Math/ADD");
      block.SetField("LPLC.in", Json("{'A':3,'B':2}"));
      _output.SetField("LPLC.in.x", "Sum.Q");
      Pump();
      _program.SetField(LP_Status.LoadedField, Fingerprint());
      Pump();
      AssertModified(false);
      Topic type = root.Get("/$YS/TYPES/LPLC/Math/ADD", false);
      JSC.JSValue original = type.GetState();
      JSC.JSValue changed = JsLib.Clone(original);
      changed["LPLC"]["op"] = "-";
      type.SetState(changed);
      Pump();
      AssertModified(true);
      type.SetState(original);
      Pump();
      AssertModified(false);
    }

    [Test]
    public void PinArgumentsAndDeviceDescriptionsAreObserved() {
      Topic pin = _program.Get("P1", true);
      pin.SetField("type", LP_Tree.PIN);
      pin.SetField("LPLC", Json("{'args':{'mode':'OUT','pin':'PA5'},'in':{'x':'F1.Q'}}"));
      Pump();
      _program.SetField(LP_Status.LoadedField, Fingerprint());
      Pump();
      AssertModified(false);
      pin.SetField("LPLC.args.pin", "PA0");
      Pump();
      AssertModified(true);
      pin.SetField("LPLC.args.pin", "PA5");
      Pump();
      AssertModified(false);
      JSC.JSValue original = _deviceType.GetState();
      _deviceType.SetState(Json("{}"));
      Pump();
      AssertModified(true);
      _deviceType.SetState(original);
      Pump();
      AssertModified(false);
      _device.SetField("type", "MQTT-SN/Unknown");
      Pump();
      AssertModified(true);
      _device.SetField("type", "MQTT-SN/StatusTest");
      Pump();
      AssertModified(false);
    }

    [Test]
    public void RemovingAndRestoringAVariableIsObserved() {
      _output.Remove();
      Pump();
      AssertModified(true);
      _output = _device.Get("M1", true);
      _output.SetField("MQTT-SN.tag", "Md");
      _output.SetField("LPLC.in.x", "F1.Q");
      Pump();
      AssertModified(false);
    }

    [Test]
    public void MovingTheProgramFollowsItsNewDevice() {
      Topic other = root.Get("/dev/other", true);
      _program.Move(other, null);
      Pump();
      AssertModified(true);
      _program.Move(_device, null);
      Pump();
      AssertModified(false);
      _output.SetField("LPLC.in.x", 7);
      Pump();
      AssertModified(true);
    }

    [Test]
    public void DisposalAndRestartRecheckStoredStatus() {
      _watcher.Dispose();
      _func.SetState(Str("return A + 2;"));
      Pump();
      AssertModified(false);
      _watcher = new LP_Status.Watcher(root);
      Pump();
      AssertModified(true);
    }

    [Test]
    public void ProgramTypeChangesStopAndRestartTracking() {
      _program.SetField("type", "Ext/DevicePLC");
      Pump();
      _func.SetState(Str("return A + 2;"));
      Pump();
      AssertModified(false);
      _program.SetField("type", LP_Schema.PLC_TYPE);
      Pump();
      Assert.That(_program.GetField(LP_Status.ModifiedField).Defined, Is.False);
      Fingerprint();
      Pump();
      AssertModified(true);
    }

    [Test]
    public void StartupAndEditsLeaveUnopenedProgramsUncompiled() {
      Topic program = _device.Get("Unopened", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      program.SetField(LP_Status.LoadedField, "old-upload");
      program.SetField(LP_Status.ModifiedField, false);
      Topic block = program.Get("Func", true);
      block.SetField("type", LP_Tree.FUNC);
      block.SetState(Str("return 1;"));
      Pump();
      Assert.That(program.GetField(LP_Status.ModifiedField).Defined, Is.False);
      Assert.That(LP_Tree.Info(block), Is.Null);
      block.SetState(Str("return 2;"));
      Pump();
      Assert.That(LP_Tree.Info(block), Is.Null);
      Assert.That(program.GetField(LP_Status.ModifiedField).Defined, Is.False);
    }

    [Test]
    public void BuildActionCompilesOnlyTheRequestedOfflineTypedProgram() {
      Topic other = root.Get("/dev/other/Logic", true);
      other.SetField("type", LP_Schema.PLC_TYPE);
      other.SetField(LP_Status.LoadedField, "other-upload");
      Topic block = other.Get("F1", true);
      block.SetField("type", LP_Tree.FUNC);
      block.SetState(Str("return 1;"));
      _func.SetState(Str("return A + 2;"));
      Pump();
      var previous = LP_Tree.Info(_func);
      var plugin = new X13.Periphery.MQTT_SNPl();
      Assert.That(plugin._plcs, Is.Empty);
      plugin.PlcBuildRpc(_program, JSC.JSValue.Undefined);
      Pump();
      Assert.That(LP_Tree.Info(_func), Is.Not.SameAs(previous));
      Assert.That(LP_Tree.Info(block), Is.Null);
      Assert.That(other.GetField(LP_Status.ModifiedField).Defined, Is.False);
      Assert.That(_program.GetField(LP_Status.LoadedField).AsString(null), Is.EqualTo(_loaded));
      AssertModified(true);
    }

    [Test]
    public void GeneratedVariableAddressesDoNotInvalidateTheBuild() {
      _output.SetField("MQTT-SN.tag", "Md42");
      Pump();
      AssertModified(false);
    }

    [Test]
    public void StatusWritesDoNotTriggerFurtherWrites() {
      _func.SetState(Str("return A + 2;"));
      Pump();
      AssertModified(true);
      ClearEvents();
      Pump();
      Assert.That(events.Any(e => e.Source == _program && e.FieldPath == LP_Status.ModifiedField), Is.False);
    }
  }
}
