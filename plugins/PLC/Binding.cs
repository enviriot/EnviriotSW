///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.PLC {
  internal class Binding : IPlcItem {
    private Topic _owner;
    private   PlcPl _plc;

    public Binding(PlcPl plc, Topic owner) {
      this._plc = plc;
      this._owner = owner;
    }

    public void Changed(Perform p, SubRec r) {
      throw new NotImplementedException();
    }

    public int Layer {
      get { 
        return -1; 
      }
    }
  }
}
