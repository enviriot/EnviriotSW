///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using X13.Repository;

namespace X13.Logram {
  interface ILoItem {
    void Changed(Perform p, SubRec r);
    int Layer { get; }
  }
}
