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
  /// <summary>Единый словарь чтения JSValue: строгие читатели, поля по составному пути, JSON.</summary>
  /// <remarks>Внешние значения должны читаться только этими методами.</remarks>
  public static class JsLib {

    public static readonly char[] SPLITTER_OBJ = new char[] { '.' };
    private static readonly JSF.ExternalFunction _SJ_Replacer;
    static JsLib() {
      _SJ_Replacer = new JSF.ExternalFunction(SJ_CustomTypesRepl);
    }
    private static JSC.JSValue SJ_CustomTypesRepl(JSC.JSValue thisBind, JSC.Arguments args) {
      if(args.Length == 2 && args[1].ValueType == JSC.JSValueType.String) {
        if (args[1].Value is string s) {
          if (s.StartsWith("¤BA")) {
            try {
              return new ByteArray(Convert.FromBase64String(s.Substring(3)));
            }
            catch (Exception ex) {
              Log.Warning("ParseJson({0}, {1}) - {2}", args[0], s, ex.Message);
              return new ByteArray();
            }
          }
          // 2015-09-16T14:15:18.994Z
          if (s.Length == 24 && s[4] == '-' && s[7] == '-' && s[10] == 'T' && s[13] == ':' && s[16] == ':' && s[19] == '.') {
            if (!DateTimeOffset.TryParseExact(s, "yyyy-MM-ddTH:mm:ss.fffK", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset dto)) {
              // строка имеет форму метки времени, но ею не является: сохраняем исходный текст вместо
              // незаметного создания значения, которое затем было бы сохранено как реальные данные
              Log.Warning("ParseJson({0}) - {1} is not a valid timestamp", args[0], s);
              return args[1];
            }
            if (dto.Year == 1001) {  // специальное значение: отправитель просит сервер установить текущее время
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
    /// <summary>Возвращает true, если значение является реальным объектом, доступным по индексу.</summary>
    /// <remarks>Необходимы обе части проверки. Именно ради этого метод и получил отдельное имя:
    /// одного условия `ValueType == Object` недостаточно, поскольку оно ИСТИННО для JSValue.Null,
    /// у которого null хранится в Value.</remarks>
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
      JSC.JSValue p = obj, c = JSC.JSValue.NotExists;  // путь, состоящий только из разделителей (".", ".."), разбивается на ноль сегментов и оставляет c неустановленным
      for (int i = 0; i < ps.Length; i++) {
        if(!p.IsObject()) {
          return JSC.JSValue.NotExists;
        }
        c = p.GetProperty(ps[i]);
        p = c;
      }
      return c;
    }

    /// <summary>Определяет, изменила ли что-либо запись <paramref name="b"/> поверх <paramref name="a"/>.</summary>
    /// <remarks>JSValue.Equals является собственной реализацией "===" движка: примитивы сравниваются по значению,
    /// строки — через CompareOrdinal, Integer считается равным соответствующему Double, undefined, NotExists
    /// и NotExistsInObject рассматриваются как одно состояние, а Object, Function, Date и Symbol сравниваются
    /// по ССЫЛКЕ.
    /// <para>При любых сомнениях возвращается false, для чего и предусмотрен catch: StrictEqual.Check выбрасывает
    /// NotImplementedException для не перечисленных им типов значений, например Property и SpreadOperatorResult.</para></remarks>
    public static bool SameValue(JSC.JSValue a, JSC.JSValue b) {
      if(object.ReferenceEquals(a, b)) {
        return true;
      }
      if(a == null || b == null) {
        return false;
      }
      try {
        return a.Equals(b);
      }
      catch(Exception) {
        return false;
      }
    }
    public static JSC.JSValue Clone(JSC.JSValue org) {
      return Clone(org, null);
    }
    private static JSC.JSValue Clone(JSC.JSValue org, HashSet<object> path) {
      if(org == null || !org.Defined) {
        return org;
      }
      if(org.ValueType == JSC.JSValueType.Object) {
        if(org.Value == null) {
          return org;  // JSValue.Null, копировать нечего
        }
        if(path == null) {
          path = new HashSet<object>(RefComparer.I);
        }
        // идентичность определяется базовым объектом, а не JSValue: NiL.JS выдаёт разные оболочки JSValue для одного и того же объекта
        if(!path.Add(org.Value)) {  // объект уже находится в текущей ветви, следовательно, обнаружена циклическая ссылка
          Log.Warning("JsLib.Clone - cyclic reference, branch replaced by null");
          return JSC.JSValue.Null;
        }
        try {
          // Array должен остаться Array: копия в виде обычного объекта теряет length и прототип Array
          JSC.JSValue ret = (org.Value is JSL.Array) ? (JSC.JSValue)new JSL.Array() : JSC.JSObject.CreateObject();
          foreach(var kv in org) {
            ret[kv.Key] = Clone(kv.Value, path);
          }
          return ret;
        }
        finally {
          path.Remove(org.Value);  // соседние ветви могут корректно использовать общий узел; ошибкой является только цикл
        }
      }
      return X13.JsExtLib.Context.ProxyValue(org.Value);
    }
    /// <summary>Компаратор идентичности; в .NET Framework нет встроенного ReferenceEqualityComparer.</summary>
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
        return d1.ToDateTime() == d2.ToDateTime();  // JSL.Date не переопределяет Equals
      }
      return object.Equals(v1.Value, v2.Value);
    }

    /// <summary>Возвращает true для C# null, Undefined, NotExists и JSValue.Null.</summary>
    public static bool IsNullOrUndefined(this JSC.JSValue v) {
      return v == null || !v.Defined || v.IsNull;
    }

    /// <summary>Читает число, отклоняя NaN и значения неверного типа.</summary>
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
        l = new SortedList<string, JSC.JSValue>(StringComparer.Ordinal);
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
        _val = src == null ? new byte[0] : src._val;  // никогда не оставляем _val равным null
        return;
      }
      if(src == null) {
        if(pos < 0) {
          pos = 0;
        }
        _val = new byte[pos + data.Length];
        Buffer.BlockCopy(data, 0, _val, pos, data.Length);
      } else {
        if(pos < 0) {  // отрицательное значение означает позицию с конца
          pos = src._val.Length + 1 + pos;
          if(pos < 0) {
            pos = 0;  // ограничиваем, как в ветви src == null; BlockCopy отклоняет отрицательное количество
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
    // семантика значения: JsLib.Equal использует object.Equals для определения изменений, а каждый цикл
    // сериализации JSON создаёт новый ByteArray, поэтому сравнение идентичности бесконечно сообщает ложные изменения
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
    public override int GetHashCode() {  // _val присваивается только в конструкторах, поэтому хеш стабилен
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
