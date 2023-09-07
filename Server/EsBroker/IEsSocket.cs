///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSL = NiL.JS.BaseLibrary;
using System;

namespace X13.EsBroker {
  internal interface IEsSocket : IDisposable {
    System.Net.IPEndPoint RemoteEndPoint { get; }
    void SendArr(JSL.Array arr, bool rep = true);
  }
}