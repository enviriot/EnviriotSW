///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using X13.Repository;

namespace X13.WebUI.Host {
  // RPC commands for the Inspector's "Manifest" tree - add/delete of manifest keys,
  // keyed off the schema catalog ManifestTreeController.ResolveFieldSchemaAt/
  // ResolveAddDescriptor resolve ("mi", global + per-topic override) rather than
  // State's "Fields". Ported from ES's InManifest.MenuItems/miAdd_Click/
  // miDelete_Click. Unlike StateRpcDispatcher, writes go straight through
  // Topic.SetField (a native dotted-path manifest write with the same merge/delete
  // semantics JsLib.SetField already gives State) - no manual merge needed.
  internal static class ManifestRpcDispatcher {
    internal static ViewOpResult Execute(Topic rootTopic, string fieldPath, string cmd, JSC.JSValue args) {
      try {
        if(cmd == "delete") return ExecuteDelete(rootTopic, fieldPath);
        if(cmd != null && cmd.StartsWith("add:", StringComparison.Ordinal)) return ExecuteAdd(rootTopic, fieldPath, cmd.Substring(4), args);
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
      if(string.IsNullOrWhiteSpace(actionName) || !WorkspaceMenuBuilder.ResolveActionDescriptor(rootTopic, actionName, out action)) {
        return ViewOpResult.Error("action_not_found", "Action not found: " + (actionName ?? "<null>"));
      }
      RPC.Call(actionName, new JSC.JSValue[] { new NiL.JS.BaseLibrary.String(rootTopic.path) });
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteDelete(Topic rootTopic, string fieldPath) {
      if(string.IsNullOrEmpty(fieldPath)) {
        return ViewOpResult.Error("delete_target_invalid", "Delete target is invalid");
      }
      JSC.JSValue ownOverride;
      JSC.JSValue schema = ManifestTreeController.ResolveFieldSchemaAt(rootTopic, fieldPath, out ownOverride);
      if(schema != null && (JsLib.OfInt(schema["attr"], 0) & 1) != 0) {
        return ViewOpResult.Error("delete_target_required", "Required field cannot be deleted: " + fieldPath);
      }
      rootTopic.SetField(fieldPath, JSC.JSValue.Null);
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteAdd(Topic rootTopic, string fieldPath, string key, JSC.JSValue args) {
      if(string.IsNullOrWhiteSpace(key)) {
        return ViewOpResult.Error("add_key_missing", "Add key is missing");
      }
      JSC.JSValue descriptor = ManifestTreeController.ResolveAddDescriptor(rootTopic, fieldPath, key);
      if(descriptor == null) {
        return ViewOpResult.Error("add_action_not_found", "Add action not found: " + key);
      }

      bool willful = JsLib.ofBool(descriptor["willful"], false);
      string name = willful ? JsLib.OfString(args == null ? null : args["name"], null) : key;
      if(!IsValidFieldName(name)) {
        return ViewOpResult.Error(willful ? "add_name_required" : "add_name_invalid", "Invalid field name: " + (name ?? "<null>"));
      }

      JSC.JSValue currentValue = string.IsNullOrEmpty(fieldPath) ? rootTopic.GetField(null) : JsLib.GetField(rootTopic.GetField(null), fieldPath);
      if(currentValue != null && currentValue.ValueType == JSC.JSValueType.Object && currentValue[name].Defined) {
        return ViewOpResult.Error("add_target_exists", "Field already exists: " + name);
      }

      string targetPath = string.IsNullOrEmpty(fieldPath) ? name : (fieldPath + "." + name);
      JSC.JSValue newValue = JsLib.Clone(descriptor["default"]);
      rootTopic.SetField(targetPath, newValue);
      return ViewOpResult.Success();
    }

    private static bool IsValidFieldName(string name) {
      if(string.IsNullOrWhiteSpace(name)) return false;
      return name.IndexOf('.') < 0 && name.IndexOf('#') < 0;
    }
  }
}
