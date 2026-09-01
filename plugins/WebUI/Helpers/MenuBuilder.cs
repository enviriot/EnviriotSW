///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal static class MenuBuilder {
    internal static List<MenuItemDto> Build(Topic topic) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      items.Add(BuildToolbar(topic));
      AddAddMenuItems(topic, items);
      AddActionMenuItems(topic, items);
      AddRootCatalogMenuItem(topic, items);
      AddRootImportMenuItem(topic, items);
      AddChartMenuItem(topic, items);
      AddExportMenuItem(topic, items);
      return items;
    }

    private static MenuItemDto BuildToolbar(Topic topic) {
      bool canOpen = topic != null;
      bool isNormalTopic = topic != null && topic.parent != null;
      bool canModify = isNormalTopic && !topic.CheckAttribute(Topic.Attribute.Required);
      return new MenuItemDto() {
        Kind = MenuItemKind.Toolbar,
        Enabled = true,
        Willful = false,
        Children = new List<MenuItemDto>() {
          MenuItem("open", "Open", "/ide_icons/cm_open.png", "Open topic", canOpen),
          MenuItem("open-tab", "Open in new tab", "/ide_icons/cm_doc.png", "Open topic in new tab", canOpen),
          MenuItem("copy-path", "Copy path", "/ide_icons/cm_path.png", "Copy topic path", canOpen),
          MenuItem("rename", "Rename", "/ide_icons/cm_rename.png", "Rename topic", canModify),
          MenuItem("cut", "Cut", "/ide_icons/cm_cut.png", "Cut topic", canModify),
          MenuItem("paste", "Paste", "/ide_icons/cm_paste.png", "Paste topic", false),
          MenuItem("delete", "Delete", "/ide_icons/cm_delete.png", "Delete topic", canModify),
        },
      };
    }

    private static MenuItemDto MenuItem(string cmd, string text, string icon, string hint, bool enabled) {
      return new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = cmd,
        Text = text,
        Icon = icon,
        Hint = hint,
        Enabled = enabled,
        Willful = false,
      };
    }

    private static void AddRootCatalogMenuItem(Topic topic, List<MenuItemDto> items) {
      if(topic == null || topic.parent != null) return;
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "catalog",
        Text = "Catalog",
        Icon = "/ide_icons/cm_catalog.png",
        Hint = "Open catalog",
        Enabled = true,
        Willful = false,
      });
    }

    private static void AddRootImportMenuItem(Topic topic, List<MenuItemDto> items) {
      if(topic == null || topic.parent != null) return;
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "import",
        Text = "Import",
        Icon = "/ide_icons/cm_import.png",
        Hint = "Import .xst file",
        Enabled = true,
        Willful = false,
      });
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });
    }

    private static void AddExportMenuItem(Topic topic, List<MenuItemDto> items) {
      if(topic == null) return;
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "export",
        Text = "Export",
        Icon = "/ide_icons/cm_export.png",
        Hint = "Export topic to .xst file",
        Enabled = true,
        Willful = false,
      });
    }

    /// <summary>"Chart" - the topic's archived history, for topics the archivist actually keeps.</summary>
    /// <remarks>Offered from the menu regardless of what the row's AltView says, and that is the
    /// point of the two being separate: AltView names the ONE view a breadcrumb button can
    /// offer, and Logram wins it, so a Logram topic that is also archived would otherwise have
    /// no route to its chart at all. The menu is built server-side, one topic at a time, with
    /// the field already in hand - see RowProjector.IsArchived for how it is read.</remarks>
    private static void AddChartMenuItem(Topic topic, List<MenuItemDto> items) {
      if(!RowProjector.IsArchived(topic)) return;
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "chart",
        Text = "Chart",
        Icon = "/ide_icons/cm_chart.svg",
        Hint = "Show archived history",
        Enabled = true,
        Willful = false,
      });
    }

    private static void AddActionMenuItems(Topic topic, List<MenuItemDto> items) {
      if(topic == null) return;

      List<MenuItemDto> actionItems = ResolveActionItems(topic);
      if(actionItems.Count == 0) return;

      if(actionItems.Count < 4) {
        items.AddRange(actionItems);
      } else {
        items.Add(new MenuItemDto() {
          Kind = MenuItemKind.Item,
          Text = "Action",
          Enabled = true,
          Willful = false,
          Children = actionItems,
        });
      }
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });
    }

    private static List<MenuItemDto> ResolveActionItems(Topic topic) {
      Dictionary<string, MenuItemDto> items = new Dictionary<string, MenuItemDto>(StringComparer.Ordinal);
      AddActionItemsFromValue(topic.GetField("Action"), items, topic.path);

      Topic typeTopic = ResolveTopicType(topic);
      if(typeTopic != null) {
        JSC.JSValue typeState = typeTopic.GetState();
        if(typeState.IsObject()) AddActionItemsFromValue(typeState["Action"], items, typeTopic.path);
      }

      return items.Values.OrderBy(z => z.Text ?? z.Cmd, StringComparer.Ordinal).ToList();
    }

    private static void AddActionItemsFromValue(JSC.JSValue actions, Dictionary<string, MenuItemDto> items, string topicPath) {
      if(!actions.IsObject()) return;
      foreach(var kv in actions) {
        JSC.JSValue action = kv.Value;
        if(!action.IsObject()) continue;

        string name = action.AsString("name", null);
        if(string.IsNullOrWhiteSpace(name)) name = kv.Key;
        if(string.IsNullOrWhiteSpace(name) || items.ContainsKey(name)) continue;

        string text = action.AsString("text", null);
        if(string.IsNullOrWhiteSpace(text)) text = name;
        items.Add(name, new MenuItemDto() {
          Kind = MenuItemKind.Item,
          Cmd = "action:" + name,
          Text = text,
          Icon = ResolveMenuIcon(action.AsString("icon", null), name, topicPath),
          Hint = action.AsString("hint", null),
          Enabled = true,
          Willful = false,
        });
      }
    }

    internal static bool ResolveActionDescriptor(Topic topic, string name, out JSC.JSValue descriptor) {
      Dictionary<string, JSC.JSValue> descriptors = new Dictionary<string, JSC.JSValue>(StringComparer.Ordinal);
      AddActionDescriptorsFromValue(topic == null ? null : topic.GetField("Action"), descriptors);

      Topic typeTopic = ResolveTopicType(topic);
      if(typeTopic != null) {
        JSC.JSValue typeState = typeTopic.GetState();
        if(typeState.IsObject()) AddActionDescriptorsFromValue(typeState["Action"], descriptors);
      }

      return descriptors.TryGetValue(name, out descriptor);
    }

    private static void AddActionDescriptorsFromValue(JSC.JSValue actions, Dictionary<string, JSC.JSValue> descriptors) {
      if(!actions.IsObject()) return;
      foreach(var kv in actions) {
        JSC.JSValue action = kv.Value;
        if(!action.IsObject()) continue;
        string name = action.AsString("name", null);
        if(string.IsNullOrWhiteSpace(name)) name = kv.Key;
        if(string.IsNullOrWhiteSpace(name) || descriptors.ContainsKey(name)) continue;
        descriptors.Add(name, action);
      }
    }

    private static void AddAddMenuItems(Topic topic, List<MenuItemDto> items) {
      if(topic == null) return;

      Dictionary<string, AddActionEntry> actions = ResolveAddActions(topic);
      if(actions == null || actions.Count == 0) return;

      List<MenuItemDto> addItems = new List<MenuItemDto>();
      foreach(KeyValuePair<string, AddActionEntry> action in actions.OrderBy(z => z.Key)) {
        if(ResourceBusy(topic, actions, action.Key, action.Value.Action)) continue;
        addItems.Add(BuildAddItem(action.Key, action.Value.Action, action.Value.SourcePath));
      }
      if(addItems.Count == 0) return;

      bool hasGroups = addItems.Any(z => !string.IsNullOrEmpty(z.Hint) && z.Hint.StartsWith("menu:", StringComparison.Ordinal));
      if(addItems.Count < 8 && !hasGroups) {
        items.AddRange(addItems);
      } else {
        MenuItemDto addRoot = new MenuItemDto() {
          Kind = MenuItemKind.Item,
          Text = "Add",
          Enabled = true,
          Willful = false,
          Children = new List<MenuItemDto>(),
        };
        foreach(MenuItemDto item in addItems) {
          string menu = null;
          if(!string.IsNullOrEmpty(item.Hint) && item.Hint.StartsWith("menu:", StringComparison.Ordinal)) {
            menu = item.Hint.Substring(5);
            item.Hint = actions[item.Text].Action.AsString("hint", null);
          }
          AddToMenuTree(addRoot.Children, menu, item);
        }
        items.Add(addRoot);
      }
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });
    }

    // Tracks, alongside each add-action's manifest value, the path of the topic
    // that actually declared it (the current topic for its own inline "Children"
    // field, or the type/Core topic when the action is inherited) - needed so
    // dynamic (inline data:image) icons resolve under the declaring topic's path,
    // not the instance topic being right-clicked.
    internal sealed class AddActionEntry {
      public JSC.JSValue Action;
      public string SourcePath;
    }

    internal static Dictionary<string, AddActionEntry> ResolveAddActions(Topic topic) {
      Dictionary<string, AddActionEntry> actions = new Dictionary<string, AddActionEntry>(StringComparer.Ordinal);
      JSC.JSValue ownChildren = topic == null ? null : topic.GetField("Children");
      Topic typeTopic = ResolveTopicType(topic);
      JSC.JSValue typeChildren = TypeChildrenValue(typeTopic);

      if(ownChildren.IsObject()) {
        AddActionsFromObject(ownChildren, actions, topic.path);
        if(typeChildren.IsObject()) AddActionsFromObject(typeChildren, actions, typeTopic.path);
      } else if(ownChildren.Is<string>()) {
        AddActionsFromChildrenPath(ownChildren, actions);
      } else if(typeChildren.IsObject()) {
        AddActionsFromObject(typeChildren, actions, typeTopic.path);
      } else if(typeChildren.Is<string>()) {
        AddActionsFromChildrenPath(typeChildren, actions);
      }

      if(actions.Count == 0) AddActionsFromTopicChildren(Topic.root.Get("/$YS/TYPES/Core", false), actions);
      return actions;
    }

    private static JSC.JSValue TypeChildrenValue(Topic typeTopic) {
      if(typeTopic == null) return JSC.JSValue.NotExists;
      JSC.JSValue state = typeTopic.GetState();
      if(!state.IsObject()) return JSC.JSValue.NotExists;
      return state["Children"];
    }

    private static Topic ResolveTopicType(Topic topic) {
      if(topic == null) return null;
      string typePath = topic.GetField("type").AsString(null);
      return TypeHelper.ResolveTypeTopic(typePath);
    }

    private static void AddActionsFromChildrenPath(JSC.JSValue children, Dictionary<string, AddActionEntry> actions) {
      string sourcePath = children.AsString(null);
      if(string.IsNullOrWhiteSpace(sourcePath)) return;
      AddActionsFromTopicChildren(Topic.root.Get(sourcePath, false), actions);
    }

    private static void AddActionsFromObject(JSC.JSValue obj, Dictionary<string, AddActionEntry> actions, string sourcePath) {
      if(!obj.IsObject()) return;
      foreach(var kv in obj) {
        if(actions.ContainsKey(kv.Key)) continue;
        if(IsAddAction(kv.Value)) actions.Add(kv.Key, new AddActionEntry() { Action = kv.Value, SourcePath = sourcePath });
      }
      if(obj.__proto__.IsObject()) {
        AddActionsFromObject(obj.__proto__, actions, sourcePath);
      }
    }

    private static void AddActionsFromTopicChildren(Topic source, Dictionary<string, AddActionEntry> actions) {
      if(source == null) return;
      foreach(Topic child in source.children) {
        if(child == null || child.disposed || actions.ContainsKey(child.name)) continue;
        JSC.JSValue state = child.GetState();
        if(IsAddAction(state)) actions.Add(child.name, new AddActionEntry() { Action = state, SourcePath = source.path });
      }
    }

    private static bool IsAddAction(JSC.JSValue action) {
      if(!action.IsObject()) return false;
      return action["default"].Defined || action["manifest"].Defined;
    }

    // internal: also built directly by LogramPaletteBuilder for a Logram block's
    // "Add pin" submenu, which walks the type's Children/pin schema itself (filtered
    // to real ddr'd pins not already present) rather than going through
    // ResolveAddActions - that method's Core-fallback (for typeless topics) doesn't
    // make sense on a Logram canvas element, so it deliberately isn't reused here.
    internal static MenuItemDto BuildAddItem(string key, JSC.JSValue action, string sourcePath) {
      string menu = action.AsString("menu", null);
      string hint = action.AsString("hint", null);
      MenuItemDto item = new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "add:" + key,
        Text = key,
        Icon = ResolveMenuIcon(action.AsString("icon", null), key, sourcePath),
        Hint = string.IsNullOrWhiteSpace(menu) ? hint : ("menu:" + menu),
        Enabled = true,
        Willful = action.AsBool("willful", false),
      };
      return item;
    }

    private static void AddToMenuTree(List<MenuItemDto> root, string menu, MenuItemDto item) {
      if(string.IsNullOrWhiteSpace(menu)) {
        root.Add(item);
        return;
      }
      List<MenuItemDto> current = root;
      foreach(string part in menu.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) {
        MenuItemDto node = current.FirstOrDefault(z => z.Kind == MenuItemKind.Item && z.Cmd == null && z.Text == part);
        if(node == null) {
          node = new MenuItemDto() {
            Kind = MenuItemKind.Item,
            Text = part,
            Enabled = true,
            Willful = false,
            Children = new List<MenuItemDto>(),
          };
          current.Add(node);
        }
        if(node.Children == null) node.Children = new List<MenuItemDto>();
        current = node.Children;
      }
      current.Add(item);
    }

    internal static bool ResourceBusy(Topic topic, Dictionary<string, AddActionEntry> actions, string key, JSC.JSValue action) {
      string rc = action.AsString("rc", null);
      if(string.IsNullOrWhiteSpace(rc)) return false;
      List<RcUse> used = BuildUsedResources(topic, actions);
      foreach(string cur in rc.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
        string trimmed = cur.Trim();
        int pos;
        if(trimmed.Length <= 1 || !int.TryParse(trimmed.Substring(1), out pos)) continue;
        if(pos < used.Count && ((trimmed[0] == (char)RcUse.Exclusive && used[pos] != RcUse.None) || (trimmed[0] == (char)RcUse.Shared && used[pos] != RcUse.None && used[pos] != RcUse.Shared))) return true;
      }
      return false;
    }

    private static List<RcUse> BuildUsedResources(Topic topic, Dictionary<string, AddActionEntry> actions) {
      List<RcUse> used = new List<RcUse>();
      if(topic == null) return used;
      foreach(Topic child in topic.children) {
        string resourceName = child.GetField("MQTT-SN.tag").AsString(null);
        if(string.IsNullOrEmpty(resourceName)) resourceName = child.name;
        AddActionEntry entry;
        if(!actions.TryGetValue(resourceName, out entry)) continue;
        JSC.JSValue action = entry.Action;
        string rc = action.AsString("rc", null);
        if(string.IsNullOrWhiteSpace(rc)) continue;
        foreach(string cur in rc.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
          string trimmed = cur.Trim();
          int pos;
          if(trimmed.Length <= 1 || !int.TryParse(trimmed.Substring(1), out pos)) continue;
          while(pos >= used.Count) used.Add(RcUse.None);
          // A Shared claim registers only on a free slot: it must be recorded (so a later
          // Exclusive request sees the slot taken) but must never overwrite a stronger claim
          // already there. The test used to read "!= RcUse.None", which inverted both halves -
          // the first S on a free slot went unrecorded, letting an X double-allocate it, and an
          // S landing on an X quietly downgraded the Exclusive. Live on MQTT-SN device types:
          // S4M13's PWM channels share timer S40 while its counters demand X40.
          if(trimmed[0] != (char)RcUse.None && (trimmed[0] != (char)RcUse.Shared || used[pos] == RcUse.None)) used[pos] = (RcUse)trimmed[0];
        }
      }
      return used;
    }

    private static string ResolveMenuIcon(string icon, string fallbackKey, string topicPath) {
      if(!string.IsNullOrWhiteSpace(icon)) {
        if(IconResource.IsAllowedIconPath(icon)) return icon;
        if(icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) {
          string dynamicName = DynamicIconName(topicPath, fallbackKey);
          return IconResource.ResolveIconValue(icon, dynamicName) ?? ResolveFallbackIcon(fallbackKey);
        }
        string semanticIcon = IconResource.SemanticIconFileName(icon);
        if(!string.IsNullOrWhiteSpace(semanticIcon)) return "/ide_icons/" + semanticIcon;
      }
      return ResolveFallbackIcon(fallbackKey);
    }

    private static string DynamicIconName(string topicPath, string fallbackKey) {
      string prefix = string.IsNullOrWhiteSpace(topicPath) ? string.Empty : TypeHelper.StripTypeRoot(topicPath).Trim('/') + "/";
      return prefix + fallbackKey;
    }

    private static string ResolveFallbackIcon(string fallbackKey) {
      string fallbackIcon = IconResource.SemanticIconFileName(fallbackKey);
      return string.IsNullOrWhiteSpace(fallbackIcon) ? null : ("/ide_icons/" + fallbackIcon);
    }

    private enum RcUse : ushort {
      None = '0',
      Baned = 'B',
      Shared = 'S',
      Exclusive = 'X',
    }

  }
}
