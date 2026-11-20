using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using X13.Periphery;
using X13.Plc;

namespace X13.Tests {
  [TestFixture]
  public class NodeAvailabilityTests : RepoTestBase {
    private sealed class Gate : IMsGate {
      public readonly List<MsMessage> Sent = new List<MsMessage>();
      public byte gwIdx { get { return 1; } }
      public byte gwRadius { get { return 1; } }
      public string name { get { return "fixture"; } }
      public string Addr2If(byte[] addr) { return "fixture"; }
      public void SendGw(byte[] addr, MsMessage msg) { Sent.Add(msg); }
      public void SendGw(MsDevice dev, MsMessage msg) { Sent.Add(msg); }
      public void Tick() { }
      public void Stop() { }
    }
    private static MsPublish Report(ushort id, byte[] data, ushort message = 1, bool duplicate = false) {
      var bytes = new byte[7 + data.Length];
      bytes[0] = (byte)bytes.Length; bytes[1] = 0x0C; bytes[2] = (byte)(0x31 | (duplicate ? 0x80 : 0));
      bytes[3] = (byte)(id >> 8); bytes[4] = (byte)id; bytes[5] = (byte)(message >> 8); bytes[6] = (byte)message;
      Array.Copy(data, 0, bytes, 7, data.Length);
      return new MsPublish(bytes, 0, bytes.Length);
    }
    [Test]
    public void ReportsAreAcknowledgedUnsignedReadOnlyAndNeverEchoed() {
      var node = root.Get("/dev/availability", true);
      var gate = new Gate();
      var device = new MsDevice(new MQTT_SNPl(), node) { _gate = gate, State = State.Connected };
      Tick(); gate.Sent.Clear();
      try {
        byte[] mask = { 53, 240, 239, 14, 255 };
        device.ProcessInPacket(Report(0xFFD8, mask));
        device.ProcessInPacket(Report(0xFFD9, new byte[] { 254 }, 2));
        Tick();
        Assert.That(PlcCapabilities.ReadPins(node.GetField(PlcCapabilities.PinsField)), Is.EqualTo(mask));
        Assert.That(node.GetField(PlcCapabilities.UartsField).AsInt(-1), Is.EqualTo(254));
        Assert.That(node.GetField("mi.MQTT-SN.mi.AvailablePins.attr").AsInt(0), Is.EqualTo(2));
        Assert.That(gate.Sent.OfType<MsPubAck>().Select(a => a.retCode), Is.EqualTo(new[] { MsReturnCode.Accepted, MsReturnCode.Accepted }));
        Assert.That(gate.Sent.OfType<MsPublish>(), Is.Empty);
        Assert.That(node.children, Is.Empty);
        device.ProcessInPacket(Report(0xFFD8, new byte[4], 3));
        device.ProcessInPacket(Report(0xFFD9, new byte[2], 4));
        Tick();
        Assert.That(PlcCapabilities.ReadPins(node.GetField(PlcCapabilities.PinsField)), Is.EqualTo(mask));
        Assert.That(node.GetField(PlcCapabilities.UartsField).AsInt(-1), Is.EqualTo(254));
        Assert.That(gate.Sent.OfType<MsPubAck>().Last().retCode, Is.EqualTo(MsReturnCode.InvalidTopicId));
        device.ProcessInPacket(Report(0xFFD8, new byte[5], 5));
        Tick();
        Assert.That(PlcCapabilities.ReadPins(node.GetField(PlcCapabilities.PinsField)), Is.EqualTo(new byte[5]));
        device.ProcessInPacket(Report(0xFFD8, mask, 5, true));
        Tick();
        Assert.That(PlcCapabilities.ReadPins(node.GetField(PlcCapabilities.PinsField)), Is.EqualTo(new byte[5]), "duplicate is acknowledged without applying it again");
        var connect = new byte[] { 7, 4, 4, 1, 0, 30, 65 };
        device.Connect(new MsConnect(connect, 0, connect.Length));
        device.ProcessInPacket(Report(0xFFD8, mask, 5, true));
        Tick();
        Assert.That(PlcCapabilities.ReadPins(node.GetField(PlcCapabilities.PinsField)), Is.EqualTo(mask), "reconnect starts a new receive history");
        Assert.That(X13.WebUI.PlcReportFields.RejectWrite(node, PlcCapabilities.PinsField, new ByteArray(new byte[5])), Is.True);
        Assert.That(X13.WebUI.PlcReportFields.RejectWrite(node, "MQTT-SN", JsLib.ParseJson("{}")), Is.True);
        Assert.That(X13.WebUI.PlcReportFields.RejectWrite(node, "MQTT-SN.SleepTime", 10), Is.False);
        Assert.That(X13.WebUI.ManifestRpcDispatcher.Execute(node, PlcCapabilities.PinsField, "delete", null).Ok, Is.False);
        Assert.That(root.Get("/dev/other", true).GetField(PlcCapabilities.PinsField).Defined, Is.False);
      } finally { device.Stop(); }
    }
    [Test]
    public void ScannerRecordsUseExistingInfoReceivePath() {
      var node = root.Get("/dev/log-stream", true);
      var device = new MsDevice(new MQTT_SNPl(), node) { _gate = new Gate(), State = State.Connected };
      using(var received = new ManualResetEventSlim()) {
        Action<LogLevel, DateTime, string> handler = (level, time, text) => {
          if(level == LogLevel.Info && text.Contains("TWI0 addr=118")) received.Set();
        };
        Log.Write += handler;
        try {
          device.ProcessInPacket(Report(0xFFE1, Encoding.ASCII.GetBytes("TWI0 addr=118")));
          Assert.That(received.Wait(3000), Is.True);
        } finally { Log.Write -= handler; device.Stop(); }
      }
    }
  }
}
