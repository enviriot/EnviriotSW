///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NiL.JS.Extensions;
using X13.Repository;

namespace X13.WebUI.Helpers {
  // Per-topic access rules for the dashboard endpoints, declared in a topic's manifest as
  // dashboard.netRW / dashboard.netRO - network specifications in the same syntax as the
  // trustedNets topic ("local", a CIDR, a bare address, comma or space separated).
  //
  // This is a different model from the one guarding the IDE: the IDE is gated by network at
  // the door (WebUiHost.IsAllowed), the dashboard socket is open to anyone and every single
  // topic read or write is checked here instead. That is what lets a dashboard be reachable
  // from a network the IDE is not.
  //
  // Dotted field paths, not "/dashboard/netRW": Topic.GetField splits on '.', and a slashed
  // name would be one flat key that the prefix subscription below could never see inside.
  // The Inspector offers them under the names DashboardRO/DashboardRW - see the "path" entries
  // in the manifest catalog at /$YS/TYPES/Ext/Manifest.
  internal static class DashboardAcl {
    // The manifest sub-object both fields live in. Subscribing to the container rather than
    // to each field is what lets one registration cover both: the dispatcher compares
    // old.Field(prefix) against new.Field(prefix) by reference (Topic.cs Publish), so any
    // change inside "dashboard" reaches us.
    public const string FieldPrefix = "dashboard";
    public const string FieldRW = "dashboard.netRW";
    public const string FieldRO = "dashboard.netRO";

    private static readonly object _sync = new object();
    private static readonly Dictionary<Topic, Rule> _rules = new Dictionary<Topic, Rule>();
    // Read without the lock on every check; replaced wholesale under it. Declarations change
    // rarely and are read on every frame, so a snapshot beats taking a lock per check.
    private static Rule[] _snapshot = new Rule[0];
    private static SubRec _sub;

    /// <summary>Starts watching the tree for declarations. Owned by WebUiPl.Start.</summary>
    /// <remarks>One tree-wide field subscription, the same shape MQTTPl uses to find MQTT.uri:
    /// subscribe/subAck deliver the declarations that already exist, changedField the later
    /// edits, create the ones that arrive whole through Fill, remove and move keep the paths
    /// honest.</remarks>
    public static void Start() {
      Stop();
      _sub = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, FieldPrefix, SubFunc);
    }

    public static void Stop() {
      SubRec sub = Interlocked.Exchange(ref _sub, null);
      if(sub != null) sub.Dispose();
      lock(_sync) {
        _rules.Clear();
        _snapshot = new Rule[0];
      }
    }

    /// <summary>True when <paramref name="client"/> may read <paramref name="topicPath"/>.</summary>
    public static bool CanRead(IPAddress client, string topicPath) {
      Rule rule = Resolve(topicPath);
      return rule != null && (InSpec(client, rule.NetRW) || InSpec(client, rule.NetRO));
    }

    /// <summary>True when <paramref name="client"/> may write <paramref name="topicPath"/>.</summary>
    public static bool CanWrite(IPAddress client, string topicPath) {
      Rule rule = Resolve(topicPath);
      return rule != null && InSpec(client, rule.NetRW);
    }

    /// <summary>IsInSpec, deliberately NOT IsAllowed.</summary>
    /// <remarks>IsAllowed exempts loopback unconditionally, which is right for "do not lock
    /// yourself out of this machine's own UI" and wrong here: it would hand every local
    /// process full read/write on every topic that declares anything at all. A grant has to
    /// be a plain membership test, which is exactly what IsInSpec already is.</remarks>
    private static bool InSpec(IPAddress client, string spec) {
      return !string.IsNullOrWhiteSpace(spec) && NetworkAcl.IsInSpec(client, spec);
    }

    /// <summary>The declaration governing <paramref name="topicPath"/>: nearest one at or above it.</summary>
    /// <remarks>Longest matching path wins, so a declaration deeper in the tree overrides the
    /// branch it sits under. No match means no access - a topic nobody has spoken for is not
    /// reachable from a dashboard, and nothing is written back to the repository to say so.</remarks>
    private static Rule Resolve(string topicPath) {
      if(string.IsNullOrEmpty(topicPath)) return null;
      Rule[] snapshot = _snapshot;
      Rule best = null;
      for(int i = 0; i < snapshot.Length; i++) {
        Rule rule = snapshot[i];
        if(!Covers(rule.Path, topicPath)) continue;
        if(best == null || rule.Path.Length > best.Path.Length) best = rule;
      }
      return best;
    }

    private static bool Covers(string rulePath, string topicPath) {
      if(string.IsNullOrEmpty(rulePath)) return false;
      // A rule on the root covers the whole tree; rulePath + "/" would be "//" and match nothing.
      if(rulePath == Topic.Bill.delmiterStr) return true;
      if(!topicPath.StartsWith(rulePath, StringComparison.Ordinal)) return false;
      return topicPath.Length == rulePath.Length || topicPath[rulePath.Length] == Topic.Bill.delmiter;
    }

    private static void SubFunc(TopicEvent p, SubRec sr) {
      if(p == null || p.Source == null) return;
      switch(p.Kind) {
      case EventKind.Removed:
        Forget(p.Source);
        break;
      // create matters, and it is not obvious that it does: Topic.Fill assigns the manifest
      // and only then publishes create, so a topic can be born already carrying a declaration
      // and never emit a changedField for it. That is the path both Xst.Import and the store's
      // load take - skipping create here left every imported or later-loaded rule invisible.
      case EventKind.Created:
      case EventKind.Moved:
      case EventKind.FieldChanged:
      case EventKind.Snapshot:
      case EventKind.Ready:
        Refresh(p.Source);
        break;
      }
    }

    // Re-read rather than take the value off the TopicEvent: move carries the old path in o and
    // no value at all, and reading the topic covers every art with one branch.
    private static void Refresh(Topic topic) {
      if(topic == null || topic.disposed) {
        Forget(topic);
        return;
      }
      string rw = topic.GetField(FieldRW).AsString(null);
      string ro = topic.GetField(FieldRO).AsString(null);
      if(string.IsNullOrWhiteSpace(rw) && string.IsNullOrWhiteSpace(ro)) {
        Forget(topic);
        return;
      }
      lock(_sync) {
        _rules[topic] = new Rule(topic.path, rw, ro);
        Rebuild();
      }
    }

    private static void Forget(Topic topic) {
      if(topic == null) return;
      lock(_sync) {
        if(_rules.Remove(topic)) Rebuild();
      }
    }

    // Called under _sync. Paths are re-read from the topics so a move that shifted a declaring
    // subtree cannot leave a stale key behind.
    private static void Rebuild() {
      List<Rule> rules = new List<Rule>(_rules.Count);
      List<Topic> doomed = null;
      foreach(KeyValuePair<Topic, Rule> entry in _rules) {
        if(entry.Key.disposed) {
          if(doomed == null) doomed = new List<Topic>();
          doomed.Add(entry.Key);
          continue;
        }
        rules.Add(entry.Key.path == entry.Value.Path ? entry.Value : new Rule(entry.Key.path, entry.Value.NetRW, entry.Value.NetRO));
      }
      if(doomed != null) {
        foreach(Topic topic in doomed) _rules.Remove(topic);
      }
      _snapshot = rules.ToArray();
    }

    private sealed class Rule {
      public readonly string Path;
      public readonly string NetRW;
      public readonly string NetRO;

      public Rule(string path, string netRW, string netRO) {
        Path = path;
        NetRW = netRW;
        NetRO = netRO;
      }
    }
  }
}
