///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
//using JSF = NiL.JS.Core.Functions;
//using JSI = NiL.JS.Core.Interop;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace X13 {
  public static class JsExtLib {
    public static readonly JSC.Context Context;

    static JsExtLib() {
      _timerCnt = 1;
      Context = new JSC.Context(true);
      Context.DefineVariable("setTimeout").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetTimeout)));
      Context.DefineVariable("setInterval").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetInterval)));
      Context.DefineVariable("clearTimeout").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue>(ClearTimeout)));
      Context.DefineVariable("clearInterval").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue>(ClearTimeout)));
    }

    #region Tick
    private class TimerContainer {
      public JSL.Function func;
      public DateTime to;
      public int interval;
      public TimerContainer next;
      public JSC.Context ctx;
      public double idx;
    }
    private static TimerContainer _timer;
    private static long _timerCnt;
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
    private static JSC.JSValue SetTimeout(JSC.JSValue func, int to) {
      return SetTimer(func, to, -1, null);
    }
    private static JSC.JSValue SetInterval(JSC.JSValue func, int interval) {
      return SetTimer(func, interval, interval, null);
    }

    public static JSC.JSValue SetTimer(JSC.JSValue func, int to, int interval, JSC.Context ctx) {
      JSL.Function f;
      double idx = -1;
      if(((f = func as JSL.Function) != null || (f = func.Value as JSL.Function)!=null) && to>0) {
        idx = Interlocked.Increment(ref _timerCnt);
        Interlocked.CompareExchange(ref _timerCnt, 1, ( (long)1<<52 )-1);
        AddTimer(new TimerContainer { func = f, to = DateTime.Now.AddMilliseconds(to), interval = interval, ctx = ctx, idx=idx });
      }
      return new JSL.Number(idx);
    }
    public static void ClearTimeout(JSC.Context ctx) {
      TimerContainer t=_timer, tp=null;
      while(t != null) {
        if(t.ctx == ctx) {
          if(tp == null) {
            _timer = t.next;
          } else {
            tp.next = t.next;
          }
        } else {
          tp = t;
        }
        t = t.next;
      }
    }
    public static void ClearTimeout(JSC.JSValue oi) {
      if(oi == null || !oi.IsNumber) {
        return;
      }
      var idx = (int)oi;
      TimerContainer t = _timer, tp = null;
      while(t != null) {
        if(t.idx == idx) {
          if(tp == null) {
            _timer = t.next;
          } else {
            tp.next = t.next;
          }
        } else {
          tp = t;
        }
        t = t.next;
      }
    }

    internal static void Tick() {
      var now = DateTime.Now;
      while(_timer != null && _timer.to <= now) {
        TimerContainer cur = _timer;
        try {
          cur.func.Call(cur.func.Context.ThisBind, new JSC.Arguments());
        }
        catch(Exception ex) {
          Log.Warning("JsTimer.Tick - {0}", ex.Message);
        }
        _timer = cur.next;
        if(cur.interval > 0) {
          cur.to = now.AddMilliseconds(cur.interval);
          AddTimer(cur);
        }
      }
    }
    #endregion Tick
  }
}
