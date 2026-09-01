///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
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
  // shaping - lives in JsonTreeControllerBase, shared with ManifestTreeController. What stays
  // here is what actually differs: the value source (state, not manifest), the "Fields"
  // schema catalog with its "type" indirection, and how a row/menu is built from it.
  internal sealed class StateTreeController : JsonTreeControllerBase {

    internal StateTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName, Action<object> onRootGone = null, Action<string, Action> post = null, Func<Topic> prim = null)
      : base(send, targets, rootTopic, viewName, "inspstate", SubRec.SubMask.Value | SubRec.SubMask.Field | SubRec.SubMask.Once, onRootGone, post, prim) {
    }

    protected override ViewTargetKind TargetKind {
      get { return ViewTargetKind.State; }
    }

    protected override bool IsRelevantChange(Perform.E_Art art) {
      return art == Perform.E_Art.changedState || art == Perform.E_Art.changedField;
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
      JSC.JSValue manifest = ResolveFieldManifestAt(_rootTopic, fieldPath);
      JSC.JSValue value = ResolveValueAt(fieldPath);
      items = new List<MenuItemDto>();

      bool isReadonly = (manifest.AsInt("attr", 0) & 2) != 0;
      bool isValueObject = value.IsObject();
      if(!isReadonly && isValueObject) {
        List<MenuItemDto> addItems = BuildAddItems(manifest, value, fieldPath);
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

    private List<MenuItemDto> BuildAddItems(JSC.JSValue manifest, JSC.JSValue value, string fieldPath) {
      List<MenuItemDto> addItems = new List<MenuItemDto>();
      JSC.JSValue fields = ResolveFields(manifest);
      if(fields.IsObject()) {
        foreach(var kv in fields.OrderBy(z => z.Key, StringComparer.Ordinal)) {
          JSC.JSValue descriptor = kv.Value;
          // IsObject, not ValueType != Object: the latter is FALSE for JSValue.Null, so a catalog
        // entry that is literally null passed this guard and the descriptor["default"] read on the
        // same line threw. Third instance of the same defect in this file family.
        if(!descriptor.IsObject() || !descriptor["default"].Defined) continue;
          if(value[kv.Key].Defined) continue;
          addItems.Add(BuildAddMenuItem(kv.Key, descriptor, fieldPath));
        }
        return addItems;
      }

      // No declared Fields schema on this node (own manifest or its type) - fall back to the
      // Core types catalog (Boolean/Double/Object/String/...), same fallback ES's
      // InValue.MenuItems uses (_data.Connection.CoreTypes.children) when it hits its own
      // "no Fields" else-branch, and the same /$YS/TYPES/Core fallback
      // MenuBuilder.ResolveAddActions already uses for topic children. These are
      // always "willful" (user names the new field) per their own manifest, so - unlike Fields
      // entries - there's no fixed key to filter as "already present".
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

    // Walks manifest["Fields"][segment] per path segment, starting from the root topic's own
    // manifest - mirrors how InValue.UpdateData/UpdateType passes _manifest["Fields"][key] down
    // as each child InValue's own manifest. Shared (internal static) so StateRpcDispatcher can
    // resolve the same Fields catalog for add:/delete without duplicating the walk.
    internal static JSC.JSValue ResolveFieldManifestAt(Topic rootTopic, string fieldPath) {
      JSC.JSValue manifest = rootTopic.GetField(null);
      if(string.IsNullOrEmpty(fieldPath)) return manifest;
      foreach(string segment in fieldPath.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries)) {
        JSC.JSValue fields = ResolveFields(manifest);
        if(!fields.IsObject()) return null;
        manifest = fields[segment];
        if(manifest == null || !manifest.Defined) return null;
      }
      return manifest;
    }

    // manifest["Fields"] if the manifest declares it directly, else - when the manifest instead
    // declares "type" - the type topic's own STATE "Fields" (e.g.
    // /$YS/TYPES/LoBlock/Binary/AND's manifest is only {"attr":...,"type":"Ext/LBDescr"}; its
    // "src" field's editor:"JS" lives in Ext/LBDescr's state Fields.src, not on AND itself).
    // ES's InValue.cs never reads "type" for this - because by the time WPF's DTopic hands
    // InValue a manifest, DTopic.ProtoDeep has already spliced the type topic's state in as
    // that manifest's live JS __proto__, so a plain manifest["Fields"] property read
    // transparently inherits it. The server has no such live prototype chain, so this
    // replicates the (single-level - type is resolved once per topic manifest, not re-resolved
    // for each nested Fields entry, matching ProtoDeep's one-time chaining) fallback
    // explicitly. Applied uniformly at every path segment, though in practice only the root
    // topic manifest (fieldPath == "") is ever expected to declare "type".
    private static JSC.JSValue ResolveFields(JSC.JSValue manifest) {
      // Reads go through JsLib rather than a hand-written `ValueType == Object` test: that test
      // is TRUE for JSValue.Null (its Value is what is null), so it let a null manifest through
      // to an indexer that throws. GetField checks both halves at every hop.
      JSC.JSValue own = manifest.Field("Fields");
      if(own.IsObject()) return own;

      string typePath = manifest.AsString("type", null);
      if(string.IsNullOrWhiteSpace(typePath)) return own;

      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      JSC.JSValue typeState = typeTopic == null ? null : typeTopic.GetState();
      JSC.JSValue inherited = typeState.Field("Fields");
      return inherited.Defined ? inherited : own;
    }

    // Resolves a single add-action descriptor by key for StateRpcDispatcher's add:<key>
    // command, via the exact same two-tier lookup BuildAddItems uses to populate the menu: the
    // node's own (or type-inherited) Fields[key] entry, or - when there's no Fields catalog at
    // all - the /$YS/TYPES/Core child of that name.
    internal static JSC.JSValue ResolveAddDescriptor(Topic rootTopic, string fieldPath, string key) {
      JSC.JSValue manifest = ResolveFieldManifestAt(rootTopic, fieldPath);
      JSC.JSValue fields = ResolveFields(manifest);
      if(fields.IsObject()) {
        JSC.JSValue descriptor = fields[key];
        // JsLib.IsObject, not a hand-rolled ValueType test: a Fields catalog entry that is
        // literally null ({"Fields":{"x":null}}) yields JSValue.Null here, whose ValueType IS
        // Object - so the obvious form passed and the indexer below threw a TypeError. The
        // correct form was already three lines above, for `fields`.
        return (descriptor.IsObject() && descriptor["default"].Defined) ? descriptor : null;
      }

      Topic coreTypes = Topic.root.Get("/$YS/TYPES/Core", false);
      Topic coreType = coreTypes == null ? null : coreTypes.Get(key, false);
      if(coreType == null) return null;
      JSC.JSValue coreDescriptor = coreType.GetState();
      return (coreDescriptor.IsObject() && coreDescriptor["default"].Defined) ? coreDescriptor : null;
    }
  }
}
