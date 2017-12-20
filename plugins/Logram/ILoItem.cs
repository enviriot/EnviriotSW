///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using X13.Repository;

namespace X13.Logram {
  internal interface ILoItem : IComparable<ILoItem> {
    Topic Owner { get; }
    int Layer { get; set; }
    void SetValue(JSC.JSValue value, Topic prim);
    ILoItem[] Route { get; set; }
    void Tick1();
    void Tick2();
    bool Disposed { get; }
  }
}
