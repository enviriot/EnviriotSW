///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace X13.AswIPC {
  /// <summary>One accepted connection from asw_ws_gateway.
  ///
  /// The protocol is SW/Server/include/asw/ipc_protocol.h: UTF-8 lines ending
  /// in '\n', fields separated by '\t'. This side is a *source* in that
  /// document's terms - the same role asw_core and tools/asw_rot_sim play - so
  /// it answers SUB/GETSNAP/CMD and may itself send PUB/SUB/CMD upward.
  ///
  /// The gateway is always the one that connects; it retries every 2s forever.
  /// That is why this class only ever accepts, never dials, and why losing a
  /// connection needs no recovery logic here at all.</summary>
  internal class IpcLink : IDisposable {
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly IpcServer _srv;
    private readonly Thread _thread;
    private readonly object _sendLock = new object();
    /// <summary>Prefixes this peer asked for with SUB. A publication is sent
    /// only if one of them matches - the same prefix rule the gateway applies
    /// in the other direction, and the reason a peer that never subscribes is
    /// never sent anything it would answer with "ERR unknown".</summary>
    private readonly List<string> _subs = new List<string>();
    private volatile bool _running = true;

    public readonly string Peer;

    public IpcLink(IpcServer srv, TcpClient tcp) {
      _srv = srv;
      _tcp = tcp;
      _tcp.NoDelay = true;      // short request/response lines; Nagle only adds latency
      _stream = tcp.GetStream();
      Peer = tcp.Client.RemoteEndPoint == null ? "?" : tcp.Client.RemoteEndPoint.ToString();
      _thread = new Thread(Run) { IsBackground = true, Name = "AswIPC " + Peer };
      _thread.Start();
    }

    public void Send(string line) {
      byte[] buf = Encoding.UTF8.GetBytes(line);
      lock(_sendLock) {
        try {
          _stream.Write(buf, 0, buf.Length);
          _stream.Flush();
        }
        catch(Exception ex) {
          if(_running) {
            Log.Warning("AswIPC {0} send failed - {1}", Peer, ex.Message);
          }
          Close();
        }
      }
    }

    /// <summary>Publish, but only to a peer that asked for this path.</summary>
    public void Publish(string path, string payload) {
      bool wanted = false;
      lock(_subs) {
        for(int i = 0; i < _subs.Count; i++) {
          if(path.StartsWith(_subs[i], StringComparison.Ordinal)) { wanted = true; break; }
        }
      }
      if(wanted) {
        Send("PUB\t" + path + "\t" + payload + "\n");
      }
    }

    private void Run() {
      var buf = new byte[4096];
      var acc = new MemoryStream();
      try {
        while(_running) {
          int n = _stream.Read(buf, 0, buf.Length);
          if(n <= 0) {
            break;
          }
          for(int i = 0; i < n; i++) {
            if(buf[i] == (byte)'\n') {
              string line = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
              acc.SetLength(0);
              // A '\r' would be invisible in the log and would silently become
              // part of the last field's value.
              Dispatch(line.TrimEnd('\r'));
            } else {
              // A line this long is not this protocol; the far end is broken or
              // hostile, and growing without bound is the wrong answer to both.
              if(acc.Length > 8192) {
                Log.Warning("AswIPC {0} - over-long line, dropping connection", Peer);
                Close();
                return;
              }
              acc.WriteByte(buf[i]);
            }
          }
        }
      }
      catch(Exception ex) {
        if(_running) {
          Log.Debug("AswIPC {0} - {1}", Peer, ex.Message);
        }
      }
      Close();
    }

    private void Dispatch(string line) {
      if(line.Length == 0) {
        return;
      }
      string[] f = line.Split('\t');
      switch(f[0]) {
      case "SUB":
        // The gateway sends "SUB\t/export/out/#" right after connecting. The
        // '#' is dropped rather than interpreted: matching is by prefix on
        // both sides of this link, and neither has a wildcard engine.
        if(f.Length >= 2) {
          string p = f[1];
          if(p.EndsWith("#", StringComparison.Ordinal)) {
            p = p.Substring(0, p.Length - 1);
          }
          lock(_subs) {
            if(!_subs.Contains(p)) {
              _subs.Add(p);
            }
          }
          Send("OK\n");
          // Deliver current values, not only future changes.
          _srv.Snapshot(this);
        } else {
          Send("ERR\tbad args\n");
        }
        break;
      case "GETSNAP":
        _srv.Snapshot(this);
        Send("OK\n");
        break;
      case "CMD":
        // A request addressed to us: the gateway routed it here because our
        // req_prefix covers the path (rotator commands from the web UI).
        if(f.Length >= 3) {
          Send(_srv.OnCommand(f[1], f[2]) ? "OK\n" : "ERR\tno mount\n");
        } else {
          Send("ERR\tbad cmd\n");
        }
        break;
      case "PUB":
        // State we subscribed to (console and Remote-node status). No reply -
        // a publication is not a request.
        if(f.Length >= 3) {
          _srv.OnPublish(f[1], f[2]);
        }
        break;
      case "OK":
      case "ERR":
        // Replies to what WE sent (SUB, CMD). Answering them would bounce a
        // line back for every acknowledgement and fill both logs with a
        // conversation neither side is having.
        if(_srv.Verbose) {
          Log.Debug("AswIPC {0} reply {1}", Peer, line);
        }
        break;
      default:
        Send("ERR\tunknown\n");
        break;
      }
    }

    private void Close() {
      if(!_running) {
        return;
      }
      _running = false;
      try { _tcp.Close(); } catch(Exception) { }
      _srv.Remove(this);
    }

    public void Dispose() {
      Close();
    }
  }
}
