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
    private static JSC.Context _context;

    static TWI() {
      _context = new JSC.Context();
      _context.DefineVariable("Delay").Assign(JSC.JSValue.Wrap(new Func<int, Task>(Delay)));
    }

    private static Task Delay(int delayTime) {
      TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();

      if(delayTime < 0)
        throw new ArgumentOutOfRangeException("Delay time cannot be under 0");

      System.Threading.Timer timer = null;
      timer = new System.Threading.Timer(p => {
        timer.Dispose(); //stop the timer
        tcs.TrySetResult(null); //timer expired, attempt to move task to the completed state.
      }, null, delayTime, System.Threading.Timeout.Infinite);
      return tcs.Task;
    }

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
