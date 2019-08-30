///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using X13.Data;

namespace X13.UI {
  /// <summary></summary>
  public partial class UIDocument : BaseWindow, IDisposable {
    private ObservableCollection<DTopic> _pathItems;
    private string _path;
    private string _view;
    private IBaseForm _contentForm;
    private Client _cl;

    public UIDocument(string path, string view) {
      _path = path;
      _view = view;
      _pathItems = new ObservableCollection<DTopic>();
      this.DataContext = this;
      ContentId = _path + (_view == null ? string.Empty : ("?view=" + _view));

      InitializeComponent();
      this.icPanel.ItemsSource = _pathItems;
      Uri url;
      if(path != null && Uri.TryCreate(path, UriKind.Absolute, out url)) {
        RequestData(url);
      }
    }

    private DTopic _data;
    private BitmapSource _icon;
    private string _altView;


    public bool connected { get { return _cl != null && _cl.Status==ClientState.Ready; } }
    public DTopic data { get { return _data; } }
    public IBaseForm contentForm {
      get {
        return _contentForm;
      }
      set {
        if(_contentForm != value) {
          _contentForm = value;
          PropertyChangedReise("contentForm");
        }
      }
    }
    public BitmapSource Icon {
      get {
        return _icon;
      }
      set {
        if(_icon!=value) {
          _icon = value;
          PropertyChangedReise();
        }
      }
    }
    public bool ChangeViewEn { get { return _altView!=null; } }

    private void ClientChanged(object sender, PropertyChangedEventArgs e) {
      if(e.PropertyName == "Status") {
        if(this.connected) {
          Uri url;
          if(_data == null && _path != null && Uri.TryCreate(_path, UriKind.Absolute, out url)) {
            RequestData(url);
          }
        } else {
          contentForm = null;
          _data = null;
          PropertyChangedReise("data");
        }
        PropertyChangedReise("connected");
      }
    }

    private void RequestData(Uri url) {
      this.Cursor = Cursors.AppStarting;
      App.Workspace.GetAsync(url).ContinueWith(this.DataUpd, TaskScheduler.FromCurrentSynchronizationContext());
    }
    private void DataUpd(Task<DTopic> t) {
      if(t.IsFaulted) {
        Log.Warning("{0}", t.Exception.Message);
      } else if(t.IsCompleted) {
        _data = t.Result;
        if(_data == null) {  // topic deleted
          App.Workspace.Close(this);
          return;
        }
        _data.changed+=DataChanged;
        if(System.Threading.Interlocked.CompareExchange(ref _cl, _data.Connection, null) == null) {
          _cl.PropertyChanged += ClientChanged;
        }
        _path = _data.fullPath;
        PropertyChangedReise("data");
        DTopic c = _data;
        _pathItems.Clear();
        while(c != null) {
          _pathItems.Insert(0, c);
          c = c.parent;
        }
        DataChanged(DTopic.Art.type, _data);
        if(_view == null) {
          _view = _altView??"IN";
        }
        if(_data == _data.Connection.root) {
          Title = _data.Connection.alias;
        } else {
          Title = _data.name;
        }

        PropertyChangedReise("connected");
        UpdContent();
      }
      this.Focus();
      this.Cursor = Cursors.Arrow;
    }


    private void DataChanged(DTopic.Art a, DTopic t) {
      if(a == DTopic.Art.type) {
        System.Windows.Media.Imaging.BitmapSource ni = null;
        string ne = null, nv=_altView;

        if(t.Manifest != null && t.Manifest.ValueType == JSC.JSValueType.Object && t.Manifest.Value!=null) {
          var vv = t.Manifest["editor"];
          string tmp_s;
          if(vv.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(tmp_s = vv.Value as string)) {
            ne = tmp_s;
          }
          var iv = t.Manifest["icon"];
          if(iv.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(tmp_s= iv.Value as string)) {
            ni = App.GetIcon(tmp_s);
          }
          string typeStr;
          if(_data == null || _data.Manifest==null || _data.Manifest.ValueType!=JSC.JSValueType.Object 
            || _data.Manifest.Value==null || string.IsNullOrEmpty(typeStr = _data.Manifest["type"].Value as string)) {
            typeStr = null;
          }
          if(typeStr == "Core/Logram") {
            nv = "LO";
          }
          if(nv!=_altView) {
            _altView = nv;
            PropertyChangedReise("ChangeViewEn");
          }
        }
        if(ne == null) {
          ne = DTopic.JSV2Type(t.State);
        }
        if(ni == null) {
          if(t.State.ValueType == JSC.JSValueType.Object && t.State.Value == null) {
            ni = App.GetIcon(string.Empty);  // Folder icon
          }
        }
        if(ni == null) {
          ni = App.GetIcon(ne);
        }
        if(Icon!=ni) {
          Icon = ni;
        }
      } else if(a == DTopic.Art.RemoveChild && t == _data) {
        App.Workspace.Close(this);
      }
    }
    private void buChangeView_Click(object sender, RoutedEventArgs e) {
      string nv = _view;
      if((ccMain.Content as InspectorForm) != null && _altView!=null) {
        nv = _altView;
      } else {
        nv = "IN";
      }
      if(_view!=nv) {
        _view = nv;
        UpdContent();
      }
    }
    private void UpdContent() {
      ContentId = _path + (_view == null ? string.Empty : ("?view=" + _view));

      if(_view == "IN") {
        if((ccMain.Content as InspectorForm) == null) {
          if(contentForm != null) {
            contentForm.Dispose();
          }
          contentForm = new InspectorForm(_data);
        }
      } else if(_view == "LO") {
        if((ccMain.Content as LogramForm) == null) {
          if(contentForm != null) {
            contentForm.Dispose();
          }
          contentForm = new LogramForm(_data);
        }
      }
    }

    #region Address bar
    private void tbAddress_Loaded(object sender, RoutedEventArgs e) {
      if(!this.connected && _path == null) {
        tbAddress.Focus();
      }
    }
    private void TextBox_IsKeyboardFocusedChanged(object sender, DependencyPropertyChangedEventArgs e) {
      if((bool)e.NewValue) {
        this.icPanel.Visibility = System.Windows.Visibility.Collapsed;
      } else if(connected) {
        this.icPanel.Visibility = System.Windows.Visibility.Visible;
      } else {
        tbAddress.Focus();
      }

    }
    private void TextBox_KeyUp(object sender, KeyEventArgs e) {
      if(e.Key == Key.Enter) {
        Uri url;
        if(Uri.TryCreate(tbAddress.Text, UriKind.Absolute, out url)) {
          tbAddress.Background = null;
          RequestData(url);
        } else {
          tbAddress.Background = Brushes.LightPink;
        }
      } else if(e.Key == Key.Escape) {
        App.Workspace.Close(this);
      }
    }
    private void Button_Click(object sender, RoutedEventArgs e) {
      var bu = sender as Button;
      DTopic t;
      if(bu != null && (t = bu.DataContext as DTopic) != null) {
        App.Workspace.Open(t.fullPath);
      }
    }
    #endregion Address bar

    #region IDisposable Member
    public void Dispose() {
      var d = System.Threading.Interlocked.Exchange(ref _data, null);
      if(d != null) {
        d.changed -= DataChanged;
        if(_cl != null) {
          _cl.PropertyChanged -= ClientChanged;
        }
        _contentForm.Dispose();
        _contentForm = null;
      }
    }
    #endregion IDisposable Member
  }
}
