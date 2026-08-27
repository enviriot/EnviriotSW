///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal static class TopicRpcDispatcher {
    internal static ViewOpResult Execute(Topic topic, string cmd, JSC.JSValue args, Topic prim = null) {
      try {
        if(cmd == "delete") return ExecuteDelete(topic, prim);
        if(cmd == "rename") return ExecuteRename(topic, args, prim);
        if(cmd == "paste") return ExecutePaste(topic, args, prim);
        if(cmd != null && cmd.StartsWith("action:", StringComparison.Ordinal)) return ExecuteAction(topic, cmd.Substring(7), args);
        if(cmd != null && cmd.StartsWith("add:", StringComparison.Ordinal)) return ExecuteAdd(topic, cmd.Substring(4), args, prim);
      }
      catch(Exception ex) {
        return ViewOpResult.Error("rpc_execution_failed", ex.Message);
      }

      return ViewOpResult.Error("rpc_command_unknown", "Unknown RPC command: " + cmd);
    }

    private static ViewOpResult ExecuteDelete(Topic topic, Topic prim) {
      if(topic == null || topic.parent == null) {
        return ViewOpResult.Error("delete_target_invalid", "Delete target is invalid");
      }
      if(topic.CheckAttribute(Topic.Attribute.Required)) {
        return ViewOpResult.Error("delete_target_required", "Required topic cannot be deleted: " + topic.path);
      }
      topic.Remove(prim);
      return ViewOpResult.Success();
    }

    private static ViewOpResult ExecuteRename(Topic topic, JSC.JSValue args, Topic prim) {
      if(topic == null || topic.parent == null) {
        return ViewOpResult.Error("rename_target_invalid", "Rename target is invalid");
      }
      if(topic.CheckAttribute(Topic.Attribute.Required)) {
        return ViewOpResult.Error("rename_target_required", "Required topic cannot be renamed: " + topic.path);
      }

      string name = args.AsString("name", null);
      if(!IsValidChildName(name)) {
        return ViewOpResult.Error("rename_name_invalid", "Invalid topic name: " + (name ?? "<null>"));
      }
      if(string.Equals(topic.name, name, StringComparison.Ordinal)) return ViewOpResult.Success();
      Topic existing = topic.parent.Get(name, false);
      if(existing != null && existing != topic) {
        return ViewOpResult.Error("rename_target_exists", "Topic already exists: " + topic.parent.path + "/" + name);
      }

      topic.Move(null, name, prim);
      return ViewOpResult.Success();
    }


    private static ViewOpResult ExecutePaste(Topic targetParent, JSC.JSValue args, Topic prim) {
      if(targetParent == null) {
        return ViewOpResult.Error("paste_target_invalid", "Paste target is invalid");
      }

      // No null check on args: the path overload goes through Field, which returns NotExists for a
      // null container instead of throwing - that is the whole reason it exists.
      string sourcePath = args.AsString("sourcePath", null);
      string sourceVid = args.AsString("sourceVid", null);
      if(string.IsNullOrWhiteSpace(sourcePath)) {
        return ViewOpResult.Error("paste_source_missing", "Paste source is missing");
      }
      if(!string.IsNullOrWhiteSpace(sourceVid)) {
        // sourceVid may come from any topic-tree view (workspace, inspchildren, ...) -
        // only the topic path it encodes matters here, not which tree it was cut from.
        if(!string.Equals(VidHelper.GetTopicPath(sourceVid), sourcePath, StringComparison.Ordinal)) {
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

      source.Move(targetParent, source.name, prim);
      return ViewOpResult.Success();
    }

    /// <summary>Runs an action the topic declares, and reports what it answered.</summary>
    /// <remarks>Shared by all three dispatchers - the topic tree, the Inspector State pane and the
    /// Inspector Manifest pane - so the same action cannot behave differently depending on which of
    /// them invoked it. It used to be copied into each, and the copies drifted the moment an action
    /// gained an answer: two of them went on reporting a fabricated ok.
    /// <para>The declaration is the permission: only a name the topic (or its type) lists under
    /// "Action" can be reached this way, so a client cannot invoke a registered handler that was
    /// never offered to it. What the handler then does is its own business - this knows nothing
    /// about any plugin, which is the whole point of routing through a declared name.</para>
    /// <para>args is appended only when the caller supplied some. Handlers written against the
    /// original one-argument shape check their arity - MQTT_SN's PlcBuildRpc and friends return
    /// silently on anything but Length == 1 - so passing an unasked-for second element would make
    /// those actions stop working while still reporting success.</para></remarks>
    internal static ViewOpResult ExecuteAction(Topic topic, string actionName, JSC.JSValue args) {
      if(topic == null) {
        return ViewOpResult.Error("action_target_invalid", "Action target is invalid");
      }
      JSC.JSValue action;
      if(string.IsNullOrWhiteSpace(actionName) || !MenuBuilder.ResolveActionDescriptor(topic, actionName, out action)) {
        return ViewOpResult.Error("action_not_found", "Action not found: " + (actionName ?? "<null>"));
      }
      JSC.JSValue[] rpcArgs = args != null && args.Defined
        ? new JSC.JSValue[] { new JSL.String(topic.path), args }
        : new JSC.JSValue[] { new JSL.String(topic.path) };
      return PendingRpc.Begin(actionName, rpcArgs);
    }

    private static ViewOpResult ExecuteAdd(Topic topic, string key, JSC.JSValue args, Topic prim) {
      if(topic == null) {
        return ViewOpResult.Error("add_target_invalid", "Add target is invalid");
      }
      Dictionary<string, MenuBuilder.AddActionEntry> actions = MenuBuilder.ResolveAddActions(topic);
      MenuBuilder.AddActionEntry entry;
      if(string.IsNullOrWhiteSpace(key) || actions == null || !actions.TryGetValue(key, out entry)) {
        return ViewOpResult.Error("add_action_not_found", "Add action not found: " + (key ?? "<null>"));
      }
      JSC.JSValue action = entry.Action;
      if(MenuBuilder.ResourceBusy(topic, actions, key, action)) {
        return ViewOpResult.Error("add_resource_busy", "Required resource is already used: " + key);
      }

      bool willful = action.AsBool("willful", false);
      string name = willful ? args.AsString("name", null) : key;
      if(!IsValidChildName(name)) {
        return ViewOpResult.Error(willful ? "add_name_required" : "add_name_invalid", "Invalid child name: " + (name ?? "<null>"));
      }
      if(topic.Get(name, false) != null) {
        return ViewOpResult.Error("add_target_exists", "Topic already exists: " + topic.path + "/" + name);
      }

      JSC.JSValue state = action["default"];
      JSC.JSValue manifest = action["manifest"];
      Topic child = Topic.I.Get(topic, name, true, prim, false, false);
      Topic.I.Fill(
        child,
        state != null && state.Defined ? JsLib.Clone(state) : JSC.JSValue.Null,
        manifest.IsObject() ? JsLib.Clone(manifest) : null,
        prim);
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
