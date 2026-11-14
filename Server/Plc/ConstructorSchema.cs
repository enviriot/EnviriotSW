using NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace X13.Plc {
  public static class PlcScript {
    public const string SerialPort = "LPLC/SerialPort";
    public static bool UsesState(string type) { return type == "LPLC/Func" || type == SerialPort; }
  }

  public sealed class ConstructorRule {
    public string Provider, Kind, Scope, BusArg, Role, Mode, ExcludeArg, PinArg;
    public bool AllowNone;
    public readonly List<string> ExcludeArgs = new List<string>();
    public readonly List<JSValue> Values = new List<JSValue>();

    internal static ConstructorRule Read(JSValue value) {
      if(!value.IsObject()) throw new FormatException("provider descriptor must be an object");
      var r = new ConstructorRule {
        Provider = value.AsString("provider", null), Kind = value.AsString("kind", null),
        Scope = value.AsString("scope", "supported"), BusArg = value.AsString("busArg", null),
        Role = value.AsString("role", null), Mode = value.AsString("mode", null),
        ExcludeArg = value.AsString("excludeArg", null), PinArg = value.AsString("pinArg", null),
        AllowNone = value.AsBool("allowNone", false)
      };
      if(value.Field("allowNone").Defined && value.Field("allowNone").ValueType != JSValueType.Boolean)
        throw new FormatException("allowNone must be Boolean");
      if(r.AllowNone && r.Provider != "pin") throw new FormatException("allowNone requires a pin provider");
      if(r.PinArg != null && r.Provider != "bus-mode") throw new FormatException("pinArg requires bus-mode");
      if(r.ExcludeArg != null) r.ExcludeArgs.Add(r.ExcludeArg);
      var exclude = value.Field("excludeArgs");
      if(exclude.Defined) {
        if(!ConstructorSchema.IsArray(exclude)) throw new FormatException("excludeArgs must be an array");
        foreach(var item in exclude) {
          string name = item.Value.AsString(null);
          if(!ConstructorSchema.IsName(name)) throw new FormatException("excludeArgs must contain argument names");
          if(!r.ExcludeArgs.Contains(name)) r.ExcludeArgs.Add(name);
        }
      }
      if(r.Provider == "list") {
        var values = value.Field("values");
        if(values.ValueType != JSValueType.Object || !(values is NiL.JS.BaseLibrary.Array)) {
          // JSON values can be wrapped JSValues rather than the Array itself.
          if(!(values.Value is NiL.JS.BaseLibrary.Array)) throw new FormatException("list values must be an array");
        }
        foreach(var v in values) {
          if(!ConstructorSchema.IsLiteral(v.Value)) throw new FormatException("list values must be finite numbers, booleans or constant names");
          if(r.Values.Any(x => ConstructorSchema.Same(x, v.Value))) throw new FormatException("duplicate list value");
          r.Values.Add(v.Value);
        }
      } else if(r.Provider != "pin" && r.Provider != "bus" && r.Provider != "bus-mode" && r.Provider != "bus-option") {
        throw new FormatException("unknown provider: " + (r.Provider ?? "<unset>"));
      }
      if(r.Provider == "bus" || r.Provider == "bus-mode" || r.Provider == "bus-option" || r.Provider == "pin" && r.Mode == null) {
        if(r.Kind != "twi" && r.Kind != "serial") throw new FormatException("bus kind must be twi or serial");
      }
      if(r.Provider == "bus" && r.Scope != "supported" && r.Scope != "configured") throw new FormatException("invalid bus scope");
      if(r.Provider == "bus-mode" || r.Provider == "bus-option" || r.Provider == "pin" && r.Mode == null) {
        if(!ConstructorSchema.IsName(r.BusArg) || string.IsNullOrEmpty(r.Role)) throw new FormatException("busArg and role are required");
      }
      return r;
    }
  }

  public sealed class ConstructorArgument {
    public string Name, Label, Editor, Format;
    public int Position;
    public bool Readonly, Required;
    public double? Min, Max;
    public JSValue Default;
    public ConstructorRule Choices, Derive, Option;
  }

  public sealed class ConstructorResource {
    public string Kind, NumberArg;
  }

  public sealed class ConstructorSchema {
    public const int MaxRows = 256, MaxText = 256;
    private static readonly Regex NamePattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    public string[] Parameters = new string[0];
    public readonly List<ConstructorArgument> Arguments = new List<ConstructorArgument>();
    public readonly List<ConstructorResource> Provides = new List<ConstructorResource>();
    public readonly List<string> Errors = new List<string>();
    public bool HasMetadata;

    public static bool IsName(string name) { return name != null && NamePattern.IsMatch(name); }
    public static bool IsArray(JSValue value) { return value != null && (value is NiL.JS.BaseLibrary.Array || value.Value is NiL.JS.BaseLibrary.Array); }
    public static bool IsLiteral(JSValue v) {
      return v != null && (v.IsNumber && !double.IsNaN((double)v) && !double.IsInfinity((double)v)
        || v.ValueType == JSValueType.Boolean || IsName(v.AsString(null)));
    }
    public static bool Same(JSValue a, JSValue b) {
      if(a == null || b == null) return a == b;
      if(a.IsNumber && b.IsNumber) return (double)a == (double)b;
      return a.ValueType == b.ValueType && JsLib.Stringify(a) == JsLib.Stringify(b);
    }
    public static string Literal(JSValue v) {
      if(!IsLiteral(v)) return null;
      return v.AsString(null) ?? (v.IsNumber ? ((double)v).ToString("R", CultureInfo.InvariantCulture) : (bool)v ? "true" : "false");
    }
    public ConstructorArgument Find(string name) { return Arguments.FirstOrDefault(a => a.Name == name); }

    public static ConstructorSchema Read(JSValue type) {
      var s = new ConstructorSchema();
      var lplc = type.Field("LPLC");
      var parameters = lplc.Field("args");
      if(parameters.Defined && !IsArray(parameters)) s.Errors.Add("constructor parameters must be an array");
      if(parameters.IsObject()) s.Parameters = parameters.Select(p => p.Value.AsString(null)).ToArray();
      if(s.Parameters.Any(p => !IsName(p)) || s.Parameters.Distinct().Count() != s.Parameters.Length) s.Errors.Add("constructor parameters must be distinct names");
      var descriptors = type.Field("mi.LPLC.mi.args.mi");
      var names = s.Parameters.Where(IsName).ToList();
      if(descriptors.IsObject()) foreach(var d in descriptors) if(!names.Contains(d.Key)) names.Add(d.Key);
      int last = 0;
      var positions = new HashSet<int>();
      var fallback = new List<ConstructorArgument>();
      foreach(string name in names) {
        if(!IsName(name)) { s.Errors.Add("invalid argument name: " + name); continue; }
        var d = descriptors.Field(name);
        if(d.IsObject() && new[] { "cdr", "label", "format", "readonly", "required", "min", "max", "choices", "derive", "option" }.Any(k => d.Field(k).Defined)) s.HasMetadata = true;
        var a = new ConstructorArgument { Name = name, Label = d.AsString("label", name), Editor = d.AsString("editor", null),
          Format = d.AsString("format", null), Readonly = d.AsBool("readonly", false), Required = d.AsBool("required", false) || s.Parameters.Contains(name), Default = d.Field("default") };
        s.Arguments.Add(a);
        try {
          if(d.Defined && !d.IsObject()) throw new FormatException("argument descriptor must be an object");
          foreach(string flag in new[] { "readonly", "required" }) if(d.Field(flag).Defined && d.Field(flag).ValueType != JSValueType.Boolean) throw new FormatException(flag + " must be Boolean");
          foreach(string text in new[] { "label", "editor", "format" }) if(d.Field(text).Defined && d.Field(text).ValueType != JSValueType.String) throw new FormatException(text + " must be a string");
          if(d.Field("cdr").Defined) {
            s.HasMetadata = true;
            double row = d.Field("cdr").IsNumber ? (double)d.Field("cdr") : -1;
            if(double.IsNaN(row) || row < 0 || row >= MaxRows || row != Math.Floor(row)) throw new FormatException("cdr must be 0..255");
            a.Position = (int)row;
            if(a.Position > 0 && !positions.Add(a.Position)) throw new FormatException("duplicate cdr row");
            last = Math.Max(last, a.Position);
          } else if(s.Parameters.Contains(name)) fallback.Add(a);
          if(a.Label.Length > MaxText || a.Format != null && a.Format.Length > MaxText) throw new FormatException("label/format too long");
          if(d.Field("min").Defined) a.Min = Bound(d.Field("min"));
          if(d.Field("max").Defined) a.Max = Bound(d.Field("max"));
          if(a.Min > a.Max) throw new FormatException("min exceeds max");
          if(d.Field("choices").Defined) { a.Choices = ConstructorRule.Read(d.Field("choices")); s.HasMetadata = true; }
          if(d.Field("option").Defined) {
            a.Option = ConstructorRule.Read(d.Field("option")); s.HasMetadata = true;
            if(a.Option.Provider != "bus-option") throw new FormatException("option requires bus-option");
          }
          if(d.Field("derive").Defined) {
            a.Derive = ConstructorRule.Read(d.Field("derive")); a.Readonly = true; s.HasMetadata = true;
            if(a.Derive.Provider != "bus-mode") throw new FormatException("unsupported derivation");
          }
          if(a.Choices != null && a.Choices.Provider == "bus-mode") throw new FormatException("bus-mode is a derivation, not choices");
        } catch(FormatException e) { s.Errors.Add(name + ": " + e.Message); }
      }
      foreach(var a in fallback) {
        a.Position = ++last;
        if(last >= MaxRows) s.Errors.Add(a.Name + ": too many constructor rows");
      }
      var provides = lplc.Field("provides");
      if(provides.Defined && !IsArray(provides)) s.Errors.Add("resource declarations must be an array");
      if(provides.IsObject()) foreach(var entry in provides) {
        s.HasMetadata = true;
        var r = new ConstructorResource { Kind = entry.Value.AsString("kind", null), NumberArg = entry.Value.AsString("numberArg", null) };
        if((r.Kind != "twi" && r.Kind != "serial") || s.Find(r.NumberArg) == null) s.Errors.Add("invalid resource declaration");
        else if(s.Provides.Any(p => p.Kind == r.Kind && p.NumberArg == r.NumberArg)) s.Errors.Add("duplicate resource declaration");
        else s.Provides.Add(r);
      }
      foreach(var a in s.Arguments) foreach(var rule in new[] { a.Choices, a.Derive, a.Option }.Where(r => r != null)) {
        foreach(var reference in new[] { rule.BusArg, rule.PinArg }.Concat(rule.ExcludeArgs).Where(r => r != null)) {
          if(s.Find(reference) == null) s.Errors.Add(a.Name + ": unknown argument " + reference);
        }
        if(rule.PinArg != null && s.Find(rule.PinArg)?.Choices?.AllowNone != true)
          s.Errors.Add(a.Name + ": pinArg must refer to an optional pin");
      }
      return s;
    }

    public static string FormatError(string format) {
      if(format == null) return null;
      if(format.Length > MaxText) return "format too long";
      var pattern = new Regex(@"\G(?:[^{}]+|\{\{|\}\}|\{0(?:,\s*(-?\d{1,3}))?(?::([dDxXfFeEgG])(\d{1,2})?)?\})");
      int offset = 0;
      while(offset < format.Length) {
        var m = pattern.Match(format, offset);
        if(!m.Success || m.Index != offset) return "unsupported composite format";
        if(m.Groups[1].Success && Math.Abs(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) > MaxText) return "format alignment too wide";
        if(m.Groups[3].Success && int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) > 16) return "format precision too large";
        offset += m.Length;
      }
      return null;
    }
    private static double Bound(JSValue value) {
      if(!value.IsNumber || double.IsNaN((double)value) || double.IsInfinity((double)value)) throw new FormatException("bound must be finite");
      return (double)value;
    }
  }
}
