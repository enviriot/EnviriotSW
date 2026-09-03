///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using NiL.JS.Extensions;
using System.Collections.Concurrent;

namespace X13.Logram {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 5)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Logram")]
  class LogramPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/Logram";
#if DEBUG
    private const bool VerboseDefault = true;
#else
    private const bool VerboseDefault = false;
#endif
    private Topic _owner;
    private IDisposable _allSub;
    private bool _verbose;
    private SubRec _verboseSR;
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

    public bool verbose { get { return _verbose; } }

    #region IPlugModul Members
    public void Init() {
      CCtor.Register("LoBind", BindCh);
      CCtor.Register("LoBlock", BlockCh);
    }
    public void Start() {
      _verboseSR = JsExtLib.EnsureCfg(Owner, "verbose",
        Topic.Attribute.Required | Topic.Attribute.DB, v => _verbose = v, VerboseDefault);
      _allSub = Topic.Subscribe(SubFunc);
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
        if (it != null) {
          try {
            it.Tick2();
          }
          catch (Exception ex) {
            Log.Warning("{0}.Tick2() - {1}", it.ToString(), ex.Message);
          }
        }
      }
      _TaskPr.Clear();
      _curIdx = -1;
    }
    /// <summary>Stop used to be empty, so nothing here was ever taken down.</summary>
    /// <remarks>The repository callback goes first: SubFunc enqueues into _TaskIn, so clearing the
    /// queues while it is still attached only makes room for the next TopicEvent to refill them.</remarks>
    public void Stop() {
      IDisposable allSub = _allSub;
      _allSub = null;
      if(allSub != null) {
        allSub.Dispose();
      }
      // EnsureCfg hands ownership of the subscription to the caller.
      if(_verboseSR != null) {
        _verboseSR.Dispose();
        _verboseSR = null;
      }
      ILoItem drop;
      while(_TaskIn.TryDequeue(out drop)) {
      }
      _TaskPr.Clear();
      _items.Clear();
    }
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        // Is<bool>, NOT AsBool/AsString: this decides whether the config topic has to be CREATED
        // and seeded. A reader with a default cannot tell "not set yet" from "set to the
        // default", so the topic would never be created. See todo.md.
        if(!Owner.GetState().Is<bool>()) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
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

    private void BindCh(Topic t, EventKind a) {
      ILoItem it;
      LoVariable v = null;
      if(( !_items.TryGetValue(t, out it) || ( v = it as LoVariable )==null ) && a == EventKind.Created) {
        v = new LoVariable(this, t);
        _items[t] = v;
      }
      if(v!=null) {
        v.ManifestChanged();
      }
    }
    private void BlockCh(Topic t, EventKind a) {
      ILoItem it;
      LoBlock v = null;
      if(!_items.TryGetValue(t, out it) || ( v = it as LoBlock )==null) {
        if(a == EventKind.Created) {
          v = new LoBlock(this, t);
          _items[t] = v;
        }
      } else {
        v.ManifestChanged();
      }
    }
    private void SubFunc(TopicEvent p) {
      ILoItem it;
      if(!_items.TryGetValue(p.Source, out it)) {
        if(p.Kind==EventKind.Created) {
          LoBlock lb;
          if(p.Source.parent!=null && _items.TryGetValue(p.Source.parent, out it) && ( lb = it as LoBlock )!=null) {
            lb.GetPin(p.Source);
          }
        }
        return;
      }
      if(p.Kind==EventKind.StateChanged) {
        it.SetValue(p.Source.GetState(), p.Author);
      } else if(p.Kind==EventKind.Removed) {
        _TaskIn.Enqueue(it);
      }
    }
  }
}
