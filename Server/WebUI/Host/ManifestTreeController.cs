///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  // Backs the Inspector document's "Manifest" tree: the topic's own manifest
  // (Topic.GetField(null)), walked recursively as an object-key tree - ported from
  // ES's InManifest.cs. Structurally a sibling of StateTreeController (same
  // expand/collapse/diff shape, pure JSValue/string helpers shared via
  // JsonTreeRowHelpers.cs), but the schema catalog ("mi", not "Fields") always comes
  // from one fixed global topic, /$YS/TYPES/Ext/Manifest, rather than per-topic
  // "type" indirection, and writes go through Topic.SetField directly (a native
  // per-path manifest write) instead of State's hand-rolled merge+SetState.
  internal sealed class ManifestTreeController : IDisposable {
    private const string ManifestSchemaTopicPath = "/$YS/TYPES/Ext/Manifest";

    private readonly Action<JSC.JSObject> _send;
    private readonly ViewTargetRegistry _targets;
    private readonly Topic _rootTopic;
    private readonly string _viewName;
    private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _lastChildren = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    private SubRec _sub;

    internal ManifestTreeController(Action<JSC.JSObject> send, ViewTargetRegistry targets, Topic rootTopic, string viewName) {
      _send = send;
      _targets = targets;
      _rootTopic = rootTopic ?? Topic.root;
      _viewName = string.IsNullOrEmpty(viewName) ? "inspmanifest" : viewName;
      // Only Field is needed (not Value) - the Manifest tree never cares about state
      // changes, only its own topic's manifest. Once is what makes the subscription
      // observe the subscribed topic's OWN changedField, not just its children's -
      // see StateTreeController's ctor comment for the full explanation/precedent.
      _sub = _rootTopic.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.Once, OnRootChanged);
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
        // The root row always resolves to the Default (read-only display) editor - see
        // BuildRow - so this is unreachable via the UI, matching WPF where the root
        // Manifest node is always IsGroupHeader/object-typed and never itself committed.
        return ViewOpResult.Error("view_commit_not_supported", "Manifest root cannot be committed directly");
      }
      _rootTopic.SetField(fieldPath, value);
      return ViewOpResult.Success();
    }

    internal ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      string fieldPath = VidHelper.GetFieldPath(vid);
      JSC.JSValue ownOverride;
      JSC.JSValue schema = ResolveFieldSchemaAt(_rootTopic, fieldPath, out ownOverride);
      JSC.JSValue value = ResolveValueAt(fieldPath);
      items = new List<MenuItemDto>();

      bool isReadonly = schema != null && (JsLib.OfInt(schema["attr"], 0) & 2) != 0;
      bool isValueObject = value != null && value.ValueType == JSC.JSValueType.Object && value.Value != null;
      if(!isReadonly && isValueObject) {
        List<MenuItemDto> addItems = BuildAddItems(schema, ownOverride, value, fieldPath);
        if(addItems.Count > 0) {
          items.Add(new MenuItemDto() { Kind = MenuItemKind.Item, Text = "Add", Enabled = true, Willful = false, Children = addItems });
        }
      }

      bool isRequired = schema != null && (JsLib.OfInt(schema["attr"], 0) & 1) != 0;
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

    // Enumerates the "mi" catalog applicable at this node. At the root this is exactly
    // the global schema's own top-level catalog (verified against InManifest.MenuItems:
    // at the root, its v1.__proto__ check never fires, because only individual "mi"
    // entries - never the whole catalog object - ever get a __proto__ assigned; a
    // topic's own top-level "mi" override affects how its OWN existing manifest keys
    // render, not what the root's Add menu offers). At any nested level, the entry's
    // generic "mi" catalog is unioned with the corresponding own-override "mi" catalog
    // (mirrors InManifest.MenuItems' v1.Union(v1.__proto__), which IS meaningful below
    // the root, since ResolveFieldSchemaAt's per-level merge does give nested "mi"
    // catalogs a real prototype/override pairing).
    private List<MenuItemDto> BuildAddItems(JSC.JSValue schema, JSC.JSValue ownOverride, JSC.JSValue value, string fieldPath) {
      List<MenuItemDto> addItems = new List<MenuItemDto>();
      bool isRoot = string.IsNullOrEmpty(fieldPath);

      JSC.JSValue genericCatalog = WorkspaceMenuBuilder.IsObject(schema) ? schema["mi"] : null;
      Dictionary<string, JSC.JSValue> catalog = new Dictionary<string, JSC.JSValue>(StringComparer.Ordinal);
      if(WorkspaceMenuBuilder.IsObject(genericCatalog)) {
        foreach(var kv in genericCatalog) catalog[kv.Key] = kv.Value;
      }
      if(!isRoot) {
        JSC.JSValue overrideCatalog = WorkspaceMenuBuilder.IsObject(ownOverride) ? ownOverride["mi"] : null;
        if(WorkspaceMenuBuilder.IsObject(overrideCatalog)) {
          foreach(var kv in overrideCatalog) if(!catalog.ContainsKey(kv.Key)) catalog[kv.Key] = kv.Value;
        }
      }

      if(catalog.Count == 0) {
        // No schema catalog anywhere for this node - fall back to /$YS/TYPES/Core, same
        // as InManifest.MenuItems' identical "no mi" else-branch and StateTreeController's.
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

      foreach(var kv in catalog.OrderBy(z => z.Key, StringComparer.Ordinal)) {
        JSC.JSValue descriptor = kv.Value;
        if(descriptor == null || descriptor.ValueType != JSC.JSValueType.Object || !descriptor["default"].Defined) continue;
        if(value[kv.Key].Defined) continue;
        addItems.Add(BuildAddMenuItem(kv.Key, descriptor, fieldPath));
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
      return ManifestRpcDispatcher.Execute(_rootTopic, fieldPath, cmd, args);
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

    private void SendChildren(string fieldPath, string vid, bool diff) {
      JSC.JSValue value = ResolveValueAt(fieldPath);
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
        if(perform.Art != Perform.E_Art.changedField) return;
        SendUpd(string.Empty);
        foreach(string vid in _expanded.ToArray().OrderBy(v => v, StringComparer.Ordinal)) {
          string fieldPath = VidHelper.GetFieldPath(vid);
          SendChildren(fieldPath, vid, true);
        }
      }
      catch(Exception ex) {
        Log.Warning("ManifestTreeController.OnRootChanged - {0}", ex.Message);
      }
    }

    // Tells the frontend this document's Manifest root is gone (evnt.del on RootVid -
    // see app-shell.js #onDocumentRootDeleted) and stops this controller, mirroring Dispose().
    private void HandleRootRemoved() {
      string vid = RootVid;
      _send(ViewProtocolSerializer.Del(vid));
      _targets.RemoveSubtree(vid);
      _targets.Remove(vid);
      Dispose();
    }

    private JSC.JSValue ResolveValueAt(string fieldPath) {
      return string.IsNullOrEmpty(fieldPath) ? _rootTopic.GetField(null) : JsLib.GetField(_rootTopic.GetField(null), fieldPath);
    }

    private ViewRowDto BuildRow(string fieldPath) {
      bool isRoot = string.IsNullOrEmpty(fieldPath);
      string vid = isRoot ? RootVid : (RootVid + "#" + fieldPath);
      JSC.JSValue value = ResolveValueAt(fieldPath);

      string resolvedEditor;
      string icon;
      bool readonlyFlag;
      if(isRoot) {
        // The root represents the manifest object itself. Per InManifest's WPF ctor
        // (UpdateType(_tManifest.State, _data.Manifest) - the schema passed for the root
        // node is the whole {"mi":{...}} blob, which has no top-level editor/icon of its
        // own), this always resolves to Default/generic-object - no per-topic
        // customization point at the root, unlike State's root (which IS the topic).
        resolvedEditor = "Default";
        icon = "/ide_icons/ty_obj.png";
        readonlyFlag = false;
      } else {
        JSC.JSValue schema = ResolveFieldSchemaAt(_rootTopic, fieldPath, out _);
        string editor = JsonTreeRowHelpers.ResolveEditorName(schema, value);
        resolvedEditor = JsonTreeRowHelpers.ResolvedEditorOrDefault(editor);
        icon = JsonTreeRowHelpers.ResolveRowIcon(schema, value, resolvedEditor, _rootTopic.path, fieldPath);
        readonlyFlag = schema != null && (JsLib.OfInt(schema["attr"], 0) & 2) != 0;
      }

      string optionsKey = null;
      JSC.JSValue options = null;
      if(!isRoot && resolvedEditor == "Enum") {
        JSC.JSValue schema = ResolveFieldSchemaAt(_rootTopic, fieldPath, out _);
        optionsKey = schema == null ? null : JsLib.OfString(schema["enum"], null);
        options = EnumHelper.ResolveOptionsForSource(optionsKey);
      }

      return new ViewRowDto() {
        Vid = vid,
        Level = JsonTreeRowHelpers.LevelOf(fieldPath),
        Expander = JsonTreeRowHelpers.HasFields(value) ? (_expanded.Contains(vid) ? 2 : 1) : 0,
        Icon = icon,
        Name = isRoot ? "Manifest" : JsonTreeRowHelpers.LastSegment(fieldPath),
        Editor = resolvedEditor,
        Value = resolvedEditor == "Default" ? TopicDisplayValueFormatter.Format(value) : WorkspaceRowProjector.ToWebStateValue(value),
        Readonly = readonlyFlag,
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
        TargetKind = ViewTargetKind.Manifest,
        CachedRow = row,
      });
    }

    private static JSC.JSValue GlobalManifestSchemaRoot() {
      Topic schemaTopic = Topic.root.Get(ManifestSchemaTopicPath, false);
      return schemaTopic == null ? null : schemaTopic.GetState();
    }

    // Walks schema["mi"][segment] per path segment, starting from the global
    // /$YS/TYPES/Ext/Manifest schema, merging in the topic's own manifest "mi"
    // override (if any) at every level via a JS prototype fallback - a direct port of
    // InManifest.UpdateType's cs/cs_p/__proto__ block, generalized to an explicit
    // parallel walk of both trees (generic schema vs. topic's own "mi") since the
    // server has no persistent live object graph for a real prototype chain to ride
    // on implicitly across separate resolution calls the way WPF's does. outOwnOverride
    // returns the paired entry from the topic's own override tree at the same path
    // (null if the topic declares no override there) - callers building an Add-menu
    // catalog for this node's children union entry["mi"] with outOwnOverride["mi"].
    internal static JSC.JSValue ResolveFieldSchemaAt(Topic rootTopic, string fieldPath, out JSC.JSValue outOwnOverride) {
      outOwnOverride = null;
      if(string.IsNullOrEmpty(fieldPath)) return GlobalManifestSchemaRoot();

      JSC.JSValue genericSource = GlobalManifestSchemaRoot();
      JSC.JSValue ownSource = rootTopic.GetField(null);
      JSC.JSValue entry = null;
      JSC.JSValue ownEntry = null;

      foreach(string segment in fieldPath.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries)) {
        JSC.JSValue genericMi = WorkspaceMenuBuilder.IsObject(genericSource) ? genericSource["mi"] : null;
        JSC.JSValue ownMi = WorkspaceMenuBuilder.IsObject(ownSource) ? ownSource["mi"] : null;

        JSC.JSValue generic = WorkspaceMenuBuilder.IsObject(genericMi) ? genericMi[segment] : null;
        JSC.JSValue own = WorkspaceMenuBuilder.IsObject(ownMi) ? ownMi[segment] : null;
        bool genericOk = WorkspaceMenuBuilder.IsObject(generic);
        bool ownOk = WorkspaceMenuBuilder.IsObject(own);
        if(!genericOk && !ownOk) {
          outOwnOverride = null;
          return null;
        }

        entry = genericOk ? generic : JSC.JSObject.CreateObject();
        ownEntry = ownOk ? own : null;
        if(ownOk && !object.ReferenceEquals(entry, own)) {
          JSC.JSValue nestedMi = entry["mi"];
          if(WorkspaceMenuBuilder.IsObject(nestedMi)) nestedMi.__proto__ = own["mi"].ToObject();
          entry.__proto__ = own.ToObject();
        }

        genericSource = entry;
        ownSource = ownEntry;
      }
      outOwnOverride = ownEntry;
      return entry;
    }

    // Resolves a single add-action descriptor by key for ManifestRpcDispatcher's
    // add:<key> command - the union catalog BuildAddItems enumerates from, narrowed to
    // one key, or the /$YS/TYPES/Core fallback when there's no schema catalog at all.
    internal static JSC.JSValue ResolveAddDescriptor(Topic rootTopic, string fieldPath, string key) {
      JSC.JSValue ownOverride;
      JSC.JSValue schema = ResolveFieldSchemaAt(rootTopic, fieldPath, out ownOverride);
      bool isRoot = string.IsNullOrEmpty(fieldPath);

      JSC.JSValue genericCatalog = WorkspaceMenuBuilder.IsObject(schema) ? schema["mi"] : null;
      if(WorkspaceMenuBuilder.IsObject(genericCatalog)) {
        JSC.JSValue descriptor = genericCatalog[key];
        if(descriptor != null && descriptor.ValueType == JSC.JSValueType.Object && descriptor["default"].Defined) return descriptor;
      }
      if(!isRoot) {
        JSC.JSValue overrideCatalog = WorkspaceMenuBuilder.IsObject(ownOverride) ? ownOverride["mi"] : null;
        if(WorkspaceMenuBuilder.IsObject(overrideCatalog)) {
          JSC.JSValue descriptor = overrideCatalog[key];
          if(descriptor != null && descriptor.ValueType == JSC.JSValueType.Object && descriptor["default"].Defined) return descriptor;
        }
      }

      Topic coreTypes = Topic.root.Get("/$YS/TYPES/Core", false);
      Topic coreType = coreTypes == null ? null : coreTypes.Get(key, false);
      if(coreType == null) return null;
      JSC.JSValue coreDescriptor = coreType.GetState();
      return (coreDescriptor != null && coreDescriptor.ValueType == JSC.JSValueType.Object && coreDescriptor.Value != null && coreDescriptor["default"].Defined) ? coreDescriptor : null;
    }
  }
}
