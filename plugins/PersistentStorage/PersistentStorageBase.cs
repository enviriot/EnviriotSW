///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;
using System.IO;
using NiL.JS.Extensions;
using System.Runtime.CompilerServices;

namespace X13.PersistentStorage {
  internal abstract class PersistentStorageBase : IPlugModul {
    private const string DISABLE_SIGN = "Disabled";
    private readonly string ENABLE_SIGN;

    protected Topic _owner;
    protected readonly System.Collections.Concurrent.ConcurrentQueue<Perform> _q;
    protected Thread _tr;
    protected bool _terminate;
    protected readonly AutoResetEvent _tick;
    protected PersistentStorageBase(string en) {
      ENABLE_SIGN = en;
      _tick = new AutoResetEvent(false);
      _q = new System.Collections.Concurrent.ConcurrentQueue<Perform>();
    }

    protected abstract void ThreadM();
    private void SubFunc(Perform p) {
      if(p.Art == Perform.E_Art.subscribe || p.Art == Perform.E_Art.subAck || p.Art == Perform.E_Art.setField || p.Art == Perform.E_Art.setState || p.Art == Perform.E_Art.unsubscribe || p.Prim == _owner) {
        return;
      }
      _q.Enqueue(p);
    }

    #region IPlugModul Members
    public void Init() {
      _owner = Topic.root.Get("/$YS/PersistentStorage", true);
    }
    public void Start() {
      _terminate = false;
      _tr = new Thread(new ThreadStart(ThreadM)) {
        IsBackground = true,
        Name = "PersistentStorage",
        Priority = ThreadPriority.BelowNormal
      };
      _tr.Start();
      _tick.WaitOne();  // wait load
      Topic.Subscribe(SubFunc);
    }
    public void Tick() {
      if(_q.Any()) {
        _tick.Set();
      }
    }
    public void Stop() {
      _terminate = true;
      _tick.Set();
      if(!_tr.Join(5000)) {
        _tr.Abort();
      }
      //Interlocked.Exchange(ref _db, null)?.Dispose();
      _tick.Dispose();
    }
    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/PersistentStorage", true);
        if(en.GetState().ValueType == JSC.JSValueType.Boolean) {
          var r = (bool)en.GetState();
          en.SetState(r ? ENABLE_SIGN : DISABLE_SIGN);
          return r;
        } else if(en.GetState().ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(en.GetState().As<string>())) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(ENABLE_SIGN);
          return true;
        }
        return en.GetState().As<string>() == ENABLE_SIGN;
      }
      set {
        var en = Topic.root.Get("/$YS/PersistentStorage", true);
        en.SetState(value ? ENABLE_SIGN : DISABLE_SIGN);
      }
    }
    #endregion IPlugModul Members


  }
}
