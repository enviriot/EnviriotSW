///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  internal sealed class CatalogViewProvider : ViewProviderBase {
    private const string ViewName = "catalog";
    private const string RootVid = "catalog#/";
    private const int ConnectTimeoutMs = 15000;
    private const int ReadTimeoutMs = 30000;
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    private readonly Action<string, Action<string, string>> _fetch;
    private readonly Dictionary<string, CatalogNode> _nodes;
    // Engine thread only. A fetch started before the session closed still calls back afterwards.
    private bool _disposed;

    /// <param name="post">Lane A: queues work for the engine thread.</param>
    /// <param name="fetch">Lane B: given a URL, eventually calls back with (body, error) -
    /// exactly one of the two non-null. The default runs the blocking HTTP on a pool thread, so
    /// the callback arrives on that thread and every use of it hands the result to <paramref
    /// name="post"/> rather than touching anything here. Tests substitute a synchronous one;
    /// nothing in production does.</param>
    public CatalogViewProvider(Action<JSC.JSObject> send, Action<string, Action> post = null, Action<string, Action<string, string>> fetch = null, Func<Topic> prim = null) {
      _send = send;
      _post = post ?? ((what, work) => work());
      _fetch = fetch ?? FetchOnPool;
      _prim = prim;
      _nodes = new Dictionary<string, CatalogNode>(StringComparer.Ordinal);
    }

    private readonly Func<Topic> _prim;

    public override void Dispose() {
      _disposed = true;
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult Expand(string vid, bool expand) {
      CatalogNode node = ResolveNode(vid);
      if(node == null) return ViewOpResult.Error("catalog_node_not_found", "Catalog node not found: " + (vid ?? "<null>"));
      if(expand) ExpandNode(node);
      else CollapseNode(node);
      return ViewOpResult.Success();
    }

    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      CatalogNode node = ResolveNode(vid);
      if(node == null) return ViewOpResult.Error("catalog_node_not_found", "Catalog node not found: " + (vid ?? "<null>"));
      if(string.Equals(cmd, "download", StringComparison.Ordinal)) return Download(node);
      if(string.Equals(cmd, "remove", StringComparison.Ordinal)) return Remove(node);
      return ViewOpResult.Error("catalog_rpc_not_supported", "Catalog RPC command is not supported: " + (cmd ?? "<null>"));
    }

    private CatalogNode ResolveNode(string vid) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) return null;
      CatalogNode node;
      if(_nodes.TryGetValue(vid, out node)) return node;
      if(vid == RootVid) {
        node = new CatalogNode() {
          Vid = RootVid,
          Name = CatalogRootName(CatalogSettings.EnsureUri()),
          Url = EnsureTrailingSlash(CatalogSettings.EnsureUri()),
          Level = 0,
          HasChildren = true,
          Children = new List<CatalogNode>(),
        };
        _nodes[RootVid] = node;
        return node;
      }
      return null;
    }

    private void ExpandNode(CatalogNode node) {
      if(node.ChildrenLoaded) {
        SendExpanded(node);
        return;
      }
      BeginLoadChildren(node);
    }

    private void SendExpanded(CatalogNode node) {
      SendUpd(node, ExpanderFor(node, true));
      foreach(CatalogNode child in node.Children) SendAdd(child);
    }

    private void CollapseNode(CatalogNode node) {
      SendUpd(node, ExpanderFor(node, false));
      foreach(CatalogNode child in node.Children) SendDel(child.Vid);
    }

    // Children.Count alone cannot tell "no children" from "we could not find out": a failed
    // index.json fetch left the list empty and pushed expander 0, at which point
    // catalog-document.js disables the twisty and #toggle bails - the node became permanently
    // un-expandable, which is exactly the retry LoadChildren's comment below promises. Only
    // report 0 once a load actually succeeded and came back empty.
    private static int ExpanderFor(CatalogNode node, bool expanded) {
      if(!node.ChildrenLoaded) return node.HasChildren ? 1 : 0;
      if(node.Children.Count == 0) return 0;
      return expanded ? 2 : 1;
    }

    // Lane B. The index.json fetch leaves the engine thread and only its text comes back, so an
    // unreachable catalog host costs a pool thread for the timeout instead of stalling the whole
    // tick loop. resp.expand therefore answers before these rows exist - which the protocol
    // allows (the client correlates on id and drives the tree from evnt.add/evnt.upd, never from
    // the response body) and catalog-document.js#toggle confirms: it ignores the result.
    private void BeginLoadChildren(CatalogNode node) {
      if(node.Loading) return;   // a second click on the twisty while the first fetch is still out
      if(string.IsNullOrWhiteSpace(node.Url)) {
        node.ChildrenLoaded = true;
        SendExpanded(node);
        return;
      }
      node.Loading = true;
      string url = EnsureTrailingSlash(node.Url) + "index.json";
      _fetch(url, (body, error) => _post("catalog load " + node.Vid, () => CompleteLoadChildren(node, body, error)));
    }

    private void CompleteLoadChildren(CatalogNode node, string body, string error) {
      node.Loading = false;
      if(_disposed) return;
      node.Children.Clear();
      if(error != null) {
        Log.Warning("CatalogViewProvider.Load({0}) - {1}", node.Url, error);
      } else {
        try {
          JSC.JSValue json = JsLib.ParseJson(body);
          foreach(var kv in json) {
            JSC.JSValue value = kv.Value;
            if(!value.IsObject()) continue;
            CatalogNode child = CreateChild(node, value);
            node.Children.Add(child);
            _nodes[child.Vid] = child;
          }
          // Only mark loaded on success - a failed fetch (logged above) or unparsable index
          // stays retryable via the next collapse/expand instead of getting stuck empty.
          node.ChildrenLoaded = true;
        }
        catch(Exception ex) {
          Log.Warning("CatalogViewProvider.Load({0}) - {1}", node.Url, ex.Message);
        }
      }
      // Sent on the failure path too: ExpanderFor reports 1 for a node that never loaded, which
      // is what puts the twisty back and keeps the retry above reachable.
      SendExpanded(node);
    }

    private static CatalogNode CreateChild(CatalogNode parent, JSC.JSValue value) {
      string name = value.AsString("name", string.Empty);
      string children = value.AsString("children", null);
      string url = ResolveUrl(parent.Url, children);
      CatalogNode node = new CatalogNode() {
        Vid = ChildVid(parent, name),
        Name = name,
        Hint = value.AsString("hint", string.Empty),
        Url = url,
        Source = ResolveSourceUrl(parent.Url, value.AsString("src", null)),
        CheckPath = value.AsString("path", null),
        SourceVersion = value.AsString("ver", string.Empty),
        Level = parent.Level + 1,
        HasChildren = !string.IsNullOrWhiteSpace(url),
        Children = new List<CatalogNode>(),
      };
      RefreshInstallState(node);
      return node;
    }

    private void SendAdd(CatalogNode node) {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, node.Vid);
      dto["level"] = new JSL.Number(node.Level);
      dto["expander"] = new JSL.Number(node.HasChildren ? 1 : 0);
      // No icon key at all: a catalog entry is not a topic and has nothing to resolve an icon
      // from, so there is nothing for the server to say here.
      dto["name"] = node.Name ?? string.Empty;
      dto["editor"] = "Default";
      dto["value"] = node.Hint ?? string.Empty;
      dto["readonly"] = true;
      dto["info"] = node.Hint ?? string.Empty;
      dto["srcVer"] = node.SourceVersion ?? string.Empty;
      dto["actVer"] = node.ActVersion ?? string.Empty;
      dto["downloadEnabled"] = node.DownloadEnabled;
      dto["removeEnabled"] = node.RemoveEnabled;
      _send(dto);
    }

    private void SendUpd(CatalogNode node, int expander) {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, node.Vid);
      dto["expander"] = new JSL.Number(expander);
      _send(dto);
    }

    private void SendStateUpd(CatalogNode node) {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, node.Vid);
      dto["actVer"] = node.ActVersion ?? string.Empty;
      dto["downloadEnabled"] = node.DownloadEnabled;
      dto["removeEnabled"] = node.RemoveEnabled;
      _send(dto);
    }

    private void SendDel(string vid) {
      _send(ViewProtocolSerializer.Del(vid));
    }


    // Only the download leaves the engine thread. Xst.Import stays on it: parsing a package and
    // creating its topics is milliseconds, and running it here means RefreshInstallState right
    // after reads a tree that already has them - Topic.Resolve publishes the instance and Fill sets
    // the manifest immediately, only the TopicEvent is queued.
    //
    // resp.rpc now answers before the install has happened, so a failure reaches the log rather
    // than the client. Accepted: catalog-document.js#rpc only console.warn's the rejection, and
    // the outcome the user actually reads is the version/button state in the evnt.upd below.
    private ViewOpResult Download(CatalogNode node) {
      if(string.IsNullOrWhiteSpace(node.Source)) {
        return ViewOpResult.Error("catalog_source_missing", "Catalog item source is missing");
      }
      if(node.Installing) return ViewOpResult.Success();
      node.Installing = true;
      _fetch(node.Source, (body, error) => _post("catalog install " + node.Vid, () => CompleteDownload(node, body, error)));
      return ViewOpResult.Success();
    }

    private void CompleteDownload(CatalogNode node, string body, string error) {
      node.Installing = false;
      if(_disposed) return;
      if(error != null) {
        Log.Warning("Catalog import({0}) - {1}", node.Source, error);
        return;
      }
      try {
        using(StreamReader reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(body)), Encoding.UTF8, true)) {
          Xst.Import(reader, null);
        }
        RefreshInstallState(node);
        SendStateUpd(node);
        Log.Info("Catalog import({0})", node.Source);
      }
      catch(Exception ex) {
        Log.Warning("Catalog import({0}) - {1}", node.Source, ex.Message);
      }
    }

    private ViewOpResult Remove(CatalogNode node) {
      if(string.IsNullOrWhiteSpace(node.CheckPath)) {
        return ViewOpResult.Error("catalog_check_path_missing", "Catalog item path is missing");
      }
      Topic topic = Topic.root.Get(node.CheckPath, false);
      if(topic == null) {
        RefreshInstallState(node);
        SendStateUpd(node);
        return ViewOpResult.Success();
      }
      topic.Remove(_prim == null ? null : _prim());
      node.ActVersion = string.Empty;
      node.DownloadEnabled = !string.IsNullOrWhiteSpace(node.Source);
      node.RemoveEnabled = false;
      SendStateUpd(node);
      return ViewOpResult.Success();
    }

    private static void RefreshInstallState(CatalogNode node) {
      node.ActVersion = string.Empty;
      node.RemoveEnabled = false;
      node.DownloadEnabled = !string.IsNullOrWhiteSpace(node.Source);
      if(string.IsNullOrWhiteSpace(node.CheckPath)) return;

      Topic topic = Topic.root.Get(node.CheckPath, false);
      if(topic == null) return;

      node.RemoveEnabled = true;
      node.ActVersion = ManifestVersion(topic);
      Version sourceVersion;
      Version actualVersion;
      if(!Version.TryParse(node.SourceVersion, out sourceVersion)) return;
      if(!Version.TryParse(node.ActVersion, out actualVersion)) return;
      node.DownloadEnabled = sourceVersion > actualVersion;
    }

    private static string ManifestVersion(Topic topic) {
      string value = topic == null ? null : topic.GetField("version").AsString(null);
      if(string.IsNullOrWhiteSpace(value)) return string.Empty;
      return value.StartsWith("¤VR", StringComparison.Ordinal) ? value.Substring(3) : value;
    }

    // The default lane B: a pool thread, because the request below is the synchronous API. The
    // asynchronous one is not an improvement here - HttpWebRequest.Timeout is ignored by
    // BeginGetResponse, so the timeouts that make this bounded would have to be rebuilt by hand.
    private static void FetchOnPool(string url, Action<string, string> done) {
      System.Threading.ThreadPool.QueueUserWorkItem(delegate {
        string body = null, error = null;
        try { body = DownloadString(url); }
        catch(Exception ex) { error = ex.Message; }
        done(body, error);
      });
    }

    private static string DownloadString(string url) {
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      HttpWebRequest request = WebRequest.CreateHttp(url);
      request.UserAgent = "X13-WebUI";
      // Bounds the pool thread this occupies. Without them the defaults are 100s connect /
      // 300s read, and a catalog host that accepts the connection then stalls would hold a
      // thread - and the node's Loading flag, so the retry - for five minutes.
      request.Timeout = ConnectTimeoutMs;
      request.ReadWriteTimeout = ReadTimeoutMs;
      using(WebResponse response = request.GetResponse())
      using(StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8, true)) {
        return reader.ReadToEnd();
      }
    }

    private static string ResolveSourceUrl(string parentUrl, string source) {
      if(string.IsNullOrWhiteSpace(source)) return null;
      if(source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return source;
      Uri parent = new Uri(EnsureTrailingSlash(parentUrl));
      if(source.StartsWith("/", StringComparison.Ordinal)) return parent.GetLeftPart(UriPartial.Authority) + source;
      return new Uri(parent, source).ToString();
    }

    private static string ResolveUrl(string parentUrl, string children) {
      if(string.IsNullOrWhiteSpace(children)) return null;
      if(children.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return EnsureTrailingSlash(children);
      Uri parent = new Uri(EnsureTrailingSlash(parentUrl));
      if(children.StartsWith("/", StringComparison.Ordinal)) return EnsureTrailingSlash(parent.GetLeftPart(UriPartial.Authority) + children);
      return EnsureTrailingSlash(new Uri(parent, children).ToString());
    }

    private static string EnsureTrailingSlash(string value) {
      if(string.IsNullOrWhiteSpace(value)) return value;
      return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private static string CatalogRootName(string uri) {
      Uri parsed;
      return Uri.TryCreate(uri, UriKind.Absolute, out parsed) ? parsed.DnsSafeHost : "Catalog";
    }

    private static string ChildVid(CatalogNode parent, string name) {
      string segment = Uri.EscapeDataString(string.IsNullOrWhiteSpace(name) ? "item" : name);
      if(parent.Vid == RootVid) return "catalog#/" + segment;
      return parent.Vid.TrimEnd('/') + "/" + segment;
    }

    private sealed class CatalogNode {
      public string Vid;
      public string Name;
      public string Hint;
      public string Url;
      public string Source;
      public string CheckPath;
      public string SourceVersion;
      public string ActVersion;
      public bool DownloadEnabled;
      public bool RemoveEnabled;
      public int Level;
      public bool HasChildren;
      public bool ChildrenLoaded;
      // In-flight guards for the two lane-B operations, so a repeated click cannot start a
      // second fetch for the same node. Engine thread only: both are set before the fetch
      // starts and cleared in the queued completion.
      public bool Loading;
      public bool Installing;
      public List<CatalogNode> Children;
    }
  }
}
