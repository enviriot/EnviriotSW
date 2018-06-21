///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSF = NiL.JS.Core.Functions;
using JSI = NiL.JS.Core.Interop;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13 {
  public static class JsLib {

    public static readonly char[] SPLITTER_OBJ = new char[] { '.' };
    private static JSF.ExternalFunction _JSON_Replacer;
    static JsLib() {
      _JSON_Replacer = new JSF.ExternalFunction(CustomTypesRepl);
    }
    private static JSC.JSValue CustomTypesRepl(JSC.JSValue thisBind, JSC.Arguments args) {
      if(args.Length == 2 && args[1].ValueType == JSC.JSValueType.String) {
        var s = args[1].Value as string;
        if(s != null) {
          if(s.StartsWith("¤BA")) {
            try {
              return new ByteArray(Convert.FromBase64String(s.Substring(3)));
            }
            catch(Exception ex) {
              Log.Warning("ParseJson(" + args[0].ToString() + ", " + s + ") - " + ex.Message);
              return new ByteArray();
            }
          }
          // 2015-09-16T14:15:18.994Z
          if(s.Length == 24 && s[4] == '-' && s[7] == '-' && s[10] == 'T' && s[13] == ':' && s[16] == ':' && s[19] == '.') {
            JSL.Date js_d = new JSL.Date(new JSC.Arguments() { args[1] });
            if(Math.Abs(( js_d.ToDateTime() - new DateTime(1001, 1, 1, 12, 0, 0) ).TotalDays) < 1) {
              return JSC.JSObject.Marshal(DateTime.UtcNow);
            }
            return JSC.JSValue.Marshal(js_d);
          }
        }
      }
      return args[1];
    }
    public static JSC.JSValue ParseJson(string json) {
      return JSL.JSON.parse(json, _JSON_Replacer);
    }
    public static JSC.JSValue SetField(JSC.JSValue oc, string path, JSC.JSValue val) {
      if(string.IsNullOrEmpty(path)) {
        return val;
      }
      if(oc == null) {
        oc = JSC.JSValue.Null;
      }
      var ps = path.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries);
      JSC.JSValue ro = JSC.JSObject.CreateObject(), rc, rn, on;
      rc = ro;
      for(int i = 0; i < ps.Length; i++) {
        on = JSC.JSValue.Null;
        if(oc.ValueType == JSC.JSValueType.Object && oc.Value != null) {
          foreach(var kv in oc) {
            if(kv.Key != ps[i]) {
              rc[kv.Key] = kv.Value;
            } else {
              on = kv.Value;
            }
          }
        }
        if(i == ps.Length-1) {
          if(val != null && !val.IsNull) {
            rc[ps[i]] = val;
          }
          rn = null;
        } else {
          rn = JSC.JSObject.CreateObject();
          rc[ps[i]] = rn;
        }
        rc = rn;
        oc = on;
      }
      return ro;
    }
    public static JSC.JSValue GetField(JSC.JSValue obj, string path) {
      if(obj == null) {
        return JSC.JSValue.NotExists;
      }
      if(string.IsNullOrEmpty(path)) {
        return obj;
      }
      var ps = path.Split(SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries);
      JSC.JSValue p = obj, c = null;
      for(int i = 0; i < ps.Length; i++) {
        if(p.ValueType != JSC.JSValueType.Object || p.Value == null) {
          return JSC.JSValue.NotExists;
        }
        c = p.GetProperty(ps[i]);
        p = c;
      }
      return c;
    }
    public static JSC.JSValue Clone(JSC.JSValue org) {
      if(org == null || !org.Defined) {
        return org;
      }
      if(org.ValueType == JSC.JSValueType.Object) {
        var ret = JSC.JSObject.CreateObject();
        foreach(var kv in org) {
          ret[kv.Key] = Clone(kv.Value);
        }
        return ret;
      }
      return JSC.JSValue.Marshal(org.Value);
    }
    public static bool Equal(JSC.JSValue v1, JSC.JSValue v2) {
      if(object.ReferenceEquals(v1, v2)) {
        return true;
      }
      if(v1==null || v2==null) {
        return false;
      }
      if(v1.ValueType!=v2.ValueType) {
        return false;
      }
      return object.Equals(v1.Value, v2.Value);
    }

    public static int OfInt(JSC.JSValue v, int def) {
      return (v == null || !v.IsNumber) ? def : ((int)v);
    }
    public static int OfInt(JSC.JSValue v, string path, int def) {
      v = GetField(v, path);
      return ( v == null || !v.IsNumber ) ? def : ( (int)v );
    }
    public static string OfString(JSC.JSValue v, string def=null) {
      return (v == null || v.ValueType!=JSC.JSValueType.String) ? def : (v.Value as string);
    }
    public static string OfString(JSC.JSValue v, string path, string def) {
      v = GetField(v, path);
      return ( v == null || v.ValueType!=JSC.JSValueType.String ) ? def : ( v.Value as string );
    }

    internal static void Propertys(ref SortedList<string, JSC.JSValue> l, JSC.JSValue o) {
      if(o == null || o.ValueType != JSC.JSValueType.Object || o.Value == null) {
        return;
      }
      if(l == null) {
        l = new SortedList<string, JSC.JSValue>();
      }
      foreach(var kv in o) {
        if(!l.ContainsKey(kv.Key)) {
          l.Add(kv.Key, kv.Value);
        }
      }
    }
  }
  public class ByteArray : JSI.CustomType {
    private byte[] _val;

    public ByteArray() {
      _val = new byte[0];
    }
    public ByteArray(byte[] data) {
      _val = data;
    }
    public ByteArray(ByteArray src, byte[] data, int pos) {
      if(data == null) {
        return;
      }
      if(src == null) {
        if(pos < 0) {
          pos = 0;
        }
        _val = new byte[pos + data.Length];
        Buffer.BlockCopy(data, 0, _val, pos, data.Length);
      } else {
        if(pos < 0) {  // negative => position from end
          pos = src._val.Length + 1 + pos;
        }
        if(pos >= src._val.Length) {
          _val = new byte[pos + data.Length];
          Buffer.BlockCopy(src._val, 0, _val, 0, src._val.Length);
          Buffer.BlockCopy(data, 0, _val, pos, data.Length);
        } else if(pos == 0) {
          _val = new byte[src._val.Length + data.Length];
          Buffer.BlockCopy(data, 0, _val, 0, data.Length);
          Buffer.BlockCopy(src._val, 0, _val, data.Length, src._val.Length);
        } else {
          _val = new byte[src._val.Length + data.Length];
          Buffer.BlockCopy(src._val, 0, _val, 0, pos);
          Buffer.BlockCopy(data, 0, _val, pos, data.Length);
          Buffer.BlockCopy(src._val, pos, _val, pos + data.Length, src._val.Length - pos);
        }
      }
    }
    public byte[] GetBytes() {
      return _val;
    }

    [JSI.DoNotEnumerate]
    public JSC.JSValue toJSON(JSC.JSValue obj) {
      return new JSL.String("¤BA" + Convert.ToBase64String(_val));
    }
    /*
    protected override JSC.JSValue GetProperty(JSC.JSValue name, bool forWrite, JSC.PropertyScope memberScope) {
      return null;
    }
    protected override void SetProperty(JSC.JSValue key, JSC.JSValue value, JSC.PropertyScope memberScope, bool throwOnError) {
    }
    protected override IEnumerator<KeyValuePair<string, JSC.JSValue>> GetEnumerator(bool hideNonEnum, JSC.EnumerationMode enumerationMode) {
      return null;
    }*/
    public override string ToString() {
      return BitConverter.ToString(_val);
    }
  }
}
