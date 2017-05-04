///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using X13.Data;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.UI {
  internal class InValue : InBase, IDisposable {
    private DTopic _data;
    private InValue _parent;
    private JSC.JSValue _value;
    private string _path;

    public InValue(DTopic data, Action<InBase, bool> collFunc) {
      _data = data;
      _parent = null;
      _collFunc = collFunc;
      name = "state";
      _path = string.Empty;
      _isVisible = true;
      _isExpanded = true; // fill _valueVC
      base.IsGroupHeader = true;
      levelPadding = 1;
      _items = new List<InBase>();
      _value = _data.value;
      UpdateType(_data.type);
      UpdateData(_data.value);
      _isExpanded = this.HasChildren;
      _data.changed += _data_PropertyChanged;
    }
    private InValue(DTopic data, InValue parent, string name, JSC.JSValue value, JSC.JSValue type, Action<InBase, bool> collFunc) {
      _data = data;
      _parent = parent;
      _collFunc = collFunc;
      _path = _parent._path + "." + name;
      base.name = name;
      _items = new List<InBase>();
      _isVisible = true;
      _isExpanded = true; // fill _valueVC
      base.IsGroupHeader = false;
      levelPadding = _parent.levelPadding + 8;
      _value = value;
      UpdateType(type);
      UpdateData(value);
      _isExpanded = this.HasChildren;
    }
    private InValue(JSC.JSValue manifest, InValue parent) {
      this._parent = parent;
      base._manifest = manifest;
      base._collFunc = parent._collFunc;
      base.name = string.Empty;
      this._path = _parent._path + ".";
      base.levelPadding = _parent == null ? 1 : _parent.levelPadding + 8;
      base._items = new List<InBase>();
      base.IsEdited = true;
    }

    private void UpdateData(JSC.JSValue val) {
      _value = val;
      if(_value.ValueType == JSC.JSValueType.Object) {
        InValue vc;
        int i;
        foreach(var kv in _value.OrderBy(z => z.Key)) {
          vc = _items.OfType<InValue>().FirstOrDefault(z => z.name == kv.Key);
          if(vc != null) {
            vc.UpdateData(kv.Value);
          } else {
            for(i = _items.Count - 1; i >= 0; i--) {
              if(string.Compare(_items[i].name, kv.Key) < 0) {
                break;
              }
            }
            JSC.JSValue cs;
            {
              JSC.JSValue pr;
              if(_manifest == null || (pr = _manifest["Fields"] as JSC.JSValue).ValueType != JSC.JSValueType.Object || (cs = pr[kv.Key]).ValueType != JSC.JSValueType.Object) {
                cs = null;
              }
            }
            var ni = new InValue(_data, this, kv.Key, kv.Value, cs, _collFunc);
            _items.Insert(i + 1, ni);
            if(_isVisible && _isExpanded) {
              _collFunc(ni, true);
            }
          }
        }
        var keys = _value.Select(z => z.Key).ToArray();
        for(i = _items.Count - 1; i >= 0; i--) {
          if(!keys.Contains(_items[i].name)) {
            if(_isVisible && _isExpanded) {
              _items[i].Deleted();
            }
            _items.RemoveAt(i);
          }
        }
      }
      if(editor == null) {
        editor = InspectorForm.GetEditor(_editorName, this, _manifest);
        PropertyChangedReise("editor");
      } else {
        editor.ValueChanged(_value);
      }
    }
    private void ChangeValue(string name, JSC.JSValue val) {
      if(_value.ValueType == JSC.JSValueType.Object) {
        var jo = JSC.JSObject.CreateObject();
        foreach(var kv in _value.OrderBy(z => z.Key)) {
          if(kv.Key == name) {
            if(val != null) {
              jo[kv.Key] = val;
            } else {
              jo.DeleteProperty(kv.Key);
            }
          } else {
            jo[kv.Key] = kv.Value;
          }
        }
        if(val != null && !jo.GetProperty(name, JSC.PropertyScope.Own).Defined) {
          jo[name] = val;
        }
        if(_parent == null) {
          _data.SetValue(jo);
        } else {
          _parent.ChangeValue(this.name, jo);
        }
      } else {
        throw new NotImplementedException();
      }
    }
    private void _data_PropertyChanged(DTopic.Art art, DTopic child) {
      if(art == DTopic.Art.type) {
        UpdateType(_data.type);
      } else if(art == DTopic.Art.value) {
        _value = _data.value;
        UpdateData(_data.value);
      }
    }

    #region InBase Members
    public override void FinishNameEdit(string name) {
      if(_data == null) {
        if(!string.IsNullOrEmpty(name)) {
          var def = _manifest["default"];
          _parent.ChangeValue(name, def.Defined ? def : JSC.JSValue.Null);
        }
        _parent._items.Remove(this);
        _collFunc(this, false);
      } else {
        IsEdited = false;
        PropertyChangedReise("IsEdited");
        throw new NotImplementedException("InValue.Move");
      }
    }
    protected override void UpdateType(JSC.JSValue type) {
      base.UpdateType(type);
      if(_manifest != null && _manifest.ValueType == JSC.JSValueType.Object && !_manifest.IsNull) {
        var pr = _manifest["Fields"] as JSC.JSValue;
        if(pr != null) {
          InValue vc;
          foreach(var kv in pr) {
            vc = _items.OfType<InValue>().FirstOrDefault(z => z.name == kv.Key);
            if(vc != null) {
              vc.UpdateType(kv.Value);
            }
          }
        }
      }
    }
    public override bool HasChildren {
      get { return _items.Any(); }
    }
    public override JSC.JSValue value {
      get {
        return _value;
      }
      set {
        JSL.Date js_d;
        if(value != null && value.ValueType == JSC.JSValueType.Date && (js_d = value.Value as JSL.Date) != null && Math.Abs((js_d.ToDateTime() - new DateTime(1001, 1, 1, 12, 0, 0)).TotalDays) < 1) {
          value = JSC.JSObject.Marshal(DateTime.UtcNow);
        }
        if(_parent == null) {
          _data.SetValue(value);
        } else {
          _parent.ChangeValue(name, value);
        }
      }
    }
    public override DTopic Root {
      get { return _data.Connection.root; }
    }
    public override int CompareTo(InBase other) {
      var o = other as InValue;
      return o == null ? -1 : this._path.CompareTo(o._path);
    }
    #endregion InBase Members

    #region ContextMenu
    public override ObservableCollection<Control> MenuItems(System.Windows.FrameworkElement src) {
      var l = new ObservableCollection<Control>();
      JSC.JSValue v1, v2;
      MenuItem mi;
      if(!base.IsReadonly && _value.ValueType == JSC.JSValueType.Object) {
        MenuItem ma = new MenuItem() { Header = "Add" };
        if(_manifest != null && (v1 = _manifest["Fields"]).ValueType == JSC.JSValueType.Object) {
          foreach(var kv in v1.Where(z => z.Value != null && z.Value.ValueType == JSC.JSValueType.Object && z.Value["default"].Defined)) {
            if(_items.Any(z => z.name == kv.Key)) {
              continue;
            }
            mi = new MenuItem();
            mi.Header = kv.Key;
            if((v2 = kv.Value["icon"]).ValueType == JSC.JSValueType.String) {
              mi.Icon = new Image() { Source = App.GetIcon(v2.Value as string), Height = 16, Width = 16 };
            }
            if((v2 = kv.Value["hint"]).ValueType == JSC.JSValueType.String) {
              mi.ToolTip = v2.Value;
            }
            mi.Tag = kv.Value;
            mi.Click += miAdd_Click;
            ma.Items.Add(mi);
          }
        } else {
          if(_data.Connection.TypeManifest != null) {
            foreach(var t in _data.Connection.TypeManifest.parent.children) {
              if(t.name == "Manifest" || (v1 = t.value).ValueType != JSC.JSValueType.Object || v1.Value == null) {
                continue;
              }
              mi = new MenuItem() { Header = t.name, Tag = v1 };
              if((v2 = v1["icon"]).ValueType == JSC.JSValueType.String) {
                mi.Icon = new Image() { Source = App.GetIcon(v2.Value as string), Height = 16, Width = 16 };
              } else {
                mi.Icon = new Image() { Source = App.GetIcon(t.name), Height = 16, Width = 16 };
              }
              if((v2 = v1["hint"]).ValueType == JSC.JSValueType.String) {
                mi.ToolTip = v2.Value;
              }
              mi.Click += miAdd_Click;
              ma.Items.Add(mi);
            }
          }
        }
        if(ma.HasItems) {
          l.Add(ma);
          l.Add(new Separator());
        }
      }
      mi = new MenuItem() { Header = "Delete", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Delete.png"), Width = 16, Height = 16 } };
      mi.IsEnabled = _parent != null && !IsRequired;
      mi.Click += miDelete_Click;
      l.Add(mi);
      return l;
    }

    private void miAdd_Click(object sender, RoutedEventArgs e) {
      var mi = sender as MenuItem;
      JSC.JSValue decl;
      if(!IsReadonly && mi != null && (decl = mi.Tag as JSC.JSValue) != null) {
        if(!IsExpanded) {
          IsExpanded = true;
          base.PropertyChangedReise("IsExpanded");
        }
        bool pc_items = false;
        if((bool)decl["willful"]) {
          if(_items == null) {
            lock(this) {
              if(_items == null) {
                _items = new List<InBase>();
                pc_items = true;
              }
            }
          }
          var ni = new InValue(decl, this);
          _items.Insert(0, ni);
          _collFunc(ni, true);
        } else {
          if(decl != null) {
            var def = decl["default"];
            this.ChangeValue(mi.Header as string, def.Defined ? def : JSC.JSValue.Null);
          }
        }
        if(pc_items) {
          PropertyChangedReise("items");
        }
      }
    }
    private void miDelete_Click(object sender, RoutedEventArgs e) {
      if(_parent != null && !IsRequired) {
        _parent.ChangeValue(name, null);
      }
    }
    #endregion ContextMenu

    #region IDisposable Member
    public void Dispose() {
      if(_parent == null) {
        _data.changed -= _data_PropertyChanged;
      }
    }
    #endregion IDisposable Member

    public override string ToString() {
      return (_data != null ? _data.fullPath : "<new>") + "." + _path;
    }
  }
}
