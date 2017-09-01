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

    static RPC() {
      _list = new Dictionary<string, Action<JSC.JSValue[]>>();
    }

    public static void Register(string name, Action<JSC.JSValue[]> cb) {
      lock(_list) {
        _list.Add(name, cb);
      }
    }
    internal static void Call(string name, JSC.JSValue[] args) {
      Action<JSC.JSValue[]> cb;
      if(_list.TryGetValue(name, out cb)) {
        cb.Invoke(args);
      }
    }
  }
}
