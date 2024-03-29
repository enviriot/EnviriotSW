﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using X13.Repository;
using System.Threading;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using System.IO;
using NiL.JS.Extensions;

namespace X13.WebUI {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 9)]
  [ExportMetadata("name", "WebUI")]

  public class WebUI_Pl : IPlugModul {
    internal static int ProcessPublish(string path, string json, Session ses) {
      Topic cur=Topic.root.Get(path, true, ses?.owner);
      if(string.IsNullOrEmpty(json) || json=="null") {                      // Remove
        cur.Remove();
      } else {
        cur.SetState(JsLib.ParseJson(json), ses?.owner);
      }
      return 200;
    }

    private Topic _owner;
    private static Topic _verbose;
    private HttpServer _sv;

    #region IPlugModul Members
    public void Init() {
    }

    public void Start() {
      _owner = Topic.root.Get("/$YS/WebUI");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
      var port = _owner.Get("port");
      int port_i;
      if(!port.GetState().IsNumber || (port_i = (int)port.GetState().Value)<=0 || port_i>65535) {
        port_i = 8080;
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        port.SetState(port_i);
      }
      _sv = new HttpServer(port_i);
      _sv.Log.Output=WsLog;
#if DEBUG
      _sv.Log.Level=WebSocketSharp.LogLevel.Trace;
#endif
      _sv.RootPath=Path.GetFullPath(Path.GetFullPath("../www"));
      if(!Directory.Exists(_sv.RootPath)) {
        Directory.CreateDirectory(_sv.RootPath);
      }
      _sv.OnGet+=OnGet;
      _sv.AddWebSocketService<ApiV04>("/api/v04", () => new ApiV04());
      _sv.Start();
      if(_sv.IsListening) {
        Log.Info("HttpServer started on {0}:{1} ", Environment.MachineName, _sv.Port.ToString());
      } else {
        Log.Error("HttpServer start failed");
      }

    }

    public void Tick() {
    }

    public void Stop() {
      _sv.Stop();
    }

    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/WebUI", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/WebUI", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members

    public static bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }

    private void WsLog(LogData d, string f) {
      if(verbose) {
        Log.Debug("WS({0}) - {1}", d.Level, d.Message);
      }
    }
    private void OnGet(object sender, HttpRequestEventArgs e) {
      var req = e.Request;
      var res = e.Response;
      if(req.RemoteEndPoint==null) {
        res.StatusCode=(int)HttpStatusCode.NotAcceptable;
        return;
      }
      System.Net.IPEndPoint remoteEndPoint = req.RemoteEndPoint;
      {
        System.Net.IPAddress remIP;
        if(req.Headers.Contains("X-Real-IP") && System.Net.IPAddress.TryParse(req.Headers["X-Real-IP"], out remIP)) {
          remoteEndPoint=new System.Net.IPEndPoint(remIP, remoteEndPoint.Port);
        }
      }
      Session ses = (req.Cookies["sessionId"]!=null)?Session.Get(req.Cookies["sessionId"].Value, remoteEndPoint, false):null;
      string client = (ses!=null && ses.owner!=null)?ses.owner.name:remoteEndPoint.Address.ToString();

      try {
        if(req.Url.LocalPath=="/api/arch04") {
          var args = req.QueryString;
          
          var topics = JsLib.ParseJson(args["p"]).Select(kv => kv.Value.As<string>()).ToArray();
          var begin = (JsLib.ParseJson(args["b"]).Value as JSL.Date).ToDateTime();
          var count = args.Contains("c")?JsLib.ParseJson(args["c"]).As<int>():0;
          var end = args.Contains("e")?(JsLib.ParseJson(args["e"]).Value as JSL.Date).ToDateTime():DateTime.MinValue;
          var resp = JsExtLib.AQuery(topics, begin, count, end);
          res.Headers.Add("Cache-Control", "no-store");
          res.ContentType="application/json; charset=utf-8";
          res.StatusCode = (int)HttpStatusCode.OK;
          var json = JsLib.Stringify(resp);
          res.WriteContent(Encoding.UTF8.GetBytes(json));
        } else {
          string path=req.RawUrl=="/"?"/index.html":req.RawUrl;
          FileInfo f = new FileInfo(Path.Combine(_sv.RootPath, path.Substring(1)));
          if(f.Exists) {
            string eTag = f.LastWriteTimeUtc.Ticks.ToString("X8") + "-" + f.Length.ToString("X4");
            //if (req.Headers.Contains("If-None-Match") && req.Headers["If-None-Match"] == eTag) {
            //  res.StatusCode = (int)HttpStatusCode.NotModified;
            //  res.WriteContent(Encoding.UTF8.GetBytes("Not Modified"));
            //} else {
            //  res.Headers.Add("ETag", eTag);
            //  res.Headers.Add("Cache-Control", "no-cache");
              res.ContentType=Ext2ContentType(f.Extension);
              using(var fs = f.OpenRead()) {
                fs.CopyTo(res.OutputStream);
                res.ContentLength64 = fs.Length;
              }
              res.StatusCode = (int)HttpStatusCode.OK;
            //}
          } else {
            res.StatusCode = (int)HttpStatusCode.NotFound;
            res.WriteContent(Encoding.UTF8.GetBytes("404 Not found"));
          }
        }
        if(verbose) {
          Log.Debug("{0} [{1}]{2} - {3}", client, req.HttpMethod, req.RawUrl, (HttpStatusCode)res.StatusCode);
        }
      }
      catch(Exception ex) {
        res.StatusCode = (int)HttpStatusCode.BadRequest;
        res.WriteContent(Encoding.UTF8.GetBytes("400 Bad Request"));
        if(verbose) {
          Log.Debug("{0} [{1}]{2} - {3}", client, req.HttpMethod, req.RawUrl, ex.Message);
        }
      }
    }
    private string Ext2ContentType(string ext) {
      switch(ext) {
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

  }
}
