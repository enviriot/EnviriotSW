///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Collections.Concurrent;

namespace X13.Logram {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 5)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Logram")]
  class LogramPl : IPlugModul {
    private Topic _owner;
    private Topic _verbose;
    private Dictionary<Topic, ILoItem> _items;
    private ConcurrentQueue<ILoItem> _TaskIn;
    private List<ILoItem> _TaskPr;
    private int _curIdx;

    public LogramPl() {
      _items = new Dictionary<Topic, ILoItem>();
      _TaskIn = new ConcurrentQueue<ILoItem>();
      _TaskPr = new List<ILoItem>();
      _curIdx = 0;
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
      Topic.Subscribe(SubFunc);
    }
    public void Tick() {
      ILoItem it;
      _curIdx = -1;
      while(_TaskIn.TryDequeue(out it)) {
        try {
          it.Tick1();
          if(it.Disposed) {
            _items.Remove(it.Owner);
          } else {
            EnqueuePr(it);
          }
        }
        catch(Exception ex) {
          Log.Warning("{0}.Tick1() - {1}", it.ToString(), ex.Message);
        }
      }
      _curIdx = 0;
      while(_curIdx< _TaskPr.Count) {
        it = _TaskPr[_curIdx++];
        try {
          it.Tick2();
        }
        catch(Exception ex) {
          Log.Warning("{0}.Tick2() - {1}", it.ToString(), ex.Message);
        }
      }
      _TaskPr.Clear();
      _curIdx = -1;
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

    internal LoVariable GetVariable(Topic t) {
      LoVariable v;
      ILoItem it;
      if(_items.TryGetValue(t, out it) && ( v = it as LoVariable )!=null) {
        return v;
      }
      v=new LoVariable(this, t);
      _items[t] = v;
      v.ManifestChanged();
      return v;
    }
    internal void EnqueuePr(ILoItem it) {
      int idx = _TaskPr.BinarySearch(it);
      if(idx<0) {
        idx = ~idx;
        if(_curIdx <= idx) {
          _TaskPr.Insert(idx, it);
        } else {
          _TaskIn.Enqueue(it);
        }
      } else if(_curIdx >= idx) {
        _TaskIn.Enqueue(it);
      }
    }
    internal void EnqueueIn(ILoItem it) {
      _TaskIn.Enqueue(it);
    }

    private void BindCh(Topic t, Perform.Art a) {
      ILoItem it;
      LoVariable v = null;
      if(( !_items.TryGetValue(t, out it) || ( v = it as LoVariable )==null ) && a == Perform.Art.create) {
        v = new LoVariable(this, t);
        _items[t] = v;
      }
      if(v!=null) {
        v.ManifestChanged();
      }
    }
    private void BlockCh(Topic t, Perform.Art a) {
      ILoItem it;
      LoBlock v = null;
      if(!_items.TryGetValue(t, out it) || ( v = it as LoBlock )==null) {
        if(a == Perform.Art.create) {
          v = new LoBlock(this, t);
          _items[t] = v;
        }
      } else {
        v.ManifestChanged();
      }
    }
    private void SubFunc(Perform p) {
      ILoItem it;
      if(!_items.TryGetValue(p.src, out it)) {
        if(p.art==Perform.Art.create) {
          LoBlock lb;
          if(p.src.parent!=null && _items.TryGetValue(p.src.parent, out it) && ( lb = it as LoBlock )!=null) {
            lb.GetPin(p.src);
          }
        }
        return;
      }
      if(p.art==Perform.Art.changedState) {
        it.SetValue(p.src.GetState(), p.prim);
      } else if(p.art==Perform.Art.remove) {
        _TaskIn.Enqueue(it);
      }
    }
  }
}
