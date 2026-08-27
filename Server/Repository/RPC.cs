///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace X13.Repository {
  public static class RPC {
    // Concurrent: plugins register from Init()/Start() on the main thread while worker threads
    // (e.g. PersistentStorage's) already Call/CCtor - a plain Dictionary is not safe for that.
    //
    // One dictionary for both registration shapes, not two: the name space has to stay single, or
    // the duplicate-name check below would let the same name be registered once in each map and
    // Call would silently pick one. A handler registered without a reply is adapted on the way in,
    // so every entry here has the same signature and "exactly one reply per call" holds for all of
    // them - see Register(name, Action<JSValue[]>).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<JSC.JSValue[], Action<JSC.JSValue>>> _list
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<JSC.JSValue[], Action<JSC.JSValue>>>();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, Perform.E_Art>> _cctors
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, Perform.E_Art>>();

    /// <summary>Registers a handler that has nothing to report back.</summary>
    /// <remarks>Answers <c>undefined</c> the moment the handler returns, so a caller waiting on a
    /// reply is not left waiting on a handler that was never going to send one. That is what the
    /// callers of such handlers already assume - the menu actions in base.xst have always been
    /// fire-and-forget - and it keeps the reply invariant the same for every registered name.</remarks>
    public static void Register(string name, Action<JSC.JSValue[]> cb) {
      if(cb == null) {
        throw new ArgumentNullException("cb");
      }
      Register(name, new Action<JSC.JSValue[], Action<JSC.JSValue>>((args, reply) => {
        cb(args);
        reply(JSC.JSValue.Undefined);
      }));
    }

    /// <summary>Registers a handler that answers, possibly later and from another thread.</summary>
    /// <remarks><paramref name="cb"/> receives the reply delegate and must call it exactly once,
    /// whenever the work is done. Calling it more than once is harmless - Call wraps it so only
    /// the first answer is passed on - but never calling it leaves the caller waiting, so a
    /// handler that can fail must answer with a failure rather than return quietly.</remarks>
    public static void Register(string name, Action<JSC.JSValue[], Action<JSC.JSValue>> cb) {
      if(!_list.TryAdd(name, cb)) {  // keeps Dictionary.Add's contract: a duplicate name is a bug
        throw new ArgumentException("RPC.Register - duplicate name: " + name);
      }
    }
    public static void Register(string name, Action<Topic, Perform.E_Art> cb) {
      if(!_cctors.TryAdd(name, cb)) {
        throw new ArgumentException("RPC.Register(cctor) - duplicate name: " + name);
      }
    }

    /// <summary>Invokes a registered handler.</summary>
    /// <param name="reply">Called with whatever the handler answers, at most once. Omitted for a
    /// caller that does not care.</param>
    /// <returns>False when no handler is registered under this name - the caller then knows the
    /// call went nowhere instead of waiting for an answer that cannot come.</returns>
    /// <remarks>The one-shot guard belongs here rather than in each handler: a second answer would
    /// travel back to a client that correlates responses by request id, and hand it another
    /// request's result. A late answer after a caller-side timeout lands in the same guard.</remarks>
    public static bool Call(string name, JSC.JSValue[] args, Action<JSC.JSValue> reply = null) {
      Action<JSC.JSValue[], Action<JSC.JSValue>> cb;
      if(!_list.TryGetValue(name, out cb)) {
        return false;
      }
      int answered = 0;
      cb.Invoke(args, v => {
        if(Interlocked.Exchange(ref answered, 1) == 0 && reply != null) {
          reply(v);
        }
      });
      return true;
    }

    internal static void CCtor(string name, Topic t, Perform.E_Art a) {
      Action<Topic, Perform.E_Art> cb;
      if(_cctors.TryGetValue(name, out cb)) {
        try {
          cb.Invoke(t, a);
        }
        catch(Exception ex) {
          Log.Warning("RPC.CCtor({0}, {1}, {2}) - {3}", name, t.path, a, ex);
        }
      }
    }
  }
}
