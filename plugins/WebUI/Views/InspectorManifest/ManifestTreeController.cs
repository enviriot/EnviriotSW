///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Backs the Inspector document's "Manifest" tree: the topic's own manifest
  // (Topic.GetField(null)), walked recursively as an object-key tree - ported from ES's
  // InManifest.cs.
  //
  // Everything structural is shared with StateTreeController through
  // JsonTreeControllerBase. What stays here is what differs: the value source (the manifest),
  // the schema catalog ("mi", from one fixed global topic /$YS/TYPES/Ext/Manifest merged with
  // the topic's own entries, rather than State's per-topic "type" indirection), and writes
  // that go through Topic.SetField directly instead of State's merge+SetState.
  internal sealed class ManifestTreeController : JsonTreeControllerBase {
    private const string ManifestSchemaTopicPath = "/$YS/TYPES/Ext/Manifest";

    internal ManifestTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, Action<object> onRootGone = null, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, rootTopic, viewName, "inspmanifest", SubRec.SubMask.Field | SubRec.SubMask.Once, onRootGone, post, prim) {
      // Only Field is needed (not Value) - the Manifest tree never cares about state changes.
    }

    protected override ViewTargetKind TargetKind {
      get { return ViewTargetKind.Manifest; }
    }

    protected override bool IsRelevantChange(Perform.E_Art art) {
      return art == Perform.E_Art.changedField;
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
      JSC.JSValue ownOverride;
      JSC.JSValue schema = ResolveFieldSchemaAt(_rootTopic, fieldPath, out ownOverride);
      JSC.JSValue value = ResolveValueAt(fieldPath);
      items = new List<MenuItemDto>();

      bool isReadonly = (schema.AsInt("attr", 0) & 2) != 0;
      bool isValueObject = value.IsObject();
      if(!isReadonly && isValueObject) {
        List<MenuItemDto> addItems = BuildAddItems(schema, ownOverride, value, fieldPath);
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

    // Enumerates the "mi" catalog applicable at this node. At the root this is exactly the
    // global schema's own top-level catalog (verified against InManifest.MenuItems: at the
    // root, its v1.__proto__ check never fires, because only individual "mi" entries - never
    // the whole catalog object - ever get a __proto__ assigned; a topic's own top-level "mi"
    // override affects how its OWN existing manifest keys render, not what the root's Add menu
    // offers). At any nested level, the entry's generic "mi" catalog is unioned with the
    // corresponding own catalog, the topic's entries winning.
    private List<MenuItemDto> BuildAddItems(JSC.JSValue schema, JSC.JSValue ownOverride, JSC.JSValue value, string fieldPath) {
      List<MenuItemDto> addItems = new List<MenuItemDto>();
      bool isRoot = string.IsNullOrEmpty(fieldPath);

      JSC.JSValue genericCatalog = schema.IsObject() ? schema["mi"] : null;
      Dictionary<string, JSC.JSValue> catalog = new Dictionary<string, JSC.JSValue>(StringComparer.Ordinal);
      if(genericCatalog.IsObject()) {
        foreach(var kv in genericCatalog) catalog[kv.Key] = kv.Value;
      }
      if(!isRoot) {
        JSC.JSValue overrideCatalog = ownOverride.IsObject() ? ownOverride["mi"] : null;
        if(overrideCatalog.IsObject()) {
          foreach(var kv in overrideCatalog) {
            // Own wins, but merged per property rather than replacing: a partial own entry
            // must not drop the generic "default" that the filter below requires.
            JSC.JSValue generic;
            catalog[kv.Key] = catalog.TryGetValue(kv.Key, out generic) ? MergeEntry(generic, kv.Value) : kv.Value;
          }
        }
      }

      if(catalog.Count == 0) {
        // No schema catalog anywhere for this node - fall back to /$YS/TYPES/Core, same as
        // InManifest.MenuItems' identical "no mi" else-branch and StateTreeController's.
        Topic coreTypes = Topic.root.Get("/$YS/TYPES/Core", false);
        if(coreTypes != null) {
          foreach(Topic coreType in coreTypes.children.OrderBy(z => z.name, StringComparer.Ordinal)) {
            JSC.JSValue descriptor = coreType.GetState();
            if(!descriptor.IsObject() || !descriptor["default"].Defined) continue;
            addItems.Add(BuildAddMenuItem(coreType.name, descriptor, fieldPath));
          }
        }
        return addItems;
      }

      foreach(var kv in catalog.OrderBy(z => z.Key, StringComparer.Ordinal)) {
        JSC.JSValue descriptor = kv.Value;
        // IsObject, not ValueType != Object: the latter is FALSE for JSValue.Null, so a catalog
        // entry that is literally null passed this guard and the descriptor["default"] read on the
        // same line threw. Third instance of the same defect in this file family.
        if(!descriptor.IsObject() || !descriptor["default"].Defined) continue;
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
      JSC.JSValue schema = isRoot ? null : ResolveFieldSchemaAt(_rootTopic, fieldPath, out _);

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

    private static JSC.JSValue GlobalManifestSchemaRoot() {
      Topic schemaTopic = Topic.root.Get(ManifestSchemaTopicPath, false);
      return schemaTopic == null ? null : schemaTopic.GetState();
    }

    // Walks schema["mi"][segment] per path segment, starting from the global
    // /$YS/TYPES/Ext/Manifest schema, merging in the topic's own manifest "mi" entries at
    // every level. outOwnOverride returns the paired entry from the topic's own tree at the
    // same path (null if the topic declares nothing there) - callers building an Add-menu
    // catalog for this node's children union entry["mi"] with outOwnOverride["mi"].
    internal static JSC.JSValue ResolveFieldSchemaAt(Topic rootTopic, string fieldPath, out JSC.JSValue outOwnOverride) {
      outOwnOverride = null;
      if(string.IsNullOrEmpty(fieldPath)) return GlobalManifestSchemaRoot();

      JSC.JSValue genericSource = GlobalManifestSchemaRoot();
      JSC.JSValue ownSource = rootTopic.GetField(null);
      JSC.JSValue merged = null;
      JSC.JSValue ownEntry = null;

      foreach(string segment in fieldPath.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries)) {
        JSC.JSValue genericMi = genericSource.IsObject() ? genericSource["mi"] : null;
        JSC.JSValue ownMi = ownSource.IsObject() ? ownSource["mi"] : null;

        JSC.JSValue generic = genericMi.IsObject() ? genericMi[segment] : null;
        JSC.JSValue own = ownMi.IsObject() ? ownMi[segment] : null;
        if(!generic.IsObject() && !own.IsObject()) {
          outOwnOverride = null;
          return null;
        }

        merged = MergeEntry(generic, own);
        ownEntry = own.IsObject() ? own : null;
        // Descend the two source trees, not the merged view: "mi" is deliberately kept out of
        // the merge (see MergeEntry), so the merged object carries no catalog to walk into.
        genericSource = generic;
        ownSource = own;
      }
      outOwnOverride = ownEntry;
      return merged;
    }

    /// <summary>The generic entry with the topic's own entry laid over it - own wins per key.</summary>
    /// <remarks>A fresh object every time. The previous implementation assigned __proto__ onto
    /// the global schema's own live sub-objects, which had two consequences: the topic's entry
    /// only acted as a fallback (a prototype cannot outrank the object's own properties), and
    /// the assignment outlived the call, so the next topic resolved at the same path inherited
    /// the previous one's manifest. The order is the point here - a topic knows better than the
    /// global schema; generic-wins dates from when the menu could only come from the type.
    /// "mi" is excluded on purpose: BuildAddItems and ResolveAddDescriptor union the two
    /// catalogs per entry themselves, so that a partial own entry cannot hide the generic
    /// "default" and make an Add item disappear.</remarks>
    private static JSC.JSValue MergeEntry(JSC.JSValue generic, JSC.JSValue own) {
      JSC.JSObject merged = JSC.JSObject.CreateObject();
      if(generic.IsObject()) {
        foreach(var kv in generic) if(kv.Key != "mi") merged[kv.Key] = kv.Value;
      }
      if(own.IsObject()) {
        foreach(var kv in own) if(kv.Key != "mi") merged[kv.Key] = kv.Value;
      }
      // The generic catalog stays reachable as schema["mi"] for the Add-menu consumers.
      JSC.JSValue genericMi = generic.IsObject() ? generic["mi"] : null;
      if(genericMi.IsObject()) merged["mi"] = genericMi;
      return merged;
    }

    // Resolves a single add-action descriptor by key for ManifestRpcDispatcher's add:<key>
    // command - the union catalog BuildAddItems enumerates from, narrowed to one key, or the
    // /$YS/TYPES/Core fallback when there's no schema catalog at all.
    internal static JSC.JSValue ResolveAddDescriptor(Topic rootTopic, string fieldPath, string key) {
      JSC.JSValue ownOverride;
      JSC.JSValue schema = ResolveFieldSchemaAt(rootTopic, fieldPath, out ownOverride);
      bool isRoot = string.IsNullOrEmpty(fieldPath);

      JSC.JSValue genericCatalog = schema.IsObject() ? schema["mi"] : null;
      JSC.JSValue genericEntry = genericCatalog.IsObject() ? genericCatalog[key] : null;
      JSC.JSValue ownEntry = null;
      if(!isRoot) {
        JSC.JSValue overrideCatalog = ownOverride.IsObject() ? ownOverride["mi"] : null;
        if(overrideCatalog.IsObject()) ownEntry = overrideCatalog[key];
      }
      // Own first, same order BuildAddItems now uses; merged when both exist so a partial own
      // entry keeps the generic "default".
      bool genericOk = genericEntry.IsObject();
      bool ownOk = ownEntry.IsObject();
      if(genericOk || ownOk) {
        JSC.JSValue descriptor = genericOk && ownOk ? MergeEntry(genericEntry, ownEntry) : (ownOk ? ownEntry : genericEntry);
        if(descriptor["default"].Defined) return descriptor;
      }

      Topic coreTypes = Topic.root.Get("/$YS/TYPES/Core", false);
      Topic coreType = coreTypes == null ? null : coreTypes.Get(key, false);
      if(coreType == null) return null;
      JSC.JSValue coreDescriptor = coreType.GetState();
      return (coreDescriptor.IsObject() && coreDescriptor["default"].Defined) ? coreDescriptor : null;
    }
  }
}
