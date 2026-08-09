///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Concurrent;
using WebSocketSharp;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
  internal static class IconResource {
    public const string ApiIconPrefix = "/api/icons/";
    private const string SemanticIconPrefix = "/ide_icons/";
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

    public static string Resolve(Topic topic, string editor) {
      if (topic == null) return SemanticIconPrefix + SemanticUrl(null);
      string icon = JsLib.OfString(topic.GetField("icon"), null);
      string url = ResolveIconValue(icon, topic.path);
      if (url != null) return url;

      string typePath = JsLib.OfString(topic.GetField("type"), null);
      url = ResolveTypeIcon(typePath);
      if (url != null) return url;

      if (string.IsNullOrWhiteSpace(editor)) editor = topic.GetStateType();
      return SemanticIconPrefix + SemanticUrl(editor);
    }

    private static string ResolveTypeIcon(string typePath) {
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      if (typeTopic == null) return null;
      string icon = JsLib.OfString(typeTopic.GetState()["icon"], null);
      return ResolveIconValue(icon, TypeHelper.StripTypeRoot(typeTopic.path));
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
      string key = dynamicName + ExtensionFor(contentType);
      if (data == null || data.Length == 0 || string.IsNullOrWhiteSpace(key)) {
        X13.Log.Warning("Empty decoded icon data for {0}", dynamicName);
        return null;
      }
      _dynamicIcons[key] = data;
      return ApiIconPrefix + key;
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
    private static string SemanticUrl(string key) {
      return SemanticIconFileName(key) ?? "ty_topic.png";
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
