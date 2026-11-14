///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  /// <summary>What the LPLC editor reads from the tree: the program (pa1), its blocks, the device's
  /// variables, their pins and the links between them.</summary>
  /// <remarks>Mirrors the contract MQTT_SN compiles (plugins/MQTT_SN/LPLC: LP_Tree, LP_Schema) by field
  /// names alone - WebUI does not reference that assembly, and reads no Func source: the outputs a src
  /// writes reach it as the block's own mi catalog, which MQTT_SN keeps.
  /// <list type="bullet">
  /// <item>The program is the topic typed Ext/LogramPLC - recognised by type, never by its name.</item>
  /// <item>A block is a program child typed LPLC/*; its position is LPLC.pos, its shown inputs the keys
  /// of LPLC.in, its shown outputs the keys of LPLC.out (all catalog outputs when that is absent).</item>
  /// <item>A variable is a child of the device whose MQTT-SN.tag starts with the tag of one of the
  /// device's "Children" templates whose menu is LPLC. Its one input is LPLC.in.x.</item>
  /// <item>A link is the input's value: "Block.out" (split at the first dot, as LP_DiagramBinder
  /// does), a variable's name, a constant, or "" for a shown but unconnected input.</item>
  /// <item>A Pin (LPLC/Pin) is a block with LPLC.args.mode and LPLC.args.pin from the PLC description of
  /// the device type; its one input is LPLC.in.x (output modes only), its one output value.</item>
  /// </list></remarks>
  internal static class LplcModel {
    internal const string ViewName = "lplc";
    internal const string ProgramType = "Ext/LogramPLC";
    internal const string FuncType = "LPLC/Func";
    internal const string PinType = "LPLC/Pin";
    internal const string TwiType = "LPLC/TwiMaster";
    private const string BlockTypePrefix = "LPLC/";
    private const string VarMenu = "LPLC";
    private const string TypesRoot = "/$YS/TYPES/";
    internal const string InPrefix = "in.";
    internal const string OutPrefix = "out.";
    internal const string VarInputField = "LPLC.in.x";
    internal const string PinInputField = "LPLC.in.x";
    internal const string PinOutput = "value";
    internal const string ObserverField = "LPLC.observer";
    internal const string TraceField = "LPLC.trace";
    // A Pin argument is pasted into the program as a constant name (LP_DiagramBinder.PinArg).
    private static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    internal enum TargetKind {
      Program,
      Block,
      Variable,
      InputPin,
      OutputPin,
      ConstructorArgument,
    }

    /// <summary>What an lplc# vid names. Topic is the block for a pin, which has no topic of its own.</summary>
    internal sealed class Target {
      public TargetKind Kind;
      public Topic Topic;
      public Topic Program;
      public string Pin;
    }

    internal static bool IsProgram(Topic t) {
      return t != null && !t.disposed && string.Equals(TypeOf(t), ProgramType, StringComparison.Ordinal);
    }

    internal static bool IsBlock(Topic t) {
      return t != null && !t.disposed && IsProgram(t.parent) && IsBlockType(TypeOf(t));
    }

    /// <summary>An Add entry that creates an LPLC block - the Core fallback of MenuBuilder.ResolveAddActions
    /// must never offer a plain topic as one.</summary>
    internal static bool IsBlockAction(JSC.JSValue action) {
      return IsBlockType(action.AsString("manifest.type", null));
    }

    private static bool IsBlockType(string type) {
      return type != null && type.StartsWith(BlockTypePrefix, StringComparison.Ordinal);
    }

    internal static bool IsPin(Topic t) {
      return t != null && !t.disposed && string.Equals(TypeOf(t), PinType, StringComparison.Ordinal);
    }

    internal static bool IsTwi(Topic t) {
      return t != null && !t.disposed && string.Equals(TypeOf(t), TwiType, StringComparison.Ordinal);
    }

    private static string TypeOf(Topic t) {
      return t.GetField("type").AsString(null);
    }

    internal static string Vid(Topic t) {
      return ViewName + "#" + (t == null ? "/" : t.path);
    }

    internal static string PinVid(Topic block, bool input, string pin) {
      return Vid(block) + "/" + (input ? InPrefix : OutPrefix) + pin;
    }

    internal static Topic ProgramOfDevice(Topic device) {
      if(device == null) return null;
      foreach(Topic c in device.children) {
        if(IsProgram(c)) return c;
      }
      return null;
    }

    /// <summary>The device's LPLC variable templates by key: its "Children" entries whose menu is LPLC.</summary>
    internal static List<KeyValuePair<string, JSC.JSValue>> VarTemplates(Topic device) {
      List<KeyValuePair<string, JSC.JSValue>> r = new List<KeyValuePair<string, JSC.JSValue>>();
      JSC.JSValue ch = device == null ? null : device.GetField("Children");
      if(!ch.IsObject()) return r;
      foreach(var kv in ch) {
        if(!kv.Value.IsObject() || !string.Equals(kv.Value.AsString("menu", null), VarMenu, StringComparison.Ordinal)) continue;
        if(string.IsNullOrEmpty(kv.Value.AsString("manifest.MQTT-SN.tag", null))) continue;
        r.Add(new KeyValuePair<string, JSC.JSValue>(kv.Key, kv.Value));
      }
      return r;
    }

    private static List<string> VarTagPrefixes(Topic device) {
      List<string> r = new List<string>();
      foreach(var kv in VarTemplates(device)) {
        string tag = kv.Value.AsString("manifest.MQTT-SN.tag", null);
        if(!r.Contains(tag)) r.Add(tag);
      }
      return r;
    }

    private static bool IsVariable(Topic t, List<string> prefixes) {
      if(t == null || t.disposed || IsProgram(t)) return false;
      string tag = t.GetField("MQTT-SN.tag").AsString(null);
      if(string.IsNullOrEmpty(tag)) return false;
      foreach(string p in prefixes) {
        if(tag.StartsWith(p, StringComparison.Ordinal)) return true;
      }
      return false;
    }

    internal static List<Topic> Blocks(Topic program) {
      List<Topic> r = new List<Topic>();
      if(program == null) return r;
      foreach(Topic c in program.children) {
        if(!c.disposed && IsBlockType(TypeOf(c))) r.Add(c);
      }
      return r;
    }

    internal static List<Topic> Variables(Topic program) {
      List<Topic> r = new List<Topic>();
      Topic device = program == null ? null : program.parent;
      if(device == null) return r;
      List<string> prefixes = VarTagPrefixes(device);
      if(prefixes.Count == 0) return r;
      foreach(Topic c in device.children) {
        if(IsVariable(c, prefixes)) r.Add(c);
      }
      return r;
    }

    private static Topic ProgramOfVariable(Topic t) {
      Topic device = t == null ? null : t.parent;
      Topic program = ProgramOfDevice(device);
      return program != null && IsVariable(t, VarTagPrefixes(device)) ? program : null;
    }

    // Keep observers in Variables for binding/compiler consumers, but not as canvas elements.
    internal static bool IsObserver(Topic variable) {
      return variable != null && variable.GetField(ObserverField).AsBool(false);
    }

    internal static string ObserverName(Topic block, string pin) {
      return block.name + "_" + pin;
    }

    internal static Topic Observer(Topic block, string pin) {
      Topic device = block?.parent?.parent;
      if(device == null) return null;
      Topic variable = device.Get(ObserverName(block, pin), false);
      if(!IsVariable(variable, VarTagPrefixes(device))) return null;
      JSC.JSValue input = variable.GetField(VarInputField);
      return input.IsNullOrUndefined() || input.AsString(null) == string.Empty ? variable : null;
    }

    internal static bool IsTraced(Topic block, string pin) {
      var observer = Observer(block, pin);
      return IsObserver(observer) && observer.GetField(TraceField).AsBool(false);
    }

    internal static bool CanTrace(Topic block, string pin) {
      if(!IsBlock(block) || string.IsNullOrEmpty(pin) || pin.IndexOf('.') >= 0 || !Topic.IsValidName(pin)) return false;
      if(!OutputCatalog(block).Contains(pin)) return false;
      Topic device = block.parent.parent;
      if(device == null) return false;
      string name = ObserverName(block, pin);
      // Underscores can make two Block_output pairs name the same device topic.
      int matches = device.children.Where(IsProgram).SelectMany(Blocks)
        .Sum(b => OutputCatalog(b).Count(p => ObserverName(b, p) == name));
      if(matches != 1) return false;
      Topic existing = device.Get(name, false);
      return existing == null || existing == Observer(block, pin);
    }

    /// <summary>Resolves an lplc# vid; null - not part of any LPLC program.</summary>
    /// <remarks>A pin vid (lplc#&lt;block&gt;/in.A, lplc#&lt;block&gt;/out.Q) is recognised first: the
    /// last segment is a valid topic name, so a plain lookup would simply find nothing.</remarks>
    internal static Target Resolve(string vid) {
      if(!string.Equals(VidHelper.GetView(vid), ViewName, StringComparison.Ordinal)) return null;
      string path = VidHelper.GetTopicPath(vid);
      if(string.IsNullOrEmpty(path) || path == "/") return null;
      int slash = path.LastIndexOf('/');
      string last = path.Substring(slash + 1);
      if(slash > 0 && last.StartsWith("args.", StringComparison.Ordinal)) {
        var block = Topic.root.Get(path.Substring(0, slash), false);
        string argument = last.Substring(5);
        if(IsBlock(block) && X13.Plc.ConstructorSchema.IsName(argument) && Configuration(block).Schema.Find(argument) != null)
          return new Target { Kind = TargetKind.ConstructorArgument, Topic = block, Program = block.parent, Pin = argument };
        return null;
      }
      bool isIn = last.StartsWith(InPrefix, StringComparison.Ordinal);
      if(slash > 0 && (isIn || last.StartsWith(OutPrefix, StringComparison.Ordinal))) {
        Topic block = Topic.root.Get(path.Substring(0, slash), false);
        string pin = last.Substring(isIn ? InPrefix.Length : OutPrefix.Length);
        if(IsBlock(block) && pin.Length > 0 && (!Configuration(block).Schema.HasMetadata || InputCatalog(block).Count > 0
          || OutputCatalog(block).Count > 0 || block.GetField("LPLC.in").IsObject() && block.GetField("LPLC.in").Any()
          || block.GetField("LPLC.out").IsObject() && block.GetField("LPLC.out").Any())) {
          return new Target() { Kind = isIn ? TargetKind.InputPin : TargetKind.OutputPin, Topic = block, Program = block.parent, Pin = pin };
        }
      }
      Topic t = Topic.root.Get(path, false);
      if(t == null) return null;
      if(IsProgram(t)) return new Target() { Kind = TargetKind.Program, Topic = t, Program = t };
      if(IsBlock(t)) return new Target() { Kind = TargetKind.Block, Topic = t, Program = t.parent };
      Topic program = ProgramOfVariable(t);
      return program == null ? null : new Target() { Kind = TargetKind.Variable, Topic = t, Program = program };
    }

    internal static List<string> ConstructorLabels(Topic block) {
      var labels = new List<string>();
      Topic type = Topic.root.Get(TypesRoot + TypeOf(block), false);
      var parameters = type == null ? JSC.JSValue.Undefined : type.GetState().Field("LPLC").Field("args");
      if(!parameters.IsObject()) return labels;
      foreach(var parameter in parameters) {
        string name = parameter.Value.AsString(null);
        if(string.IsNullOrEmpty(name)) continue;
        labels.Add(name + ": " + ConstructorValue(block, name));
      }
      return labels;
    }

    internal static string ConstructorValue(Topic block, string name) {
      var value = block.GetField("LPLC.args").Field(name);
      return !value.Defined || value.IsNull ? "<unset>" : value.AsString(null) ?? JsLib.Stringify(value);
    }

    /// <summary>Possible inputs of a block - the mi catalog under LPLC.in, by key.</summary>
    internal static List<string> InputCatalog(Topic block) {
      return Catalog(block, "LPLC.in");
    }

    /// <summary>Possible outputs of a block - the type's catalog plus what MQTT_SN found in a Func's src.</summary>
    internal static List<string> OutputCatalog(Topic block) {
      return Catalog(block, "LPLC.out");
    }

    private static List<string> Catalog(Topic block, string path) {
      SchemaOverlay at = SchemaOverlay.ForManifest(block).At(path);
      return at == null ? new List<string>() : at.CatalogEntries.Select(kv => kv.Key).ToList();
    }

    /// <summary>Shown inputs: catalog order first, then whatever LPLC.in holds beyond the catalog.</summary>
    internal static List<string> ShownInputs(Topic block, List<string> catalog) {
      List<string> r = new List<string>();
      JSC.JSValue inputs = block.GetField("LPLC.in");
      if(!inputs.IsObject()) return r;
      foreach(string k in catalog) {
        if(inputs.Field(k).Defined) r.Add(k);
      }
      foreach(var kv in inputs) {
        if(!r.Contains(kv.Key)) r.Add(kv.Key);
      }
      return r;
    }

    /// <summary>Shown outputs: LPLC.out in its own key order (the catalog when absent), then every
    /// output a link references - an output in use is never hidden.</summary>
    internal static List<string> ShownOutputs(Topic block, List<string> catalog, IEnumerable<string> referenced) {
      List<string> r = new List<string>();
      JSC.JSValue outs = block.GetField("LPLC.out");
      if(outs.IsObject()) {
        foreach(var kv in outs) {
          if(!r.Contains(kv.Key)) r.Add(kv.Key);
        }
      } else {
        r.AddRange(catalog);
      }
      if(referenced != null) {
        foreach(string p in referenced) {
          if(!r.Contains(p)) r.Add(p);
        }
      }
      return r;
    }

    /// <summary>Outputs referenced by some link, by block name, each list in ordinal order.</summary>
    internal static Dictionary<string, List<string>> ReferencedOutputs(List<Topic> blocks, List<Topic> variables) {
      Dictionary<string, List<string>> r = new Dictionary<string, List<string>>(StringComparer.Ordinal);
      foreach(Topic b in blocks) {
        JSC.JSValue inputs = b.GetField("LPLC.in");
        if(!inputs.IsObject()) continue;
        foreach(var kv in inputs) AddReference(r, kv.Value);
      }
      foreach(Topic v in variables) AddReference(r, v.GetField(VarInputField));
      foreach(List<string> l in r.Values) l.Sort(StringComparer.Ordinal);
      return r;
    }

    private static void AddReference(Dictionary<string, List<string>> r, JSC.JSValue link) {
      string name, pin;
      if(!TrySplitLink(link, out name, out pin) || pin == null) return;
      List<string> l;
      if(!r.TryGetValue(name, out l)) {
        l = new List<string>();
        r[name] = l;
      }
      if(!l.Contains(pin)) l.Add(pin);
    }

    /// <summary>"Block.out" - name and pin; "Var" - name, pin null; anything else (constant, "") - false.</summary>
    internal static bool TrySplitLink(JSC.JSValue link, out string name, out string pin) {
      name = null;
      pin = null;
      string s = link.AsString(null);
      if(string.IsNullOrEmpty(s)) return false;
      int dot = s.IndexOf('.');
      name = dot < 0 ? s : s.Substring(0, dot);
      pin = dot < 0 ? null : s.Substring(dot + 1);
      return true;
    }

    /// <summary>A link that is a constant (number or true/false) rather than a connection.</summary>
    internal static bool IsConstant(JSC.JSValue link) {
      return link != null && (link.IsNumber || link.ValueType == JSC.JSValueType.Boolean);
    }

    /// <summary>An input fed by anything - a source or a constant; "", null and absent are not.</summary>
    internal static bool IsConnected(JSC.JSValue link) {
      return IsConstant(link) || !string.IsNullOrEmpty(link.AsString(null));
    }

    // === Pin modes - the PLC description of the device type ===

    /// <summary>A pin a Pin block can take.</summary>
    internal sealed class PinChoice {
      public int Number;
      /// <summary>The pin's name in PLC.names, otherwise its number - the menu text and the block name's tail.</summary>
      public string Name;
      /// <summary>Name is a PLC.names constant, not the number.</summary>
      public bool Named;
    }

    /// <summary>A mode a Pin block can be in, with every pin of every group that offers it.</summary>
    internal sealed class PinMode {
      public string Name;
      public int Value;
      /// <summary>"in", "out" or "io".</summary>
      public string Io;
      public readonly List<PinChoice> Pins = new List<PinChoice>();
      public PinChoice DefaultPin;
      /// <summary>Only an output mode has the input x (LP_DiagramBinder.AnalyzePin).</summary>
      public bool TakesInput { get { return Io == "out"; } }
    }

    /// <summary>The Pin modes of the program's device, in description order, each with its pins by number.</summary>
    /// <remarks>Read the way X13.DevicePLC.EP_Device reads the PLC field of the device type, by field names
    /// alone: a mode without io is for ConfigPin only and is left out, as is a mode or pin name that is not a
    /// program constant; a mode in several groups gets the pins of all of them. A malformed entry is skipped -
    /// the compiler reports it.</remarks>
    internal static List<PinMode> PinModes(Topic program) {
      return DeviceModes(program, false);
    }

    internal static List<X13.Plc.PlcCapabilities.Bus> SerialBuses(Topic program) {
      return Capabilities(program).Buses.Where(b => b.Kind == "serial" && b.Available).OrderBy(b => b.Number).ToList();
    }

    internal static bool SerialTaken(Topic program, int port) {
      return Blocks(program).Any(b => {
        var c = Configuration(b);
        return c.Schema.Provides.Any(p => p.Kind == "serial" && c.Values.Field(p.NumberArg).AsInt(-1) == port);
      });
    }

    internal static Dictionary<string, JSC.JSValue> SerialDefaults(Topic program, int port) {
      var fields = new Dictionary<string, JSC.JSValue> { { "LPLC.args.Port", port } };
      var modes = DeviceModes(program, true);
      var tx = modes.Find(m => m.Name == "UART" + port + "_TX")?.DefaultPin;
      var rx = modes.Find(m => m.Name == "UART" + port + "_RX")?.DefaultPin;
      if(tx != null && rx != null && tx.Number == rx.Number) tx = rx = null;
      if(tx != null) fields["LPLC.args.TxPin"] = tx.Named ? (JSC.JSValue)tx.Name : tx.Number;
      if(rx != null) fields["LPLC.args.RxPin"] = rx.Named ? (JSC.JSValue)rx.Name : rx.Number;
      return fields;
    }

    private static List<PinMode> DeviceModes(Topic program, bool configOnly) {
      List<PinMode> r = new List<PinMode>();
      JSC.JSValue plc = DevicePlc(program);
      if(!plc.IsObject()) return r;
      var capabilities = Capabilities(program);
      Dictionary<string, int> numbers = new Dictionary<string, int>(StringComparer.Ordinal);
      Dictionary<int, string> names = new Dictionary<int, string>();
      JSC.JSValue jn = plc.Field("names");
      if(jn.IsObject()) {
        foreach(var kv in jn) {
          int n;
          if(!TryInt(kv.Value, out n)) continue;
          numbers[kv.Key] = n;
          if(!names.ContainsKey(n) && Identifier.IsMatch(kv.Key)) names[n] = kv.Key;
        }
      }
      JSC.JSValue groups = plc.Field("groups");
      if(!groups.IsObject()) return r;
      Dictionary<string, PinMode> byName = new Dictionary<string, PinMode>(StringComparer.Ordinal);
      Dictionary<PinMode, SortedSet<int>> pins = new Dictionary<PinMode, SortedSet<int>>();
      Dictionary<PinMode, HashSet<int>> defaults = new Dictionary<PinMode, HashSet<int>>();
      foreach(var g in groups) {
        if(!g.Value.IsObject()) continue;
        List<int> groupPins = new List<int>();
        JSC.JSValue jp = g.Value.Field("pins");
        if(jp.IsObject()) {
          foreach(var p in jp) {
            string pinName = p.Value.AsString(null);
            int n;
            if(pinName != null ? numbers.TryGetValue(pinName, out n) : TryInt(p.Value, out n)) groupPins.Add(n);
          }
        }
        JSC.JSValue jm = g.Value.Field("modes");
        if(!jm.IsObject()) continue;
        foreach(var m in jm) {
          if(!m.Value.IsObject() || !Identifier.IsMatch(m.Key)) continue;
          string io = m.Value.AsString("io", null);
          if(configOnly ? m.Value.Field("io").Defined : io != "in" && io != "out" && io != "io") continue;
          PinMode mode;
          if(!byName.TryGetValue(m.Key, out mode)) {
            int v;
            if(!TryInt(m.Value.Field("v"), out v)) continue;
            mode = new PinMode() { Name = m.Key, Io = io, Value = v };
            byName.Add(m.Key, mode);
            pins.Add(mode, new SortedSet<int>());
            defaults.Add(mode, new HashSet<int>());
            r.Add(mode);
          }
          pins[mode].UnionWith(groupPins);
          var defaultPin = m.Value.Field("defaultPin");
          if(configOnly && defaultPin.Defined) {
            int n;
            string name = defaultPin.AsString(null);
            defaults[mode].Add((name != null ? numbers.TryGetValue(name, out n) : TryInt(defaultPin, out n)) ? n : -1);
          }
        }
      }
      foreach(PinMode mode in r) {
        foreach(int n in pins[mode]) {
          X13.Plc.PlcCapabilities.Mode available;
          if(!capabilities.Modes.TryGetValue(mode.Name, out available) || !available.Pins.Contains(n)) continue;
          string name;
          bool named = names.TryGetValue(n, out name);
          mode.Pins.Add(new PinChoice() { Number = n, Name = named ? name : n.ToString(CultureInfo.InvariantCulture), Named = named });
        }
        if(defaults[mode].Count == 1) mode.DefaultPin = mode.Pins.Find(p => defaults[mode].Contains(p.Number));
      }
      r.RemoveAll(m => m.Pins.Count == 0);
      return r;
    }

    internal sealed class TwiBus {
      public int Number;
      public PinMode Sda, Scl;
      public string Name { get { return "TWI" + Number.ToString(CultureInfo.InvariantCulture); } }
    }

    internal static List<TwiBus> TwiBuses(Topic program) {
      var modes = DeviceModes(program, true);
      var buses = new List<TwiBus>();
      foreach(var bus in Capabilities(program).Buses.Where(b => b.Kind == "twi")) {
        var sda = modes.Find(m => m.Name == bus.Roles["SDA"].Name);
        var scl = modes.Find(m => m.Name == bus.Roles["SCL"].Name);
        if(sda != null && scl != null) buses.Add(new TwiBus() { Number = bus.Number, Sda = sda, Scl = scl });
      }
      return buses.OrderBy(b => b.Number).ToList();
    }

    internal static bool TwiTaken(Topic program, TwiBus bus) {
      return program.Get(bus.Name, false) != null || Blocks(program).Select(Configuration).Any(c => c.Schema.Provides.Any(p =>
        p.Kind == "twi" && c.Values.Field(p.NumberArg).IsNumber && c.Values.Field(p.NumberArg).AsInt(-1) == bus.Number));
    }

    /// <summary>The name a new Pin gets: mode_pin.</summary>
    internal static string PinName(PinMode mode, PinChoice pin) {
      return mode.Name + "_" + pin.Name;
    }

    /// <summary>The mode of a Pin block by its LPLC.args.mode - a name, or a number as the compiler also
    /// takes; null - not in the device description.</summary>
    internal static PinMode PinModeOf(Topic pin) {
      JSC.JSValue a = pin.GetField("LPLC.args.mode");
      string s = a.AsString(null);
      int v;
      if(s == null) {
        if(!TryInt(a, out v)) return null;
      } else if(!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) {
        return PinModes(pin.parent).Find(m => m.Name == s);
      }
      return PinModes(pin.parent).Find(m => m.Value == v);
    }

    /// <summary>A Pin whose mode has the input x.</summary>
    internal static bool PinTakesInput(Topic pin) {
      PinMode mode = PinModeOf(pin);
      return mode != null && mode.TakesInput;
    }

    // The PLC field of the device type's state, as LP_Program.LoadDevice reads it.
    internal static X13.Plc.PlcCapabilities Capabilities(Topic program) {
      var device = program == null ? null : program.parent;
      return X13.Plc.PlcCapabilities.Read(DevicePlc(program), device == null ? null : device.GetField(X13.Plc.PlcCapabilities.PinsField),
        device == null ? null : device.GetField(X13.Plc.PlcCapabilities.UartsField));
    }

    internal static JSC.JSValue BlockType(Topic block) {
      var type = Topic.root.Get(TypesRoot + TypeOf(block), false);
      return type == null ? JSC.JSValue.Undefined : type.GetState();
    }

    internal static X13.Plc.ConstructorInstance Configuration(Topic block) {
      return new X13.Plc.ConstructorInstance { Name = block.name, Schema = X13.Plc.ConstructorSchema.Read(BlockType(block)), Values = block.GetField("LPLC.args") };
    }

    internal static X13.Plc.ConstructorResolver Configurations(Topic program) {
      return new X13.Plc.ConstructorResolver(Capabilities(program), Blocks(program).Select(Configuration));
    }

    private static JSC.JSValue DevicePlc(Topic program) {
      Topic device = program == null ? null : program.parent;
      string type = device == null ? null : device.GetField("type").AsString(null);
      Topic tt = string.IsNullOrEmpty(type) ? null : Topic.root.Get(TypesRoot + type, false);
      return tt == null ? JSC.JSValue.NotExists : tt.GetState().Field("PLC");
    }

    private static bool TryInt(JSC.JSValue v, out int r) {
      r = 0;
      if(v == null || !v.IsNumber) return false;
      double d = (double)v;
      if(d != Math.Floor(d) || d < 0 || d > int.MaxValue) return false;
      r = (int)d;
      return true;
    }

    /// <summary>Disconnects every link whose source matches.</summary>
    /// <remarks>A block input stays shown, as "" - unconnected; a variable's x is removed, since having
    /// one is what makes the variable a receiver. Done before a source is deleted or hidden, so no link is
    /// left pointing at nothing. skip - a sink not to touch: the block being deleted itself.</remarks>
    internal static void ClearLinks(Topic program, Func<string, string, bool> matches, Topic prim, Topic skip = null) {
      foreach(Topic b in Blocks(program)) {
        if(b == skip) continue;
        JSC.JSValue inputs = b.GetField("LPLC.in");
        if(!inputs.IsObject()) continue;
        List<string> hit = new List<string>();
        foreach(var kv in inputs) {
          string name, pin;
          if(TrySplitLink(kv.Value, out name, out pin) && matches(name, pin)) hit.Add(kv.Key);
        }
        foreach(string key in hit) b.SetField("LPLC.in." + key, string.Empty, prim);
      }
      foreach(Topic v in Variables(program)) {
        if(v == skip) continue;
        string name, pin;
        if(TrySplitLink(v.GetField(VarInputField), out name, out pin) && matches(name, pin)) {
          v.SetField(VarInputField, JSC.JSValue.Null, prim);
        }
      }
    }
  }
}
