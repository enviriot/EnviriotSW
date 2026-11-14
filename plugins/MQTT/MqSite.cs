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
  /// <summary>Одна связка топика с темой брокера: подписка в дереве и подписка у брокера.</summary>
  /// <remarks>Флаги обмена обновляются подпиской на манифест.
  /// Dispose снимает обе подписки Repository и удаляет привязку у клиента.</remarks>
  internal class MqSite : IDisposable {
    private readonly object _sync = new object();
    private Uri _uri;
    private MQTTPl _pl;
    private SubRec _sr, _cfgSr;
    private bool _subEn, _pubEn, _retainedEn;
    private bool _subscribed, _ready, _disposed;

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
          _mask |= SubRec.SubMask.Children;
          break;
        }
        if(sl[i] == "#") {
          _mask |= SubRec.SubMask.All;
          break;
        }
        remotePrefix = remotePrefix + "/" + sl[i];
      }
      if((_mask & (SubRec.SubMask.Children | SubRec.SubMask.All)) == SubRec.SubMask.None) {
        _mask |= SubRec.SubMask.Once;
      }
      _cfgSr = Owner.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT", SettingsChanged);
      lock(Client.Sites) {
        Client.Sites.Add(this);
      }
      if(Client.status == MqClient.Status.Connected) {
        this.Connected();
      }
      var act = this.Owner.GetField("Action");
      
      // Пустой элемент Action не должен приводить к чтению свойства у null.
      if(act==null || !act.Any(z => z.Value.IsObject() && z.Value.AsString("name", null) == "MQTT.Reconnect")) {
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
    private void SettingsChanged(TopicEvent p, SubRec sr) {
      lock(_sync) {
        if(_disposed || !ReferenceEquals(sr, _cfgSr)) {
          return;
        }
        if(p.Kind == EventKind.Removed) {
          Dispose();
        } else if(p.Kind == EventKind.Snapshot || p.Kind == EventKind.FieldChanged) {
          RefreshSettings();
        }
      }
    }
    private void RefreshSettings() {
      _subEn = ReadFlag("MQTT.subscribe", true);
      _pubEn = ReadFlag("MQTT.publish", true);
      _retainedEn = ReadFlag("MQTT.retained", false);
      UpdateSubscriptions();
    }
    private void UpdateSubscriptions() {
      if(Client.status != MqClient.Status.Connected) {
        StopPublishing();
        _subscribed = false;
        return;
      }
      if(_pubEn) {
        if(_sr == null) {
          _ready = false;
          _sr = Owner.Subscribe(_mask, Changed);
        }
      } else {
        StopPublishing();
      }
      if(!_subEn && _subscribed) {
        Client.Unsubscribe(this);
        _subscribed = false;
      } else if(_subEn && !_subscribed && (!_pubEn || _ready)) {
        Client.Subscribe(this);
        _subscribed = true;
      }
    }
    private void StopPublishing() {
      var sr = _sr;
      _sr = null;
      _ready = false;
      if(sr != null) {
        sr.Dispose();
      }
    }
    public void Publish(string path, string payload) {
      lock(_sync) {
        if(_disposed || !_subEn) {
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
    }
    public void Connected() {
      lock(_sync) {
        if(!_disposed) {
          RefreshSettings();
        }
      }
    }
    public void Disconnected() {
      lock(_sync) {
        StopPublishing();
        _subscribed = false;
      }
    }

    public void Dispose() {
      lock(_sync) {
        if(_disposed) {
          return;
        }
        _disposed = true;
        if(_cfgSr != null) {
          _cfgSr.Dispose();
          _cfgSr = null;
        }
        StopPublishing();
        if(_subscribed) {
          Client.Unsubscribe(this);
          _subscribed = false;
        }
        lock(Client.Sites) {
          Client.Sites.Remove(this);
        }
      }
    }

    private void Changed(TopicEvent p, SubRec sr) {
      lock(_sync) {
        if(_disposed || !ReferenceEquals(sr, _sr)) {
          return;
        }
        if(Client.status != MqClient.Status.Connected) {
          Disconnected();
          Log.Warning("{0}.Changed({1}) - Client OFFLINE", Owner.path, p.ToString());
          return;
        }
        // Snapshot приходит раньше FieldChanged, но манифест этого тика уже применён.
        RefreshSettings();
        if(!ReferenceEquals(sr, _sr)) {
          return;
        }
        if((p.Kind == EventKind.Snapshot || ((p.Kind == EventKind.StateChanged || p.Kind == EventKind.Created) && p.Author != Owner)) && !p.Source.CheckAttribute(Topic.Attribute.Internal)) {
          var rp = remotePrefix + p.Source.path.Substring(Owner.path.Length);
          var payload = JsLib.Stringify(p.Source.GetState() ?? JSC.JSValue.Null);
          if(!string.IsNullOrEmpty(rp) && payload != null) {
            Client.Send(new MqPublish(rp, payload) { Retained = _retainedEn });
          }
        } else if(p.Kind == EventKind.Ready) {
          _ready = true;
          UpdateSubscriptions();
        }
      }
    }
  }
}
