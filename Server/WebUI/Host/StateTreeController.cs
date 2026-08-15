///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  // Backs the Inspector document's "State" tree: the topic's own live JSON `State`
  // value, walked recursively as an object-key tree rather than a topic graph -
  // ported from ES's InValue.cs. Unlike TopicTreeController (one Topic per node,
  // each independently subscribable) there is only ever one live object here (the
  // root topic), so a single Value|Field subscription for the controller's whole
  // lifetime is enough; "children" of an expanded node are just JSValue object
  // properties, resolved on demand from the root topic's current state/manifest.
  // Pure JSValue/string helpers shared with ManifestTreeController live in
  // JsonTreeRowHelpers.cs.
  internal sealed class StateTreeController : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly Topic _rootTopic;
    private readonly string _viewName;
    private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _lastChildren = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    private SubRec _sub;

    internal StateTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName) {
      _send = send;
      _targets = targets;
      _rootTopic = rootTopic ?? Topic.root;
      _viewName = string.IsNullOrEmpty(viewName) ? "inspstate" : viewName;
      // Value|Field alone only observes a topic's CHILDREN (see TopicTreeController) -
      // Once is what makes a subscription eligible to observe the subscribed topic's
      // own changedState/changedField (confirmed by precedent: every existing plugin
      // that watches one specific topic's own state/field always includes Once, e.g.
      // MQTTPl.cs's verboserSR, PersistentStorage*'s KEEP_FIELD watchers).
      _sub = _rootTopic.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.Field | SubRec.SubMask.Once, OnRootChanged);
    }

    internal string RootVid {
      get { return _viewName + "#" + _rootTopic.path; }
    }

    internal void SendRoot() {
      SendAdd(BuildRow(string.Empty));
    }

    internal ViewOpResult Expand(string vid, bool expand) {
      if(expand) ExpandField(vid);
      else CollapseField(vid);
      return ViewOpResult.Success();
    }

    internal ViewOpResult Commit(string vid, JSC.JSValue value) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      if(string.IsNullOrEmpty(fieldPath)) {
        _rootTopic.SetState(value);
      } else {
        _rootTopic.SetState(JsLib.SetField(_rootTopic.GetState(), fieldPath, value));
      }
      return ViewOpResult.Success();
    }

    internal ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      JSC.JSValue manifest = ResolveFieldManifestAt(_rootTopic, fieldPath);
      JSC.JSValue value = ResolveValueAt(fieldPath);
      items = new List<MenuItemDto>();

      bool isReadonly = manifest != null && (JsLib.OfInt(manifest["attr"], 0) & 2) != 0;
      bool isValueObject = value != null && value.ValueType == JSC.JSValueType.Object && value.Value != null;
      if(!isReadonly && isValueObject) {
        List<MenuItemDto> addItems = BuildAddItems(manifest, value, fieldPath);
        if(addItems.Count > 0) {
          items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Add", Enabled = true, Willful = false, Children = addItems });
        }
      }

      bool isRequired = manifest != null && (JsLib.OfInt(manifest["attr"], 0) & 1) != 0;
      items.Add(new MenuItemDto() {
        Kind = MenuItemKind.Item,
        Cmd = "delete",
        Text = "Delete",
        Icon = "/ide_icons/cm_delete.png",
        Enabled = !string.IsNullOrEmpty(fieldPath) && !isRequired,
        Willful = false,
      });
      return ViewOpResult.Success();
    }

    private List<MenuItemDto> BuildAddItems(JSC.JSValue manifest, JSC.JSValue value, string fieldPath) {
      List<MenuItemDto> addItems = new List<MenuItemDto>();
      JSC.JSValue fields = ResolveFields(manifest);
      if(fields != null && fields.ValueType == JSC.JSValueType.Object && fields.Value != null) {
        foreach(var kv in fields.OrderBy(z => z.Key, StringComparer.Ordinal)) {
          JSC.JSValue descriptor = kv.Value;
          if(descriptor == null || descriptor.ValueType != JSC.JSValueType.Object || !descriptor["default"].Defined) continue;
          if(value[kv.Key].Defined) continue;
          addItems.Add(BuildAddMenuItem(kv.Key, descriptor, fieldPath));
        }
        return addItems;
      }

      // No declared Fields schema on this node (own manifest or its type) - fall back
      // to the Core types catalog (Boolean/Double/Object/String/...), same fallback
      // ES's InValue.MenuItems uses (_data.Connection.CoreTypes.children) when it hits
      // its own "no Fields" else-branch, and the same /$YS/TYPES/Core fallback
      // WorkspaceMenuBuilder.ResolveAddActions already uses for topic children. These
      // are always "willful" (user names the new field) per their own manifest, so -
      // unlike Fields entries - there's no fixed key to filter as "already present".
      Topic coreTypes = Topic.root.Get("/$YS/TYPES/Core", false);
      if(coreTypes != null) {
        foreach(Topic coreType in coreTypes.children.OrderBy(z => z.name, StringComparer.Ordinal)) {
          JSC.JSValue descriptor = coreType.GetState();
          if(descriptor == null || descriptor.ValueType != JSC.JSValueType.Object || descriptor.Value == null || !descriptor["default"].Defined) continue;
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
        Icon = JsonTreeRowHelpers.ResolveMenuIcon(JsLib.OfString(descriptor["icon"], null), descEditor == "Default" ? Topic.JsValueTypeName(descriptor["default"]) : descEditor, _rootTopic.path, fieldPath),
        Hint = JsLib.OfString(descriptor["hint"], null),
        Enabled = true,
        Willful = JsLib.ofBool(descriptor["willful"], false),
      };
    }

    internal ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      return StateRpcDispatcher.Execute(_rootTopic, fieldPath, cmd, args);
    }

    internal void Close() {
      Dispose();
    }

    public void Dispose() {
      SubRec sub = System.Threading.Interlocked.Exchange(ref _sub, null);
      if(sub != null) sub.Dispose();
      _expanded.Clear();
      _lastChildren.Clear();
    }

    private void ExpandField(string vid) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      _expanded.Add(vid);
      SendUpd(fieldPath);
      SendChildren(fieldPath, vid, false);
    }

    private void CollapseField(string vid) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      RemoveChildrenOf(vid, fieldPath);
      _expanded.Remove(vid);
      SendUpd(fieldPath);
    }

    private void RemoveChildrenOf(string vid, string fieldPath) {
      HashSet<string> previousKeys;
      if(!_lastChildren.TryGetValue(vid, out previousKeys)) return;
      foreach(string key in previousKeys) {
        string childFieldPath = string.IsNullOrEmpty(fieldPath) ? key : fieldPath + "." + key;
        string childVid = RootVid + "#" + childFieldPath;
        _send(ViewProtocolSerializer.Del(childVid));
        _targets.RemoveSubtree(childVid);
        _expanded.RemoveWhere(v => v == childVid || VidHelper.IsDescendant(childVid, v));
        _lastChildren.Remove(childVid);
      }
      _lastChildren.Remove(vid);
    }

    // Inserts (or, when diff=true, reconciles) the row set for fieldPath's own
    // object keys. diff=false is used right after Expand (the client has no rows
    // for this subtree yet, so everything is a fresh add); diff=true is used from
    // OnRootChanged, where existing children may just need an update, new keys need
    // an add, and removed keys need a del - mirroring InValue.UpdateData's full
    // recursive re-walk on every state change.
    private void SendChildren(string fieldPath, string vid, bool diff) {
      JSC.JSValue value = string.IsNullOrEmpty(fieldPath) ? _rootTopic.GetState() : JsLib.GetField(_rootTopic.GetState(), fieldPath);
      HashSet<string> currentKeys = new HashSet<string>(StringComparer.Ordinal);
      if(JsonTreeRowHelpers.HasFields(value)) {
        foreach(var kv in value.OrderBy(z => z.Key, StringComparer.Ordinal)) {
          currentKeys.Add(kv.Key);
          string childFieldPath = string.IsNullOrEmpty(fieldPath) ? kv.Key : fieldPath + "." + kv.Key;
          string childVid = RootVid + "#" + childFieldPath;
          if(diff && _targets.Get(childVid) != null) SendUpd(childFieldPath);
          else SendAdd(BuildRow(childFieldPath));
        }
      }

      if(diff) {
        HashSet<string> previousKeys;
        if(_lastChildren.TryGetValue(vid, out previousKeys)) {
          foreach(string staleKey in previousKeys) {
            if(currentKeys.Contains(staleKey)) continue;
            string staleFieldPath = string.IsNullOrEmpty(fieldPath) ? staleKey : fieldPath + "." + staleKey;
            string staleVid = RootVid + "#" + staleFieldPath;
            _send(ViewProtocolSerializer.Del(staleVid));
            _targets.RemoveSubtree(staleVid);
            _expanded.RemoveWhere(v => v == staleVid || VidHelper.IsDescendant(staleVid, v));
            _lastChildren.Remove(staleVid);
          }
        }
      }

      if(currentKeys.Count > 0) _lastChildren[vid] = currentKeys;
      else _lastChildren.Remove(vid);
    }

    private void OnRootChanged(Perform perform, SubRec sub) {
      try {
        if(perform == null) return;
        if(perform.Art == Perform.E_Art.remove) {
          HandleRootRemoved();
          return;
        }
        if(perform.Art != Perform.E_Art.changedState && perform.Art != Perform.E_Art.changedField) return;
        SendUpd(string.Empty);
        foreach(string vid in _expanded.ToArray().OrderBy(v => v, StringComparer.Ordinal)) {
          string fieldPath = VidHelper.GetFieldPath(vid);
          SendChildren(fieldPath, vid, true);
        }
      }
      catch(Exception ex) {
        Log.Warning("StateTreeController.OnRootChanged - {0}", ex.Message);
      }
    }

    // Tells the frontend this document's State root is gone (evnt.del on RootVid - see
    // app-shell.js #onDocumentRootDeleted) and stops this controller, mirroring Dispose().
    private void HandleRootRemoved() {
      string vid = RootVid;
      _send(ViewProtocolSerializer.Del(vid));
      _targets.RemoveSubtree(vid);
      _targets.Remove(vid);
      Dispose();
    }

    private JSC.JSValue ResolveValueAt(string fieldPath) {
      return string.IsNullOrEmpty(fieldPath) ? _rootTopic.GetState() : JsLib.GetField(_rootTopic.GetState(), fieldPath);
    }

    private ViewRowDto BuildRow(string fieldPath) {
      bool isRoot = string.IsNullOrEmpty(fieldPath);
      string vid = isRoot ? RootVid : (RootVid + "#" + fieldPath);
      JSC.JSValue manifest = ResolveFieldManifestAt(_rootTopic, fieldPath);
      JSC.JSValue value = ResolveValueAt(fieldPath);

      // The root row IS the topic itself (top-level state/manifest), so it reuses the
      // same Topic-shaped, type-indirection-aware resolvers Workspace/Children already
      // use (WorkspaceRowProjector.BuildTopicRow) instead of the field-shaped ones
      // below, which only exist because nested field descriptors aren't Topics.
      string editor = isRoot ? EditorHelper.Resolve(_rootTopic) : JsonTreeRowHelpers.ResolveEditorName(manifest, value);
      string resolvedEditor = JsonTreeRowHelpers.ResolvedEditorOrDefault(editor);

      string optionsKey;
      JSC.JSValue options;
      if(isRoot) {
        optionsKey = EnumHelper.Resolve(_rootTopic, editor);
        options = EnumHelper.ResolveOptions(_rootTopic, editor);
      } else if(resolvedEditor == "Enum") {
        optionsKey = manifest == null ? null : JsLib.OfString(manifest["enum"], null);
        options = EnumHelper.ResolveOptionsForSource(optionsKey);
      } else {
        optionsKey = null;
        options = null;
      }

      return new ViewRowDto() {
        Vid = vid,
        Level = JsonTreeRowHelpers.LevelOf(fieldPath),
        Expander = JsonTreeRowHelpers.HasFields(value) ? (_expanded.Contains(vid) ? 2 : 1) : 0,
        Icon = isRoot ? IconResource.Resolve(_rootTopic, editor) : JsonTreeRowHelpers.ResolveRowIcon(manifest, value, resolvedEditor, _rootTopic.path, fieldPath),
        Name = isRoot ? "State" : JsonTreeRowHelpers.LastSegment(fieldPath),
        Editor = resolvedEditor,
        Value = resolvedEditor == "Default" ? TopicDisplayValueFormatter.Format(value) : WorkspaceRowProjector.ToWebStateValue(value),
        Readonly = isRoot ? _rootTopic.CheckAttribute(Topic.Attribute.Readonly) : (manifest != null && (JsLib.OfInt(manifest["attr"], 0) & 2) != 0),
        OptionsKey = optionsKey,
        Options = options,
      };
    }

    private void SendAdd(ViewRowDto row) {
      RememberTarget(row);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, row.Vid);
      dto["level"] = new JSL.Number(row.Level);
      if(row.Expander != 0) dto["expander"] = new JSL.Number(row.Expander);
      dto["icon"] = row.Icon ?? string.Empty;
      dto["name"] = row.Name ?? string.Empty;
      if(!string.IsNullOrEmpty(row.Editor) && row.Editor != "Default") dto["editor"] = row.Editor;
      if(!JsonTreeRowHelpers.IsDefaultValue(row.Value)) dto["value"] = row.Value;
      if(row.Readonly) dto["readonly"] = row.Readonly;
      if(row.Options != null) dto["options"] = row.Options;
      _send(dto);
    }

    private void SendUpd(string fieldPath) {
      ViewRowDto row = BuildRow(fieldPath);
      ViewRowDto previous = null;
      ViewTarget target = _targets.Get(row.Vid);
      if(target != null) previous = target.CachedRow;

      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, row.Vid);
      bool changed = false;

      if(previous == null || previous.Expander != row.Expander) {
        dto["expander"] = new JSL.Number(row.Expander);
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.Icon, row.Icon)) {
        dto["icon"] = row.Icon ?? string.Empty;
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.Editor, row.Editor)) {
        dto["editor"] = row.Editor ?? "Default";
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.JsValueEquals(previous.Value, row.Value)) {
        dto["value"] = row.Value ?? JSC.JSValue.Null;
        changed = true;
      }
      if(previous == null || previous.Readonly != row.Readonly) {
        dto["readonly"] = row.Readonly;
        changed = true;
      }
      if(previous == null || !JsonTreeRowHelpers.StringEquals(previous.OptionsKey, row.OptionsKey)) {
        dto["options"] = row.Options ?? JSC.JSValue.Null;
        changed = true;
      }

      RememberTarget(row);
      if(changed) _send(dto);
    }

    private void RememberTarget(ViewRowDto row) {
      _targets.Add(row.Vid, new ViewTarget() {
        TopicPath = _rootTopic.path,
        FieldPath = VidHelper.GetFieldPath(row.Vid),
        TargetKind = ViewTargetKind.State,
        CachedRow = row,
      });
    }

    // Walks manifest["Fields"][segment] per path segment, starting from the root
    // topic's own manifest - mirrors how InValue.UpdateData/UpdateType passes
    // _manifest["Fields"][key] down as each child InValue's own manifest. Shared
    // (internal static) so StateRpcDispatcher can resolve the same Fields catalog
    // for add:/delete without duplicating the walk.
    internal static JSC.JSValue ResolveFieldManifestAt(Topic rootTopic, string fieldPath) {
      JSC.JSValue manifest = rootTopic.GetField(null);
      if(string.IsNullOrEmpty(fieldPath)) return manifest;
      foreach(string segment in fieldPath.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries)) {
        JSC.JSValue fields = ResolveFields(manifest);
        if(fields == null || fields.ValueType != JSC.JSValueType.Object || fields.Value == null) return null;
        manifest = fields[segment];
        if(manifest == null || !manifest.Defined) return null;
      }
      return manifest;
    }

    // manifest["Fields"] if the manifest declares it directly, else - when the
    // manifest instead declares "type" - the type topic's own STATE "Fields" (e.g.
    // /$YS/TYPES/LoBlock/Binary/AND's manifest is only {"attr":...,"type":
    // "Ext/LBDescr"}; its "src" field's editor:"JS" lives in Ext/LBDescr's state
    // Fields.src, not on AND itself). ES's InValue.cs never reads "type" for this -
    // because by the time WPF's DTopic hands InValue a manifest, DTopic.ProtoDeep
    // has already spliced the type topic's state in as that manifest's live JS
    // __proto__, so a plain manifest["Fields"] property read transparently inherits
    // it. The server has no such live prototype chain, so this replicates the
    // (single-level - type is resolved once per topic manifest, not re-resolved for
    // each nested Fields entry, matching ProtoDeep's one-time chaining) fallback
    // explicitly. Applied uniformly at every path segment, though in practice only
    // the root topic manifest (fieldPath == "") is ever expected to declare "type".
    private static JSC.JSValue ResolveFields(JSC.JSValue manifest) {
      JSC.JSValue own = (manifest != null && manifest.ValueType == JSC.JSValueType.Object) ? manifest["Fields"] : null;
      if(own != null && own.ValueType == JSC.JSValueType.Object && own.Value != null) return own;

      string typePath = manifest == null ? null : JsLib.OfString(manifest["type"], null);
      if(string.IsNullOrWhiteSpace(typePath)) return own;

      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      JSC.JSValue typeState = typeTopic == null ? null : typeTopic.GetState();
      return (typeState != null && typeState.ValueType == JSC.JSValueType.Object) ? typeState["Fields"] : own;
    }

    // Resolves a single add-action descriptor by key for StateRpcDispatcher's
    // add:<key> command, via the exact same two-tier lookup BuildAddItems uses to
    // populate the menu: the node's own (or type-inherited) Fields[key] entry, or -
    // when there's no Fields catalog at all - the /$YS/TYPES/Core child of that name.
    internal static JSC.JSValue ResolveAddDescriptor(Topic rootTopic, string fieldPath, string key) {
      JSC.JSValue manifest = ResolveFieldManifestAt(rootTopic, fieldPath);
      JSC.JSValue fields = ResolveFields(manifest);
      if(fields != null && fields.ValueType == JSC.JSValueType.Object && fields.Value != null) {
        JSC.JSValue descriptor = fields[key];
        return (descriptor != null && descriptor.ValueType == JSC.JSValueType.Object && descriptor["default"].Defined) ? descriptor : null;
      }

      Topic coreTypes = Topic.root.Get("/$YS/TYPES/Core", false);
      Topic coreType = coreTypes == null ? null : coreTypes.Get(key, false);
      if(coreType == null) return null;
      JSC.JSValue coreDescriptor = coreType.GetState();
      return (coreDescriptor != null && coreDescriptor.ValueType == JSC.JSValueType.Object && coreDescriptor.Value != null && coreDescriptor["default"].Defined) ? coreDescriptor : null;
    }
  }
}
