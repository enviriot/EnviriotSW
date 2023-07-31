using JSL = NiL.JS.BaseLibrary;
using System;

namespace X13.EsBroker {
  internal interface IEsSocket : IDisposable {
    System.Net.IPEndPoint RemoteEndPoint { get; }
    Action<EsMessage> Callback { get; set; }
    void SendArr(JSL.Array arr, bool rep = true);
  }
}