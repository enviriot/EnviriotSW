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

    /// <summary>The manifest fields Logram owns on somebody else's topic.</summary>
    /// <remarks>Both were "cctor.LoBlock" and "cctor.LoBind" while a registry called CCtor read
    /// that field on the core's behalf. That registry is gone, so the name meant nothing any
    /// more - these are Logram's data, and they sit in Logram's namespace beside Logram.top,
    /// Logram.left and Logram.trace. Old trees are NOT read: LogramPl.Start says so out loud
    /// instead, see WarnAboutOldFields.</remarks>
    internal const string FLD_BLOCK = "Logram.block";
    internal const string FLD_BIND = "Logram.bind";
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
    }
    public void Start() {
      _verboseSR = JsExtLib.EnsureCfg(Owner, "verbose",
        Topic.Attribute.Required | Topic.Attribute.DB, v => _verbose = v, VerboseDefault);
      _allSub = Topic.Subscribe(SubFunc);
      // The tree is older than this plugin: PersistentStorage is priority 2 and restores everything
      // before Logram, priority 5, is even initialised. The subscription above only reports what
      // happens next, so what is already there has to be walked once. Parents come before children
      // in this order, which is what LoBlock's constructor needs - it collects its pins from the
      // children that exist by then.
      foreach(Topic t in Topic.root.all) {
        Claim(t);
      }
      WarnAboutOldFields();
    }

    /// <summary>Says so, loudly, when the tree still carries the pre-rename field names.</summary>
    /// <remarks>Reading "cctor.LoBlock" as well would be the easy answer and the wrong one - a
    /// compatibility read is the kind of prop that never comes out again. But a forgotten migration
    /// is every block and every wire silently gone, which is worse than either, so the one thing
    /// that must not happen is silence. Counted over the same walk Claim uses, and reported once.
    /// <para>The topic's own manifest and a type topic's STATE are both checked: the value sits in
    /// the manifest on an instance and in the state on a descriptor, and a tree can have one
    /// without the other.</para></remarks>
    private static void WarnAboutOldFields() {
      int n = 0;
      foreach(Topic t in Topic.root.all) {
        if(t.GetField("cctor.LoBlock").Defined || t.GetField("cctor.LoBind").Defined
          || t.GetState().Field("cctor.LoBlock").Defined) {
          n++;
        }
      }
      if(n > 0) {
        Log.Error("{0} topics still carry cctor.LoBlock / cctor.LoBind - blocks and wires will NOT load until Output/bin_a/Migrate has been run", n);
      }
    }

    /// <summary>Materialises the topic if it names one of Logram's handlers, itself or by type.</summary>
    /// <remarks>This is the half of CCtor that Logram actually used. The registry it replaces knew
    /// a field name, a type root and a difference algorithm on the core's behalf, and resolved the
    /// type only to answer "call LoBlock" - while LoBlock.ManifestChanged resolved the very same
    /// type again, for the "src" it really wanted. One resolution in one layer now.</remarks>
    private bool Claim(Topic t) {
      if(_items.TryGetValue(t, out _)) return true;
      if(Names(t, FLD_BLOCK)) {
        _items.Add(t, new LoBlock(this, t));
        return true;
      }
      if(Names(t, FLD_BIND)) {
        var v = new LoVariable(this, t);
        _items.Add(t, v);
        v.ManifestChanged();
        return true;
      }
      return false;
    }


    /// <summary>Whether a written field path can have changed the field we care about.</summary>
    /// <remarks>Both directions, and each for its own reason. A write INSIDE the field changes it -
    /// "Logram.bind.x" changes "Logram.bind". A write of a PARENT changes it too: writing the whole
    /// "Logram" object replaces "bind" along with everything else. Nothing does that today -
    /// LogramViewProvider.Commit deliberately merges "Logram.&lt;key&gt;" one dotted path at a time
    /// rather than replacing the object - but a miss here would be silent, and this costs a
    /// comparison.
    /// <para>Matched on the dot the manifest nests with, never on raw prefix: otherwise "typeface"
    /// would count as a write to "type".</para></remarks>
    private static bool Affects(string written, string field) {
      return written != null
        && (string.Equals(written, field, StringComparison.Ordinal)
          || written.StartsWith(field + ".", StringComparison.Ordinal)
          || field.StartsWith(written + ".", StringComparison.Ordinal));
    }

    /// <summary>Whether the topic names this handler in its own manifest or through its type.</summary>
    /// <remarks>On a TYPE topic the field lives in its STATE, not its manifest - see the
    /// descriptors under /$YS/TYPES/LoBlock in base.xst and in the published catalog.</remarks>
    private static bool Names(Topic t, string field) {
      if(t.GetField(field).Defined) {
        return true;
      }
      var jType = t.GetField("type");
      if(!jType.Is<string>() || jType.Value == null) {
        return false;
      }
      Topic types = Topic.root.Get("$YS/TYPES", false), tt;   // null until PersistentStorage seeds it
      if(types == null || !types.Exist(jType.Value as string, out tt)) {
        return false;
      }
      return tt.GetState().Field(field).Defined;
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

    private void SubFunc(TopicEvent p) {
      ILoItem it;
      if(!_items.TryGetValue(p.Source, out it)) {
        // Created covers a block added with its type already in the manifest - which is how the
        // IDE adds one, cloning the descriptor's manifest - and FieldChanged covers a plain topic
        // given a type or Logram.block/bind afterwards. Claiming does not end the pass: a claimed one can
        // still be a pin of its parent, and it was reached by both mechanisms while they were two.
        if(p.Kind == EventKind.Created || p.Kind == EventKind.FieldChanged) {
          Claim(p.Source);
        }
        if(p.Kind==EventKind.Created) {
          ILoItem parent;
          LoBlock lb;
          if(p.Source.parent!=null && _items.TryGetValue(p.Source.parent, out parent) && ( lb = parent as LoBlock )!=null) {
            lb.GetPin(p.Source);
          }
        }
        return;
      }
      if(p.Kind==EventKind.StateChanged) {
        it.SetValue(p.Source.GetState(), p.Author);
      } else if(p.Kind==EventKind.Removed) {
        _TaskIn.Enqueue(it);
      } else if(p.Kind==EventKind.FieldChanged) {
        // Only when the field that DEFINES the item changed. CCtor delivered a difference, so it
        // never said "the manifest moved" - and neither may this: LoVariable.ManifestChanged
        // ASSIGNS Source from Logram.bind, and a block's own output pin has no bind of its own
        // (GetPin sets Source = the block for any pin whose type declares ddr > 0). Calling it on
        // an unrelated write - Logram.trace on a traced pin, Logram.top/left when the block is
        // dragged - therefore set Source to null and the block's output stopped propagating, with
        // nothing in the log but "constructor is not defined" from the block's own redundant call.
        var lb = it as LoBlock;
        if(lb != null) {
          if(Affects(p.FieldPath, "type") || Affects(p.FieldPath, FLD_BLOCK)) {
            lb.ManifestChanged();
          }
        } else {
          var lv = it as LoVariable;
          if(lv != null && Affects(p.FieldPath, FLD_BIND)) {
            lv.ManifestChanged();
          }
        }
      }
    }
  }
}
