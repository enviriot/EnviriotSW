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
    private int _layer;
    private List<ILoItem> _links;
    private Topic _prim;

    public LoVariable(LogramPl pl, Topic owner) {
      this._pl = pl;
      this._owner = owner;
      _links = new List<ILoItem>();
      _value = null;
      _value_new = _owner.GetState();
      _pl.EnqueuePr(this);
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
    public void AddLink(ILoItem v) {
      _links.Add(v);
    }
    public void DeleteLink(ILoItem v) {
      _links.Remove(v);
      if(!_links.Any() && _src==null) {
        Disposed = true;
        _pl.EnqueuePr(this);
      }
    }
    public ILoItem Source {
      get {
        return _src;
      }
      set {
        if(value!=_src || (_src!=null && (_src.Layer!=_layer || !Object.ReferenceEquals(_src.Route, Route) ))) {
          _src_new = value;
          _pl.EnqueueIn(this);
        }
      }
    }
    public JSC.JSValue GetValue() {
      return _value??_value_new;  // for 1st Call
    }

    #region ILoItem Members
    public Topic Owner { get { return _owner; } }
    public int Layer {
      get {
        return _layer;
      }
    }
    public void SetValue(JSC.JSValue value, Topic prim) {
      if(!JsLib.Equal(_value, value)) {
        _value_new = value;
        _prim = prim;
        _pl.EnqueuePr(this);
      }
    }

    public ILoItem[] Route { get; set; }
    public bool Disposed { get; private set; }

    public void Tick1() {
      LoBlock bl;
      if(_owner.disposed && !Disposed) {
        Disposed = true;
        for(int i = _links.Count-1; i>=0; i--) {
          if(( bl = _links[i] as LoBlock )!=null) {
            bl.DeletePin(this);
          } else {
            _links[i].Owner.SetField("cctor.LoBind", null, _owner);
          }
        }
        _links.Clear();
        _src_new = null;
      }

      if(_src != _src_new || ( _src!=null && ( _src.Layer!=Math.Abs(_layer) || !Object.ReferenceEquals(_src.Route, Route) ) )) {
        var svo = Interlocked.Exchange(ref _src, _src_new) as LoVariable;
        if(svo != null) {
          svo.DeleteLink(this);
        }
        if(_src!=null) {
          _layer = _src.Layer;
          Route = _src.Route;
          if(_owner.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)) {
            _owner.ClearAttribute(Topic.Attribute.Saved);
          }
          if(( svo = _src as LoVariable )!=null) {
            svo.AddLink(this);
            if(svo._value!=null && svo._value.Defined) {
              _value_new = svo._value;
              _prim = svo._owner;
            }
          }
          if(Route!=null && ( bl = _links.OfType<LoBlock>().FirstOrDefault() )!=null && Route.Contains(bl)) {  // make loop
            if(_layer!=0) {
              _layer = -_layer;
            }
          }
        } else {
          _layer = 0;
          Route = null;
          if(!_owner.disposed) {
            _owner.SetAttribute(Topic.Attribute.DB);
          }
          if(!_links.Any()) {
            Disposed = true;
          }
        }
        if(_layer>=0) {  // if _layer < 0 -> loop
          for(int i = _links.Count-1; i>=0; i--) {
            if(_links[i]!=_src) {
              _pl.EnqueueIn(_links[i]);
            }
          }
        }
        //Log.Debug(this.ToString());
      }
    }
    public void Tick2() {
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
