///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal static class WorkspaceRpcDispatcher {
    internal static ViewOpResult Execute(Topic topic, string cmd, JSC.JSValue args) {
      try {
        if(cmd == "delete") return ExecuteDelete(topic);
        if(cmd == "rename") return ExecuteRename(topic, args);
        if(cmd == "paste") return ExecutePaste(topic, args);
        if(cmd.StartsWith("action:", StringComparison.Ordinal)) return ExecuteAction(topic, cmd.Substring(7));
        if(cmd.StartsWith("add:", StringComparison.Ordinal)) return ExecuteAdd(topic, cmd.Substring(4), args);
      }
      catch(Exception ex) {
        return ViewOpResult.Error("rpc_execution_failed", ex.Message);
      }

      return ViewOpResult.Error("rpc_command_unknown", "Unknown RPC command: " + cmd);
    }

    private static ViewOpResult ExecuteDelete(Topic topic) {
      if(topic == null || topic.parent == null) {
        return ViewOpResult.Error("delete_target_invalid", "Delete target is invalid");
      }
      if(topic.CheckAttribute(Topic.Attribute.Required)) {
        return ViewOpResult.Error("delete_target_required", "Required topic cannot be deleted: " + topic.path);
      }
      topic.Remove();
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteRename(Topic topic, JSC.JSValue args) {
      if(topic == null || topic.parent == null) {
        return ViewOpResult.Error("rename_target_invalid", "Rename target is invalid");
      }
      if(topic.CheckAttribute(Topic.Attribute.Required)) {
        return ViewOpResult.Error("rename_target_required", "Required topic cannot be renamed: " + topic.path);
      }

      string name = JsLib.OfString(args == null ? null : args["name"], null);
      if(!IsValidChildName(name)) {
        return ViewOpResult.Error("rename_name_invalid", "Invalid topic name: " + (name ?? "<null>"));
      }
      if(string.Equals(topic.name, name, StringComparison.Ordinal)) return ViewOpResult.Success();
      Topic existing = topic.parent.Get(name, false);
      if(existing != null && existing != topic) {
        return ViewOpResult.Error("rename_target_exists", "Topic already exists: " + topic.parent.path + "/" + name);
      }

      topic.Move(null, name);
      return ViewOpResult.Success();
    }


    private static ViewOpResult ExecutePaste(Topic targetParent, JSC.JSValue args) {
      if(targetParent == null) {
        return ViewOpResult.Error("paste_target_invalid", "Paste target is invalid");
      }

      string sourcePath = JsLib.OfString(args == null ? null : args["sourcePath"], null);
      string sourceVid = JsLib.OfString(args == null ? null : args["sourceVid"], null);
      if(string.IsNullOrWhiteSpace(sourcePath)) {
        return ViewOpResult.Error("paste_source_missing", "Paste source is missing");
      }
      if(!string.IsNullOrWhiteSpace(sourceVid)) {
        if(VidHelper.GetView(sourceVid) != "workspace" || !string.Equals(VidHelper.GetTopicPath(sourceVid), sourcePath, StringComparison.Ordinal)) {
          return ViewOpResult.Error("paste_source_mismatch", "Paste source does not match source vid");
        }
      }

      Topic source = Topic.root.Get(sourcePath, false);
      if(source == null) {
        return ViewOpResult.Error("paste_source_not_found", "Paste source not found: " + sourcePath);
      }
      if(source.parent == null) {
        return ViewOpResult.Error("paste_source_root", "Root topic cannot be moved");
      }
      if(source.CheckAttribute(Topic.Attribute.Required)) {
        return ViewOpResult.Error("paste_source_required", "Required topic cannot be moved: " + source.path);
      }
      if(object.ReferenceEquals(targetParent, source)) {
        return ViewOpResult.Error("paste_target_is_source", "Target parent is the source topic");
      }
      if(IsDescendantOf(targetParent, source)) {
        return ViewOpResult.Error("paste_target_descendant", "Target parent is a descendant of the source topic");
      }
      if(targetParent.Get(source.name, false) != null) {
        return ViewOpResult.Error("paste_target_exists", "Topic already exists: " + targetParent.path + "/" + source.name);
      }

      source.Move(targetParent, source.name);
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteAction(Topic topic, string actionName) {
      JSC.JSValue action;
      if(string.IsNullOrWhiteSpace(actionName) || !WorkspaceMenuBuilder.ResolveActionDescriptor(topic, actionName, out action)) {
        return ViewOpResult.Error("action_not_found", "Action not found: " + (actionName ?? "<null>"));
      }
      RPC.Call(actionName, new JSC.JSValue[] { new JSL.String(topic.path) });
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteAdd(Topic topic, string key, JSC.JSValue args) {
      Dictionary<string, JSC.JSValue> actions = WorkspaceMenuBuilder.ResolveAddActions(topic);
      JSC.JSValue action;
      if(string.IsNullOrWhiteSpace(key) || actions == null || !actions.TryGetValue(key, out action)) {
        return ViewOpResult.Error("add_action_not_found", "Add action not found: " + (key ?? "<null>"));
      }
      if(WorkspaceMenuBuilder.ResourceBusy(topic, actions, key, action)) {
        return ViewOpResult.Error("add_resource_busy", "Required resource is already used: " + key);
      }

      bool willful = JsLib.ofBool(action["willful"], false);
      string name = willful ? JsLib.OfString(args == null ? null : args["name"], null) : key;
      if(!IsValidChildName(name)) {
        return ViewOpResult.Error(willful ? "add_name_required" : "add_name_invalid", "Invalid child name: " + (name ?? "<null>"));
      }
      if(topic.Get(name, false) != null) {
        return ViewOpResult.Error("add_target_exists", "Topic already exists: " + topic.path + "/" + name);
      }

      JSC.JSValue state = action["default"];
      JSC.JSValue manifest = action["manifest"];
      Topic child = Topic.I.Get(topic, name, true, null, false, false);
      Topic.I.Fill(
        child,
        state != null && state.Defined ? JsLib.Clone(state) : JSC.JSValue.Null,
        WorkspaceMenuBuilder.IsObject(manifest) ? JsLib.Clone(manifest) : null,
        null);
      return ViewOpResult.Success();
    }

    private static bool IsValidChildName(string name) {
      if(string.IsNullOrWhiteSpace(name)) return false;
      return name.IndexOf('/') < 0 && name.IndexOf('#') < 0;
    }

    private static bool IsDescendantOf(Topic candidate, Topic ancestor) {
      for(Topic cur = candidate; cur != null; cur = cur.parent) {
        if(object.ReferenceEquals(cur, ancestor)) return true;
      }
      return false;
    }
  }
}
