///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System.Collections.Concurrent;
using X13.Repository;

namespace X13.WebUI.Helpers {
  internal static class EditorHelper {
    private static readonly ConcurrentDictionary<string, string> _typeEditorCache = new ConcurrentDictionary<string, string>();

    public static string Resolve(Topic topic) {
      if(topic == null) return null;
      string editor = topic.GetField("editor").AsString(null);
      if(!string.IsNullOrWhiteSpace(editor)) return editor;

      string typePath = topic.GetField("type").AsString(null);
      if(!string.IsNullOrWhiteSpace(typePath)) {
        editor = _typeEditorCache.GetOrAdd(typePath, ResolveFromType);
        if(!string.IsNullOrWhiteSpace(editor)) return editor;
      }

      return StateType2Editor(topic.GetStateType());
    }

    public static void InvalidateTypeCache(string typePath = null) {
      if(string.IsNullOrWhiteSpace(typePath)) {
        _typeEditorCache.Clear();
      } else {
        _typeEditorCache.TryRemove(typePath, out _);
      }
    }

    private static string ResolveFromType(string typePath) {
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      return typeTopic == null ? null : typeTopic.GetState().AsString("editor", null);
    }

    private static string StateType2Editor(string editor) {
      return editor != "Object" ? editor : null;
    }
  }
}
