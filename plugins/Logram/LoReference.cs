///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.Logram {
  internal class LoReference : ILoItem {
    private Topic _owner;
    private LogramPl _pl;
    private Topic _src;
    private SubRec _srcSR, _ownSR;

    public LoReference(LogramPl pl, Topic owner) {
      this._pl = pl;
      this._owner = owner;
      this.Changed(null, null);
      _ownSR = this._owner.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, OwnerChanged);
    }

    private void OwnerChanged(Perform p, SubRec sr) {
      if(p.prim != _src && p.art == Perform.Art.changedState) {
        _src.SetState(_owner.GetState(), _owner);
      }
    }
    private void SourceChanged(Perform p, SubRec sr) {
      if(p.prim!=_owner && (p.art == Perform.Art.changedState || p.art == Perform.Art.subscribe)) {
        _owner.SetState(_src.GetState(), _src);
      }
    }


    #region IloItem Members
    public int Layer {
      get {
        return -1;
      }
    }

    public void Changed(Perform p, SubRec sr) {
      var sj = this._owner.GetField("cctor.LoRef");
      string src;
      Topic st;
      if(sj.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(src = sj.Value as string) && _owner.Exist(src, out st)) {
        if(st != _src) {
          st = System.Threading.Interlocked.Exchange(ref _src, st);
          if(st != null) {
            _srcSR.Dispose();
          }
          _srcSR = _src.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.Once, SourceChanged);
        }
      }

    }
    #endregion IloItem Members

  }
}
