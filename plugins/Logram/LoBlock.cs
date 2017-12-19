///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

/*
namespace X13.Logram {
  internal class LoBlock : ILoItem {
    private LogramPl _pl;
    private Topic _owner;
    private SubRec _pinsSR;
    private JSC.Context _ctx;
    private JSC.JSValue _self;
    private JSL.Function _calcFunc;

    public LoBlock(LogramPl pl, Topic owner) {
      this._pl = pl;
      this._owner = owner;
      this.Changed(null, null);
      _pinsSR = this._owner.Subscribe(SubRec.SubMask.Chldren | SubRec.SubMask.Value, PinsChanged);
    }

    #region ILoItem Members
    public Topic Owner { get{ return _owner; } }
    public ILoItem Source { get; set; }
    public int Layer { get; set; }
    public JSC.JSValue GetValue();
    public void SetValue(JSC.JSValue value, Topic prim);
    public ILoItem[] Route { get; set; }
    public void Tick(){
    }
    public bool Disposed { get; private set;}
    #endregion ILoItem Members
 
    private void PinsChanged(Perform p, SubRec sr) {
      if((p.prim==_owner && p.art!=Perform.Art.subAck) || p.art == Perform.Art.subscribe) {
        return;
      } // perform only after SubAck
      if(_calcFunc != null) {
        string pr = p.src.parent == _owner ? p.src.name : p.src.path;
        try {
          _calcFunc.Call(_self, new JSC.Arguments { pr });
        }
        catch(Exception ex) {
          Log.Warning("{0}.Calculate({1}) - {2}", _owner.path, pr, ex.Message);
        }
      }
    }

    #region IloItem Members
    public int Layer {
      get {
        return -1;
      }
    }
    public void Changed(Perform p, SubRec sr) {
      JSC.JSValue jSrc;
      var jType = _owner.GetField("type");
      Topic tt;
      if(jType.ValueType == JSC.JSValueType.String && jType.Value != null && Topic.root.Get("$YS/TYPES", false).Exist(jType.Value as string, out tt)
        && (jSrc = JsLib.GetField(tt.GetState(), "src")).ValueType == JSC.JSValueType.String) {
      } else {
        jSrc = null;
      }
      if(jSrc != null) {
        try {
          _ctx = new JSC.Context(JsExtLib.Context);
          _ctx.DefineVariable("setTimeout").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetTimeout)));
          _ctx.DefineVariable("setInterval").Assign(JSC.JSValue.Marshal(new Func<JSC.JSValue, int, JSC.JSValue>(SetInterval)));

          var f = _ctx.Eval(jSrc.Value as string) as JSL.Function;
          if(f != null) {
            if(f.RequireNewKeywordLevel == JSL.RequireNewKeywordLevel.WithNewOnly) {
              this._self = JSC.JSObject.create(new JSC.Arguments { f.prototype });
            } else {
              this._self = JSC.JSObject.CreateObject();
            }
            var cf = _self.GetProperty("Calculate");
            _calcFunc = (cf as JSL.Function) ?? (cf.Value as JSL.Function);
            
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
        Log.Warning("{0} constructor is not defined", _owner.path);
      }
    }
    #endregion IloItem Members

    #region JsFunctions
    private JSC.JSValue SetTimeout(JSC.JSValue func, int to) {
      return JsExtLib.SetTimer(func, to, -1, _ctx);
    }
    private JSC.JSValue SetInterval(JSC.JSValue func, int interval) {
      return JsExtLib.SetTimer(func, interval, interval, _ctx);
    }
    private JSC.JSValue GetState(string path) {
      Topic t;
      if(_owner.Exist(path, out t)) {
        return t.GetState();
      }
      return JSC.JSValue.NotExists;
    }
    private void SetState(string path, JSC.JSValue value) {
      if(!_owner.disposed) {
        Topic t = _owner.Get(path, true, _owner);
        t.SetState(value, _owner);
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

  }
}*/
