﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
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
      _owner = Topic.root.Get("/$YS/MQTT");
      var verboseT = _owner.Get("verbose");
      if(verboseT.GetState().ValueType != JSC.JSValueType.Boolean) {
        verboseT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
//#if DEBUG
//        verboseT.SetState(true);
//#else
        verboseT.SetState(false);
//#endif
      }
      _verboserSR = verboseT.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, (p, s) => verbose = (_verboserSR.setTopic != null && _verboserSR.setTopic.GetState().As<bool>()));
      _subMq = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "MQTT.uri", SubFunc);
    }
    public void Tick() {
    }
    public void Stop() {
      var sr = Interlocked.Exchange(ref _subMq, null);
      if(sr != null) {
        sr.Dispose();
      }
      int i;
      for(i = _clients.Count - 1; i >= 0; i--) {
        _clients[i].Dispose();
      }
    }
    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/MQTT", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/MQTT", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members

    public bool verbose;

    #region RPC
    private void ReconnectRpc(JSC.JSValue[] obj) {
      string path;
      if(obj == null || obj.Length != 1 || obj[0] == null || obj[0].ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(path = obj[0].Value as string)) {
        return;
      }
      var s = _sites.FirstOrDefault(z => z.Owner.path==path);
      if(s==null) {
        Log.Warning("No MQTT binding for {0}", path);
      }
      System.Threading.ThreadPool.QueueUserWorkItem(s.Client.Restart);
    }
    #endregion RPC


    private void SubFunc(Perform p, SubRec sr) {
      if(p.Art == Perform.E_Art.create) {
        return;
      }
      MqSite ms = _sites.FirstOrDefault(z => z.Owner == p.src);
      MqClient client;
      if(ms != null) {
        ms.Dispose();
        _sites.Remove(ms);
      }
      if(p.Art == Perform.E_Art.changedField || p.Art==Perform.E_Art.subscribe) {
        var uri = p.src.GetField("MQTT.uri").Value as string;
        if(string.IsNullOrEmpty(uri)) {
          return;
        }
        Uri uUri;
        try {
          uUri = new Uri(uri, UriKind.Absolute);
        }
        catch(Exception ex) {
          Log.Warning("{0}.MQTT.uri = {1} - {2}", p.src.path, uri, ex.Message);
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
        _sites.Add( new MqSite(this, client, p.src, uUri));
      }
    }
  }
}
