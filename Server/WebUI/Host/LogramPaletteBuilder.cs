///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  // Builds the "Add block" context menu for an empty Logram canvas by walking the
  // /$YS/TYPES/LoBlock registry - the same data ES's palette panel reads
  // (ES/Logram/LogramForm.xaml.cs LoBlockLoad/LBDescrChanged), just rendered as a
  // categorized menu (one submenu per registry folder: Timer, Common, Math, ...)
  // instead of a flat drag-and-drop icon panel, since a menu with 20+ flat entries
  // is unusable. A descriptor topic (leaf, manifest.type=="Ext/LBDescr") becomes one
  // menu item; anything else is a category folder, recursed into and only added if
  // it actually yields at least one descriptor.
  internal static class LogramPaletteBuilder {
    private const string RegistryPath = "/$YS/TYPES/LoBlock";
    internal const string AddBlockCmdPrefix = "add-block:";

    internal static List<MenuItemDto> BuildAddMenu() {
      List<MenuItemDto> items = new List<MenuItemDto>();
      Topic registry = Topic.root.Get(RegistryPath, false);
      if(registry != null) AddCategory(registry, items);
      return items;
    }

    private static void AddCategory(Topic folder, List<MenuItemDto> items) {
      List<Topic> children = new List<Topic>(folder.children);
      children.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
      foreach(Topic child in children) {
        if(IsDescriptor(child)) {
          items.Add(BuildBlockItem(child));
          continue;
        }
        List<MenuItemDto> sub = new List<MenuItemDto>();
        AddCategory(child, sub);
        if(sub.Count > 0) {
          items.Add(new MenuItemDto() {
            Kind = MenuItemKind.Item,
            Text = child.name,
            Enabled = true,
            Willful = false,
            Children = sub,
          });
        }
      }
    }

    private static bool IsDescriptor(Topic topic) {
      return string.Equals(JsLib.OfString(topic.GetField("type"), null), "Ext/LBDescr", StringComparison.Ordinal);
    }

    // Tells a pin (right-click target inside a block, e.g. "En"/"Q" under a Timer
    // element) apart from a top-level element (block/variable, direct child of the
    // diagram) for BuildMenu's routing - true exactly when topic's own name is a
    // ddr'd Children entry of its PARENT's resolved type, i.e. the same test
    // LogramGraphController.BuildLayout uses to populate ElementLayout.Pins (mirrored
    // rather than shared, same reasoning as BuildAddPinItems above).
    internal static bool IsPin(Topic topic) {
      if(topic?.parent == null) return false;
      string typePath = JsLib.OfString(topic.parent.GetField("type"), null);
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      JSC.JSValue children = typeTopic?.GetState()?["Children"];
      if(!WorkspaceMenuBuilder.IsObject(children)) return false;
      return JsLib.OfInt(children[topic.name], "ddr", 0) != 0;
    }

    // Right-click on an existing block/variable element (not the empty canvas): for
    // a genuine block (its resolved type declares a real ddr'd pin schema - see
    // LogramGraphController.ChildrenSchema/TryGetDdr, mirrored here rather than
    // shared, to keep this self-contained), whichever declared pins aren't already
    // created on this instance yet (required pins are auto-created at block-creation
    // time - see LogramViewProvider.CreateRequiredChildren - so only optional ones
    // normally show up here) - flattened directly into the menu when there are few
    // enough to fit without a submenu, tucked under "Add pin" otherwise. "Delete"
    // always comes last, after a separator from whatever pin items are above it.
    internal static List<MenuItemDto> BuildElementMenu(Topic topic) {
      List<MenuItemDto> items = new List<MenuItemDto>();

      List<MenuItemDto> pinItems = BuildAddPinItems(topic);
      if(pinItems.Count > 0) {
        if(pinItems.Count < 11) {
          items.AddRange(pinItems);
        } else {
          items.Add(new MenuItemDto() {
            Kind = MenuItemKind.Item,
            Text = "Add pin",
            Enabled = true,
            Willful = false,
            Children = pinItems,
          });
        }
        items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });
      }

      bool canDelete = topic.parent != null && !topic.CheckAttribute(Topic.Attribute.Required);
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "delete",
        Text = "Delete",
        Icon = "/ide_icons/cm_delete.png",
        Hint = "Delete element",
        Enabled = canDelete,
        Willful = false,
      });
      return items;
    }

    // Right-click on a pin itself - ports ES's loPin branch of LogramView.cs
    // MenuItems: Open/Show in Workspace (generic topic actions every row gets there;
    // Logram's menu doesn't route through WorkspaceMenuBuilder at all, so they're
    // just plain cmd items here, dispatched client-side in logram-document.js
    // #onMenuCommand rather than server-built submenus), Trace (checkable, mirrors
    // t.SetField("Logram.trace", !ic) - toggled via LogramViewProvider.ExecuteRpc's
    // "trace" command; ES also uses this flag to show a pin's live value as an
    // on-canvas label (LogramItems.cs), which isn't ported here - out of scope for
    // just the menu), then Delete (same Required-attribute guard as an element, but
    // pins are never parentless so the topic.parent!=null half of that check is
    // skipped).
    internal static List<MenuItemDto> BuildPinMenu(Topic topic) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "open", Text = "Open", Icon = "/ide_icons/cm_open.png", Hint = "Open pin in Inspector", Enabled = true, Willful = false });
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "show-in-workspace", Text = "Show in Workspace", Hint = "Reveal pin in the Workspace tree", Enabled = true, Willful = false });
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });

      bool traced = JsLib.ofBool(topic.GetField("Logram.trace"), false);
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Cmd = "trace", Text = "Trace", Hint = "Toggle Logram.trace", Checked = traced, Enabled = true, Willful = false });
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });

      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "delete",
        Text = "Delete",
        Icon = "/ide_icons/cm_delete.png",
        Hint = "Delete pin",
        Enabled = !topic.CheckAttribute(Topic.Attribute.Required),
        Willful = false,
      });
      return items;
    }

    private static List<MenuItemDto> BuildAddPinItems(Topic topic) {
      List<MenuItemDto> pins = new List<MenuItemDto>();
      string typePath = JsLib.OfString(topic.GetField("type"), null);
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      JSC.JSValue typeState = typeTopic?.GetState();
      if(!WorkspaceMenuBuilder.IsObject(typeState)) return pins;
      JSC.JSValue children = typeState["Children"];
      if(!WorkspaceMenuBuilder.IsObject(children)) return pins;

      foreach(var entry in children) {
        if(JsLib.OfInt(entry.Value, "ddr", 0) == 0) continue;
        if(topic.Get(entry.Key, false) != null) continue;
        pins.Add(WorkspaceMenuBuilder.BuildAddItem(entry.Key, entry.Value, typeTopic.path));
      }
      pins.Sort((a, b) => string.Compare(a.Text ?? a.Cmd, b.Text ?? b.Cmd, StringComparison.Ordinal));
      return pins;
    }

    private static MenuItemDto BuildBlockItem(Topic descriptor) {
      JSC.JSValue state = descriptor.GetState();
      string typePath = TypeHelper.StripTypeRoot(descriptor.path);
      string icon = JsLib.OfString(state["icon"], null);
      string iconUrl = string.IsNullOrEmpty(icon) ? null : IconResource.ResolveIconValue(icon, typePath);
      return new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = AddBlockCmdPrefix + typePath,
        Text = descriptor.name,
        Icon = iconUrl,
        Hint = JsLib.OfString(state["hint"], null),
        Enabled = true,
        Willful = false,
      };
    }
  }
}
