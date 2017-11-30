///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.Logram {
  internal class LoBinding : ILoItem, IDisposable {
    private Topic _owner;
    private LogramPl _pl;
    private Topic _src;
    private SubRec _srcSR;

    public LoBinding(LogramPl pl, Topic owner) {
      this._pl = pl;
      this._owner = owner;
      this.Changed(null, null);
    }
    private void SourceChanged(Perform p, SubRec sr) {
      if(p.prim != _owner && (p.art == Perform.Art.changedState || p.art == Perform.Art.subscribe)) {
        _owner.SetState(_src.GetState(), _src);
      } else if(p.art == Perform.Art.remove) {
        this._owner.SetField("cctor.LoBind", null, _src);
      }
    }

    #region IloItem Members
    public int Layer {
      get { 
        return -1; 
      }
    }

    public void Changed(Perform p, SubRec sr) {
      var sj = this._owner.GetField("cctor.LoBind");
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
      } else {
        this.Dispose();
      }
    }
    #endregion IloItem Members

    public void Dispose() {
      var st = System.Threading.Interlocked.Exchange(ref _src, null);
      if(st != null) {
        _srcSR.Dispose();
      }
    }
    public bool Disposed { get { return _src == null; } }
  }
}
