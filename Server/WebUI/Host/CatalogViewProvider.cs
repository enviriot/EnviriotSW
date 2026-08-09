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

namespace X13.WebUI.Host {
  internal sealed class CatalogViewProvider : ViewProviderBase {
    private const string ViewName = "catalog";
    private const string RootVid = "catalog#/";
    private const string DefaultCatalogUri = "https://enviriot.github.io/catalog/";
    private readonly Action<JSC.JSObject> _send;
    private readonly Dictionary<string, CatalogNode> _nodes;

    public CatalogViewProvider(Action<JSC.JSObject> send) {
      _send = send;
      _nodes = new Dictionary<string, CatalogNode>(StringComparer.Ordinal);
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
          Name = CatalogRootName(EnsureCatalogUri()),
          Url = EnsureTrailingSlash(EnsureCatalogUri()),
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
      if(!node.ChildrenLoaded) LoadChildren(node);
      SendUpd(node, node.Children.Count > 0 ? 2 : 0);
      foreach(CatalogNode child in node.Children) SendAdd(child);
    }

    private void CollapseNode(CatalogNode node) {
      SendUpd(node, node.Children.Count > 0 ? 1 : 0);
      foreach(CatalogNode child in node.Children) SendDel(child.Vid);
    }

    private void LoadChildren(CatalogNode node) {
      node.Children.Clear();
      if(string.IsNullOrWhiteSpace(node.Url)) {
        node.ChildrenLoaded = true;
        return;
      }

      try {
        JSC.JSValue json = JsLib.ParseJson(DownloadString(EnsureTrailingSlash(node.Url) + "index.json"));
        foreach(var kv in json) {
          JSC.JSValue value = kv.Value;
          if(value == null || value.ValueType != JSC.JSValueType.Object || value.Value == null) continue;
          CatalogNode child = CreateChild(node, value);
          node.Children.Add(child);
          _nodes[child.Vid] = child;
        }
        // Only mark loaded on success - a failed fetch (logged below) stays
        // retryable via the next collapse/expand instead of getting stuck empty.
        node.ChildrenLoaded = true;
      }
      catch(Exception ex) {
        Log.Warning("CatalogViewProvider.Load({0}) - {1}", node.Url, ex.Message);
      }
    }

    private static CatalogNode CreateChild(CatalogNode parent, JSC.JSValue value) {
      string name = JsLib.OfString(value["name"], string.Empty);
      string children = JsLib.OfString(value["children"], null);
      string url = ResolveUrl(parent.Url, children);
      CatalogNode node = new CatalogNode() {
        Vid = ChildVid(parent, name),
        Name = name,
        Hint = JsLib.OfString(value["hint"], string.Empty),
        Url = url,
        Source = ResolveSourceUrl(parent.Url, JsLib.OfString(value["src"], null)),
        CheckPath = JsLib.OfString(value["path"], null),
        SourceVersion = JsLib.OfString(value["ver"], string.Empty),
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
      dto["icon"] = "/ide_icons/ty_topic.png";
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


    private ViewOpResult Download(CatalogNode node) {
      if(string.IsNullOrWhiteSpace(node.Source)) {
        return ViewOpResult.Error("catalog_source_missing", "Catalog item source is missing");
      }
      try {
        using(StreamReader reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(DownloadString(node.Source))), Encoding.UTF8, true)) {
          Repo.Import(reader, null);
        }
        RefreshInstallState(node);
        SendStateUpd(node);
        Log.Info("Catalog import({0})", node.Source);
        return ViewOpResult.Success();
      }
      catch(Exception ex) {
        Log.Warning("Catalog import({0}) - {1}", node.Source, ex.Message);
        return ViewOpResult.Error("catalog_download_failed", ex.Message);
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
      topic.Remove();
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
      string value = topic == null ? null : JsLib.OfString(topic.GetField("version"), null);
      if(string.IsNullOrWhiteSpace(value)) return string.Empty;
      return value.StartsWith("¤VR", StringComparison.Ordinal) ? value.Substring(3) : value;
    }

    internal static string EnsureCatalogUri() {
      Topic catalog = Topic.root.Get("/$YS/Catalog", true);
      catalog.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
      Topic uri = catalog.Get("uri", true);
      uri.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
      string value = JsLib.OfString(uri.GetState(), null);
      if(string.IsNullOrWhiteSpace(value)) {
        value = DefaultCatalogUri;
        uri.SetState(value);
      }
      return value;
    }

    private static string DownloadString(string url) {
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      HttpWebRequest request = WebRequest.CreateHttp(url);
      request.UserAgent = "X13-WebUI";
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
      public List<CatalogNode> Children;
    }
  }
}
