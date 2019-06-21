///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Net;
using System.Collections.ObjectModel;

namespace X13.UI {
  public partial class UiCatalog : BaseWindow, IDisposable {
    private const string SERVER_URL = "https://enviriot.github.io/catalog/";

    private string _path, _serverUrl;
    private ObservableCollection<CatalogItem> _items;
    private CatalogItem _root;

    public UiCatalog(string path) {
      _path = path;
      ContentId = _path + "?view=catatlog";

      ServicePointManager.Expect100Continue = true;
      ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

      _items = new ObservableCollection<CatalogItem>();

      App.Workspace.GetAsync(new Uri(path)).ContinueWith(CatLoaded);
      InitializeComponent();
      lvItems.ItemsSource = _items;
    }

    private async void CatLoaded(Task<Data.DTopic> tt) {
      if(!tt.IsFaulted && tt.IsCompleted && tt.Result != null) {
        var root = tt.Result;
        var catPath = await root.GetAsync("/$YS/Catalog/uri");
        if(catPath == null || catPath.State == null || catPath.State.ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(_serverUrl = catPath.State.Value as string)) {
          var manifest = JSC.JSObject.CreateObject();
          manifest["attr"] = 9;
          var catRoot = await root.CreateAsync("$YS/Catalog", JSC.JSObject.Null, manifest);
          _serverUrl = SERVER_URL;
          catPath = await catRoot.CreateAsync("uri", _serverUrl, manifest);
        }
        _root = new CatalogItem(_serverUrl, root, CollectionChange);
      }
    }
    private void CollectionChange(CatalogItem item, bool visible) {
      if(item == null) {
        throw new ArgumentNullException("item");
      }
      Dispatcher.BeginInvoke(new Action<CatalogItem, bool>(CollectionChangePr), item, visible);
    }
    private void CollectionChangePr(CatalogItem item, bool visible) {
      if(visible) {
        lock(_items) {
          int min = 0, mid = -1, max = _items.Count - 1, cr;

          while(min <= max) {
            mid = (min + max) / 2;
            cr = item.CompareTo(_items[mid]);
            if(cr > 0) {
              min = mid + 1;
            } else if(cr < 0) {
              max = mid - 1;
              mid = max;
            } else {
              break;
            }
          }
          _items.Insert(mid + 1, item);
        }
      } else {
        _items.Remove(item);
      }
    }

    public static async Task<string> GetHttpString(string url) {
      var req = WebRequest.CreateHttp(url);
      var resp = await req.GetResponseAsync();
      string txt;
      using(var sr = new System.IO.StreamReader(resp.GetResponseStream(), Encoding.UTF8, true)) {
        txt = await sr.ReadToEndAsync();
      }
      return txt;
    }

    #region IDisposable Member
    public void Dispose() {
    }
    #endregion IDisposable Member

    private void buDownload_Click(object sender, RoutedEventArgs e) {
      var fe = sender as FrameworkElement;
      CatalogItem item;
      if(fe!=null && (item = fe.DataContext as CatalogItem)!=null){
        item.Download();
      }
    }
  }
  public class CatalogItem : Data.NPC_UI, IComparable<CatalogItem> {
    private CatalogItem _parent;
    private Data.DTopic _rootT, _checkTopic;
    private string _name, _path, _url, _hint, _src, _checkPath;
    private bool _isExpanded, _isVisible, _loaded, _downloadEnable, _removeEnable;
    private Version _srcVersion, _actVersion;
    private List<CatalogItem> _items;
    private Action<CatalogItem, bool> _collFunc;

    public CatalogItem(string serverUrl, Data.DTopic root, Action<CatalogItem, bool> collFunc) {
      _loaded = false;
      _collFunc = collFunc;
      _url = serverUrl;
      _rootT = root;
      _path = "/";
      _name = new Uri(_url).DnsSafeHost;
      _srcVersion = new Version(0, 0);
      _actVersion = new Version(0, 0);
      ActionButtonsVisible = Visibility.Collapsed;
      IsVisible = true;
      IsExpanded = true;

    }
    public CatalogItem(CatalogItem parent, JSC.JSValue inf) {
      _loaded = false;
      _parent = parent;
      _collFunc = _parent._collFunc;
      _rootT = _parent._rootT;
      _name = inf["name"].Value as string;
      _hint = inf["hint"].Value as string;
      var tmp_s = inf["children"].Value as string;
      if(string.IsNullOrEmpty(tmp_s)) {
        _url = null;
      } else if(tmp_s.StartsWith("http")) {
        _url = tmp_s;
      } else if(tmp_s.StartsWith("/")) {
        _url = (new Uri(_parent._url)).GetLeftPart(UriPartial.Authority) + tmp_s + "/";
      } else {
        _url = _parent._url + tmp_s + "/";
      }
      _path = _parent._path+_name+"/";
      _src = inf["src"].Value as string;
      _checkPath = inf["path"].Value as string;
      tmp_s = inf["ver"].Value as string;
      _actVersion = new Version(0, 0);
      if(string.IsNullOrWhiteSpace(tmp_s) || !Version.TryParse(tmp_s, out _srcVersion)) {
        _srcVersion = new Version(0, 0);
        ActionButtonsVisible = Visibility.Collapsed;
      } else if(_src!=null && _checkPath!=null) {
        _rootT.GetAsync(_checkPath).ContinueWith(CheckTopicLoaded);
        ActionButtonsVisible = Visibility.Visible;
      }
      _isExpanded = false;
      _items = null;
      IsVisible = _parent._isVisible && _parent._isExpanded;
    }

    private void CheckTopicLoaded(Task<Data.DTopic> tt) {
      if(!tt.IsFaulted && tt.IsCompleted && tt.Result != null) {
        _checkTopic = tt.Result;
        Refresh();
        _checkTopic.changed+=_checkTopic_changed;
      } else {
        DownlodEnabled = true;
      }
    }
    private void _checkTopic_changed(Data.DTopic.Art art, Data.DTopic t) {
      if(art == Data.DTopic.Art.type) {
        Refresh();
      }
    }
    private void Refresh() {
      RemoveEnabled = true;
      var ver = _checkTopic.Manifest["version"].Value as string;
      if(ver==null || !ver.StartsWith("¤VR") || !Version.TryParse(ver.Substring(3), out _actVersion)) {
        DownlodEnabled = true;
      } else {
        base.PropertyChangedReise("ActVer");
        DownlodEnabled = _actVersion < _srcVersion;
      }
    }

    public async void Download() {
      string srcUrl;
      if(string.IsNullOrEmpty(_src)) {
        srcUrl = null;
      } else if(_src.StartsWith("http")) {
        srcUrl = _src;
      } else if(_src.StartsWith("/")) {
        srcUrl = (new Uri(_parent._url)).GetLeftPart(UriPartial.Authority) + _src;
      } else {
        srcUrl = _parent._url + _src;
      }
      if(srcUrl == null) {
        return;
      }
      try {
        var txt = await UiCatalog.GetHttpString(srcUrl);
        var body = Encoding.UTF8.GetBytes(txt);
        var payload = Convert.ToBase64String(body);

        _rootT.Connection.SendCmd(16, srcUrl, payload);
        if(_checkTopic==null) {
          await Task.Delay(300);
          var tt = _rootT.GetAsync(_checkPath).ContinueWith(CheckTopicLoaded);
        }
        Log.Info("Import({0})", srcUrl);
      }
      catch(Exception ex) {
        Log.Warning("Import({0}) - {1}", srcUrl, ex.Message);
      }
    }


    public string Name { get { return _name; } }
    public string Hint { get { return _hint; } }
    public string SrcVer { get { return _srcVersion.Minor>0?_srcVersion.ToString(4):string.Empty; } }
    public string ActVer { get { return _actVersion.Minor>0?_actVersion.ToString(4):string.Empty; } }
    public bool DownlodEnabled { get { return _downloadEnable; } set { base.SetVal(ref _downloadEnable, value); } }
    public bool RemoveEnabled { get { return _removeEnable; } set { base.SetVal(ref _removeEnable, value); } }
    public Visibility ActionButtonsVisible { get; private set; }

    public double LevelPadding { get { return _parent==null?0:(_parent.LevelPadding+10);} }
    public bool IsExpanded { 
      get { 
        return _isExpanded; 
      } 
      set {
        if(value != _isExpanded) {
          _isExpanded = value;
          PropertyChangedReise();
          if(_items != null) {
            foreach(var i in _items) {
              i.IsVisible= this._isVisible && this._isExpanded;
            }
          } else if(!_loaded && _url!=null) {
            LoadChildren();
          }
        }
      } 
    }
    public bool HasChildren { get { return _url!=null; } }
    public bool IsVisible {
      get { return _isVisible; }
      set {
        if(value != _isVisible) {
          _isVisible = value;
          if(_items != null) {
            foreach(var i in _items) {
              i.IsVisible = this._isVisible && this._isExpanded;
            }
          }
          _collFunc(this, _isVisible);
        }
      }
    }

    private async void LoadChildren() {
      _loaded = true;
      var json = await UiCatalog.GetHttpString(_url + "index.json");
      var arr = JSL.JSON.parse(json);
      _items = new List<CatalogItem>();
      foreach(var kv in arr) {
        _items.Add(new CatalogItem(this, kv.Value));
      }
    }

    #region IComparable<CatalogItem> Members
    public int CompareTo(CatalogItem o) {
      if(o==null) {
        return -1;
      }
      return this._path.CompareTo(o._path);
    }
    #endregion IComparable<CatalogItem> Members

  }
}
