///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
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
  /// </para>
  /// <para>There is no reply. A second shape used to exist - the handler was given a delegate and
  /// had to call it exactly once, whenever the work finished - carrying a one-shot guard here, a
  /// deferred result through the WebUI protocol and a timeout sweeper beside it. Not one plugin
  /// ever registered that shape; MQTT and MQTT_SN have only ever used this one, and the rest was
  /// machinery for a consumer that never arrived. What a caller learns now is whether the name
  /// existed, which is what a context menu needs to tell "no such action" from "sent".</para>
  /// </remarks>
  public static class RPC {
    // Concurrent: plugins register from Init()/Start() on the main thread while worker threads
    // (e.g. PersistentStorage's) already Call - a plain Dictionary is not safe for that.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, JSC.JSValue>> _list
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, JSC.JSValue>>();

    /// <summary>Registers the handler for one action name.</summary>
    public static void Register(string name, Action<Topic, JSC.JSValue> cb) {
      if(cb == null) {
        throw new ArgumentNullException("cb");
      }
      if(!_list.TryAdd(name, cb)) {  // keeps Dictionary.Add's contract: a duplicate name is a bug
        throw new ArgumentException("RPC.Register - duplicate name: " + name);
      }
    }

    /// <summary>Invokes a registered handler about one topic.</summary>
    /// <param name="t">What the call is about - the topic the action was declared on.</param>
    /// <param name="arg">Whatever the caller supplied, or undefined. One value, not a list: no
    /// caller has ever passed more, and a list is what let the topic hide among the arguments.</param>
    /// <returns>False when no handler is registered under this name - the caller then knows the
    /// call went nowhere instead of reporting a success it never had.</returns>
    /// <remarks>What the handler throws is NOT caught here, deliberately: the caller is the one
    /// that knows whether a failure is worth reporting, and swallowing it would turn a broken
    /// action into a silent one. See MQTTPl.ReconnectRpc for what that cost once.</remarks>
    public static bool Call(string name, Topic t, JSC.JSValue arg) {
      Action<Topic, JSC.JSValue> cb;
      if(!_list.TryGetValue(name, out cb)) {
        return false;
      }
      cb.Invoke(t, arg ?? JSC.JSValue.Undefined);
      return true;
    }
  }
}
