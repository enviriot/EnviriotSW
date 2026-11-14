///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using NUnit.Framework;
using X13.Periphery;
using X13.Repository;

namespace X13.Tests {
  [TestFixture]
  public class MqttSnConfigTests : RepoTestBase {
    private MQTT_SNPl _plugin;
    private bool _plc, _twi;
    private byte _radius;
    private const string OwnerPath = "/$YS/MQTT-SN/";

    [SetUp]
    public void SaveFlags() {
      _plc = MQTT_SNPl.verbosePlc;
      _twi = MQTT_SNPl.verboseTwi;
      _radius = MQTT_SNPl.gwRadius;
      _plugin = new MQTT_SNPl();
    }

    [TearDown]
    public void RestoreFlags() {
      _plugin.Stop();
      MQTT_SNPl.verbosePlc = _plc;
      MQTT_SNPl.verboseTwi = _twi;
      MQTT_SNPl.gwRadius = _radius;
    }

    private Topic SavedBool(string path, bool value) {
      var topic = root.Get(path, true);
      topic.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
      topic.SetState(value);
      return topic;
    }

    [Test]
    public void FreshConfigurationCreatesOnlyOwnedVerboseSettings() {
      _plugin.Configure();
      Tick();
      foreach(var name in new[] { "DPLC_verbose", "TWI_verbose" }) {
        var topic = root.Get(OwnerPath + name, false);
        Assert.That(topic, Is.Not.Null);
        Assert.That(topic.GetState().ValueType, Is.EqualTo(NiL.JS.Core.JSValueType.Boolean));
        Assert.That(topic.CheckAttribute(Topic.Attribute.Required | Topic.Attribute.Config), Is.True);
        Assert.That(topic.CheckAttribute(Topic.Attribute.DB), Is.False);
      }
      Assert.That(MQTT_SNPl.verbosePlc, Is.EqualTo(root.Get(OwnerPath + "DPLC_verbose", false).GetState().AsBool(false)));
      Assert.That(MQTT_SNPl.verboseTwi, Is.EqualTo(root.Get(OwnerPath + "TWI_verbose", false).GetState().AsBool(false)));
      Assert.That(root.Get("/$YS/DevicePLC", false), Is.Null);
      Assert.That(root.Get("/$YS/TWI", false), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExistingFlagsAreRecreatedAsConfigWithTheirValues(bool value) {
      var plc = SavedBool("/$YS/DevicePLC/verbose", value);
      var twi = SavedBool("/$YS/TWI/verbose", !value);
      plc.SetField("hint", "custom hint");
      twi.SetField("hint", "custom TWI hint");
      Tick();
      _plugin.Configure();
      Assert.That(MQTT_SNPl.verbosePlc, Is.EqualTo(value), "до первого тика");
      Assert.That(MQTT_SNPl.verboseTwi, Is.EqualTo(!value));
      var newPlc = root.Get(OwnerPath + "DPLC_verbose", false);
      var newTwi = root.Get(OwnerPath + "TWI_verbose", false);
      Assert.That(newPlc, Is.Not.Null.And.Not.SameAs(plc));
      Assert.That(newTwi, Is.Not.Null.And.Not.SameAs(twi));
      Tick();
      Assert.That(newPlc.GetState().AsBool(!value), Is.EqualTo(value));
      Assert.That(newTwi.GetState().AsBool(value), Is.EqualTo(!value));
      foreach(var topic in new[] { newPlc, newTwi }) {
        Assert.That(topic.GetField("hint").Defined, Is.False);
        Assert.That(topic.CheckAttribute(Topic.Attribute.Required | Topic.Attribute.Config), Is.True);
        Assert.That(topic.CheckAttribute(Topic.Attribute.DB), Is.False);
      }
      Assert.That(root.Get("/$YS/DevicePLC", false), Is.Null);
      Assert.That(root.Get("/$YS/TWI", false), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NewValuesWinWhenBothPathsExist(bool value) {
      SavedBool("/$YS/DevicePLC/verbose", !value);
      SavedBool("/$YS/TWI/verbose", value);
      var plc = SavedBool(OwnerPath + "DPLC_verbose", value);
      var twi = SavedBool(OwnerPath + "TWI_verbose", !value);
      Tick();
      _plugin.Configure();
      Assert.That(MQTT_SNPl.verbosePlc, Is.EqualTo(value));
      Assert.That(MQTT_SNPl.verboseTwi, Is.EqualTo(!value));
      Tick();
      Assert.That(root.Get(OwnerPath + "DPLC_verbose", false), Is.SameAs(plc));
      Assert.That(root.Get(OwnerPath + "TWI_verbose", false), Is.SameAs(twi));
      Assert.That(root.Get("/$YS/DevicePLC/verbose", false), Is.Null);
      Assert.That(root.Get("/$YS/TWI/verbose", false), Is.Null);
    }

    [Test]
    public void InvalidDestinationUsesSavedOldValue() {
      SavedBool("/$YS/DevicePLC/verbose", false);
      var destination = SavedBool(OwnerPath + "DPLC_verbose", true);
      destination.SetState(Str("not a boolean"));
      Tick();
      _plugin.Configure();
      Assert.That(MQTT_SNPl.verbosePlc, Is.False);
      Tick();
      Assert.That(root.Get(OwnerPath + "DPLC_verbose", false), Is.SameAs(destination));
      Assert.That(destination.GetState().ValueType, Is.EqualTo(NiL.JS.Core.JSValueType.Boolean));
      Assert.That(destination.GetState().AsBool(true), Is.False);
      Assert.That(destination.CheckAttribute(Topic.Attribute.Required | Topic.Attribute.Config), Is.True);
      Assert.That(destination.CheckAttribute(Topic.Attribute.DB), Is.False);
      Assert.That(root.Get("/$YS/DevicePLC/verbose", false), Is.Null);
    }

    [Test]
    public void UnrelatedLegacyFolderContentsArePreserved() {
      SavedBool("/$YS/DevicePLC/verbose", false);
      SavedBool("/$YS/TWI/verbose", false);
      var other = root.Get("/$YS/DevicePLC/other", true);
      other.SetState(Num(42));
      var folder = root.Get("/$YS/TWI", false);
      folder.SetState(Str("user state"));
      Tick();
      _plugin.Configure();
      Tick();
      Assert.That(root.Get("/$YS/DevicePLC/other", false), Is.SameAs(other));
      Assert.That(other.GetState().AsInt(0), Is.EqualTo(42));
      Assert.That(root.Get("/$YS/TWI", false), Is.SameAs(folder));
      Assert.That(folder.GetState().AsString(null), Is.EqualTo("user state"));
      Assert.That(root.Get("/$YS/TWI/verbose", false), Is.Null);
    }

    [Test]
    public void SettingsStayLiveUntilStopAndAreReappliedOnRestart() {
      var plc = SavedBool(OwnerPath + "DPLC_verbose", false);
      var twi = SavedBool(OwnerPath + "TWI_verbose", true);
      Tick();
      _plugin.Configure();
      _plugin.Configure();
      Tick();
      plc.SetState(true);
      twi.SetState(false);
      Tick();
      Assert.That(MQTT_SNPl.verbosePlc, Is.True);
      Assert.That(MQTT_SNPl.verboseTwi, Is.False);
      _plugin.Stop();
      plc.SetState(false);
      twi.SetState(true);
      Tick();
      Assert.That(MQTT_SNPl.verbosePlc, Is.True, "подписка освобождена");
      Assert.That(MQTT_SNPl.verboseTwi, Is.False);
      _plugin.Configure();
      Assert.That(MQTT_SNPl.verbosePlc, Is.False);
      Assert.That(MQTT_SNPl.verboseTwi, Is.True);
      Assert.That(_plugin._gates, Is.Empty, "настройки не открывают UDP/UART");
    }
  }
}
