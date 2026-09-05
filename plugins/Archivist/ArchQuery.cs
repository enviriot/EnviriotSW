///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Archivist {

  /// <summary>What one query decided and what it cost, for the log.</summary>
  internal struct ArchQueryStat {
    /// <summary>Bucket width that answered, in seconds; GRAN_RAW for the samples themselves,
    /// -1 when the request was refused before it chose anything.</summary>
    internal int Gran;
    /// <summary>Rows pulled out of the store - the number that decides whether a query is cheap.</summary>
    internal long Rows;

    internal string Level {
      get {
        return Gran < 0 ? "-"
          : Gran == ArchTime.GRAN_RAW ? "raw"
          : Gran == ArchTime.GRAN_5MIN ? "5min"
          : Gran == ArchTime.GRAN_HOUR ? "hour" : "day";
      }
    }
  }

  /// <summary>Turns a range request into the row array AQuery returns.</summary>
  /// <remarks>Separate from the plugin so the same path can be driven without a running server -
  /// by a test, or by the migration tool measuring what the conversion produced. The plugin adds
  /// nothing to it beyond the store it owns.</remarks>
  internal static class ArchQuery {
    /// <summary>Ceiling on the points a caller may request. A 2000 pixel chart cannot show more.</summary>
    internal const int MAX_POINTS = 10000;
    /// <summary>Ceiling on raw rows one query may read.</summary>
    /// <remarks>The last line of defence, not the mechanism: the granularity ladder is what keeps a
    /// wide request off the raw data. It exists because count == 0 carries neither a step nor a
    /// limit, and that is the shape that read 2.8 M rows and took 624 MB of heap.</remarks>
    internal const int MAX_RAW_ROWS = 500000;

    internal static JSL.Array Run(ArchStore st, string[] topics, DateTime begin, int count, DateTime end) {
      ArchQueryStat ignored;
      return Run(st, topics, begin, count, end, out ignored);
    }

    /// <summary>The same query, reporting what it decided and how much it moved.</summary>
    /// <remarks>The elapsed time alone says a request was slow but not why, and every "why" here is
    /// one of two numbers: which rung of the ladder answered, and how many rows that cost. Those are
    /// exactly what the migration tool's bench prints, and they are what made the ladder debuggable
    /// in the first place - so the running server can now say the same thing about a live chart.</remarks>
    internal static JSL.Array Run(ArchStore st, string[] topics, DateTime begin, int count, DateTime end,
                                  out ArchQueryStat stat) {
      stat = new ArchQueryStat { Gran = -1 };
      var rez = new JSL.Array();
      if(st == null || !st.IsOpen || topics == null || topics.Length == 0) {
        return rez;                                 // before Open or after Close, not an exception
      }
      if(count > MAX_POINTS) {
        count = MAX_POINTS;
      } else if(count < -MAX_POINTS) {
        count = -MAX_POINTS;
      }
      // Everything below this line is UTC. The caller asks in local time - a JS Date arriving over
      // the API - and the answer goes back with local timestamps, but nothing in between reasons
      // about local time, because the hour the clock repeats is not addressable in it.
      begin = begin.ToUniversalTime();
      end = end == DateTime.MinValue ? end : end.ToUniversalTime();

      var ids = new int[topics.Length];
      for(int k = 0; k < topics.Length; k++) {
        var at = st.ByPath(topics[k]);
        ids[k] = at == null ? 0 : at.Id;
      }

      if(end <= begin || count == 0) {
        // Raw, in the order and quantity asked for - three cases, taken from the Firebird backend
        // because that is the one that has actually been in service. It asked, respectively, for
        // DT<BEGIN descending, DT between BEGIN and END, and DT>BEGIN ascending.
        DateTime lo, hi;
        bool desc;
        int limit;
        if(count < 0) {
          // The |count| newest samples BEFORE begin. Exclusive, hence the tick: the bound is a
          // strict inequality in the original, and on a 100 ns key that is exactly one tick.
          lo = DateTime.MinValue;
          hi = begin.AddTicks(-1);
          desc = true;
          limit = Math.Min(-count, MAX_RAW_ROWS);
        } else if(count == 0) {
          // Everything the window holds, which for end <= begin is nothing. That looks like an
          // oversight and is not: a caller asking for no points over an empty window is asking for
          // nothing, and inventing a range for it is how the LiteDB path came to answer with the
          // oldest rows in the entire archive.
          lo = begin;
          hi = end;
          desc = false;
          limit = MAX_RAW_ROWS;
        } else {
          // The first count samples AFTER begin - the case that diverged. The LiteDB path read
          // everything below begin ascending, so a request for "the next few points" answered with
          // the oldest few the archive still held, years away from what was asked.
          lo = begin.AddTicks(1);
          hi = DateTime.MaxValue;
          desc = false;
          limit = Math.Min(count, MAX_RAW_ROWS);
        }
        stat.Gran = ArchTime.GRAN_RAW;
        var rows = new long[1];
        var res = ArchAccumulator.Raw(Counted(st.Stream(ids, lo, hi, desc, limit), rows), topics.Length);
        stat.Rows = rows[0];
        return res;
      }

      int gran = ArchTime.Granularity((end - begin).TotalSeconds / Math.Abs(count));
      stat.Gran = gran;
      Snap(gran, ref begin, ref end, ref count);
      DateTime horizon = st.RollupHorizon(ids);
      // Left in UTC, like everything else past line 35. It used to be converted to local here, a
      // leftover from before the conversion was hoisted to the top of the method, and the result
      // was compared against begin and end, which are UTC. DateTime comparison ignores Kind, so the
      // boundary sat a whole time-zone offset away from where it belonged: east of Greenwich the
      // split looked later than the window and the unfolded tail was never read, so every chart
      // reaching the present lost its last few hours.
      DateTime split = horizon == DateTime.MaxValue ? end : horizon;
      if(gran == ArchTime.GRAN_RAW || split <= begin) {
        // Either the request wants more resolution than a bucket carries, or nothing in the
        // window has been folded yet.
        stat.Gran = ArchTime.GRAN_RAW;
        var rawRows = new long[1];
        var rawRes = ArchAccumulator.Resample(Counted(st.Stream(ids, begin, end, false, MAX_RAW_ROWS), rawRows),
                                              begin, end, count, st.Seed(ids, begin, ArchTime.GRAN_RAW));
        stat.Rows = rawRows[0];
        return rawRes;
      }
      // Folded up to the horizon, raw after it. The boundary is shared by every series in the
      // request: a per-topic one would interleave one topic in buckets with another in raw and
      // break the time order the accumulator relies on.
      IEnumerable<ArchSample> src = st.RollStream(gran, ids, begin, split < end ? split : end, false);
      if(split < end) {
        src = src.Concat(st.Stream(ids, split, end, false, MAX_RAW_ROWS));
      }
      var n = new long[1];
      var rez2 = ArchAccumulator.Resample(Counted(src, n), begin, end, count, st.Seed(ids, begin, gran));
      stat.Rows = n[0];
      return rez2;
    }

    /// <summary>Moves the window onto an absolute grid, so a point does not move when the chart does.</summary>
    /// <remarks>The output buckets used to be measured from the requested begin: step was
    /// (end - begin) / count and the first bucket started wherever the window happened to start. Pan
    /// by a pixel and every bucket boundary moved with it, so the same underlying data was
    /// re-averaged into different points - a spike would slide, split in two, or disappear while the
    /// mouse was down. That is not a performance question, it is the chart changing under the reader.
    /// <para>Fixed by two things together. The step is rounded UP to a whole number of source
    /// buckets, and the window is then snapped outward to multiples of that step counted from the
    /// zero of the calendar. Both ends move, so the answer covers a little more than was asked; the
    /// caller sets its own dateWindow and clips, and the extra is what lets a pan reuse the same
    /// points instead of recomputing different ones.</para>
    /// <para>Why it holds while panning: dragging changes where the window is, not how wide it is,
    /// so the step is unchanged and the grid it is anchored to is unchanged. Zooming does change the
    /// step, and there the grid rebases - which is honest, because the resolution really did
    /// change.</para></remarks>
    private static void Snap(int gran, ref DateTime begin, ref DateTime end, ref int count) {
      // A second for raw, one bucket otherwise: the step can then never ask for a fraction of a
      // stored bucket, which is the other half of what keeps consecutive answers comparable.
      long unit = gran == ArchTime.GRAN_RAW ? 1 : gran;
      double raw = (end - begin).TotalSeconds / Math.Abs(count);
      long stepSec = (long)Math.Ceiling(raw / unit) * unit;
      if(stepSec < unit) {
        stepSec = unit;
      }
      long step = stepSec * TimeSpan.TicksPerSecond;
      long b = begin.Ticks / step * step;
      long e = (end.Ticks + step - 1) / step * step;
      begin = new DateTime(b, DateTimeKind.Utc);
      end = new DateTime(e, DateTimeKind.Utc);
      count = (int)((e - b) / step);
      if(count < 1) {
        count = 1;
      }
    }

    /// <summary>Passes the stream through, counting it.</summary>
    /// <remarks>A cell in an array rather than a captured local because the count has to be readable
    /// after the accumulator has finished draining a lazily evaluated sequence, and a closure over a
    /// local would be, but reads oddly next to an out parameter that cannot be captured at all.</remarks>
    private static IEnumerable<ArchSample> Counted(IEnumerable<ArchSample> src, long[] n) {
      foreach(var s in src) {
        n[0]++;
        yield return s;
      }
    }
  }
}
