///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;

namespace X13 {
  /// <summary>Writes a repeating fault down once a second, and says how many it held back.</summary>
  /// <remarks>Everything here runs on a loop that turns about sixty times a second, so a fault that happens
  /// every turn - a plugin throwing on every tick, a subscriber throwing on every value - would be
  /// written down sixty times a second with a full stack each time, burying the log that was meant to
  /// explain it.
  /// <para>The budget is per (site, exception type) and not per site alone. With one budget per
  /// site, a fault that happens constantly spends it and a DIFFERENT fault from the same place -
  /// the rarer and more interesting one - is never written down at all.</para>
  /// <para>An instance rather than a static, because a static would carry one caller's budget into
  /// another's: two tests, two servers in one process, or simply the repository and the engine
  /// loop sharing a key by accident. Each loop owns one and flushes it once a turn.</para></remarks>
  internal sealed class FaultThrottle {
    private readonly object _sync = new object();
    private readonly Dictionary<string, Entry> _seen = new Dictionary<string, Entry>();
    private int _held;
    private DateTime _day;

    /// <summary>Reports a fault, or holds it back and remembers that it did.</summary>
    /// <param name="ours">True when the fault is in code that owns this throttle - an error,
    /// because nobody outside could have caused it. False for a callback somebody else supplied -
    /// a warning: the loop is fine, the question is only who does not get told.</param>
    /// <param name="subject">What the fault was about, or null. Free-form: a command, an event, a
    /// duration - whatever names the occasion in the log line.</param>
    public void Report(bool ours, string where, object subject, Exception ex) {
      lock(_sync) {
        string key = where + "|" + (ex == null ? "?" : ex.GetType().Name);
        Entry e;
        if(!_seen.TryGetValue(key, out e)) {
          e = new Entry();
          _seen[key] = e;
        }
        e.ours = ours;
        e.where = where;
        e.what = ex == null ? "?" : ex.GetType().Name + ": " + ex.Message;
        e.count++;
        e.total++;
        DateTime now = DateTime.Now;
        if(now < e.next) {
          _held++;
          return;
        }
        Emit(e, now, subject == null ? "-" : subject.ToString(), ex == null ? "?" : ex.ToString());
      }
    }
    /// <summary>Reports what was held back, and once a day everything that happened.</summary>
    /// <remarks>Without the first half the tail of a burst is never accounted for: the held count
    /// is only carried into the NEXT line from that key, and a fault that stops has no next line.
    /// A fault that happened three times and went away should still read as three.
    /// <para>The second half is there because a throttle is also a way to miss things. A fault
    /// that drips - once a minute, all night - gets one line and then silence, and nobody scrolls
    /// back to count them. A total written when the date turns over is a line somebody actually
    /// reads, and it lands next to the date banner the engine loop already writes.</para>
    /// <para>Driven from here rather than from a driver of its own because Flush is already called
    /// once a turn by every owner - the repository, the engine loop and the script timers - so the
    /// date is noticed three times over and no registry of instances is needed.</para></remarks>
    public void Flush(DateTime now) {
      lock(_sync) {
        if(_day != now.Date) {
          if(_day != default(DateTime)) {
            Summarise();
          }
          _day = now.Date;
        }
        if(_held == 0) {
          return;
        }
        _held = 0;
        foreach(var kv in _seen) {
          Entry e = kv.Value;
          if(e.count > 0 && now >= e.next) {
            Emit(e, now, "-", e.what + ", no longer occurring");
          } else if(e.count > 0) {
            _held++;   // still inside its window; look again next turn
          }
        }
      }
    }

    /// <summary>One line per kind of fault: how many there were, and the last of them.</summary>
    private void Summarise() {
      foreach(var kv in _seen) {
        Entry e = kv.Value;
        if(e.total > 0) {
          if(e.ours) {
            Log.Error("{0} - {1} failures on {2:yyyy-MM-dd}, last was {3}", e.where, e.total, _day, e.what);
          } else {
            Log.Warning("{0} - {1} failures on {2:yyyy-MM-dd}, last was {3}", e.where, e.total, _day, e.what);
          }
          e.total = 0;
        }
      }
    }

    private void Emit(Entry e, DateTime now, string subject, string detail) {
      int n = e.count;
      e.count = 0;
      e.next = now.AddSeconds(1);
      string more = n > 1 ? string.Format(" [and {0} more]", n - 1) : string.Empty;
      if(e.ours) {
        Log.Error("{0}({1}){2} - {3}", e.where, subject, more, detail);
      } else {
        Log.Warning("{0}({1}){2} - {3}", e.where, subject, more, detail);
      }
    }

    private sealed class Entry {
      public DateTime next;
      public int count;
      public int total;   // since the last daily summary, and not reset by a line being written
      public bool ours;
      public string where;
      public string what;
    }
  }
}
