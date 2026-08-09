///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal static class WorkspaceMenuBuilder {
    internal static List<MenuItemDto> Build(Topic topic) {
      List<MenuItemDto> items = new List<MenuItemDto>();
      items.Add(BuildToolbar(topic));
      AddAddMenuItems(topic, items);
      AddActionMenuItems(topic, items);
      AddRootCatalogMenuItem(topic, items);
      AddRootImportMenuItem(topic, items);
      AddExportMenuItem(topic, items);
      return items;
    }

    private static MenuItemDto BuildToolbar(Topic topic) {
      bool isNormalTopic = topic != null && topic.parent != null;
      bool canModify = isNormalTopic && !topic.CheckAttribute(Topic.Attribute.Required);
      return new MenuItemDto() {
        Kind = MenuItemKind.Toolbar,
        Enabled = true,
        Willful = false,
        Children = new List<MenuItemDto>() {
          MenuItem("open", "Open", "/ide_icons/cm_open.png", "Open topic", isNormalTopic),
          MenuItem("open-tab", "Open in new tab", "/ide_icons/cm_doc.png", "Open topic in new tab", isNormalTopic),
          MenuItem("copy-path", "Copy path", "/ide_icons/cm_path.png", "Copy topic path", isNormalTopic),
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
        Icon = "/ide_icons/cm_doc.png",
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
        Icon = "/ide_icons/cm_open.png",
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
        Icon = "/ide_icons/cm_doc.png",
        Hint = "Export topic to .xst file",
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
        if(IsObject(typeState)) AddActionItemsFromValue(typeState["Action"], items, topic.path);
      }

      return items.Values.OrderBy(z => z.Text ?? z.Cmd, StringComparer.Ordinal).ToList();
    }

    private static void AddActionItemsFromValue(JSC.JSValue actions, Dictionary<string, MenuItemDto> items, string topicPath) {
      if(!IsObject(actions)) return;
      foreach(var kv in actions) {
        JSC.JSValue action = kv.Value;
        if(!IsObject(action)) continue;

        string name = JsLib.OfString(action["name"], null);
        if(string.IsNullOrWhiteSpace(name)) name = kv.Key;
        if(string.IsNullOrWhiteSpace(name) || items.ContainsKey(name)) continue;

        string text = JsLib.OfString(action["text"], null);
        if(string.IsNullOrWhiteSpace(text)) text = name;
        items.Add(name, new MenuItemDto() {
          Kind = MenuItemKind.Item,
          Cmd = "action:" + name,
          Text = text,
          Icon = ResolveMenuIcon(JsLib.OfString(action["icon"], null), name, topicPath),
          Hint = JsLib.OfString(action["hint"], null),
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
        if(IsObject(typeState)) AddActionDescriptorsFromValue(typeState["Action"], descriptors);
      }

      return descriptors.TryGetValue(name, out descriptor);
    }

    private static void AddActionDescriptorsFromValue(JSC.JSValue actions, Dictionary<string, JSC.JSValue> descriptors) {
      if(!IsObject(actions)) return;
      foreach(var kv in actions) {
        JSC.JSValue action = kv.Value;
        if(!IsObject(action)) continue;
        string name = JsLib.OfString(action["name"], null);
        if(string.IsNullOrWhiteSpace(name)) name = kv.Key;
        if(string.IsNullOrWhiteSpace(name) || descriptors.ContainsKey(name)) continue;
        descriptors.Add(name, action);
      }
    }

    private static void AddAddMenuItems(Topic topic, List<MenuItemDto> items) {
      if(topic == null) return;

      Dictionary<string, JSC.JSValue> actions = ResolveAddActions(topic);
      if(actions == null || actions.Count == 0) return;

      List<MenuItemDto> addItems = new List<MenuItemDto>();
      foreach(KeyValuePair<string, JSC.JSValue> action in actions.OrderBy(z => z.Key)) {
        if(ResourceBusy(topic, actions, action.Key, action.Value)) continue;
        addItems.Add(BuildAddItem(action.Key, action.Value, topic.path));
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
            item.Hint = JsLib.OfString(actions[item.Text]["hint"], null);
          }
          AddToMenuTree(addRoot.Children, menu, item);
        }
        items.Add(addRoot);
      }
      items.Add(new MenuItemDto() { Kind = MenuItemKind.Separator, Enabled = true });
    }

    internal static Dictionary<string, JSC.JSValue> ResolveAddActions(Topic topic) {
      Dictionary<string, JSC.JSValue> actions = new Dictionary<string, JSC.JSValue>(StringComparer.Ordinal);
      JSC.JSValue ownChildren = topic == null ? null : topic.GetField("Children");
      JSC.JSValue typeChildren = ResolveTypeChildren(topic);

      if(IsObject(ownChildren)) {
        AddActionsFromObject(ownChildren, actions);
        if(IsObject(typeChildren)) AddActionsFromObject(typeChildren, actions);
      } else if(ownChildren != null && ownChildren.ValueType == JSC.JSValueType.String) {
        AddActionsFromChildrenPath(ownChildren, actions);
      } else if(IsObject(typeChildren)) {
        AddActionsFromObject(typeChildren, actions);
      } else if(typeChildren != null && typeChildren.ValueType == JSC.JSValueType.String) {
        AddActionsFromChildrenPath(typeChildren, actions);
      }

      if(actions.Count == 0) AddActionsFromTopicChildren(Topic.root.Get("/$YS/TYPES/Core", false), actions);
      return actions;
    }

    private static JSC.JSValue ResolveTypeChildren(Topic topic) {
      Topic typeTopic = ResolveTopicType(topic);
      if(typeTopic == null) return JSC.JSValue.NotExists;
      JSC.JSValue state = typeTopic.GetState();
      if(!IsObject(state)) return JSC.JSValue.NotExists;
      return state["Children"];
    }

    private static Topic ResolveTopicType(Topic topic) {
      if(topic == null) return null;
      string typePath = JsLib.OfString(topic.GetField("type"), null);
      return TypeHelper.ResolveTypeTopic(typePath);
    }

    internal static bool IsObject(JSC.JSValue value) {
      return value != null && value.ValueType == JSC.JSValueType.Object && value.Value != null;
    }

    private static void AddActionsFromChildrenPath(JSC.JSValue children, Dictionary<string, JSC.JSValue> actions) {
      string sourcePath = JsLib.OfString(children, null);
      if(string.IsNullOrWhiteSpace(sourcePath)) return;
      AddActionsFromTopicChildren(Topic.root.Get(sourcePath, false), actions);
    }

    private static void AddActionsFromObject(JSC.JSValue obj, Dictionary<string, JSC.JSValue> actions) {
      if(obj == null || obj.ValueType != JSC.JSValueType.Object || obj.Value == null) return;
      foreach(var kv in obj) {
        if(actions.ContainsKey(kv.Key)) continue;
        if(IsAddAction(kv.Value)) actions.Add(kv.Key, kv.Value);
      }
      if(obj.__proto__ != null && obj.__proto__.Defined && !obj.__proto__.IsNull && obj.__proto__.ValueType == JSC.JSValueType.Object) {
        AddActionsFromObject(obj.__proto__, actions);
      }
    }

    private static void AddActionsFromTopicChildren(Topic source, Dictionary<string, JSC.JSValue> actions) {
      if(source == null) return;
      foreach(Topic child in source.children) {
        if(child == null || child.disposed || actions.ContainsKey(child.name)) continue;
        JSC.JSValue state = child.GetState();
        if(IsAddAction(state)) actions.Add(child.name, state);
      }
    }

    private static bool IsAddAction(JSC.JSValue action) {
      if(action == null || action.ValueType != JSC.JSValueType.Object || action.Value == null) return false;
      return action["default"].Defined || action["manifest"].Defined;
    }

    private static MenuItemDto BuildAddItem(string key, JSC.JSValue action, string topicPath) {
      string menu = JsLib.OfString(action["menu"], null);
      string hint = JsLib.OfString(action["hint"], null);
      MenuItemDto item = new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "add:" + key,
        Text = key,
        Icon = ResolveMenuIcon(JsLib.OfString(action["icon"], null), key, topicPath),
        Hint = string.IsNullOrWhiteSpace(menu) ? hint : ("menu:" + menu),
        Enabled = true,
        Willful = JsLib.ofBool(action["willful"], false),
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

    internal static bool ResourceBusy(Topic topic, Dictionary<string, JSC.JSValue> actions, string key, JSC.JSValue action) {
      string rc = JsLib.OfString(action["rc"], null);
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

    private static List<RcUse> BuildUsedResources(Topic topic, Dictionary<string, JSC.JSValue> actions) {
      List<RcUse> used = new List<RcUse>();
      if(topic == null) return used;
      foreach(Topic child in topic.children) {
        string resourceName = JsLib.OfString(JsLib.GetField(child.GetField(null), "MQTT-SN.tag"), null);
        if(string.IsNullOrEmpty(resourceName)) resourceName = child.name;
        JSC.JSValue action;
        if(!actions.TryGetValue(resourceName, out action)) continue;
        string rc = JsLib.OfString(action["rc"], null);
        if(string.IsNullOrWhiteSpace(rc)) continue;
        foreach(string cur in rc.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
          string trimmed = cur.Trim();
          int pos;
          if(trimmed.Length <= 1 || !int.TryParse(trimmed.Substring(1), out pos)) continue;
          while(pos >= used.Count) used.Add(RcUse.None);
          if(trimmed[0] != (char)RcUse.None && (trimmed[0] != (char)RcUse.Shared || used[pos] != RcUse.None)) used[pos] = (RcUse)trimmed[0];
        }
      }
      return used;
    }

    private static string ResolveMenuIcon(string icon, string fallbackKey, string topicPath) {
      if(!string.IsNullOrWhiteSpace(icon)) {
        if(icon.StartsWith("/", StringComparison.Ordinal)) return icon;
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
