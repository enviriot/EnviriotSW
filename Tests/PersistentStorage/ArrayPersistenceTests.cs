using System.Linq;
using System;
using System.Reflection;
using LiteDB;
using NUnit.Framework;
using X13.DevicePLC;
using X13.LogramPLC;
using X13.PersistentStorage;
using X13.Plc;
using X13.Repository;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Tests {
  [TestFixture]
  public class ArrayPersistenceTests : RepoTestBase {
    private LiteDB_Pl _storage;
    private static readonly MethodInfo Save = typeof(LiteDB_Pl).GetMethod("Js2Bs", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo Load = typeof(LiteDB_Pl).GetMethod("Bs2Js", BindingFlags.Instance | BindingFlags.NonPublic);

    [SetUp]
    public void CreateConverter() {
      // Init/Start не вызываются: тест не открывает persist.ldb и не запускает рабочий поток.
      _storage = new LiteDB_Pl();
    }

    [TearDown]
    public void DisposeConverter() { _storage.Stop(); }

    private static JSC.JSValue Json(string text) { return JsLib.ParseJson(text.Replace('\'', '"')); }
    private BsonValue Serialize(JSC.JSValue value) { return (BsonValue)Save.Invoke(_storage, new object[] { value }); }
    private JSC.JSValue Deserialize(BsonValue value) { return (JSC.JSValue)Load.Invoke(_storage, new object[] { value }); }
    private JSC.JSValue RoundTrip(JSC.JSValue value) {
      var document = new BsonDocument { ["v"] = Serialize(value) };
      return Deserialize(BsonSerializer.Deserialize(BsonSerializer.Serialize(document))["v"]);
    }

    [TestCase("")]
    [TestCase("text")]
    [TestCase("/target")]
    [TestCase("¤TR")]
    [TestCase("¤TR/target")]
    [TestCase("¤TR/missing")]
    public void StringsRemainLiteral(string text) {
      root.Get("/target");
      Tick();
      var value = new JSL.String(text);
      var stored = Serialize(value);
      Assert.That(stored.IsString, Is.True);
      Assert.That(stored.AsString, Is.EqualTo(text));
      Assert.That(RoundTrip(RoundTrip(value)).AsString(null), Is.EqualTo(text));
    }

    [Test]
    public void LegacyPrefixesInObjectsAndArraysRemainStrings() {
      var value = Json("{'state':'¤TR/missing','manifest':{'value':'¤TR/target','values':['¤TR','text','/target']}}");
      var stored = Serialize(value);
      Assert.That(stored["state"].IsString, Is.True);
      Assert.That(stored["manifest"]["value"].IsString, Is.True);
      Assert.That(stored["manifest"]["values"].AsArray.Select(v => v.IsString), Is.All.True);
      Assert.That(JsLib.Stringify(RoundTrip(RoundTrip(value))), Is.EqualTo(JsLib.Stringify(value)));
    }

    [TestCase("scalar")]
    [TestCase("array")]
    [TestCase("document")]
    public void ObjectIdValuesAreUnsupported(string shape) {
      BsonValue value = new BsonValue(ObjectId.NewObjectId());
      if(shape == "array") {
        value = new BsonArray { value };
      } else if(shape == "document") {
        value = new BsonDocument { ["value"] = value };
      }
      Action deserialize = () => { Deserialize(value); };
      var error = Assert.Throws<TargetInvocationException>(deserialize);
      Assert.That(error.InnerException, Is.TypeOf<NotImplementedException>());
      Assert.That(error.InnerException.Message, Is.EqualTo("Bs2Js(ObjectId)"));
    }

    [Test]
    public void RecordIdsRemainObjectIds() {
      var id = ObjectId.NewObjectId();
      var document = new BsonDocument {
        ["_id"] = id,
        ["v"] = Serialize(Json("{'text':'¤TR/missing'}"))
      };
      var stored = BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
      Assert.That(stored["_id"].IsObjectId, Is.True);
      Assert.That(stored["_id"].AsObjectId, Is.EqualTo(id));
      Assert.That(Deserialize(stored["v"]).Field("text").AsString(null), Is.EqualTo("¤TR/missing"));
    }

    [TestCase("[]", false)]
    [TestCase("[]", true)]
    [TestCase("['TwiNr']", false)]
    [TestCase("['TwiNr']", true)]
    [TestCase("[[],[118,119],{'values':[true,0,'SDA']}]", false)]
    [TestCase("[[],[118,119],{'values':[true,0,'SDA']}]", true)]
    public void DirectAndWrappedArraysRemainArrays(string json, bool wrapped) {
      var value = Json("{'value':" + json + "}").Field("value");
      if(!wrapped) value = value as JSL.Array ?? (JSL.Array)value.Value;
      Assert.That(value is JSL.Array, Is.EqualTo(!wrapped), "exercise both NiL representations");
      Assert.That(Serialize(value).IsArray, Is.True);
      var restored = RoundTrip(RoundTrip(value));
      Assert.That(ConstructorSchema.IsArray(restored), Is.True);
      Assert.That(JsLib.Stringify(restored), Is.EqualTo(JsLib.Stringify(Json(json))));
    }

    [Test]
    public void NumericKeyObjectsAreNotConvertedToArrays() {
      var value = Json("{'LPLC':{'args':{'0':'TwiNr'},'provides':{}},'empty':{},'values':{'0':118,'1':119}}");
      var restored = RoundTrip(value);
      Assert.That(ConstructorSchema.IsArray(restored.Field("LPLC.args")), Is.False);
      Assert.That(ConstructorSchema.IsArray(restored.Field("empty")), Is.False);
      Assert.That(JsLib.Stringify(restored), Is.EqualTo(JsLib.Stringify(value)));
      Assert.That(ConstructorSchema.Read(restored).Errors, Does.Contain("constructor parameters must be an array"));
      Assert.That(ConstructorSchema.Read(restored).Errors, Does.Contain("resource declarations must be an array"));
    }

    [Test]
    public void ListChoicesKeepArrayIdentityAndValueTypes() {
      var type = Json("{'LPLC':{'args':['Address']},'mi':{'LPLC':{'mi':{'args':{'mi':{'Address':{'choices':{'provider':'list','values':[118,119]}}}}}}}}");
      var schema = ConstructorSchema.Read(RoundTrip(RoundTrip(type)));
      Assert.That(schema.Errors, Is.Empty);
      Assert.That(schema.Find("Address").Choices.Values.Select(v => v.ValueType), Is.All.EqualTo(JSC.JSValueType.Integer));
      Assert.That(schema.Find("Address").Choices.Values.Select(v => v.AsInt(-1)), Is.EqualTo(new[] { 118, 119 }));
    }

    [Test]
    public void WrappedByteArraysRemainBinary() {
      var value = Json("{'mask':'¤BANfD/Dv8='}");
      Assert.That(Serialize(value)["mask"].IsBinary, Is.True);
      var restored = RoundTrip(value).Field("mask");
      Assert.That(ByteArray.IsByteArray(restored, out ByteArray bytes), Is.True);
      Assert.That(bytes.GetBytes(), Is.EqualTo(System.Convert.FromBase64String("NfD/Dv8=")));
    }

    [Test]
    public void RestoredMasterAndScannerCompileWithSavedArguments() {
      LP_Tree.ImportTypes(); Tick();
      var program = root.Get("/dev/node/custom-program", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      foreach(var entry in new[] { new[] { "LPLC/TwiMaster", "TWI0" }, new[] { "LPLC/TWI/SCAN_BUS", "SCAN_BUS01" } }) {
        var type = root.Get("/$YS/TYPES/" + entry[0], false);
        var state = type.GetState();
        var restored = RoundTrip(RoundTrip(state));
        Assert.That(ConstructorSchema.Read(restored).Errors, Is.Empty, entry[0]);
        type.SetState(restored);
        var block = Topic.Declare(program, entry[1]);
        Topic.Fill(block, JSC.JSValue.Null, RoundTrip(state.Field("manifest")), null);
      }
      program.Get("TWI0", false).SetField("LPLC.args", Json("{'TwiNr':0,'SdaPin':21,'SclPin':22}"));
      Tick();
      var device = EP_Device.Parse(RoundTrip(Json(LplcTwiTests.DeviceDescription)));
      var result = LP_DiagramCompiler.Compile(LP_Tree.Read(program), EP_Target.EspNode, device);
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems));
      Assert.That(result.Source, Does.Contain("21, 16, 22, 19"));
      Assert.That(program.Get("TWI0", false).GetField("LPLC.args.TwiNr").AsInt(-1), Is.Zero);
    }

    [Test]
    public void CatalogRefreshRepairsLegacyArrayDocumentsWithoutChangingInstances() {
      LP_Tree.ImportTypes(); Tick();
      var program = root.Get("/dev/node/custom-program", true);
      program.SetField("type", LP_Schema.PLC_TYPE);
      var master = program.Get("TWI0", true);
      master.SetField("type", "LPLC/TwiMaster");
      master.SetField("LPLC", Json("{'args':{'TwiNr':0,'SdaPin':21,'SclPin':22},'pos':{'top':1,'left':1},'in':{},'out':{},'run':{}}"));
      var scanner = program.Get("SCAN_BUS01", true);
      scanner.SetField("type", "LPLC/TWI/SCAN_BUS");
      scanner.SetField("LPLC", Json("{'args':{'TwiNr':0},'pos':{'top':8,'left':9},'in':{'Scan':0},'out':{'Addr':true},'run':{}}"));
      var types = new[] { root.Get(LP_Tree.TYPES + "/TwiMaster", false), root.Get(LP_Tree.TYPES + "/TWI/SCAN_BUS", false) };
      foreach(var type in types) {
        var state = type.GetState();
        foreach(string field in new[] { "LPLC.args", "LPLC.in", "LPLC.provides" }) {
          var value = state.Field(field);
          if(!value.Defined) continue;
          var legacy = JSC.JSObject.CreateObject();
          foreach(var item in value) legacy[item.Key] = item.Value;
          state = JsLib.SetField(state, field, legacy);
        }
        type.SetState(RoundTrip(state));
        type.SetField("version", "¤VR0.5.2610.27004");
      }
      Tick();
      string savedMaster = JsLib.Stringify(master.GetField(null));
      string savedScanner = JsLib.Stringify(scanner.GetField(null));
      foreach(var type in types) Assert.That(ConstructorSchema.Read(type.GetState()).Errors, Is.Not.Empty);

      LP_Tree.ImportTypes(); Tick();
      foreach(var type in types) {
        var restored = RoundTrip(RoundTrip(type.GetState()));
        Assert.That(ConstructorSchema.Read(restored).Errors, Is.Empty, type.path);
        Assert.That(ConstructorSchema.IsArray(restored.Field("LPLC.args")), Is.True);
        Assert.That(ConstructorSchema.IsArray(restored.Field("LPLC.in")), Is.True);
        if(type == types[0]) Assert.That(ConstructorSchema.IsArray(restored.Field("LPLC.provides")), Is.True);
        type.SetState(restored);
      }
      Tick();
      LP_Tree.ImportTypes(); Tick();
      var result = LP_DiagramCompiler.Compile(LP_Tree.Read(program), EP_Target.EspNode, DevicePlcDeviceTests.Device(LplcTwiTests.DeviceDescription));
      Assert.That(result.Ok, Is.True, string.Join("\n", result.Problems));
      Assert.That(JsLib.Stringify(master.GetField(null)), Is.EqualTo(savedMaster));
      Assert.That(JsLib.Stringify(scanner.GetField(null)), Is.EqualTo(savedScanner));
    }

    [Test]
    public void CatalogRefreshPreservesNewerAndCustomTypes() {
      LP_Tree.ImportTypes(); Tick();
      var types = new[] { root.Get(LP_Tree.TYPES + "/TwiMaster", false), root.Get(LP_Tree.TYPES + "/TWI/SCAN_BUS", false), root.Get(LP_Tree.TYPES + "/CustomDriver", true) };
      foreach(var type in types) {
        type.SetState(Json("{'custom':true,'LPLC':{'args':[],'in':[]}}"));
        type.SetField("version", "¤VR99.0.0.0");
      }
      Tick();
      var states = types.Select(t => JsLib.Stringify(t.GetState())).ToArray();
      LP_Tree.ImportTypes(); Tick();
      Assert.That(types.Select(t => JsLib.Stringify(t.GetState())), Is.EqualTo(states));
      Assert.That(types.Select(t => t.GetField("version").AsString(null)), Is.All.EqualTo("¤VR99.0.0.0"));
    }
  }
}
