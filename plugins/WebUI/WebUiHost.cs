///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using NiL.JS.Extensions;
using WebSocketSharp;
using WebSocketSharp.Server;
using X13.Repository;
using X13.WebUI.Helpers;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using WSN = WebSocketSharp.Net;

namespace X13.WebUI {
  internal sealed class WebUiHost : IDisposable {
    private readonly string _staticPath;
    private readonly string _staticPathWithSeparator;
    // One object rather than the five delegates this used to take. They were all reads of the
    // same configuration, each one a repository walk on a hot path; WebUiConfig holds the
    // current values and keeps them current by subscription, so what arrives here is a field
    // read. It also gives the three verbose flags one place to be explained: they are three
    // different questions - "which files are being fetched", "what is the editor saying",
    // "what is a dashboard saying" - that one switch used to answer all at once.
    private readonly WebUiConfig _config;
    private static WebUiHost _instance;  // WSBehavior is constructed by the server, not by us
    private HttpServer _server;

    public WebUiHost(WebUiConfig config) {
      _config = config;
      _staticPath = Path.GetFullPath(config.StaticPath);
      _staticPathWithSeparator = _staticPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? _staticPath : _staticPath + Path.DirectorySeparatorChar;
      _instance = this;
    }

    /// <summary>The address the access decision is made on.</summary>
    /// <remarks>X-Real-IP is honoured only when the socket peer is itself a configured proxy -
    /// IsInSpec, not IsAllowed, so an empty proxy list trusts nobody (loopback included, or a
    /// local process could forge the header). Behind a real proxy the socket peer is the proxy
    /// and the filter has to apply to the forwarded address, otherwise proxying would bypass
    /// the ACL entirely.</remarks>
    private IPAddress EffectiveClient(IPAddress socketPeer, Func<string, string> header) {
      if(socketPeer == null || !NetworkAcl.IsInSpec(socketPeer, _config.TrustedProxies)) {
        return socketPeer;
      }
      IPAddress forwarded;
      string value = header("X-Real-IP");
      return !string.IsNullOrEmpty(value) && IPAddress.TryParse(value, out forwarded) ? forwarded : socketPeer;
    }

    /// <summary>Access check for one endpoint group.</summary>
    /// <remarks>The network rule guards the IDE and nothing else. The dashboard endpoints share
    /// this port but not this model: /api/dashboard accepts anyone and decides access per topic
    /// (DashboardAcl), and the assets those pages are built from - lib/, components/, img/,
    /// favicon.ico - have to be reachable from wherever a dashboard is opened. Otherwise the
    /// per-topic grant would be pointless, since the page carrying it could not be loaded.</remarks>
    private bool IsAllowed(IPAddress effectiveClient, string path) {
      return !IsIdeSurface(path) || NetworkAcl.IsAllowed(effectiveClient, _config.TrustedNets);
    }

    /// <summary>Everything that belongs to the IDE rather than to a dashboard.</summary>
    /// <remarks>Export and import stay on this side deliberately: they are IDE operations, and
    /// their session token is a second lock rather than a replacement for this one.</remarks>
    internal static bool IsIdeSurface(string path) {
      if(string.IsNullOrEmpty(path)) return true;  // unknown shape: treat as the guarded side
      if(path == "/api/ide" || path == "/api/export" || path == "/api/import"
        || path.StartsWith("/api/export/", StringComparison.Ordinal)
        || path.StartsWith(IconResource.ApiIconPrefix, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
      if(path == "/api/dashboard" || path == "/api/archivist") return false;
      // Static files: the IDE's own are the ones whose first segment starts with "ide" -
      // ide.html, ide_app.js, ide_components/, ide_editors/, ide_icons/, ide_services/.
      string relative = path.TrimStart('/');
      int slash = relative.IndexOf('/');
      string first = slash < 0 ? relative : relative.Substring(0, slash);
      return first.StartsWith("ide", StringComparison.OrdinalIgnoreCase);
    }
    public bool TryStart(int port) {
      if (_server != null) return true;
      try {
        HttpServer server = new HttpServer(IPAddress.Any, port);
        _server = server;
        server.OnGet += OnGet;
        server.OnPost += OnPost;
        server.AddWebSocketService<WSBehavior>("/api/ide", delegate () { return new WSBehavior(() => _config.VerboseIde); });
        server.AddWebSocketService<DashboardBehavior>("/api/dashboard", delegate () { return new DashboardBehavior(() => _config.VerboseDashboard); });
        server.Start();
        // Bound to every interface, not just loopback - say so, and say what limits access.
        // The limit named here is the IDE's; the dashboard is open and gated per topic instead.
        Log.Info("WebUI listening on all interfaces, port {0}, from {1}; IDE access limited to [{2}]", port, _staticPath, _config.TrustedNets);
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
    private bool IsStaticVerbose() {
      return _config.VerboseStatic;
    }
    private void OnGet(object sender, HttpRequestEventArgs e) {
      IPAddress client = EffectiveClient(e.Request.RemoteEndPoint?.Address, n => e.Request.Headers[n]);
      string path = e.Request.Url.AbsolutePath;
      if(path == "/") path = "/index.html";
      if(!IsAllowed(client, path)) {
        Log.Warning("WebUI GET {0} refused from {1}", path, FormatAddress(client));
        WriteResponse(e.Response, HttpStatusCode.Forbidden);
        return;
      }
      IPEndPoint remoteEndPoint = new IPEndPoint(client ?? IPAddress.None, e.Request.RemoteEndPoint == null ? 0 : e.Request.RemoteEndPoint.Port);
      try {
        ServeGet(e, path);
      }
      catch(Exception ex) {
        // OnPost has always guarded its body; without the same here an export failure escapes
        // into websocket-sharp's dispatch instead of becoming a 500.
        Exception cause = Innermost(ex);
        if(IsClientGone(ex)) {
          // Not a failure, and not ours to report as one: the caller hung up while the answer
          // was being written. /api/archivist sees this constantly and by design - a chart
          // abandons a request the moment a pan supersedes it (chart-document.js #query), and
          // the window it asked for is one the user has already left. Logged as a warning it
          // was pure noise, and noise on the same line that has to carry real failures.
          Log.Debug("WebUI GET {0} abandoned by {1} - {2}", DescribeRequest(e.Request, path), FormatAddress(client), cause.Message);
        } else {
          Log.Warning("WebUI GET {0} from {1} failed - {2}: {3}", DescribeRequest(e.Request, path), FormatAddress(client), cause.GetType().Name, cause.Message);
        }
        TryWriteStatus(e.Response, HttpStatusCode.InternalServerError);
      }
      if(IsStaticVerbose()) Log.Debug("{0} GET {1} - {2}", FormatRemoteEndPoint(remoteEndPoint), path, (HttpStatusCode)e.Response.StatusCode);
    }
    private void ServeGet(HttpRequestEventArgs e, string path) {
      if(path == "/api/archivist") {
        ServeArchivist(e, EffectiveClient(e.Request.RemoteEndPoint?.Address, n => e.Request.Headers[n]));
      } else if(path == "/api/export" || path.StartsWith("/api/export/", StringComparison.Ordinal)) {
        if(!ApiTokens.IsValid(QueryToken(e.Request))) {
          WriteResponse(e.Response, HttpStatusCode.NotFound);  // 404, not 403: stay invisible
          return;
        }
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
        // Url.AbsolutePath is already percent-decoded; decoding it again mangled names with '%'.
        string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string file = Path.GetFullPath(Path.Combine(_staticPath, relative));
        if(file.StartsWith(_staticPathWithSeparator, StringComparison.OrdinalIgnoreCase) && File.Exists(file)) {
          using(FileStream stream = File.OpenRead(file)) WriteResponse(e.Response, stream, ContentType(file));
        } else if(path == "/index.html") {
          WriteRedirect(e.Response, "/ide.html");
        } else if(path.StartsWith("/ide.html/", StringComparison.Ordinal)) {
          string ideFile = Path.Combine(_staticPath, "ide.html");
          if(File.Exists(ideFile)) {
            using(FileStream stream = File.OpenRead(ideFile)) WriteResponse(e.Response, stream, ContentType(ideFile));
          } else {
            WriteResponse(e.Response, HttpStatusCode.NotFound);
          }
        } else {
          WriteResponse(e.Response, HttpStatusCode.NotFound);
        }
      }
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

    /// <summary>History for a chart: GET /api/archivist?p=&amp;b=&amp;c=&amp;e=&amp;t=</summary>
    /// <remarks>Two locks, and they guard different things. The token says the caller completed
    /// a handshake on one of this port's sockets - that is CSRF protection, exactly as it is for
    /// import/export, and on its own it authorises nothing, since anyone may open /api/dashboard
    /// and be issued one. ArchivistAccessAllowed below is the authorisation, and without it this
    /// endpoint would hand out the history of every topic in the tree while the live values of
    /// those same topics stay behind DashboardAcl.
    ///
    /// Deliberately NOT routed through WebUiHost.Post: AQuery goes to the store synchronously,
    /// and a chart asking for months of samples would stall the 64 Hz tick for the whole server.
    /// It runs on the HTTP thread, which is where H04 ran it and what ArchivistPl.AQuery - with
    /// its own try/catch - is written for.</remarks>
    private void ServeArchivist(HttpRequestEventArgs e, IPAddress client) {
      WSN.HttpListenerResponse response = e.Response;
      if(!ApiTokens.IsValid(QueryToken(e.Request))) {
        WriteResponse(response, HttpStatusCode.NotFound);  // 404, not 403: stay invisible
        return;
      }
      ArchivistQuery query;
      string error;
      if(!TryParseArchivistQuery(e.Request.QueryString, out query, out error)) {
        WriteJsonResponse(response, HttpStatusCode.BadRequest, false, "archivist_bad_request", error);
        return;
      }
      // Only the first path is checked. A chart carries tens of lines and they come from one
      // branch in every case the protocol was built for; checking each would put a scan of the
      // rule set on a request that already costs a store round trip. The gap is real and
      // deliberate: paths from different branches ride in on the first path's grant.
      if(!ArchivistAccessAllowed(client, query.Topics[0], _config.TrustedNets)) {
        Log.Warning("WebUI archivist {0} refused for {1}", FormatAddress(client), query.Topics[0]);
        WriteResponse(response, HttpStatusCode.Forbidden);
        return;
      }
      Func<string[], DateTime, int, DateTime, JSL.Array> aQuery = JsExtLib.AQuery;
      if(aQuery == null) {
        // Archivist (or another store) sets JsExtLib.AQuery when it starts; with none loaded
        // the delegate is simply null. H04 dereferenced it and turned that into a 400.
        WriteJsonResponse(response, HttpStatusCode.ServiceUnavailable, false, "archivist_unavailable",
          "No archive provider is loaded");
        return;
      }
      JSL.Array rows = aQuery(query.Topics, query.Begin, query.Count, query.End);
      response.Headers.Add("Cache-Control", "no-store");
      WriteResponse(response, Encoding.UTF8.GetBytes(JsLib.Stringify(rows)), "application/json; charset=utf-8");
    }

    /// <summary>Who may read history: a caller on the IDE's network, or a dashboard grant on the topic.</summary>
    /// <remarks>Separated out so it can be tested, for the same reason TryParseArchivistQuery was:
    /// the rest of the endpoint needs a live HttpListenerResponse.
    /// <para>The first half is not a new grant. NetworkAcl.IsAllowed(client, trustedNets) is
    /// exactly the door IsAllowed puts in front of the IDE, and a caller who passes it already
    /// reads - and writes - every topic in the tree over /api/ide. Refusing that same caller the
    /// history of a topic whose live value it can watch hid nothing; it only made a chart in the
    /// IDE useless on every topic nobody had declared for a dashboard, which is nearly all of
    /// them, since DashboardAcl.Resolve answers "no rule" with "no access".</para>
    /// <para>Note that IsAllowed exempts loopback unconditionally, so a dashboard page opened on
    /// the server itself takes this branch too and bypasses DashboardAcl. That opens nothing
    /// either: the whole IDE is reachable from loopback by the same rule.</para></remarks>
    internal static bool ArchivistAccessAllowed(IPAddress client, string topicPath, string trustedNets) {
      return NetworkAcl.IsAllowed(client, trustedNets) || DashboardAcl.CanRead(client, topicPath);
    }

    /// <summary>Parses the archivist query arguments. Separated out so it can be tested.</summary>
    /// <remarks>Validation follows JsExtLib.AQueryJS, which is the same query reached from
    /// script and has already been hardened: AsString(null) rather than As&lt;string&gt;(),
    /// because the latter coerces JSValue.Null into the four-character string "null" and sends
    /// it on as a topic path.</remarks>
    internal static bool TryParseArchivistQuery(System.Collections.Specialized.NameValueCollection args,
                                                out ArchivistQuery query, out string error) {
      query = null;
      error = null;
      string topicsText = args == null ? null : args["p"];
      string beginText = args == null ? null : args["b"];
      if(string.IsNullOrEmpty(topicsText)) {
        error = "p (topics) is required";
        return false;
      }
      if(string.IsNullOrEmpty(beginText)) {
        error = "b (begin) is required";
        return false;
      }
      string[] topics;
      DateTime begin, end = DateTime.MinValue;
      int count = 0;
      try {
        JSC.JSValue topicsJs = JsLib.ParseJson(topicsText);
        topics = topicsJs.Is<string>()
          ? new string[] { topicsJs.AsString(null) }
          : topicsJs.Select(kv => kv.Value.AsString(null)).ToArray();
        if(topics.Length == 0 || topics.Any(z => string.IsNullOrEmpty(z))) {
          error = "p must be a non-empty topic path or an array of them";
          return false;
        }
        // ParseJson revives an ISO string into a Date through its reviver, which is why the
        // client can simply JSON.stringify(new Date(...)).
        JSL.Date beginDate = JsLib.ParseJson(beginText).Value as JSL.Date;
        if(beginDate == null) {
          error = "b must be a Date";
          return false;
        }
        begin = beginDate.ToDateTime();
        string endText = args["e"];
        if(!string.IsNullOrEmpty(endText)) {
          JSL.Date endDate = JsLib.ParseJson(endText).Value as JSL.Date;
          if(endDate == null) {
            error = "e must be a Date";
            return false;
          }
          end = endDate.ToDateTime();
        }
        string countText = args["c"];
        if(!string.IsNullOrEmpty(countText)) count = JsLib.ParseJson(countText).AsInt(0);
      }
      catch(Exception ex) {
        error = ex.Message;
        return false;
      }
      query = new ArchivistQuery() { Topics = topics, Begin = begin, End = end, Count = count };
      return true;
    }

    internal sealed class ArchivistQuery {
      public string[] Topics;
      public DateTime Begin;
      public DateTime End;
      public int Count;
    }
    private void OnPost(object sender, HttpRequestEventArgs e) {
      IPAddress client = EffectiveClient(e.Request.RemoteEndPoint?.Address, n => e.Request.Headers[n]);
      string path = e.Request.Url.AbsolutePath;
      if(!IsAllowed(client, path)) {
        Log.Warning("WebUI POST {0} refused from {1}", path, FormatAddress(client));
        WriteResponse(e.Response, HttpStatusCode.Forbidden);
        return;
      }
      IPEndPoint remoteEndPoint = new IPEndPoint(client ?? IPAddress.None, e.Request.RemoteEndPoint == null ? 0 : e.Request.RemoteEndPoint.Port);
      string filename = "empty";
      try {
        if(path == "/api/import") {
          if(!ApiTokens.IsValid(QueryToken(e.Request))) {
            WriteResponse(e.Response, HttpStatusCode.NotFound);  // 404, not 403: stay invisible
            return;
          }
          ImportUpload upload = ReadImportUpload(e.Request);
          if(upload == null || upload.Data == null) {
            WriteJsonResponse(e.Response, HttpStatusCode.BadRequest, false, "import_file_missing", "Import file is missing");
          } else {
            filename = upload.FileName;
            using(StreamReader reader = new StreamReader(new MemoryStream(upload.Data, upload.Offset, upload.Count, false), Encoding.UTF8, true)) Repo.Import(reader, null);
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
      if (e.Response.StatusCode == 200) {
        Log.Info("{0} POST {1} {2}", FormatRemoteEndPoint(remoteEndPoint), path, filename);
      } else {
        Log.Warning("{0} POST {1} {2} - {3}", FormatRemoteEndPoint(remoteEndPoint), path, filename, (HttpStatusCode)e.Response.StatusCode);
      }
    }
    private static ImportUpload ReadImportUpload(WSN.HttpListenerRequest request) {
      string contentType = request.ContentType ?? string.Empty;
      string boundary = MultipartBoundary(contentType);
      if(string.IsNullOrWhiteSpace(boundary)) return null;

      long declared = request.ContentLength64;
      // One copy of the payload, and only one: sizing the stream up front avoids the repeated
      // doubling reallocations, and everything downstream works on this same buffer.
      using(MemoryStream ms = new MemoryStream(declared > 0 && declared < int.MaxValue ? (int)declared : 0)) {
        request.InputStream.CopyTo(ms);
        return ParseMultipartImport(ms.GetBuffer(), (int)ms.Length, boundary);
      }
    }
    /// <summary>Locates the "file" part directly in <paramref name="body"/>, without copying it.</summary>
    /// <remarks>Scanning is byte-wise: the previous latin1 round trip cost three extra copies of
    /// the payload, and its trailing "--" trim silently truncated any file whose content ended
    /// that way. The result points into the caller's buffer.</remarks>
    internal static ImportUpload ParseMultipartImport(byte[] body, int length, string boundary) {
      if(body == null || length <= 0) return null;
      byte[] marker = Encoding.ASCII.GetBytes("--" + boundary);
      int position = IndexOf(body, length, marker, 0);
      if(position < 0) return null;

      while(position >= 0) {
        int partStart = position + marker.Length;
        int next = IndexOf(body, length, marker, partStart);
        int partEnd = next < 0 ? length : next;
        position = next;

        if(partStart + 2 <= partEnd && body[partStart] == (byte)'\r' && body[partStart + 1] == (byte)'\n') partStart += 2;
        else continue;  // "--" terminator of the final boundary, or a malformed part
        // The boundary is preceded by the CRLF that closes the part's content.
        if(partEnd - 2 >= partStart && body[partEnd - 2] == (byte)'\r' && body[partEnd - 1] == (byte)'\n') partEnd -= 2;

        int headerEnd = IndexOf(body, partEnd, HeaderTerminator, partStart);
        if(headerEnd < 0) continue;
        string headers = Encoding.ASCII.GetString(body, partStart, headerEnd - partStart);
        if(HeaderParameter(headers, "name") != "file") continue;

        int contentStart = headerEnd + HeaderTerminator.Length;
        return new ImportUpload {
          FileName = HeaderParameter(headers, "filename"),
          Data = body,
          Offset = contentStart,
          Count = Math.Max(0, partEnd - contentStart)
        };
      }
      return null;
    }
    private static readonly byte[] HeaderTerminator = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
    private static int IndexOf(byte[] haystack, int length, byte[] needle, int start) {
      int last = length - needle.Length;
      for(int i = Math.Max(0, start); i <= last; i++) {
        int j = 0;
        while(j < needle.Length && haystack[i + j] == needle[j]) j++;
        if(j == needle.Length) return i;
      }
      return -1;
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
    private static string FormatRemoteEndPoint(IPEndPoint remoteEndPoint) {
      return remoteEndPoint == null ? "unknown" : remoteEndPoint.ToString();
    }
    private static string FormatAddress(IPAddress address) {
      return address == null ? "unknown" : address.ToString();
    }

    /// <summary>path?query for the log, with the session token redacted.</summary>
    /// <remarks>The path alone did not say which request failed: every chart pan hits the same
    /// /api/archivist, and which topic and which window it asked for is the whole of what makes
    /// one of those identifiable. The token is redacted because it is a live credential for
    /// export, import and archivist until its socket closes, and a log file is exactly the wrong
    /// place to leave one - it outlives the session and is read by people who were never issued
    /// it. Values are cut short so a request carrying tens of topic paths cannot push everything
    /// else out of the line.</remarks>
    private static string DescribeRequest(WSN.HttpListenerRequest request, string path) {
      return DescribeRequest(request == null ? null : request.QueryString, path);
    }

    /// <summary>Split from the overload above so the redaction can be tested.</summary>
    /// <remarks>Same reason TryParseArchivistQuery was split out: the caller needs a live
    /// HttpListenerRequest, and what is worth pinning is what the query string turns into.</remarks>
    internal static string DescribeRequest(System.Collections.Specialized.NameValueCollection args, string path) {
      if(args == null || args.Count == 0) return path;
      StringBuilder text = new StringBuilder(path);
      char separator = '?';
      foreach(string key in args.AllKeys) {
        text.Append(separator).Append(key ?? string.Empty).Append('=');
        text.Append(key == "t" ? "<token>" : Ellipsize(args[key], 96));
        separator = '&';
      }
      return text.ToString();
    }

    private static string Ellipsize(string value, int limit) {
      if(string.IsNullOrEmpty(value)) return string.Empty;
      return value.Length <= limit ? value : value.Substring(0, limit) + "...";
    }

    /// <summary>The exception that says what actually happened.</summary>
    /// <remarks>What reaches the catch is usually a wrapper whose own message names the layer
    /// rather than the fault; the useful sentence is at the bottom of the chain.</remarks>
    private static Exception Innermost(Exception ex) {
      Exception cause = ex;
      while(cause.InnerException != null) cause = cause.InnerException;
      return cause;
    }

    /// <summary>True when the request died because the caller hung up, not because we failed.</summary>
    /// <remarks>A peer that goes away mid-response surfaces as an IOException wrapping a
    /// SocketException - "an established connection was aborted by the software in your host
    /// machine", which reads like a server fault and is not one - or as an ObjectDisposedException
    /// once websocket-sharp has already torn the connection down. Neither is actionable: there is
    /// nobody left to answer, and nothing was lost that anyone still wants.</remarks>
    internal static bool IsClientGone(Exception ex) {
      for(Exception cause = ex; cause != null; cause = cause.InnerException) {
        if(cause is ObjectDisposedException) return true;
        System.Net.Sockets.SocketException socket = cause as System.Net.Sockets.SocketException;
        if(socket == null) continue;
        switch(socket.SocketErrorCode) {
        case System.Net.Sockets.SocketError.ConnectionAborted:
        case System.Net.Sockets.SocketError.ConnectionReset:
        case System.Net.Sockets.SocketError.OperationAborted:
        case System.Net.Sockets.SocketError.Shutdown:
          return true;
        }
      }
      return false;
    }
    private static string QueryToken(WSN.HttpListenerRequest request) {
      return request == null || request.QueryString == null ? null : request.QueryString["t"];
    }
    private static void TryWriteStatus(WSN.HttpListenerResponse response, HttpStatusCode statusCode) {
      try {
        WriteResponse(response, statusCode);
      }
      catch(Exception) {
        // The response may already be committed or the peer gone; nothing useful left to do.
      }
    }

    /// <summary>Per-session tokens gating /api/export and /api/import.</summary>
    /// <remarks>Issued in resp.hello over the WebSocket, so only a same-origin page that
    /// completed a handshake can use those endpoints. Besides making them non-obvious this
    /// closes a real hole: any site open in the user's browser could previously fire a
    /// no-cors POST at /api/import and silently write topics.</remarks>
    private static class ApiTokens {
      private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tokens
        = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

      public static string Issue() {
        byte[] raw = new byte[16];
        using(var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(raw);
        string token = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tokens[token] = 0;
        return token;
      }
      public static void Revoke(string token) {
        byte ignored;
        if(!string.IsNullOrEmpty(token)) _tokens.TryRemove(token, out ignored);
      }
      public static bool IsValid(string token) {
        return !string.IsNullOrEmpty(token) && _tokens.ContainsKey(token);
      }
    }
    /// <summary>Sends the caller somewhere else, without letting the answer be remembered.</summary>
    /// <remarks>Found, not MovedPermanently, and no-store on top of it. The one caller redirects
    /// "/" to the IDE only because no index.html is present - a condition that changes the moment
    /// a dashboard is installed. A 301 is cached by browsers indefinitely and, worse, they stop
    /// asking: a client that saw the redirect once keeps landing on /ide.html long after the site
    /// root became a real page, and no server-side change can reach it. Dropping a permanent
    /// answer on a temporary condition is the defect; the status code is the fix.</remarks>
    private static void WriteRedirect(WSN.HttpListenerResponse response, string location) {
      response.StatusCode = (int)HttpStatusCode.Found;
      response.Headers["Location"] = location;
      response.Headers["Cache-Control"] = "no-store";
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
    /// <remarks>No OutputStream.Close() to finish with, unlike the Stream overload above:
    /// websocket-sharp's WriteContent "writes and sends" - it closes the response itself, so
    /// closing again throws ObjectDisposedException. The client has its bytes by then and the
    /// throw lands in OnGet's catch, which turns a perfectly served request into a warning
    /// line. Harmless once per icon; once per request on /api/archivist, which a chart polls
    /// on every pan.</remarks>
    private static void WriteResponse(WSN.HttpListenerResponse response, byte[] body, string contentType) {
      response.StatusCode = (int)HttpStatusCode.OK;
      response.ContentLength64 = body.Length;
      response.ContentType = contentType;
      response.WriteContent(body);
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
    // Data/Offset/Count point into the request buffer - no slice is ever materialised.
    internal sealed class ImportUpload {
      public string FileName;
      public byte[] Data;
      public int Offset;
      public int Count;
    }

    // One inbound queue for every WS session, drained by WebUiPl.Tick() on the engine thread.
    // That thread is the only one allowed to touch view-layer state, which is what lets the
    // whole layer drop its locks: Program.cs runs the modules in priority order, and the
    // repository (priority 1) has already dispatched this tick's subscriber callbacks by the
    // time WebUI (priority 20) starts draining - so a pump pass sees a quiescent repository.
    //
    // Deliberately ONE queue rather than one per session with a registry of sessions to walk:
    // each posted action closes over its own session, so the queue already is the list of work
    // to do. A registry would only add an entry to add on open and remove on close - the exact
    // shape that kept dead controllers alive until Item 1 fixed it. A single FIFO still gives
    // each client its own responses in its own request order; the interleaving of different
    // sessions is nothing the protocol cares about.
    //
    // Static for the same reason WSBehavior reaches the host through _instance: there is one host.
    // Every item carries a label describing what it is, because the stack trace alone will not
    // say: queued work is a closure, so an exception surfaces inside something like
    // <>c__DisplayClass7_0.<Handle>b__0 with no session, no command and no frame in sight. The
    // label is what makes a failure here correlate with the "WebUI WS#<id> connected" lines
    // around it. Callers pass a literal plus their session id - see ViewSession.Label.
    private struct WorkItem {
      public string What;
      public Action Run;
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<WorkItem> _inbox
      = new System.Collections.Concurrent.ConcurrentQueue<WorkItem>();

    internal static void Post(string what, Action work) {
      if(work != null) _inbox.Enqueue(new WorkItem() { What = what, Run = work });
    }

    /// <summary>Runs everything queued as of entry. Engine thread only.</summary>
    /// <remarks>Bounded to the items already queued rather than draining until empty, so a pass
    /// cannot chase its own tail. The case that forces this: an action fails, the catch below
    /// logs it, X13.Log.Write fires, LogHandler posts that line to the client - and if THAT push
    /// is what keeps failing, an unbounded loop would never return from one tick. Bounding turns
    /// such a feedback into one item per tick instead of a hang. Work posted during a pass simply
    /// runs on the next one, 15 ms later.</remarks>
    internal static void Pump() {
      WorkItem item;
      int budget = _inbox.Count;
      while(budget-- > 0 && _inbox.TryDequeue(out item)) {
        // Per item, not per pass: one failed frame must not swallow the rest of the queue.
        // Program.cs:243 would also catch this, but only after abandoning everything still
        // queued - and every client waiting on a resp.* would wait forever, ws-client.js having
        // no timeout. This catch is the last resort for exactly the cases with the least context
        // of their own, which is why the label is not optional.
        try { item.Run(); }
        catch(Exception ex) {
          X13.Log.Error("WebUI pump: {0} failed - {1}", item.What ?? "<unlabelled>", ex.ToString());
        }
      }
      // After the queue rather than before it: an answer that arrived during this pass is
      // delivered by the work item that carries it, and only what is still outstanding after
      // that has any business being timed out.
      PendingRpc.Sweep();
    }

    /// <summary>Transport for the dashboard protocol; DashboardSession holds the protocol.</summary>
    /// <remarks>No network check on open, unlike WSBehavior. That is the whole point of the
    /// split: this socket is reachable from anywhere and grants nothing by itself - every read
    /// and every write is decided against the topic's own dashboard.netRW/netRO.</remarks>
    private sealed class DashboardBehavior : WebSocketBehavior {
      private static int _nextSessionId;

      private readonly Func<bool> _verbose;
      private DashboardSession _session;
      private int _sessionId;
      private IPEndPoint _remoteEndPoint;
      private string _apiToken;

      public DashboardBehavior(Func<bool> verbose) {
        _verbose = verbose ?? (() => false);
      }
      protected override void OnOpen() {
        _sessionId = Interlocked.Increment(ref _nextSessionId);
        WebUiHost host = _instance;
        IPAddress socketPeer = Context.UserEndPoint == null ? null : Context.UserEndPoint.Address;
        IPAddress client = host == null ? socketPeer : host.EffectiveClient(socketPeer, n => Context.Headers[n]);
        client = NetworkAcl.Unmap(client) ?? IPAddress.None;
        _remoteEndPoint = new IPEndPoint(client, Context.UserEndPoint == null ? 0 : Context.UserEndPoint.Port);
        _apiToken = ApiTokens.Issue();
        _session = new DashboardSession(SendText, client, _apiToken, Post, CloseSocket, _verbose);
        _session.Start();
        X13.Log.Info("dashboard WS#{0} connected from {1}", _sessionId, FormatRemoteEndPoint(_remoteEndPoint));
      }
      protected override void OnMessage(MessageEventArgs e) {
        DashboardSession session = _session;
        if(session == null || !e.IsText) return;
        session.Handle(e.Data);
      }
      protected override void OnError(WebSocketSharp.ErrorEventArgs e) {
        X13.Log.Warning("dashboard WS#{0} error - {1}", _sessionId, e.Message);
      }
      protected override void OnClose(CloseEventArgs e) {
        ApiTokens.Revoke(Interlocked.Exchange(ref _apiToken, null));
        DashboardSession session = Interlocked.Exchange(ref _session, null);
        if(session == null) return;
        X13.Log.Info("dashboard WS#{0} disconnected from {1}: {2} {3}", _sessionId, FormatRemoteEndPoint(_remoteEndPoint), e.Code, e.Reason);
        // Queued for the same reason the IDE's teardown is: disposal then runs between queued
        // actions on the engine thread rather than in parallel with one.
        Post("dashboard WS#" + _sessionId + " close", session.Dispose);
      }
      private void CloseSocket() {
        Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "logon refused");
      }
      private void SendText(string text) {
        if(_verbose()) X13.Log.Debug("dashboard WS#{0} => {1}", _sessionId, text);
        base.Send(text);
      }
    }

    private sealed class WSBehavior : WebSocketBehavior {
      private static int _nextSessionId;
      private static int _activeSessionCount;

      private readonly Func<bool> _verbose;
      private ViewSession _viewSession;
      private ClientSession _clientSession;
      private int _sessionId;
      private IPEndPoint _remoteEndPoint;
      private string _apiToken;

      public WSBehavior(Func<bool> verbose) {
        _verbose = verbose ?? (() => false);
      }
      protected override void OnOpen() {
        _sessionId = Interlocked.Increment(ref _nextSessionId);
        WebUiHost host = _instance;
        IPAddress socketPeer = Context.UserEndPoint == null ? null : Context.UserEndPoint.Address;
        IPAddress client = host == null ? socketPeer : host.EffectiveClient(socketPeer, n => Context.Headers[n]);
        if(host != null && !host.IsAllowed(client, "/api/ide")) {
          X13.Log.Warning("WebUI WS#{0} refused from {1}", _sessionId, FormatAddress(client));
          Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "forbidden");
          return;  // _viewSession stays null, so OnMessage ignores anything that still arrives
        }
        Interlocked.Increment(ref _activeSessionCount);
        _remoteEndPoint = new IPEndPoint(client ?? IPAddress.None, Context.UserEndPoint == null ? 0 : Context.UserEndPoint.Port);
        _apiToken = ApiTokens.Issue();
        // The IDE is already past the network gate here, so its session topic is created up
        // front rather than lazily the way the dashboard's is - there is no anonymous peer to
        // ration it against. Queued because it writes to the repository.
        _clientSession = new ClientSession(client, Post);
        ClientSession clientSession = _clientSession;
        Post("WS#" + _sessionId + " session topic", () => clientSession.EnsureOwner());
        _viewSession = new ViewSession(Send, _sessionId, _apiToken, Post, () => clientSession.owner);
        X13.Log.Info("WebUI WS#{0} connected from {1}", _sessionId, FormatRemoteEndPoint(_remoteEndPoint));
      }
      protected override void OnMessage(MessageEventArgs e) {
        ViewSession viewSession = _viewSession;
        if (viewSession == null) return;
        if (_verbose()) X13.Log.Debug("WebUI WS#{0} <= {1}", _sessionId, e.Data);
        viewSession.Handle(e.Data);
      }
      protected override void OnError(WebSocketSharp.ErrorEventArgs e) {
        X13.Log.Warning("WebUI WS#{0} error - {1}", _sessionId, e.Message);
      }
      protected override void OnClose(CloseEventArgs e) {
        ApiTokens.Revoke(Interlocked.Exchange(ref _apiToken, null));
        ViewSession viewSession = _viewSession;
        if(viewSession == null) {
          return;  // refused in OnOpen: never counted as active, nothing to tear down
        }
        X13.Log.Info("WebUI WS#{0} disconnected from {1}: {2} {3}", _sessionId, FormatRemoteEndPoint(_remoteEndPoint), e.Code, e.Reason);
        _viewSession = null;
        ClientSession clientSession = Interlocked.Exchange(ref _clientSession, null);
        if(clientSession != null) Post("WS#" + _sessionId + " session topic close", clientSession.Dispose);
        // Queued, not called here: teardown then happens on the engine thread BETWEEN queued
        // actions rather than in parallel with one. That is what removes concurrent disposal as
        // a concept - and with it the need for LogramGraphController.Quiesce() and its volatile
        // _disposed, which existed only to wait out a callback already in flight.
        Post("WS#" + _sessionId + " close", viewSession.Dispose);
        if(Interlocked.Decrement(ref _activeSessionCount) <= 0) EditorHelper.InvalidateTypeCache();
      }
      /// <param name="noLog">Skips the verbose WS trace. That trace goes through
      /// X13.Log.Debug, so tracing the live log push (evnt.log) would re-enter
      /// X13.Log.Write and push another evnt.log, tracing that too, forever - a
      /// self-feeding loop the moment verbose WS tracing is on.</param>
      private void Send(JSC.JSObject response, bool noLog = false) {
        var json = JsLib.Stringify(response);
        if (!noLog && _verbose()) X13.Log.Debug("WebUI WS#{0} => {1}", _sessionId, json);
        base.Send(json);
      }
    }
  }
}
