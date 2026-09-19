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
  /// <summary>Движок скриптов: глобальный контекст NiL.JS, таймеры, XMLHttpRequest, доступ к архиву и объявление config-топиков.</summary>
  /// <remarks>Контекст привязан к потоку, поэтому ActivateEngineOnThisThread зовётся первойстрокой движкового потока: 
  /// функция захватывает контекст в момент компиляции, и без этого скрипт нашёл бы setTimeout и Arch неопределёнными, не упав. 
  /// Пользовательский JS исполняется только на этом потоке.</remarks>
  public static class JsExtLib {
    public static readonly JSC.GlobalContext Context;

    static JsExtLib() {
      _timerCnt = 1;
      Context = new JSC.GlobalContext();
      // Контекст активируется здесь, а также в ActivateEngineOnThisThread: DefineVariable и ProxyValue
      // ниже выполняются внутри этого конструктора и требуют активного контекста.
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

    /// <summary>Назначает вызывающий поток владельцем движка скриптов.</summary>
    /// <remarks>NiL.JS хранит стек активных контекстов в поле [ThreadStatic], поэтому утверждение «контекст
    /// активен» справедливо только для одного потока.
    /// <para>Ошибка не проявляется явно, поэтому активация оформлена отдельным именованным вызовом.
    /// При компиляции Function захватывает Context.CurrentContext, а при его отсутствии использует собственный
    /// DefaultGlobalContext NiL.JS. В этом контексте нет setTimeout, console, File и Arch, поэтому скрипт
    /// не завершается ошибкой, а просто видит их как undefined.</para>
    /// <para>Метод безопасно вызывать независимо от того, выполнялся ли здесь статический конструктор:
    /// в текущем потоке ActivateInCurrentThread сначала деактивирует контекст, а затем активирует его повторно;
    /// в любом другом потоке в стеке нет контекста, который требовалось бы деактивировать.</para></remarks>
    public static void ActivateEngineOnThisThread() {
      Context.ActivateInCurrentThread();
    }

    #region XMLHttpRequest
    [JSI.RequireNewKeyword]
    private class XMLHttpRequest : IDisposable {
      private HttpWebRequest _req;
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
        //_req.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;  // ошибка TrustFailure в Linux
        // нормализуется как в браузере, поэтому open("post", ...) выполняется как POST
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
        if(data != null && _req.Method != "GET" && _req.Method != "HEAD") {  // PUT, PATCH и DELETE также могут содержать тело
          _req.ContentType = _contentType??"application/x-www-form-urlencoded";
          _req.ContentLength = data.Length;
          using(var stream = _req.GetRequestStream()) {
            stream.Write(data, 0, data.Length);
          }
        }
        _req.BeginGetResponse(RespCallback, null);
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
        // Выполняется в потоке ThreadPool: блокирующий ввод-вывод должен выполняться здесь, но изменения
        // readyState ниже вызывают скрипт, поэтому передаются в основной поток тика
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
          // Если сервер вернул ошибочный HTTP-статус, фактический ответ доступен в WebException.Response.
          // При наличии извлекаем код состояния, текст и тело ответа
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
            stText = e.Status.ToString();  // ошибка не связана с HTTP, status остаётся равным 0
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
            readyState = 2;  // заголовки получены; состояние пропускается, если запрос не достиг сервера
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
    // Таймер, callback которого выполняется сейчас: он уже исключён из _timer, поэтому ClearTimeout
    // должен проверять его отдельно, чтобы обработать таймер, отменяющий сам себя
    private static TimerContainer _firing;
    private static long _timerCnt;
    // Защищает _timer и _firing: setTimeout и связанные функции доступны из любого потока
    private static readonly object _timerLock = new object();
    // Действия, переданные основному потоку тика. См. Post
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _completions = new System.Collections.Concurrent.ConcurrentQueue<Action>();

    /// <summary>Ставит действие в очередь для выполнения в основном потоке тика.</summary>
    /// <remarks>Callback скриптов, поступающие из потока ThreadPool, не должны напрямую обращаться к общему
    /// контексту NiL.JS. Вместо этого они передаются через этот метод.</remarks>
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
        interval = 1;  // нулевой период превратил бы интервальный таймер в одноразовый
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
          to = 0;   // setTimeout(f, 0) и отрицательные задержки срабатывают в следующем тике, а не отбрасываются
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
      long idx = (long)(double)oi;
      lock(_timerLock) {
        if(_firing != null && (long)_firing.idx == idx) {
          _firing.cancelled = true;
        }
        TimerContainer t = _timer, tp = null;
        while(t != null) {
          if((long)t.idx == idx) {
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
    /// <summary>Ошибки таймеров скриптов и завершений с тем же ограничением частоты, что и в цикле движка.</summary>
    /// <remarks>Поле статическое, поскольку JsExtLib также статический. Таймер скрипта, выбрасывающий исключение, 
    /// делает это при каждом срабатывании. Это наиболее вероятный источник потока сообщений из трёх.</remarks>
    private static readonly FaultThrottle _faults = new FaultThrottle();
    /// <summary>Максимальная длительность callback скрипта с момента предыдущего запроса, в миллисекундах.</summary>
    /// <remarks>Публикуется в /$YS/Performance/Script. Callback таймера является единственной написанной пользователем
    /// частью прохода цикла движка, поэтому именно он может привести к пропуску периода. Чтение значения сбрасывает его, 
    /// поэтому показатель всегда описывает интервал с момента последней публикации, а не всё время работы.</remarks>
    internal static double TakeMaxCallbackMs() {
      return Interlocked.Exchange(ref _maxCallbackMs, 0);
    }
    private static void Longest(long since) {
      double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
      double was;
      do {
        was = Interlocked.CompareExchange(ref _maxCallbackMs, 0, 0);
        if(ms <= was) {
          return;
        }
      } while(Interlocked.CompareExchange(ref _maxCallbackMs, ms, was) != was);
    }
    private static double _maxCallbackMs;


    internal static void Tick() {
      Action act;
      while(_completions.TryDequeue(out act)) {
        try {
          act();
        }
        catch(Exception ex) {
          _faults.Report(false, "JsExtLib.Tick(completion)", null, ex);
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
          // Исключаем из списка до выполнения callback: callback может добавлять или удалять таймеры,
          // иначе всё ещё связанный контейнер мог бы дважды оказаться в списке
          _timer = cur.next;
          cur.next = null;
          _firing = cur;
        }
        try {
          long cbStart = System.Diagnostics.Stopwatch.GetTimestamp();
          cur.func.Call(cur.func.Context.ThisBind, new JSC.Arguments());
          Longest(cbStart);
        }
        catch(Exception ex) {
          _faults.Report(false, "JsTimer.Tick", null, ex);
        }
        finally {
          // Очистка _firing, чтение cancelled и повторное планирование должны быть одной атомарной операцией,
          // иначе ClearTimeout из другого потока может попасть в промежуток и потеряться
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
      _faults.Flush(now);
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
        Log.onWrite(_ll, "{0}", msg);  // msg является произвольным текстом скрипта, а не строкой формата
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
    /// <summary>Создаёт config-топик с начальным значением, если он отсутствует, и поддерживает <paramref name="apply"/> в актуальном состоянии.</summary>
    /// <param name="owner">Собственный топик плагина, например /$YS/WebUI.</param>
    /// <param name="relativePath">Относительный путь внутри топика. Например, "Static/verbose".</param>
    /// <param name="attr">Атрибуты топика, если он создаётся этим вызовом.</param>
    /// <param name="apply">Вызывается один раз до возврата из метода и затем при каждом последующем изменении.</param>
    /// <param name="defaultValue">Начальное значение устанавливается, только если топик ещё не содержит значение типа <typeparamref name="T"/>.</param>
    /// <returns>Подписка. Вызывающий код владеет ею и должен освободить её при завершении.</returns>
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
      return topic.Subscribe(Repository.SubRec.SubMask.Once | Repository.SubRec.SubMask.Value,
        (p, sub) => apply(sub.setTopic.GetState().As<T>()));
    }
    #endregion Configuration

    #region AQuery
    public static Func<string[], DateTime, int, DateTime, JSL.Array> AQuery { get; set; }
    private static Task<JSL.Array> AQueryJS(JSC.JSValue topicsJS, JSC.JSValue beginJS, int count, JSC.JSValue endJS) {
      var query = AQuery;
      if(query == null) {  // поставщик архива не зарегистрирован, то есть Archivist отключён
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
