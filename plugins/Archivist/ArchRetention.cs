///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using X13.Repository;
using NiL.JS.Extensions;

namespace X13.Archivist {

  /// <summary>Sweeps expired samples and buckets, one bounded window at a time.</summary>
  /// <remarks>Thinning is gone: raw samples are kept in full for as long as Arch.keep says and then
  /// removed outright, rather than being progressively averaged into unrecognisability. That is what
  /// makes it possible to zoom back into an old peak - the rollups answer the wide view, the raw is
  /// still there underneath for the narrow one.</remarks>
  internal static class ArchRetention {
    /// <summary>How much history one pass may sweep.</summary>
    /// <remarks>The predicate is served by the time index and then filtered by topic, so the cost of
    /// a sweep is set by the width of the window, not by how much of it belongs to this topic. A day
    /// is small enough to disappear into an idle pass and large enough that a year of backlog clears
    /// in a few hundred of them.</remarks>
    internal static readonly TimeSpan STEP = TimeSpan.FromDays(1);

    /// <summary>Bounds on how often one topic may be swept.</summary>
    /// <remarks>Without a lower bound the sweep never stops. The cutoff is now minus keep, so it
    /// travels with the clock; a step sets the cursor exactly to it, and by the time the round-robin
    /// comes back - about a second later with eighty-odd topics - the cutoff has moved on and the
    /// condition is true again. Every topic therefore swept on every visit, about sixty-six times a
    /// second between them, each doing six separate committed writes for a window a second wide and
    /// usually deleting nothing at all. On a disk answering in 19 ms that is more than it can carry,
    /// and it showed up as half a megabyte a second of writes on an idle server with no data coming
    /// in and no charts open.</remarks>
    private static readonly TimeSpan MIN_GAP = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MAX_GAP = TimeSpan.FromHours(1);

    /// <summary>How long to let expired data accumulate before sweeping it.</summary>
    /// <remarks>A quarter of what the topic keeps, because the right granularity is set by the
    /// retention it enforces and by nothing else. A ring buffer holding sixty seconds has to be
    /// swept on the scale of seconds or it stops being a ring buffer; a topic keeping a week does
    /// not care whether its oldest sample goes now or in an hour. One number for both would be
    /// wrong at one end: a flat hour turns the ring buffers into hour-long ones, a flat minute
    /// spends eighty-three writes a minute on topics that would not notice a day.</remarks>
    private static TimeSpan Gap(ArchTopic at) {
      double sec = at.Keep * 24 * 3600 / 4;
      if(sec < MIN_GAP.TotalSeconds) {
        return MIN_GAP;
      }
      return sec > MAX_GAP.TotalSeconds ? MAX_GAP : TimeSpan.FromSeconds(sec);
    }

    /// <summary>Advances one topic's retention cursor by at most one window.</summary>
    /// <returns>True when something was swept or the topic was dropped.</returns>
    internal static bool Step(ArchStore st, ArchTopic at) {
      if(st == null || !st.IsOpen || at == null) {
        return false;
      }
      DateTime nowUtc = DateTime.UtcNow;
      bool orphan = IsOrphan(at);
      // An orphan keeps nothing: the cutoff is now, so the sweep walks its whole history and the
      // registry row goes once it has caught up.
      DateTime cutoff = orphan ? nowUtc : nowUtc.AddDays(-at.Keep);

      if(at.Pt == DateTime.MinValue) {
        // Only a store written before this cursor existed lands here; fall back to the rollup
        // cursor, which marks the same "how far back this topic is known" point.
        at.Pt = at.Rt == DateTime.MinValue ? nowUtc : at.Rt;
        st.Store(at);
        return false;
      }
      if(at.Pt >= cutoff) {
        if(orphan) {
          st.DropTopic(at);
          return true;
        }
        return false;
      }
      // Not enough has expired since the last sweep to be worth the writes. Orphans are exempt:
      // their cutoff is now, so this test would hold forever and the registry row would never reach
      // the branch above that drops it.
      if(!orphan && cutoff - at.Pt < Gap(at)) {
        return false;
      }
      DateTime hi = at.Pt + STEP < cutoff ? at.Pt + STEP : cutoff;
      st.PurgeRaw(at, at.Pt.ToLocalTime(), hi.ToLocalTime());
      // Buckets are atomic, and the range is a primary-key seek, so sweeping from zero costs
      // nothing extra and survives a cursor that skipped a stretch.
      foreach(var g in ArchTime.LEVELS) {
        st.PurgeRoll(g, at.Id, 0, ArchTime.BucketIndex(hi, g) - 1);
      }
      at.Pt = hi;
      if(orphan && hi >= cutoff) {
        // Swept up to the present, so there is nothing left to keep the row alive for. Dropping it
        // here rather than waiting for the test at the top, which an orphan can never satisfy: its
        // cutoff is the current instant, so by the next visit - a second or so later with the
        // round-robin - the cursor is behind again. Measured on a live server, six such topics were
        // being swept forty-eight times a minute each, forever, and that was most of what the disk
        // was doing. No test caught it because a test loops faster than the clock moves.
        st.DropTopic(at);
        return true;
      }
      st.Store(at);
      return true;
    }

    /// <summary>True when the topic is gone, disposed, or no longer asking to be archived.</summary>
    /// <remarks>Arch.enable is read with As&lt;bool&gt;() - JS truthiness - to match the write path
    /// exactly. Reading it strictly here and loosely there would make a topic whose enable is 1
    /// archive samples and sweep them as an orphan at the same time.</remarks>
    private static bool IsOrphan(ArchTopic at) {
      return at.T == null || at.T.disposed || !at.T.GetField("Arch.enable").As<bool>();
    }
  }
}
