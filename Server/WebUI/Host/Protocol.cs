///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;

namespace X13.WebUI.Host {
  internal static class ViewMessageTypes {
    public const string ReqHello = "req.hello";
    public const string ReqExpand = "req.expand";
    public const string ReqCommit = "req.commit";
    public const string ReqMenu = "req.menu";
    public const string ReqRpc = "req.rpc";
    public const string ReqOpen = "req.open";
    public const string ReqClose = "req.close";
    public const string ReqLog = "req.log";
    public const string RespHello = "resp.hello";
    public const string RespExpand = "resp.expand";
    public const string RespCommit = "resp.commit";
    public const string RespMenu = "resp.menu";
    public const string RespRpc = "resp.rpc";
    public const string RespOpen = "resp.open";
    public const string RespClose = "resp.close";
    public const string RespLog = "resp.log";
    public const string ProtocolError = "protocol.error";
    public const string EvntAdd = "evnt.add";
    public const string EvntUpd = "evnt.upd";
    public const string EvntDel = "evnt.del";
    public const string EvntLog = "evnt.log";
  }

  internal sealed class ViewRowDto {
    public string Vid;
    public int Level;
    public int Expander;
    public string Icon;
    public string Name;
    public string Editor;
    public JSC.JSValue Value;
    public bool Readonly;
    public string OptionsKey;
    public JSC.JSValue Options;
    // Hint for the frontend's editor-host "view" mode (currently only "value" is
    // meaningful - see view-row.js) beyond the default vid-prefix rule
    // (inspstate#/inspmanifest# get "value", everything else "row"). Null/empty means
    // "use the default". Set by TopicTreeController's optional resolveEditorView hook -
    // e.g. InspectorChildrenViewProvider uses it to give a DevicePLC document's "src"
    // child topic the multi-line/auto-growing JS editor even though it's a Children row.
    public string EditorView;
    // True when the topic's resolved manifest.type is Core/Logram - lets the
    // frontend decide, synchronously from an already-loaded row (no extra round
    // trip), whether to default a newly opened document to the Logram view and
    // whether to show the Inspector/Logram toggle at all. See LogramViewProvider.
    public bool IsLogram;
  }

  internal enum MenuItemKind {
    Item,
    Separator,
    Toolbar
  }

  internal sealed class MenuItemDto {
    public MenuItemKind Kind;
    public string Cmd;
    public string Text;
    public string Icon;
    public string Hint;
    public bool Enabled;
    public bool Willful;
    // Renders as a checkmark in place of Icon (context-menu.js) - a toggle command's
    // current state (e.g. Logram pin "Trace"), not a submenu/selection indicator.
    public bool Checked;
    public List<MenuItemDto> Children;
  }

  internal sealed class ViewTarget {
    public string TopicPath;
    public string FieldPath;
    public ViewTargetKind TargetKind;
    public ViewRowDto CachedRow;
  }

  internal enum ViewTargetKind {
    Topic,
    State,
    Manifest,
    Children,
    Catalog,
    Logram,
    Action
  }

  internal sealed class ViewOpResult {
    public bool Ok { get; private set; }
    public string ErrorCode { get; private set; }
    public string ErrorMessage { get; private set; }
    public JSC.JSValue Data { get; private set; }
    public string View { get; private set; }
    public string Vid { get; private set; }
    public string Title { get; private set; }

    public static ViewOpResult Success(JSC.JSValue data = null) {
      return new ViewOpResult() {
        Ok = true,
        Data = data,
      };
    }

    public static ViewOpResult Open(string view, string vid, string title, JSC.JSValue data = null) {
      return new ViewOpResult() {
        Ok = true,
        View = view,
        Vid = vid,
        Title = title,
        Data = data,
      };
    }

    public static ViewOpResult Error(string code, string message) {
      return new ViewOpResult() {
        Ok = false,
        ErrorCode = code,
        ErrorMessage = message,
      };
    }
  }


  internal interface IViewProvider : IDisposable {
    bool CanHandle(string vid);
    ViewOpResult TryCreateTarget(string vid);
    ViewOpResult Expand(string vid, bool expand);
    ViewOpResult Commit(string vid, JSC.JSValue value);
    ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items);
    ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args);
    ViewOpResult Open(string vid, string view);
    ViewOpResult Close(string vid);
  }

  internal abstract class ViewProviderBase : IViewProvider {
    public abstract bool CanHandle(string vid);

    public virtual ViewOpResult TryCreateTarget(string vid) {
      return ViewOpResult.Error("view_target_not_supported", "View target is not supported: " + (vid ?? "<null>"));
    }

    public virtual ViewOpResult Expand(string vid, bool expand) {
      return ViewOpResult.Error("view_expand_not_supported", "Expand is not supported for view target: " + (vid ?? "<null>"));
    }

    public virtual ViewOpResult Commit(string vid, JSC.JSValue value) {
      return ViewOpResult.Error("view_commit_not_supported", "Commit is not supported for view target: " + (vid ?? "<null>"));
    }

    public virtual ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      return ViewOpResult.Error("view_menu_not_supported", "Menu is not supported for view target: " + (vid ?? "<null>"));
    }

    public virtual ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      return ViewOpResult.Error("view_rpc_not_supported", "RPC is not supported for view target: " + (vid ?? "<null>"));
    }

    public virtual ViewOpResult Open(string vid, string view) {
      return ViewOpResult.Error("view_open_not_supported", "Open is not supported for view target: " + (vid ?? "<null>"));
    }

    public virtual ViewOpResult Close(string vid) {
      return ViewOpResult.Success();
    }

    public virtual void Dispose() {
    }
  }
  internal sealed class ViewTargetRegistry {
    private readonly object _gate = new object();
    private readonly Dictionary<string, ViewTarget> _targets;

    public ViewTargetRegistry() {
      _targets = new Dictionary<string, ViewTarget>(StringComparer.Ordinal);
    }

    // Mutated from both the WebSocket message thread (req.* handlers) and the
    // engine tick thread (Topic.Subscribe callbacks) - all access goes through _gate.
    public ViewTarget Get(string vid) {
      lock(_gate) {
        ViewTarget target;
        return !string.IsNullOrEmpty(vid) && _targets.TryGetValue(vid, out target) ? target : null;
      }
    }

    public void Add(string vid, ViewTarget target) {
      if(string.IsNullOrEmpty(vid) || target == null) return;
      lock(_gate) {
        _targets[vid] = target;
      }
    }

    public ViewTarget GetOrCreate(string vid, Func<string, ViewTarget> createTarget) {
      if(string.IsNullOrEmpty(vid) || createTarget == null) return null;
      lock(_gate) {
        ViewTarget target;
        if(_targets.TryGetValue(vid, out target)) return target;
        target = createTarget(vid);
        if(target != null) _targets[vid] = target;
        return target;
      }
    }

    public void Remove(string vid) {
      if(string.IsNullOrEmpty(vid)) return;
      lock(_gate) {
        _targets.Remove(vid);
      }
    }

    public void RemoveSubtree(string vid) {
      if(string.IsNullOrEmpty(vid)) return;
      lock(_gate) {
        foreach(string key in new List<string>(_targets.Keys)) {
          if(key == vid || X13.WebUI.Helpers.VidHelper.IsDescendant(vid, key)) _targets.Remove(key);
        }
      }
    }

    public void Clear() {
      lock(_gate) {
        _targets.Clear();
      }
    }
  }

  internal static class ViewProtocolSerializer {
    public static JSC.JSObject RowBase(string type, string vid) {
      JSC.JSObject dto = JSC.JSObject.CreateObject();
      dto["type"] = type;
      dto["vid"] = vid;
      return dto;
    }

    public static JSC.JSObject Del(string vid) {
      return RowBase(ViewMessageTypes.EvntDel, vid);
    }

    public static JSL.Array SerializeMenuItems(IEnumerable<MenuItemDto> items) {
      JSL.Array array = new JSL.Array();
      if(items == null) return array;
      int index = 0;
      foreach(MenuItemDto item in items) array[index++] = SerializeMenuItem(item);
      return array;
    }

    private static JSC.JSObject SerializeMenuItem(MenuItemDto item) {
      JSC.JSObject dto = JSC.JSObject.CreateObject();
      if(item == null) return dto;

      if(item.Kind != MenuItemKind.Item) dto["kind"] = MenuItemKindToString(item.Kind);
      if(!string.IsNullOrEmpty(item.Cmd)) dto["cmd"] = item.Cmd;
      if(!string.IsNullOrEmpty(item.Text)) dto["text"] = item.Text;

      string icon = item.Icon??string.Empty;
      if(!string.IsNullOrEmpty(icon)) dto["icon"] = icon;
      if(!string.IsNullOrEmpty(item.Hint)) dto["hint"] = item.Hint;
      if(!item.Enabled) dto["enabled"] = false;
      if(item.Willful) dto["willful"] = true;
      if(item.Checked) dto["checked"] = true;
      if(item.Children != null && item.Children.Count > 0) dto["children"] = SerializeMenuItems(item.Children);
      return dto;
    }

    private static string MenuItemKindToString(MenuItemKind kind) {
      switch(kind) {
      case MenuItemKind.Separator: return "separator";
      case MenuItemKind.Toolbar: return "toolbar";
      default: return "item";
      }
    }
  }

}
