///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;

namespace X13.WebUI.Helpers {
  internal static class TopicDisplayValueFormatter {
    private const int MaxDisplaySummaryItems = 7;
    private const int MaxDisplayLength = 80;

    public static string Format(JSC.JSValue state) {
      if(state.IsNullOrUndefined()) return string.Empty;
      if(state.IsObject()) {
        return X13.JsExtLib.IsArray(state) ? FormatArraySummary(state) : FormatObjectSummary(state);
      }
      return TrimDisplay(state.ToString());
    }

    // Keys are sorted before truncating to a display subset - JS object property
    // enumeration order isn't guaranteed stable across rebuilds of the same logical
    // value (e.g. JsLib.SetField always reconstructs the object from scratch on every
    // single-field write), so an unsorted summary could read as "changed" between two
    // calls that differ only in enumeration order, not content - causing spurious
    // evnt.upd pushes for any row using the Default editor's summary text.
    private static string FormatObjectSummary(JSC.JSValue value) {
      List<string> names = new List<string>();
      foreach(var kv in value) names.Add(kv.Key);
      if(names.Count == 0) return "{}";
      names.Sort(StringComparer.Ordinal);
      int total = names.Count;
      if(names.Count > MaxDisplaySummaryItems) names.RemoveRange(MaxDisplaySummaryItems, names.Count - MaxDisplaySummaryItems);
      string suffix = total > names.Count ? ", …" : string.Empty;
      return TrimDisplay("{ " + string.Join(", ", names.ToArray()) + suffix + " }");
    }

    private static string FormatArraySummary(JSC.JSValue value) {
      List<string> items = new List<string>();
      int total = 0;
      bool scalarOnly = true;
      foreach(var kv in value) {
        total++;
        if(items.Count < MaxDisplaySummaryItems) {
          string item = FormatArrayItem(kv.Value);
          if(item == null) scalarOnly = false;
          else items.Add(item);
        } else if(!IsScalarDisplayValue(kv.Value)) {
          scalarOnly = false;
        }
      }
      if(total == 0) return "[]";
      if(!scalarOnly || items.Count == 0) return "[" + total.ToString() + "]";
      string suffix = total > items.Count ? ", …" : string.Empty;
      return TrimDisplay("[ " + string.Join(", ", items.ToArray()) + suffix + " ]");
    }

    private static string FormatArrayItem(JSC.JSValue value) {
      if(!IsScalarDisplayValue(value)) return null;
      if(value.IsNullOrUndefined()) return "null";
      if(value.Is<string>()) return "\"" + TrimDisplay(value.AsString(string.Empty)) + "\"";
      return TrimDisplay(value.ToString());
    }

    private static bool IsScalarDisplayValue(JSC.JSValue value) {
      if(value.IsNullOrUndefined()) return true;
      return !value.IsObject();
    }

    private static string TrimDisplay(string text) {
      if(string.IsNullOrEmpty(text) || text.Length <= MaxDisplayLength) return text ?? string.Empty;
      return text.Substring(0, MaxDisplayLength - 1) + "…";
    }
  }
}
