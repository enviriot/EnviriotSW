///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
namespace X13.Periphery {
  internal interface IMsExt : IDisposable {
    void Recv(byte[] buf);
    void SendAck(byte[] buf);
    void Tick();
  }
}
