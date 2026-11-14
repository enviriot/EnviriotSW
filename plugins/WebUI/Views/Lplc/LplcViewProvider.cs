///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  /// <summary>vid-routed entry point for LPLC programs (view "lplc") - one LplcGraphController per open
  /// program, keyed by the program's Topic, for the reason LogramViewProvider gives.</summary>
  /// <remarks>Every target goes through LplcModel.Resolve, which is also the guard: a vid that is not a
  /// program, one of its blocks, one of their pins or one of the device's variables is refused, so no
  /// write here reaches a topic outside the program the user has open.
  /// <para>Only manifest fields are written - LPLC.pos, LPLC.in, LPLC.out - and the blocks and variables
  /// themselves; MQTT_SN compiles from those. Removing a source (a block, a variable, a shown output)
  /// first disconnects every input it feeds, so no link is left naming nothing.</para></remarks>
  internal sealed class LplcViewProvider : ViewProviderBase {
    private static readonly Regex OutputName = new Regex(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant);
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    private readonly Func<Topic> _prim;
    private readonly Dictionary<Topic, LplcGraphController> _open = new Dictionary<Topic, LplcGraphController>();

    internal LplcViewProvider(Action<JSC.JSObject> send, Action<string, Action> post = null, Func<Topic> prim = null) {
      _send = send;
      _post = post;
      _prim = prim;
    }

    private Topic Prim { get { return _prim == null ? null : _prim(); } }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == LplcModel.ViewName;
    }

    public override ViewOpResult Open(string vid, string view) {
      if(!string.Equals(view, LplcModel.ViewName, StringComparison.Ordinal)) {
        return ViewOpResult.Error("view_open_not_supported", "Open is not supported for view: " + (view ?? "<null>"));
      }
      LplcModel.Target target = LplcModel.Resolve(vid);
      if(target == null || target.Kind != LplcModel.TargetKind.Program) {
        return ViewOpResult.Error("lplc_program_required", "Topic is not an LPLC program: " + (VidHelper.GetTopicPath(vid) ?? "<null>"));
      }
      Topic program = target.Program;

      LplcGraphController existing;
      _open.TryGetValue(program, out existing);
      _open.Remove(program);
      if(existing != null) existing.Dispose();

      LplcGraphController controller = new LplcGraphController(_send, program, ForgetRoot, _post);
      _open[program] = controller;
      controller.Open();
      return ViewOpResult.Open(LplcModel.ViewName, controller.RootVid, program.name);
    }

    /// <summary>Drag-to-move writes LPLC.pos; a shown, unlinked block input accepts a numeric constant.</summary>
    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
      LplcModel.Target target = LplcModel.Resolve(vid);
      if(target != null && target.Kind == LplcModel.TargetKind.InputPin) {
        return CommitConstant(target, value, Prim);
      }
      if(target == null || (target.Kind != LplcModel.TargetKind.Block && target.Kind != LplcModel.TargetKind.Variable)) {
        return ViewOpResult.Error("view_target_not_found", "Not a block or variable of an LPLC program: " + (VidHelper.GetTopicPath(vid) ?? "<null>"));
      }
      if(!value.IsObject()) {
        return ViewOpResult.Error("commit_value_invalid", "Expected an object with top/left");
      }
      Topic prim = Prim;
      // Same floors as the client: a block keeps the first column free for its input pins; a Pin is
      // drawn as a variable and may sit flush against the edge.
      int minLeft = target.Kind == LplcModel.TargetKind.Block && !LplcModel.IsPin(target.Topic) ? LogramGraphController.MinLeftCell : 0;
      if(value.Field("top").IsNumber) target.Topic.SetField("LPLC.pos.top", new JSL.Number(Math.Max(0, value.AsInt("top", 0))), prim);
      if(value.Field("left").IsNumber) target.Topic.SetField("LPLC.pos.left", new JSL.Number(Math.Max(minLeft, value.AsInt("left", 0))), prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult CommitConstant(LplcModel.Target target, JSC.JSValue value, Topic prim) {
      if(LplcModel.IsPin(target.Topic) || !IsPinName(target.Pin)) {
        return ViewOpResult.Error("commit_target_invalid", "Only a shown block input accepts a constant");
      }
      string field = "LPLC.in." + target.Pin;
      JSC.JSValue current = target.Topic.GetField(field);
      if(!current.Defined) {
        return ViewOpResult.Error("commit_target_invalid", "The input is not shown: " + target.Pin);
      }
      if(!string.IsNullOrEmpty(current.AsString(null))) {
        return ViewOpResult.Error("commit_input_linked", "Disconnect the input before assigning a constant");
      }
      if(value == null || !value.IsNumber || double.IsNaN((double)value) || double.IsInfinity((double)value)) {
        return ViewOpResult.Error("commit_value_invalid", "Expected a finite integer or floating-point number");
      }
      target.Topic.SetField(field, value, prim);
      return ViewOpResult.Success();
    }

    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      LplcModel.Target target = LplcModel.Resolve(vid);
      if(target == null) {
        return ViewOpResult.Error("view_target_not_found", "Not part of an LPLC program: " + (VidHelper.GetTopicPath(vid) ?? "<null>"));
      }
      items = LplcPaletteBuilder.BuildMenu(target);
      return ViewOpResult.Success();
    }

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      LplcModel.Target target = LplcModel.Resolve(vid);
      if(target == null) {
        return ViewOpResult.Error("view_target_not_found", "Not part of an LPLC program: " + (VidHelper.GetTopicPath(vid) ?? "<null>"));
      }
      try {
        return Execute(target, cmd ?? string.Empty, args);
      }
      catch(Exception ex) {
        return ViewOpResult.Error("rpc_execution_failed", ex.Message);
      }
    }

    private ViewOpResult Execute(LplcModel.Target target, string cmd, JSC.JSValue args) {
      Topic prim = Prim;
      if(cmd == "set-arg" && (target.Kind == LplcModel.TargetKind.Block || target.Kind == LplcModel.TargetKind.ConstructorArgument))
        return SetArgument(target, args, prim);
      switch(target.Kind) {
      case LplcModel.TargetKind.Program:
        if(cmd.StartsWith("action:", StringComparison.Ordinal)) {
          return TopicRpcDispatcher.Execute(target.Topic, cmd, args, prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.AddBlockCmdPrefix, StringComparison.Ordinal)) {
          return AddBlock(target.Program, cmd.Substring(LplcPaletteBuilder.AddBlockCmdPrefix.Length), args, prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.AddPinCmdPrefix, StringComparison.Ordinal)) {
          return AddPin(target.Program, cmd.Substring(LplcPaletteBuilder.AddPinCmdPrefix.Length), args, prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.AddTwiCmdPrefix, StringComparison.Ordinal)) {
          return AddTwi(target.Program, cmd.Substring(LplcPaletteBuilder.AddTwiCmdPrefix.Length), args, prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.AddSerialCmdPrefix, StringComparison.Ordinal)) {
          return AddSerial(target.Program, cmd.Substring(LplcPaletteBuilder.AddSerialCmdPrefix.Length), args, prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.AddVarCmdPrefix, StringComparison.Ordinal)) {
          return AddVariable(target.Program, cmd.Substring(LplcPaletteBuilder.AddVarCmdPrefix.Length), args, prim);
        }
        break;
      case LplcModel.TargetKind.Block:
        if(cmd == "delete") {
          Topic block = target.Topic;
          return Delete(target.Program, block, (n, p) => p != null && n == block.name, prim);
        }
        if(LplcModel.IsPin(target.Topic)) {
          if(cmd == "trace") return Trace(target.Topic, LplcModel.PinOutput, prim);
          // Drawn as a variable: its one input x is bound and unbound as the variable's is.
          if(cmd == "bind") {
            if(!LplcModel.PinTakesInput(target.Topic)) {
              return ViewOpResult.Error("bind_target_invalid", "The pin's mode has no input: " + target.Topic.name);
            }
            return Bind(target, args, prim);
          }
          if(cmd == "unbind") {
            target.Topic.SetField(LplcModel.PinInputField, string.Empty, prim);
            return ViewOpResult.Success();
          }
          break;
        }
        if(cmd.StartsWith(LplcPaletteBuilder.ShowInCmdPrefix, StringComparison.Ordinal)) {
          return ShowInput(target.Topic, cmd.Substring(LplcPaletteBuilder.ShowInCmdPrefix.Length), prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.HideInCmdPrefix, StringComparison.Ordinal)) {
          return HideInput(target.Topic, cmd.Substring(LplcPaletteBuilder.HideInCmdPrefix.Length), prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.ShowOutCmdPrefix, StringComparison.Ordinal)) {
          return ShowOutput(target.Topic, cmd.Substring(LplcPaletteBuilder.ShowOutCmdPrefix.Length), prim);
        }
        if(cmd.StartsWith(LplcPaletteBuilder.HideOutCmdPrefix, StringComparison.Ordinal)) {
          return HideOutput(target.Program, target.Topic, cmd.Substring(LplcPaletteBuilder.HideOutCmdPrefix.Length), prim);
        }
        if(cmd == LplcPaletteBuilder.AddOutCmd) {
          return AddOutput(target.Topic, args.AsString("name", null), prim);
        }
        break;
      case LplcModel.TargetKind.Variable:
        if(cmd == "delete") {
          Topic variable = target.Topic;
          return Delete(target.Program, variable, (n, p) => p == null && n == variable.name, prim);
        }
        if(cmd == "bind") return Bind(target, args, prim);
        if(cmd == "unbind") {
          target.Topic.SetField(LplcModel.VarInputField, JSC.JSValue.Null, prim);
          return ViewOpResult.Success();
        }
        break;
      case LplcModel.TargetKind.InputPin:
        if(cmd == "bind") return Bind(target, args, prim);
        if(cmd == "unbind") {
          // Stays shown, unconnected - the key's existence is what shows it.
          target.Topic.SetField("LPLC.in." + target.Pin, string.Empty, prim);
          return ViewOpResult.Success();
        }
        if(cmd == "hide" || cmd == "delete") return HideInput(target.Topic, target.Pin, prim);
        break;
      case LplcModel.TargetKind.OutputPin:
        if(cmd == "trace") return Trace(target.Topic, target.Pin, prim);
        if(cmd == "hide" || cmd == "delete") return HideOutput(target.Program, target.Topic, target.Pin, prim);
        break;
      }
      return ViewOpResult.Error("rpc_command_unknown", "Unknown LPLC command: " + cmd);
    }

    private static ViewOpResult SetArgument(LplcModel.Target target, JSC.JSValue args, Topic prim) {
      string name = args.AsString("name", null);
      if(!X13.Plc.ConstructorSchema.IsName(name) || target.Kind == LplcModel.TargetKind.ConstructorArgument && target.Pin != name)
        return ViewOpResult.Error("lplc_argument_invalid", "Invalid constructor argument");
      var configuration = LplcModel.Configuration(target.Topic);
      var argument = configuration.Schema.Find(name);
      if(argument == null || argument.Readonly) return ViewOpResult.Error("lplc_argument_readonly", "Unknown or readonly constructor argument");
      bool unset = args.AsBool("unset", false);
      var value = args.Field("value");
      if(unset && argument.Required || !unset && !X13.Plc.ConstructorSchema.IsLiteral(value))
        return ViewOpResult.Error("lplc_argument_invalid", "A typed constructor value is required");
      var values = configuration.Values.IsObject() ? JsLib.Clone(configuration.Values) : JSC.JSObject.CreateObject();
      values[name] = unset ? JSC.JSValue.Null : value;
      configuration.Values = values;
      var configurations = new List<X13.Plc.ConstructorInstance>();
      foreach(var block in LplcModel.Blocks(target.Program)) configurations.Add(block == target.Topic ? configuration : LplcModel.Configuration(block));
      var resolver = new X13.Plc.ConstructorResolver(LplcModel.Capabilities(target.Program), configurations);
      var result = resolver.Resolve(target.Topic.name);
      string error;
      if(result.Errors.TryGetValue("", out error) || result.Errors.TryGetValue(name, out error))
        return ViewOpResult.Error("lplc_argument_invalid", error);
      target.Topic.SetField("LPLC.args." + name, unset ? JSC.JSValue.Null : value, prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult Trace(Topic block, string pin, Topic prim) {
      if(!IsPinName(pin)) return ViewOpResult.Error("pin_name_invalid", "Invalid output name: " + pin);
      Topic observer = LplcModel.Observer(block, pin);
      if(LplcModel.IsTraced(block, pin)) {
        observer.SetField(LplcModel.TraceField, false, prim);
        return ViewOpResult.Success();
      }
      if(!LplcModel.CanTrace(block, pin)) {
        return ViewOpResult.Error("trace_target_invalid", "Output is unavailable or its observer name is already in use: " + block.name + "." + pin);
      }
      if(observer == null) {
        Topic device = block.parent.parent;
        string name = LplcModel.ObserverName(block, pin);
        // Run publishes these templates with the actual merker address. Before Run use an unbound
        // prefix: MQTT_SN will not register it until the compiler assigns the observer's address.
        JSC.JSValue action = device.GetField("Children").Field(name);
        string tag = action.AsString("manifest.MQTT-SN.tag", null);
        bool known = action.AsString("menu", null) == "plc";
        bool merker = false;
        foreach(var template in LplcModel.VarTemplates(device)) {
          string prefix = template.Value.AsString("manifest.MQTT-SN.tag", null);
          if(tag != null && tag.StartsWith(prefix, StringComparison.Ordinal)) merker = true;
        }
        JSC.JSValue manifest = known && merker ? JsLib.Clone(action.Field("manifest")) : JSC.JSObject.CreateObject();
        if(!known || !merker) manifest = JsLib.SetField(manifest, "MQTT-SN.tag", new JSL.String("Md"));
        manifest = JsLib.SetField(manifest, LplcModel.ObserverField, true);
        manifest = JsLib.SetField(manifest, LplcModel.TraceField, true);
        observer = Topic.Declare(device, name, prim);
        // Unknown until the device publishes; do not pretend a freshly traced output is zero.
        Topic.Fill(observer, JSC.JSValue.Null, manifest, prim);
      } else {
        observer.SetField(LplcModel.ObserverField, true, prim);
        observer.SetField(LplcModel.TraceField, true, prim);
      }
      return ViewOpResult.Success();
    }

    // === Links ===

    /// <summary>Deletes a block or a variable after disconnecting every input it feeds.</summary>
    /// <remarks>The Required guard is TopicRpcDispatcher's, asked first: a refused delete must not have
    /// cut the links already.</remarks>
    private static ViewOpResult Delete(Topic program, Topic element, Func<string, string, bool> feeds, Topic prim) {
      if(element.CheckAttribute(Topic.Attribute.Required)) {
        return ViewOpResult.Error("delete_target_required", "Required topic cannot be deleted: " + element.path);
      }
      LplcModel.ClearLinks(program, feeds, prim, element);
      return TopicRpcDispatcher.Execute(element, "delete", null, prim);
    }

    /// <summary>Connects the sink (an input pin, a variable or a Pin) to args.source - an output pin, a
    /// variable or a Pin of the same program - by writing the link as the compiler reads it.</summary>
    private static ViewOpResult Bind(LplcModel.Target sink, JSC.JSValue args, Topic prim) {
      string sourceVid = args.AsString("source", null);
      LplcModel.Target source = LplcModel.Resolve(sourceVid);
      if(source == null) {
        return ViewOpResult.Error("bind_source_not_found", "Source not found: " + (sourceVid ?? "<null>"));
      }
      if(source.Program != sink.Program) {
        return ViewOpResult.Error("bind_source_invalid", "Source belongs to another LPLC program: " + sourceVid);
      }
      string link;
      if(source.Kind == LplcModel.TargetKind.OutputPin) {
        link = source.Topic.name + "." + source.Pin;
      } else if(source.Kind == LplcModel.TargetKind.Variable) {
        if(source.Topic == sink.Topic && sink.Kind == LplcModel.TargetKind.Variable) {
          return ViewOpResult.Error("bind_source_invalid", "Cannot bind a variable to itself");
        }
        link = source.Topic.name;
      } else if(source.Kind == LplcModel.TargetKind.Block && LplcModel.IsPin(source.Topic)) {
        if(source.Topic == sink.Topic) {
          return ViewOpResult.Error("bind_source_invalid", "Cannot bind a pin to itself");
        }
        link = source.Topic.name + "." + LplcModel.PinOutput;
      } else {
        return ViewOpResult.Error("bind_source_invalid", "Source is not an output: " + sourceVid);
      }
      // The compiler splits a link at its first dot, so a source name holding one could never resolve.
      if(source.Topic.name.IndexOf('.') >= 0) {
        return ViewOpResult.Error("bind_source_invalid", "A source name must not contain '.': " + source.Topic.name);
      }
      string field = sink.Kind == LplcModel.TargetKind.InputPin ? "LPLC.in." + sink.Pin
        : sink.Kind == LplcModel.TargetKind.Block ? LplcModel.PinInputField : LplcModel.VarInputField;
      sink.Topic.SetField(field, link, prim);
      return ViewOpResult.Success();
    }

    // === Pins ===

    private static ViewOpResult ShowInput(Topic block, string pin, Topic prim) {
      if(!IsPinName(pin)) return ViewOpResult.Error("pin_name_invalid", "Invalid input name: " + (pin ?? "<null>"));
      JSC.JSValue inputs = block.GetField("LPLC.in");
      if(inputs.IsObject() && inputs.GetProperty(pin).Defined) return ViewOpResult.Success();
      if(!LplcModel.InputCatalog(block).Contains(pin)) {
        return ViewOpResult.Error("pin_not_found", "The block has no input " + pin);
      }
      block.SetField("LPLC.in." + pin, string.Empty, prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult HideInput(Topic block, string pin, Topic prim) {
      if(!IsPinName(pin)) return ViewOpResult.Error("pin_name_invalid", "Invalid input name: " + (pin ?? "<null>"));
      block.SetField("LPLC.in." + pin, JSC.JSValue.Null, prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult ShowOutput(Topic block, string pin, Topic prim) {
      if(!IsPinName(pin)) return ViewOpResult.Error("pin_name_invalid", "Invalid output name: " + (pin ?? "<null>"));
      JSC.JSValue outs = block.GetField("LPLC.out");
      if(outs.IsObject()) {
        if(!outs.GetProperty(pin).Defined) block.SetField("LPLC.out." + pin, true, prim);
        return ViewOpResult.Success();
      }
      // No LPLC.out yet: the whole catalog is shown, so an output beyond it needs the list written out.
      List<string> shown = LplcModel.OutputCatalog(block);
      if(shown.Contains(pin)) return ViewOpResult.Success();
      shown.Add(pin);
      block.SetField("LPLC.out", OutputList(shown), prim);
      return ViewOpResult.Success();
    }

    /// <summary>Hides an output and disconnects every input it feeds - an output in use is always shown.</summary>
    private static ViewOpResult HideOutput(Topic program, Topic block, string pin, Topic prim) {
      if(!IsPinName(pin)) return ViewOpResult.Error("pin_name_invalid", "Invalid output name: " + (pin ?? "<null>"));
      LplcModel.ClearLinks(program, (n, p) => p == pin && n == block.name, prim);
      JSC.JSValue outs = block.GetField("LPLC.out");
      if(outs.IsObject()) {
        block.SetField("LPLC.out." + pin, JSC.JSValue.Null, prim);
        return ViewOpResult.Success();
      }
      // No LPLC.out yet: write what is shown now, less this one - removing a key that is not there
      // would change nothing.
      List<string> shown = LplcModel.ShownOutputs(block, LplcModel.OutputCatalog(block), null);
      shown.Remove(pin);
      block.SetField("LPLC.out", OutputList(shown), prim);
      return ViewOpResult.Success();
    }

    /// <summary>Func only: shows an output by name. Its src writes it as this.&lt;name&gt;; until it does the
    /// pin is drawn as stale and the compiler says so.</summary>
    private static ViewOpResult AddOutput(Topic block, string name, Topic prim) {
      if(!X13.Plc.PlcScript.UsesState(block.GetField("type").AsString(null))) {
        return ViewOpResult.Error("add_output_unsupported", "Only a state-script block gets outputs by name");
      }
      if(string.IsNullOrEmpty(name) || !OutputName.IsMatch(name)) {
        return ViewOpResult.Error("add_name_invalid", "Not an output name: " + (name ?? "<null>"));
      }
      return ShowOutput(block, name, prim);
    }

    private static JSC.JSValue OutputList(List<string> pins) {
      JSC.JSObject o = JSC.JSObject.CreateObject();
      foreach(string p in pins) o[p] = true;
      return o;
    }

    // A pin name goes into a dotted field path: a dot would address something else entirely.
    private static bool IsPinName(string pin) {
      return !string.IsNullOrEmpty(pin) && pin.IndexOf('.') < 0 && Topic.IsValidName(pin);
    }

    // === Creation ===

    /// <summary>A block of an Add-catalog type of the program, at the clicked cell, auto-named.</summary>
    private static ViewOpResult AddBlock(Topic program, string type, JSC.JSValue args, Topic prim) {
      LplcPaletteBuilder.BlockAction action = LplcPaletteBuilder.BlockActions(program).Find(a => string.Equals(a.Type, type, StringComparison.Ordinal));
      if(action == null) {
        return ViewOpResult.Error("lplc_type_not_found", "Block type not found: " + (type ?? "<null>"));
      }
      if(string.Equals(type, LplcModel.PinType, StringComparison.Ordinal)) {
        return ViewOpResult.Error("lplc_pin_args_required", "A Pin is added with its mode and pin: " + LplcPaletteBuilder.AddPinCmdPrefix + "<mode>:<pin>");
      }
      if(string.Equals(type, LplcModel.TwiType, StringComparison.Ordinal)) {
        return ViewOpResult.Error("lplc_twi_args_required", "Select a TWI bus from the TWI menu");
      }
      if(type == X13.Plc.PlcScript.SerialPort)
        return ViewOpResult.Error("lplc_serial_args_required", "Select a UART from the SerialPort menu");
      string name = args.AsString("name", null);
      if(name == null) {
        name = NextName(program, action.Action.AsString("namePrefix", action.Key));
      } else if(!IsElementName(name)) {
        return ViewOpResult.Error("add_name_invalid", "Invalid block name: " + name);
      } else if(program.Get(name, false) != null) {
        return ViewOpResult.Error("add_target_exists", "Topic already exists: " + program.path + "/" + name);
      }
      int top = Math.Max(0, args.AsInt("top", 0));
      int left = Math.Max(LogramGraphController.MinLeftCell, args.AsInt("left", 0));
      Create(program, name, action.Action, top, left, prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult AddSerial(Topic program, string spec, JSC.JSValue args, Topic prim) {
      int number;
      var bus = int.TryParse(spec, out number) ? LplcModel.SerialBuses(program).Find(b => b.Number == number) : null;
      if(bus == null) return ViewOpResult.Error("lplc_serial_not_found", "Unavailable UART: " + spec);
      if(LplcModel.SerialTaken(program, number)) return ViewOpResult.Error("add_target_exists", "UART already has a SerialPort");
      var action = LplcPaletteBuilder.BlockActions(program).Find(a => a.Type == X13.Plc.PlcScript.SerialPort);
      if(action == null) return ViewOpResult.Error("lplc_type_not_found", "SerialPort type not found");
      Create(program, NextName(program, "SerialPort"), action.Action, Math.Max(0, args.AsInt("top", 0)),
        Math.Max(LogramGraphController.MinLeftCell, args.AsInt("left", 0)), prim, LplcModel.SerialDefaults(program, number));
      return ViewOpResult.Success();
    }

    private static ViewOpResult AddTwi(Topic program, string spec, JSC.JSValue args, Topic prim) {
      int number;
      var bus = int.TryParse(spec, out number) ? LplcModel.TwiBuses(program).Find(b => b.Number == number) : null;
      if(bus == null) return ViewOpResult.Error("lplc_twi_not_found", "Unsupported TWI bus: " + spec);
      var action = LplcPaletteBuilder.BlockActions(program).Find(a => a.Type == LplcModel.TwiType);
      if(action == null) return ViewOpResult.Error("lplc_type_not_found", "Block type not found: " + LplcModel.TwiType);
      if(LplcModel.TwiTaken(program, bus)) return ViewOpResult.Error("add_target_exists", bus.Name + " already exists or has a master");
      var fields = new Dictionary<string, JSC.JSValue> { { "LPLC.args", JSC.JSObject.CreateObject() }, { "LPLC.args.TwiNr", number } };
      var sda = bus.Sda.DefaultPin;
      var scl = bus.Scl.DefaultPin;
      if(sda != null && scl != null && sda.Number == scl.Number) sda = scl = null;
      if(sda != null) fields["LPLC.args.SdaPin"] = PinValue(sda);
      if(scl != null) fields["LPLC.args.SclPin"] = PinValue(scl);
      Create(program, bus.Name, action.Action, Math.Max(0, args.AsInt("top", 0)),
        Math.Max(LogramGraphController.MinLeftCell, args.AsInt("left", 0)), prim, fields);
      return ViewOpResult.Success();
    }

    private static JSC.JSValue PinValue(LplcModel.PinChoice pin) {
      return pin.Named ? (JSC.JSValue)new JSL.String(pin.Name) : new JSL.Number(pin.Number);
    }


    /// <summary>A Pin from the catalog's Pin type: mode and pin from the device's PLC description, named
    /// mode_pin, at the clicked cell.</summary>
    /// <remarks>spec is &lt;mode&gt;:&lt;pin&gt; as LplcPaletteBuilder.PinItems writes it. The arguments are
    /// written as the compiler takes them: the mode by name, the pin by its PLC.names name or number. The
    /// option is left out - the mode's default. A mode without the input x gets no LPLC.in.</remarks>
    private static ViewOpResult AddPin(Topic program, string spec, JSC.JSValue args, Topic prim) {
      int colon = spec.IndexOf(':');
      string modeName = colon < 0 ? spec : spec.Substring(0, colon);
      string pinName = colon < 0 ? null : spec.Substring(colon + 1);
      LplcModel.PinMode mode = LplcModel.PinModes(program).Find(m => m.Name == modeName);
      if(mode == null) {
        return ViewOpResult.Error("lplc_pin_mode_not_found", "Pin mode not found: " + modeName);
      }
      LplcModel.PinChoice pin = mode.Pins.Find(p => p.Name == pinName);
      if(pin == null) {
        return ViewOpResult.Error("lplc_pin_not_found", mode.Name + " is not available on pin " + (pinName ?? "<null>"));
      }
      LplcPaletteBuilder.BlockAction action = LplcPaletteBuilder.BlockActions(program).Find(a => string.Equals(a.Type, LplcModel.PinType, StringComparison.Ordinal));
      if(action == null) {
        return ViewOpResult.Error("lplc_type_not_found", "Block type not found: " + LplcModel.PinType);
      }
      string name = LplcModel.PinName(mode, pin);
      if(!IsElementName(name)) {
        return ViewOpResult.Error("add_name_invalid", "Invalid block name: " + name);
      }
      if(program.Get(name, false) != null) {
        return ViewOpResult.Error("add_target_exists", "Topic already exists: " + program.path + "/" + name);
      }
      int top = Math.Max(0, args.AsInt("top", 0));
      int left = Math.Max(0, args.AsInt("left", 0));
      Dictionary<string, JSC.JSValue> fields = new Dictionary<string, JSC.JSValue>(StringComparer.Ordinal);
      fields["LPLC.args.mode"] = new JSL.String(mode.Name);
      fields["LPLC.args.pin"] = pin.Named ? (JSC.JSValue)new JSL.String(pin.Name) : new JSL.Number(pin.Number);
      if(!mode.TakesInput) fields["LPLC.in"] = JSC.JSValue.Null;
      Create(program, name, action.Action, top, left, prim, fields);
      return ViewOpResult.Success();
    }

    /// <summary>A variable from one of the device's LPLC templates, at the clicked cell.</summary>
    /// <remarks>Named by args.name when given (the template is willful), auto-named otherwise. Its tag is
    /// the template's prefix alone - the address comes with the next compile.</remarks>
    private static ViewOpResult AddVariable(Topic program, string key, JSC.JSValue args, Topic prim) {
      Topic device = program.parent;
      JSC.JSValue template = null;
      foreach(var kv in LplcModel.VarTemplates(device)) {
        if(string.Equals(kv.Key, key, StringComparison.Ordinal)) template = kv.Value;
      }
      if(template == null) {
        return ViewOpResult.Error("lplc_template_not_found", "Variable template not found: " + (key ?? "<null>"));
      }
      string name = args.AsString("name", null);
      if(name == null) {
        name = NextName(device, key);
      } else if(!IsElementName(name)) {
        return ViewOpResult.Error("add_name_invalid", "Invalid variable name: " + name);
      } else if(device.Get(name, false) != null) {
        return ViewOpResult.Error("add_target_exists", "Topic already exists: " + device.path + "/" + name);
      }
      int top = Math.Max(0, args.AsInt("top", 0));
      int left = Math.Max(0, args.AsInt("left", 0));
      Create(device, name, template, top, left, prim);
      return ViewOpResult.Success();
    }

    /// <param name="fields">Manifest fields to set over the action's; JSValue.Null removes one.</param>
    private static void Create(Topic parent, string name, JSC.JSValue action, int top, int left, Topic prim, Dictionary<string, JSC.JSValue> fields = null) {
      JSC.JSValue manifestSrc = action["manifest"];
      JSC.JSValue manifest = manifestSrc.IsObject() ? JsLib.Clone(manifestSrc) : JSC.JSObject.CreateObject();
      string type = manifest.AsString("type", null);
      Topic typeTopic = type == null ? null : Topic.root.Get("/$YS/TYPES/" + type, false);
      if(typeTopic != null) {
        foreach(var argument in X13.Plc.ConstructorSchema.Read(typeTopic.GetState()).Arguments) {
          if(argument.Derive == null && argument.Default.Defined && !manifest.Field("LPLC.args." + argument.Name).Defined)
            manifest = JsLib.SetField(manifest, "LPLC.args." + argument.Name, JsLib.Clone(argument.Default));
        }
      }
      manifest = JsLib.SetField(manifest, "LPLC.pos.top", new JSL.Number(top));
      manifest = JsLib.SetField(manifest, "LPLC.pos.left", new JSL.Number(left));
      if(fields != null) {
        foreach(var kv in fields) manifest = JsLib.SetField(manifest, kv.Key, kv.Value);
      }
      JSC.JSValue state = action["default"];
      Topic child = Topic.Declare(parent, name, prim);
      Topic.Fill(child, state != null && state.Defined ? JsLib.Clone(state) : JSC.JSValue.Null, manifest, prim);
    }

    // A link names its source by name and is split at the first dot, so a dot is refused here too.
    private static bool IsElementName(string name) {
      return Topic.IsValidName(name) && name.IndexOf('.') < 0;
    }

    private static string NextName(Topic parent, string prefix) {
      int i = 1;
      string name;
      do {
        name = prefix + i.ToString("D2");
        i++;
      } while(parent.Get(name, false) != null);
      return name;
    }

    // === Lifecycle - as LogramViewProvider ===

    private void ForgetRoot(LplcGraphController controller) {
      if(controller == null) return;
      LplcGraphController current;
      if(_open.TryGetValue(controller.Root, out current) && current == controller) {
        _open.Remove(controller.Root);
      }
    }

    public override ViewOpResult Close(string vid) {
      string topicPath = VidHelper.GetTopicPath(vid);
      LplcGraphController controller = null;
      Topic topic = string.IsNullOrEmpty(topicPath) ? null : Topic.root.Get(topicPath, false);
      if(topic != null) {
        _open.TryGetValue(topic, out controller);
        _open.Remove(topic);
      }
      if(controller != null) controller.Dispose();
      return ViewOpResult.Success();
    }

    public override void Dispose() {
      List<LplcGraphController> doomed = new List<LplcGraphController>(_open.Values);
      _open.Clear();
      foreach(LplcGraphController controller in doomed) controller.Dispose();
    }
  }
}
