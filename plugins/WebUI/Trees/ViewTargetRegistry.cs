///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;

namespace X13.WebUI {
  /// <summary>Which rows this session has already sent, by vid.</summary>
  /// <remarks>Lived in Protocol.cs until the dependency graph made the odd pairing plain: that
  /// file is the wire vocabulary - data types with no behaviour and no outgoing dependencies -
  /// while this is live per-session state with a lifecycle of its own. Filing it among the DTOs
  /// carried a real cost rather than an aesthetic one: it twice dropped out of inventories taken
  /// while reviewing the layer, once from the lock audit and once from the target-cleanup audit,
  /// because nobody looking for session state opens the file named for the protocol.</remarks>
  internal sealed class ViewTargetRegistry {
    private readonly Dictionary<string, ViewTarget> _targets;

    public ViewTargetRegistry() {
      _targets = new Dictionary<string, ViewTarget>(StringComparer.Ordinal);
    }

    // No lock: this used to be reached from both the WebSocket message thread (req.* handlers)
    // and the engine tick thread (Topic.Subscribe callbacks). Both now arrive through the
    // session's queue, so every access runs on the engine thread.
    public ViewTarget Get(string vid) {
      ViewTarget target;
      return !string.IsNullOrEmpty(vid) && _targets.TryGetValue(vid, out target) ? target : null;
    }

    public void Add(string vid, ViewTarget target) {
      if(string.IsNullOrEmpty(vid) || target == null) return;
      _targets[vid] = target;
    }

    public ViewTarget GetOrCreate(string vid, Func<string, ViewTarget> createTarget) {
      if(string.IsNullOrEmpty(vid) || createTarget == null) return null;
      ViewTarget target;
      if(_targets.TryGetValue(vid, out target)) return target;
      target = createTarget(vid);
      if(target != null) _targets[vid] = target;
      return target;
    }

    public void Remove(string vid) {
      if(string.IsNullOrEmpty(vid)) return;
      _targets.Remove(vid);
    }

    // The key list is still materialised before the scan - not for locking now, but because the
    // loop removes from the very dictionary it walks.
    public void RemoveSubtree(string vid) {
      if(string.IsNullOrEmpty(vid)) return;
      List<string> doomed = new List<string>();
      foreach(string key in _targets.Keys) {
        if(key == vid || X13.WebUI.Helpers.VidHelper.IsDescendant(vid, key)) doomed.Add(key);
      }
      foreach(string key in doomed) _targets.Remove(key);
    }

    public void Clear() {
      _targets.Clear();
    }
  }
}
