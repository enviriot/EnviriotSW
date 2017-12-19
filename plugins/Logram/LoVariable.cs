///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;

namespace X13.Logram {
  internal class LoVariable : ILoItem {
    private LogramPl _pl;
    private Topic _owner;
    private ILoItem _src, _src_new;
    private JSC.JSValue _value, _value_new;
    private int _layer, _layer_new;
    private List<ILoItem> _links;
    private Topic _prim;

    public LoVariable(LogramPl pl, Topic owner) {
      this._pl = pl;
      this._owner = owner;
      _links = new List<ILoItem>();
      _value_new = _owner.GetState();
      _pl.Enqueue(this);
    }

    public void ManifestChanged() {
      string ss = JsLib.OfString(_owner.GetField("cctor.LoBind"), null);
      Topic st;
      LoVariable sv;
      if(ss!=null && _owner.Exist(ss, out st)) {
        sv = _src as LoVariable;
        if(sv==null || sv._owner!=st) {
          sv = _pl.GetVariable(st);
        }
      } else {
        sv = null;
      }
      Source = sv;
    }
    public void AddLink(LoVariable v) {
      _links.Add(v);
    }
    public void DeleteLink(LoVariable v) {
      _links.Remove(v);
      if(!_links.Any() && _src==null) {
        Disposed = true;
        _pl.Enqueue(this);
      }
    }

    #region ILoItem Members
    public Topic Owner { get { return _owner; } }
    public ILoItem Source {
      get {
        return _src;
      }
      set {
        if(value!=_src) {
          _src_new = value;
          _pl.Enqueue(this);
        }
      }
    }
    public int Layer {
      get {
        return _layer;
      }
      set {
        if(_layer!=value) {
          _layer=value;
          _pl.Enqueue(this);
        }
      }
    }
    public JSC.JSValue GetValue() {
        return _value;
      }
    public void SetValue(JSC.JSValue value, Topic prim){
        if(!JSC.JSValue.ReferenceEquals(_value, value)) {
          if(_pl.CurrentLayer<=_layer) {
            _value_new = value;
            _prim = prim;
            _pl.Enqueue(this);
          } else {
            _owner.SetState(value, _prim); // process in next tick
          }
        }
      }
    
    public ILoItem[] Route { get; set; }
    public bool Disposed { get; private set; }

    public void Tick() {
      if(_owner.disposed) {
        for(int i = _links.Count-1; i>=0; i--) {
          _links[i].Owner.SetField("cctor.LoBind", null, _owner);
        }
        Disposed = true;
        _src_new = null;
      }
      if(_src != _src_new) {
        var svo = Interlocked.Exchange(ref _src, _src_new) as LoVariable;
        if(svo != null) {
          svo.DeleteLink(this);
        }
        if(_src!=null) {
          _layer_new = _src.Layer;
          Route = _src.Route;
          if(_owner.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
            _owner.ClearAttribute(Topic.Attribute.Saved);
          }
          if(( svo = _src as LoVariable )!=null) {
            svo.AddLink(this);
            _value_new = svo._value;
            _prim = svo._owner;
          }
        } else {
          _layer_new = 0;
          if(!_owner.disposed) {
            _owner.SetAttribute(Topic.Attribute.DB);
          }
          if(!_links.Any()) {
            Disposed = true;
          }
        }
      }
      if(_layer!=_layer_new) {
        _layer = _layer_new;
        for(int i = _links.Count-1; i>=0; i--) {
          _links[i].Layer = _layer;
        }
      }
      if(!JSC.JSValue.ReferenceEquals(_value, _value_new)) {
        _value = _value_new;
        _owner.SetState(_value, _prim);
        _prim = null;
        for(int i = _links.Count-1; i>=0; i--) {
          _links[i].SetValue(_value, _owner);
        }
      }
    }
    #endregion ILoItem Members

    #region IComparable<ILoItem> Members
    public int CompareTo(ILoItem other) {
      if(other == null) {
        return -1;
      }
      if(this._layer!=other.Layer) {
        return this._layer.CompareTo(other.Layer);
      }
      return this._owner.path.CompareTo(other.Owner.path);
    }
    #endregion IComparable<ILoItem> Members

    public override string ToString() {
      return "["+_layer.ToString("000") + "] " + _owner.path;
    }
  }
}
