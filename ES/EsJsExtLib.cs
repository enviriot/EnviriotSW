using System;
using JSC = NiL.JS.Core;

namespace X13 {
  // Minimal ES-specific JsExtLib providing its own GlobalContext instance.
  // This satisfies ES references to X13.JsExtLib.Context.ProxyValue(...)
  public static class JsExtLib {
    public static readonly JSC.GlobalContext Context;
    static JsExtLib() {
      Context = new JSC.GlobalContext();
      try {
        Context.ActivateInCurrentThread();
      }
      catch {
        // ignore activation failures in design-time or unexpected hosts
      }
    }
  }
}
