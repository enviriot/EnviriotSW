﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using X13.Data;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.UI {
  internal class InManifest : InBase {
    private static int SIGNATURE_CNT = 0;

    private DTopic _data;
    private DTopic _tManifest;
    private InManifest _parent;
    private JSC.JSValue _value;
    private string _path;
    private int _signature;

    public InManifest(DTopic data, Action<InBase, bool> collFunc) {
      _signature = System.Threading.Interlocked.Increment(ref SIGNATURE_CNT);
      _data = data;
      _parent = null;
      base._compactView = false;
      base._collFunc = collFunc;
      name = "Manifest";
      _path = string.Empty;
      base._isVisible = true;
      base._isExpanded = true;
      base.IsGroupHeader = true;
      base.levelPadding = 1;
      base._items = new List<InBase>();
      _data.changed += _data_PropertyChanged;
      _data.GetAsync("/$YS/TYPES/Ext/Manifest").ContinueWith(ManifestLoaded, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ManifestLoaded(Task<DTopic> td) {
      if(td.IsCompleted && !td.IsFaulted && td.Result != null) {
        _tManifest = td.Result;
        _tManifest.changed += Manifest_changed;
        UpdateType(_tManifest.State, _data.Manifest);
        base._isExpanded = this.HasChildren;
      }
    }
    private InManifest(InManifest parent, string name, JSC.JSValue value, JSC.JSValue type) {
      _signature = System.Threading.Interlocked.Increment(ref SIGNATURE_CNT);
      this._parent = parent;
      base._compactView = false;
      this._data = _parent._data;
      base._collFunc = _parent._collFunc;
      base.name = name;
      this._path = string.IsNullOrEmpty(_parent._path) ? name : (_parent._path + "." + name);
      base._isVisible = _parent._isExpanded;
      base._items = new List<InBase>();
      base.IsGroupHeader = false;
      base.levelPadding = _parent.levelPadding + 8;
      this._value = value;
      UpdateType(type, value);
    }
    private InManifest(JSC.JSValue manifest, InManifest parent) {
      _signature = System.Threading.Interlocked.Increment(ref SIGNATURE_CNT);
      this._parent = parent;
      base._compactView = false;
      base._manifest = manifest;
      base._collFunc = parent._collFunc;
      base.name = string.Empty;
      this._path = _parent._path + ".";
      base.levelPadding = _parent.levelPadding + 8;
      base._items = new List<InBase>();
      base.IsEdited = true;
    }
    
    private void _data_PropertyChanged(DTopic.Art art, DTopic child) {
      if(art == DTopic.Art.type) {
        _value = _data.Manifest;
        UpdateType(_tManifest != null ? _tManifest.State : null, _data.Manifest);
      }
    }
    private void Manifest_changed(DTopic.Art art, DTopic src) {
      if(art == DTopic.Art.value) {
        UpdateType(_tManifest != null ? _tManifest.State : null, _value);
      }
    }
    private void SetFieldResp(Task<JSC.JSValue> r) {
      if(r.IsCompleted) {
        if(r.IsFaulted) {
          UpdateType(_tManifest != null ? _tManifest.State : null, value);
          Log.Warning("{0}.{1} - {2}", _data.fullPath, _path, r.Exception.InnerException);
        }
      }
    }
    private void UpdateType(JSC.JSValue type, JSC.JSValue val) {
      _value = val;

      #region Trace
      /*{
        StringBuilder sb = new StringBuilder();
        sb.Append(this.ToString());
        sb.Append(" $UpdateType( m{");
        if(type == null) {
          sb.Append("null");
        } else {
          foreach(var kv in type) {
            sb.AppendFormat("{0}:{1},", kv.Key, kv.Value.ValueType == JSC.JSValueType.Object ? "Object" : kv.Value.ToString());
          }
          sb.Append("}");
          if(type.__proto__.ValueType == JSC.JSValueType.Object && type.__proto__.Any()) {
            sb.Append(" mp{");
            foreach(var kv in type.__proto__) {
              sb.AppendFormat("{0}:{1},", kv.Key, kv.Value.ValueType == JSC.JSValueType.Object ? "Object" : kv.Value.ToString());
            }
            sb.Append("}");
          }
        }
        if(val!=null && val.ValueType == JSC.JSValueType.Object) {
          sb.Append(", s{");
          foreach(var kv in val) {
            sb.AppendFormat("{0}:{1},", kv.Key, kv.Value.ValueType == JSC.JSValueType.Object ? "Object" : kv.Value.ToString());
          }
          sb.Append("}");
          if(val.__proto__.ValueType == JSC.JSValueType.Object && val.__proto__.Any()) {
            sb.Append(" proto{");
            foreach(var kv in val.__proto__) {
              sb.AppendFormat("{0}:{1},", kv.Key, kv.Value.ValueType == JSC.JSValueType.Object ? "Object" : kv.Value.ToString());
            }
            sb.Append("}");
          }
        } else {
          sb.Append(", s=" + JSL.JsLib.Stringify(_value, null, null));
        }
        sb.Append(")");
        Log.Debug("{0}", sb.ToString());
      }*/
      #endregion Trace

      bool o_hc = _items.Any();
      base.UpdateType(type);
      base.editor.ValueChanged(_value);

      if(_value.ValueType == JSC.JSValueType.Object) {
        InManifest vc;
        int i;
        JSC.JSValue cs, cs_mi, cs_p;
        foreach(var kv in _value.OrderBy(z => z.Key)) {
          if(_manifest == null || _manifest.ValueType != JSC.JSValueType.Object || _manifest.Value == null || (cs_mi = _manifest["mi"]).ValueType != JSC.JSValueType.Object || cs_mi.Value == null || (cs = cs_mi[kv.Key]).ValueType != JSC.JSValueType.Object || cs.Value == null) {
            cs = JSC.JSObject.CreateObject();
          }
          if((cs_mi = (IsGroupHeader ? _value : _manifest)["mi"]).ValueType == JSC.JSValueType.Object && cs_mi.Value != null) {
            if((cs_p = cs_mi[kv.Key]).ValueType == JSC.JSValueType.Object && cs_p.Value != null && cs != cs_p) {
              if(cs["mi"].ValueType == JSC.JSValueType.Object) {
                cs["mi"].__proto__ = cs_p["mi"].ToObject();
              }
              cs.__proto__ = cs_p.ToObject();
            }
          }
          vc = _items.OfType<InManifest>().FirstOrDefault(z => z.name == kv.Key);
          if(vc != null) {
            vc.UpdateType(cs, kv.Value);
          } else {
            var ni = new InManifest(this, kv.Key, kv.Value, cs);
            for(i = _items.Count - 1; i >= 0; i--) {
              if(string.Compare(_items[i].name, kv.Key) < 0) {
                break;
              }
            }
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
      if(o_hc != _items.Any()) {
        if(o_hc) {  // now has now children
          IsExpanded = false;
        }
        PropertyChangedReise("HasChildren");
      }
    }

    #region InBase Members
    public override void FinishNameEdit(string name) {
      if(_data == null) {
        if(!string.IsNullOrEmpty(name)) {
          var def = _manifest["default"];
          _parent._data.SetField(_parent.IsGroupHeader ? name : (_parent._path + "." + name), def.Defined ? def : JSC.JSValue.Null);
        }
        _parent._items.Remove(this);
        _collFunc(this, false);
      } else {
        IsEdited = false;
        PropertyChangedReise("IsEdited");
        throw new NotImplementedException("InValue.Move");
      }
    }
    public override bool HasChildren { get { return _items.Any(); } }
    public override JSC.JSValue value {
      get {
        return _value;
      }
      set {
        _data.SetField(_path, value).ContinueWith(SetFieldResp, TaskScheduler.FromCurrentSynchronizationContext());
      }
    }
    public override DTopic Root {
      get { return _data.Connection.root; }
    }
    public override int CompareTo(InBase other) {
      var o = other as InManifest;
      if(o == null) {
        return (other is InValue) ? 1 : -1;
      }
      return this._path.CompareTo(o._path);
    }
    #endregion InBase Members

    #region ContextMenu
    public override ObservableCollection<Control> MenuItems(FrameworkElement src) {
      var l = new ObservableCollection<Control>();
      JSC.JSValue v1, v2;
      MenuItem mi;
      if(!base.IsReadonly && (_value==null || _value.ValueType == JSC.JSValueType.Object)) {
        MenuItem ma = new MenuItem() { Header = "Add" };
        if(_manifest != null && (v1 = _manifest["mi"]).ValueType == JSC.JSValueType.Object) {
          KeyValuePair<string, JSC.JSValue>[] iArr;
          if(v1.__proto__.ValueType == JSC.JSValueType.Object) {
            iArr = v1.Union(v1.__proto__).ToArray();
          } else {
            iArr = v1.ToArray();
          }

          foreach(var kv in iArr.Where(z => z.Value != null && z.Value.ValueType == JSC.JSValueType.Object && z.Value["default"].Defined)) {
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
          if(_data.Connection.CoreTypes != null) {
            foreach(var t in _data.Connection.CoreTypes.children) {
              if((v1 = t.State).ValueType != JSC.JSValueType.Object || v1.Value == null || !v1["default"].Defined) {
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
        if((bool)decl["willful"]) {
          bool o_hc = _items.Any();

          var ni = new InManifest(decl, this);
          _items.Insert(0, ni);
          _collFunc(ni, true);
          if(!o_hc) {
            PropertyChangedReise("HasChildren");
          }
          if(!IsExpanded) {
            IsExpanded = true;
            base.PropertyChangedReise("IsExpanded");
          }
        } else {
          string fName = mi.Header as string;
          _data.SetField(IsGroupHeader ? fName : _path + "." + fName, decl["default"]);
        }
      }
    }
    private void miDelete_Click(object sender, RoutedEventArgs e) {
      if(!IsRequired && _parent != null) {
        _data.SetField(_path, null);
      }
    }
    #endregion ContextMenu

    #region IDisposable Member
    public override void Dispose() {
      if(_parent == null) {
        _data.changed -= _data_PropertyChanged;
        if(_tManifest != null) {
          _tManifest.changed -= Manifest_changed;
        }
      }
    }
    #endregion IDisposable Member

    public override string ToString() {
      return "/" + _signature.ToString("X4") + "/ " + (_data != null ? _data.fullPath : "<new>") + "." + _path;
    }
  }
}
