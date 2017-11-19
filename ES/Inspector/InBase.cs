///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using X13.Data;

namespace X13.UI {
  public abstract class InBase : NPC_UI, IComparable<InBase> {
    protected bool _isVisible;
    protected JSC.JSValue _manifest;
    protected string _editorName;
    protected bool _isExpanded;
    protected List<InBase> _items;
    protected Action<InBase, bool> _collFunc;

    public double levelPadding { get; protected set; }
    public virtual bool IsExpanded {
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
          }
        }
      }
    }

    public abstract bool HasChildren { get; }
    public abstract DTopic Root { get; }
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
    public bool IsGroupHeader { get; protected set; }
    public bool IsEdited { get; protected set; }
    public string name { get; set; }
    public BitmapSource icon { get; protected set; }
    public IValueEditor editor { get; protected set; }
    public bool IsReadonly { get; private set; }
    public bool IsRequired { get; private set; }

    public abstract JSC.JSValue value { get; set; }
    public abstract ObservableCollection<Control> MenuItems(FrameworkElement src);
    public abstract void FinishNameEdit(string name);
    public void GotFocus(object sender, RoutedEventArgs e) {
      DependencyObject cur;
      ListViewItem parent;
      DependencyObject parentObject;

      for(cur = sender as DependencyObject; cur != null; cur = parentObject) {
        parentObject = VisualTreeHelper.GetParent(cur);
        if((parent = parentObject as ListViewItem) != null) {
          parent.IsSelected = true;
          break;
        }
      }
    }

    protected virtual void UpdateType(JSC.JSValue manifest) {
      this._manifest = manifest;

      string nv = null;
      int attr = 0;
      BitmapSource ni = null;

      if(_manifest != null && _manifest.ValueType == JSC.JSValueType.Object && _manifest.Value!=null) {
        var vv = _manifest["editor"];
        string tmp_s;
        if(vv.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(tmp_s = vv.Value as string)) {
          nv = tmp_s;
        }
        var iv = _manifest["icon"];
        if(iv.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(tmp_s= iv.Value as string)) {
          ni = App.GetIcon(tmp_s);
        }
        JSC.JSValue js_attr;
        if((js_attr = _manifest["attr"]).IsNumber) {
          attr = (int)js_attr;
        }
      }
      IsReadonly = (attr&2)!=0;
      IsRequired = (attr&1)!=0;
      if(nv == null){
        nv = DTopic.JSV2Type(value);
      }
      if(ni == null) {
        if(value.ValueType == JSC.JSValueType.Object && value.Value == null) {
          ni = App.GetIcon((this is InTopic) ? string.Empty : "Null");  // Folder or Null
        }
      }
      if(ni == null) {
        ni = App.GetIcon(nv);
      }

      if(ni != icon) {
        icon = ni;
        PropertyChangedReise("icon");
      }
      if(nv != _editorName) {
        _editorName = nv;
        editor = InspectorForm.GetEditor(_editorName, this, _manifest);
        PropertyChangedReise("editor");
      }
      this.editor.TypeChanged(_manifest);
    }

    public void Deleted() {
      if(_isVisible) {
        if(_isExpanded) {
          foreach(var ch in _items.ToArray()) {
            ch.Deleted();
          }
        }
        _collFunc(this, false);
      }
    }
    public abstract int CompareTo(InBase other);
  }
}
