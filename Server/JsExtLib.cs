///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
//using JSF = NiL.JS.Core.Functions;
//using JSI = NiL.JS.Core.Interop;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13 {
  public static class JsExtLib {
    public static readonly JSC.Context Context;
    public static readonly Func<string, JSC.JSValue> CreateFunc;

    static JsExtLib() {
      Context = new JSC.Context(true);
      CreateFunc = (Context.Eval("Function('src', 'return Function(\"value\", src);')") as JSL.Function).MakeDelegate(typeof(Func<string, JSC.JSValue>)) as Func<string, JSC.JSValue>;
      Context.DefineVariable("setTimeout").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue, int>(SetTimeout)));
    }

    #region Tick
    private class TimerContainer {
      public JSL.Function func;
      public DateTime to;
      public int interval;
      public TimerContainer next;
    }
    private static TimerContainer _timer;
    private static void AddTimer(TimerContainer tc) {
      TimerContainer cur = _timer, prev = null;
      while(cur != null && cur.to < tc.to) {
        prev = cur;
        cur = prev.next;
      }
      tc.next = cur;
      if(prev == null) {
        _timer = tc;
      } else {
        prev.next = tc;
      }
    }
    private static void SetTimeout(JSC.JSValue func, int to) {
      JSL.Function f;
      if(((f = func as JSL.Function) != null || (f = func.Value as JSL.Function)!=null) && to>0) {
        AddTimer(new TimerContainer { func = f, to = DateTime.Now.AddMilliseconds(to), interval = -1 });
      }
    }

    internal static void Tick() {
      var now = DateTime.Now;
      while(_timer != null && _timer.to > now) {
        TimerContainer cur = _timer;
        try {
          cur.func.Call(cur.func.Context.ThisBind, new JSC.Arguments());
        }
        catch(Exception ex) {
          Log.Warning("JsTimer.Tick - {0}", ex.Message);
        }
        _timer = cur.next;
        if(cur.interval > 0) {
          AddTimer(cur);
        }
      }
    }
    #endregion Tick
  }
}
