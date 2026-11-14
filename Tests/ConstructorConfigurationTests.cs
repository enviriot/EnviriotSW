using System.Linq;
using NUnit.Framework;
using X13.Plc;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class ConstructorConfigurationTests {
    [TestCase("0x{0:X2}", 118, "0x76")]
    [TestCase("{0:F2}", 12.5, "12.50")]
    [TestCase("{0,6:D3}", 7, "   007")]
    public void FormattingUsesInvariantCompositeSyntax(string format, double value, string expected) {
      string error;
      Assert.That(ConstructorResolver.Display(new ConstructorArgument { Format = format }, value, out error), Is.EqualTo(expected));
      Assert.That(error, Is.Null);
    }
    [Test]
    public void SparseHiddenAndFallbackRowsDoNotReorderParameters() {
      var schema = Schema("['A','B','C']", "{'A':{'cdr':3},'B':{'cdr':0},'Only':{'cdr':1},'Invisible':{}}");
      Assert.That(schema.Errors, Is.Empty);
      Assert.That(schema.Arguments.Select(a => a.Position), Is.EqualTo(new[] { 3, 0, 4, 1, 0 }));
      foreach(string row in new[] { "-1", "1.5", "256", "'1'" }) Assert.That(Schema("['A']", "{'A':{'cdr':" + row + "}}").Errors, Is.Not.Empty);
      Assert.That(Schema("['A']", "{'A':{'readonly':true}}").HasMetadata, Is.True);
    }
    [TestCase("hex2")]
    [TestCase("{0:Q2}")]
    [TestCase("{1}")]
    [TestCase("{0,999}")]
    [TestCase("{0:F99}")]
    [TestCase("{0")]
    public void InvalidFormatsAreVisibleAndPreserveTheRawValue(string format) {
      if(format == "hex2") format = "{0:hex2}";
      string error;
      Assert.That(ConstructorResolver.Display(new ConstructorArgument { Format = format }, 118, out error), Is.EqualTo("118"));
      Assert.That(error, Is.Not.Null);
    }
    [Test]
    public void MissingReferencesAndArgumentCyclesAreRejected() {
      Assert.That(Schema("['A']", "{'A':{'choices':{'provider':'pin','kind':'twi','busArg':'Missing','role':'SDA'}}}").Errors, Is.Not.Empty);
      var schema = Schema("['A','B']", "{'A':{'derive':{'provider':'bus-mode','kind':'twi','busArg':'B','role':'SDA'}},'B':{'derive':{'provider':'bus-mode','kind':'twi','busArg':'A','role':'SCL'}}}");
      var result = new ConstructorResolver(null, new[] { new ConstructorInstance { Name = "cycle", Schema = schema, Values = Json("{}") } }).Resolve("cycle");
      Assert.That(result.Ok, Is.False);
      Assert.That(result.Errors.Values.Any(e => e.Contains("dependency cycle")), Is.True);
    }
    [Test]
    public void SerialConsumersNeedProvidersEvenWithAvailableUarts() {
      var device = PlcCapabilities.Read(Json("{'groups':{'U1':{'modes':{'UART1_TX':{'v':40},'UART1_RX':{'v':41}},'pins':[32,33]},'U2':{'modes':{'UART2_TX':{'v':50},'UART2_RX':{'v':51}},'pins':[34,35]}}}"), Json("[0,0,0,0,255]"), Json("6"));
      var driver = new ConstructorInstance { Name = "Consumer", Schema = Schema("['Port']", "{'Port':{'choices':{'provider':'bus','kind':'serial','scope':'configured'}}}"), Values = Json("{'Port':2}") };
      var resolver = new ConstructorResolver(device, new[] { driver });
      Assert.That(resolver.Choices(driver.Name, "Port"), Is.Empty);
      Assert.That(resolver.Resolve(driver.Name).Ok, Is.False);
      var master = new ConstructorInstance { Name = "Provider", Schema = Schema("['Port']", "{'Port':{'choices':{'provider':'bus','kind':'serial'}}}", "[{'kind':'serial','numberArg':'Port'}]"), Values = Json("{'Port':2}") };
      resolver = new ConstructorResolver(device, new[] { driver, master });
      Assert.That(resolver.Resolve(driver.Name).Ok, Is.True);
      Assert.That(resolver.Choices(driver.Name, "Port").Select(c => (int)c.Value), Is.EqualTo(new[] { 2 }));
      device.AvailableUarts = 2;
      device.Buses.RemoveAll(b => b.Kind == "serial" && b.Number == 2);
      Assert.That(new ConstructorResolver(device, new[] { driver, master }).Resolve(driver.Name).Ok, Is.False);
    }
    [Test]
    public void ResourceCyclesAreInvalidButUnselectedCandidatesDoNotCreateCycles() {
      var device = PlcCapabilities.Read(Json("{'groups':{'T':{'modes':{'TWI0_SDA':{'v':16},'TWI0_SCL':{'v':19},'TWI1_SDA':{'v':38},'TWI1_SCL':{'v':42}},'pins':[21,22]}}}"));
      var schema = Schema("['Own','Bus']", "{'Own':{},'Bus':{'choices':{'provider':'bus','kind':'twi','scope':'configured'}}}", "[{'kind':'twi','numberArg':'Own'}]");
      var a = new ConstructorInstance { Name = "A", Schema = schema, Values = Json("{'Own':0,'Bus':1}") };
      var b = new ConstructorInstance { Name = "B", Schema = schema, Values = Json("{'Own':1,'Bus':0}") };
      var resolver = new ConstructorResolver(device, new[] { a, b });
      Assert.That(resolver.Resolve("A").Ok, Is.False);
      Assert.That(resolver.Resolve("B").Ok, Is.False);
      Assert.That(resolver.Resolve("A").Errors.Values.Any(e => e.Contains("dependency cycle")), Is.True);
      b.Schema = Schema("['Own']", "{}", "[{'kind':'twi','numberArg':'Own'}]");
      resolver = new ConstructorResolver(device, new[] { a, b });
      Assert.That(resolver.Resolve("A").Ok, Is.True);
      Assert.That(resolver.Resolve("A").Dependencies, Is.EquivalentTo(new[] { "B" }));
      Assert.That(resolver.Choices("A", "Bus").Select(c => (int)c.Value), Is.EqualTo(new[] { 1 }));
    }
    private static JSC.JSValue Json(string s) { return JsLib.ParseJson(s.Replace('\'', '"')); }
    private static ConstructorSchema Schema(string args, string descriptors, string provides = "[]") {
      return ConstructorSchema.Read(Json("{'LPLC':{'args':" + args + ",'provides':" + provides + "},'mi':{'LPLC':{'mi':{'args':{'mi':" + descriptors + "}}}}}"));
    }
    [Test]
    public void DisplayOrderIsIndependentAndListsKeepNumbers() {
      var s = Schema("['Address','Bus']", "{'Address':{'cdr':2,'format':'0x{0:X2}','choices':{'provider':'list','values':[118,119]}},'Bus':{'cdr':1}}");
      Assert.That(s.Errors, Is.Empty);
      Assert.That(s.Parameters, Is.EqualTo(new[] { "Address", "Bus" }));
      var i = new ConstructorInstance { Name = "Sensor", Schema = s, Values = Json("{'Address':118,'Bus':0}") };
      var r = new ConstructorResolver(null, new[] { i });
      Assert.That(r.Resolve("Sensor").Ok, Is.True);
      Assert.That(r.Choices("Sensor", "Address").Select(c => c.Label), Is.EqualTo(new[] { "0x76", "0x77" }));
      Assert.That(r.Choices("Sensor", "Address")[0].Value.IsNumber, Is.True);
    }
    [Test]
    public void InvalidListsAndRowsAreDiagnostics() {
      Assert.That(Schema("['A']", "{'A':{'choices':{'provider':'unknown'}}}").Errors, Is.Not.Empty);
      Assert.That(Schema("['A','B']", "{'A':{'cdr':1},'B':{'cdr':1}}").Errors, Is.Not.Empty);
      var s = Schema("['A']", "{'A':{'choices':{'provider':'list','values':[118]}}}");
      var r = new ConstructorResolver(null, new[] { new ConstructorInstance { Name = "B", Schema = s, Values = Json("{'A':119}") } });
      Assert.That(r.Resolve("B").Errors.ContainsKey("A"), Is.True);
    }
    [Test]
    public void AvailabilityRetainsHighPinsAndIndependentMissingMasks() {
      var c = PlcCapabilities.Read(Json("{}"), Json("[53,240,239,14,255]"), Json("6"));
      Assert.That(c.PinAvailable(1), Is.False);
      Assert.That(c.PinAvailable(3), Is.False);
      Assert.That(c.PinAvailable(32), Is.True);
      Assert.That(c.PinAvailable(39), Is.True);
      Assert.That(c.UartAvailable(0), Is.False);
      Assert.That(c.UartAvailable(1), Is.True);
      Assert.That(c.UartAvailable(2), Is.True);
      var zero = PlcCapabilities.Read(Json("{}"), Json("[0,0,0,0,0]"));
      Assert.That(zero.PinAvailable(32), Is.False);
      Assert.That(zero.UartAvailable(2), Is.True);
      Assert.That(PlcCapabilities.ReadPins(new ByteArray(new byte[] { 1, 2, 3, 4, 5 })), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
      Assert.Throws<System.FormatException>((System.Action)(() => PlcCapabilities.ReadPins(Json("[1,2]"))));
    }
    [Test]
    public void BusConsumersDependOnValidProvidersAndDerivedValuesAreNotSaved() {
      var device = PlcCapabilities.Read(Json("{'groups':{'TWI':{'modes':{'TWI0_SDA':{'v':16},'TWI0_SCL':{'v':19}},'pins':[21,22]}}}"));
      var master = new ConstructorInstance { Name = "ZMaster", Schema = Schema("['Sda','Mode']", "{'Bus':{'cdr':1,'choices':{'provider':'bus','kind':'twi'}},'Sda':{'choices':{'provider':'pin','kind':'twi','busArg':'Bus','role':'SDA'}},'Mode':{'cdr':0,'derive':{'provider':'bus-mode','kind':'twi','busArg':'Bus','role':'SDA'}}}", "[{'kind':'twi','numberArg':'Bus'}]"), Values = Json("{'Bus':0,'Sda':21,'Mode':999}") };
      var driver = new ConstructorInstance { Name = "ADriver", Schema = Schema("['Bus']", "{'Bus':{'choices':{'provider':'bus','kind':'twi','scope':'configured'}}}"), Values = Json("{'Bus':0}") };
      var resolver = new ConstructorResolver(device, new[] { driver, master });
      Assert.That(resolver.Resolve(driver.Name).Ok, Is.True);
      Assert.That(resolver.Resolve(driver.Name).Dependencies, Does.Contain(master.Name));
      Assert.That((int)resolver.Resolve(master.Name).Values["Mode"], Is.EqualTo(16));
      Assert.That(master.Values.Field("Mode").AsInt(0), Is.EqualTo(999));
    }
  }
}
