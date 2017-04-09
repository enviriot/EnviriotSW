///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace X13.Periphery {
  internal interface IMsGate {
    void SendGw(byte[] addr, MsMessage msg);
    void SendGw(MsDevice dev, MsMessage msg);
    byte gwIdx { get; }
    byte gwRadius { get; }
    string name { get; }
    string Addr2If(byte[] addr);
    void Stop();
  }
}
