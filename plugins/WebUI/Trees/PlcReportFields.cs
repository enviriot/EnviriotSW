using System;
using X13.Plc;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.WebUI {
  internal static class PlcReportFields {
    internal static bool RejectWrite(Topic topic, string path, JSC.JSValue value) {
      path = string.Join(".", (path ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries));
      foreach(string field in new[] { PlcCapabilities.PinsField, PlcCapabilities.UartsField,
        "mi.MQTT-SN.mi.AvailablePins", "mi.MQTT-SN.mi.AvailableUarts" }) {
        if(path == field || path.StartsWith(field + ".", StringComparison.Ordinal)) return true;
        if(path.Length == 0 || field.StartsWith(path + ".", StringComparison.Ordinal)) {
          var before = topic.GetField(field);
          var after = value.Field(path.Length == 0 ? field : field.Substring(path.Length + 1));
          if(!ConstructorSchema.Same(before, after)) return true;
        }
      }
      return false;
    }
  }
}
