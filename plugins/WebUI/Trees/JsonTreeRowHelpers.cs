///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using System;
using System.Linq;
using X13.Repository;

namespace X13.WebUI {
  // Pure JSValue/string helpers shared by StateTreeController and ManifestTreeController - both
  // walk one topic's JSON value as a recursive key tree (state vs. manifest respectively), and
  // everything here is agnostic to which one it's called from. Extracted out of
  // StateTreeController (the first of the two to exist) verbatim, with the root topic's path
  // taken as an explicit parameter instead of closing over an instance field.
  internal static class JsonTreeRowHelpers {
    internal static string ResolveEditorName(JSC.JSValue manifest, JSC.JSValue value) {
      string explicitEditor = manifest.AsString("editor", null);
      if(!string.IsNullOrWhiteSpace(explicitEditor)) return explicitEditor;
      return Topic.JsValueTypeName(value);
    }

    internal static string ResolvedEditorOrDefault(string editor) {
      return string.IsNullOrWhiteSpace(editor) || editor == "Object" ? "Default" : editor;
    }

    // Returns null when nothing resolved, and that is the answer the client wants: view-row.js
    // already substitutes the same default twice over (`row.icon || '/ide_icons/ty_topic.png'`
    // at :158, and again on the img's onerror at :263), so naming the file here only duplicated
    // the client's own decision on the server. Which default an unresolved row draws is a
    // display question and belongs to the side doing the drawing - the same split
    // ResolveMenuIcon below already follows for menus, where null means "no icon at all".
    // Serialization turns null into "" (JsonTreeControllerBase.cs:226), which is exactly what
    // that `||` is looking for.
    internal static string ResolveRowIcon(JSC.JSValue manifest, JSC.JSValue value, string resolvedEditor, string rootTopicPath, string fieldPath) {
      string explicitIcon = manifest.AsString("icon", null);
      string fallbackKey = IsEmptyObject(value) ? "Null" : (resolvedEditor == "Default" ? Topic.JsValueTypeName(value) : resolvedEditor);
      return ResolveMenuIcon(explicitIcon, fallbackKey, rootTopicPath, fieldPath);
    }

    internal static string ResolveMenuIcon(string icon, string fallbackKey, string rootTopicPath, string fieldPath) {
      if(!string.IsNullOrWhiteSpace(icon)) {
        if(IconResource.IsAllowedIconPath(icon)) return icon;
        if(icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) {
          string resolved = IconResource.ResolveIconValue(icon, DynamicIconName(rootTopicPath, fieldPath, fallbackKey));
          if(resolved != null) return resolved;
        } else {
          string semantic = IconResource.SemanticIconFileName(icon);
          if(!string.IsNullOrWhiteSpace(semantic)) return "/ide_icons/" + semantic;
        }
      }
      string fallback = IconResource.SemanticIconFileName(fallbackKey);
      return string.IsNullOrWhiteSpace(fallback) ? null : ("/ide_icons/" + fallback);
    }

    internal static string DynamicIconName(string rootTopicPath, string fieldPath, string fallbackKey) {
      string prefix = (rootTopicPath ?? string.Empty).Trim('/');
      if(!string.IsNullOrEmpty(fieldPath)) prefix += "/" + fieldPath.Replace('.', '/');
      return string.IsNullOrEmpty(prefix) ? (fallbackKey ?? string.Empty) : (prefix + "/" + fallbackKey);
    }

    internal static bool HasFields(JSC.JSValue value) {
      return value.IsObject() && value.Any();
    }

    internal static bool IsEmptyObject(JSC.JSValue value) {
      // JSValue.IsNull is exactly this pair (_valueType >= Object && _oValue == null) - the
      // hand-written form said the same thing in three terms.
      return value != null && value.IsNull;
    }

    internal static int LevelOf(string fieldPath) {
      return string.IsNullOrEmpty(fieldPath) ? 0 : fieldPath.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    internal static string LastSegment(string fieldPath) {
      int index = fieldPath.LastIndexOf('.');
      return index < 0 ? fieldPath : fieldPath.Substring(index + 1);
    }

    internal static bool StringEquals(string left, string right) {
      return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    internal static bool JsValueEquals(JSC.JSValue left, JSC.JSValue right) {
      if(left == null || !left.Defined) return right == null || !right.Defined;
      if(right == null || !right.Defined) return false;
      if(object.ReferenceEquals(left, right)) return true;
      return left.Equals(right);
    }

    // A row's "value" counts as the client's default (view-store.js's normalizeRow()
    // falls back to '') when it's absent or an empty string - used by SendAdd to skip
    // the field on evnt.add rows instead of sending an empty value every time.
    internal static bool IsDefaultValue(JSC.JSValue value) {
      if(value.IsNullOrUndefined()) return true;
      return value.Is<string>() && string.IsNullOrEmpty(value.AsString(null));
    }
  }
}
