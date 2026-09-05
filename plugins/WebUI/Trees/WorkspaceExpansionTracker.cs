///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using X13.Repository;

namespace X13.WebUI {
  // Owns which workspace rows are fully expanded (subscribed to Children|Value|Field)
  // vs collapsed-but-watched (lightweight Children-only subscription, so the expander
  // arrow can update without a full subscription).
  //
  // No lock: this used to be reached from both the WebSocket message thread (req.expand
  // handlers) and the engine tick thread (Topic.Subscribe callbacks), and every public method
  // was made atomic under an internal gate. Both callers now arrive through the session's
  // queue, so all of this runs on the engine thread.
  internal sealed class WorkspaceExpansionTracker {
    private readonly Dictionary<string, SubRec> _expanded = new Dictionary<string, SubRec>(StringComparer.Ordinal);
    private readonly Dictionary<string, SubRec> _collapsedWatch = new Dictionary<string, SubRec>(StringComparer.Ordinal);

    public bool IsExpanded(string vid) {
      return _expanded.ContainsKey(vid);
    }

    // Drops any collapsed-watch for vid, then subscribes it as expanded unless already expanded.
    public void Expand(string vid, Func<SubRec> subscribe) {
      RemoveOne(_collapsedWatch, vid);
      if(!_expanded.ContainsKey(vid)) _expanded[vid] = subscribe();
    }

    // Drops the full subscription for vid, if any.
    public void ExitExpanded(string vid) {
      RemoveOne(_expanded, vid);
    }

    // Starts a lightweight watch for vid unless it's already expanded or already watched.
    public void Watch(string vid, Func<SubRec> watchFactory) {
      if(_expanded.ContainsKey(vid) || _collapsedWatch.ContainsKey(vid)) return;
      _collapsedWatch[vid] = watchFactory();
    }

    // Removes vid itself and its whole subtree from the collapsed-watch set.
    public void RemoveWatchSubtree(string vid, Func<string, string, bool> isDescendant) {
      RemoveSubtreeFrom(_collapsedWatch, vid, isDescendant, includeSelf: true);
    }

    // Removes vid's *strict* expanded descendants (vid itself is left alone - ExitExpanded owns that).
    public void RemoveExpandedDescendants(string vid, Func<string, string, bool> isDescendant) {
      RemoveSubtreeFrom(_expanded, vid, isDescendant, includeSelf: false);
    }

    public void Clear() {
      DisposeAll(_expanded);
      DisposeAll(_collapsedWatch);
    }

    private static void RemoveOne(Dictionary<string, SubRec> map, string vid) {
      SubRec sub;
      if(map.TryGetValue(vid, out sub)) {
        sub?.Dispose();
        map.Remove(vid);
      }
    }

    private static void RemoveSubtreeFrom(Dictionary<string, SubRec> map, string vid, Func<string, string, bool> isDescendant, bool includeSelf) {
      foreach(string key in new List<string>(map.Keys)) {
        if((includeSelf && key == vid) || isDescendant(vid, key)) {
          map[key]?.Dispose();
          map.Remove(key);
        }
      }
    }

    private static void DisposeAll(Dictionary<string, SubRec> map) {
      foreach(SubRec sub in map.Values) sub?.Dispose();
      map.Clear();
    }
  }
}
