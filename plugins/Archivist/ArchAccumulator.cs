///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Archivist {
  /// <summary>One archived value, already resolved to its slot in the caller's topic list.</summary>
  /// <remarks>T is UTC, and so is every bound handed to this class. Local time enters only where a
  /// timestamp is produced for the caller. That is not tidiness: LiteDB puts DateTime through local
  /// time end to end, so in the hour the clock repeats every autumn two instants an hour apart are
  /// indistinguishable and a window over them is empty. Keeping the arithmetic in UTC is what makes
  /// that hour behave like any other.
  /// V is a plain double, not a JSValue: nothing but numbers is ever archived (the plugin only
  /// stores a state that converts to a finite double), and a stream of 2.8 M boxed values is the
  /// kind of allocation this rework exists to remove. A readonly struct rather than a record for
  /// the same reason - and because net48 defaults to C# 7.3, where records do not exist.</remarks>
  internal readonly struct ArchSample {
    public readonly int Idx;
    public readonly DateTime T;
    public readonly double V;

    public ArchSample(int idx, DateTime t, double v) {
      Idx = idx;
      T = t;
      V = v;
    }
  }

  /// <summary>Turns a time-ordered stream of samples into the row array AQuery returns.</summary>
  /// <remarks>Lifted out of LiteDB_Pl.AQuery unchanged, so the move to a new store could be verified
  /// against the old output before anything about the arithmetic was touched. It is pure: whatever
  /// produces the stream - raw documents, hourly buckets, or a test - is invisible here.</remarks>
  internal static class ArchAccumulator {

    /// <summary>Samples through unchanged, with values from the same instant merged into one row.</summary>
    /// <remarks>The 15-second window is what makes several topics sampled in the same server tick
    /// share a row instead of producing one row each with holes.</remarks>
    internal static JSL.Array Raw(IEnumerable<ArchSample> src, int topicCount) {
      var rez = new JSL.Array();
      JSL.Array lo = null;
      DateTime rowT = DateTime.MinValue;          // when the open row started, in UTC
      foreach(var s in src) {
        int slot = s.Idx + 1;                     // slot 0 carries the timestamp
        // Deliberately a raw ValueType test, NOT IsObject(): this branch must ALSO catch
        // JSValue.Null, whose ValueType is Object - an empty slot in the archive row passes here
        // and gets filled below. IsObject() excludes Null and would skip it.
        // The age of the row is tracked here rather than read back out of slot 0, which by then
        // has been converted to local time and would compare wrongly across a clock change.
        if(lo != null && lo[slot].ValueType == JSC.JSValueType.Object
            && (s.T - rowT).TotalSeconds < 15) {
          lo[slot] = new JSL.Number(s.V);
        } else {
          rowT = s.T;
          lo = new JSL.Array(topicCount + 1) {
            [0] = X13.JsExtLib.Context.ProxyValue(s.T.ToLocalTime())
          };
          for(var j = 1; j <= topicCount; j++) {
            lo[j] = (slot == j) ? (JSC.JSValue)new JSL.Number(s.V) : JSC.JSValue.Null;
          }
          rez.Add(lo);
        }
      }
      return rez;
    }

    /// <summary>Resamples the stream onto |count| equal buckets between begin and end, each value
    /// the average weighted by how long it held.</summary>
    /// <param name="seed">Per topic, the last value before begin, or NaN when there is none. It
    /// fills the head of the first bucket; without it a bucket would start from nothing and read
    /// low. The caller supplies it because finding it is a storage question, not an arithmetic one.</param>
    internal static JSL.Array Resample(IEnumerable<ArchSample> src, DateTime begin, DateTime end,
                                       int count, double[] seed) {
      int topicCount = seed.Length;
      var rez = new JSL.Array();
      var step = (end - begin).TotalSeconds / Math.Abs(count);

      DateTime cursor = begin.AddSeconds(step);
      var f_cnt = new int[topicCount];
      var f_val = new double[topicCount];
      var l_val = new double[topicCount];
      var l_delta = new double[topicCount];
      var t_cnt = 0;
      double t_sum = 0;
      int i;

      for(i = 0; i < topicCount; i++) {
        f_val[i] = 0;
        f_cnt[i] = 0;
        l_delta[i] = -step;
        l_val[i] = seed[i];
      }

      foreach(var s in src) {
        var t_cur = s.T;
        if(t_cur >= cursor) {
          AddRecord();
          do {
            cursor = cursor.AddSeconds(step);
          } while(t_cur >= cursor);
        }
        i = s.Idx;
        if(i < 0 || i >= topicCount) {
          continue;
        }
        var v = s.V;
        // Infinity is rejected as well as NaN: one poisoned sample exists in the live archive and
        // would otherwise propagate through the weighted sum and blank out the whole series.
        if(!double.IsNaN(v) && !double.IsInfinity(v)) {
          var td = (t_cur - cursor).TotalSeconds;
          if(!double.IsNaN(l_val[i])) {
            f_val[i] += l_val[i] * (td - l_delta[i]) / step;
            l_delta[i] = td;
          }
          f_cnt[i]++;
          l_val[i] = v;
          t_cnt++;
          t_sum += td;
        }
      }
      AddRecord();
      return rez;

      void AddRecord() {
        JSL.Array lo = new JSL.Array(topicCount + 1) {
          [0] = X13.JsExtLib.Context.ProxyValue(cursor.AddSeconds(t_cnt == 1 ? t_sum : (-step / 2)).ToLocalTime())
        };
        t_cnt = 0;
        t_sum = 0;
        for(i = 0; i < topicCount; i++) {
          // A bucket that saw fewer than two samples reports the value still in force, and only a
          // topic with nothing known before it at all reports Null. Copied from the Firebird
          // backend, which is the one that has actually been in service.
          // <para>The LiteDB path wrote Null here instead. It had been meant to carry forward - the
          // dead inner test `f_cnt[i] == 1 ? l_val[i]` is the remains of it, stranded under an outer
          // condition that already excluded it - and the effect was a slowly changing topic drawn as
          // a line of gaps whenever a chart asked for more points than the data had. It also fed the
          // Logram Average block a Null at count=1, which then fell back to the raw pin.</para>
          // l_val is deliberately NOT reset below: that is what makes it the carried value.
          lo[i + 1] = f_cnt[i] > 1
            ? (JSC.JSValue)new JSL.Number(f_val[i] + l_val[i] * (-l_delta[i]) / step)
            : (double.IsNaN(l_val[i]) ? JSC.JSValue.Null : new JSL.Number(l_val[i]));
          f_val[i] = 0;
          f_cnt[i] = 0;
          l_delta[i] = -step;
        }
        rez.Add(lo);
      }
    }
  }
}
