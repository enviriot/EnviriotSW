///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using X13.Repository;
using WebSocketSharp.Server;
using System.IO;
using static System.Collections.Specialized.BitVector32;

namespace X13.EsBroker {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 8)]
  [System.ComponentModel.Composition.ExportMetadata("name", "EsBroker")]
  internal class EsBrokerPl : IPlugModul {
    private readonly System.Collections.Concurrent.ConcurrentBag<EsConnection> _connections;
    private readonly System.Collections.Concurrent.ConcurrentBag<EsMessage> _msgs;
    private Topic _owner;
    private Topic _verbose;
    public EsBrokerPl() {
      _connections = new System.Collections.Concurrent.ConcurrentBag<EsConnection>();
      _msgs = new System.Collections.Concurrent.ConcurrentBag<EsMessage>();
    }
    private void OnConnect(Func<Action<EsMessage>, IEsSocket> s_fab) {
      _connections.Add(new EsConnection(this, s_fab));
    }
    internal void AddRMsg(EsMessage msg) {
      _msgs.Add(msg);
    }
    public bool Verbose {
      get {
        return _verbose != null && _verbose.GetState().As<bool>();
      }
    }

    #region IPlugModul Members
    public void Init() {
    }
    public void Start() {
      _owner = Topic.root.Get("/$YS/ES");
      _verbose = _owner.Get("verbose");
      if (_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
        _verbose.SetState(false);
      }
      int tcpPort = GetOrDefault(_owner.Get("TCP"), 10013);
      if (tcpPort > 0) {
        EsSocketTCP.Start(tcpPort, _verbose, OnConnect);
      }
      int wsPort = GetOrDefault(_owner.Get("WebSocket"), 3128);
      if (wsPort > 0) {
        EsSocketWS.Start(wsPort, _verbose, OnConnect); 
      }
    }
    public void Tick() {
      while (_msgs.TryTake(out EsMessage msg)) {
        if (msg.Count == 0) {
          continue;
        }
        try {
          if (msg[0].ValueType == JSC.JSValueType.String) {
            var key = msg[0].Value as string;
            var a = new JSC.JSValue[msg.Count - 1];
            for (int i = 1; i < msg.Count; i++) {
              a[i - 1] = msg[i];
            }
            RPC.Call(key, a);
          }
        }
        catch (Exception ex) {
          if (Verbose) {
            Log.Warning("{0} - {1}", msg, ex);
          }
        }
      }
    }
    public void Stop() {
      foreach (var cl in _connections.ToArray()) {
        try {
          cl.Dispose();
        }
        catch (Exception) {
        }
      }
      EsSocketWS.Stop();
      EsSocketTCP.Stop();
    }
    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/ES", true);
        if (en.GetState().ValueType != JSC.JSValueType.Boolean) {
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

    private static T GetOrDefault<T>(Topic t, T defaultValue = default) {
      var j_v = JSC.JSValue.Marshal(defaultValue);
      if(j_v.ValueType == t.GetState().ValueType) {
        return t.GetState().As<T>();
      } else {
        t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        t.SetState(j_v);
      }
      return defaultValue;
    }
  }
}
