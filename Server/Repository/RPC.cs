///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public static class RPC {
    private static readonly Dictionary<string, Action<JSC.JSValue[]>> _list;
    private static readonly Dictionary<string, Action<Topic, Perform.ArtEnum>> _cctors;

    static RPC() {
      _list = new Dictionary<string, Action<JSC.JSValue[]>>();
      _cctors = new Dictionary<string, Action<Topic, Perform.ArtEnum>>();
    }

    public static void Register(string name, Action<JSC.JSValue[]> cb) {
      lock(_list) {
        _list.Add(name, cb);
      }
    }
    public static void Register(string name, Action<Topic, Perform.ArtEnum> cb) {
      lock(_list) {
        _cctors.Add(name, cb);
      }
    }
    internal static void Call(string name, JSC.JSValue[] args) {
      if(_list.TryGetValue(name, out Action<JSC.JSValue[]> cb)) {
        cb.Invoke(args);
      }
    }
    internal static void CCtor(string name, Topic t, Perform.ArtEnum a) {
      if(_cctors.TryGetValue(name, out Action<Topic, Perform.ArtEnum> cb)) {
        try {
          cb.Invoke(t, a);
        }
        catch(Exception ex) {
          Log.Warning("RPC.CCtor({0}, {1}, {2}) - {3}", name, t.Path, a, ex);
        }
      }
    }
  }
}
