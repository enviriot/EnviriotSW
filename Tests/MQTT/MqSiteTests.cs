///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using X13.MQTT;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  [TestFixture]
  public class MqSiteTests : RepoTestBase {
    private MQTTPl _plugin;
    private MqClient _client;
    private TcpListener _listener;
    private TcpClient _broker;
    private NetworkStream _wire;
    private readonly List<MqSite> _sites = new List<MqSite>();

    [SetUp]
    public void StartBroker() {
      _plugin = new MQTTPl();
      _client = null;
      _broker = null;
      _wire = null;
      _sites.Clear();
      _listener = new TcpListener(IPAddress.Loopback, 0);
      _listener.Start();
    }

    [TearDown]
    public void StopBroker() {
      try {
        foreach(var site in _sites) {
          site.Dispose();
        }
        _plugin.Stop();
        if(_client != null) {
          _client.Dispose();
        }
      }
      finally {
        if(_wire != null) {
          _wire.Dispose();
        }
        if(_broker != null) {
          _broker.Close();
        }
        _listener.Stop();
      }
    }

    [Test]
    public void ExactTopic_PublishesOwnerSnapshotAndChangesOnly() {
      var owner = MakeOwner("/bridge", false, true);
      var child = owner.Get("child");
      child.SetState(Num(30));
      Tick();
      CreateSite(owner, "/value");
      Tick();

      AssertPublish("/value", "20");
      child.SetState(Num(31));
      Tick();
      AssertNoPacket();
      owner.SetState(Num(21));
      Tick();
      AssertPublish("/value", "21");
      AssertNoPacket();
    }

    [TestCase("+", false)]
    [TestCase("#", true)]
    public void Wildcards_PreserveTheirLocalScope(string wildcard, bool all) {
      var owner = MakeOwner("/bridge", false, true);
      var child = owner.Get("child");
      var grandchild = child.Get("grandchild");
      child.SetState(Num(30));
      grandchild.SetState(Num(40));
      Tick();
      CreateSite(owner, "/remote/" + wildcard);
      Tick();

      var paths = new List<string>();
      for(int i = 0; i < (all ? 3 : 1); i++) {
        paths.Add(Read<MqPublish>().Path);
      }
      Assert.That(paths, Is.EquivalentTo(all
        ? new[] { "/remote", "/remote/child", "/remote/child/grandchild" }
        : new[] { "/remote/child" }));
      grandchild.SetState(Num(41));
      Tick();
      if(all) {
        AssertPublish("/remote/child/grandchild", "41");
      }
      AssertNoPacket();
    }

    [Test]
    public void SubscribeFlag_TogglesBrokerSubscriptionAndIncomingValues() {
      var owner = MakeOwner("/bridge", true, false);
      var site = CreateSite(owner, "/value");
      Tick();
      AssertSubscribe("/value");

      owner.SetField("MQTT.subscribe", false);
      Tick();
      AssertUnsubscribe("/value");
      Assert.That(_client.Sites, Does.Contain(site));
      site.Publish("/value", "21");
      Tick();
      Assert.That((int)owner.GetState(), Is.EqualTo(20));

      owner.SetField("MQTT.subscribe", true);
      Tick();
      AssertSubscribe("/value");
      ClearEvents();
      site.Publish("/value", "22");
      Tick();
      Assert.That((int)owner.GetState(), Is.EqualTo(22));
      Assert.That(EventOf(owner, EventKind.StateChanged).Author, Is.SameAs(owner));
      AssertNoPacket();
    }

    [Test]
    public void PublishFlag_RestartsSnapshotWithoutResubscribingBroker() {
      var owner = MakeOwner("/bridge", true, false);
      CreateSite(owner, "/value");
      Tick();
      AssertSubscribe("/value");

      owner.SetField("MQTT.publish", true);
      Tick();
      Tick();
      AssertPublish("/value", "20");
      AssertNoPacket();
      owner.SetField("MQTT.publish", false);
      owner.SetState(Num(21));
      Tick();
      AssertNoPacket();

      owner.SetField("MQTT.publish", true);
      Tick();
      Tick();
      AssertPublish("/value", "21");
      AssertNoPacket();
    }

    [Test]
    public void RetainedFlag_AffectsTheNextPublicationWithoutResubscribing() {
      var owner = MakeOwner("/bridge", true, true);
      CreateSite(owner, "/value");
      Tick();
      AssertPublish("/value", "20");
      AssertSubscribe("/value");

      owner.SetField("MQTT.retained", true);
      owner.SetState(Num(21));
      Tick();
      AssertPublish("/value", "21", true);
      AssertNoPacket();
      owner.SetField("MQTT.retained", false);
      owner.SetState(Num(22));
      Tick();
      AssertPublish("/value", "22");
      AssertNoPacket();
    }

    [Test]
    public void WholeMqttField_AppliesAllFlagsTogether() {
      var owner = MakeOwner("/bridge", false, false);
      CreateSite(owner, "/value");
      Tick();
      AssertNoPacket();

      var flags = JSC.JSObject.CreateObject();
      flags["subscribe"] = true;
      flags["publish"] = true;
      flags["retained"] = true;
      owner.SetField("MQTT", flags);
      Tick();
      Tick();
      AssertPublish("/value", "20", true);
      AssertSubscribe("/value");
      AssertNoPacket();

      flags = JSC.JSObject.CreateObject();
      flags["subscribe"] = false;
      flags["publish"] = false;
      flags["retained"] = false;
      owner.SetField("MQTT", flags);
      Tick();
      AssertUnsubscribe("/value");
      owner.SetState(Num(21));
      Tick();
      AssertNoPacket();
    }

    [Test]
    public void PendingSnapshot_UsesTheLatestPublishFlag() {
      var owner = MakeOwner("/bridge", true, false);
      CreateSite(owner, "/value");
      Tick();
      AssertSubscribe("/value");

      owner.SetField("MQTT.publish", true);
      Tick();
      owner.SetField("MQTT.publish", false);
      Tick();
      AssertNoPacket();
    }

    [Test]
    public void SubscribeFlag_ChangedBeforeReadyDoesNotSendAStaleSubscription() {
      var owner = MakeOwner("/bridge", true, true);
      CreateSite(owner, "/value");
      owner.SetField("MQTT.subscribe", false);
      Tick();
      AssertPublish("/value", "20");
      AssertNoPacket();

      owner.SetField("MQTT.subscribe", true);
      Tick();
      AssertSubscribe("/value");
      AssertNoPacket();
    }

    [Test]
    public void OfflineFlagChanges_AreUsedOnReconnect() {
      var owner = MakeOwner("/bridge", true, true);
      CreateSite(owner, "/value");
      Tick();
      AssertPublish("/value", "20");
      AssertSubscribe("/value");

      _client.Restart(null);
      AcceptConnection(false);
      owner.SetField("MQTT.subscribe", false);
      owner.SetField("MQTT.publish", false);
      owner.SetField("MQTT.retained", true);
      Tick();
      Send(new MqConnack(MqConnack.MqttConnectionResponse.Accepted));
      WaitConnected();
      Tick();
      AssertNoPacket();

      owner.SetField("MQTT.subscribe", true);
      Tick();
      AssertSubscribe("/value");
      owner.SetField("MQTT.publish", true);
      Tick();
      Tick();
      AssertPublish("/value", "20", true);
      AssertNoPacket();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Dispose_RemovesTheSiteAndStopsWatchingFlags(bool subscribe) {
      var owner = MakeOwner("/bridge", subscribe, true);
      var site = CreateSite(owner, "/value");
      Tick();
      AssertPublish("/value", "20");
      if(subscribe) {
        AssertSubscribe("/value");
      }

      site.Dispose();
      if(subscribe) {
        AssertUnsubscribe("/value");
      }
      Assert.That(_client.Sites, Does.Not.Contain(site));
      owner.SetField("MQTT.subscribe", Num(1));
      owner.SetField("MQTT.retained", true);
      owner.SetState(Num(21));
      Tick();
      Tick();
      site.Connected();
      site.Publish("/value", "99");
      site.Dispose();
      Tick();
      Assert.That(owner.GetField("MQTT.subscribe").IsNumber, Is.True);
      Assert.That((int)owner.GetState(), Is.EqualTo(21));
      AssertNoPacket();
    }

    [Test]
    public void SharedFilter_IsUnsubscribedOnlyWhenTheLastSiteDisablesIt() {
      var first = MakeOwner("/first", true, false);
      var second = MakeOwner("/second", true, false);
      var firstSite = CreateSite(first, "/shared");
      CreateSite(second, "/shared");
      Tick();
      AssertSubscribe("/shared");
      AssertNoPacket();

      first.SetField("MQTT.subscribe", false);
      Tick();
      AssertNoPacket();
      Assert.That(_client.Sites.Count, Is.EqualTo(2));
      second.SetField("MQTT.subscribe", false);
      Tick();
      AssertUnsubscribe("/shared");

      first.SetField("MQTT.subscribe", true);
      Tick();
      AssertSubscribe("/shared");
      firstSite.Dispose();
      AssertUnsubscribe("/shared");
      Assert.That(_client.Sites.Count, Is.EqualTo(1));
      AssertNoPacket();
    }

    [Test]
    public void UnchangedFlagsAndUnrelatedFields_DoNotChurnSubscriptions() {
      var owner = MakeOwner("/bridge", true, true);
      CreateSite(owner, "/value");
      Tick();
      AssertPublish("/value", "20");
      AssertSubscribe("/value");

      owner.SetField("MQTT.subscribe", true);
      owner.SetField("MQTT.publish", true);
      owner.SetField("MQTT.retained", false);
      owner.SetField("MQTT.extra", Num(1));
      owner.SetField("hint", Str("sensor"));
      Tick();
      Tick();
      AssertNoPacket();
    }

    [Test]
    public void NonBooleanFlags_AreResetToDefaultsWhileRunning() {
      var owner = MakeOwner("/bridge", false, false, true);
      CreateSite(owner, "/value");
      Tick();
      owner.SetField("MQTT.subscribe", Num(1));
      owner.SetField("MQTT.publish", Str("false"));
      owner.SetField("MQTT.retained", Num(1));
      Tick();
      Tick();

      AssertPublish("/value", "20");
      AssertSubscribe("/value");
      Assert.That((bool)owner.GetField("MQTT.subscribe"), Is.True);
      Assert.That((bool)owner.GetField("MQTT.publish"), Is.True);
      Assert.That((bool)owner.GetField("MQTT.retained"), Is.False);
      AssertNoPacket();
    }

    [Test]
    public void DisablingSubscribe_DoesNotDisablePublishing() {
      var owner = MakeOwner("/bridge", true, true);
      CreateSite(owner, "/value");
      Tick();
      AssertPublish("/value", "20");
      AssertSubscribe("/value");
      owner.SetField("MQTT.subscribe", false);
      Tick();
      AssertUnsubscribe("/value");

      owner.SetState(Num(21));
      Tick();
      AssertPublish("/value", "21");
      owner.SetField("MQTT.subscribe", true);
      Tick();
      AssertSubscribe("/value");
      AssertNoPacket();
    }

    [Test]
    public void PluginStop_DisposesTheFlagSubscription() {
      var owner = MakeOwner("/bridge", true, true);
      _plugin.Start();
      owner.SetField("MQTT.uri", Str("mqtt://127.0.0.1:" + Port + "/value"));
      Tick();
      AcceptConnection(true);
      Assert.That(SpinWait.SpinUntil(() => {
        Tick();
        return root.Get("/$YS/MQTT/Connections/127.0.0.1").GetState().AsString(null) == "Connected";
      }, 2000), Is.True);
      Tick();
      AssertPublish("/value", "20");
      AssertSubscribe("/value");

      _plugin.Stop();
      owner.SetField("MQTT.subscribe", Num(1));
      Tick();
      Tick();
      Assert.That(owner.GetField("MQTT.subscribe").IsNumber, Is.True);
    }

    private int Port { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }

    private Topic MakeOwner(string path, bool subscribe, bool publish, bool retained = false) {
      var owner = root.Get(path);
      owner.SetState(Num(20));
      owner.SetField("MQTT.subscribe", subscribe);
      owner.SetField("MQTT.publish", publish);
      owner.SetField("MQTT.retained", retained);
      Tick();
      return owner;
    }

    private MqSite CreateSite(Topic owner, string remote) {
      if(_client == null) {
        _client = new MqClient(_plugin, "127.0.0.1", Port, null, null);
        AcceptConnection(true);
        WaitConnected();
      }
      var site = new MqSite(_plugin, _client, owner, new Uri("mqtt://127.0.0.1:" + Port + remote));
      _sites.Add(site);
      return site;
    }

    private void AcceptConnection(bool accept) {
      if(_broker != null) {
        _broker.Close();
      }
      var pending = _listener.BeginAcceptTcpClient(null, null);
      using(var ready = pending.AsyncWaitHandle) {
        Assert.That(ready.WaitOne(2000), Is.True, "MQTT client did not connect");
        _broker = _listener.EndAcceptTcpClient(pending);
      }
      _wire = _broker.GetStream();
      _wire.ReadTimeout = 2000;
      Read<MqConnect>();
      if(accept) {
        Send(new MqConnack(MqConnack.MqttConnectionResponse.Accepted));
      }
    }

    private void WaitConnected() {
      Assert.That(SpinWait.SpinUntil(() => _client.status == MqClient.Status.Connected, 2000), Is.True);
    }

    private void Send(MqMessage message) {
      using(var buffer = new MemoryStream()) {
        message.Serialise(buffer);
        var bytes = buffer.ToArray();
        _wire.Write(bytes, 0, bytes.Length);
      }
    }

    private T Read<T>() where T : MqMessage {
      int header = _wire.ReadByte();
      Assert.That(header, Is.GreaterThanOrEqualTo(0), "MQTT connection closed before the expected packet");
      uint length = 0;
      int shift = 0, digit;
      do {
        digit = _wire.ReadByte();
        Assert.That(digit, Is.GreaterThanOrEqualTo(0));
        Assert.That(shift, Is.LessThan(28), "Invalid MQTT remaining length");
        length |= (uint)(digit & 0x7f) << shift;
        shift += 7;
      } while((digit & 0x80) != 0);
      var payload = new byte[checked((int)length)];
      int offset = 0;
      while(offset < payload.Length) {
        int read = _wire.Read(payload, offset, payload.Length - offset);
        Assert.That(read, Is.GreaterThan(0));
        offset += read;
      }
      MqMessage message;
      using(var buffer = new MemoryStream(payload)) {
        message = MqMessage.Parse((byte)header, length, buffer);
      }
      if(message is MqSubscribe subscribe) {
        var ack = new MqSuback { MessageID = subscribe.MessageID };
        foreach(var entry in subscribe.list) {
          ack.Add(QoS.AtMostOnce);
        }
        Send(ack);
      } else if(message is MqUnsubscribe unsubscribe) {
        Send(new MqUnsuback { MessageID = unsubscribe.MessageID });
      }
      Assert.That(message, Is.InstanceOf<T>());
      return (T)message;
    }

    private void AssertPublish(string path, string value, bool retained = false) {
      var message = Read<MqPublish>();
      Assert.That(message.Path, Is.EqualTo(path));
      Assert.That(message.Payload, Is.EqualTo(value));
      Assert.That(message.Retained, Is.EqualTo(retained));
    }

    private void AssertSubscribe(string path) {
      var message = Read<MqSubscribe>();
      Assert.That(message.list.Select(z => z.Key), Is.EqualTo(new[] { path }));
      Assert.That(message.list[0].Value, Is.EqualTo(QoS.AtMostOnce));
    }

    private void AssertUnsubscribe(string path) {
      Assert.That(Read<MqUnsubscribe>().list, Is.EqualTo(new[] { path }));
    }

    private void AssertNoPacket() {
      Assert.That(SpinWait.SpinUntil(() => _wire.DataAvailable, 150), Is.False, "Unexpected MQTT packet");
    }
  }
}
