///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;

namespace X13.WebUI.Helpers {
  internal static class VidHelper {
    public static string GetView(string vid) {
      string[] parts = Parts(vid);
      return parts.Length > 0 ? parts[0] : string.Empty;
    }

    public static string GetTopicPath(string vid) {
      string[] parts = Parts(vid);
      return parts.Length > 1 ? parts[1] : string.Empty;
    }

    public static string GetFieldPath(string vid) {
      string[] parts = Parts(vid);
      if(parts.Length <= 2) return string.Empty;
      return string.Join("#", parts, 2, parts.Length - 2);
    }

    public static string GetName(string vid) {
      string fieldPath = GetFieldPath(vid);
      if(!string.IsNullOrEmpty(fieldPath)) return LastSegment(fieldPath, '.');
      string topicPath = GetTopicPath(vid);
      if(string.IsNullOrEmpty(topicPath) || topicPath == "/") return string.Empty;
      return LastSegment(topicPath, '/');
    }

    public static string GetParentVid(string vid, string name = null) {
      string view = GetView(vid);
      string topicPath = GetTopicPath(vid);
      string fieldPath = GetFieldPath(vid);
      if(string.IsNullOrEmpty(view)) return string.Empty;
      if(name == null) name = GetName(vid);
      if(!string.IsNullOrEmpty(fieldPath)) {
        string parentField = RemoveTrailingName(fieldPath, name, '.');
        return string.IsNullOrEmpty(parentField) ? view + "#" + topicPath : view + "#" + topicPath + "#" + parentField;
      }
      return view + "#" + ParentTopicPath(topicPath, name);
    }

    public static bool IsDescendant(string parentVid, string candidateVid) {
      if(string.IsNullOrEmpty(parentVid) || string.IsNullOrEmpty(candidateVid) || parentVid == candidateVid) return false;
      string current = candidateVid;
      for(int guard = 0; guard < 256; guard++) {
        string parent = GetParentVid(current);
        if(string.IsNullOrEmpty(parent) || parent == current) return false;
        if(parent == parentVid) return true;
        current = parent;
      }
      return false;
    }

    public static int CompareVid(string a, string b) {
      return string.Compare(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
    }

    private static string[] Parts(string vid) {
      return (vid ?? string.Empty).Split(new char[] { '#' });
    }

    private static string LastSegment(string value, char separator) {
      int index = value.LastIndexOf(separator);
      return index < 0 ? value : value.Substring(index + 1);
    }

    private static string RemoveTrailingName(string value, string name, char separator) {
      if(string.IsNullOrEmpty(value)) return string.Empty;
      string suffix = separator + (name ?? string.Empty);
      if(!string.IsNullOrEmpty(name) && value.EndsWith(suffix, StringComparison.Ordinal)) return value.Substring(0, value.Length - suffix.Length);
      if(!string.IsNullOrEmpty(name) && value == name) return string.Empty;
      int index = value.LastIndexOf(separator);
      return index < 0 ? string.Empty : value.Substring(0, index);
    }

    private static string ParentTopicPath(string topicPath, string name) {
      if(string.IsNullOrEmpty(topicPath) || topicPath == "/") return "/";
      string suffix = "/" + (name ?? string.Empty);
      if(!string.IsNullOrEmpty(name) && topicPath.EndsWith(suffix, StringComparison.Ordinal)) {
        string parent = topicPath.Substring(0, topicPath.Length - suffix.Length);
        return string.IsNullOrEmpty(parent) ? "/" : parent;
      }
      int index = topicPath.LastIndexOf('/');
      return index <= 0 ? "/" : topicPath.Substring(0, index);
    }
  }
}
