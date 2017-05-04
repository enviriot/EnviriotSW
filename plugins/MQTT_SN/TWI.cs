///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading.Tasks;
using System.Threading;

namespace X13.Periphery {
  internal class TWI : IDisposable {
    private static Func<string, JSC.JSValue> _createFunc;

    static TWI() {
      _createFunc = (JsExtLib.Context.Eval("Function('src', 'return Function(src);')") as JSL.Function).MakeDelegate(typeof(Func<string, JSC.JSValue>)) as Func<string, JSC.JSValue>;
    }

    private Topic _owner;
    private Action<byte[]> _pub;
    private SubRec _deviceChangedsSR;
    private List<TwiDevice> _devs;

    public TWI(Topic owner, Action<byte[]> pub) {
      this._owner = owner;
      this._pub = pub;
      this._devs = new List<TwiDevice>();
      _deviceChangedsSR = this._owner.Subscribe(SubRec.SubMask.Chldren | SubRec.SubMask.Field, "type", DeviceChanged);
    }

    public void Recv(byte[] buf) {
      throw new NotImplementedException();
    }

    private void DeviceChanged(Perform p, SubRec sr) {
      var d = _devs.FirstOrDefault(z => z.owner == p.src);
      if(d != null) {
        _devs.Remove(d);
        d.Dispose();
      }
      JSC.JSValue jType;
      if((p.art == Perform.Art.create || p.art == Perform.Art.changedField || p.art==Perform.Art.subscribe) && (jType = p.src.GetField("type")).ValueType == JSC.JSValueType.String 
        && jType.Value != null && (jType.Value as string).StartsWith("TWI")) {
          _devs.Add(new TwiDevice(p.src, this));
      }
    }

    #region IDisposable Member
    public void Dispose() {
      _deviceChangedsSR.Dispose();
    }
    #endregion IDisposable Member

    private class TwiDevice : IDisposable {
      public readonly Topic owner;
      private TWI _twi;
      private JSC.JSObject _self;

      public TwiDevice(Topic owner, TWI twi) {
        this.owner = owner;
        this._twi = twi;
        JSC.JSValue jSrc;
        jSrc = owner.GetField("TWI.src");
        if(jSrc.ValueType != JSC.JSValueType.String) {
          var jType = owner.GetField("type");
          Topic tt;
          if(jType.ValueType == JSC.JSValueType.String && jType.Value != null && Topic.root.Get("$YS/TYPES", false).Exist(jType.Value as string, out tt)
            && (jSrc = JsLib.GetField(tt.GetState(), "TWI.src")).ValueType == JSC.JSValueType.String) {
          } else {
            jSrc = null;
          }
        }
        if(jSrc != null) {
          try {
            var f = _createFunc(jSrc.Value as string) as JSL.Function;
            if(f != null) {
              this._self = JSC.JSObject.CreateObject();
              _self["GetState"] = JSC.JSValue.Marshal(new Func<string, JSC.JSValue>(GetState));
              _self["SetState"] = JSC.JSValue.Marshal(new Action<string, JSC.JSValue>(SetState));
              _self["GetProperty"] = JSC.JSValue.Marshal(new Func<string, string, JSC.JSValue>(GetProperty));

              f.Call(_self, new JSC.Arguments());
            }
          }
          catch(Exception ex) {
            Log.Warning("{0}.ctor() - {1}", owner.path, ex.Message);
          }
        } else {
          Log.Warning("{0} constructor is not defined", owner.path);
        }

      }

      private JSC.JSValue GetState(string path) {
        Topic t;
        if(owner.Exist(path, out t)) {
          return t.GetState();
        }
        return JSC.JSValue.NotExists;
      }
      private void SetState(string path, JSC.JSValue value) {
        Topic t;
        if(!owner.Exist(path, out t)) {
          t = owner.Get(path, true, owner);
          t.SetField("MQTT-SN.tag", "---", owner);
          t.SetAttribute(Topic.Attribute.Required);
        }
        t.SetState(value, owner);
      }
      private JSC.JSValue GetProperty(string path, string field) {
        Topic t;
        if(owner.Exist(path, out t)) {
          return t.GetField(field);
        }
        return JSC.JSValue.NotExists;

      }

      #region IDisposable Member
      public void Dispose() {
        throw new NotImplementedException();
      }
      #endregion IDisposable Member
    }
  }
}
