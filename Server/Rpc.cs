///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Threading;
using X13.Repository;

namespace X13 {
  /// <summary>A flat name space of handlers, each called about one topic.</summary>
  /// <remarks>The topic used to travel as the first element of a JSValue[], stringified to its
  /// path, with the caller's argument appended after it. Every handler then began by checking the
  /// arity and resolving the path back into whatever it needed - written out five times in MQTT_SN
  /// alone - and, worse, refused the call outright when an argument WAS supplied, because that
  /// made the array two long. The action did nothing and the client was told it had worked.
  /// <para>Passing the topic beside the argument instead of inside it makes that unexpressible.
  /// It does put a repository type back into this file, which was free of one; the price is worth
  /// paying, because every call here is about a topic and pretending otherwise cost correctness.
  /// </para></remarks>
  public static class RPC {
    // Concurrent: plugins register from Init()/Start() on the main thread while worker threads
    // (e.g. PersistentStorage's) already Call - a plain Dictionary is not safe for that.
    //
    // One dictionary for both registration shapes, not two: the name space has to stay single, or
    // the duplicate-name check below would let the same name be registered once in each map and
    // Call would silently pick one. A handler registered without a reply is adapted on the way in,
    // so every entry here has the same signature and "exactly one reply per call" holds for all of
    // them - see Register(name, Action<Topic, JSValue>).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, JSC.JSValue, Action<JSC.JSValue>>> _list
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, JSC.JSValue, Action<JSC.JSValue>>>();

    /// <summary>Registers a handler that has nothing to report back.</summary>
    /// <remarks>Answers <c>undefined</c> the moment the handler returns, so a caller waiting on a
    /// reply is not left waiting on a handler that was never going to send one. That is what the
    /// callers of such handlers already assume - the menu actions in base.xst have always been
    /// fire-and-forget - and it keeps the reply invariant the same for every registered name.</remarks>
    public static void Register(string name, Action<Topic, JSC.JSValue> cb) {
      if(cb == null) {
        throw new ArgumentNullException("cb");
      }
      Register(name, new Action<Topic, JSC.JSValue, Action<JSC.JSValue>>((t, arg, reply) => {
        cb(t, arg);
        reply(JSC.JSValue.Undefined);
      }));
    }

    /// <summary>Registers a handler that answers, possibly later and from another thread.</summary>
    /// <remarks><paramref name="cb"/> receives the reply delegate and must call it exactly once,
    /// whenever the work is done. Calling it more than once is harmless - Call wraps it so only
    /// the first answer is passed on - but never calling it leaves the caller waiting, so a
    /// handler that can fail must answer with a failure rather than return quietly.</remarks>
    public static void Register(string name, Action<Topic, JSC.JSValue, Action<JSC.JSValue>> cb) {
      if(!_list.TryAdd(name, cb)) {  // keeps Dictionary.Add's contract: a duplicate name is a bug
        throw new ArgumentException("RPC.Register - duplicate name: " + name);
      }
    }

    /// <summary>Invokes a registered handler about one topic.</summary>
    /// <param name="t">What the call is about - the topic the action was declared on.</param>
    /// <param name="arg">Whatever the caller supplied, or undefined. One value, not a list: no
    /// caller has ever passed more, and a list is what let the topic hide among the arguments.</param>
    /// <param name="reply">Called with whatever the handler answers, at most once. Omitted for a
    /// caller that does not care.</param>
    /// <returns>False when no handler is registered under this name - the caller then knows the
    /// call went nowhere instead of waiting for an answer that cannot come.</returns>
    /// <remarks>The one-shot guard belongs here rather than in each handler: a second answer would
    /// travel back to a client that correlates responses by request id, and hand it another
    /// request's result. A late answer after a caller-side timeout lands in the same guard.</remarks>
    public static bool Call(string name, Topic t, JSC.JSValue arg, Action<JSC.JSValue> reply = null) {
      Action<Topic, JSC.JSValue, Action<JSC.JSValue>> cb;
      if(!_list.TryGetValue(name, out cb)) {
        return false;
      }
      int answered = 0;
      cb.Invoke(t, arg ?? JSC.JSValue.Undefined, v => {
        if(Interlocked.Exchange(ref answered, 1) == 0 && reply != null) {
          reply(v);
        }
      });
      return true;
    }
  }
}
