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
    private List<ILoItem> _tasks;
    private int _curLayer;

    public LogramPl() {
      _items = new Dictionary<Topic, ILoItem>();
      _tasks = new List<ILoItem>();
      _curLayer = 0;
    }

    public bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }
    public int CurrentLayer { get { return _curLayer; } }

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
      _curLayer = 0;
      while(( it = _tasks.FirstOrDefault() )!=null) {
        _tasks.RemoveAt(0);
        try {
          _curLayer = it.Layer;
          it.Tick();
          if(it.Disposed) {
            _items.Remove(it.Owner);
          }
        }
        catch(Exception ex) {
          Log.Warning("{0}.Tick() - {1}", it.ToString(), ex.Message);
        }
      }
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
    internal void Enqueue(ILoItem it) {
      int idx = _tasks.BinarySearch(it);
      if(idx<0) {
        _tasks.Insert(~idx, it);
      }
    }

    private void BindCh(Topic t, Perform.Art a) {
      ILoItem it;
      LoVariable v = null;
      if((!_items.TryGetValue(t, out it) || (v = it as LoVariable)==null) && a == Perform.Art.create) {
        v = new LoVariable(this, t);
        _items[t] = v;
      }
      v.ManifestChanged();
    }
    private void BlockCh(Topic t, Perform.Art a) {
      /*
      ILoItem it;
      if(_itemsO.TryGetValue(t, out it)) {
        LoBlock b = it as LoBlock;
        if(a == Perform.Art.remove) {
          if(b != null) {
            //b.Remove();
          }
          _itemsO.Remove(t);
        } else if(b != null) {
          b.Changed(null, null);
        }
      } else if(a == Perform.Art.create) {
        _itemsO[t] = new LoBlock(this, t);
      }
      */
    }
    private void SubFunc(Perform p) {
      ILoItem it;
      if(!_items.TryGetValue(p.src, out it)) {
        return;
      }
      if(p.art==Perform.Art.changedState) {
        it.SetValue(p.src.GetState(), p.prim);
      } else if(p.art==Perform.Art.remove) {
        Enqueue(it);
      }
    }
  }
}
