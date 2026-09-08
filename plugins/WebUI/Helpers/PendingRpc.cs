///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using X13.Repository;

namespace X13.WebUI {
  /// <summary>Turns one RPC.Call into a ViewOpResult that arrives whenever the plugin answers.</summary>
  /// <remarks>The deadline is the reason this exists as a place rather than a lambda. ws-client.js
  /// correlates on the request id and has no timeout at all (see ViewSession.Handle and
  /// WebUiHost.Pump), so the first plugin that forgets to answer - or throws before it gets to -
  /// would hang that request's promise for the life of the page. Nothing below can make a plugin
  /// answer; it can make sure the client always hears something.
  /// <para>Every entry is answered exactly once: by the plugin, by the deadline, or not at all if
  /// no handler was registered - and that last case is reported synchronously, before an entry is
  /// ever tracked. RPC.Call's own one-shot guard is what makes a late answer after the deadline
  /// harmless rather than a second response carrying an already-used id.</para></remarks>
  internal static class PendingRpc {
    /// <summary>How long a plugin may take before the client is told it did not answer.</summary>
    /// <remarks>Generous on purpose: this is the backstop for a broken handler, not a service-level
    /// deadline. An operation with a bound of its own (a UART round trip, a query) should report its
    /// own failure long before this, and say something more useful than "timeout" when it does.</remarks>
    internal static int TimeoutMs = 30000;

    /// <summary>Elapsed time for the timeouts, running from process start and never adjusted.</summary>
    /// <remarks>Deadlines used to be DateTime.Now, which moves: the daylight-saving step alone
    /// either fires every outstanding action an hour early or holds it an hour late, and an NTP
    /// correction does the same on a smaller scale. Nothing here wants a wall clock - it wants
    /// "has 30 seconds passed", which is what a monotonic count answers.</remarks>
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private sealed class Entry {
      public readonly object Sync = new object();
      /// <summary>_clock.ElapsedMilliseconds at which this entry times out.</summary>
      public long Deadline;
      public Action<ViewOpResult> Done;
      public ViewOpResult Result;
      public bool HasResult;
      public bool Delivered;
    }

    private static readonly List<Entry> _pending = new List<Entry>();

    /// <summary>Calls <paramref name="name"/> and hands back the result, or a way to wait for it.</summary>
    /// <returns>An error when nothing is registered under that name - a caller that waited on it
    /// would wait forever - otherwise a Pending result.</returns>
    public static ViewOpResult Begin(string name, Topic t, JSC.JSValue arg) {
      Entry e = new Entry() { Deadline = _clock.ElapsedMilliseconds + TimeoutMs };
      if(!RPC.Call(name, t, arg, v => Complete(e, ToResult(v)))) {
        return ViewOpResult.Error("action_no_handler", "No handler is registered for action: " + (name ?? "<null>"));
      }
      // Only tracked if it is still outstanding. A handler registered without a reply answers
      // inside the Call above, so the common case - every menu action in base.xst - never touches
      // the list or the sweep at all.
      lock(_pending) {
        if(!e.HasResult) _pending.Add(e);
      }
      return ViewOpResult.Pending(done => Attach(e, done));
    }

    /// <summary>Answers everything whose deadline has passed. Engine thread only.</summary>
    internal static void Sweep() {
      List<Entry> due = null;
      long now = _clock.ElapsedMilliseconds;
      lock(_pending) {
        for(int i = _pending.Count - 1; i >= 0; i--) {
          if(_pending[i].Deadline > now) continue;
          if(due == null) due = new List<Entry>();
          due.Add(_pending[i]);
          _pending.RemoveAt(i);
        }
      }
      if(due == null) return;
      foreach(Entry e in due) {
        Complete(e, ViewOpResult.Error("action_timeout", "Action did not answer within " + TimeoutMs.ToString() + " ms"));
      }
    }

    /// <summary>Drops everything outstanding, unanswered. Tests only - the list is process-wide.</summary>
    internal static void Reset() {
      lock(_pending) {
        _pending.Clear();
      }
    }

    // Called from whatever thread the plugin answers on, so the delivery decision is taken under
    // the entry's own lock and the callback runs outside it - the subscriber posts to the engine
    // thread from there, and holding a lock across that would serialize unrelated sessions.
    private static void Complete(Entry e, ViewOpResult result) {
      Action<ViewOpResult> done = null;
      lock(e.Sync) {
        if(e.HasResult) return;
        e.HasResult = true;
        e.Result = result ?? ViewOpResult.Error("action_failed", "Action produced no result");
        if(e.Done != null) {
          e.Delivered = true;
          done = e.Done;
        }
      }
      if(done != null) done(e.Result);
      lock(_pending) {
        _pending.Remove(e);
      }
    }

    // The result can already be here: a handler registered without a reply answers inside Begin's
    // RPC.Call, before anyone has had the chance to subscribe.
    private static void Attach(Entry e, Action<ViewOpResult> done) {
      if(done == null) return;
      ViewOpResult ready = null;
      lock(e.Sync) {
        if(e.Delivered) return;
        e.Done = done;
        if(e.HasResult) {
          e.Delivered = true;
          ready = e.Result;
        }
      }
      if(ready != null) done(ready);
    }

    /// <summary>Reads a plugin's answer as an outcome.</summary>
    /// <remarks>Undefined and null both mean plain success - that is what a handler registered
    /// without a reply sends, and what a handler with nothing to report should send. An object
    /// carrying a string "error" is a failure, which is how a plugin reports one without needing
    /// an API of its own; anything else is the result value.</remarks>
    private static ViewOpResult ToResult(JSC.JSValue v) {
      if(v == null || !v.Defined || v.IsNull) {
        return ViewOpResult.Success();
      }
      string error = v.AsString("error", null);
      if(!string.IsNullOrEmpty(error)) {
        return ViewOpResult.Error(error, v.AsString("message", error));
      }
      return ViewOpResult.Success(v);
    }
  }
}
