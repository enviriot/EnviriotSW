///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using X13.Data;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.UI {
  internal class InTopic : InBase {
    private InTopic _parent;
    private DTopic _owner;
    private bool _populated;
    private JSC.JSValue _createTag;

    public InTopic(DTopic owner, InTopic parent, Action<InBase, bool> collFunc) {
      _owner = owner;
      _parent = parent;
      base._compactView = true;
      _collFunc = collFunc;
      IsGroupHeader = _parent == null;
      _owner.changed += _owner_PropertyChanged;
      if(IsGroupHeader) {
        if(owner.Connection.root == _owner) {
          name = string.IsNullOrWhiteSpace(owner.Connection.alias)?owner.Connection.server:owner.Connection.alias;
        } else {
          name = "children";
        }
        _manifest = _owner.Manifest;  // if(IsGroupHeader) don't use UpdateType(...)
        icon = App.GetIcon("children");
        editor = null;
        levelPadding = 1;

        if(_owner.children != null && _owner.children.Any()) {
          _populated = true;
          if(_owner.children != null) {
            InsertItems(_owner.children);
          }
        }
        base._isExpanded = true;
      } else {
        name = _owner.name;
        base.UpdateType(_owner.Manifest);
        levelPadding = _parent.levelPadding + 8;
        base._isExpanded = false;
      }
      base._isVisible = IsGroupHeader || (_parent._isVisible && _parent._isExpanded);
    }

    private InTopic(JSC.JSValue tag, InTopic parent) {
      _parent = parent;
      base._compactView = true;
      _collFunc = parent._collFunc;
      name = string.Empty;
      IsEdited = true;
      levelPadding = _parent == null ? 1 : _parent.levelPadding + 8;
      _createTag = tag;
    }

    public override bool IsExpanded {
      get {
        return _isExpanded && HasChildren;
      }
      set {
        if(value && _owner != null && _items == null) {
          _populated = true;
          if(_owner.children != null) {
            InsertItems(_owner.children);
          }
        }
        base.IsExpanded = value;
      }
    }
    public override bool HasChildren {
      get {
        return (_owner != null && _owner.children != null && _owner.children.Any()) || (_items != null && _items.Any());
      }
    }
    public override JSC.JSValue value { get { return _owner != null ? _owner.State : JSC.JSValue.NotExists; } set { if(_owner != null) { _owner.SetValue(value); } } }
    public override DTopic Root {
      get { return _owner.Connection.root; }
    }
    public DTopic Owner { get { return _owner; } }
    public override void FinishNameEdit(string name) {
      if(_owner == null) {
        _parent._items.Remove(this);
        _parent._collFunc(this, false);
        if(!string.IsNullOrEmpty(name)) {
          _parent._owner.CreateAsync(name, _createTag["default"], _createTag["manifest"]).ContinueWith(SetNameComplete, TaskScheduler.FromCurrentSynchronizationContext());
        }
        if(!_parent._items.Any()) {
          _parent._items = null;
          PropertyChangedReise("items");
          PropertyChangedReise("HasChildren");
          _parent.IsExpanded = false;
        }
      } else {
        if(!string.IsNullOrEmpty(name)) {
          _owner.Move(_owner.parent, name);
        }
        IsEdited = false;
        PropertyChangedReise("IsEdited");
      }
    }

    private void SetNameComplete(Task<DTopic> td) {
      if(td.IsCompleted && td.Result != null) {
        //_owner = td.Result;
        //_owner.changed += _owner_PropertyChanged;
        //base.name = _owner.name;
        //base.UpdateType(_owner.type);
        //IsEdited = false;
        //PropertyChangedReise("IsEdited");
        //PropertyChangedReise("name");
      } else {
        if(td.IsFaulted) {
          Log.Warning("{0}/{1} - {2}", _parent._owner.fullPath, base.name, td.Exception.Message);
        }
        if(_parent._items != null) {
          _parent._items.Remove(this);
          _collFunc(this, false);
          if(!_parent._items.Any()) {
            _parent._items = null;
            _parent.PropertyChangedReise("items");
            _parent.PropertyChangedReise("HasChildren");
            _parent.IsExpanded = false;
          }
        }
      }
    }
    private void InsertItems(ReadOnlyCollection<DTopic> its) {
      bool pc_items = false;
      if(_items == null) {
        lock(this._sync) {
          if(_items == null) {
            _items = new List<InBase>();
            pc_items = true;
          }
        }
      }
      foreach(var t in its.ToArray()) {
        var td = AddTopic(t);
      }
      if(pc_items) {
        PropertyChangedReise("items");
        PropertyChangedReise("HasChildren");
        if(_items != null && _items.Any()) {
          _parent.IsExpanded = true;
        }
      }
    }
    private async Task AddTopic(DTopic t) {
      InTopic tmp;
      var tt = await t.GetAsync(null);
      if(tt != null) {
        bool o_hc = _items != null && _items.Any();
        if((tmp = _items.OfType<InTopic>().FirstOrDefault(z => z.name == tt.name)) != null) {
          _items.Remove(tmp);
          _collFunc(tmp, false);
          tmp.RefreshOwner(tt);
        } else {
          tmp = new InTopic(tt, this, _collFunc);
        }
        int i;
        for(i = 0; i < _items.Count; i++) {
          if(string.Compare(_items[i].name, tt.name) > 0) {
            break;
          }
        }
        _items.Insert(i, tmp);
        if(!o_hc) {
          PropertyChangedReise("items");
          PropertyChangedReise("HasChildren");
        }
        if(_isVisible && _isExpanded) {
          _collFunc(tmp, true);
        }
      }
    }
    private void RefreshOwner(DTopic tt) {
      if(_owner != null) {
        _owner.changed -= _owner_PropertyChanged;
        if(_items != null) {
          _items.Clear();
          _items = null;
        }
      }
      _owner = tt;
      name = tt.name;
      if(_populated && _owner.children != null) {
        InsertItems(_owner.children);
      }
    }
    private void _owner_PropertyChanged(DTopic.Art art, DTopic child) {
      bool o_hc = _items!=null && _items.Any();
      {
        var pr = this;
        while(pr._parent != null) {
          pr = pr._parent;
        }
        //Log.Debug("$ " + pr._owner.path + "(" + art.ToString() + ", " + (child != null ? child.path : "null") + ")");
      }
      if(IsGroupHeader) {
        if(art == DTopic.Art.type) {
          _manifest = _owner.Manifest;
        } else if(art == DTopic.Art.addChild && !_populated) {
          _populated = true;
        }
      } else {
        if(art == DTopic.Art.type) {
          this.UpdateType(_owner.Manifest);
        } else if(art == DTopic.Art.value) {
          this.UpdateType(_owner.Manifest);
          this.editor.ValueChanged(_owner.State);
        }
      }
      if(_populated) {
        if(art == DTopic.Art.addChild) {
          if(_items == null) {
            InsertItems(_owner.children);
          } else {
            var td = AddTopic(child);
          }
        } else if(art == DTopic.Art.RemoveChild) {
          if(_items != null) {
            var it = _items.FirstOrDefault(z => z.name == child.name);
            if(it != null) {
              it.Deleted();
              _items.Remove(it);
              if(!_items.Any()) {
                _items = null;
                IsExpanded = false;
              }
            }
          }
        }
      }
      if(o_hc != this.HasChildren) {
        PropertyChangedReise("items");
        PropertyChangedReise("HasChildren");
        PropertyChangedReise("IsExpanded");
      }
    }

    #region ContextMenu
    public override ObservableCollection<Control> MenuItems(System.Windows.FrameworkElement src) {
      var l = new ObservableCollection<Control>();
      JSC.JSValue v1;
      MenuItem mi;
      if(!IsGroupHeader) {
        mi = new MenuItem() { Header = "Open in new tab" };
        mi.Click += miOpen_Click;
        l.Add(mi);
        l.Add(new Separator());
      }
      if(_owner.Connection.Status!=ClientState.Ready) {
        if(_owner.Connection.root==_owner) {
          mi = new MenuItem() { Header = "Connect" };
          mi.Click += miOpen_Click;
          l.Add(mi);
          mi = new MenuItem() { Header = "Delete connection" };
          mi.Click += miDelConn_Click;
          l.Add(mi);
        }
      } else if(_manifest != null && (v1 = _manifest["Children"]).ValueType == JSC.JSValueType.Object) {
        var ad = new Dictionary<string, JSC.JSValue>();
        Jso2Acts(v1, ad);
        FillContextMenu(l, ad);
      } else if(_manifest != null && (v1 = _manifest["Children"]).ValueType == JSC.JSValueType.String) {
        _owner.GetAsync(v1.Value as string).ContinueWith(tt => FillContextMenuFromChildren(l, tt), TaskScheduler.FromCurrentSynchronizationContext());
      } else {
        _owner.Connection.CoreTypes.GetAsync(null).ContinueWith(tt => FillContextMenuFromChildren(l, tt), TaskScheduler.FromCurrentSynchronizationContext());
      }
      return l;
    }

    private void Jso2Acts(JSC.JSValue obj, Dictionary<string, JSC.JSValue> act) {
      foreach(var kv in obj.Where(z => z.Value != null && z.Value.ValueType == JSC.JSValueType.Object && z.Value["default"].Defined)) {
        if(!act.ContainsKey(kv.Key)) {
          act.Add(kv.Key, kv.Value);
        }
      }
      if(obj.__proto__ != null && !obj.__proto__.IsNull && obj.Defined && obj.__proto__.ValueType == JSC.JSValueType.Object) {
        Jso2Acts(obj.__proto__, act);
      }
    }
    private async void FillContextMenuFromChildren(ObservableCollection<Control> l, Task<DTopic> tt) {
      var acts = new Dictionary<string, JSC.JSValue>();
      if(tt.IsCompleted && !tt.IsFaulted && tt.Result != null) {
        foreach(var t in tt.Result.children) {
          var z = await t.GetAsync(null);
          if(z.State.ValueType == JSC.JSValueType.Object && z.State.Value != null && (z.State["default"].Defined || z.State["manifest"].Defined)) {
            acts.Add(z.name, z.State);
          }
        }
      }
      FillContextMenu(l, acts);
    }
    private void FillContextMenu(ObservableCollection<Control> l, Dictionary<string, JSC.JSValue> _acts) {
      JSC.JSValue v2;
      MenuItem mi;
      MenuItem ma = new MenuItem() { Header = "Add" };

      if(_acts != null) {
        List<RcUse> resource = new List<RcUse>();
        string rName;
        JSC.JSValue tmp1;
        KeyValuePair<string, JSC.JSValue> rca;
        string rcs;
        // fill used resources
        if(_owner.children != null) {
          foreach(var ch in _owner.children) {
            if((tmp1 = JsLib.GetField(ch.Manifest, "MQTT-SN.tag")).ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(rName = tmp1.Value as string)) {
              rName = ch.name;
            }
            rca = _acts.FirstOrDefault(z => z.Key == rName);
            if(rca.Value == null || (tmp1 = rca.Value["rc"]).ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(rcs = tmp1.Value as string)) {
              continue;
            }
            foreach(string curRC in rcs.Split(',').Where(z => !string.IsNullOrWhiteSpace(z) && z.Length > 1)) {
              int pos;
              if(!int.TryParse(curRC.Substring(1), out pos)) {
                continue;
              }
              for(int i = pos - resource.Count; i >= 0; i--) {
                resource.Add(RcUse.None);
              }
              if(curRC[0] != (char)RcUse.None && (curRC[0] != (char)RcUse.Shared || resource[pos] != RcUse.None)) {
                resource[pos] = (RcUse)curRC[0];
              }
            }
          }
        }
        // Add menuitems
        foreach(var kv in _acts) {
          bool busy = false;
          if((tmp1 = kv.Value["rc"]).ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(rcs = tmp1.Value as string)) { // check used resources
            foreach(string curRC in rcs.Split(',').Where(z => !string.IsNullOrWhiteSpace(z) && z.Length > 1)) {
              int pos;
              if(!int.TryParse(curRC.Substring(1), out pos)) {
                continue;
              }
              if(pos < resource.Count && ((curRC[0] == (char)RcUse.Exclusive && resource[pos] != RcUse.None) || (curRC[0] == (char)RcUse.Shared && resource[pos] != RcUse.None && resource[pos] != RcUse.Shared))) {
                busy = true;
                break;
              }
            }
          }
          if(busy) {
            continue;
          }
          mi = new MenuItem() { Header = kv.Key.Replace("_", "__"), Tag = kv.Value };
          if((v2 = kv.Value["icon"]).ValueType == JSC.JSValueType.String) {
            mi.Icon = new Image() { Source = App.GetIcon(v2.Value as string), Height = 16, Width = 16 };
          } else {
            mi.Icon = new Image() { Source = App.GetIcon(kv.Key), Height = 16, Width = 16 };
          }
          if((v2 = kv.Value["hint"]).ValueType == JSC.JSValueType.String) {
            mi.ToolTip = v2.Value;
          }
          mi.Click += miAdd_Click;
          if((v2 = kv.Value["menu"]).ValueType == JSC.JSValueType.String && kv.Value.Value != null) {
            AddSubMenu(ma, v2.Value as string, mi);
          } else {
            ma.Items.Add(mi);
          }
        }
      }
      if(ma.HasItems) {
        if(ma.Items.Count < 5) {
          foreach(var sm in ma.Items.OfType<System.Windows.Controls.Control>()) {
            l.Add(sm);
          }
        } else {
          l.Add(ma);
        }
        l.Add(new Separator());
      }
      if((v2 = _manifest["Action"]).ValueType == JSC.JSValueType.Object) {
        FillActions(l, v2);
      }
      Uri uri;
      if(System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Text)
        && Uri.TryCreate(System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Text), UriKind.Absolute, out uri)
        && _owner.Connection.server == uri.DnsSafeHost) {
        mi = new MenuItem() { Header = "Paste", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Paste.png"), Width = 16, Height = 16 } };
        mi.Click += miPaste_Click;
        l.Add(mi);
      }
      mi = new MenuItem() { Header = "Cut", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Cut.png"), Width = 16, Height = 16 } };
      mi.IsEnabled = !IsGroupHeader && !IsRequired;
      mi.Click += miCut_Click;
      l.Add(mi);
      mi = new MenuItem() { Header = "Delete", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Delete.png"), Width = 16, Height = 16 } };
      mi.IsEnabled = !IsGroupHeader && !IsRequired;
      mi.Click += miDelete_Click;
      l.Add(mi);
      if(!IsGroupHeader && !IsRequired) {
        mi = new MenuItem() { Header = "Rename", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Rename.png"), Width = 16, Height = 16 } };
        mi.Click += miRename_Click;
        l.Add(mi);
      }
    }
    private void FillActions(ObservableCollection<Control> l, JSC.JSValue lst) {
      JSC.JSValue v2;
      MenuItem mi;
      MenuItem ma = new MenuItem() { Header = "Action" };

      foreach(var kv in lst) {
        mi = new MenuItem() { Header = kv.Value["text"].Value as string, Tag = kv.Value["name"].Value as string };

        if((v2 = kv.Value["icon"]).ValueType == JSC.JSValueType.String) {
          mi.Icon = new Image() { Source = App.GetIcon(v2.Value as string), Height = 16, Width = 16 };
        }
        if((v2 = kv.Value["hint"]).ValueType == JSC.JSValueType.String) {
          mi.ToolTip = v2.Value;
        }
        mi.Click += miAktion_Click;
        ma.Items.Add(mi);
      }

      if(ma.HasItems) {
        if(ma.Items.Count < 5) {
          foreach(var sm in ma.Items.OfType<System.Windows.Controls.Control>()) {
            l.Add(sm);
          }
        } else {
          l.Add(ma);
        }
        l.Add(new Separator());
      }

    }
    private void AddSubMenu(MenuItem ma, string prefix, MenuItem mi) {
      MenuItem mm = ma, mn;
      string[] lvls = prefix.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
      for(int j = 0; j < lvls.Length; j++) {
        mn = mm.Items.OfType<MenuItem>().FirstOrDefault(z => z.Header as string == lvls[j]);
        if(mn == null) {
          mn = new MenuItem();
          mn.Header = lvls[j];
          mm.Items.Add(mn);
        }
        mm = mn;
      }
      mm.Items.Add(mi);
    }

    private void miAdd_Click(object sender, System.Windows.RoutedEventArgs e) {
      var mi = sender as MenuItem;
      if(mi == null) {
        return;
      }
      bool pc_items = false;
      var tag = mi.Tag as JSC.JSValue;
      if(tag != null) {
        if((bool)tag["willful"]) {
          if(_items == null) {
            lock(this._sync) {
              if(_items == null) {
                _items = new List<InBase>();
                pc_items = true;
              }
            }
          }
          if(!IsExpanded && HasChildren) {
            IsExpanded = true;
            base.PropertyChangedReise("IsExpanded");
          }
          var ni = new InTopic(tag, this);
          _items.Insert(0, ni);
          _collFunc(ni, true);
        } else {
          _owner.CreateAsync((mi.Header as string).Replace("__", "_"), tag["default"], tag["manifest"]);
        }
      }
      if(pc_items) {
        PropertyChangedReise("items");
        PropertyChangedReise("HasChildren");
      }
    }
    private void miAktion_Click(object sender, System.Windows.RoutedEventArgs e) {
      var mi = sender as MenuItem;
      if(mi == null) {
        return;
      }
      var tag = mi.Tag as string;
      if(tag != null) {
        _owner.Call(tag, _owner.path);
      }

    }
    private void miOpen_Click(object sender, System.Windows.RoutedEventArgs e) {
      App.Workspace.Open(_owner.fullPath);
    }
    private void miDelConn_Click(object sender, System.Windows.RoutedEventArgs e) {
      App.Workspace.Clients.Remove(_owner.Connection);
    }

    private void miCut_Click(object sender, System.Windows.RoutedEventArgs e) {
      System.Windows.Clipboard.SetText(_owner.fullPath, System.Windows.TextDataFormat.Text);
    }
    private void miPaste_Click(object sender, System.Windows.RoutedEventArgs e) {
      Uri uri;
      if(System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Text)
        && Uri.TryCreate(System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Text), UriKind.Absolute, out uri)
        && _owner.Connection.server==uri.DnsSafeHost) {
        System.Windows.Clipboard.Clear();
        App.Workspace.GetAsync(uri).ContinueWith(td => {
          if(td.IsCompleted && !td.IsFaulted && td.Result != null) {
            td.Result.Move(_owner, td.Result.name);
          }
        }, TaskScheduler.FromCurrentSynchronizationContext());
      }
    }

    private void miDelete_Click(object sender, System.Windows.RoutedEventArgs e) {
      _owner.Delete();
    }
    private void miRename_Click(object sender, System.Windows.RoutedEventArgs e) {
      base.IsEdited = true;
      PropertyChangedReise("IsEdited");
    }

    private void IconFromTypeLoaded(Task<DTopic> td, object o) {
      var img = o as Image;
      if(img != null && td.IsCompleted && td.Result != null) {
        //img.Source = App.GetIcon(td.Result.GetField<string>("icon"));
      }
    }
    private enum RcUse : ushort {
      None = '0',
      Baned = 'B',
      Shared = 'S',
      Exclusive = 'X',
    }

    #endregion ContextMenu

    #region IComparable<InBase> Members
    public override int CompareTo(InBase other) {
      var o = other as InTopic;
      return o == null ? 1 : this.path.CompareTo(o.path);
    }
    private string path {
      get {
        if(_owner != null) {
          return _owner.fullPath;
        } else if(_parent != null && _parent._owner != null) {
          return _parent._owner.fullPath;
        }
        return "/";
      }
    }
    #endregion IComparable<InBase> Members

    #region IDisposable Member
    public override void Dispose() {
      _collFunc(this, false);
      var o = System.Threading.Interlocked.Exchange(ref _owner, null);
      if(o != null) {
        o.changed -= _owner_PropertyChanged;
#pragma warning disable 420
        var its = System.Threading.Interlocked.Exchange(ref base._items, null);
#pragma warning restore 420
        if(its != null) {
          foreach(var it in its.ToArray()) {
            it.Dispose();
          }
        }
      }
    }
    #endregion IDisposable Member

    public override string ToString() {
      StringBuilder sb = new StringBuilder();
      if(_owner == null) {
        if(_parent != null && _parent._owner != null) {
          sb.Append(_parent._owner.path);
        } else {
          sb.Append("...");
        }
        sb.AppendFormat("/{0}", name);
      } else {
        sb.Append(_owner.path);
      }
      return sb.ToString();
    }

  }
}
