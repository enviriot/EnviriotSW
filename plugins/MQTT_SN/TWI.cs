///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading.Tasks;
using System.Threading;

namespace X13.Periphery {
  internal class TWI {
    private Topic _owner;
    private Action<byte[]> _pub;

    public TWI(Topic owner, Action<byte[]> pub) {
      this._owner = owner;
      this._pub = pub;
    }

    public void Recv(byte[] buf) {
      throw new NotImplementedException();
    }
  }
}
