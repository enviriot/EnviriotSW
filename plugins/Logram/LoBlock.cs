///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;


namespace X13.Logram {
  internal class LoBlock : ILoItem {
    private LogramPl _pl;
    private Topic _owner, _typeT;
    private List<LoVariable> _pins;
    private int _layer;
    private Topic _prim;


    private JSC.Context _ctx;
    private JSC.JSValue _self;
    private JSL.Function _calcFunc;

    public LoBlock(LogramPl pl, Topic owner) {
      this._pl = pl;
      this._owner = owner;
      _pins = new List<LoVariable>();
      ManifestChanged();
      foreach(var ch in _owner.children) {
        GetPin(ch);
      }
      _pl.EnqueueIn(this);
    }
    public void ManifestChanged() {
      JSC.JSValue jSrc;
      var jType = _owner.GetField("type");
      Topic tt;
      if(jType.ValueType == JSC.JSValueType.String && jType.Value != null && Topic.root.Get("$YS/TYPES", false).Exist(jType.Value as string, out tt)
        && _typeT!=tt && ( jSrc = JsLib.GetField(tt.GetState(), "src") ).ValueType == JSC.JSValueType.String) {
        _typeT = tt;
      } else {
        jSrc = null;
      }
      if(jSrc != null) {

        try {
          _ctx = new JSC.Context(JsExtLib.Context);
          _ctx.DefineVariable("setTimeout").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetTimeout)));
          _ctx.DefineVariable("setInterval").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetInterval)));
          _ctx.DefineVariable("clearTimeout").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue>(ClearTimer)));
          _ctx.DefineVariable("clearInterval").Assign(JSC.JSValue.Marshal(new Action<JSC.JSValue>(ClearTimer)));
          

          var f = _ctx.Eval(jSrc.Value as string) as JSL.Function;
          if(f != null) {
            if(f.RequireNewKeywordLevel == JSL.RequireNewKeywordLevel.WithNewOnly) {
              this._self = JSC.JSObject.create(new JSC.Arguments { f.prototype });
            } else {
              this._self = JSC.JSObject.CreateObject();
            }
            var cf = _self.GetProperty("Calculate");
            _calcFunc = ( cf as JSL.Function ) ?? ( cf.Value as JSL.Function );

            _self["GetState"] = JSC.JSValue.Marshal(new Func<string, JSC.JSValue>(GetState));
            _self["SetState"] = JSC.JSValue.Marshal(new Action<string, JSC.JSValue>(SetState));
            _self["GetField"] = JSC.JSValue.Marshal(new Func<string, string, JSC.JSValue>(GetField));

            if(f.RequireNewKeywordLevel == JSL.RequireNewKeywordLevel.WithNewOnly) {
              _self = f.Construct(_self, new JSC.Arguments());
            } else {
              f.Call(_self, new JSC.Arguments());  // Call constructor
            }
          }
        }
        catch(Exception ex) {
          Log.Warning("{0}.ctor() - {1}", _owner.path, ex.Message);
        }
      } else {
        if(!_owner.disposed) {
          Log.Warning("{0} constructor is not defined", _owner.path);
        }
      }
    }

    public LoVariable GetPin(Topic t) {
      LoVariable v;
      v = _pins.FirstOrDefault(z => z.Owner==t);
      if(v==null) {
        v = _pl.GetVariable(t);
        var ddr = _typeT!=null?JsLib.OfInt(_typeT.GetState(), "Children."+t.name+".ddr", 0):0;
        if(t.parent!=_owner || ddr<=0) {
          v.AddLink(this);
        } else {
          v.Source = this;
        }
        _pins.Add(v);
      }
      return v;
    }
    public void DeletePin(LoVariable v) {
      _pins.Remove(v);
    }

    #region ILoItem Members
    public Topic Owner { get { return _owner; } }
    public int Layer {
      get {
        return _layer;
      }
    }
    public void SetValue(JSC.JSValue value, Topic prim) {
      _prim = prim;
      _pl.EnqueuePr(this);
    }
    public ILoItem[] Route { get; set; }
    public void Tick1() {
      if(_owner.disposed && !Disposed) {
        Disposed = true;
        foreach(var p in _pins.Where(z => z.Owner.parent!=_owner)) {
          p.DeleteLink(this);
        }
        _pins.Clear();
        if(_ctx!=null) {
          JsExtLib.ClearTimeout(_ctx);
        }
        _ctx = null;
        _self=null;
        _calcFunc = null;
        return;
      }
      var ln = 0;
      foreach(var p in _pins.Where(z => z.Source!=this && z.Layer>0)) {
        if(ln<p.Layer) {
          ln = p.Layer;
        }
      }
      ln++;
      if(_layer!=ln) {
        List<ILoItem> route = new List<ILoItem>();
        foreach(var p in _pins.Where(z => z.Source!=this && z.Layer>0)) {
          if(p.Route!=null) {
            route.AddRange(p.Route);
          }
        }
        route.Add(this);
        Route = route.ToArray();
        _layer = ln;
        foreach(var p in _pins.Where(z => z.Source==this)) {
          _pl.EnqueueIn(p);
        }
        //Log.Debug(this.ToString());
      }
    }
    public void Tick2() {
      if(_prim!=null) {
        string pr = _prim.parent == _owner ? _prim.name : _prim.path;
        _prim = null;
        if(_calcFunc != null) {
          try {
            _calcFunc.Call(_self, new JSC.Arguments { pr });
          }
          catch(Exception ex) {
            Log.Warning("{0}.Calculate({1}) - {2}", _owner.path, pr, ex.Message);
          }
        }
      }
    }
    public bool Disposed { get; private set; }
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

    #region JsFunctions
    private JSC.JSValue SetTimeout(JSC.JSValue func, int to) {
      return JsExtLib.SetTimer(func, to, -1, _ctx);
    }
    private void ClearTimer(JSC.JSValue id) {
      JsExtLib.ClearTimeout(id);
    }

    private JSC.JSValue SetInterval(JSC.JSValue func, int interval) {
      return JsExtLib.SetTimer(func, interval, interval, _ctx);
    }
    private JSC.JSValue GetState(string path) {
      Topic t;
      LoVariable v;
      if(_owner.Exist(path, out t)) {
        v=GetPin(t);
        return v.GetValue();
      }
      return JSC.JSValue.NotExists;
    }
    private void SetState(string path, JSC.JSValue value) {
      if(!_owner.disposed) {
        Topic t = _owner.Get(path, true, _owner);
        var v=GetPin(t);
        if(v.Source == this) {
          v.SetValue(value, _owner);
        }
      }
    }
    private JSC.JSValue GetField(string path, string field) {
      Topic t;
      if(_owner.Exist(path, out t)) {
        return t.GetField(field);
      }
      return JSC.JSValue.NotExists;

    }

    #endregion JsFunctions

    public override string ToString() {
      return "["+_layer.ToString("000") + "] " + _owner.path;
    }
  }
}
