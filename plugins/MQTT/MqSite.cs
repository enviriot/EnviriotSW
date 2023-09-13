///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.MQTT {
  internal class MqSite : IDisposable {
    private Uri _uri;
    private MQTTPl _pl;
    private SubRec _sr;
    private bool _subEn, _pubEn, _retainedEn;

    public readonly Topic Owner;
    public readonly MqClient Client;
    public readonly string remotePath;
    public readonly string remotePrefix;
    private SubRec.SubMask _mask;

    public MqSite(MQTTPl pl, MqClient client, Topic owner, Uri uUri) {
      this.Client = client;
      this.Owner = owner;
      this._pl = pl;
      this._uri = uUri;

      _subEn = ReadFlag("MQTT.subscribe", true);
      _pubEn = ReadFlag("MQTT.publish", true);
      _retainedEn = ReadFlag("MQTT.retained", false);

      remotePath = _uri.PathAndQuery + _uri.Fragment;
      var sl = remotePath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
      remotePrefix = string.Empty;
      _mask = SubRec.SubMask.Value;
      for(int i = 0; i < sl.Length; i++) {
        if(sl[i] == "+") {
          _mask |= SubRec.SubMask.Chldren;
          break;
        }
        if(sl[i] == "#") {
          _mask |= SubRec.SubMask.All;
          break;
        }
        remotePrefix = remotePrefix + "/" + sl[i];
      }
      Client.Sites.Add(this);
      if(Client.status == MqClient.Status.Connected) {
        this.Connected();
      }
      var act = this.Owner.GetField("Action");
      
      JSC.JSValue txt;
      if(act==null || !act.Any(z => z.Value.ValueType==JSC.JSValueType.Object && (txt=z.Value["name"]).ValueType == JSC.JSValueType.String && (txt.Value as string) == "MQTT.Reconnect")) {
        int i;
        JSL.Array act_n;
        if(act==null) {
          act_n = new JSL.Array(1);
          i = 0;
        } else {
          int j = act.Count();
          act_n = new JSL.Array(j+1);
          for(i = 0; i<j; i++) {
            act_n[i] = act[i.ToString()];
          }
        }
        var r_a = JSC.JSObject.CreateObject();
        r_a["name"] = "MQTT.Reconnect";
        r_a["text"] = "Reconnect MQTT connection";
        act_n[i] = r_a;
        this.Owner.SetField("Action", act_n);
      }
    }
    private bool ReadFlag(string path, bool def) {
      bool rv;
      var v1 = this.Owner.GetField(path);
      if(v1.ValueType==JSC.JSValueType.Boolean) {
         rv = (bool)v1;
      } else {
        rv = def;
        this.Owner.SetField(path, def);
      }
      return rv;
    }
    public void Publish(string path, string payload) {
      if(!_subEn) {
        return;
      }
      string lp = (path.Length > remotePrefix.Length) ? path.Substring(remotePrefix.Length + 1) : string.Empty;
      try {
        var o = JsLib.ParseJson(payload);
        var t = Owner.Get(lp, true, Owner);
        if(!t.CheckAttribute(Topic.Attribute.Internal)) {
          t.SetState(o, Owner);
        }
      }
      catch(Exception ex) {
        if(_pl.verbose) {
          Log.Warning("{0}{1} R {2} - {3}", Client, path, payload, ex.Message);
        }
      }
    }
    public void Connected() {
      if(_pubEn) {
        _sr = Owner.Subscribe(_mask, Changed);
      } else if(_subEn) {
        Client.Subscribe(this);
      }
    }
    public void Disconnected() {
      var sr = Interlocked.Exchange(ref _sr, null);
      if(sr != null) {
        sr.Dispose();
      }
    }

    public void Dispose() {
      Disconnected();
      if(_subEn) {
        Client.Unsubscribe(this);
      }
    }

    private void Changed(Perform p, SubRec sr) {
      if(Client == null || Client.status != MqClient.Status.Connected) {
        Disconnected();
        Log.Warning("{0}.Changed({1}) - Client OFFLINE", Owner.path, p.ToString());
        return;
      }
      if((p.Art == Perform.E_Art.subscribe || ((p.Art == Perform.E_Art.changedState || p.Art == Perform.E_Art.create) && p.Prim != Owner)) && !p.src.CheckAttribute(Topic.Attribute.Internal)) {
        var rp = remotePrefix + p.src.path.Substring(Owner.path.Length);
        var payload = JsLib.Stringify(p.src.GetState() ?? JSC.JSValue.Null);
        if(!string.IsNullOrEmpty(rp) && payload != null) {
          Client.Send(new MqPublish(rp, payload) { Retained = _retainedEn });
        }
      } else if(p.Art == Perform.E_Art.subAck && _subEn) {
        Client.Subscribe(this);
      }
    }
  }
}
