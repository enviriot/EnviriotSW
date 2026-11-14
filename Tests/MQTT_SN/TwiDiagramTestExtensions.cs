using System.Linq;
using X13.LogramPLC;
using X13.Plc;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  internal static class TwiDiagramTestExtensions {
    internal static LP_Diagram AddTwiMaster(this LP_Diagram diagram, string name, string number, string sda, string scl, object tag = null) {
      var type = Topic.root.Get(LP_Tree.TYPES + "/TwiMaster", false).GetState();
      var schema = ConstructorSchema.Read(type);
      var values = JSC.JSObject.CreateObject();
      values["TwiNr"] = Value(number); values["SdaPin"] = Value(sda); values["SclPin"] = Value(scl);
      diagram.AddBlock(name, type.Field("LPLC.src").AsString(null), new string[0], tag,
        schema.Parameters, new[] { number, sda, scl });
      diagram.Blocks.Last().Configuration = new ConstructorInstance { Name = name, Schema = schema, Values = values };
      return diagram;
    }
    private static JSC.JSValue Value(string text) {
      int n;
      return text == null ? JSC.JSValue.Null : int.TryParse(text, out n) ? (JSC.JSValue)n : (JSC.JSValue)text;
    }
  }
}
