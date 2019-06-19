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
        _root = new CatalogItem(_serverUrl, CollectionChange);
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
  }
  public class CatalogItem : Data.NPC_UI, IComparable<CatalogItem> {
    private CatalogItem _parent;
    private string _name, _path, _url;
    private bool _isExpanded, _isVisible, _loaded;
    private List<CatalogItem> _items;
    private Action<CatalogItem, bool> _collFunc;

    public CatalogItem(string serverUrl, Action<CatalogItem, bool> collFunc) {
      _loaded = false;
      _collFunc = collFunc;
      _url = serverUrl;
      _path = "/";
      _name = new Uri(_url).DnsSafeHost;
      LevelPadding = 5;
      IsVisible = true;
      IsExpanded = true;
    }
    public CatalogItem(CatalogItem parent, JSC.JSValue inf) {
      _loaded = false;
      _parent = parent;
      _collFunc = _parent._collFunc;
      _name = inf["name"].Value as string;
      var ch = inf["children"].Value as string;
      if(string.IsNullOrEmpty(ch)) {
        _url = null;
      } else if(ch.StartsWith("http")) {
        _url = ch;
      } else if(ch.StartsWith("/")) {
        _url = (new Uri(_parent._url)).GetLeftPart(UriPartial.Authority) + ch + "/";
      } else {
        _url = _parent._url + ch + "/";
      }
      _path = _parent._path+_name+"/";
      _isExpanded = false;
      LevelPadding = _parent.LevelPadding + 10;
      _items = null;
      IsVisible = _parent._isVisible && _parent._isExpanded;
    }

    public string Name { get { return _name; } private set { base.SetVal(ref _name, value); } }
    public double LevelPadding { get; protected set; }
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
