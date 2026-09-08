///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Backs the Inspector document's "State" tree: the topic's own live JSON `State` value,
  // walked recursively as an object-key tree rather than a topic graph - ported from ES's
  // InValue.cs. Unlike TopicTreeController (one Topic per node, each independently
  // subscribable) there is only ever one live object here (the root topic), so a single
  // Value|Field subscription for the controller's whole lifetime is enough; "children" of an
  // expanded node are just JSValue object properties, resolved on demand.
  //
  // Everything structural - expansion bookkeeping, child reconciliation, evnt.add/upd/del
  // shaping - lives in JsonTreeControllerBase, shared with ManifestTreeController; schema
  // resolution lives in SchemaOverlay, shared with it too. Both trees lay their sources over one
  // another by the same rule, and this one differs only in which sources and which catalog key
  // (SchemaOverlay.ForState: the type topic's state, then the topic's own manifest, catalog
  // "Fields"). What stays here is the value source (state, not manifest) and how a row or a menu
  // is built once the schema is resolved.
  internal sealed class StateTreeController : JsonTreeControllerBase {

    internal StateTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, Action<object> onRootGone = null, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, rootTopic, viewName, "inspstate", SubRec.SubMask.Value | SubRec.SubMask.Field | SubRec.SubMask.Once, onRootGone, post, prim) {
    }

    protected override ViewTargetKind TargetKind {
      get { return ViewTargetKind.State; }
    }

    protected override bool IsRelevantChange(EventKind art) {
      return art == EventKind.StateChanged || art == EventKind.FieldChanged;
    }

    protected override JSC.JSValue ResolveValueAt(string fieldPath) {
      return string.IsNullOrEmpty(fieldPath) ? _rootTopic.GetState() : _rootTopic.GetState().Field(fieldPath);
    }

    internal ViewOpResult Commit(string vid, JSC.JSValue value) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      ViewOpResult blocked = CheckWritable(_rootTopic, fieldPath);
      if(blocked != null) return blocked;
      if(string.IsNullOrEmpty(fieldPath)) {
        _rootTopic.SetState(value, Prim);
      } else {
        _rootTopic.SetState(JsLib.SetField(_rootTopic.GetState(), fieldPath, value), Prim);
      }
      return ViewOpResult.Success();
    }

    internal ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      return StateRpcDispatcher.Execute(_rootTopic, VidHelper.GetFieldPath(vid), cmd, args, Prim);
    }

    internal ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      SchemaOverlay schema = SchemaOverlay.ForState(_rootTopic).At(fieldPath);
      JSC.JSValue manifest = schema == null ? null : schema.Descriptor;
      JSC.JSValue value = ResolveValueAt(fieldPath);
      items = new List<MenuItemDto>();

      bool isReadonly = (manifest.AsInt("attr", 0) & 2) != 0;
      bool isValueObject = value.IsObject();
      if(!isReadonly && isValueObject) {
        List<MenuItemDto> addItems = BuildAddItems(schema, value, fieldPath);
        if(addItems.Count > 0) {
          items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Add", Enabled = true, Willful = false, Children = addItems });
        }
      }

      bool isRequired = (manifest.AsInt("attr", 0) & 1) != 0;
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

    private List<MenuItemDto> BuildAddItems(SchemaOverlay schema, JSC.JSValue value, string fieldPath) {
      List<MenuItemDto> addItems = new List<MenuItemDto>();
      if(schema != null && !schema.CatalogIsEmpty) {
        foreach(var kv in schema.CatalogEntries) {
          if(!kv.Value["default"].Defined) continue;
          if(value[kv.Key].Defined) continue;
          addItems.Add(BuildAddMenuItem(kv.Key, kv.Value, fieldPath));
        }
        return addItems;
      }

      // No Fields catalog anywhere for this node - fall back to the Core types catalog
      // (Boolean/Double/Object/String/...), same fallback ES's InValue.MenuItems uses
      // (_data.Connection.CoreTypes.children) when it hits its own "no Fields" else-branch.
      // These are always "willful" (user names the new field) per their own manifest, so - unlike
      // Fields entries - there's no fixed key to filter as "already present".
      foreach(var kv in SchemaOverlay.CoreCatalogEntries()) {
        if(!kv.Value.IsObject() || !kv.Value["default"].Defined) continue;
        addItems.Add(BuildAddMenuItem(kv.Key, kv.Value, fieldPath));
      }
      return addItems;
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
      JSC.JSValue manifest = ResolveFieldManifestAt(_rootTopic, fieldPath);
      JSC.JSValue value = ResolveValueAt(fieldPath);

      // The root row IS the topic itself (top-level state/manifest), so it reuses the same
      // Topic-shaped, type-indirection-aware resolvers Workspace/Children already use
      // (RowProjector.BuildTopicRow) instead of the field-shaped ones below, which
      // only exist because nested field descriptors aren't Topics.
      string editor = isRoot ? EditorHelper.Resolve(_rootTopic) : JsonTreeRowHelpers.ResolveEditorName(manifest, value);
      string resolvedEditor = JsonTreeRowHelpers.ResolvedEditorOrDefault(editor);

      string optionsKey;
      JSC.JSValue options;
      if(isRoot) {
        optionsKey = EnumHelper.Resolve(_rootTopic, editor);
        options = EnumHelper.ResolveOptions(_rootTopic, editor);
      } else if(resolvedEditor == "Enum") {
        optionsKey = manifest.AsString("enum", null);
        options = EnumHelper.ResolveOptionsForSource(optionsKey);
      } else {
        optionsKey = null;
        options = null;
      }

      return new ViewRowDto() {
        Vid = vid,
        Level = JsonTreeRowHelpers.LevelOf(fieldPath),
        Expander = JsonTreeRowHelpers.HasFields(value) ? (IsExpanded(vid) ? 2 : 1) : 0,
        Icon = isRoot ? IconResource.Resolve(_rootTopic, editor) : JsonTreeRowHelpers.ResolveRowIcon(manifest, value, resolvedEditor, _rootTopic.path, fieldPath),
        Name = isRoot ? "State" : JsonTreeRowHelpers.LastSegment(fieldPath),
        Editor = resolvedEditor,
        Value = resolvedEditor == "Default" ? TopicDisplayValueFormatter.Format(value) : RowProjector.ToWebStateValue(value),
        Readonly = isRoot ? _rootTopic.CheckAttribute(Topic.Attribute.Readonly) : ((manifest.AsInt("attr", 0) & 2) != 0),
        OptionsKey = optionsKey,
        Options = options,
      };
    }

    /// <summary>Null when the client may write here, an error result when it may not.</summary>
    /// <remarks>Enforced at the view/RPC layer rather than inside Topic.SetState on purpose:
    /// plugins seed their own readonly $YS topics through the very same Topic API and must keep
    /// working. Only the client-driven paths have to honour the flag - which the UI already
    /// greys the row out for, so this refuses nothing a normal session would attempt.
    /// Catalog's remove path is deliberately exempt from attribute checks: every topic it owns
    /// is Required, so honouring that flag there would make it unable to uninstall anything.</remarks>
    internal static ViewOpResult CheckWritable(Topic rootTopic, string fieldPath) {
      // Topic-level half in Helpers/WritePermission - shared with the topic trees, which have no
      // field path and therefore never reach the manifest walk below.
      string blocked = WritePermission.CheckTopic(rootTopic);
      if(blocked != null) return ViewOpResult.Error(WritePermission.ReadonlyCode, blocked);
      if(rootTopic == null) return null;
      if(!string.IsNullOrEmpty(fieldPath)) {
        JSC.JSValue manifest = ResolveFieldManifestAt(rootTopic, fieldPath);
        if((manifest.AsInt("attr", 0) & 2) != 0) {
          return ViewOpResult.Error("target_readonly", "Field is readonly: " + fieldPath);
        }
      }
      return null;
    }

    // The descriptor SchemaOverlay resolves for one node, or null when no source declares that
    // path. Kept as a named entry point so StateRpcDispatcher and CheckWritable read the schema
    // the same way the rows do, without each building an overlay of its own.
    internal static JSC.JSValue ResolveFieldManifestAt(Topic rootTopic, string fieldPath) {
      SchemaOverlay schema = SchemaOverlay.ForState(rootTopic).At(fieldPath);
      return schema == null ? null : schema.Descriptor;
    }

    // Resolves a single add-action descriptor by key for StateRpcDispatcher's add:<key>
    // command, out of the same catalog BuildAddItems enumerates - or, when there is none at all,
    // the /$YS/TYPES/Core child of that name.
    internal static JSC.JSValue ResolveAddDescriptor(Topic rootTopic, string fieldPath, string key) {
      SchemaOverlay schema = SchemaOverlay.ForState(rootTopic).At(fieldPath);
      if(schema != null && !schema.CatalogIsEmpty) {
        JSC.JSValue descriptor = schema.CatalogEntry(key);
        return (descriptor != null && descriptor["default"].Defined) ? descriptor : null;
      }

      JSC.JSValue coreDescriptor = SchemaOverlay.CoreCatalogEntry(key);
      return (coreDescriptor.IsObject() && coreDescriptor["default"].Defined) ? coreDescriptor : null;
    }
  }
}
