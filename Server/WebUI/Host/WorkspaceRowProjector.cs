///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal sealed class WorkspaceRowProjector {
    private const string ViewName = "workspace";
    private readonly Func<string, bool> _isExpanded;

    internal WorkspaceRowProjector(Func<string, bool> isExpanded) {
      _isExpanded = isExpanded ?? (_ => false);
    }

    internal ViewRowDto BuildRootRow() {
      Topic root = Topic.root;
      string vid = TopicVid(root);
      return new ViewRowDto() {
        Vid = vid,
        Level = 0,
        Expander = root.HasChildren() ? (_isExpanded(vid) ? 2 : 1) : 0,
        Icon = "/ide_icons/ty_topic.png",
        Name = Environment.MachineName,
        Editor = "ConnectionStatus",
        Value = "connected",
        Readonly = true,
      };
    }

    internal ViewRowDto BuildTopicRow(Topic topic) {
      string editor = EditorHelper.Resolve(topic);
      string resolvedEditor = string.IsNullOrWhiteSpace(editor) ? "Default" : editor;
      string vid = TopicVid(topic);
      JSC.JSValue state = topic.GetState();
      return new ViewRowDto() {
        Vid = vid,
        Level = TopicLevel(topic),
        Expander = topic.HasChildren() ? (_isExpanded(vid) ? 2 : 1) : 0,
        Icon = IconResource.Resolve(topic, editor),
        Name = topic.name,
        Editor = resolvedEditor,
        Value = resolvedEditor == "Default" ? TopicDisplayValueFormatter.Format(state) : ToWebStateValue(state),
        Readonly = topic.CheckAttribute(Topic.Attribute.Readonly),
        OptionsKey = EnumHelper.Resolve(topic, editor),
        Options = EnumHelper.ResolveOptions(topic, editor),
      };
    }

    internal static string TopicVid(Topic topic) {
      return ViewName + "#" + (topic == null ? "/" : topic.path);
    }

    private static int TopicLevel(Topic topic) {
      if(topic == null || topic.path == "/") return 0;
      return topic.path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static JSC.JSValue ToWebStateValue(JSC.JSValue state) {
      X13.ByteArray byteArray;
      if(X13.ByteArray.IsByteArray(state, out byteArray)) {
        return "¤BA" + Convert.ToBase64String(byteArray.GetBytes());
      }
      return state;
    }
  }
}
