///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using X13.Repository;

namespace X13.EsBroker {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 8)]
  [System.ComponentModel.Composition.ExportMetadata("name", "EsBroker")]
  internal class EsBrokerPl : IPlugModul {
    #region internal Members
    private TcpListener _tcp;
    private System.Collections.Concurrent.ConcurrentBag<EsConnection> _connections;
    private System.Collections.Concurrent.ConcurrentBag<EsMessage> _msgs;
    private Topic _owner;
    private Topic _verbose;

    private void Connect(IAsyncResult ar) {
      if (_tcp == null) return;
      try {
        TcpClient c = _tcp.EndAcceptTcpClient(ar);
        _connections.Add(new EsConnection(this, c));
      }
      catch (ObjectDisposedException) {
        return;   // Socket allready closed
      }
      catch(NullReferenceException) {
        return;   // Socket allready destroyed
      }
      catch(SocketException) {
      }
      _tcp.BeginAcceptTcpClient(new AsyncCallback(Connect), null);
    }
    internal void AddRMsg(EsMessage msg) {
      _msgs.Add(msg);
    }
    #endregion internal Members

    public EsBrokerPl() {
      _connections = new System.Collections.Concurrent.ConcurrentBag<EsConnection>();
      _msgs = new System.Collections.Concurrent.ConcurrentBag<EsMessage>();
    }

    public bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }
    #region IPlugModul Members
    public void Init() {
      _tcp = new TcpListener(IPAddress.Any, 10013);
      _tcp.Start();
    }
    public void Start() {
      _owner = Topic.root.Get("/$YS/ES");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
        //#if DEBUG
        //        _verbose.SetState(true);
        //#else
        _verbose.SetState(false);
        //#endif
      }

      _tcp.BeginAcceptTcpClient(new AsyncCallback(Connect), null);
    }
    public void Tick() {
      EsMessage msg;
      while(_msgs.TryTake(out msg)) {
        if(msg.Count == 0) {
          continue;
        }
        try {
          if(msg[0].ValueType == JSC.JSValueType.String) {
            var key = msg[0].Value as string;
            var a = new JSC.JSValue[msg.Count - 1];
            for(int i = 1; i < msg.Count; i++) {
              a[i - 1] = msg[i];
            }
            RPC.Call(key, a);
          }
        }
        catch(Exception ex) {
          if(verbose) {
            Log.Warning("{0} - {1}", msg, ex);
          }
        }
      }
    }
    public void Stop() {
      if(_tcp == null) {
        return;
      }
      foreach(var cl in _connections.ToArray()) {
        try {
          cl.Dispose();
        }
        catch(Exception) {
        }
      }
      _tcp.Stop();
      _tcp = null;
    }

    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/ES", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/ES", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members
  }
}
