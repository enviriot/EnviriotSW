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
    }
    public void Publish(string path, string payload) {
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
      _sr = Owner.Subscribe(_mask, Changed);
    }
    public void Disconnected() {
      var sr = Interlocked.Exchange(ref _sr, null);
      if(sr != null) {
        sr.Dispose();
        Client.Unsubscribe(this);
      }
    }

    public void Dispose() {
      Disconnected();
    }

    private void Changed(Perform p, SubRec sr) {
      if(Client == null || Client.status != MqClient.Status.Connected) {
        Disconnected();
        Log.Warning("{0}.Changed({1}) - Client OFFLINE", Owner.path, p.ToString());
        return;
      }
      if((p.art == Perform.Art.subscribe || ((p.art == Perform.Art.changedState || p.art == Perform.Art.create) && p.prim != Owner)) && !p.src.CheckAttribute(Topic.Attribute.Internal)) {
        var rp = remotePrefix + p.src.path.Substring(Owner.path.Length);
        var payload = JSL.JSON.stringify(p.src.GetState() ?? JSC.JSValue.Null, null, null);
        if(!string.IsNullOrEmpty(rp) && payload != null) {
          Client.Send(new MqPublish(rp, payload));
        }
      } else if(p.art == Perform.Art.subAck) {
        Client.Subscribe(this);
      }
    }
  }
}
