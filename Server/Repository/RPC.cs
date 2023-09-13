///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public static class RPC {
    private static Dictionary<string, Action<JSC.JSValue[]>> _list;
    private static Dictionary<string, Action<Topic, Perform.E_Art>> _cctors;

    static RPC() {
      _list = new Dictionary<string, Action<JSC.JSValue[]>>();
      _cctors = new Dictionary<string, Action<Topic, Perform.E_Art>>();
    }

    public static void Register(string name, Action<JSC.JSValue[]> cb) {
      lock(_list) {
        _list.Add(name, cb);
      }
    }
    public static void Register(string name, Action<Topic, Perform.E_Art> cb) {
      lock(_list) {
        _cctors.Add(name, cb);
      }
    }
    internal static void Call(string name, JSC.JSValue[] args) {
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
