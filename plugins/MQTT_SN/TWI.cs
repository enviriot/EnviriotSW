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
    private Queue<Tuple<byte[], TaskCompletionSource<JSC.JSValue>>> _reqs;
    private int _flag;

    public TWI(Topic owner, Action<byte[]> pub) {
      this._owner = owner;
      this._pub = pub;
      this._devs = new List<TwiDevice>();
      this._reqs = new Queue<Tuple<byte[], TaskCompletionSource<JSC.JSValue>>>();
      _flag = 1;
      _deviceChangedsSR = this._owner.Subscribe(SubRec.SubMask.Chldren | SubRec.SubMask.Field, "type", DeviceChanged);
    }

    public void Recv(byte[] buf) {
      if(buf == null || buf.Length < 4) {
        Log.Warning("{0}.recv({1})", _owner.path, buf == null ? "null" : BitConverter.ToString(buf));
        return;
      }
      if(_reqs.Any()) {
        if(_reqs.Peek().Item1[0] == buf[0]) {
          var req = _reqs.Dequeue();
          if((buf[1] & 0xF0) == 0x10) {
            if(buf[3] == req.Item1[3]) {
              req.Item2.SetResult(new JSL.Array(buf));
            } else {
              req.Item2.SetException(new JSC.JSException(new JSL.Number(5)));  // wrong response length
            }
          } else if((buf[1] & 0x20) != 0) {
            req.Item2.SetException(new JSC.JSException(new JSL.Number(2)));  // Timeout
          } else if((buf[1] & 0x40) != 0) {
            req.Item2.SetException(new JSC.JSException(new JSL.Number(3)));  // Slave Addr NACK received - Device not present
          } else {
            req.Item2.SetException(new JSC.JSException(new JSL.Number(4)));  // Internal Error
          }
          _flag = 1;
          SendReq();
        } else {
          Log.Warning("{0}.recv({1}) - unknown request", _owner.path, BitConverter.ToString(buf));
        }
      } else {
        Log.Warning("{0}.recv({1}) - unknown request", _owner.path, BitConverter.ToString(buf));
      }
    }
    private void SendReq() {
      if(Interlocked.CompareExchange(ref _flag, 2, 1) == 1) {
        if(_reqs.Any()) {
          var req = _reqs.Peek();
          _pub(req.Item1);
          if(req.Item1[3] == 0) {  // to recive 0 bytes => no answer
            Recv(new byte[] { req.Item1[0], 0x10, req.Item1[2], 0 });
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
      JSC.JSValue jType;
      if((p.art == Perform.Art.create || p.art == Perform.Art.changedField || p.art == Perform.Art.subscribe) && (jType = p.src.GetField("type")).ValueType == JSC.JSValueType.String
        && jType.Value != null && (jType.Value as string).StartsWith("TWI")) {
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
      _reqs.Enqueue(new Tuple<byte[], TaskCompletionSource<JSC.JSValue>>(ba, tsc));
      SendReq();
      return tsc.Task;
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
        var jType = owner.GetField("type");
        Topic tt;
        if(jType.ValueType == JSC.JSValueType.String && jType.Value != null && Topic.root.Get("$YS/TYPES", false).Exist(jType.Value as string, out tt)
          && (jSrc = JsLib.GetField(tt.GetState(), "src")).ValueType == JSC.JSValueType.String) {
        } else {
          jSrc = null;
        }
        if(jSrc != null) {
          try {
            var f = _createFunc(jSrc.Value as string) as JSL.Function;
            if(f != null) {
              this._self = JSC.JSObject.CreateObject();
              _self["GetState"] = JSC.JSValue.Marshal(new Func<string, JSC.JSValue>(GetState));
              _self["SetState"] = JSC.JSValue.Marshal(new Action<string, JSC.JSValue>(SetState));
              _self["GetField"] = JSC.JSValue.Marshal(new Func<string, string, JSC.JSValue>(GetField));
              _self["TwiReq"] = JSC.JSValue.Marshal(new Func<int[], Task<JSC.JSValue>>(_twi.TwiReq));
              f.Call(_self, new JSC.Arguments());  // Call constructor
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
      private JSC.JSValue GetField(string path, string field) {
        Topic t;
        if(owner.Exist(path, out t)) {
          return t.GetField(field);
        }
        return JSC.JSValue.NotExists;

      }

      #region IDisposable Member
      public void Dispose() {
      }
      #endregion IDisposable Member
    }
  }
}
