///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
//using JSF = NiL.JS.Core.Functions;
using JSI = NiL.JS.Core.Interop;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.IO;

namespace X13 {
  public static class JsExtLib {
    public static readonly JSC.GlobalContext Context;

    static JsExtLib() {
      _timerCnt = 1;
      Context = new JSC.GlobalContext();
      Context.ActivateInCurrentThread();
      Context.DefineVariable("setTimeout").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetTimeout)));
      Context.DefineVariable("setInterval").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetInterval)));
      Context.DefineVariable("setAlarm").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, JSC.JSValue, JSC.JSValue>(SetAlarm)));
      Context.DefineVariable("clearTimeout").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue>(ClearTimeout)));
      Context.DefineVariable("clearInterval").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue>(ClearTimeout)));
      Context.DefineConstructor(typeof(XMLHttpRequest));
      Context.DefineVariable("console").Assign(JSC.JSValue.Marshal(new X13.JsExtLib.Console()));
    }

    #region XMLHttpRequest
    [JSI.RequireNewKeyword]
    private class XMLHttpRequest {
      private HttpWebRequest _req;
      private IAsyncResult _resp_w;
      private HttpWebResponse _resp;
      private string _contentType;
      private int _readyState;

      public XMLHttpRequest() {
        _readyState = 0;
      }
      public void open(string method, string url, bool async=true, string user=null, string password=null) {
        if(!async) {
          throw new NotImplementedException("XMLHttpRequest.open( synchron )");
        }
        _req = (HttpWebRequest)WebRequest.Create(url);
        _req.Method = method;
        _contentType = null;
        readyState = 1;

      }
      public void setRequestHeader(string header, string value) {
        switch(header) {
        case "Content-Type":
          _contentType = value;
          break;
        }
      }
      public void abort() {
        if(_req!=null) {
          _req.Abort();
        }
      }
      public void send(JSC.JSValue value) {
        byte[] data;
        if(value==null) {
          data = null;
        } else if(value.ValueType == JSC.JSValueType.String) {
          string s = value.Value as string;
          data = s!=null?Encoding.UTF8.GetBytes(s):null;
        } else {
          data = null;
        }
        if(_req.Method == "POST" && data!=null) {
          _req.ContentType = _contentType??"application/x-www-form-urlencoded";
          _req.ContentLength = data.Length;
          using(var stream = _req.GetRequestStream()) {
            stream.Write(data, 0, data.Length);
          }
        }
        _resp_w = _req.BeginGetResponse(RespCallback, null);
      }
      public JSL.Function onreadystatechange { get; set; }
      public int readyState {
        get {
          return _readyState;
        }
        private set {
          _readyState = value;
          if(onreadystatechange!=null) {
            try {
              onreadystatechange.Call(null);
            }
            catch(Exception ex) {
              Log.Warning("XMLHttpRequest({0}).onreadystatechange - {1}", _req.RequestUri, ex.Message);
            }
          }

        }
      }
      public string responseText { get; private set; }
      public ushort status { get; private set; }
      public string statusText { get; private set; }

      private void RespCallback(IAsyncResult asynchronousResult) {
        try {
          _resp = (HttpWebResponse)_req.EndGetResponse(asynchronousResult);
          status = (ushort)(int)_resp.StatusCode;
          statusText = _resp.StatusDescription;
          readyState = 2;
          using(var responseStream = _resp.GetResponseStream()) {
            using(var str = new StreamReader(responseStream, Encoding.UTF8)) {
              responseText = str.ReadToEnd();
            }
          }
          readyState = 4;
        }
        catch(WebException e) {
          Log.Warning("XMLHttpRequest({0}) - [{1}] {2}", _req.RequestUri, e.Status, e.Message);
          readyState = 4;
        }
      }
    }
    #endregion XMLHttpRequest

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
      return SetTimer(func, to, 0, null);
    }
    private static JSC.JSValue SetInterval(JSC.JSValue func, int interval) {
      return SetTimer(func, interval, interval, null);
    }
    private static JSC.JSValue SetAlarm(JSC.JSValue func, JSC.JSValue time) {
      var jd = time.Value as JSL.Date;
      if(jd != null) {
        return JsExtLib.SetTimer(func, jd.ToDateTime(), null);
      } else {
        throw new ArgumentException("SetAlarm(, Date)");
      }
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
    public static JSC.JSValue SetTimer(JSC.JSValue func, DateTime time, JSC.Context ctx) {
      JSL.Function f;
      double idx = -1;
      if(((f = func as JSL.Function) != null || (f = func.Value as JSL.Function) != null)) {
        idx = Interlocked.Increment(ref _timerCnt);
        Interlocked.CompareExchange(ref _timerCnt, 1, ((long)1 << 52) - 1);
        var now = DateTime.Now;
        if((time.TimeOfDay-now.TimeOfDay).TotalMilliseconds<1) {
          now=now.AddDays(1);
        }
        AddTimer(new TimerContainer { func = f, to = now.Date.Add(time.TimeOfDay), interval = int.MinValue, ctx = ctx, idx = idx });
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
        } else if(cur.interval == int.MinValue) {
          cur.to = cur.to.AddDays(1);
          AddTimer(cur);
        }
      }
    }
    #endregion Tick

    #region Log
    private class Console : JSL.JSConsole {
      private LogWriter _debug, _info, _warning, _error;

      public Console() {
        _debug = new LogWriter(X13.LogLevel.Debug);
        _info = new LogWriter(X13.LogLevel.Info);
        _warning = new LogWriter(X13.LogLevel.Warning);
        _error = new LogWriter(X13.LogLevel.Error);
      }

      public override TextWriter GetLogger(LogLevel ll) {
        switch(ll) {
        case LogLevel.Error:
          return _error;
        case LogLevel.Warn:
          return _warning;
        case LogLevel.Info:
          return _info;
        }
        return _debug;
      }
    }

    private class LogWriter : TextWriter {
      private LogLevel _ll;
      public LogWriter(LogLevel ll) {
        _ll = ll;
      }
      public override Encoding Encoding { get { return Encoding.UTF8; } }
      public override void WriteLine(string msg) {
        Log.onWrite(_ll, msg);
      }
    }
    #endregion Log
  }
}
