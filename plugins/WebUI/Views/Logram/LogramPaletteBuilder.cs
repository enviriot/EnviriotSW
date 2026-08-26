///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
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
          MenuItemDto item = BuildBlockItem(child);
          if(item != null) items.Add(item);
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
      return string.Equals(topic.GetField("type").AsString(null), "Ext/LBDescr", StringComparison.Ordinal);
    }

    // Tells a pin (right-click target inside a block, e.g. "En"/"Q" under a Timer
    // element) apart from a top-level element (block/variable, direct child of the
    // diagram) for BuildMenu's routing - true exactly when topic's own name is a
    // ddr'd Children entry of its PARENT's resolved type, i.e. the same test
    // LogramGraphController.BuildLayout uses to populate ElementLayout.Pins (mirrored
    // rather than shared, same reasoning as BuildAddPinItems above).
    internal static bool IsPin(Topic topic) {
      if(topic?.parent == null) return false;
      string typePath = topic.parent.GetField("type").AsString(null);
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      // JsLib.GetField, not the raw indexer: GetState() is `_state ?? JSValue.Null` and never
      // returns C# null, so the `?.` that used to sit here was dead and the index ran straight
      // into the JSValue.Undefined a grouping node under /$YS/TYPES carries - the same TypeError
      // already fixed in IconResource.ResolveTypeIcon. BuildMenu calls this, so it took the
      // whole element and pin context menu down, not one entry.
      JSC.JSValue children = typeTopic == null ? JSC.JSValue.NotExists : typeTopic.GetState().Field("Children");
      if(!children.IsObject()) return false;
      return children[topic.name].AsInt("ddr", 0) != 0;
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
    // Logram's menu doesn't route through MenuBuilder at all, so they're
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

      bool traced = topic.GetField("Logram.trace").AsBool(false);
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
      string typePath = topic.GetField("type").AsString(null);
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      JSC.JSValue typeState = typeTopic?.GetState();
      if(!typeState.IsObject()) return pins;
      JSC.JSValue children = typeState["Children"];
      if(!children.IsObject()) return pins;

      foreach(var entry in children) {
        if(entry.Value.AsInt("ddr", 0) == 0) continue;
        if(topic.Get(entry.Key, false) != null) continue;
        pins.Add(MenuBuilder.BuildAddItem(entry.Key, entry.Value, typeTopic.path));
      }
      pins.Sort((a, b) => string.Compare(a.Text ?? a.Cmd, b.Text ?? b.Cmd, StringComparison.Ordinal));
      return pins;
    }

    // null for a descriptor with no usable state. IsDescriptor selects purely on the manifest
    // (type == "Ext/LBDescr"), so a topic can qualify while its state is still Undefined - and
    // indexing that threw, taking the entire /$YS/TYPES/LoBlock walk with it rather than one
    // entry. Skipped rather than listed: ExecuteAddBlock guards the same value and answers
    // logram_type_not_found, so the item could never have worked anyway.
    private static MenuItemDto BuildBlockItem(Topic descriptor) {
      JSC.JSValue state = descriptor.GetState();
      if(!state.IsObject()) return null;
      string typePath = TypeHelper.StripTypeRoot(descriptor.path);
      // ResolveIconRef, not ResolveIconValue: the latter only decodes data: URIs, and the
      // whole LoBlock/Variable category names its icons semantically ("Boolean", "Double",
      // "Integer", "String") the way the Core types do, so that section came through with no
      // icons at all. Unknown names still yield null - the Add/Action items deliberately send
      // an empty icon rather than falling back to ty_topic.png.
      string iconUrl = IconResource.ResolveIconRef(state.AsString("icon", null), typePath);
      return new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = AddBlockCmdPrefix + typePath,
        Text = descriptor.name,
        Icon = iconUrl,
        Hint = state.AsString("hint", null),
        Enabled = true,
        Willful = false,
      };
    }
  }
}
