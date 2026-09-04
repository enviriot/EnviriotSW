///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;

namespace X13.WebUI {
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
    // The topic's other view, if it has one: "logram" for a Core/Logram topic, "chart" for one
    // the archivist keeps history for, null for an ordinary topic. Tells the breadcrumb bar of
    // the document rooted on this topic which button to offer - and is therefore set on ROOT rows
    // only (see RowProjector.ResolveAltView for why it is not worth carrying on every row).
    //
    // One field rather than a flag per view, because a topic only ever has one alternative to
    // offer and the choice between them belongs on this side: Logram wins, since a Logram topic
    // carries no state of its own and is unlikely to be archived - and if one ever is, its
    // diagram is still the more useful of the two.
    public string AltView;
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

  /// <summary>What the session knows about one row, by the vid it was sent under.</summary>
  /// <remarks>Held no topic path and no field path since todo.md #7. Both were derivations of the
  /// entry's own key - CreateTarget built TopicPath as VidHelper.GetTopicPath(vid) and nothing
  /// ever read FieldPath at all - so storing them bought nothing and gave the registry the
  /// appearance of holding a path that could go stale against a rename. It never could: a copy
  /// of the key cannot diverge from the key. The one place that needs the topic path derives it
  /// from the vid it already has (TopicTreeController.Commit).</remarks>
  internal sealed class ViewTarget {
    /// <summary>Which view registered this entry.</summary>
    /// <remarks>Not redundant even though each controller's CreateTarget hard-codes its own kind:
    /// GetOrCreate hands back an EXISTING entry without the view check CreateTarget performs, so
    /// this is what stops one tree's Commit from acting on an entry another view registered.</remarks>
    public ViewTargetKind TargetKind;
    /// <summary>The row as last sent, for SendUpd to diff against.</summary>
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

      if(!string.IsNullOrEmpty(item.Icon)) dto["icon"] = item.Icon;
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
