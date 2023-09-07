///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JST = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.EsBroker {
  internal class EsMessage {
    public readonly EsSocketTCP _conn;
    private JST.Array _request;

    public EsMessage(EsSocketTCP conn, JST.Array req) {
      this._conn = conn;
      this._request = req;
      this.Count = req.Count();
    }
    

    public JSC.JSValue this[int idx] {
      get {
        return _request[idx];
      }
    }
    public readonly int Count;
    public void Response(params JSC.JSValue[] args) {
      _conn.SendArr(new JST.Array(args));
    }
    public override string ToString() {
      return _conn.ToString() + ">" + _request.ToString();
    }
  }
}
