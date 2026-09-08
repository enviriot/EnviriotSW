///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace X13.AswIPC {
  /// <summary>The listening endpoint.
  ///
  /// asw_ws_gateway always dials out to its sources and retries every 2s
  /// forever, so this end only ever accepts. That is why there is no timer, no
  /// backoff and no connection state machine here: recovery is entirely the far
  /// end's business.
  ///
  /// The listener exists as soon as the plugin starts, before any mount is
  /// configured. A gateway that connects to an unconfigured plugin gets a
  /// working but empty conversation, which is a far better symptom than a
  /// refused connection - it separates "the plugin is not running" from "the
  /// plugin has nothing mapped".</summary>
  internal class IpcServer : IDisposable {
    private readonly List<IpcLink> _links = new List<IpcLink>();
    private readonly AswIPCPl _pl;
    private TcpListener _listener;
    private Thread _accept;
    private volatile bool _running = true;

    public readonly string Signature;

    public bool Verbose { get { return _pl.verbose; } }

    public IpcServer(AswIPCPl pl, IPAddress bind, int port) {
      _pl = pl;
      Signature = bind.ToString() + ":" + port.ToString();
      try {
        _listener = new TcpListener(bind, port);
        // Without this a restart fails for the length of TIME_WAIT and the
        // plugin would come back with a dead endpoint and no obvious reason.
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket,
                                         SocketOptionName.ReuseAddress, true);
        _listener.Start();
      }
      catch(Exception ex) {
        Log.Error("AswIPC cannot listen on {0} - {1}", Signature, ex.Message);
        _listener = null;
        return;
      }
      Log.Info("AswIPC listening on {0}", Signature);
      _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "AswIPC accept " + Signature };
      _accept.Start();
    }

    #region dispatch, delegated to the plugin
    public void Subscriptions(IpcLink link) { _pl.SendSubscriptions(link); }
    public void Snapshot(IpcLink link) { _pl.Snapshot(link); }
    public bool OnCommand(string path, string payload) { return _pl.OnCommand(path, payload); }
    public void OnPublish(string path, string payload) { _pl.OnPublish(path, payload); }
    #endregion dispatch

    #region outbound
    /// <summary>Our own state, to whoever subscribed to that path.</summary>
    public void Publish(string path, string payload) {
      foreach(var l in Snap()) {
        l.Publish(path, payload);
      }
    }

    /// <summary>A request of ours, going up. Unlike a publication this is not
    /// filtered by the peer's subscriptions: the gateway routes it by our
    /// cmd_prefix, and whether the gateway happens to subscribe to the request
    /// tree has nothing to do with it.</summary>
    public void Request(string path, string payload) {
      var snap = Snap();
      if(snap.Length == 0) {
        Log.Warning("AswIPC: no connection, request {0} dropped", path);
        return;
      }
      foreach(var l in snap) {
        l.Send("CMD\t" + path + "\t" + payload + "\n");
      }
    }

    /// <summary>Snapshot the list and release the lock before touching a
    /// socket: holding it across a blocking write is how two of these threads
    /// would end up waiting on each other.</summary>
    private IpcLink[] Snap() {
      lock(_links) {
        return _links.ToArray();
      }
    }
    #endregion outbound

    private void AcceptLoop() {
      while(_running) {
        TcpClient tcp;
        try {
          tcp = _listener.AcceptTcpClient();
        }
        catch(Exception) {
          if(_running) {
            Thread.Sleep(200);
          }
          continue;
        }
        var link = new IpcLink(this, tcp);
        lock(_links) {
          _links.Add(link);
        }
        Log.Info("AswIPC {0} connected", link.Peer);
        _pl.SendSubscriptions(link);
      }
    }

    public void Remove(IpcLink link) {
      bool had;
      lock(_links) {
        had = _links.Remove(link);
      }
      if(had) {
        Log.Info("AswIPC {0} disconnected", link.Peer);
      }
    }

    /// <summary>Drop every connection. The protocol has no UNSUB, so this is
    /// how a changed set of mounts is retired: subscriptions belong to a
    /// connection, and the gateway restates everything within a couple of
    /// seconds of coming back.</summary>
    public void DropLinks(string why) {
      IpcLink[] snap;
      lock(_links) {
        snap = _links.ToArray();
        _links.Clear();
      }
      if(snap.Length > 0) {
        Log.Info("AswIPC {0}: dropping {1} connection(s), {2}", Signature, snap.Length, why);
        foreach(var l in snap) {
          l.Dispose();
        }
      }
    }

    public void Dispose() {
      _running = false;
      DropLinks("shutting down");
      var l = Interlocked.Exchange(ref _listener, null);
      if(l != null) {
        try { l.Stop(); } catch(Exception) { }
      }
    }
  }
}
