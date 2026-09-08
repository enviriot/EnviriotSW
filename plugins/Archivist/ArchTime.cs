///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;

namespace X13.Archivist {
  /// <summary>Bucket arithmetic for the archive. Pure - no state, no IO, no clock.</summary>
  internal static class ArchTime {
    /// <summary>Bucket numbering origin. UTC, so the numbering never moves with the time zone.</summary>
    internal static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal const int GRAN_RAW = 0;
    internal const int GRAN_5MIN = 300;
    internal const int GRAN_HOUR = 3600;
    internal const int GRAN_DAY = 86400;

    /// <summary>Every stored granularity, coarsest first.</summary>
    internal static readonly int[] LEVELS = { GRAN_DAY, GRAN_HOUR, GRAN_5MIN };

    /// <summary>Picks the coarsest source that still satisfies the requested resolution.</summary>
    /// <remarks>A bucket can be used only when it fits inside one step. The default chart is 2 days
    /// wide and asks for ~500 points - a step of 5.8 minutes, so five-minute buckets.
    /// <para>The five-minute level was nearly left out on the argument that raw already carries that
    /// resolution and is retained anyway. Measurement against the live archive killed that: two days
    /// of raw is 166 734 rows there, and thirty days runs into the read ceiling outright. Recent
    /// data is an order of magnitude denser than the archive average, so raw is only affordable for
    /// windows of hours.</para></remarks>
    internal static int Granularity(double stepSeconds) {
      foreach(var g in LEVELS) {
        if(stepSeconds >= g) {
          return g;
        }
      }
      return GRAN_RAW;
    }

    internal static DateTime HourFloor(DateTime utc) {
      return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, utc.Kind);
    }
    internal static DateTime DayFloor(DateTime utc) {
      return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, utc.Kind);
    }

    internal static long HourIndex(DateTime utc) {
      return (long)Math.Floor((utc - Epoch).TotalHours);
    }
    internal static long DayIndex(DateTime utc) {
      return (long)Math.Floor((utc - Epoch).TotalDays);
    }
    internal static DateTime HourStart(long index) {
      return Epoch.AddHours(index);
    }
    internal static DateTime DayStart(long index) {
      return Epoch.AddDays(index);
    }

    /// <summary>Bucket index at any granularity, so callers do not branch.</summary>
    /// <remarks>Uniform seconds-since-epoch arithmetic: it agrees with HourIndex and DayIndex
    /// exactly, and extends to any bucket width without another special case.</remarks>
    internal static long BucketIndex(DateTime utc, int granularity) {
      return (long)Math.Floor((utc - Epoch).TotalSeconds / granularity);
    }
    internal static DateTime BucketStart(long index, int granularity) {
      return Epoch.AddSeconds((double)index * granularity);
    }
    /// <summary>The stamp a bucket carries: its midpoint, the convention ArchCompact2 already used.</summary>
    internal static DateTime BucketMid(long index, int granularity) {
      return BucketStart(index, granularity).AddSeconds(granularity / 2.0);
    }

    /// <summary>Packs topic and bucket into one key, so the primary index doubles as the
    /// per-topic ordered index and no second index has to be maintained.</summary>
    /// <remarks>The bucket occupies the low 32 bits, which holds ~490 000 years of hours; the
    /// guard is against a pre-2000 timestamp, whose negative index would otherwise borrow into
    /// the topic id and silently attribute the row to a different topic.</remarks>
    internal static long PackId(int topicId, long bucket) {
      if(topicId < 0) {
        throw new ArgumentOutOfRangeException("topicId", topicId, "topic id must not be negative");
      }
      if(bucket < 0 || bucket > uint.MaxValue) {
        throw new ArgumentOutOfRangeException("bucket", bucket, "bucket index outside the packable range");
      }
      return ((long)topicId << 32) | (uint)bucket;
    }
    internal static int TopicOf(long packed) {
      return (int)(packed >> 32);
    }
    internal static long BucketOf(long packed) {
      return packed & 0xFFFFFFFFL;
    }
    /// <summary>Inclusive key range covering every bucket of one topic - a single PK seek.</summary>
    internal static void TopicRange(int topicId, out long lo, out long hi) {
      lo = PackId(topicId, 0);
      hi = PackId(topicId, uint.MaxValue);
    }

    #region sample keys

    /// <summary>Width of a raw sample key: topic id then instant, both big-endian.</summary>
    internal const int KEY_LEN = 12;

    /// <summary>Key for a raw sample: the topic, then the instant, as one comparable blob.</summary>
    /// <remarks>Twelve bytes rather than a packed Int64, and the reason is that there is nothing
    /// left to budget. Packing forces sixty-three bits to be divided between the topic count, the
    /// calendar span and the resolution, and each of those three is a guess about how the server
    /// will be used decades from now; two such guesses were already made and revised while this
    /// was being designed. Here the topic gets a whole Int32 and the instant a whole Int64 of
    /// ticks, so no arrangement of them can run out. Measured against the packed alternative the
    /// cost is eighteen bytes a document - some 58 MB over the live archive - at identical query
    /// time, both being a primary-key range.
    /// <para>Big-endian on both halves, because LiteDB compares Binary bytewise: written this way
    /// the primary key sorts by topic first and by time within a topic, which turns every
    /// per-topic read - folding, seeding, retention - into an exact range instead of a scan across
    /// every other topic's rows. That is the point of the layout, and it is why the topic id is no
    /// longer a field beside the key. What it costs is the free cross-topic time ordering: a chart
    /// over several topics now merges one ordered stream per topic instead of reading one.</para>
    /// <para>Ticks, not milliseconds: eight bytes hold the whole DateTime range, so the stored
    /// instant is the one handed in, exactly. That removes the sequence counter a packed key needs
    /// - on a 100 ns grid two samples of one topic cannot collide - and with it the assumption
    /// that a topic never reports twice in one server tick, which is a property of today's
    /// scheduler rather than of the format.</para>
    /// <para>Time belongs in the key rather than in a DateTime field beside it for a separate
    /// reason that still holds: LiteDB puts DateTime through LOCAL time end to end - on write, on
    /// read and in query parameters - so in the hour the clock repeats every autumn two instants an
    /// hour apart read back identical and a window over that hour collapses to nothing. Raw bytes
    /// have no such notion.</para></remarks>
    internal static byte[] PackSample(int topicId, DateTime utc) {
      return PackSample(topicId, utc.Ticks);
    }

    internal static byte[] PackSample(int topicId, long ticks) {
      if(topicId < 0) {
        throw new ArgumentOutOfRangeException("topicId", topicId, "topic id must not be negative");
      }
      if(ticks < 0) {
        throw new ArgumentOutOfRangeException("ticks", ticks, "instant outside the representable range");
      }
      var b = new byte[KEY_LEN];
      b[0] = (byte)(topicId >> 24);
      b[1] = (byte)(topicId >> 16);
      b[2] = (byte)(topicId >> 8);
      b[3] = (byte)topicId;
      for(int i = 0; i < 8; i++) {
        b[4 + i] = (byte)(ticks >> (56 - i * 8));
      }
      return b;
    }

    /// <summary>Lowest key one topic can hold - the inclusive start of a whole-topic range.</summary>
    internal static byte[] TopicFloor(int topicId) {
      return PackSample(topicId, 0L);
    }
    /// <summary>Highest key one topic can hold - the inclusive end of a whole-topic range.</summary>
    internal static byte[] TopicCeil(int topicId) {
      return PackSample(topicId, DateTime.MaxValue.Ticks);
    }

    internal static int TopicOfSample(byte[] key) {
      return (key[0] << 24) | (key[1] << 16) | (key[2] << 8) | key[3];
    }

    /// <summary>The instant a sample key stands for, in UTC.</summary>
    internal static DateTime TimeOfSample(byte[] key) {
      long t = 0;
      for(int i = 0; i < 8; i++) {
        t = (t << 8) | key[4 + i];
      }
      return new DateTime(t, DateTimeKind.Utc);
    }

    #endregion sample keys
  }
}
