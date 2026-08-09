///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using X13.Repository;
using NiL.JS.Extensions;

namespace X13.WebUI.Helpers {
  internal static class EnumHelper {


    public static JSC.JSValue ResolveOptions(Topic topic, string editor) {
      string enumSource = Resolve(topic, editor);
      if(string.IsNullOrWhiteSpace(enumSource)) return null;

      Topic enumTopic = Topic.root.Get(ResolveEnumPath(enumSource), false);
      if(enumTopic == null) return null;

      JSC.JSValue state = enumTopic.GetState();
      if(state == null || state.ValueType != JSC.JSValueType.Object || state.Value == null) return null;

      JSL.Array items = new JSL.Array();
      bool isArray = JSL.Array.isArray(new JSC.Arguments() { state }).As<bool>();
      int index = 0;
      foreach(var kv in state) {
        JSC.JSObject item = NormalizeEnumItem(kv.Key, kv.Value, isArray);
        if(item != null) items[index++] = item;
      }
      return items;
    }

    private static string ResolveEnumPath(string enumSource) {
      string source = enumSource == null ? string.Empty : enumSource.Trim();
      if(source.Length == 0) return "/$YS/TYPES/Enum";
      if(source[0] == '/') return NormalizeTopicPath(source);
      return NormalizeTopicPath("/$YS/TYPES/Enum/" + source);
    }

    private static string NormalizeTopicPath(string path) {
      if(string.IsNullOrWhiteSpace(path)) return "/";
      string normalized = path.Replace('\\', '/').Trim();
      if(!normalized.StartsWith("/")) normalized = "/" + normalized;
      while(normalized.Contains("//")) normalized = normalized.Replace("//", "/");
      if(normalized.Length > 1 && normalized.EndsWith("/")) normalized = normalized.TrimEnd('/');
      return normalized;
    }

    private static JSC.JSObject NormalizeEnumItem(string key, JSC.JSValue descriptor, bool isArray) {
      JSC.JSObject item = JSC.JSObject.CreateObject();
      int numberKey;
      if(isArray && int.TryParse(key, out numberKey)) item["value"] = numberKey;
      else item["value"] = key;

      if(descriptor != null && descriptor.ValueType == JSC.JSValueType.String) {
        item["label"] = JsLib.OfString(descriptor, key);
        return item;
      }

      if(descriptor != null && descriptor.ValueType == JSC.JSValueType.Object && descriptor.Value != null) {
        item["label"] = JsLib.OfString(descriptor["text"], key);
        string title = JsLib.OfString(descriptor["title"], null) ?? JsLib.OfString(descriptor["hint"], null);
        if(!string.IsNullOrWhiteSpace(title)) item["title"] = title;
        if(JsLib.ofBool(descriptor["disabled"], false)) item["disabled"] = true;
        if(descriptor["order"].IsNumber) item["order"] = (double)descriptor["order"];
        string background = JsLib.OfString(descriptor["BG"], null);
        if(!string.IsNullOrWhiteSpace(background)) item["background"] = background;
        return item;
      }

      item["label"] = descriptor == null || !descriptor.Defined ? key : descriptor.ToString();
      return item;
    }

    public static string Resolve(Topic topic, string editor) {
      if(topic == null || editor != "Enum") return null;

      string enumSource = JsLib.OfString(topic.GetField("enum"), null);
      if(!string.IsNullOrWhiteSpace(enumSource)) return enumSource;

      string typePath = JsLib.OfString(topic.GetField("type"), null);
      if(string.IsNullOrWhiteSpace(typePath)) return null;

      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      return JsLib.OfString(typeTopic == null ? null : JsLib.GetField(typeTopic.GetState(), "enum"), null);
    }
  }
}
