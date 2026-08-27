///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using X13.Repository;

namespace X13.WebUI {
  // RPC commands for the Inspector's "State" tree - add/delete of object fields
  // within one topic's JSON state, keyed off the field's own manifest Fields
  // catalog (ResolveFieldManifestAt) rather than topic creation like
  // TopicRpcDispatcher. Ported from ES's InValue.MenuItems/miAdd_Click/
  // miDelete_Click/ChangeValue.
  internal static class StateRpcDispatcher {
    internal static ViewOpResult Execute(Topic rootTopic, string fieldPath, string cmd, JSC.JSValue args, Topic prim = null) {
      try {
        if(cmd == "delete") return ExecuteDelete(rootTopic, fieldPath, prim);
        if(cmd != null && cmd.StartsWith("add:", StringComparison.Ordinal)) return ExecuteAdd(rootTopic, fieldPath, cmd.Substring(4), args, prim);
        // Always the document's own root topic, never the field row that hosts the button -
        // mirrors ES's veDevicePLC.xaml.cs, which calls _stateT.Call(name, _stateT.path) where
        // _stateT is the InValue's owning topic. Shared with the tree dispatcher so the rule about
        // what reaches a handler cannot differ between the panes an action is invoked from.
        if(cmd != null && cmd.StartsWith("action:", StringComparison.Ordinal)) return TopicRpcDispatcher.ExecuteAction(rootTopic, cmd.Substring(7), args);
      }
      catch(Exception ex) {
        return ViewOpResult.Error("rpc_execution_failed", ex.Message);
      }
      return ViewOpResult.Error("rpc_command_unknown", "Unknown RPC command: " + cmd);
    }

    private static ViewOpResult ExecuteDelete(Topic rootTopic, string fieldPath, Topic prim) {
      if(string.IsNullOrEmpty(fieldPath)) {
        return ViewOpResult.Error("delete_target_invalid", "Delete target is invalid");
      }
      JSC.JSValue manifest = StateTreeController.ResolveFieldManifestAt(rootTopic, fieldPath);
      if(manifest != null && (manifest.AsInt("attr", 0) & 1) != 0) {
        return ViewOpResult.Error("delete_target_required", "Required field cannot be deleted: " + fieldPath);
      }
      ViewOpResult blocked = StateTreeController.CheckWritable(rootTopic, fieldPath);
      if(blocked != null) return blocked;
      rootTopic.SetState(JsLib.SetField(rootTopic.GetState(), fieldPath, JSC.JSValue.Null), prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteAdd(Topic rootTopic, string fieldPath, string key, JSC.JSValue args, Topic prim) {
      if(string.IsNullOrWhiteSpace(key)) {
        return ViewOpResult.Error("add_key_missing", "Add key is missing");
      }
      // fieldPath is the container being extended - the same one BuildMenu gates the Add
      // submenu on, so server and UI agree on what "readonly" covers here.
      ViewOpResult blocked = StateTreeController.CheckWritable(rootTopic, fieldPath);
      if(blocked != null) return blocked;
      JSC.JSValue descriptor = StateTreeController.ResolveAddDescriptor(rootTopic, fieldPath, key);
      if(descriptor == null) {
        return ViewOpResult.Error("add_action_not_found", "Add action not found: " + key);
      }

      bool willful = descriptor.AsBool("willful", false);
      string name = willful ? args.AsString("name", null) : key;
      if(!IsValidFieldName(name)) {
        return ViewOpResult.Error(willful ? "add_name_required" : "add_name_invalid", "Invalid field name: " + (name ?? "<null>"));
      }

      JSC.JSValue currentValue = string.IsNullOrEmpty(fieldPath) ? rootTopic.GetState() : rootTopic.GetState().Field(fieldPath);
      // Value != null matters: JSValue.Null also reports ValueType Object, and indexing it throws.
      if(currentValue.IsObject() && currentValue[name].Defined) {
        return ViewOpResult.Error("add_target_exists", "Field already exists: " + name);
      }

      string targetPath = string.IsNullOrEmpty(fieldPath) ? name : (fieldPath + "." + name);
      JSC.JSValue newValue = JsLib.Clone(descriptor["default"]);
      rootTopic.SetState(JsLib.SetField(rootTopic.GetState(), targetPath, newValue), prim);
      return ViewOpResult.Success();
    }

    private static bool IsValidFieldName(string name) {
      if(string.IsNullOrWhiteSpace(name)) return false;
      return name.IndexOf('.') < 0 && name.IndexOf('#') < 0;
    }
  }
}
