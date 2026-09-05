///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;

namespace X13.MQTT {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 8)]
  [ExportMetadata("name", "MQTT")]
  public class MQTTPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/MQTT";
    private Topic _owner;
    private SubRec _verboserSR;
    private SubRec _subMq;
    private List<MqSite> _sites;
    private List<MqClient> _clients;

    public MQTTPl() {
      _sites = new List<MqSite>();
      _clients = new List<MqClient>();
    }

    #region IPlugModul Members
    public void Init() {
      RPC.Register("MQTT.Reconnect", ReconnectRpc);
    }
    public void Start() {
      _verboserSR = JsExtLib.EnsureCfg(Owner, "verbose",
        Topic.Attribute.Required | Topic.Attribute.DB, v => verbose = v, false);
      _subMq = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "MQTT.uri", SubFunc);
    }
    public void Tick() {
    }
    public void Stop() {
      if(_subMq != null) {
        _subMq.Dispose();
        _subMq = null;
      }
      // EnsureCfg hands ownership of the subscription to the caller.
      if(_verboserSR != null) {
        _verboserSR.Dispose();
        _verboserSR = null;
      }
      int i;
      for(i = _clients.Count - 1; i >= 0; i--) {
        _clients[i].Dispose();
      }
    }
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        // Is<bool>, NOT AsBool: this decides whether the config topic has to be CREATED and
        // seeded. A reader with a default cannot tell "not set yet" from "set to the default",
        // so the topic would never be created.
        if(!Owner.GetState().Is<bool>()) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
      }
    }
    #endregion IPlugModul Members

    public bool verbose;

    #region RPC
    /// <summary>Restarts the client bound to this topic.</summary>
    /// <remarks>The missing return is the point: it used to log that there was no binding and then
    /// dereference the very variable it had just called null. A menu action on a topic without an
    /// MQTT binding threw instead of saying so, and RPC.Call does not catch what a handler
    /// throws - deliberately, because somebody is waiting for the answer.</remarks>
    private void ReconnectRpc(Topic t, JSC.JSValue arg) {
      var s = _sites.FirstOrDefault(z => z.Owner == t);
      if(s == null) {
        Log.Warning("No MQTT binding for {0}", t.path);
        return;
      }
      System.Threading.ThreadPool.QueueUserWorkItem(s.Client.Restart);
    }
    #endregion RPC


    private void SubFunc(TopicEvent p, SubRec sr) {
      if(p.Kind == EventKind.Created) {
        return;
      }
      MqSite ms = _sites.FirstOrDefault(z => z.Owner == p.Source);
      MqClient client;
      if(ms != null) {
        ms.Dispose();
        _sites.Remove(ms);
      }
      if(p.Kind == EventKind.FieldChanged || p.Kind==EventKind.Snapshot) {
        string uri = p.Source.GetField("MQTT.uri").AsString(null);
        if(string.IsNullOrEmpty(uri)) {
          return;
        }
        Uri uUri;
        try {
          uUri = new Uri(uri, UriKind.Absolute);
        }
        catch(Exception ex) {
          Log.Warning("{0}.MQTT.uri = {1} - {2}", p.Source.path, uri, ex.Message);
          return;
        }
        string uName, uPass;
        if(!string.IsNullOrEmpty(uUri.UserInfo)) {
          var uia = uUri.UserInfo.Split(':');
          uName = uia[0];
          uPass = uia.Length > 1 ? uia[1] : null;
        } else {
          uName = null;
          uPass = null;
        }
        var cid = "MQTT://" + (uName == null ? string.Empty : (uName + "@")) + uUri.DnsSafeHost + (uUri.IsDefaultPort ? string.Empty : (":" + uUri.Port.ToString()));
        client = _clients.FirstOrDefault(z => z.Signature == cid);
        if(client == null) {
          client = new MqClient(this, uUri.DnsSafeHost, uUri.IsDefaultPort?1883:uUri.Port, uName, uPass);
          _clients.Add(client);
        }
        _sites.Add( new MqSite(this, client, p.Source, uUri));
      }
    }
  }
}
