///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace X13.DevicePLC {
  /// <summary>Пины и режимы узла: поле PLC в состоянии типа устройства, например /$YS/TYPES/MQTT-SN/E1S10.</summary>
  /// <remarks>Режимы и пины у контроллеров разные, поэтому компилятор берёт их отсюда, а не из своей таблицы.
  /// <code>
  /// "PLC": {
  ///   "names":  { "PA5": 5 },
  ///   "groups": {
  ///     "Outputs": {
  ///       "modes": {
  ///         "OUT": { "v": 5, "io": "out" },
  ///         "PWM": { "v": 11, "io": "out",
  ///                  "opt": { "hint": "frequency, Hz", "min": 1, "max": 40000000, "default": 1000 } } },
  ///       "pins": [0, 2, 4, "PA5"] } } }
  /// </code>
  /// <list type="bullet">
  /// <item>names - необязательно: имя пина и его номер в операнде IN/OUT. Имена режимов и пинов -
  /// константы программы: new Pin(PA5, OUT).</item>
  /// <item>v - номер режима для прошивки.</item>
  /// <item>io - что умеет блок Pin: in - только Tick(), out - ещё и Tick(x), io - оба (счётчик читает и
  /// задаёт счёт). Без io режим только для ConfigPin: шины.</item>
  /// <item>opt - параметр режима: min/max (включительно) или values (ключи - подписи для интерфейса блока),
  /// default обязателен. Без opt параметра у режима нет.</item>
  /// <item>pins обязателен: «любой пин» разрешил бы и резервные. Режим может стоять в нескольких группах -
  /// так у контроллера без матрицы ШИМ живёт в группе каждого таймера, - но v, io и opt у него одни.
  /// Режим разрешён на пине, если пин есть хотя бы в одной из его групп.</item>
  /// </list></remarks>
  internal sealed class EP_Device {
    internal enum PinIo {
      None,     // только ConfigPin
      In,
      Out,
      InOut,
    }

    internal sealed class Option {
      public int Min, Max, Default;
      /// <summary>Допустимые значения; null - диапазон Min..Max.</summary>
      public SortedSet<int> Values;

      public bool Accepts(int v) {
        return Values != null ? Values.Contains(v) : v >= Min && v <= Max;
      }
      public bool SameAs(Option o) {
        return o != null && Min == o.Min && Max == o.Max && Default == o.Default
          && (Values == null ? o.Values == null : o.Values != null && Values.SetEquals(o.Values));
      }
      public override string ToString() {
        return Values != null ? string.Join(", ", Values) : Min + ".." + Max;
      }
    }

    internal sealed class Mode {
      public string Name;
      public int Value;
      public PinIo Io;
      /// <summary>null - параметра нет.</summary>
      public Option Opt;
      /// <summary>Пины всех групп режима.</summary>
      public readonly HashSet<int> Pins = new HashSet<int>();

      public bool SameAs(Mode o) {
        return Value == o.Value && Io == o.Io && (Opt == null ? o.Opt == null : Opt.SameAs(o.Opt));
      }
    }

    private readonly Dictionary<string, Mode> _modes = new Dictionary<string, Mode>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _names = new Dictionary<string, int>(StringComparer.Ordinal);
    public X13.Plc.PlcCapabilities Capabilities { get; private set; }

    private EP_Device() {
    }

    internal sealed class TwiBus {
      public int Number;
      public Mode Sda, Scl;
    }

    /// <summary>Public bus numbers are zero-based; firmware modes retain their descriptor values.</summary>
    public List<TwiBus> TwiBuses() {
      var buses = new List<TwiBus>();
      int offset = _modes.ContainsKey("TWI0_SDA") || _modes.ContainsKey("TWI0_SCL") ? 0 : -1;
      foreach(var mode in _modes.Values) {
        var match = System.Text.RegularExpressions.Regex.Match(mode.Name, @"^TWI(0|[1-9]\d*)_SDA$");
        int index;
        Mode scl;
        if(!match.Success || !int.TryParse(match.Groups[1].Value, out index)
          || index + offset < 0 || mode.Io != PinIo.None
          || !_modes.TryGetValue("TWI" + match.Groups[1].Value + "_SCL", out scl) || scl.Io != PinIo.None) continue;
        buses.Add(new TwiBus() { Number = index + offset, Sda = mode, Scl = scl });
      }
      return buses.OrderBy(b => b.Number).ToList();
    }

    public bool TryPin(string text, out int pin) {
      pin = 0;
      return text != null && (int.TryParse(text, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out pin) || _names.TryGetValue(text, out pin));
    }

    /// <summary>Значение константы программы: номер режима или пина по имени.</summary>
    public bool TryConst(string name, out int value) {
      Mode m;
      if(_modes.TryGetValue(name, out m)) {
        value = m.Value;
        return true;
      }
      return _names.TryGetValue(name, out value);
    }
    /// <summary>Режим по номеру, иначе null.</summary>
    public Mode ModeOf(int v) {
      return _modes.Values.FirstOrDefault(z => z.Value == v);
    }
    public bool Allows(int pin, Mode mode) {
      X13.Plc.PlcCapabilities.Mode available;
      return mode.Pins.Contains(pin) && Capabilities.Modes.TryGetValue(mode.Name, out available) && available.Pins.Contains(pin);
    }
    /// <remarks>Без opt параметра у режима нет: допустим только 0, то же, что пропуск.</remarks>
    public bool CheckOption(Mode mode, int option) {
      return mode.Opt == null ? option == 0 : mode.Opt.Accepts(option);
    }
    public int DefaultOption(Mode mode) {
      return mode.Opt == null ? 0 : mode.Opt.Default;
    }

    /// <summary>Описание из поля PLC; ошибка формата - FormatException с путём внутри описания.</summary>
    public static EP_Device Parse(JSValue plc, JSValue availablePins = null, JSValue availableUarts = null) {
      if(!plc.IsObject()) {
        throw Error("PLC", "object expected");
      }
      var d = new EP_Device();
      var names = plc.Field("names");
      if(names.Defined) {
        if(!names.IsObject()) {
          throw Error("PLC.names", "object expected");
        }
        foreach(var kv in names) {
          d._names[kv.Key] = Int(kv.Value, "PLC.names." + kv.Key, 0, ushort.MaxValue);
        }
      }
      var groups = plc.Field("groups");
      if(!groups.IsObject()) {
        throw Error("PLC.groups", "object expected");
      }
      foreach(var g in groups) {
        string gp = "PLC.groups." + g.Key;
        if(!g.Value.IsObject()) {
          throw Error(gp, "object expected");
        }
        var pins = new HashSet<int>();
        var jp = g.Value.Field("pins");
        if(jp.IsObject()) {
          foreach(var p in jp) {
            pins.Add(d.PinOf(p.Value, gp + ".pins[" + p.Key + "]"));
          }
        }
        if(pins.Count == 0) {
          throw Error(gp + ".pins", "required, at least one pin");
        }
        var modes = g.Value.Field("modes");
        if(!modes.IsObject() || !modes.Any()) {
          throw Error(gp + ".modes", "required, at least one mode");
        }
        foreach(var jm in modes) {
          string mp = gp + ".modes." + jm.Key;
          var mode = ParseMode(jm.Key, jm.Value, mp);
          Mode known;
          if(d._modes.TryGetValue(jm.Key, out known)) {
            if(!known.SameAs(mode)) {
              throw Error(mp, "v, io and opt differ from the same mode in another group");
            }
            mode = known;
          } else {
            var dup = d._modes.Values.FirstOrDefault(z => z.Value == mode.Value);
            if(dup != null) {
              throw Error(mp + ".v", mode.Value + " is already used by " + dup.Name);
            }
            if(d._names.ContainsKey(jm.Key)) {
              throw Error(mp, "the name is also a pin name");
            }
            d._modes.Add(jm.Key, mode);
          }
          mode.Pins.UnionWith(pins);
        }
      }
      d.Capabilities = X13.Plc.PlcCapabilities.Read(plc, availablePins, availableUarts);
      return d;
    }

    private static Mode ParseMode(string name, JSValue j, string path) {
      if(!j.IsObject()) {
        throw Error(path, "object expected");
      }
      var m = new Mode() { Name = name, Value = Int(j.Field("v"), path + ".v", 0, int.MaxValue) };
      var io = j.Field("io");
      if(!io.Defined) {
        m.Io = PinIo.None;
      } else {
        switch(io.AsString(null)) {
        case "in":
          m.Io = PinIo.In;
          break;
        case "out":
          m.Io = PinIo.Out;
          break;
        case "io":
          m.Io = PinIo.InOut;
          break;
        default:
          throw Error(path + ".io", "in, out or io expected");
        }
      }
      var opt = j.Field("opt");
      if(opt.Defined) {
        m.Opt = ParseOption(opt, path + ".opt");
      }
      return m;
    }

    private static Option ParseOption(JSValue j, string path) {
      if(!j.IsObject()) {
        throw Error(path, "object expected");
      }
      var o = new Option();
      var values = j.Field("values");
      if(values.Defined) {
        if(j.Field("min").Defined || j.Field("max").Defined) {
          throw Error(path, "min/max and values are exclusive");
        }
        if(!values.IsObject()) {
          throw Error(path + ".values", "object expected");
        }
        o.Values = new SortedSet<int>();
        foreach(var kv in values) {
          o.Values.Add(Int(kv.Value, path + ".values." + kv.Key, int.MinValue, int.MaxValue));
        }
        if(o.Values.Count == 0) {
          throw Error(path + ".values", "at least one value expected");
        }
      } else {
        if(!j.Field("min").Defined || !j.Field("max").Defined) {
          throw Error(path, "min and max, or values, expected");
        }
        o.Min = Int(j.Field("min"), path + ".min", int.MinValue, int.MaxValue);
        o.Max = Int(j.Field("max"), path + ".max", o.Min, int.MaxValue);
      }
      if(!j.Field("default").Defined) {
        throw Error(path + ".default", "required");
      }
      o.Default = Int(j.Field("default"), path + ".default", int.MinValue, int.MaxValue);
      if(!o.Accepts(o.Default)) {
        throw Error(path + ".default", o.Default + " is not in " + o.ToString());
      }
      return o;
    }

    /// <summary>Пин группы: номер или имя из names.</summary>
    private int PinOf(JSValue p, string path) {
      string name = p.AsString(null);
      if(name == null) {
        return Int(p, path, 0, ushort.MaxValue);
      }
      int pin;
      if(!_names.TryGetValue(name, out pin)) {
        throw Error(path, "unknown pin name " + name);
      }
      return pin;
    }

    private static int Int(JSValue v, string path, int min, int max) {
      if(v == null || !v.IsNumber) {
        throw Error(path, "integer expected");
      }
      double d = (double)v;
      if(d != Math.Floor(d) || d < min || d > max) {
        throw Error(path, "integer " + min + ".." + max + " expected, got " + d);
      }
      return (int)d;
    }

    private static FormatException Error(string path, string message) {
      return new FormatException(path + ": " + message);
    }
  }
}
