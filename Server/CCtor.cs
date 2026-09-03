///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;

namespace X13 {
  /// <summary>Type constructors: the "cctor" manifest field, and who gets told when it changes.</summary>
  /// <remarks>Lifted out of Repo, which had no business knowing any of this. The repository is a
  /// tree of topics and a queue of changes; that a field named "cctor" names a handler, that the
  /// handler may also be inherited from a type topic under /$YS/TYPES, and that Logram spells its
  /// wires "LoBind" - none of it is repository mechanics, and the core named all three.
  /// <para>Still driven from Repo.Tick, between applying a batch and publishing it, and that is
  /// deliberate: a handler creates topics synchronously, so running them all before any subscriber
  /// sees anything is what keeps a tick's view of the tree whole. What the core lost is the
  /// knowledge of WHAT it is calling, not the call.</para></remarks>
  public static class CCtor {
    // Concurrent because plugins register from Init()/Start() on the main thread while the tick
    // thread is already invoking them - the same reason the RPC registry is concurrent.
    private static readonly ConcurrentDictionary<string, Action<Topic, EventKind>> _list
      = new ConcurrentDictionary<string, Action<Topic, EventKind>>();

    /// <summary>Registers the handler for one cctor name.</summary>
    /// <remarks>Keeps Dictionary.Add's contract: a duplicate name is a bug, not a late winner.
    /// There is no Unregister and it would serve nothing - IPlugModul.enabled is read once, at
    /// startup, and a plugin is never initialised twice in one process.</remarks>
    public static void Register(string name, Action<Topic, EventKind> cb) {
      if(cb == null) {
        throw new ArgumentNullException("cb");
      }
      if(!_list.TryAdd(name, cb)) {
        throw new ArgumentException("CCtor.Register - duplicate name: " + name);
      }
    }

    /// <summary>Works out which cctor handlers this change added, dropped or altered.</summary>
    /// <remarks>Two sources are merged: the topic's own manifest and, when the "type" field points
    /// at one, the cctor of the type topic under /$YS/TYPES. A name present before and after with
    /// the same value is not reported at all - only the difference is.</remarks>
    internal static void Check(TopicEvent p) {
      SortedList<string, JSValue> lo = null, ln = null, lc = null;
      JSValue to = null, tn = p.Source.GetField("type"), vn;
      if(p.Kind == EventKind.FieldChanged) {
        JSValue o = p.OldManifest.Field("cctor"), n = p.Source.GetField("cctor");
        to = p.OldManifest.Field("type");
        if(!object.ReferenceEquals(o, n)) {
          JsLib.Propertys(ref lo, o);
          JsLib.Propertys(ref ln, n);
        }
      } else if(p.Kind == EventKind.Created) {
        JsLib.Propertys(ref ln, p.Source.GetField("cctor"));
      } else if(p.Kind == EventKind.Removed) {
        JsLib.Propertys(ref lo, p.Source.GetField("cctor"));
      } else {
        return;
      }
      if(!object.ReferenceEquals(to, tn)) {
        // $YS/TYPES is seeded by PersistentStorage (priority 2) and does not exist yet during
        // Repo.Init(), nor at all when that plugin is disabled
        Topic types = Topic.root.Get("$YS/TYPES", false), tt;
        if(types != null) {
          if(to.Is<string>() && to.Value != null && types.Exist(to.Value as string, out tt)) {
            JsLib.Propertys(ref lo, tt.GetState().Field("cctor"));
          }
          if(tn.Is<string>() && tn.Value != null && types.Exist(tn.Value as string, out tt)) {
            JsLib.Propertys(ref ln, tt.GetState().Field("cctor"));
          }
        }
      }
      if(lo != null && ln != null) {
        foreach(var k in lo.Where(z => ln.ContainsKey(z.Key)).Select(z => z.Key).ToArray()) {
          vn = ln[k];
          if(!JSValue.ReferenceEquals(lo[k], vn)) {
            if(lc==null) {
              lc = new SortedList<string, JSValue>();
            }
            lc.Add(k, vn);
          }
          lo.Remove(k);
          ln.Remove(k);
        }
      }

      if(lo != null) {
        Process(lo, p.Source, EventKind.Removed);
      }
      if(ln != null) {
        Process(ln, p.Source, EventKind.Created);
      }
      if(lc != null) {
        Process(lc, p.Source, EventKind.FieldChanged);
      }
    }


    private static void Process(SortedList<string, JSValue> l, Topic t, EventKind a) {
      foreach(var kv in l) {
        Invoke(kv.Key, t, a);
      }
    }

    private static void Invoke(string name, Topic t, EventKind a) {
      Action<Topic, EventKind> cb;
      if(_list.TryGetValue(name, out cb)) {
        try {
          cb.Invoke(t, a);
        }
        catch(Exception ex) {
          Log.Warning("CCtor({0}, {1}, {2}) - {3}", name, t.path, a, ex);
        }
      }
    }
  }
}
