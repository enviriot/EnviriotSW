///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
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
      };
    }

    /// <summary>The one other view this topic offers, or null.</summary>
    /// <remarks>Called only for a tree's ROOT row, not from BuildTopicRow: the only thing that
    /// reads it is the breadcrumb bar of the document rooted there, and the document knows which
    /// topic that is. Putting it on every row instead would mean re-reading Arch.enable and
    /// diffing the result for every row of every tree - the very cost MenuBuilder declines to
    /// pay for its own branch - to answer a question asked once per open document. Navigation
    /// does not need it either: ViewSession.HandleOpenAuto resolves the same thing server-side
    /// and opens the right side in the same round trip, so a row-carried hint would only let
    /// the client duplicate a decision already made for it.
    /// <para>"logram" is the resolved manifest type; "chart" is the same Arch.enable the
    /// archivist itself reads, so a row cannot promise a chart for a topic ArchivistPl ignores.
    /// Logram wins when a topic somehow has both: it carries no state of its own, so an archive
    /// of that state is the less useful of the two - and MenuBuilder still offers Chart from the
    /// context menu either way, so nothing becomes unreachable.</para></remarks>
    internal static string ResolveAltView(Topic topic) {
      if(topic == null) return null;
      if(string.Equals(topic.GetField("type").AsString(null), "Core/Logram", StringComparison.Ordinal)) return "logram";
      return IsArchived(topic) ? "chart" : null;
    }

    /// <summary>Whether the archivist keeps history for this topic.</summary>
    /// <remarks>As&lt;bool&gt;(), not AsBool(false): JS truthiness, the same reading that decides
    /// whether the topic is archived at all (ArchivistPl.SubFunc, ArchRetention.IsOrphan), so
    /// "enable": 1 counts as on here exactly as it does there. Offering a chart for a topic the
    /// archivist ignores would be worse than offering nothing.</remarks>
    internal static bool IsArchived(Topic topic) {
      return topic != null && topic.GetField("Arch.enable").As<bool>();
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
