///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using X13.Repository;

namespace X13.WebUI {
  // RPC commands for the Inspector's "Manifest" tree - add/delete of manifest keys,
  // keyed off the schema catalog ManifestTreeController.ResolveFieldSchemaAt/
  // ResolveAddDescriptor resolve ("mi", global + per-topic override) rather than
  // State's "Fields". Ported from ES's InManifest.MenuItems/miAdd_Click/
  // miDelete_Click. Unlike StateRpcDispatcher, writes go straight through
  // Topic.SetField (a native dotted-path manifest write with the same merge/delete
  // semantics JsLib.SetField already gives State) - no manual merge needed.
  internal static class ManifestRpcDispatcher {
    internal static ViewOpResult Execute(Topic rootTopic, string fieldPath, string cmd, JSC.JSValue args, Topic prim = null) {
      try {
        if(cmd == "delete") return ExecuteDelete(rootTopic, fieldPath, prim);
        if(cmd != null && cmd.StartsWith("add:", StringComparison.Ordinal)) return ExecuteAdd(rootTopic, fieldPath, cmd.Substring(4), args, prim);
        if(cmd != null && cmd.StartsWith("action:", StringComparison.Ordinal)) return ExecuteAction(rootTopic, cmd.Substring(7));
      }
      catch(Exception ex) {
        return ViewOpResult.Error("rpc_execution_failed", ex.Message);
      }
      return ViewOpResult.Error("rpc_command_unknown", "Unknown RPC command: " + cmd);
    }

    // See StateRpcDispatcher.ExecuteAction - same rationale, duplicated rather than
    // shared because the two dispatchers otherwise share no Execute-level code path.
    private static ViewOpResult ExecuteAction(Topic rootTopic, string actionName) {
      JSC.JSValue action;
      if(string.IsNullOrWhiteSpace(actionName) || !MenuBuilder.ResolveActionDescriptor(rootTopic, actionName, out action)) {
        return ViewOpResult.Error("action_not_found", "Action not found: " + (actionName ?? "<null>"));
      }
      RPC.Call(actionName, new JSC.JSValue[] { new NiL.JS.BaseLibrary.String(rootTopic.path) });
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteDelete(Topic rootTopic, string fieldPath, Topic prim) {
      if(string.IsNullOrEmpty(fieldPath)) {
        return ViewOpResult.Error("delete_target_invalid", "Delete target is invalid");
      }
      JSC.JSValue ownOverride;
      JSC.JSValue schema = ManifestTreeController.ResolveFieldSchemaAt(rootTopic, fieldPath, out ownOverride);
      if(schema != null && (schema.AsInt("attr", 0) & 1) != 0) {
        return ViewOpResult.Error("delete_target_required", "Required field cannot be deleted: " + fieldPath);
      }
      rootTopic.SetField(fieldPath, JSC.JSValue.Null, prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteAdd(Topic rootTopic, string fieldPath, string key, JSC.JSValue args, Topic prim) {
      if(string.IsNullOrWhiteSpace(key)) {
        return ViewOpResult.Error("add_key_missing", "Add key is missing");
      }
      JSC.JSValue descriptor = ManifestTreeController.ResolveAddDescriptor(rootTopic, fieldPath, key);
      if(descriptor == null) {
        return ViewOpResult.Error("add_action_not_found", "Add action not found: " + key);
      }

      bool willful = descriptor.AsBool("willful", false);
      // A catalog entry may name the field it writes separately from the key it is listed
      // under, so one well-named menu item can add a field inside a namespaced group -
      // "DashboardRO" writing dashboard.netRO. Without it the key is both the label and the
      // field name, and a grouped field could only be reached by adding its container first
      // and descending into it. Only for non-willful entries: a willful one takes its name
      // from the user, which is the whole point of it.
      string relativePath = willful ? null : ManifestTreeController.AddDescriptorPath(descriptor);
      string name = willful ? args.AsString("name", null) : key;
      if(relativePath == null && !IsValidFieldName(name)) {
        return ViewOpResult.Error(willful ? "add_name_required" : "add_name_invalid", "Invalid field name: " + (name ?? "<null>"));
      }
      string relative = relativePath ?? name;

      // Topic.GetField already walks a dotted path and returns the whole manifest for an empty
      // one - the ternary and the extra Field hop were both saying it a second time.
      JSC.JSValue currentValue = rootTopic.GetField(fieldPath);
      // Field, not the indexer: relative may be dotted, and Field walks it while returning
      // NotExists rather than throwing at any missing hop.
      if(currentValue.IsObject() && currentValue.Field(relative).Defined) {
        return ViewOpResult.Error("add_target_exists", "Field already exists: " + relative);
      }

      string targetPath = string.IsNullOrEmpty(fieldPath) ? relative : (fieldPath + "." + relative);
      JSC.JSValue newValue = JsLib.Clone(descriptor["default"]);
      rootTopic.SetField(targetPath, newValue, prim);
      return ViewOpResult.Success();
    }

    private static bool IsValidFieldName(string name) {
      if(string.IsNullOrWhiteSpace(name)) return false;
      return name.IndexOf('.') < 0 && name.IndexOf('#') < 0;
    }
  }
}
