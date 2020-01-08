///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using X13.Repository;

namespace X13.Periphery {
  internal class RsGate : IDisposable {
    private SerialPort _port;

    public RsGate(SerialPort port) {
      _port = port;
    }

    public string PortName { get { return _port.PortName; } }

    #region IDisposable Members
    public void Dispose() {
      if(_port.IsOpen) {
        _port.Close();
      }
    }
    #endregion IDisposable Members
  }
}
