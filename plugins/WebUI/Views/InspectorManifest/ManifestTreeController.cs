///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Backs the Inspector document's "Manifest" tree: the topic's own manifest
  // (Topic.GetField(null)), walked recursively as an object-key tree - ported from ES's
  // InManifest.cs.
  //
  // Everything structural is shared with StateTreeController through JsonTreeControllerBase, and
  // schema resolution through SchemaOverlay. Both trees lay their sources over one another by the
  // same rule, and this one differs only in which sources and which catalog key
  // (SchemaOverlay.ForManifest: the global /$YS/TYPES/Ext/Manifest schema, then the type topic's
  // state, then the topic's own manifest, catalog "mi"). What stays here is the value source (the
  // manifest), the "path" indirection of a catalog entry, and writes that go through
  // Topic.SetField directly instead of State's merge+SetState.
  internal sealed class ManifestTreeController : JsonTreeControllerBase {

    internal ManifestTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, Action<object> onRootGone = null, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, rootTopic, viewName, "inspmanifest", SubRec.SubMask.Field | SubRec.SubMask.Once, onRootGone, post, prim) {
      // Only Field is needed (not Value) - the Manifest tree never cares about state changes.
    }

    protected override ViewTargetKind TargetKind {
      get { return ViewTargetKind.Manifest; }
    }

    protected override bool IsRelevantChange(EventKind art) {
      return art == EventKind.FieldChanged;
    }

    protected override JSC.JSValue ResolveValueAt(string fieldPath) {
      // Topic.GetField already walks a dotted path and returns the whole manifest for an empty one.
      return _rootTopic.GetField(fieldPath);
    }

    internal ViewOpResult Commit(string vid, JSC.JSValue value) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      if(string.IsNullOrEmpty(fieldPath)) {
        // The root row always resolves to the Default (read-only display) editor - see
        // BuildRow - so this is unreachable via the UI, matching WPF where the root Manifest
        // node is always IsGroupHeader/object-typed and never itself committed.
        return ViewOpResult.Error("view_commit_not_supported", "Manifest root cannot be committed directly");
      }
      _rootTopic.SetField(fieldPath, value, Prim);
      return ViewOpResult.Success();
    }

    internal ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      return ManifestRpcDispatcher.Execute(_rootTopic, VidHelper.GetFieldPath(vid), cmd, args, Prim);
    }

    internal ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      SchemaOverlay overlay = SchemaOverlay.ForManifest(_rootTopic).At(fieldPath);
      JSC.JSValue schema = overlay == null ? null : overlay.Descriptor;
      JSC.JSValue value = ResolveValueAt(fieldPath);
      items = new List<MenuItemDto>();

      bool isReadonly = (schema.AsInt("attr", 0) & 2) != 0;
      bool isValueObject = value.IsObject();
      if(!isReadonly && isValueObject) {
        List<MenuItemDto> addItems = BuildAddItems(overlay, value, fieldPath);
        if(addItems.Count > 0) {
          items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Add", Enabled = true, Willful = false, Children = addItems });
        }
      }

      bool isRequired = (schema.AsInt("attr", 0) & 1) != 0;
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "delete",
        Text = "Delete",
        Icon = "/ide/icons/cm_delete.png",
        Enabled = !string.IsNullOrEmpty(fieldPath) && !isRequired,
        Willful = false,
      });
      return ViewOpResult.Success();
    }

    // Enumerates the "mi" catalog applicable at this node - the union SchemaOverlay resolves out
    // of the global schema, the type topic and the topic's own manifest. The root used to be an
    // exception, offering the global catalog alone: ES chains a topic's own "mi" onto the generic
    // entries for the root's CHILD rows (InManifest.UpdateType's IsGroupHeader ? _value :
    // _manifest) but not onto the root's own Add menu, and the port copied that asymmetry. One
    // rule now applies at every level, the root included.
    private List<MenuItemDto> BuildAddItems(SchemaOverlay overlay, JSC.JSValue value, string fieldPath) {
      List<MenuItemDto> addItems = new List<MenuItemDto>();

      if(overlay == null || overlay.CatalogIsEmpty) {
        // No schema catalog anywhere for this node - fall back to /$YS/TYPES/Core, same as
        // InManifest.MenuItems' identical "no mi" else-branch and StateTreeController's.
        foreach(var kv in SchemaOverlay.CoreCatalogEntries()) {
          if(!kv.Value.IsObject() || !kv.Value["default"].Defined) continue;
          addItems.Add(BuildAddMenuItem(kv.Key, kv.Value, fieldPath));
        }
        return addItems;
      }

      foreach(var kv in overlay.CatalogEntries) {
        JSC.JSValue descriptor = kv.Value;
        if(!descriptor["default"].Defined) continue;
        // The already-present test has to follow "path" when the entry writes somewhere other
        // than its own key, or an entry like DashboardRO -> dashboard.netRO would keep being
        // offered after it had been added, and adding it again would answer add_target_exists.
        string relative = AddDescriptorPath(descriptor) ?? kv.Key;
        if(value.Field(relative).Defined) continue;
        addItems.Add(BuildAddMenuItem(kv.Key, descriptor, fieldPath));
      }
      return addItems;
    }

    /// <summary>The field a catalog entry writes, relative to the node, when it is not the key.</summary>
    /// <remarks>Lets a menu entry be named independently of the field it adds - the catalog's
    /// key is both the label (BuildAddMenuItem) and, by default, the field name
    /// (ManifestRpcDispatcher.ExecuteAdd), so without this a field inside a namespaced group
    /// can only be offered under its bare leaf name, one level down from its container.
    /// Dotted, and validated here so a malformed entry falls back to the key rather than
    /// writing somewhere unintended.</remarks>
    internal static string AddDescriptorPath(JSC.JSValue descriptor) {
      string path = descriptor.AsString("path", null);
      if(string.IsNullOrWhiteSpace(path)) return null;
      foreach(string part in path.Split('.')) {
        if(string.IsNullOrEmpty(part) || part.IndexOf('#') >= 0) return null;
      }
      return path;
    }

    private MenuItemDto BuildAddMenuItem(string key, JSC.JSValue descriptor, string fieldPath) {
      string descEditor = JsonTreeRowHelpers.ResolvedEditorOrDefault(JsonTreeRowHelpers.ResolveEditorName(descriptor, descriptor["default"]));
      return new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "add:" + key,
        Text = key,
        Icon = JsonTreeRowHelpers.ResolveMenuIcon(descriptor.AsString("icon", null), descEditor == "Default" ? Topic.JsValueTypeName(descriptor["default"]) : descEditor, _rootTopic.path, fieldPath),
        Hint = descriptor.AsString("hint", null),
        Enabled = true,
        Willful = descriptor.AsBool("willful", false),
      };
    }

    protected override ViewRowDto BuildRow(string fieldPath) {
      bool isRoot = string.IsNullOrEmpty(fieldPath);
      string vid = isRoot ? RootVid : (RootVid + "#" + fieldPath);
      JSC.JSValue value = ResolveValueAt(fieldPath);

      // Resolved once: the walk rebuilds a merged object per path segment, so calling it again
      // for the Enum branch below would repeat the whole thing.
      JSC.JSValue schema = isRoot ? null : ResolveFieldSchemaAt(_rootTopic, fieldPath);

      string resolvedEditor;
      string icon;
      bool readonlyFlag;
      if(isRoot) {
        // The root represents the manifest object itself. Per InManifest's WPF ctor
        // (UpdateType(_tManifest.State, _data.Manifest) - the schema passed for the root node
        // is the whole {"mi":{...}} blob, which has no top-level editor/icon of its own), this
        // always resolves to Default/generic-object - no per-topic customization point at the
        // root, unlike State's root (which IS the topic).
        resolvedEditor = "Default";
        icon = "/ide/icons/ty_obj.png";
        readonlyFlag = false;
      } else {
        string editor = JsonTreeRowHelpers.ResolveEditorName(schema, value);
        resolvedEditor = JsonTreeRowHelpers.ResolvedEditorOrDefault(editor);
        icon = JsonTreeRowHelpers.ResolveRowIcon(schema, value, resolvedEditor, _rootTopic.path, fieldPath);
        readonlyFlag = (schema.AsInt("attr", 0) & 2) != 0;
      }

      string optionsKey = null;
      JSC.JSValue options = null;
      if(!isRoot && resolvedEditor == "Enum") {
        optionsKey = schema.AsString("enum", null);
        options = EnumHelper.ResolveOptionsForSource(optionsKey);
      }

      return new ViewRowDto() {
        Vid = vid,
        Level = JsonTreeRowHelpers.LevelOf(fieldPath),
        Expander = JsonTreeRowHelpers.HasFields(value) ? (IsExpanded(vid) ? 2 : 1) : 0,
        Icon = icon,
        Name = isRoot ? "Manifest" : JsonTreeRowHelpers.LastSegment(fieldPath),
        Editor = resolvedEditor,
        Value = resolvedEditor == "Default" ? TopicDisplayValueFormatter.Format(value) : RowProjector.ToWebStateValue(value),
        Readonly = readonlyFlag,
        OptionsKey = optionsKey,
        Options = options,
      };
    }

    // The descriptor SchemaOverlay resolves for one node, or null when no source declares that
    // path. Kept as a named entry point so ManifestRpcDispatcher reads the schema the same way
    // the rows do, without building an overlay of its own.
    internal static JSC.JSValue ResolveFieldSchemaAt(Topic rootTopic, string fieldPath) {
      SchemaOverlay overlay = SchemaOverlay.ForManifest(rootTopic).At(fieldPath);
      return overlay == null ? null : overlay.Descriptor;
    }

    // Resolves a single add-action descriptor by key for ManifestRpcDispatcher's add:<key>
    // command - the union catalog BuildAddItems enumerates from, narrowed to one key, or the
    // /$YS/TYPES/Core fallback when there's no schema catalog at all.
    internal static JSC.JSValue ResolveAddDescriptor(Topic rootTopic, string fieldPath, string key) {
      SchemaOverlay overlay = SchemaOverlay.ForManifest(rootTopic).At(fieldPath);
      if(overlay != null && !overlay.CatalogIsEmpty) {
        JSC.JSValue descriptor = overlay.CatalogEntry(key);
        return (descriptor != null && descriptor["default"].Defined) ? descriptor : null;
      }

      JSC.JSValue coreDescriptor = SchemaOverlay.CoreCatalogEntry(key);
      return (coreDescriptor.IsObject() && coreDescriptor["default"].Defined) ? coreDescriptor : null;
    }
  }
}
