///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  /// <summary>Context menus of the LPLC editor - the counterpart of LogramPaletteBuilder.</summary>
  /// <remarks>Commands, all handled by LplcViewProvider.ExecuteRpc:
  /// <list type="bullet">
  /// <item>canvas: add-block:&lt;type&gt; (the program's Add catalog, grouped by catalog folder),
  /// add-pin:&lt;mode&gt;:&lt;pin&gt; (the catalog's Pin as a mode - pin submenu from the device's PLC description)
  /// and add-var:&lt;template&gt; (the device's LPLC variable templates); all carry the clicked cell;</item>
  /// <item>block: show-in:/hide-in:&lt;pin&gt;, show-out:/hide-out:&lt;pin&gt;, add-out (Func only, willful -
  /// the client asks for the name), delete;</item>
  /// <item>Pin (drawn as a variable): trace, unbind, delete;</item>
  /// <item>pin: unbind (an input), trace (an output), hide; variable: unbind, delete.</item>
  /// </list>
  /// open and show-in-workspace are handled by the client alone, from the vid's path.</remarks>
  internal static class LplcPaletteBuilder {
    internal const string AddBlockCmdPrefix = "add-block:";
    internal const string AddPinCmdPrefix = "add-pin:";
    internal const string AddTwiCmdPrefix = "add-twi:";
    internal const string AddSerialCmdPrefix = "add-serial:";
    internal const string AddVarCmdPrefix = "add-var:";
    internal const string ShowInCmdPrefix = "show-in:";
    internal const string HideInCmdPrefix = "hide-in:";
    internal const string ShowOutCmdPrefix = "show-out:";
    internal const string HideOutCmdPrefix = "hide-out:";
    internal const string AddOutCmd = "add-out";

    internal static List<MenuItemDto> BuildMenu(LplcModel.Target target) {
      switch(target.Kind) {
      case LplcModel.TargetKind.Program:
        return BuildCanvasMenu(target.Program);
      case LplcModel.TargetKind.Block:
        return LplcModel.IsPin(target.Topic) ? BuildPinMenu(target.Topic) : BuildBlockMenu(target.Topic, target.Program);
      case LplcModel.TargetKind.Variable:
        return BuildVariableMenu(target.Topic);
      case LplcModel.TargetKind.ConstructorArgument:
        return ArgumentMenu(target.Topic, target.Pin).Children ?? new List<MenuItemDto>();
      case LplcModel.TargetKind.InputPin:
        return BuildInputPinMenu(target.Topic, target.Pin);
      default:
        return BuildOutputPinMenu(target.Topic, target.Pin);
      }
    }

    // === Canvas ===

    private static List<MenuItemDto> BuildCanvasMenu(Topic program) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      List<BlockAction> blocks = BlockActions(program);
      blocks.Sort((a, b) => {
        int c = string.CompareOrdinal(a.Menu ?? string.Empty, b.Menu ?? string.Empty);
        return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
      });
      foreach(BlockAction b in blocks) {
        MenuItemDto item = MenuBuilder.BuildAddItem(b.Key, b.Action, b.SourcePath);
        item.Cmd = AddBlockCmdPrefix + b.Type;
        // The grouping is the catalog folder (AddToMenuTree below), not BuildAddItem's "menu:" hint.
        item.Hint = b.Action.AsString("hint", null);
        item.Willful = false;
        if(string.Equals(b.Type, LplcModel.PinType, StringComparison.Ordinal)) {
          item.Cmd = null;
          item.Children = PinItems(program);
          item.Enabled = item.Children.Count > 0;
          if(!item.Enabled) item.Hint = "No pin modes in the PLC description of the device type";
        }
        if(string.Equals(b.Type, LplcModel.TwiType, StringComparison.Ordinal)) {
          item.Cmd = null;
          item.Text = "TWI";
          item.Children = new List<MenuItemDto>();
          foreach(var bus in LplcModel.TwiBuses(program)) {
            item.Children.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = bus.Name,
              Cmd = AddTwiCmdPrefix + bus.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
              Enabled = !LplcModel.TwiTaken(program, bus), Willful = false });
          }
          item.Enabled = item.Children.Count > 0;
          if(!item.Enabled) item.Hint = "No complete TWI SDA/SCL pairs in the device description";
        }
        if(b.Type == X13.Plc.PlcScript.SerialPort) {
          item.Cmd = null;
          item.Children = LplcModel.SerialBuses(program).Select(bus => new MenuItemDto {
            Kind = MenuItemKind.Item, Text = "UART" + bus.Number,
            Cmd = AddSerialCmdPrefix + bus.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Enabled = !LplcModel.SerialTaken(program, bus.Number), Willful = false
          }).ToList();
          item.Enabled = item.Children.Count > 0;
          if(!item.Enabled) item.Hint = "No available UART TX/RX pair in the device description";
        }
        AddToMenuTree(items, b.Menu, item);
      }

      List<MenuItemDto> vars = new List<MenuItemDto>();
      Topic device = program.parent;
      foreach(var kv in LplcModel.VarTemplates(device)) {
        MenuItemDto item = MenuBuilder.BuildAddItem(kv.Key, kv.Value, device.path);
        item.Cmd = AddVarCmdPrefix + kv.Key;
        // BuildAddItem turns the template's menu ("LPLC") into the tree's grouping hint.
        item.Hint = kv.Value.AsString("hint", null);
        item.Willful = true;
        vars.Add(item);
      }
      if(vars.Count > 0) {
        if(items.Count > 0) items.Add(Separator());
        items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Variable", Enabled = true, Willful = false, Children = vars });
      }
      return items;
    }

    /// <summary>One block type the program offers - an Add entry whose manifest types it as LPLC/*.</summary>
    internal sealed class BlockAction {
      public string Key;
      public string Type;
      public JSC.JSValue Action;
      public string SourcePath;
      public string Menu;
    }

    /// <summary>The block types of the program's Add catalog (Ext/LogramPLC: Children = /$YS/TYPES/LPLC).</summary>
    /// <remarks>The same resolution the Inspector's Add menu uses, so the editor and the tree can never
    /// offer different blocks; filtered to LPLC/* manifests, which also drops ResolveAddActions' Core
    /// fallback.</remarks>
    internal static List<BlockAction> BlockActions(Topic program) {
      List<BlockAction> r = new List<BlockAction>();
      foreach(var kv in MenuBuilder.ResolveAddActions(program)) {
        JSC.JSValue action = kv.Value.Action;
        if(!LplcModel.IsBlockAction(action)) continue;
        r.Add(new BlockAction() {
          Key = kv.Key,
          Type = action.AsString("manifest.type", null),
          Action = action,
          SourcePath = kv.Value.SourcePath,
          Menu = kv.Value.Menu,
        });
      }
      return r;
    }

    /// <summary>Pin: a submenu per mode, in it the mode's pins by name. A pin whose block (mode_pin) is already
    /// in the program is shown disabled - the name is taken.</summary>
    private static List<MenuItemDto> PinItems(Topic program) {
      List<MenuItemDto> modes = new List<MenuItemDto>();
      foreach(LplcModel.PinMode mode in LplcModel.PinModes(program)) {
        List<MenuItemDto> pins = new List<MenuItemDto>();
        foreach(LplcModel.PinChoice pin in mode.Pins) {
          string name = LplcModel.PinName(mode, pin);
          bool taken = program.Get(name, false) != null;
          pins.Add(new MenuItemDto() {
            Kind = MenuItemKind.Item,
            Cmd = AddPinCmdPrefix + mode.Name + ":" + pin.Name,
            Text = pin.Name,
            Hint = taken ? name + " is already in the program" : "New Pin " + name,
            Enabled = !taken,
            Willful = false,
          });
        }
        modes.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = mode.Name, Enabled = true, Willful = false, Children = pins });
      }
      return modes;
    }

    private static void AddToMenuTree(List<MenuItemDto> root, string menu, MenuItemDto item) {
      List<MenuItemDto> current = root;
      if(!string.IsNullOrEmpty(menu)) {
        foreach(string part in menu.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) {
          MenuItemDto node = current.Find(z => z.Kind == MenuItemKind.Item && z.Cmd == null && z.Text == part);
          if(node == null) {
            node = new MenuItemDto() { Kind = MenuItemKind.Item, Text = part, Enabled = true, Willful = false, Children = new List<MenuItemDto>() };
            current.Add(node);
          } else if(!node.Enabled && item.Enabled) {
            node.Enabled = true;
            node.Hint = null;
          }
          current = node.Children;
        }
      }
      current.Add(item);
    }

    // === Elements ===

    private static List<MenuItemDto> BuildBlockMenu(Topic block, Topic program) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      AddNavigation(items);
      var configuration = LplcModel.Configuration(block);
      var arguments = configuration.Schema.Arguments.Where(a => a.Position > 0).OrderBy(a => a.Position).Select(a => ArgumentMenu(block, a.Name)).ToList();
      if(arguments.Count > 0) items.Add(new MenuItemDto { Kind = MenuItemKind.Item, Text = "Constructor", Enabled = true, Children = arguments });

      List<string> inCatalog = LplcModel.InputCatalog(block);
      List<string> shownIn = LplcModel.ShownInputs(block, inCatalog);
      List<MenuItemDto> ins = new List<MenuItemDto>();
      foreach(string pin in Union(inCatalog, shownIn)) {
        bool shown = shownIn.Contains(pin);
        ins.Add(Toggle(pin, shown, ShowInCmdPrefix, HideInCmdPrefix, "input"));
      }

      List<string> outCatalog = LplcModel.OutputCatalog(block);
      List<string> referenced;
      LplcModel.ReferencedOutputs(LplcModel.Blocks(program), LplcModel.Variables(program)).TryGetValue(block.name, out referenced);
      List<string> shownOut = LplcModel.ShownOutputs(block, outCatalog, referenced);
      List<MenuItemDto> outs = new List<MenuItemDto>();
      foreach(string pin in Union(outCatalog, shownOut)) {
        bool shown = shownOut.Contains(pin);
        outs.Add(Toggle(pin, shown, ShowOutCmdPrefix, HideOutCmdPrefix, "output"));
      }
      if(IsFunc(block)) {
        if(outs.Count > 0) outs.Add(Separator());
        outs.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = AddOutCmd, Text = "Add output...", Hint = "Show an output by name - the src writes it as this.<name>", Enabled = true, Willful = true });
      }

      if(ins.Count > 0) items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Inputs", Enabled = true, Willful = false, Children = ins });
      if(outs.Count > 0) items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Outputs", Enabled = true, Willful = false, Children = outs });
      if(ins.Count > 0 || outs.Count > 0) items.Add(Separator());
      items.Add(Delete(block, "Delete block"));
      return items;
    }

    private static MenuItemDto ArgumentMenu(Topic block, string name) {
      var argument = LplcModel.Configuration(block).Schema.Find(name);
      var resolver = LplcModel.Configurations(block.parent);
      var result = resolver.Resolve(block.name);
      string error;
      var menu = new MenuItemDto { Kind = MenuItemKind.Item, Text = argument.Label, Enabled = !argument.Readonly,
        Hint = result.Errors.TryGetValue(name, out error) || result.Errors.TryGetValue("", out error) ? error : null, Children = new List<MenuItemDto>() };
      if(argument.Readonly) return menu;
      if(result.Errors.ContainsKey("")) { menu.Enabled = false; return menu; }
      var current = block.GetField("LPLC.args." + name);
      if(!argument.Required) menu.Children.Add(ArgumentItem(name, "Unset", JSC.JSValue.Null, true, !current.Defined || current.IsNull, true));
      if(argument.Choices != null) {
        foreach(var choice in resolver.Choices(block.name, name)) menu.Children.Add(ArgumentItem(name, choice.Label, choice.Value, choice.Enabled, resolver.EqualChoice(argument, current, choice.Value)));
      } else if(argument.Editor == "Boolean") {
        menu.Children.Add(ArgumentItem(name, "false", false, true, current.ValueType == JSC.JSValueType.Boolean && !(bool)current));
        menu.Children.Add(ArgumentItem(name, "true", true, true, current.ValueType == JSC.JSValueType.Boolean && (bool)current));
      } else {
        var item = ArgumentItem(name, "Set value...", current, true, false);
        item.Cmd = "edit-arg";
        menu.Children.Add(item);
      }
      return menu;
    }

    private static MenuItemDto ArgumentItem(string name, string label, JSC.JSValue value, bool enabled, bool selected, bool unset = false) {
      var args = JSC.JSObject.CreateObject();
      args["name"] = name;
      if(unset) args["unset"] = true; else if(value != null && value.Defined) args["value"] = value;
      return new MenuItemDto { Kind = MenuItemKind.Item, Text = label, Cmd = "set-arg", Args = args, Enabled = enabled, Checked = selected };
    }


    // A Pin is drawn as a variable - no pins of its own to toggle; its input x is the variable's input.
    private static List<MenuItemDto> BuildPinMenu(Topic pin) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      AddNavigation(items);
      items.Add(Trace(pin, LplcModel.PinOutput));
      bool connected = LplcModel.IsConnected(pin.GetField(LplcModel.PinInputField));
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "unbind", Text = "Disconnect", Hint = "Disconnect the pin's input", Enabled = connected, Willful = false });
      items.Add(Separator());
      items.Add(Delete(pin, "Delete pin"));
      return items;
    }

    private static List<MenuItemDto> BuildVariableMenu(Topic variable) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      AddNavigation(items);
      bool connected = LplcModel.IsConnected(variable.GetField(LplcModel.VarInputField));
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "unbind", Text = "Disconnect", Hint = "Disconnect the variable's input", Enabled = connected, Willful = false });
      items.Add(Separator());
      items.Add(Delete(variable, "Delete variable"));
      return items;
    }

    private static List<MenuItemDto> BuildInputPinMenu(Topic block, string pin) {
      JSC.JSValue inputs = block.GetField("LPLC.in");
      JSC.JSValue link = inputs.IsObject() ? inputs.GetProperty(pin) : JSC.JSValue.NotExists;
      List<MenuItemDto> items = new List<MenuItemDto>();
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "unbind", Text = "Disconnect", Hint = "Leave the input unconnected", Enabled = LplcModel.IsConnected(link), Willful = false });
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "hide", Text = "Hide", Hint = "Hide the input", Enabled = true, Willful = false });
      return items;
    }

    private static List<MenuItemDto> BuildOutputPinMenu(Topic block, string pin) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      items.Add(Trace(block, pin));
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "hide", Text = "Hide", Hint = "Hide the output and disconnect every input it feeds", Enabled = true, Willful = false });
      return items;
    }

    private static MenuItemDto Trace(Topic block, string pin) {
      bool traced = LplcModel.IsTraced(block, pin);
      return new MenuItemDto() {
        Kind = MenuItemKind.Item, Cmd = "trace", Text = "Trace", Checked = traced,
        Enabled = traced || LplcModel.CanTrace(block, pin), Willful = false,
        Hint = "Show the device value at this output using " + LplcModel.ObserverName(block, pin),
      };
    }

    // === Helpers ===

    private static bool IsFunc(Topic block) {
      return X13.Plc.PlcScript.UsesState(block.GetField("type").AsString(null));
    }

    // Catalog order first, then what is shown beyond it - a stale pin stays reachable, so it can be hidden.
    private static List<string> Union(List<string> catalog, List<string> shown) {
      List<string> r = new List<string>(catalog);
      foreach(string p in shown) {
        if(!r.Contains(p)) r.Add(p);
      }
      return r;
    }

    private static MenuItemDto Toggle(string pin, bool shown, string showPrefix, string hidePrefix, string what) {
      return new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = (shown ? hidePrefix : showPrefix) + pin,
        Text = pin,
        Hint = (shown ? "Hide " : "Show ") + what + " " + pin,
        Checked = shown,
        Enabled = true,
        Willful = false,
      };
    }

    private static void AddNavigation(List<MenuItemDto> items) {
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "open", Text = "Open", Icon = "/ide/icons/cm_open.png", Hint = "Open in Inspector", Enabled = true, Willful = false });
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "show-in-workspace", Text = "Show in Workspace", Hint = "Reveal in the Workspace tree", Enabled = true, Willful = false });
      items.Add(Separator());
    }

    private static MenuItemDto Delete(Topic topic, string hint) {
      return new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "delete",
        Text = "Delete",
        Icon = "/ide/icons/cm_delete.png",
        Hint = hint,
        Enabled = topic.parent != null && !topic.CheckAttribute(Topic.Attribute.Required),
        Willful = false,
      };
    }

    private static MenuItemDto Separator() {
      return new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true };
    }
  }
}
