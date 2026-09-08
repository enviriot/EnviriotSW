///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace X13.AntGen {
  /// <summary>One connection to an Antenna Genius.
  ///
  /// Strictly one request in flight at a time. That is not a simplification —
  /// it is what the device does: across 1304 captured responses there was
  /// exactly one per request and never an unsolicited frame. A reply that does
  /// not match the request we sent therefore means the stream has gone wrong,
  /// and the connection is dropped rather than resynchronised by guesswork.
  ///
  /// One exception, found on hardware rather than in a capture: a SetAntenna
  /// the device refuses (an antenna outside the port's current band) answers
  /// CODE_SET_ANT_NAK, not CODE_SET_ANT - a second legitimate reply, not a
  /// framing error. Treating it as one, before this was found, dropped and
  /// reconnected the link on every refused write; see EnforceAntenna() and
  /// the Exchange(int[]) overload.
  ///
  /// We dial out (unlike AswIPC, which listens), so reconnection is our
  /// problem: a fixed retry, and no state carried across a reconnect.
  ///
  /// One write happens here: after every status poll, each physical port is
  /// asked what antenna it SHOULD show (IAgSink.DesiredAntenna, per port) and,
  /// if that differs from what the device just reported and the port is not
  /// transmitting, AgProto.SetAntenna() is sent — see EnforceAntenna(). Nothing
  /// else is ever written: Exchange() still goes through AgProto.Request()'s
  /// whitelist for every other code, so a mistake elsewhere in this file still
  /// cannot reach the wire as a command.</summary>
  internal class AgClient : IDisposable {
    /// <summary>Long enough that a busy device is not mistaken for a dead one,
    /// short enough that a dead one is noticed within one operator action.</summary>
    private const int READ_TIMEOUT_MS = 3000;
    private const int RETRY_MS = 5000;

    private readonly string _host;
    private readonly int _port;
    private readonly int _pollMs;
    private readonly IAgSink _sink;
    private readonly Thread _thread;
    /// <summary>The live socket, so Dispose can shut it from another thread.
    /// The device accepts one client at a time, so "stopped" has to mean the
    /// connection is gone now, not once a blocking read times out. */</summary>
    private TcpClient _tcp;
    private volatile bool _running = true;

    public string Signature { get { return _host + ":" + _port.ToString(); } }

    public AgClient(IAgSink sink, string host, int port, int pollMs) {
      _sink = sink;
      _host = host;
      _port = port;
      _pollMs = pollMs < 100 ? 100 : (pollMs > 60000 ? 60000 : pollMs);
      _thread = new Thread(Run) { IsBackground = true, Name = "AntGen " + Signature };
      _thread.Start();
    }

    private void Run() {
      while(_running) {
        TcpClient tcp = null;
        try {
          tcp = new TcpClient();
          tcp.NoDelay = true;
          Interlocked.Exchange(ref _tcp, tcp);
          if(!_running) {
            break;                       /* stopped while we were setting up */
          }
          tcp.Connect(_host, _port);
          var stream = tcp.GetStream();
          stream.ReadTimeout = READ_TIMEOUT_MS;
          var rd = new AgFrameReader();
          Log.Info("AntGen {0} connected", Signature);
          Session(stream, rd);
        }
        catch(Exception ex) {
          if(_running) {
            Log.Debug("AntGen {0} - {1}", Signature, ex.Message);
          }
        }
        finally {
          Interlocked.CompareExchange(ref _tcp, null, tcp);
          if(tcp != null) {
            try { tcp.Close(); } catch(Exception) { }
          }
        }
        if(_running) {
          _sink.AgLink(false, null);
          Log.Info("AntGen {0} disconnected, retrying", Signature);
          for(int i = 0; i < RETRY_MS / 100 && _running; i++) {
            Thread.Sleep(100);
          }
        }
      }
    }

    private void Session(NetworkStream stream, AgFrameReader rd) {
      /* Tables first, in the vendor's own order. A device that answers these
       * is a device we understand; if any of it fails we never start polling,
       * which keeps a half-known device from looking like a working one. */
      foreach(var q in AgProto.Discovery()) {
        var f = Exchange(stream, rd, AgProto.Request(q.Key, q.Value), q.Key);
        _sink.AgTable(f);
      }
      _sink.AgLink(true, rd);

      var pollReq = AgProto.Request(AgProto.CODE_STATUS, "");
      var next = DateTime.UtcNow;
      while(_running) {
        var f = Exchange(stream, rd, pollReq, AgProto.CODE_STATUS);
        AgStatus st;
        if(AgStatus.TryParse(f.Payload, out st)) {
          _sink.AgStatus(st);
        } else {
          throw new InvalidOperationException("unparsable status: " + f.Payload);
        }
        EnforceAntenna(stream, rd, 1, st.A);
        EnforceAntenna(stream, rd, 2, st.B);
        if(rd.OddPreamble != null) {
          /* Six bytes we have only ever seen as zeros just said something.
           * Worth one loud line: it is the only clue their meaning will give. */
          Log.Warning("AntGen {0}: non-zero frame preamble {1} - the format has more in it than we know",
                      Signature, rd.OddPreamble);
        }
        next = next.AddMilliseconds(_pollMs);
        var wait = next - DateTime.UtcNow;
        if(wait.TotalMilliseconds > _pollMs || wait.TotalMilliseconds < 0) {
          /* Fell behind, or the clock moved. Re-base instead of spinning to
           * catch up: a burst of polls helps nobody and looks like a fault. */
          next = DateTime.UtcNow.AddMilliseconds(_pollMs);
          wait = TimeSpan.FromMilliseconds(_pollMs);
        }
        for(int left = (int)wait.TotalMilliseconds; left > 0 && _running; left -= 100) {
          Thread.Sleep(left > 100 ? 100 : left);
        }
      }
    }

    /// <summary>Checked for both physical ports every poll, independently —
    /// there is no "active port" special case here, since a standing request
    /// is a per-port fact, exactly like the portA/ant and portB/ant it is
    /// named after.
    ///
    /// Two guards, both load-bearing: no request (want == 0), and no write
    /// while transmitting. The second exists because AG has no PTT input of
    /// its own — the hardware interlock runs console -> FLEX, not through
    /// this box — so switching a relay mid-transmission is a hazard nothing
    /// else here guards against. A missed cycle costs one poll interval; a
    /// hot-switched relay does not come back. A port with no band selected
    /// (cur.Band == 0) is skipped too: nothing is plugged in there to
    /// switch.</summary>
    /// <summary>Per port, the antenna id whose rejection was already logged —
    /// so a standing request the device keeps refusing (wrong band, most
    /// likely) gets one warning, not one every poll forever. Reset the moment
    /// `want` changes to anything else, including 0, so a genuinely new
    /// request — even one the operator already tried once before — always
    /// gets its own first attempt logged.</summary>
    private int _rejectedA, _rejectedB;

    private void EnforceAntenna(NetworkStream stream, AgFrameReader rd, int port, AgPort cur) {
      if(cur.Band == 0 || cur.Tx) {
        return;
      }
      int want = _sink.DesiredAntenna(port, cur);
      if(want == 0 || want == cur.Antenna) {
        return;
      }
      var setReq = AgProto.SetAntenna(port, want);
      var ack = Exchange(stream, rd, setReq, new[] { AgProto.CODE_SET_ANT, AgProto.CODE_SET_ANT_NAK });
      if(ack.Op == AgProto.CODE_SET_ANT_NAK) {
        /* The device said no — most likely `want` does not cover the port's
         * current band. Keep retrying every poll (the band may yet change to
         * one it does cover), but only the first refusal is worth a line: at
         * the poll rate this would otherwise be a warning every 500ms for as
         * long as the request stands. */
        bool first = (port == 1 ? _rejectedA : _rejectedB) != want;
        if(port == 1) {
          _rejectedA = want;
        } else {
          _rejectedB = want;
        }
        if(first) {
          Log.Warning("AntGen {0}: antenna {1} refused for port {2} (does not match the current band?) - {3}",
                      Signature, want, port, ack.Payload);
        }
        return;
      }
      if(port == 1) {
        _rejectedA = 0;
      } else {
        _rejectedB = 0;
      }
      Log.Info("AntGen {0}: antenna set port {1} -> {2} ({3})", Signature, port, want, ack.Payload);
    }

    /// <summary>Send a pre-built request, return the response matching
    /// expectOp. Anything else on the wire is a protocol error, not something
    /// to skip past — including a bad reply to the one write this client
    /// makes: a mismatched response means the framing itself is suspect, and
    /// that call is exactly as disqualifying for a write as for a read.
    ///
    /// Takes raw bytes rather than (code, payload) so the caller decides how
    /// they were built: through AgProto.Request(), which enforces the
    /// read-only whitelist, or through AgProto.SetAntenna(), the one
    /// deliberate exception to it.</summary>
    private AgFrame Exchange(NetworkStream stream, AgFrameReader rd, byte[] req, int expectOp) {
      return Exchange(stream, rd, req, new[] { expectOp });
    }

    /// <summary>Same contract, for the one exchange with more than one valid
    /// outcome: a SetAntenna may come back as CODE_SET_ANT (applied) or
    /// CODE_SET_ANT_NAK (rejected — see that constant). Both are legitimate
    /// answers to the request we sent, not framing errors; anything outside
    /// acceptOps still is.</summary>
    private AgFrame Exchange(NetworkStream stream, AgFrameReader rd, byte[] req, int[] acceptOps) {
      stream.Write(req, 0, req.Length);
      stream.Flush();
      var buf = new byte[2048];
      var deadline = DateTime.UtcNow.AddMilliseconds(READ_TIMEOUT_MS);
      while(true) {
        if(DateTime.UtcNow > deadline) {
          throw new TimeoutException(string.Format("no answer to {0:x4}", acceptOps[0]));
        }
        int n = stream.Read(buf, 0, buf.Length);
        if(n <= 0) {
          throw new InvalidOperationException("connection closed");
        }
        foreach(var f in rd.Feed(buf, n)) {
          bool ok = f.IsResponse;
          if(ok) {
            ok = false;
            for(int i = 0; i < acceptOps.Length; i++) {
              if(f.Op == acceptOps[i]) {
                ok = true;
                break;
              }
            }
          }
          if(!ok) {
            throw new InvalidOperationException(
              string.Format("expected a response to {0:x4}, got {1:x6}", acceptOps[0], f.Code));
          }
          return f;
        }
      }
    }

    public void Dispose() {
      _running = false;
      /* Close the socket from here rather than waiting for the worker to
       * notice. A blocking Connect or Read throws the moment its socket is
       * shut, which turns "up to three seconds" into "now" — and with a device
       * that permits a single client, the operator is switching us off
       * precisely because something else needs the connection. */
      var tcp = Interlocked.Exchange(ref _tcp, null);
      if(tcp != null) {
        try { tcp.Close(); } catch(Exception) { }
      }
      if(!_thread.Join(2000)) {
        /* Say so. A thread still alive here may still hold the connection,
         * and the operator would otherwise sit in front of vendor software
         * that cannot connect, with nothing to explain why. */
        Log.Warning("AntGen {0}: worker did not stop within 2s", Signature);
      }
    }
  }

  /// <summary>Where a client's findings go, and the one decision it asks back.
  /// Kept an interface so the protocol and the socket can be exercised without
  /// Enviriot behind them.</summary>
  internal interface IAgSink {
    void AgTable(AgFrame f);
    void AgStatus(AgStatus st);
    void AgLink(bool up, AgFrameReader rd);

    /// <summary>Called once per poll for each physical port (1 and 2). Return
    /// the standing antenna request for that port, or 0 for "no request" — a
    /// plain lookup in the sink, no table resolution done here or there;
    /// EnforceAntenna() (AgClient) is what decides whether it is safe to act
    /// on right now.</summary>
    int DesiredAntenna(int port, AgPort cur);
  }
}
