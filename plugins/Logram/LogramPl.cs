///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.Logram {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 5)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Logram")]
  class LogramPl : IPlugModul {
    private Topic _owner;
    private Topic _verbose;
    private Dictionary<Topic, ILoItem> _items;

    public LogramPl() {
      _items = new Dictionary<Topic, ILoItem>();
    }

    public bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }

    #region IPlugModul Members
    public void Init() {
      RPC.Register("LoBind", BindCh);
      RPC.Register("LoBlock", BlockCh);
    }
    public void Start() {
      _owner = Topic.root.Get("/$YS/Logram");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
    }
    public void Tick() {

    }
    public void Stop() {
    }
    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/Logram", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        Topic.root.Get("/$YS/Logram", true).SetState(value);
      }
    }
    #endregion IPlugModul Members

    private void BindCh(Topic t, Perform.Art a) {
      ILoItem it;
      if(_items.TryGetValue(t, out it)) {
        LoBinding b = it as LoBinding;
        if(a == Perform.Art.remove) {
          if(b != null) {
            b.Dispose();
          }
          _items.Remove(t);
        } else if(b != null) {
          b.Changed(null, null);
          if(b.Disposed) {
            _items.Remove(t);
          }
        }
      } else if(a == Perform.Art.create) {
        _items[t] = new LoBinding(this, t);
      }
    }
    private void BlockCh(Topic t, Perform.Art a) {
      ILoItem it;
      if(_items.TryGetValue(t, out it)) {
        LoBlock b = it as LoBlock;
        if(a == Perform.Art.remove) {
          if(b != null) {
            //b.Remove();
          }
          _items.Remove(t);
        } else if(b != null) {
          b.Changed(null, null);
        }
      } else if(a == Perform.Art.create) {
        _items[t] = new LoBlock(this, t);
      }
    }

  }
}
