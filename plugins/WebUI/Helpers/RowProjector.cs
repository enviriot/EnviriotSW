///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal sealed class RowProjector {
    private readonly string _viewName;
    private readonly Topic _rootTopic;
    private readonly Func<string, bool> _isExpanded;

    internal RowProjector(string viewName, Topic rootTopic, Func<string, bool> isExpanded) {
      _viewName = string.IsNullOrEmpty(viewName) ? "workspace" : viewName;
      _rootTopic = rootTopic ?? Topic.root;
      _isExpanded = isExpanded ?? (_ => false);
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
        IsLogram = string.Equals(topic.GetField("type").AsString(null), "Core/Logram", StringComparison.Ordinal),
      };
    }

    internal string TopicVid(Topic topic) {
      return _viewName + "#" + (topic == null ? "/" : topic.path);
    }

    private int TopicLevel(Topic topic) {
      if(topic == null) return 0;
      return Math.Max(0, SegmentCount(topic.path) - SegmentCount(_rootTopic == null ? "/" : _rootTopic.path));
    }

    private static int SegmentCount(string path) {
      if(string.IsNullOrEmpty(path) || path == "/") return 0;
      return path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    internal static JSC.JSValue ToWebStateValue(JSC.JSValue state) {
      X13.ByteArray byteArray;
      if(X13.ByteArray.IsByteArray(state, out byteArray)) {
        return "¤BA" + Convert.ToBase64String(byteArray.GetBytes());
      }
      return state;
    }
  }
}
