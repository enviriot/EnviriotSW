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
    private static JSF.ExternalFunction _SJ_Replacer;
    static JsLib() {
      _SJ_Replacer = new JSF.ExternalFunction(SJ_CustomTypesRepl);
    }
    private static JSC.JSValue SJ_CustomTypesRepl(JSC.JSValue thisBind, JSC.Arguments args) {
      if(args.Length == 2 && args[1].ValueType == JSC.JSValueType.String) {
        var s = args[1].Value as string;
        if(s != null) {
          if(s.StartsWith("¤BA")) {
            try {
              return new ByteArray(Convert.FromBase64String(s.Substring(3)));
            }
            catch(Exception ex) {
              Log.Warning("ParseJson({0}, {1}) - {2}", args[0], s, ex.Message);
              return new ByteArray();
            }
          }
          // 2015-09-16T14:15:18.994Z
          if(s.Length == 24 && s[4] == '-' && s[7] == '-' && s[10] == 'T' && s[13] == ':' && s[16] == ':' && s[19] == '.') {
            DateTimeOffset dto;
            if(!DateTimeOffset.TryParseExact(s, "yyyy-MM-ddTH:mm:ss.fffK", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out dto)) {
              // shaped like a timestamp but not one: keep the original text instead of
              // silently fabricating a value that would then be persisted as real data
              Log.Warning("ParseJson({0}) - {1} is not a valid timestamp", args[0], s);
              return args[1];
            }
            if(dto.Year == 1001) {  // sentinel: the sender asks the server to stamp the current time
              return X13.JsExtLib.Context.ProxyValue(DateTime.Now);
            }
            return X13.JsExtLib.Context.ProxyValue(dto.LocalDateTime);
          }
        }
      }
      return args[1];
    }
    public static JSC.JSValue ParseJson(string json) {
      return JSL.JSON.parse(json, _SJ_Replacer);
    }
    public static string Stringify(JSC.JSValue jv) {
      return JSL.JSON.stringify(jv, null, null, null);
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
        if(oc.IsObject()) {
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
    /// <summary>True when the value is a real object that can be indexed.</summary>
    /// <remarks>Both halves are required, and that is the whole point of having this by name:
    /// `ValueType == Object` alone is TRUE for JSValue.Null, whose null sits in Value, so the
    /// obvious-looking guard lets exactly the case it was written to stop straight through.
    /// The same defect was found and fixed three separate times before it was named (see
    /// todo.md #4). GetField below applies this very test at every hop, which is why the two
    /// live side by side: use GetField to read a path, this to ask about one value.
    /// <para>Lived in WorkspaceMenuBuilder until the dependency graph showed five files taking a
    /// dependency on a menu builder to reach it - while the rule that came out of #4 tells the
    /// reader to look in JsLib.</para></remarks>
    public static bool IsObject(this JSC.JSValue value) {
      return value != null && value.ValueType == JSC.JSValueType.Object && value.Value != null;
    }

    public static JSC.JSValue Field(this JSC.JSValue obj, string path) {
      if(obj == null) {
        return JSC.JSValue.NotExists;
      }
      if(string.IsNullOrEmpty(path)) {
        return obj;
      }
      var ps = path.Split(SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries);
      JSC.JSValue p = obj, c = null;
      for(int i = 0; i < ps.Length; i++) {
        if(!p.IsObject()) {
          return JSC.JSValue.NotExists;
        }
        c = p.GetProperty(ps[i]);
        p = c;
      }
      // a path of separators only (".", "..") splits into zero segments and leaves c unset
      return c ?? JSC.JSValue.NotExists;
    }
    public static JSC.JSValue Clone(JSC.JSValue org) {
      return Clone(org, null);
    }
    private static JSC.JSValue Clone(JSC.JSValue org, HashSet<object> path) {
      if(org == null || !org.Defined) {
        return org;
      }
      // Deliberately NOT IsObject(): this branch must also catch JSValue.Null, whose ValueType is
      // Object, so that Clone returns it unchanged instead of falling through to ProxyValue(null).
      if(org.ValueType == JSC.JSValueType.Object) {
        if(org.Value == null) {
          return org;  // JSValue.Null, nothing to copy
        }
        if(path == null) {
          path = new HashSet<object>(RefComparer.I);
        }
        // identity is the underlying object, not the JSValue: NiL.JS hands out different
        // JSValue wrappers for one and the same object
        if(!path.Add(org.Value)) {  // already on the current branch => cyclic reference
          Log.Warning("JsLib.Clone - cyclic reference, branch replaced by null");
          return JSC.JSValue.Null;
        }
        try {
          // an Array must stay an Array: a plain object copy loses length and the Array prototype
          JSC.JSValue ret = (org.Value is JSL.Array) ? (JSC.JSValue)new JSL.Array() : JSC.JSObject.CreateObject();
          foreach(var kv in org) {
            ret[kv.Key] = Clone(kv.Value, path);
          }
          return ret;
        }
        finally {
          path.Remove(org.Value);  // siblings may legitimately share a node, only a cycle is an error
        }
      }
      return X13.JsExtLib.Context.ProxyValue(org.Value);
    }
    /// <summary>Identity comparer; .NET Framework has no built-in ReferenceEqualityComparer.</summary>
    private sealed class RefComparer : IEqualityComparer<object> {
      public static readonly RefComparer I = new RefComparer();
      public new bool Equals(object a, object b) {
        return object.ReferenceEquals(a, b);
      }
      public int GetHashCode(object o) {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
      }
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
      if(v1.Value is JSL.Date d1 && v2.Value is JSL.Date d2) {
        return d1.ToDateTime() == d2.ToDateTime();  // JSL.Date does not override Equals
      }
      return object.Equals(v1.Value, v2.Value);
    }

    /// <summary>True for C# null, Undefined, NotExists and JSValue.Null alike.</summary>
    /// <remarks>Names the triplet `v == null || !v.Defined || v.IsNull`, which was written out by
    /// hand in six places. The three are genuinely different states in NiL.JS and none of the
    /// library predicates covers all three: IsUndefined() misses JSValue.Null, and Defined is TRUE
    /// for it.</remarks>
    public static bool IsNullOrUndefined(this JSC.JSValue v) {
      return v == null || !v.Defined || v.IsNull;
    }

    /// <summary>Reads a number, rejecting NaN as well as the wrong type.</summary>
    /// <remarks>Strict like the readers above - a numeric STRING still yields the default. NaN is
    /// rejected because a NaN that reached storage or the wire is indistinguishable from a real
    /// reading afterwards; this exact guard was spelled out five times over (the archive retention
    /// read in three storage plugins).</remarks>
    /// <summary>Reads a value at a dotted PATH below this one, safely at every hop.</summary>
    /// <remarks>Prefer these over indexing first: indexing and then reading throws when v is
    /// JSValue.Null or Undefined, while `v.AsBool("k", false)` goes through Field, which checks
    /// the container at each step and returns NotExists instead.
    /// <para><b>The argument is a path, not a key.</b> It is split on '.', so AsString("MQTT-SN.tag")
    /// looks for v["MQTT-SN"]["tag"] and NOT for a field literally named "MQTT-SN.tag". For a key
    /// that contains a dot, index it and read with the one-argument form.</para></remarks>
    public static double AsDouble(this JSC.JSValue v, double def=0) {
      if(v == null || !v.IsNumber) {
        return def;
      }
      double d = (double)v;
      return double.IsNaN(d) ? def : d;
    }
    public static double AsDouble(this JSC.JSValue v, string path, double def=0) {
      return v.Field(path).AsDouble(def);
    }

    public static bool AsBool(this JSC.JSValue v, bool def=false) {
      return (v == null || v.ValueType != JSC.JSValueType.Boolean) ? def : ((bool)v);
    }
    public static bool AsBool(this JSC.JSValue v, string path, bool def=false) {
      v = v.Field(path);
      return (v == null || v.ValueType != JSC.JSValueType.Boolean) ? def : ((bool)v);
    }

    public static int AsInt(this JSC.JSValue v, int def=0) {
      return (v == null || !v.IsNumber) ? def : ((int)v);
    }
    public static int AsInt(this JSC.JSValue v, string path, int def=0) {
      v = v.Field(path);
      return ( v == null || !v.IsNumber ) ? def : ( (int)v );
    }
    public static string AsString(this JSC.JSValue v, string def = null) {
      return (v == null || v.ValueType!=JSC.JSValueType.String) ? def : (v.Value as string);
    }
    public static string AsString(this JSC.JSValue v, string path, string def) {
      v = v.Field(path);
      return ( v == null || v.ValueType!=JSC.JSValueType.String ) ? def : ( v.Value as string );
    }

    internal static void Propertys(ref SortedList<string, JSC.JSValue> l, JSC.JSValue o) {
      if(!o.IsObject()) {
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
        _val = src == null ? new byte[0] : src._val;  // never leave _val null
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
          if(pos < 0) {
            pos = 0;  // clamp like the src == null branch does, BlockCopy rejects a negative count
          }
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
    public static bool IsByteArray(JSC.JSValue value, out ByteArray data) {
      data = value as ByteArray;
      if(data == null && value != null) {
        data = value.Value as ByteArray;
      }
      return data != null;
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
    // value semantics: JsLib.Equal gates change detection on object.Equals, and every JSON
    // round trip mints a fresh ByteArray, so identity comparison reports endless false changes
    public override bool Equals(object obj) {
      if(object.ReferenceEquals(this, obj)) {
        return true;
      }
      var o = obj as ByteArray;
      if(o == null) {
        return false;
      }
      byte[] a = _val, b = o._val;
      if(a == null || b == null) {
        return a == b;
      }
      if(a.Length != b.Length) {
        return false;
      }
      for(int i = 0; i < a.Length; i++) {
        if(a[i] != b[i]) {
          return false;
        }
      }
      return true;
    }
    public override int GetHashCode() {  // _val is assigned in the constructors only, so this is stable
      if(_val == null) {
        return 0;
      }
      int h = 17;
      for(int i = 0; i < _val.Length; i++) {
        h = h * 31 + _val[i];
      }
      return h;
    }
    public override string ToString() {
      return BitConverter.ToString(_val);
    }
  }
}
