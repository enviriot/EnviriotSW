﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
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
  internal class MsGSerial : IMsGate { //-V3074
    private static byte[] _disconnectAll;
    private static int _scanBusy;
    private static AutoResetEvent _startScan;
    private static MQTT_SNPl _pl;
    private static Topic _portsTopic;
    private static SubRec _portValuesSR;

    static MsGSerial() {
      _scanBusy = 0;
      _startScan = new AutoResetEvent(false);
      _disconnectAll = new byte[] { 0x02, 0x02, 0x18, 0xC0 };
    }

    public static void Init(MQTT_SNPl pl) {
      _pl = pl;
      ThreadPool.RegisterWaitForSingleObject(_startScan, ScanSerialPorts, null, 289012, false);
      _portsTopic = Topic.root.Get("/$YS/MQTT-SN/ports");
      if(!_portsTopic.CheckAttribute(Topic.Attribute.Required) || _portsTopic.GetState().ValueType!=JSC.JSValueType.Boolean) {
        var act = new JSL.Array(1);
        var r_a = JSC.JSObject.CreateObject();
        r_a["name"] = "MQTT_SN.RefreshPorts";
        r_a["text"] = "Refresh";
        act[0] = r_a;
        _portsTopic.SetField("Action", act);
        _portsTopic.SetState(true);
        _portsTopic.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        _scanBusy = 1;
      }
      _portValuesSR = _portsTopic.Subscribe(SubRec.SubMask.Chldren | SubRec.SubMask.Value, PortValuesChanged);

      _startScan.Set();
    }

    private static void PortValuesChanged(Perform p, SubRec sr) {
      _startScan.Set();
    }

    public static void StartScan() {
      _startScan.Set();
    }

    private static void ScanSerialPorts(object o, bool intervalScan) {
      if(_portsTopic.GetState().ValueType==JSC.JSValueType.Boolean && (bool)_portsTopic.GetState()) {
        Interlocked.CompareExchange(ref _scanBusy, 1, 0);  // turn on scan
      } else {
        Interlocked.CompareExchange(ref _scanBusy, 0, 1);  // turn off scan
      }
      if(Interlocked.CompareExchange(ref _scanBusy, 2, 1) != 1) {
        return;
      }
      SerialPort port = null;

      if(!intervalScan || !_pl._devs.Any() || _pl._devs.Where(z => z.state == State.Lost || z.state == State.Disconnected).Select(z => z.owner.GetField("MQTT-SN.tag").Value as string).Any(z => z != null && z.Length > 2 && z[2] == 'S')) {
        var pns = SerialPort.GetPortNames().Where(z => !z.StartsWith("/dev/tty") || z.StartsWith("/dev/ttyS") || z.StartsWith("/dev/ttyUSB") || z.StartsWith("/dev/ttyA")).ToArray();
        for(int i = 0; i < pns.Length; i++) {
          if(_pl._gates.Exists(z => z.name == pns[i])) {
            continue;
          }
          {
            string pn = pns[i];
            int si = pn.LastIndexOf('/');
            if(si>=0){
              pn = pn.Substring(si+1);
            }
            var portT = _portsTopic.Get(pn, true, _portsTopic);
            if(portT.GetState().ValueType != NiL.JS.Core.JSValueType.Boolean) {
              portT.SetAttribute(Topic.Attribute.Config);
              portT.SetState(true, _portsTopic);
            } else if((bool)portT.GetState() == false) {
              continue;
            }
          }
          try {
            port = new SerialPort(pns[i], 38400, Parity.None, 8, StopBits.One);
            port.ReadBufferSize = 300;
            port.WriteBufferSize = 300;
            port.ReadTimeout = 5;
            port.Open();
            new MsGSerial(port);
          }
          catch(Exception ex) {
            if(_pl.verbose) {
              Log.Debug("MQTT-SN.Serial search on {0} - {1}", pns[i], ex.Message);
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
        }
        foreach(var t in _portsTopic.children.Where(z=>z.name != "ScanAll" && (z.GetState().ValueType != NiL.JS.Core.JSValueType.Boolean || (bool)z.GetState()) && pns.All(z1 => !z1.EndsWith(z.name)))) {
          t.Remove(_portsTopic);
        }
      }
      Thread.Sleep(200); // prevent rescan
      _scanBusy = 1;
    }

    private SerialPort _port;
    private Queue<MsMessage> _sendQueue;
    private byte[] _sndBuf;
    private DateTime _advTick;

    private byte[] _inBuffer;
    private bool _inEscChar;
    private int _inCnt;
    private int _inLen;
    private DateTime _busyTime;

    internal bool _useSlip;
    internal byte[] _gateAddr;

    public MsGSerial(SerialPort port) {
      _port = port;
      _sendQueue = new Queue<MsMessage>();
      _sndBuf = new byte[384];
      _inBuffer = new byte[384];
      _inEscChar = false;
      _inCnt = -1;
      _inLen = -1;
      _busyTime = DateTime.Now;

      Topic t;
      if(Topic.root.Exist("/$YS/MQTT-SN/radius", out t) && t.GetState().IsNumber) {
        gwRadius = (byte)(int)t.GetState();
        if(gwRadius < 1 || gwRadius > 3) {
          gwRadius = 0;
        }
      } else {
        gwRadius = 1;
      }
      ThreadPool.QueueUserWorkItem(DiscoveryGate);
    }

    #region IMsGate Members
    public void SendGw(byte[] addr, MsMessage msg) {
      msg.GetBytes();
      lock(_sendQueue) {
        _sendQueue.Enqueue(msg);
      }
    }
    public void SendGw(MsDevice dev, MsMessage msg) {
      msg.GetBytes();
      lock(_sendQueue) {
        _sendQueue.Enqueue(msg);
      }
    }
    public void Tick() {
      MsMessage msg = null;
      try {
        if(_port != null && _port.IsOpen) {
          if(GetPacket(ref _inLen, _inBuffer, ref _inCnt, ref _inEscChar)) {
            if(_inLen == 5 && _inBuffer[1] == (byte)MsMessageType.SUBSCRIBE) {
              _advTick = DateTime.Now.AddMilliseconds(100);   // Send Advertise
            }
            if(!_pl.ProcessInPacket(this, _gateAddr, _inBuffer, 0, _inLen)) {
              _port.DiscardInBuffer();
            }
            _inCnt = -1;
          }
          if(_busyTime <= DateTime.Now) {
            lock(_sendQueue) {
              if(_sendQueue.Count > 0) {
                msg = _sendQueue.Dequeue();
              }
            }
            if(msg != null) {
              SendRaw(msg, _sndBuf);
              _busyTime = DateTime.Now.AddMilliseconds(msg.IsRequest ? 20 : 5);
            } else if(_advTick < DateTime.Now) {
              SendRaw(new MsAdvertise(gwIdx, 900), _sndBuf);
              _advTick = DateTime.Now.AddMinutes(15);
            }
          }
          return;
        }
      }
      catch(IOException ex) {
        if(_pl.verbose) {
          Log.Error("MsGSerial({0}).CommThread() - {1}", gwIdx, ex.Message);
        }
      }
      catch(Exception ex) {
        Log.Error("MsGSerial({0}).CommThread() - {1}", gwIdx, ex.ToString());
      }
      if(_pl.verbose) {
        Log.Debug("MsGSerial({0}).CommThread - exit", gwIdx);
      }
      this.Dispose();
    }
    public byte gwIdx { get; private set; }
    public byte gwRadius { get; private set; }
    public string name { get { return _port != null ? _port.PortName : string.Empty; } }
    public string Addr2If(byte[] addr) {
      return _port != null ? _port.PortName : string.Empty;
    }
    public void Stop() {
      try {
        if(_port != null && _port.IsOpen) {
          var nodes = _pl._devs.Where(z => z._gate == this).ToArray();
          for(int i = 0; i < nodes.Length; i++) {
            nodes[i].Stop();
          }
          _port.Close();
          _port = null;
        }
      }
      catch(Exception ex) {
        Log.Error("MsGSerial.Close({0}) - {1}", gwIdx, ex.ToString());
      }
    }
    #endregion IMsGate Members

    private void SendRaw(MsMessage msg, byte[] tmp) {
      if(_port == null || !_port.IsOpen || msg == null) {
        return;
      }
      byte[] buf = msg.GetBytes();
      int i, j = 0;
      byte b;
      b = (byte)buf.Length;
      if(_useSlip) {
        tmp[j++] = 0xC0;
        if(b == 0xC0 || b == 0xDB) {
          tmp[j++] = 0xDB;
          tmp[j++] = (byte)(b ^ 0x20);
        } else {
          tmp[j++] = b;
        }
        for(i = 0; i < buf.Length; i++) {
          if(buf[i] == 0xC0 || buf[i] == 0xDB) {
            tmp[j++] = 0xDB;
            tmp[j++] = (byte)(buf[i] ^ 0x20);
          } else {
            tmp[j++] = buf[i];
          }
        }
        tmp[j++] = 0xC0;
      } else {
        tmp[j++] = b;
        for(i = 0; i < buf.Length; i++) {
          tmp[j++] = buf[i];
        }
      }
      _port.Write(tmp, 0, j);

      if(_pl.verbose) {
        Log.Debug("s {0}: {1}  {2}", _port.PortName, BitConverter.ToString(buf), msg.ToString());
      }
    }
    private bool GetPacket(ref int length, byte[] buf, ref int cnt, ref bool escChar) {
      int b;
      if(_port == null || !_port.IsOpen) {
        return false;
      }
      while(_port.BytesToRead > 0) {
        b = _port.ReadByte();
        if(b < 0) {
          break;
        }
        if(_useSlip) {
          if(b == 0xC0) {
            escChar = false;
            if(cnt > 1 && cnt == length) {
              return true;
            } else {
              if(_pl.verbose && cnt > 1) {
                Log.Warning("r  {0}: {1}  size mismatch: {2}/{3}", _port.PortName, BitConverter.ToString(buf, 0, cnt), cnt, length);
              }
              cnt = -1;
            }
            continue;
          }
          if(b == 0xDB) {
            escChar = true;
            continue;
          }
          if(escChar) {
            b ^= 0x20;
            escChar = false;
          }
          if(cnt == 0x100) {
            cnt = -1;
            continue;
          }
        }
        if(cnt >= 0) {
          buf[cnt++] = (byte)b;
          if(!_useSlip) {
            if(cnt == length) {
              return true;
            }
          }
        } else {
          if(!_useSlip) {
            if(b < 2 || b > MsMessage.MSG_MAX_LENGTH) {
              if(_pl.verbose) {
                Log.Warning("r {0}:0x{1:X2} wrong length of the packet", _port.PortName, b);
              }
              cnt = -1;
              _port.DiscardInBuffer();
              return false;
            }
          }
          length = b;
          cnt++;
        }
      }
      return false;
    }
    private void DiscoveryGate(object o) {
      bool found = false;

      try {
        var tryCnt = 3;
        do {
          _busyTime = DateTime.Now.AddMilliseconds(1100);
          _inCnt = -1;
          _inLen = -1;

          _port.DiscardInBuffer();
          _port.Write(_disconnectAll, 0, _disconnectAll.Length);   // Send Disconnect
          if(_pl.verbose) {
            Log.Debug("s {0}: {1}  DISCONNECT", _port.PortName, BitConverter.ToString(_disconnectAll));
          }

          while(_busyTime > DateTime.Now) {
            if(_port.BytesToRead > 0) {
              _busyTime = DateTime.Now.AddMilliseconds(100);
              if(_inCnt >= 0) {
                _inBuffer[_inCnt] = (byte)_port.ReadByte();
              } else {
                _inLen = _port.ReadByte();
              }
              _inCnt++;
            }
            Thread.Sleep(0);
          }

          if(_inCnt > 2 && _inCnt >= _inLen) {
            var msgTyp = (MsMessageType)(_inBuffer[0] > 1 ? _inBuffer[1] : _inBuffer[3]);
            if(msgTyp == MsMessageType.SEARCHGW || msgTyp == MsMessageType.DHCP_REQ) {   // Received Ack
              _inEscChar = false;
              if(_inCnt > _inLen && _inBuffer[_inCnt - 1] == 0xC0) {
                int j, k = -1;
                for(j = 0; j < _inCnt; j++) {
                  if(_inBuffer[j] == 0xDB) {
                    _inEscChar = true;
                    continue;
                  }
                  if(_inEscChar) {
                    _inBuffer[++k] = (byte)(_inBuffer[j] ^ 0x20);
                    _inEscChar = false;
                  } else {
                    _inBuffer[++k] = _inBuffer[j];
                  }
                }
                _inEscChar = true;
                _inCnt = k;
              }
              if(_inCnt == _inLen) {
                found = true;
                _useSlip = _inEscChar;
                Log.Debug("I {0}: SLIP={1}", _port.PortName, _inEscChar);
                _pl.ProcessInPacket(this, this._gateAddr, _inBuffer, 0, _inCnt);
                break;
              }
            }
            if(_pl.verbose) {
              Log.Debug("r {0}: {1}  {2}", _port.PortName, BitConverter.ToString(_inBuffer, 0, _inCnt), msgTyp);
            }
          }
          Thread.Sleep(90);
        } while(--tryCnt > 0);
      }
      catch(Exception ex) {
        if(_pl.verbose) {
          Log.Debug("MQTT-SN.Serial search on {0} - {1}", _port!=null?_port.PortName:"Unknown", ex.Message);
        }
      }
      if(!found) {
        try {
          if(_port != null) {
            if(_port.IsOpen) {
              _port.Close();
            }
            _port.Dispose();
          }
        }
        catch(Exception) {
        }
        return;
      }

      _inCnt = -1;
      _inLen = -1;
      _inEscChar = false;

      byte i = 1;
      _pl._gates.Add(this);
      foreach(var g in _pl._gates) {
        i = g.gwIdx >= i ? (byte)(g.gwIdx + 1) : i;
      }
      gwIdx = i;
      int tmpAddr;
      if(!int.TryParse(new string(_port.PortName.Where(z => char.IsDigit(z)).ToArray()), out tmpAddr) || tmpAddr == 0 || tmpAddr > 254) {
        tmpAddr = (byte)(new Random()).Next(1, 254);
      }
      _gateAddr = new byte[] { gwIdx, (byte)tmpAddr };
    }
    private void Dispose() {
      var p = Interlocked.Exchange(ref _port, null);
      if(p != null) {
        try {
          if(p.IsOpen) {
            p.Close();
          }
        }
        catch(Exception) {
        }
      }
      lock(_pl._gates) {
        _pl._gates.Remove(this);
      }
    }
  }
}
