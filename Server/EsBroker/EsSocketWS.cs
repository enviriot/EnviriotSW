///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
using JST = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using X13.Repository;


namespace X13.EsBroker {
  internal class EsSocketWS : WebSocketBehavior, IEsSocket {
    #region static
    private static WebSocketSessionManager _wsMan;
    private static Topic _verbose;
    private static Action<Func<Action<EsMessage>, IEsSocket>> _onConnect;
    private static HttpServer _sv;

    static EsSocketWS() {
      _ = new Timer(PingF, null, 270000, 300000);
    }
    public static void Start(int port, Topic verbose, Action<Func<Action<EsMessage>, IEsSocket>> onConnect) {
      _verbose = verbose;
      _onConnect = onConnect;
      _sv = new HttpServer(port);
      _sv.Log.Output = (d, f) => { if (Verbose) { X13.Log.Debug("WebIDE({0}) - {1}", d.Level, d.Message); } };
#if DEBUG
      _sv.Log.Level = WebSocketSharp.LogLevel.Trace;
#endif
      _sv.RootPath = Path.GetFullPath(Path.GetFullPath("../ide"));
      if (!Directory.Exists(_sv.RootPath)) {
        Directory.CreateDirectory(_sv.RootPath);
      }
      _sv.OnGet += OnGet;
      _sv.AddWebSocketService<EsSocketWS>("/ide/v01", ConnectWS);
      _sv.Start();
      if (_sv.IsListening) {
        X13.Log.Info("WebIDE started on {0}:{1} ", Environment.MachineName, _sv.Port.ToString());
      } else {
        X13.Log.Error("WebIDE start failed");
      }

    }
    public static void Stop() {
      _sv?.Stop();
    }
    private static EsSocketWS ConnectWS() {
      return new EsSocketWS();
    }
    private static void PingF(object o) {
      _wsMan?.Broadping();
    }
    private static void OnGet(object sender, HttpRequestEventArgs e) {
      var req = e.Request;
      var res = e.Response;
      if (req.RemoteEndPoint == null) {
        res.StatusCode = (int)HttpStatusCode.NotAcceptable;
        return;
      }
      System.Net.IPEndPoint remoteEndPoint = req.RemoteEndPoint;
      {
        System.Net.IPAddress remIP;
        if (req.Headers.Contains("X-Real-IP") && System.Net.IPAddress.TryParse(req.Headers["X-Real-IP"], out remIP)) {
          remoteEndPoint = new System.Net.IPEndPoint(remIP, remoteEndPoint.Port);
        }
      }
      //Session ses = (req.Cookies["sessionId"] != null) ? Session.Get(req.Cookies["sessionId"].Value, remoteEndPoint, false) : null;
      //string client = (ses != null && ses.owner != null) ? ses.owner.name : remoteEndPoint.Address.ToString();
      string client = remoteEndPoint.Address.ToString();

      try {
        {
          string path = req.RawUrl == "/" ? "/index.html" : req.RawUrl;
          FileInfo f = new FileInfo(Path.Combine(_sv.RootPath, path.Substring(1)));
          if (f.Exists) {
            string eTag = f.LastWriteTimeUtc.Ticks.ToString("X8") + "-" + f.Length.ToString("X4");
            res.ContentType = Ext2ContentType(f.Extension);
            using (var fs = f.OpenRead()) {
              fs.CopyTo(res.OutputStream);
              res.ContentLength64 = fs.Length;
            }
            res.StatusCode = (int)HttpStatusCode.OK;
          } else {
            res.StatusCode = (int)HttpStatusCode.NotFound;
            res.WriteContent(Encoding.UTF8.GetBytes("404 Not found"));
          }
        }
        if (Verbose) {
          X13.Log.Debug("{0} [{1}]{2} - {3}", client, req.HttpMethod, req.RawUrl, (HttpStatusCode)res.StatusCode);
        }
      }
      catch (Exception ex) {
        res.StatusCode = (int)HttpStatusCode.BadRequest;
        res.WriteContent(Encoding.UTF8.GetBytes("400 Bad Request"));
        if (Verbose) {
          X13.Log.Debug("{0} [{1}]{2} - {3}", client, req.HttpMethod, req.RawUrl, ex.Message);
        }
      }
    }
    private static string Ext2ContentType(string ext) {
      switch (ext) {
        case ".jpg":
        case ".jpeg":
          return "image/jpeg";
        case ".png":
          return "image/png";
        case ".css":
          return "text/css; charset=utf-8";
        case ".csv":
          return "text/csv; charset=utf-8";
        case ".htm":
        case ".html":
          return "text/html; charset=utf-8";
        case ".js":
        case ".mjs":
          return "application/javascript; charset=utf-8";
        case ".ico":
          return "image/x-icon";
      }
      return "application/octet-stream";
    }
    private static bool Verbose {
      get {
        return _verbose != null && _verbose.GetState().As<bool>();
      }
    }
    #endregion static

    private Action<EsMessage> _callback;
    private System.Net.IPEndPoint _remIPE;


    #region  WebSocketBehavior Members
    protected override void OnOpen() {
      if (_wsMan == null) {
        _wsMan = Sessions;
      }
      _remIPE = Context.UserEndPoint;
      if (Context.Headers.Contains("X-Real-IP") && System.Net.IPAddress.TryParse(Context.Headers["X-Real-IP"], out System.Net.IPAddress remIP)) {
        _remIPE = new System.Net.IPEndPoint(remIP, _remIPE.Port);
      }
      _onConnect(SetCB);
    }
    protected override void OnMessage(MessageEventArgs e) {
      if (e.IsText) {
        if (Verbose) {
          X13.Log.Debug("{0}.Rcv({1})", this.ToString(), e.Data);
        }
        if (JsLib.ParseJson(e.Data) is JST.Array mj && mj.Count() > 0) {
          _callback(new EsMessage(this, mj));
        }
      }
    }
    protected override void OnClose(CloseEventArgs e) {
      if (Verbose) {
        X13.Log.Debug("{0}.onClose({1}){2}", this.ToString(), e.Reason, e.WasClean ? " - clean" : string.Empty);
      }
      this.Dispose();
    }
    #endregion  WebSocketBehavior Members

    #region IEsSocket Members
    public System.Net.IPEndPoint RemoteEndPoint { get { return _remIPE; } }

    public void SendArr(NiL.JS.BaseLibrary.Array arr, bool rep = true) {
      if (base.State == WebSocketState.Open) {
        var ms = JsLib.Stringify(arr);
        Send(ms);
        if (Verbose && rep) {
          X13.Log.Debug("{0}.Send({1})", this.ToString(), ms);
        }
      }
    }

    private IEsSocket SetCB(Action<EsMessage> cb) {
      _callback = cb;
      return this;
    }
    #endregion IEsSocket Members

    #region IDisposable Member
    public void Dispose() {
    }
    #endregion IDisposable Member

    public override string ToString() {
      return Convert.ToBase64String(_remIPE.Address.GetAddressBytes().Union(BitConverter.GetBytes((ushort)_remIPE.Port)).ToArray()).TrimEnd('=').Replace('/', '*');
    }
  }
}
