using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using X13.MQTT;

namespace X13.Tests {
  [TestFixture]
  public class MqStreamerTests : RepoTestBase {
    [Test]
    public void Send_DoesNotLeaveAMessageBehindWhenTheWriterBecomesIdle() {
      var listener = new TcpListener(IPAddress.Loopback, 0);
      listener.Start();
      try {
        using(var client = new TcpClient()) {
          client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
          using(var peer = listener.AcceptTcpClient())
          using(var streamer = new MqStreamer(client, _ => { }, () => { }))
          using(var firstWritten = new ManualResetEventSlim())
          using(var secondWritten = new ManualResetEventSlim()) {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var queue = (Queue<MqMessage>)typeof(MqStreamer).GetField("_sendQ", flags).GetValue(streamer);
            var complete = typeof(MqStreamer).GetMethod("SendProcess", flags);
            IAsyncResult first = null, second = null;
            int writes = 0;
            AsyncCallback capture = result => {
              if(Interlocked.Increment(ref writes) == 1) {
                first = result;
                firstWritten.Set();
              } else {
                second = result;
                secondWritten.Set();
              }
            };
            // Завершения настоящих TCP-записей вызываем вручную, чтобы зафиксировать порядок гонки.
            typeof(MqStreamer).GetField("_sendCB", flags).SetValue(streamer, capture);
            streamer.Send(new MqPingReq());
            Assert.That(firstWritten.Wait(2000), Is.True);

            Exception senderError = null;
            var message = new MqPingReq();
            var sender = new Thread(() => {
              try { streamer.Send(message); }
              catch(Exception ex) { senderError = ex; }
            }) { IsBackground = true };
            lock(queue) {
              sender.Start();
              Assert.That(SpinWait.SpinUntil(
                () => (sender.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000), Is.True);
              // Отправитель уже пришёл к занятой очереди; текущее завершение видит её ещё пустой.
              complete.Invoke(streamer, new object[] { first });
            }
            Assert.That(sender.Join(2000), Is.True);
            Assert.That(senderError, Is.Null);
            Assert.That(secondWritten.Wait(1000), Is.True,
              "The queued packet never started after the writer became idle");
            complete.Invoke(streamer, new object[] { second });

            peer.ReceiveTimeout = 2000;
            var wire = peer.GetStream();
            Assert.That(wire.ReadByte(), Is.EqualTo(0xc0));
            Assert.That(wire.ReadByte(), Is.Zero);
            Assert.That(wire.ReadByte(), Is.EqualTo(0xc0));
            Assert.That(wire.ReadByte(), Is.Zero);
            Assert.That(writes, Is.EqualTo(2));
          }
        }
      }
      finally {
        listener.Stop();
      }
    }
  }
}
