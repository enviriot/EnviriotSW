///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using WebSocketSharp;
using WebSocketSharp.Server;
using X13.Repository;
using X13.WebUI.Helpers;
using X13.WebUI.Host;
using JSC = NiL.JS.Core;
using WSN = WebSocketSharp.Net;

namespace X13.WebUI {
  internal sealed class WebUiHost : IDisposable {
    private readonly string _staticPath;
    private readonly string _staticPathWithSeparator;
    private readonly Func<bool> _verbose;
    private HttpServer _server;

    public WebUiHost(string staticPath, Func<bool> verbose) {
      _staticPath = Path.GetFullPath(staticPath);
      _staticPathWithSeparator = _staticPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? _staticPath : _staticPath + Path.DirectorySeparatorChar;
      _verbose = verbose ?? (() => false);
    }

    public bool TryStart(int port) {
      if (_server != null) return true;
      try {
        HttpServer server = new HttpServer(IPAddress.Any, port);
        _server = server;
        server.OnGet += OnGet;
        server.OnPost += OnPost;
        server.AddWebSocketService<WSBehavior>("/api/ide", delegate () { return new WSBehavior(_verbose); });
        server.Start();
        Log.Info("WebUI started at http://localhost:{0}/ from {1}", port, _staticPath);
        return true;
      }
      catch (Exception ex) {
        Stop();
        Log.Debug("WebUI port {0} unavailable - {1}", port, ex.Message);
        return false;
      }
    }

    public void Stop() {
      HttpServer server = _server;
      _server = null;
      try {
        server?.Stop();
      }
      catch (Exception ex) {
        Log.Warning("WebUI.Stop - {0}", ex.Message);
      }
    }

    public void Dispose() {
      Stop();
    }

    private bool IsVerbose() {
      return _verbose();
    }

    private void OnGet(object sender, HttpRequestEventArgs e) {
      IPEndPoint remoteEndPoint = ResolveRemoteEndPoint(e.Request);
      string path = e.Request.Url.AbsolutePath;
      if(path == "/") path = "/index.html";

      if(path == "/api/export" || path.StartsWith("/api/export/", StringComparison.Ordinal)) {
        WriteExportResponse(e.Response, path);
      } else if(path.StartsWith(IconResource.ApiIconPrefix)) {
        byte[] iconData = IconResource.TryGetIconContent(path);
        if(iconData != null) {
          e.Response.Headers.Add("Cache-Control", "public, max-age=31536000, immutable");
          WriteResponse(e.Response, iconData, ContentType(path));
        } else {
          WriteResponse(e.Response, HttpStatusCode.NotFound);
        }
      } else {
        string relative = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        string file = Path.GetFullPath(Path.Combine(_staticPath, relative));
        if(file.StartsWith(_staticPathWithSeparator, StringComparison.OrdinalIgnoreCase) && File.Exists(file)) {
          using(FileStream stream = File.OpenRead(file)) WriteResponse(e.Response, stream, ContentType(file));
        } else if(path == "/index.html") {
          WriteRedirect(e.Response, "/ide.html");
        } else {
          WriteResponse(e.Response, HttpStatusCode.NotFound);
        }
      }
      if(IsVerbose()) Log.Debug("{0} GET {1} - {2}", FormatRemoteEndPoint(remoteEndPoint), path, (HttpStatusCode)e.Response.StatusCode);
    }

    private static void WriteExportResponse(WSN.HttpListenerResponse response, string requestPath) {
      string topicPath = ExportTopicPath(requestPath);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        WriteJsonResponse(response, HttpStatusCode.NotFound, false, "export_topic_not_found", "Topic not found: " + topicPath);
        return;
      }

      string fileName = SafeExportFileName(topic.parent == null ? "root" : topic.name) + ".xst";
      response.Headers["Content-Disposition"] = "attachment; filename=\"" + fileName + "\"";
      using(MemoryStream stream = new MemoryStream()) {
        Repo.Export(stream, topic, false);
        stream.Position = 0;
        WriteResponse(response, stream, "application/octet-stream");
      }
     }
 
    private static string ExportTopicPath(string requestPath) {
      const string prefix = "/api/export";
      if(string.IsNullOrEmpty(requestPath) || requestPath.Length <= prefix.Length) return "/";
      return Uri.UnescapeDataString(requestPath.Substring(prefix.Length));
    }


    private static string SafeExportFileName(string value) {
      string fileName = string.IsNullOrWhiteSpace(value) ? "topic" : value;
      foreach(char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
      return fileName;
    }

    private void OnPost(object sender, HttpRequestEventArgs e) {
      IPEndPoint remoteEndPoint = ResolveRemoteEndPoint(e.Request);
      string path = e.Request.Url.AbsolutePath;
      try {
        if(path == "/api/import") {
          ImportUpload upload = ReadImportUpload(e.Request);
          if(upload == null || upload.Data == null) {
            WriteJsonResponse(e.Response, HttpStatusCode.BadRequest, false, "import_file_missing", "Import file is missing");
          } else {
            using(StreamReader reader = new StreamReader(new MemoryStream(upload.Data), Encoding.UTF8, true)) Repo.Import(reader, null);
            WriteJsonResponse(e.Response, HttpStatusCode.OK, true, null, upload.FileName ?? string.Empty);
          }
        } else {
          WriteResponse(e.Response, HttpStatusCode.NotFound);
        }
      }
      catch(Exception ex) {
        Log.Warning("WebUI import failed - {0}", ex.Message);
        WriteJsonResponse(e.Response, HttpStatusCode.InternalServerError, false, "import_failed", ex.Message);
      }
      if(IsVerbose()) Log.Debug("{0} POST {1} - {2}", FormatRemoteEndPoint(remoteEndPoint), path, (HttpStatusCode)e.Response.StatusCode);
    }

    private static ImportUpload ReadImportUpload(WSN.HttpListenerRequest request) {
      string contentType = request.ContentType ?? string.Empty;
      string boundary = MultipartBoundary(contentType);
      if(string.IsNullOrWhiteSpace(boundary)) return null;

      byte[] body;
      using(MemoryStream ms = new MemoryStream()) {
        request.InputStream.CopyTo(ms);
        body = ms.ToArray();
      }

      return ParseMultipartImport(body, boundary);
    }

    private static ImportUpload ParseMultipartImport(byte[] body, string boundary) {
      Encoding latin1 = Encoding.GetEncoding("iso-8859-1");
      string text = latin1.GetString(body ?? new byte[0]);
      string[] parts = text.Split(new string[] { "--" + boundary }, StringSplitOptions.None);
      ImportUpload upload = new ImportUpload();

      foreach(string rawPart in parts) {
        string part = rawPart;
        if(string.IsNullOrWhiteSpace(part) || part.StartsWith("--")) continue;
        if(part.StartsWith("\r\n")) part = part.Substring(2);
        int headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if(headerEnd < 0) continue;

        string headers = part.Substring(0, headerEnd);
        string content = part.Substring(headerEnd + 4);
        if(content.EndsWith("\r\n")) content = content.Substring(0, content.Length - 2);
        if(content.EndsWith("--")) content = content.Substring(0, content.Length - 2);

        if(HeaderParameter(headers, "name") == "file") {
          upload.FileName = HeaderParameter(headers, "filename");
          upload.Data = latin1.GetBytes(content);
        }
      }

      return upload.Data == null ? null : upload;
    }

    private static string MultipartBoundary(string contentType) {
      foreach(string part in contentType.Split(';')) {
        string trimmed = part.Trim();
        if(trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) return trimmed.Substring(9).Trim('"');
      }
      return null;
    }

    private static string HeaderParameter(string headers, string name) {
      string marker = name + "=\"";
      int start = headers.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
      if(start < 0) return null;
      start += marker.Length;
      int end = headers.IndexOf('"', start);
      return end < 0 ? null : headers.Substring(start, end - start);
    }

    private static IPEndPoint ResolveRemoteEndPoint(WSN.HttpListenerRequest req) {
      IPEndPoint remoteEndPoint = req.RemoteEndPoint;
      IPAddress remIP;
      if (remoteEndPoint != null && req.Headers.AllKeys.Contains("X-Real-IP") && IPAddress.TryParse(req.Headers["X-Real-IP"], out remIP)) {
        remoteEndPoint = new IPEndPoint(remIP, remoteEndPoint.Port);
      }
      return remoteEndPoint;
    }

    private static string FormatRemoteEndPoint(IPEndPoint remoteEndPoint) {
      return remoteEndPoint == null ? "unknown" : remoteEndPoint.ToString();
    }

    private static void WriteRedirect(WSN.HttpListenerResponse response, string location) {
      response.StatusCode = (int)HttpStatusCode.MovedPermanently;
      response.Headers["Location"] = location;
      response.ContentLength64 = 0;
      response.OutputStream.Close();
    }

    private static void WriteResponse(WSN.HttpListenerResponse response, Stream body, string contentType) {
      response.StatusCode = (int)HttpStatusCode.OK;
      response.ContentLength64 = body == null ? 0 : body.Length;
      response.ContentType = contentType;
      if (body != null && body.Length > 0) body.CopyTo(response.OutputStream);
      response.OutputStream.Close();
    }

    private static void WriteResponse(WSN.HttpListenerResponse response, byte[] body, string contentType) {
      response.StatusCode = (int)HttpStatusCode.OK;
      response.ContentLength64 = body.Length;
      response.ContentType = contentType;
      response.WriteContent(body);
      response.OutputStream.Close();
    }

    private static void WriteResponse(WSN.HttpListenerResponse response, HttpStatusCode statusCode) {
      byte[] bytes = Encoding.UTF8.GetBytes(statusCode.ToString());
      response.StatusCode = (int)statusCode;
      response.ContentLength64 = bytes.Length;
      if (bytes.Length > 0) response.OutputStream.Write(bytes, 0, bytes.Length);
      response.OutputStream.Close();
    }

    private static void WriteJsonResponse(WSN.HttpListenerResponse response, HttpStatusCode statusCode, bool ok, string code, string message) {
      response.StatusCode = (int)statusCode;
      response.ContentType = "application/json; charset=utf-8";
      string json = "{\"ok\":" + (ok ? "true" : "false")
        + (string.IsNullOrEmpty(code) ? string.Empty : ",\"code\":" + JsonString(code))
        + (message == null ? string.Empty : ",\"message\":" + JsonString(message))
        + "}";
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      response.ContentLength64 = bytes.Length;
      response.OutputStream.Write(bytes, 0, bytes.Length);
      response.OutputStream.Close();
    }

    private static string JsonString(string value) {
      return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string ContentType(string file) {
      string ext = Path.GetExtension(file).ToLowerInvariant();
      if (ext == ".html") return "text/html; charset=utf-8";
      if (ext == ".js") return "text/javascript; charset=utf-8";
      if (ext == ".css") return "text/css; charset=utf-8";
      if (ext == ".png") return "image/png";
      if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
      if (ext == ".gif") return "image/gif";
      if (ext == ".svg") return "image/svg+xml";
      if (ext == ".ico") return "image/x-icon";
      if (ext == ".map") return "application/json";
      return "application/octet-stream";
    }

    private sealed class ImportUpload {
      public string FileName;
      public byte[] Data;
    }

    private sealed class WSBehavior : WebSocketBehavior {
      private static int _nextSessionId;
      private static int _activeSessionCount;

      private readonly Func<bool> _verbose;
      private readonly SortedList<string, Action<JSC.JSValue>> _handlers;
      private ViewSession _viewSession;
      private int _sessionId;
      private IPEndPoint _remoteEndPoint;

      public WSBehavior(Func<bool> verbose) {
        _verbose = verbose ?? (() => false);
        _handlers = new SortedList<string, Action<JSC.JSValue>>(StringComparer.Ordinal);
      }

      protected override void OnOpen() {
        _sessionId = Interlocked.Increment(ref _nextSessionId);
        Interlocked.Increment(ref _activeSessionCount);
        _remoteEndPoint = ResolveRemoteEndPoint();
        _viewSession = new ViewSession(Send);
        _handlers.Clear();
        _handlers[ViewMessageTypes.ReqHello] = _viewSession.HandleHello;
        _handlers[ViewMessageTypes.ReqExpand] = _viewSession.HandleExpand;
        _handlers[ViewMessageTypes.ReqCommit] = _viewSession.HandleCommit;
        _handlers[ViewMessageTypes.ReqMenu] = _viewSession.HandleMenu;
        _handlers[ViewMessageTypes.ReqRpc] = _viewSession.HandleRpc;
        _handlers[ViewMessageTypes.ReqOpen] = _viewSession.HandleOpen;
        X13.Log.Info("WebUI WS#{0} connected from {1}", _sessionId, FormatRemoteEndPoint(_remoteEndPoint));
      }

      protected override void OnMessage(MessageEventArgs e) {
        if (_viewSession == null) return;
        if (_verbose()) X13.Log.Debug("WebUI WS#{0} <= {1}", _sessionId, e.Data);
        Handle(e.Data);
      }

      private void Handle(string text) {
        JSC.JSValue request;
        try {
          request = JsLib.ParseJson(text);
        }
        catch (Exception ex) {
          if (_verbose()) X13.Log.Warning("WebUI WS#{0} bad JSON - {1}", _sessionId, ex.ToString());
          Send(Error(null, "bad_json", ex.Message));
          return;
        }

        string type = JsLib.OfString(request["type"], null);
        Action<JSC.JSValue> handler;
        if (!string.IsNullOrEmpty(type) && _handlers.TryGetValue(type, out handler)) {
          try {
            handler(request);
          }
          catch (Exception ex) {
            if (_verbose()) X13.Log.Warning("WebUI WS#{0} command {1} error - {2}", _sessionId, type, ex.ToString());
            Send(Error(request, "command_error", ex.Message));
          }
        } else {
          if(_verbose()) X13.Log.Warning("WebUI WS#{0} unsupported command - {1}", _sessionId, type ?? "<null>");
          Send(Error(request, "unsupported_command", "Unsupported command: " + (type ?? "<null>")));
        }
      }

      protected override void OnError(WebSocketSharp.ErrorEventArgs e) {
        X13.Log.Warning("WebUI WS#{0} error - {1}", _sessionId, e.Message);
      }

      protected override void OnClose(CloseEventArgs e) {
        X13.Log.Info("WebUI WS#{0} disconnected from {1}: {2} {3}", _sessionId, FormatRemoteEndPoint(_remoteEndPoint), e.Code, e.Reason);
        ViewSession viewSession = _viewSession;
        _viewSession = null;
        _handlers.Clear();
        viewSession?.Dispose();
        if(Interlocked.Decrement(ref _activeSessionCount) <= 0) EditorHelper.InvalidateTypeCache();
      }


      private void Send(JSC.JSObject response) {
        var json = JsLib.Stringify(response);
        if (_verbose()) X13.Log.Debug("WebUI WS#{0} => {1}", _sessionId, json);
        base.Send(json);
      }

      private static JSC.JSObject Error(JSC.JSValue request, string code, string message) {
        JSC.JSObject response = ResponseBase(request, ViewMessageTypes.ProtocolError, false);
        response["code"] = code;
        response["message"] = message;
        return response;
      }

      private static JSC.JSObject ResponseBase(JSC.JSValue request, string type, bool ok) {
        JSC.JSObject response = JSC.JSObject.CreateObject();
        if (request != null && request.ValueType == JSC.JSValueType.Object && request["id"].Defined) response["id"] = request["id"];
        response["type"] = type;
        response["ok"] = ok;
        return response;
      }

      private IPEndPoint ResolveRemoteEndPoint() {
        IPEndPoint remoteEndPoint = Context.UserEndPoint;
        IPAddress remIP;
        if (remoteEndPoint != null && Context.Headers.Contains("X-Real-IP") && IPAddress.TryParse(Context.Headers["X-Real-IP"], out remIP)) {
          remoteEndPoint = new IPEndPoint(remIP, remoteEndPoint.Port);
        }
        return remoteEndPoint;
      }
    }
  }
}
