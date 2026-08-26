///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public static class RPC {
    // Concurrent: plugins register from Init()/Start() on the main thread while worker threads
    // (e.g. PersistentStorage's) already Call/CCtor - a plain Dictionary is not safe for that.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<JSC.JSValue[]>> _list
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<JSC.JSValue[]>>();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, Perform.E_Art>> _cctors
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, Perform.E_Art>>();

    public static void Register(string name, Action<JSC.JSValue[]> cb) {
      if(!_list.TryAdd(name, cb)) {  // keeps Dictionary.Add's contract: a duplicate name is a bug
        throw new ArgumentException("RPC.Register - duplicate name: " + name);
      }
    }
    public static void Register(string name, Action<Topic, Perform.E_Art> cb) {
      if(!_cctors.TryAdd(name, cb)) {
        throw new ArgumentException("RPC.Register(cctor) - duplicate name: " + name);
      }
    }
    public static void Call(string name, JSC.JSValue[] args) {
      Action<JSC.JSValue[]> cb;
      if(_list.TryGetValue(name, out cb)) {
        cb.Invoke(args);
      }
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
