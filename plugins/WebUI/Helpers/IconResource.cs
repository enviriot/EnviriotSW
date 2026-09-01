///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Concurrent;
using WebSocketSharp;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal static class IconResource {
    public const string ApiIconPrefix = "/api/icons/";
    private const string SemanticIconPrefix = "/ide/icons/";
    private static readonly ConcurrentDictionary<string, byte[]> _dynamicIcons = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    public static byte[] TryGetIconContent(string apiPath) {
      string key = IconKeyFromApiPath(apiPath);
      return (!string.IsNullOrWhiteSpace(key) && _dynamicIcons.TryGetValue(key, out byte[] data)) ? data : null;
    }
    private static string IconKeyFromApiPath(string apiPath) {
      if (string.IsNullOrWhiteSpace(apiPath) || !apiPath.StartsWith(ApiIconPrefix, StringComparison.OrdinalIgnoreCase)) return null;
      return IconKey(Uri.UnescapeDataString(apiPath.Substring(ApiIconPrefix.Length)));
    }
    private static string IconKey(string path) {
      return string.IsNullOrWhiteSpace(path) ? null : TypeHelper.StripTypeRoot(path).Trim('/');
    }

    /// <summary>The row's icon URL, or null when nothing resolved.</summary>
    /// <remarks>Null rather than a default: which icon an unresolved row draws is a display
    /// question and belongs to the side doing the drawing. view-row.js already answers it twice
    /// over (`row.icon || '/ide/icons/ty_topic.png'`, and again on the img's onerror), so
    /// naming a file here only duplicated the client's own decision - and pinned it, since
    /// changing the client default would then no longer change these rows. Same split
    /// JsonTreeRowHelpers.ResolveMenuIcon has always used for menus.
    /// <para>Every caller handles null: the three in LogramGraphController via
    /// `?? string.Empty`, and StateTreeController/RowProjector by assigning to
    /// ViewRowDto.Icon, which serializes as `row.Icon ?? string.Empty`.</para></remarks>
    public static string Resolve(Topic topic, string editor) {
      if (topic == null) return null;
      string icon = topic.GetField("icon").AsString(null);
      string url = ResolveIconRef(icon, topic.path);
      if (url != null) return url;

      string typePath = topic.GetField("type").AsString(null);
      url = ResolveTypeIcon(typePath);
      if (url != null) return url;

      if (string.IsNullOrWhiteSpace(editor)) editor = topic.GetStateType();
      return SemanticIconUrl(editor);
    }

    private static string ResolveTypeIcon(string typePath) {
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      if (typeTopic == null) return null;
      // GetField, not the raw indexer: a grouping node under /$YS/TYPES exists as a Topic but
      // never gets a state, and Topic.GetState() hands back JSValue.Undefined for it - indexing
      // that throws a JS TypeError which aborted the entire req.expand. EditorHelper and
      // EnumHelper already read type state this way.
      string icon = typeTopic.GetState().Field("icon").AsString(null);
      return ResolveIconRef(icon, TypeHelper.StripTypeRoot(typeTopic.path));
    }

    // An "icon" comes in three forms, and until now this class understood only one of them.
    // The two menu-icon resolvers (JsonTreeRowHelpers, MenuBuilder) have always
    // accepted all three; here anything that was not a data: URI returned null and fell
    // through to the semantic fallback keyed on the *value shape*. For /$YS/TYPES/Core/Boolean
    // and friends that happened to land on the same file, so nothing looked broken, but a
    // type whose icon has no value-shape twin lost it outright: Ext/Connection's
    // "icon":"EsConnection" never reached a row, which is why ty_es.png only ever showed up
    // in the Add menu. Core/Logram is that same case.
    internal static string ResolveIconRef(string icon, string dynamicName) {
      if (string.IsNullOrWhiteSpace(icon)) return null;
      if (IsAllowedIconPath(icon)) return icon;
      if (icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return ResolveIconValue(icon, dynamicName);
      string semantic = SemanticIconFileName(icon);
      return string.IsNullOrWhiteSpace(semantic) ? null : (SemanticIconPrefix + semantic);
    }

    internal static string ResolveIconValue(string icon, string dynamicName) {
      if (string.IsNullOrEmpty(icon) || !icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
      int comma = icon.IndexOf(',');
      if (comma <= 5) {
        X13.Log.Warning("Malformed data: icon URI for {0}", dynamicName);
        return null;
      }
      string header = icon.Substring(5, comma - 5);
      if (header.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0) {
        X13.Log.Warning("Unsupported (non-base64) data: icon URI for {0}", dynamicName);
        return null;
      }
      string contentType = header.Split(';')[0].ToLowerInvariant();
      byte[] data;
      try {
        data = Convert.FromBase64String(icon.Substring(comma + 1));
      }
      catch {
        X13.Log.Error("Failed to decode base64 icon data for {0}", dynamicName);
        return null;
      }
      // IconKey, the same normalizer the lookup side uses: Resolve passes a raw topic.path, which
      // keeps its leading slash, while IconKeyFromApiPath trims one off before looking the entry
      // up - so a topic-level data: icon was stored under "/dev/Node1.png", served as
      // "/api/icons//dev/Node1.png" and missed on every request. Sharing one function makes the
      // two sides impossible to desynchronize. The other callers already pass a trimmed name, and
      // IconKey leaves those unchanged.
      string key = IconKey(dynamicName) + ExtensionFor(contentType);
      if (data == null || data.Length == 0 || string.IsNullOrWhiteSpace(key)) {
        X13.Log.Warning("Empty decoded icon data for {0}", dynamicName);
        return null;
      }
      _dynamicIcons[key] = data;
      return ApiIconPrefix + key;
    }
    /// <summary>True for the only two icon locations a stored "icon" value may point at.</summary>
    /// <remarks>Accepting any string that merely starts with '/' was too broad: "//host/px.png" is
    /// a protocol-relative URL, so a value written into a topic or type turned into an outbound
    /// fetch from every viewer's browser - a per-view beacon leaking their address. Restricting to
    /// the two prefixes the server actually serves also keeps icon values from addressing the rest
    /// of the static tree. Anything else falls through to the semantic fallback.</remarks>
    internal static bool IsAllowedIconPath(string icon) {
      if (string.IsNullOrWhiteSpace(icon)) return false;
      return icon.StartsWith(SemanticIconPrefix, StringComparison.OrdinalIgnoreCase)
          || icon.StartsWith(ApiIconPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtensionFor(string contentType) {
      switch (contentType) {
      case "image/jpeg": return ".jpg";
      case "image/gif": return ".gif";
      case "image/svg+xml": return ".svg";
      case "image/x-icon": return ".ico";
      default: return ".png";
      }
    }
    // Null for a key the semantic table does not know - the caller passes that null on. The
    // table's own "Folder" -> ty_topic.png entry is untouched: that is a mapping, not a default.
    private static string SemanticIconUrl(string key) {
      string file = SemanticIconFileName(key);
      return string.IsNullOrWhiteSpace(file) ? null : SemanticIconPrefix + file;
    }

    internal static string SemanticIconFileName(string key) {
      switch (key) {
      case "Attribute": return "attr.png";
      case "Boolean": return "ty_bool.png";
      case "ByteArray": return "ty_byteArray.png";
      case "Date": return "ty_dt.png";
      case "Double": return "ty_double.png";
      case "Editor": return "ic_editor.png";
      case "EsConnection": return "ty_es.png";
      case "Folder": return "ty_topic.png";
      case "Hexadecimal": return "ed_hex.png";
      case "Integer": return "ty_int.png";
      case "JS": return "ty_js.png";
      case "Logram": return "ty_logram.png";
      case "Null": return "ty_null.png";
      case "Object": return "ty_obj.png";
      case "String": return "ty_str.png";
      case "Time": return "ed_time.png";
      case "Version": return "ty_version.png";
      default: return null;
      }
    }
  }
}
