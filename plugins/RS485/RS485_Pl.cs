///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;
using System.IO.Ports;

namespace X13.Periphery {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 8)]
  [ExportMetadata("name", "RS485")]
  public class RS485 : IPlugModul {
    private Topic _owner;
    private Topic _verbose;
    private Topic _portsTopic;
    private int _scanBusy;
    private List<RsGate> _gates;
    private SubRec _portValuesSR;

    public RS485() {
      _gates = new List<RsGate>();
    }

    #region IPlugModul Members
    public void Init() {
      _scanBusy = 0;
      RPC.Register("RS485.RefreshPorts", RefreshPortsRpc);
    }
    public void Start() {
      _owner = Topic.root.Get("/$YS/RS485");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
      _portsTopic = _owner.Get("ports", true, _owner);
      if(!_portsTopic.CheckAttribute(Topic.Attribute.Required)) {
        _portsTopic.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        var act = new JSL.Array(1);
        var r_a = JSC.JSObject.CreateObject();
        r_a["name"] = "RS485.RefreshPorts";
        r_a["text"] = "Refresh";
        act[0] = r_a;
        _portsTopic.SetField("Action", act, _owner);
        _portsTopic.SetState(JSC.JSObject.CreateObject());
      }
      _portValuesSR = _portsTopic.Subscribe(SubRec.SubMask.Chldren | SubRec.SubMask.Value, PortValuesChanged);

      _scanBusy = 1;
      ThreadPool.QueueUserWorkItem(RefreshPorts);
    }

    public void Tick() {
    }
    public void Stop() {
      _portValuesSR.Dispose();
      foreach(var g in _gates) {
        g.Dispose();
      }
    }

    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/RS485", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/RS485", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members

    public bool Verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }

    private void RefreshPortsRpc(JSC.JSValue[] obj) {
      ThreadPool.QueueUserWorkItem(RefreshPorts);
    }
    private void PortValuesChanged(Perform p, SubRec sr) {
      ThreadPool.QueueUserWorkItem(RefreshPorts);
    }
    private void RefreshPorts(object o) {
      if(Interlocked.CompareExchange(ref _scanBusy, 2, 1) != 1) {
        return;
      }
      SerialPort port = null;
      var pns = SerialPort.GetPortNames().Where(z => !z.StartsWith("/dev/tty") || z.StartsWith("/dev/ttyS") || z.StartsWith("/dev/ttyUSB") || z.StartsWith("/dev/ttyAMA")).ToArray();
      Topic curPortT;
      bool openPort;
      RsGate gate;

      var existPT = _portsTopic.children.ToList();
      for(int i = 0; i < pns.Length; i++) {
        string pn = pns[i];
        int si = pn.LastIndexOf('/');
        if(si>=0) {
          pn = pn.Substring(si+1);
        }

        curPortT = _portsTopic.Get(pn, true, _owner);
        existPT.Remove(curPortT);   // exist
        openPort = false;
        if(curPortT.GetState().ValueType != NiL.JS.Core.JSValueType.Boolean) {
          curPortT.SetAttribute(Topic.Attribute.Config);
          curPortT.SetState(false, _owner);
        } else {
          openPort = (bool)curPortT.GetState();
        }
        gate = _gates.FirstOrDefault(z => z.PortName == pns[i]);
        if(openPort && gate==null) {
          try {
            port = new SerialPort(pns[i], 38400, Parity.None, 8, StopBits.One);
            port.ReadBufferSize = 64;
            port.WriteBufferSize = 64;
            port.RtsEnable = true;
            port.ReadTimeout = 5;
            port.Open();
            _gates.Add(new RsGate(port));
            Log.Info("{0} RS485 opened", pns[i]);
          }
          catch(Exception ex) {
            if(Verbose) {
              Log.Debug("RS485 search on {0} - {1}", pns[i], ex.Message);
            }
            try {
              if(port != null) {
                if(port.IsOpen) {
                  port.Close();
                }
                port.Dispose();
              }
            }
            catch(Exception) {
            }
          }
          port = null;
        } else if(gate!=null && !openPort) {
          _gates.Remove(gate);
          Log.Info("{0} RS485 closed", gate.PortName);
          gate.Dispose();
        }
      }
      foreach(var t in existPT) {  // Remove port if not exist and not enabled
        var st = t.GetState();
        if(st.ValueType != NiL.JS.Core.JSValueType.Boolean || !((bool)st)) {
          t.Remove(_owner);
        }
      }
      _scanBusy = 1;
    }
  }
}
