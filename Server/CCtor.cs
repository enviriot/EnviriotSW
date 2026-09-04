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
    /// at one, the cctor of the type topic under /$YS/TYPES.
    /// <para>A name present before and after is reported only when its value differs - by
    /// REFERENCE, which is a weaker claim than it reads: two equal strings built separately are
    /// different to it. That gap used to show, because a rewrite of the same value arrived here as
    /// a FieldChanged; it no longer does, since CmdField stops such a write from being published
    /// at all. So the reference test is now a second line of defence rather than the only one, and
    /// what it still cannot tell apart are two equal-looking objects.</para></remarks>
    internal static void Check(TopicEvent p) {
      SortedList<string, JSValue> lo = null, ln = null, lc = null;
      // "type" comes from the same snapshot as "cctor" does, and for the same reason: a Created
      // event published after the Field phase would otherwise see a type written in that very tick
      // and announce the type's handlers, which that write's own FieldChanged then announces again.
      JSValue to = null, vn;
      JSValue tn = p.Kind == EventKind.Created ? p.OldManifest.Field("type") : p.Source.GetField("type");
      if(p.Kind == EventKind.FieldChanged) {
        JSValue o = p.OldManifest.Field("cctor"), n = p.Source.GetField("cctor");
        to = p.OldManifest.Field("type");
        if(!object.ReferenceEquals(o, n)) {
          JsLib.Propertys(ref lo, o);
          JsLib.Propertys(ref ln, n);
        }
      } else if(p.Kind == EventKind.Created) {
        // The manifest the topic was DECLARED with, not the one it has now. A topic created and
        // given its cctor by a separate write in the same tick used to be announced twice, both
        // times as Created: once from here, reading a manifest the Field phase had already
        // updated, and once from that write's own FieldChanged.
        JsLib.Propertys(ref ln, p.OldManifest.Field("cctor"));
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
              lc = new SortedList<string, JSValue>(StringComparer.Ordinal);
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
          Topic.PluginFailed("CCtor:" + name, t, ex);
        }
      }
    }
  }
}
