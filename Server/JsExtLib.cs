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
using NiL.JS.Extensions;
using System.Threading.Tasks;

namespace X13 {
  public static class JsExtLib {
    public static readonly JSC.GlobalContext Context;

    static JsExtLib() {
      _timerCnt = 1;
      Context = new JSC.GlobalContext();
      // Activated here as well as in ActivateEngineOnThisThread: DefineVariable and ProxyValue
      // below run inside this constructor and want a live context. Which thread that turns out to
      // be is not decided here - see ActivateEngineOnThisThread.
      Context.ActivateInCurrentThread();
      Context.DefineVariable("setTimeout").Assign(Context.ProxyValue(new Func<JSC.JSValue, int, JSC.JSValue>(SetTimeout)));
      Context.DefineVariable("setInterval").Assign(Context.ProxyValue(new Func<JSC.JSValue, int, JSC.JSValue>(SetInterval)));
      Context.DefineVariable("setAlarm").Assign(Context.ProxyValue(new Func<JSC.JSValue, JSC.JSValue, JSC.JSValue>(SetAlarm)));
      Context.DefineVariable("clearTimeout").Assign(Context.ProxyValue(new Action<JSC.JSValue>(ClearTimeout)));
      Context.DefineVariable("clearInterval").Assign(Context.ProxyValue(new Action<JSC.JSValue>(ClearTimeout)));
      Context.DefineConstructor(typeof(XMLHttpRequest));
      Context.DefineVariable("console").Assign(Context.ProxyValue(new X13.JsExtLib.Console()));
      var fs = JSC.JSObject.CreateObject();
      fs["AppendText"] = Context.ProxyValue(new Action<string, string>(AppendFile));
      Context.DefineVariable("File").Assign(fs);
      var arch= JSC.JSObject.CreateObject();
      arch["Query"] = Context.ProxyValue(new Func<JSC.JSValue, JSC.JSValue, int, JSC.JSValue, Task<JSL.Array>>(AQueryJS));
      Context.DefineVariable("Arch").Assign(arch);
    }

    /// <summary>Makes the calling thread the one the script engine belongs to.</summary>
    /// <remarks>NiL.JS holds the active-context stack in a [ThreadStatic] field, so "the context
    /// is active" is true of one thread and no other. The activation used to happen as a side
    /// effect of the static constructor above, which runs on whichever thread first touches this
    /// class - an ordering nothing declared and nothing checked.
    /// <para>Getting it wrong is silent, which is the reason this exists as a named call. A
    /// Function captures Context.CurrentContext when it is compiled and falls back to NiL.JS's own
    /// DefaultGlobalContext if there is none; that context has no setTimeout, console, File or
    /// Arch, so the script does not fail - it finds them undefined.</para>
    /// <para>Safe to call whether or not the static constructor already ran here: on this thread
    /// ActivateInCurrentThread deactivates the context before re-activating it, and on any other
    /// thread there is nothing on the stack to deactivate.</para></remarks>
    public static void ActivateEngineOnThisThread() {
      Context.ActivateInCurrentThread();
    }

    #region XMLHttpRequest
    [JSI.RequireNewKeyword]
    private class XMLHttpRequest : IDisposable {
      private HttpWebRequest _req;
      //private IAsyncResult _resp_w;
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
        //_req.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;  // TrustFailure on Linux
        // normalized like a browser does, so open("post", ...) behaves as POST
        _req.Method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
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
        _req?.Abort();
      }
      public void send(JSC.JSValue value) {
        byte[] data = (value.Is<string>() && value.Value is string s)? Encoding.UTF8.GetBytes(s) : null;
        if(data != null && _req.Method != "GET" && _req.Method != "HEAD") {  // PUT/PATCH/DELETE carry a body too
          _req.ContentType = _contentType??"application/x-www-form-urlencoded";
          _req.ContentLength = data.Length;
          using(var stream = _req.GetRequestStream()) {
            stream.Write(data, 0, data.Length);
          }
        }
        /*_resp_w = */_req.BeginGetResponse(RespCallback, null);
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

      private static string ReadBody(HttpWebResponse resp) {
        using(var responseStream = resp.GetResponseStream()) {
          if(responseStream == null) {
            return null;
          }
          using(var str = new StreamReader(responseStream, Encoding.UTF8)) {
            return str.ReadToEnd();
          }
        }
      }
      private void RespCallback(IAsyncResult asynchronousResult) {
        // this runs on a ThreadPool thread: the blocking IO belongs here, but the readyState
        // transitions below call into script, so they are handed to the main tick thread
        ushort st = 0;
        string stText = null, body = null;
        try {
          var resp = (HttpWebResponse)_req.EndGetResponse(asynchronousResult);
          _resp = resp;
          st = (ushort)(int)resp.StatusCode;
          stText = resp.StatusDescription;
          body = ReadBody(resp);
        }
        catch(WebException e) {
          Log.Debug("XMLHttpRequest({0}) - [{1}] {2}", _req.RequestUri, e.Status, e.ToString());
          // If server returned an HTTP error status, the real response is available
          // in the WebException.Response. Extract status code/text and body when present
          var errResp = e.Response as HttpWebResponse;
          if(errResp != null) {
            _resp = errResp;
            st = (ushort)(int)errResp.StatusCode;
            stText = errResp.StatusDescription;
            try {
              body = ReadBody(errResp);
            }
            catch(Exception ex2) {
              Log.Debug("XMLHttpRequest({0}) - error reading error response: {1}", _req.RequestUri, ex2.Message);
            }
          } else {
            stText = e.Status.ToString();  // non-HTTP failure, status stays 0
          }
        }
        catch(Exception ex) {
          Log.Warning("XMLHttpRequest({0}) - {1}", _req.RequestUri, ex.Message);
          stText = ex.Message;
        }
        JsExtLib.Post(() => {
          status = st;
          statusText = stText;
          if(st != 0) {
            readyState = 2;  // headers received; skipped when the request never reached a server
          }
          responseText = body;
          readyState = 4;
        });
      }
      #region IDisposable Member
      public void Dispose() {
        Interlocked.Exchange(ref _resp, null)?.Close();
      }
      #endregion IDisposable Member
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
      public bool cancelled;
    }
    private static TimerContainer _timer;
    // the timer whose callback is running right now: it is already unlinked from _timer,
    // so ClearTimeout has to look at it separately to catch a timer clearing itself
    private static TimerContainer _firing;
    private static long _timerCnt;
    // guards _timer/_firing: setTimeout & friends are reachable from any thread
    private static readonly object _timerLock = new object();
    // actions handed over to the main tick thread, see Post
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _completions = new System.Collections.Concurrent.ConcurrentQueue<Action>();

    /// <summary>Queues an action to be executed on the main tick thread.</summary>
    /// <remarks>Script callbacks that originate on a ThreadPool thread must not touch the
    /// shared NiL.JS context directly - they go through here instead.</remarks>
    internal static void Post(Action act) {
      if(act != null) {
        _completions.Enqueue(act);
      }
    }
    private static void AddTimer(TimerContainer tc) {
      lock(_timerLock) {
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
    }
    private static JSC.JSValue SetTimeout(JSC.JSValue func, int to) {
      return SetTimer(func, to, 0, null);
    }
    private static JSC.JSValue SetInterval(JSC.JSValue func, int interval) {
      if(interval < 1) {
        interval = 1;  // a zero period would degrade the interval into a one-shot timer
      }
      return SetTimer(func, interval, interval, null);
    }
    private static JSC.JSValue SetAlarm(JSC.JSValue func, JSC.JSValue time) {
      if(time.Value is JSL.Date jd) {
        return JsExtLib.SetTimer(func, jd.ToDateTime(), null);
      } else {
        throw new ArgumentException("SetAlarm(, Date)");
      }
    }

    public static JSC.JSValue SetTimer(JSC.JSValue func, int to, int interval, JSC.Context ctx) {
      JSL.Function f;
      double idx = -1;
      if((f = func as JSL.Function) != null || (f = func.Value as JSL.Function)!=null) {
        if(to < 0) {
          to = 0;   // setTimeout(f, 0) and negative delays fire on the next tick, they are not dropped
        }
        idx = Interlocked.Increment(ref _timerCnt);
        Interlocked.CompareExchange(ref _timerCnt, 1, ((long)1<<52)-1);
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
      lock(_timerLock) {
        if(_firing != null && _firing.ctx == ctx) {
          _firing.cancelled = true;
        }
        TimerContainer t=_timer, tp=null;
        while(t != null) {
          if(t.ctx == ctx) {
            t.cancelled = true;
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
    }
    public static void ClearTimeout(JSC.JSValue oi) {
      if(oi == null || !oi.IsNumber) {
        return;
      }
      var idx = (int)oi;
      lock(_timerLock) {
        if(_firing != null && (long)_firing.idx == (long)idx) {
          _firing.cancelled = true;
        }
        TimerContainer t = _timer, tp = null;
        while(t != null) {
          if((long)t.idx == (long)idx) {
            t.cancelled = true;
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
    }

    internal static void Tick() {
      Action act;
      while(_completions.TryDequeue(out act)) {
        try {
          act();
        }
        catch(Exception ex) {
          Log.Warning("JsExtLib.Tick(completion) - {0}", ex.Message);
        }
      }

      var now = DateTime.Now;
      while(true) {
        TimerContainer cur;
        lock(_timerLock) {
          if(_timer == null || _timer.to > now) {
            break;
          }
          cur = _timer;
          // unlink before running the callback: the callback may add or clear timers, and a
          // container that is still linked could otherwise end up in the list twice
          _timer = cur.next;
          cur.next = null;
          _firing = cur;
        }
        try {
          cur.func.Call(cur.func.Context.ThisBind, new JSC.Arguments());
        }
        catch(Exception ex) {
          Log.Warning("JsTimer.Tick - {0}", ex.Message);
        }
        finally {
          // clearing _firing, reading cancelled and rescheduling have to be one atomic step,
          // otherwise a ClearTimeout from another thread lands in the gap and is lost
          lock(_timerLock) {
            _firing = null;
            if(!cur.cancelled) {
              if(cur.interval > 0) {
                cur.to = now.AddMilliseconds(cur.interval);
                AddTimer(cur);
              } else if(cur.interval == int.MinValue) {
                cur.to = cur.to.AddDays(1);
                AddTimer(cur);
              }
            }
          }
        }
      }
    }
    #endregion Tick

    #region Filesystem
    private static void AppendFile(string path, string data) {
      if(string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(data)) {
        return;
      }
      try {
        var pp = path.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for(int i = pp.Length-2; i>=0; i--){
          if(pp[i]==".." || pp[i]=="." || pp[i].IndexOf(Path.VolumeSeparatorChar) >=0){
            break;
          }
          sb.Insert(0, Path.DirectorySeparatorChar);
          sb.Insert(0, pp[i]);
        }
        sb.Insert(0, Path.DirectorySeparatorChar);
        sb.Insert(0, Directory.GetParent(Directory.GetCurrentDirectory()).ToString());
        sb.Append(Path.GetFileName(path));
        var p2 = sb.ToString();
        var dir = Path.GetDirectoryName(p2);
        if(!Directory.Exists(dir)) {
          Directory.CreateDirectory(dir);
        }
        File.AppendAllText(p2, data);
      }
      catch(Exception ex) {
        Log.Warning("AppendFile({0}, {1}) - {2}", path, data, ex.Message);
      }
    }
    #endregion Filesystem

    #region Log
    private class Console : JSL.JSConsole, IDisposable {
      private readonly LogWriter _debug, _info, _warning, _error;

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

      public void Dispose() {
        _debug.Dispose();
        _info.Dispose();
        _warning.Dispose();
        _error.Dispose();
      }
    }

    private class LogWriter : TextWriter {
      private readonly LogLevel _ll;
      public LogWriter(LogLevel ll) {
        _ll = ll;
      }
      public override Encoding Encoding { get { return Encoding.UTF8; } }
      public override void WriteLine(string msg) {
        Log.onWrite(_ll, "{0}", msg);  // msg is arbitrary script text, never a format string
      }
    }
    #endregion Log

    public static bool IsArray(JSC.JSValue value) {
      if(!value.IsObject()) {
        return false;
      }
      try {
        return JSL.Array.isArray(new JSC.Arguments() { value }).AsBool(false);
      }
      catch {
        return false;
      }
    }

    #region Configuration
    /// <summary>Seeds a config topic if it is missing, then keeps <paramref name="apply"/> current.</summary>
    /// <param name="owner">The plugin's own topic, e.g. /$YS/WebUI.</param>
    /// <param name="relativePath">Path below it, slashes allowed: "Static/verbose" creates the
    /// group on the way. Intermediate topics need no attributes of their own - Xst.Export keeps
    /// a parent whose children were exported, so a Config leaf carries its groups with it.</param>
    /// <param name="attr">Attributes for the leaf when this call is the one that seeds it.</param>
    /// <param name="apply">Called once before this returns, and again on every later change.</param>
    /// <param name="defaultValue">Seeded only when the topic holds nothing of type
    /// <typeparamref name="T"/> yet - a type test rather than a reader with a fallback, because
    /// a reader cannot tell "not set" from "set to the default" and the topic would never be
    /// created. A null default seeds nothing.</param>
    /// <returns>The subscription. The caller owns it and must dispose it on shutdown.</returns>
    /// <remarks>The immediate call is not redundant with the subscription: Subscribe does not
    /// call back synchronously, it queues a subscribe command that Repo dispatches on its next
    /// tick. A plugin that starts listening the moment its Start() returns would otherwise
    /// answer the first requests from whatever its fields were initialised to.
    ///
    /// apply rather than a `ref T` parameter, which is what this wants to be: a ref cannot be
    /// captured by the subscription's callback (CS1628), so a ref could only ever serve the
    /// first read and nothing would carry the later ones.</remarks>
    public static Repository.SubRec EnsureCfg<T>(Repository.Topic owner, string relativePath,
                                                 Repository.Topic.Attribute attr, Action<T> apply, T defaultValue = default(T)) {
      if(owner == null) throw new ArgumentNullException("owner");
      if(apply == null) throw new ArgumentNullException("apply");
      Repository.Topic topic = owner.Get(relativePath, true);
      T value;
      if(!topic.GetState().Is<T>()) {
        topic.SetAttribute(attr);
        topic.SetState(Context.ProxyValue(defaultValue), owner);
        value = defaultValue;
      } else {
        value = topic.GetState().As<T>(); 
      }
      apply(value);
      // Once is what makes the subscription see the subscribed topic's OWN state rather than
      // only its children's. The value comes from sub.setTopic, not the captured topic, so the
      // callback cannot outrun the assignment a caller might have made.
      return topic.Subscribe(Repository.SubRec.SubMask.Once | Repository.SubRec.SubMask.Value,
        (p, sub) => apply(sub.setTopic.GetState().As<T>()));
    }
    #endregion Configuration

    #region AQuery
    public static Func<string[], DateTime, int, DateTime, JSL.Array> AQuery { get; set; }
    private static Task<JSL.Array> AQueryJS(JSC.JSValue topicsJS, JSC.JSValue beginJS, int count, JSC.JSValue endJS) {
      var query = AQuery;
      if(query == null) {  // no archive provider registered, i.e. PersistentStorage is disabled
        throw new InvalidOperationException("Arch.Query - no archive provider available");
      }
      if(topicsJS == null || !topicsJS.Defined) {
        throw new ArgumentException("Arch.Query(topics, begin, count, end) - topics is required");
      }
      string[] topics;
      if(topicsJS.Is<string>()) {
        topics = new string[1];
        topics[0] = topicsJS.AsString(null);
      } else {
        // JsLib.OfString, not As<string>(): As<string>() coerces, and on JSValue.Null it yields the
        // four-character string "null", which then travelled on as a topic path. Undefined yields
        // C# null instead, so the two empty values are not even symmetrical - a null check on the
        // result would still have let "null" through as data.
        topics = topicsJS.Select(kv => kv.Value.AsString(null)).ToArray();
        if(topics.Any(z => string.IsNullOrEmpty(z))) {
          throw new ArgumentException("Arch.Query(topics, begin, count, end) - every topic must be a non-empty string");
        }
      }
      if(!(beginJS != null && beginJS.Value is JSL.Date beginDate)) {
        throw new ArgumentException("Arch.Query(topics, begin, count, end) - begin must be a Date");
      }
      DateTime begin = beginDate.ToDateTime();
      DateTime end = (endJS!=null && endJS.Is(JSC.JSValueType.Date))?(endJS.Value as JSL.Date).ToDateTime():DateTime.MinValue;
      //Log.Debug("AQuery([{0}], {1:HHmmss}, {2}, {3:HHmmss})", string.Join(", ", topics), begin, count, end);
      return Task.Run(() => query(topics, begin, count, end));
    }
    #endregion AQuery
  }
}
