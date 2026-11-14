using NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace X13.Plc {
  public sealed class PlcCapabilities {
    public const string PinsField = "MQTT-SN.AvailablePins", UartsField = "MQTT-SN.AvailableUarts";
    public sealed class Mode {
      public string Name, Io;
      public int Value;
      public JSValue Options;
      public readonly SortedSet<int> Pins = new SortedSet<int>();
    }
    public sealed class Bus {
      public string Kind;
      public int Number;
      public readonly Dictionary<string, Mode> Roles = new Dictionary<string, Mode>(StringComparer.OrdinalIgnoreCase);
      public bool Available {
        get {
          if(Kind != "serial") return Roles.Values.All(m => m.Pins.Count > 0);
          return Roles["TX"].Pins.Any(tx => Roles["RX"].Pins.Any(rx => rx != tx));
        }
      }
    }
    public readonly Dictionary<string, int> Names = new Dictionary<string, int>(StringComparer.Ordinal);
    public readonly Dictionary<string, Mode> Modes = new Dictionary<string, Mode>(StringComparer.Ordinal);
    public readonly List<Bus> Buses = new List<Bus>();
    public byte[] AvailablePins;
    public byte? AvailableUarts;

    public bool PinAvailable(int pin) { return pin >= 0 && (AvailablePins == null || pin < 40 && (AvailablePins[pin / 8] & (1 << (pin % 8))) != 0); }
    public bool UartAvailable(int port) { return (port == 1 || port == 2) && (!AvailableUarts.HasValue || (AvailableUarts.Value & (1 << port)) != 0); }
    public bool ModeAvailable(Mode mode) {
      var m = Regex.Match(mode.Name, @"^UART([12])_");
      return !m.Success || UartAvailable(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }
    public bool TryPin(JSValue value, out int pin) {
      pin = 0;
      return TryInt(value, out pin) && pin >= 0 || value != null && value.AsString(null) != null && Names.TryGetValue(value.AsString(null), out pin);
    }
    public bool TryConstant(string name, out int number) {
      Mode m;
      if(name != null && Modes.TryGetValue(name, out m)) { number = m.Value; return true; }
      number = 0;
      return name != null && Names.TryGetValue(name, out number);
    }
    public string PinLabel(int pin) { return Names.FirstOrDefault(p => p.Value == pin && ConstructorSchema.IsName(p.Key)).Key ?? pin.ToString(CultureInfo.InvariantCulture); }
    public JSValue PinValue(int pin) {
      string name = Names.FirstOrDefault(p => p.Value == pin && ConstructorSchema.IsName(p.Key)).Key;
      return name == null ? (JSValue)pin : (JSValue)name;
    }
    public static bool TryInt(JSValue value, out int number) {
      number = 0;
      if(value == null || !value.IsNumber) return false;
      double d = (double)value;
      if(double.IsNaN(d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;
      number = (int)d; return true;
    }
    public static byte[] ReadPins(JSValue value) {
      if(value == null || !value.Defined || value.IsNull) return null;
      ByteArray bytes;
      if(ByteArray.IsByteArray(value, out bytes)) {
        if(bytes.GetBytes().Length != 5) throw new FormatException("AvailablePins must contain five bytes");
        return bytes.GetBytes().ToArray();
      }
      var values = ConstructorSchema.IsArray(value) ? value.Select(x => x.Value).ToArray() : new JSValue[0];
      if(values.Length != 5) throw new FormatException("AvailablePins must contain five bytes");
      var r = new byte[5];
      for(int i = 0; i < 5; i++) {
        int n;
        if(!TryInt(values[i], out n) || n < 0 || n > 255) throw new FormatException("AvailablePins must contain bytes");
        r[i] = (byte)n;
      }
      return r;
    }
    public static byte? ReadUarts(JSValue value) {
      if(value == null || !value.Defined || value.IsNull) return null;
      int n;
      if(!TryInt(value, out n) || n < 0 || n > 255) throw new FormatException("AvailableUarts must be UInt8");
      return (byte)n;
    }
    public static PlcCapabilities Read(JSValue plc, JSValue pins = null, JSValue uarts = null) {
      var c = new PlcCapabilities { AvailablePins = ReadPins(pins), AvailableUarts = ReadUarts(uarts) };
      if(!plc.IsObject()) return c;
      var names = plc.Field("names");
      if(names.IsObject()) foreach(var entry in names) {
        int n;
        if(TryInt(entry.Value, out n)) c.Names[entry.Key] = n;
      }
      var groups = plc.Field("groups");
      if(groups.IsObject()) foreach(var group in groups) {
        var pm = group.Value.Field("modes"); var pp = group.Value.Field("pins");
        if(!pm.IsObject() || !pp.IsObject()) continue;
        foreach(var entry in pm) {
          int v;
          if(!TryInt(entry.Value.Field("v"), out v)) continue;
          Mode mode;
          if(!c.Modes.TryGetValue(entry.Key, out mode)) {
            mode = new Mode { Name = entry.Key, Value = v, Io = entry.Value.AsString("io", null), Options = entry.Value.Field("opt") };
            c.Modes.Add(entry.Key, mode);
          }
          foreach(var p in pp) { int n; if(c.TryPin(p.Value, out n) && c.PinAvailable(n) && c.ModeAvailable(mode)) mode.Pins.Add(n); }
        }
      }
      int offset = c.Modes.ContainsKey("TWI0_SDA") || c.Modes.ContainsKey("TWI0_SCL") ? 0 : -1;
      foreach(var mode in c.Modes.Values) {
        var match = Regex.Match(mode.Name, @"^TWI(0|[1-9]\d*)_SDA$");
        int index; Mode other;
        if(match.Success && int.TryParse(match.Groups[1].Value, out index) && index + offset >= 0 && mode.Io == null
          && c.Modes.TryGetValue("TWI" + match.Groups[1].Value + "_SCL", out other) && other.Io == null) {
          var bus = new Bus { Kind = "twi", Number = index + offset };
          bus.Roles.Add("SDA", mode); bus.Roles.Add("SCL", other); c.Buses.Add(bus);
        }
      }
      for(int port = 1; port <= 2; port++) {
        Mode tx, rx, de;
        if(!c.Modes.TryGetValue("UART" + port + "_TX", out tx) || !c.Modes.TryGetValue("UART" + port + "_RX", out rx)
          || tx.Io != null || rx.Io != null || !c.UartAvailable(port)) continue;
        var bus = new Bus { Kind = "serial", Number = port };
        bus.Roles.Add("TX", tx); bus.Roles.Add("RX", rx);
        if(c.Modes.TryGetValue("UART" + port + "_DE", out de) && de.Io == null) bus.Roles.Add("DE", de);
        c.Buses.Add(bus);
      }
      return c;
    }
  }

  public sealed class ConstructorInstance {
    public string Name;
    public ConstructorSchema Schema;
    public JSValue Values;
  }
  public sealed class ConstructorChoice {
    public JSValue Value;
    public string Label;
    public bool Enabled = true;
  }
  public sealed class ConstructorResult {
    public readonly Dictionary<string, JSValue> Values = new Dictionary<string, JSValue>(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.Ordinal);
    public readonly HashSet<string> Dependencies = new HashSet<string>(StringComparer.Ordinal);
    internal bool Cyclic;
    public bool Ok { get { return Errors.Count == 0; } }
  }

  public sealed class ConstructorResolver {
    private readonly PlcCapabilities _device;
    private readonly List<ConstructorInstance> _instances;
    private readonly Dictionary<string, ConstructorResult> _results = new Dictionary<string, ConstructorResult>(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new HashSet<string>(StringComparer.Ordinal);
    public ConstructorResolver(PlcCapabilities device, IEnumerable<ConstructorInstance> instances) {
      _device = device ?? new PlcCapabilities(); _instances = instances.ToList();
    }
    public ConstructorResult Resolve(string name) {
      ConstructorResult result;
      if(_results.TryGetValue(name, out result)) return result;
      var instance = _instances.Single(i => i.Name == name);
      result = new ConstructorResult();
      if(!_resolving.Add(name)) { result.Cyclic = true; result.Errors[""] = "constructor resource dependency cycle"; return result; }
      foreach(string error in instance.Schema.Errors) result.Errors[""] = error;
      foreach(var arg in instance.Schema.Arguments) ResolveArgument(instance, arg, result, new HashSet<string>());
      foreach(var resource in instance.Schema.Provides) {
        int number;
        JSValue v;
        if(!result.Values.TryGetValue(resource.NumberArg, out v) || !PlcCapabilities.TryInt(v, out number)
          || !_device.Buses.Any(b => b.Kind == resource.Kind && b.Number == number && b.Available)) {
          result.Errors[resource.NumberArg] = "resource number must identify supported hardware";
          continue;
        }
        if(Providers(resource.Kind, number).Count != 1) result.Errors[resource.NumberArg] = resource.Kind + number + " already has a master";
      }
      _resolving.Remove(name);
      _results[name] = result;
      return result;
    }
    private JSValue ResolveArgument(ConstructorInstance instance, ConstructorArgument arg, ConstructorResult result, HashSet<string> visiting) {
      JSValue value;
      if(result.Values.TryGetValue(arg.Name, out value)) return value;
      if(!visiting.Add(arg.Name)) { result.Errors[arg.Name] = "argument dependency cycle"; return JSValue.Undefined; }
      value = instance.Values.Field(arg.Name);
      var rule = arg.Derive ?? arg.Choices ?? arg.Option;
      if(rule != null && rule.BusArg != null) {
        var other = instance.Schema.Find(rule.BusArg);
        if(other != null) ResolveArgument(instance, other, result, visiting);
      }
      if(arg.Derive != null) {
        var mode = Mode(instance, arg.Derive, result);
        var pin = arg.Derive.PinArg == null ? null : instance.Schema.Find(arg.Derive.PinArg);
        bool disabled = pin != null && pin.Choices?.AllowNone == true
          && ConstructorSchema.Same(ResolveArgument(instance, pin, result, visiting), (JSValue)(-1));
        value = disabled ? (JSValue)(-1) : mode == null ? JSValue.Undefined : (JSValue)mode.Value;
      }
      result.Values[arg.Name] = value;
      visiting.Remove(arg.Name);
      bool absent = value == null || !value.Defined || value.IsNull;
      if(absent) { if(arg.Required && !result.Errors.ContainsKey(arg.Name)) result.Errors[arg.Name] = "required constructor argument is unset"; return value; }
      int constant;
      bool literal = ConstructorSchema.IsLiteral(value);
      if(!literal || value.AsString(null) != null && !_device.TryConstant(value.AsString(null), out constant)) {
        result.Errors[arg.Name] = "constructor argument must be a finite literal or device constant"; return value;
      }
      if(value.IsNumber || value.AsString(null) != null) {
        double n = value.IsNumber ? (double)value : _device.TryConstant(value.AsString(null), out constant) ? constant : double.NaN;
        if(arg.Editor == "Boolean" || arg.Editor == "Integer" && Math.Floor(n) != n || arg.Min.HasValue && n < arg.Min || arg.Max.HasValue && n > arg.Max) result.Errors[arg.Name] = "value outside argument bounds or editor type";
      } else if(arg.Editor == "Integer" && value.ValueType == JSValueType.Boolean || arg.Editor == "Boolean" && value.ValueType != JSValueType.Boolean) result.Errors[arg.Name] = "value does not match editor type";
      if(arg.Choices != null) {
        var choices = Choices(instance, arg, result, true);
        if(!choices.Any(c => c.Enabled && EqualChoice(arg, c.Value, value))) result.Errors[arg.Name] = "value is unavailable for " + arg.Name + (arg.Choices.ExcludeArgs.Count == 0 ? "" : "; pins must be distinct");
      }
      if(arg.Option != null) {
        var mode = Mode(instance, arg.Option, result);
        int n, min, max;
        var opt = mode?.Options;
        var values = opt.Field("values");
        bool valid = PlcCapabilities.TryInt(value, out n) && mode != null
          && (values.IsObject() ? values.Any(v => ConstructorSchema.Same(v.Value, value))
            : PlcCapabilities.TryInt(opt.Field("min"), out min) && PlcCapabilities.TryInt(opt.Field("max"), out max) && n >= min && n <= max);
        if(!valid) result.Errors[arg.Name] = "value is outside the device mode options";
      }
      string formatError;
      Display(arg, value, out formatError);
      if(formatError != null) result.Errors[arg.Name] = formatError;
      return value;
    }
    public bool EqualChoice(ConstructorArgument arg, JSValue a, JSValue b) {
      int x, y;
      return arg.Choices != null && arg.Choices.Provider == "pin"
        ? arg.Choices.AllowNone && ConstructorSchema.Same(a, (JSValue)(-1)) && ConstructorSchema.Same(b, a)
          || _device.TryPin(a, out x) && _device.TryPin(b, out y) && x == y
        : ConstructorSchema.Same(a, b);
    }
    private List<ConstructorInstance> Providers(string kind, int number) {
      return _instances.Where(i => i.Schema.Provides.Any(p => {
        int n; return p.Kind == kind && PlcCapabilities.TryInt(i.Values.Field(p.NumberArg), out n) && n == number;
      })).ToList();
    }
    private PlcCapabilities.Mode Mode(ConstructorInstance instance, ConstructorRule rule, ConstructorResult result) {
      PlcCapabilities.Mode mode;
      if(rule.Mode != null) return _device.Modes.TryGetValue(rule.Mode, out mode) ? mode : null;
      JSValue v; int number;
      if(!result.Values.TryGetValue(rule.BusArg, out v)) v = instance.Values.Field(rule.BusArg);
      var bus = PlcCapabilities.TryInt(v, out number) ? _device.Buses.FirstOrDefault(b => b.Kind == rule.Kind && b.Number == number) : null;
      return bus != null && bus.Roles.TryGetValue(rule.Role, out mode) ? mode : null;
    }
    public List<ConstructorChoice> Choices(string name, string argument) {
      var i = _instances.Single(x => x.Name == name);
      var a = i.Schema.Find(argument);
      return a == null ? new List<ConstructorChoice>() : Choices(i, a, Resolve(name));
    }
    private List<ConstructorChoice> Choices(ConstructorInstance instance, ConstructorArgument arg, ConstructorResult result, bool selectedOnly = false) {
      var choices = new List<ConstructorChoice>(); var rule = arg.Choices;
      if(rule == null) return choices;
      if(rule.Provider == "list") {
        choices.AddRange(rule.Values.Select(v => new ConstructorChoice { Value = v, Label = Display(arg, v) }));
      } else if(rule.Provider == "pin") {
        var mode = Mode(instance, rule, result);
        var excluded = new HashSet<int>();
        foreach(string name in rule.ExcludeArgs) {
          int pin;
          if(_device.TryPin(instance.Values.Field(name), out pin)) excluded.Add(pin);
        }
        if(rule.AllowNone) choices.Add(new ConstructorChoice { Value = -1, Label = "Disabled" });
        if(mode != null) foreach(int n in mode.Pins) choices.Add(new ConstructorChoice { Value = _device.PinValue(n), Label = _device.PinLabel(n), Enabled = !excluded.Contains(n) });
      } else if(rule.Provider == "bus-option") {
        var mode = Mode(instance, rule, result);
        var values = mode?.Options.Field("values");
        if(values.IsObject()) foreach(var v in values) choices.Add(new ConstructorChoice { Value = v.Value, Label = v.Key });
      } else if(rule.Provider == "bus") {
        foreach(var bus in _device.Buses.Where(b => b.Kind == rule.Kind && b.Available).OrderBy(b => b.Number)) {
          JSValue selected;
          bool isSelected = result.Values.TryGetValue(arg.Name, out selected) && ConstructorSchema.Same(selected, (JSValue)bus.Number);
          if(selectedOnly && !isSelected) continue;
          if(bus.Kind == "twi" && !bus.Roles["SDA"].Pins.Any(sda => bus.Roles["SCL"].Pins.Any(scl => scl != sda))) continue;
          bool available = true;
          if(rule.Scope == "configured") {
            var providers = Providers(rule.Kind, bus.Number);
            available = providers.Count == 1;
            if(available) {
              var provider = Resolve(providers[0].Name);
              if(provider.Cyclic && isSelected) { result.Cyclic = true; result.Errors[""] = "constructor resource dependency cycle"; }
              available = providers[0].Name != instance.Name && provider.Ok;
            }
            if(available && isSelected) result.Dependencies.Add(providers[0].Name);
          }
          if(available) choices.Add(new ConstructorChoice { Value = bus.Number, Label = (bus.Kind == "twi" ? "TWI" : "UART") + bus.Number });
        }
      }
      return choices;
    }
    public static string Display(ConstructorArgument argument, JSValue value) {
      string error;
      return Display(argument, value, out error);
    }
    public static string Display(ConstructorArgument argument, JSValue value, out string error) {
      error = null;
      if(value == null || !value.Defined || value.IsNull) return "<unset>";
      if(argument.Choices?.AllowNone == true && ConstructorSchema.Same(value, (JSValue)(-1))) return "Disabled";
      string text = value.AsString(null) ?? JsLib.Stringify(value);
      if(text.Length > ConstructorSchema.MaxText) { error = "display value too long"; return text.Substring(0, ConstructorSchema.MaxText - 1) + "…"; }
      error = ConstructorSchema.FormatError(argument.Format);
      if(error != null || argument.Format == null || value.AsString(null) != null) return text;
      try {
        if(!value.IsNumber && value.ValueType != JSValueType.Boolean) { error = "invalid constructor value"; return text; }
        object raw = value.IsNumber ? (object)(double)value : ((bool)value ? "true" : "false");
        if(value.IsNumber && (double)value == Math.Floor((double)value) && (double)value >= int.MinValue && (double)value <= uint.MaxValue) raw = (long)(double)value;
        string formatted = string.Format(CultureInfo.InvariantCulture, argument.Format, raw);
        if(formatted.Length > ConstructorSchema.MaxText) throw new FormatException("formatted value too long");
        return formatted;
      } catch(FormatException ex) { error = ex.Message; return text; }
    }
  }
}
