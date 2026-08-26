///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;

namespace X13.Archivist {

  /// <summary>One folded interval: what a rollup document carries.</summary>
  internal struct Bucket {
    /// <summary>Samples that went into it. Zero means the interval was empty and nothing is stored.</summary>
    internal int N;
    /// <summary>Time-weighted average over the whole interval.</summary>
    internal double V;
    /// <summary>The last value seen, which seeds the next interval.</summary>
    internal double Last;
  }

  /// <summary>Folds a stream into one interval average, and drives the rollup cursors.</summary>
  internal static class ArchRollup {
    /// <summary>How far behind now a bucket is folded, so a late sample cannot arrive after its
    /// bucket has been computed. The jitter allowance the old thinning already used.</summary>
    internal static readonly TimeSpan ROLL_LAG = TimeSpan.FromDays(ArchStore.HOT_KEEP_DAYS);

    /// <summary>Folds the next pending hour of one topic, and the day it completes.</summary>
    /// <returns>True when a bucket was written, so a caller can tell progress from idleness.</returns>
    /// <remarks>A bucket is computed once, from data that has stopped arriving, and never
    /// recomputed - which is what keeps it consistent with whatever happens to the raw rows later.</remarks>
    internal static bool FoldHour(ArchStore st, ArchTopic at) {
      if(at == null || at.Hot) {
        return false;                              // a ring buffer holds minutes; no hour to fold
      }
      DateTime nowUtc = DateTime.UtcNow;
      if(at.Rt == DateTime.MinValue) {
        at.Rt = ArchTime.HourFloor(nowUtc);        // nothing behind it: start folding from now
        st.Store(at);
        return false;
      }
      long idx = ArchTime.HourIndex(at.Rt);
      if(idx >= ArchTime.HourIndex(nowUtc - ROLL_LAG)) {
        return false;
      }
      DateTime bs = ArchTime.HourStart(idx), be = bs.AddHours(1);
      var ids = new[] { at.Id };
      double seed = st.Seed(ids, bs, ArchTime.GRAN_RAW)[0];
      // The upper bound is inclusive, so it stops one tick short of the next bucket rather than
      // counting a sample on the boundary into both.
      var buf = st.Stream(ids, bs, be.AddTicks(-1), false, 0).ToList();
      var b = FoldInto(st, at, buf, idx, seed);
      at.Rt = be;
      st.Store(at);

      if(ArchTime.DayIndex(be) > ArchTime.DayIndex(bs)) {
        FoldDay(st, at, ArchTime.DayIndex(bs));
      }
      return b.N > 0;
    }

    /// <summary>Writes one hour and the twelve five-minute buckets inside it, from one read.</summary>
    /// <remarks>The finer level exists because measurement contradicted the assumption it was built
    /// on: raw was expected to cover short windows cheaply, but two days of the live archive is
    /// 166 734 samples and thirty days runs past the read ceiling. Both levels come out of the same
    /// buffer, so the finer one costs writes and disk, not another pass over the raw.</remarks>
    internal static Bucket FoldInto(ArchStore st, ArchTopic at, List<ArchSample> buf, long hourIdx, double seed) {
      DateTime bs = ArchTime.HourStart(hourIdx), be = bs.AddHours(1);
      var b = Fold(buf, bs, be, seed);
      st.UpsertBucket(ArchTime.GRAN_HOUR, at.Id, hourIdx, b);

      int pos = 0;
      double s5 = seed;
      int per = ArchTime.GRAN_HOUR / ArchTime.GRAN_5MIN;
      long sub0 = ArchTime.BucketIndex(bs, ArchTime.GRAN_5MIN);
      for(int k = 0; k < per; k++) {
        DateTime ss = bs.AddSeconds((double)k * ArchTime.GRAN_5MIN), se = ss.AddSeconds(ArchTime.GRAN_5MIN);
        // buf is already in time order, so the slice is a walk, not a filter over the whole hour.
        int from = pos;
        while(pos < buf.Count && buf[pos].T < se) {
          pos++;
        }
        var b5 = Fold(buf.GetRange(from, pos - from), ss, se, s5);
        st.UpsertBucket(ArchTime.GRAN_5MIN, at.Id, sub0 + k, b5);
        if(b5.N > 0) {
          s5 = b5.Last;
        }
      }
      return b;
    }

    /// <summary>Folds a finished day out of its hours - never out of the raw samples.</summary>
    /// <remarks>Hours are equal length and already folded, so a day built from them is exact and
    /// costs twenty-four primary-key reads instead of a day of raw. They are fed at the instant each
    /// hour BEGINS: held forward from the stored midpoint, the first and last hour of the day would
    /// carry the wrong weight.</remarks>
    internal static void FoldDay(ArchStore st, ArchTopic at, long day) {
      DateTime ds = ArchTime.DayStart(day), de = ds.AddDays(1);
      long h0 = ArchTime.HourIndex(ds), h1 = ArchTime.HourIndex(de) - 1;

      var src = new List<ArchSample>();
      int n = 0;
      foreach(var kv in st.Buckets(ArchTime.GRAN_HOUR, at.Id, h0, h1)) {
        src.Add(new ArchSample(0, ArchTime.HourStart(kv.Key), kv.Value.V));
        n += kv.Value.N;
      }
      if(src.Count == 0) {
        return;
      }
      Bucket prev;
      double seed = st.TryLastBucket(ArchTime.GRAN_HOUR, at.Id, h0, out prev) ? prev.V : double.NaN;
      var d = Fold(src, ds, de, seed);
      // The count describes the raw data underneath, not the hours in between.
      d.N = n;
      st.UpsertBucket(ArchTime.GRAN_DAY, at.Id, day, d);
    }

    /// <summary>Time-weighted average of the stream over [begin, end), plus the extremes.</summary>
    /// <param name="seed">Value in force at begin, or NaN when nothing is known before it. NaN makes
    /// the first sample backfill the head of the interval rather than average against nothing.</param>
    /// <remarks>The arithmetic is ArchCompact2, which folded an hour of raw samples into one
    /// document, with one deliberate difference: that code returned the bare value when an interval
    /// held exactly one sample, discarding the carried-in portion. That shortcut was invisible while
    /// the result only had to look right on a chart, but a rollup has to compose - averaging the
    /// buckets must equal averaging the raw - so the single-sample case is weighted like every other.
    /// <para>Only the average, the count and the carried-forward last value are kept. Extremes were
    /// written for a while on the theory that a chart might one day draw a band; nothing read them,
    /// so they are gone - on half a million five-minute buckets they were not free.</para></remarks>
    internal static Bucket Fold(IEnumerable<ArchSample> src, DateTime begin, DateTime end, double seed) {
      var b = new Bucket { N = 0, V = double.NaN, Last = seed };
      double interval = (end - begin).TotalSeconds;
      if(interval <= 0) {
        return b;
      }
      double l_val = seed, f_val = 0, l_delta = 0;
      int n = 0;

      foreach(var s in src) {
        double v = s.V;
        if(double.IsNaN(v) || double.IsInfinity(v)) {
          continue;
        }
        double td = (s.T - begin).TotalSeconds;
        if(td < 0) {
          td = 0;
        } else if(td > interval) {
          td = interval;
        }
        if(!double.IsNaN(l_val)) {
          f_val += l_val * (td - l_delta) / interval;
          l_delta = td;
        }
        n++;
        l_val = v;
      }
      if(n == 0) {
        return b;                                  // nothing happened here, so nothing is stored
      }
      b.N = n;
      b.V = f_val + l_val * (interval - l_delta) / interval;
      b.Last = l_val;
      return b;
    }
  }
}
