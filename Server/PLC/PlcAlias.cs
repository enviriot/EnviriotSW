///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.PLC {
  internal class PlcAlias {
    private Topic _owner;

    public PlcAlias(Topic owner) {
      this._owner = owner;
    }
  }
}
