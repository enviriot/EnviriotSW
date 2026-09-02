///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
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
  internal class TWI : IMsExt {
    private readonly Topic _owner;
    private readonly Action<byte[]> _pub;
    private readonly SubRec _deviceChangedsSR;
    private readonly List<TwiDevice> _devs;
    private readonly Queue<TwiPack> _reqs;
    private int _flag;

    public TWI(Topic owner, Action<byte[]> pub) {
      this._owner = owner;
      this._pub = pub;
      this._devs = new List<TwiDevice>();
      this._reqs = new Queue<TwiPack>();
      _flag = 1;
      _deviceChangedsSR = this._owner.Subscribe(SubRec.SubMask.Children | SubRec.SubMask.Field, "type", DeviceChanged);
      if(Verbose) {
        Log.Debug("{0}.Created", _owner.path);
      }
    }

    /// <summary>/$YS/TWI/verbose, declared once by the plugin rather than per bus.</summary>
    public bool Verbose {
      get {
        return MQTT_SNPl.verboseTwi;
      }
    }

    #region IMsExt Members
    public void Recv(byte[] buf) {
      if(buf == null || buf.Length < 4) {
        if(Verbose) {
          Log.Warning("{0}.recv({1})", _owner.path, buf == null ? "null" : BitConverter.ToString(buf));
        }
        return;
      }
      if(_reqs.Any()) {
        if(_reqs.Peek().data[0] == buf[0]) {
          var req = _reqs.Dequeue();
          if((buf[1] & 0xF0) == 0x10) {
            if(buf[3] == req.data[3]) {
              req.cb.SetResult(new JSL.Array(buf));
              if(Verbose) {
                Log.Debug("{0}.recv({1})", _owner.path, BitConverter.ToString(buf));
              }
            } else {
              req.cb.SetException(new JSC.JSException(new JSL.Number(5)));  // wrong response length
              if(Verbose) {
                Log.Warning("{0}.recv({1}) - wrong response length", _owner.path, BitConverter.ToString(buf));
              }
            }
          } else if((buf[1] & 0x20) != 0) {
            req.cb.SetException(new JSC.JSException(new JSL.Number(2)));  // Timeout
            if(Verbose) {
              Log.Warning("{0}.recv({1}) - Timeout", _owner.path, BitConverter.ToString(buf));
            }
          } else if((buf[1] & 0x40) != 0) {
            req.cb.SetException(new JSC.JSException(new JSL.Number(3)));  // Slave Addr NACK received - Device not present
            if(Verbose) {
              Log.Warning("{0}.recv({1}) - Slave Addr NACK", _owner.path, BitConverter.ToString(buf));
            }
          } else {
            req.cb.SetException(new JSC.JSException(new JSL.Number(4)));  // Internal Error
            if(Verbose) {
              Log.Warning("{0}.recv({1}) - Internal Error", _owner.path, BitConverter.ToString(buf));
            }
          }
          _flag = 1;
          SendReq();
        } else {
          if(Verbose) {
            Log.Warning("{0}.recv({1}) - unknown response", _owner.path, BitConverter.ToString(buf));
          }
        }
      } else {
        if(Verbose) {
          Log.Warning("{0}.recv({1}) - unknown response", _owner.path, BitConverter.ToString(buf));
        }
      }
    }
    public void SendAck(byte[] buf) {
      TwiPack req;
      if(_reqs.Any() && (req = _reqs.Peek()).data.Length == buf.Length && buf.SequenceEqual(req.data)) {
        req.to = DateTime.Now.AddSeconds(15);
      }
    }
    public void Tick() {
      if(_reqs.Any()) {
        TwiPack req = _reqs.Peek();
        if(req.to < DateTime.Now) {
          Recv(new byte[] { req.data[0], 0x20, req.data[2], 0, 0xFF });  // Timeout server-side
        }
      }
    }
    #endregion IMsExt Members
    private void SendReq() {
      if(Interlocked.CompareExchange(ref _flag, 2, 1) == 1) {
        if(_reqs.Any()) {
          var req = _reqs.Peek();
          _pub(req.data);
          if(Verbose) {
            Log.Debug("{0}.send({1})", _owner.path, BitConverter.ToString(req.data));
          }
          if(req.data[3] == 0) {  // to recive 0 bytes => no answer
            Recv(new byte[] { req.data[0], 0x10, req.data[2], 0, 0xFF});
          }
        } else {
          _flag = 1;
        }
      }
    }
    private void DeviceChanged(Perform p, SubRec sr) {
      var d = _devs.FirstOrDefault(z => z.owner == p.src);
      if(d != null) {
        _devs.Remove(d);
        d.Dispose();
      }
      if((p.Art == Perform.E_Art.create || p.Art == Perform.E_Art.changedField || p.Art == Perform.E_Art.subscribe) && p.src.GetField("type").AsString(string.Empty).StartsWith("TWI")) {
        _devs.Add(new TwiDevice(p.src, this));
      }
    }
    private Task<JSC.JSValue> TwiReq(int[] arr) {
      if(arr == null) {
        throw new ArgumentNullException("arr", "TwiReq");
      }
      if(arr.Length < 4) {
        throw new ArgumentOutOfRangeException("arr", "TwiReq len=" + arr.Length.ToString() + "<4");
      }
      arr[2] = arr.Length - 4;
      var ba = arr.Select(z => (byte)z).ToArray();
      var tsc = new TaskCompletionSource<JSC.JSValue>();
      _reqs.Enqueue(new TwiPack(ba, tsc));
      SendReq();
      return tsc.Task;
    }

    #region IDisposable Member
    public void Dispose() {
      _deviceChangedsSR.Dispose();
      _reqs.Clear();
      foreach(var d in _devs) {
        d.Dispose();
      }
      _devs.Clear();
      if(Verbose) {
        Log.Debug("{0}.Disposed", _owner.path);
      }
    }
    #endregion IDisposable Member

    private class TwiDevice : IDisposable {
      public readonly Topic owner;
      private readonly TWI _twi;
      private readonly JSC.Context _ctx;
      private readonly JSC.JSValue _self;

      public TwiDevice(Topic owner, TWI twi) {
        this.owner = owner;
        this._twi = twi;
        JSC.JSValue jSrc;
        var jType = owner.GetField("type");
        var types = Topic.root.Get("$YS/TYPES", false);  // null until PersistentStorage seeds it, and always null when that plugin is off
        if(types != null && jType.AsString(null) is string tp && types.Exist(tp, out var tt)
          && (jSrc = tt.GetState().Field("src")).Is<string>()) {
        } else {
          jSrc = null;
        }
        if(jSrc != null) {
          try {
            _ctx  = new JSC.Context(JsExtLib.Context);
            _ctx.DefineVariable("setTimeout").Assign(X13.JsExtLib.Context.ProxyValue(new Func<JSC.JSValue, int, JSC.JSValue>(SetTimeout)));
            _ctx.DefineVariable("setInterval").Assign(X13.JsExtLib.Context.ProxyValue(new Func<JSC.JSValue, int, JSC.JSValue>(SetInterval)));
            _ctx.DefineVariable("setAlarm").Assign(X13.JsExtLib.Context.ProxyValue(new Func<JSC.JSValue, JSC.JSValue, JSC.JSValue>(SetAlarm)));

            if(_ctx.Eval(jSrc.AsString(string.Empty)) is JSL.Function f) {
              f.prototype["GetState"] = X13.JsExtLib.Context.ProxyValue(new Func<string, JSC.JSValue>(GetState));
              f.prototype["SetState"] = X13.JsExtLib.Context.ProxyValue(new Action<string, JSC.JSValue>(SetState));
              f.prototype["GetField"] = X13.JsExtLib.Context.ProxyValue(new Func<string, string, JSC.JSValue>(GetField));
              f.prototype["TwiReq"] = X13.JsExtLib.Context.ProxyValue(new Func<int[], Task<JSC.JSValue>>(_twi.TwiReq));
              if(f.RequireNewKeywordLevel == JSL.RequireNewKeywordLevel.WithNewOnly) {
                _self = f.Construct(new JSC.Arguments());
              } else {
                Log.Error("{0} - class not found", owner.path);
                return;
              }
            }
          }
          catch(Exception ex) {
            Log.Warning("{0}.ctor() - {1}", owner.path, ex.Message);
          }
        } else {
          Log.Warning("{0} constructor is not defined", owner.path);
        }

      }

      private JSC.JSValue SetTimeout(JSC.JSValue func, int to) {
        return JsExtLib.SetTimer(func, to, -1, _ctx);
      }
      private JSC.JSValue SetInterval(JSC.JSValue func, int interval) {
        return JsExtLib.SetTimer(func, interval, interval, _ctx);
      }
      private JSC.JSValue SetAlarm(JSC.JSValue func, JSC.JSValue time) {
        if(time.Value is JSL.Date jd) {
          return JsExtLib.SetTimer(func, jd.ToDateTime(), _ctx);
        } else {
          throw new ArgumentException("SetAlarm(, Date)");
        }
      }

      private JSC.JSValue GetState(string path) {
        if(owner.Exist(path, out var t)) {
          return t.GetState();
        }
        return JSC.JSValue.NotExists;
      }
      private void SetState(string path, JSC.JSValue value) {
        if(!owner.Exist(path, out var t)) {
          t = owner.Get(path, true, owner);
          t.SetField("MQTT-SN.tag", "---", owner);
          t.SetAttribute(Topic.Attribute.Required);
        }
        t.SetState(value, owner);
      }
      private JSC.JSValue GetField(string path, string field) {
        if(owner.Exist(path, out var t)) {
          return t.GetField(field);
        }
        return JSC.JSValue.NotExists;

      }

      #region IDisposable Member
      public void Dispose() {
        if(!owner.disposed) {
          owner.SetState(0, _twi._owner);
        }
        JsExtLib.ClearTimeout(_ctx);
      }
      #endregion IDisposable Member
    }
    private class TwiPack {
      public byte[] data;
      public TaskCompletionSource<JSC.JSValue> cb;
      public DateTime to;
      public TwiPack(byte[] data, TaskCompletionSource<JSC.JSValue> cb) {
        this.data = data;
        this.cb = cb;
        this.to = DateTime.MaxValue;
      }

    }
  }
}
