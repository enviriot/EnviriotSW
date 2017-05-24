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
  internal class MsGSerial : IMsGate {
    private SerialPort _port;
    private Queue<MsMessage> _sendQueue;
    private MQTT_SNPl _pl;
    private byte[] _sndBuf;
    private DateTime _advTick;

    internal bool _useSlip;
    internal byte[] _gateAddr;

    public MsGSerial(MQTT_SNPl pl, SerialPort port) {
      _port = port;
      _pl = pl;
      byte i = 1;
      foreach(var g in _pl._gates) {
        i = g.gwIdx >= i ? (byte)(g.gwIdx + 1) : i;
      }
      gwIdx = i;
      int tmpAddr;
      if(!int.TryParse(new string(_port.PortName.Where(z => char.IsDigit(z)).ToArray()), out tmpAddr) || tmpAddr == 0 || tmpAddr > 254) {
        tmpAddr = (byte)(new Random()).Next(1, 254);
      }
      _gateAddr = new byte[] { gwIdx, (byte)tmpAddr };
      _sendQueue = new Queue<MsMessage>();
      _sndBuf = new byte[384];

      Topic t;
      if(Topic.root.Exist("/$YS/MQTT-SN/radius", out t) && t.GetState().IsNumber) {
        gwRadius = (byte)(int)t.GetState();
        if(gwRadius < 1 || gwRadius > 3) {
          gwRadius = 0;
        }
      } else {
        gwRadius = 1;
      }
      ThreadPool.QueueUserWorkItem(CommThread);
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
    public byte gwIdx { get; private set; }
    public byte gwRadius { get; private set; }
    public string name { get { return _port != null ? _port.PortName : string.Empty; } }
    public string Addr2If(byte[] addr) {
      return _port != null ? _port.PortName : string.Empty;
    }
    public void Stop() {
      try {
        if(_port != null && _port.IsOpen) {
          var nodes = _pl._devs.Where(z=>z._gate==this).ToArray();
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
    private void CommThread(object o) {
      byte[] buf = new byte[256];
      bool escChar = false;
      int cnt = -1;
      int len = -1;
      MsMessage msg;
      DateTime busyTime = DateTime.Now;
      try {
        while(_port != null && _port.IsOpen) {
          if(GetPacket(ref len, buf, ref cnt, ref escChar)) {
            if(len == 5 && buf[1] == (byte)MsMessageType.SUBSCRIBE) {
              _advTick = DateTime.Now.AddMilliseconds(100);   // Send Advertise
            }
            if(!_pl.ProcessInPacket(this, _gateAddr, buf, 0, len)) {
              _port.DiscardInBuffer();
            }
            cnt = -1;
            msg = null;
            continue;
          }
          msg = null;
          if(busyTime > DateTime.Now) {
            Thread.Sleep(0);
            continue;
          }
          lock(_sendQueue) {
            if(_sendQueue.Count > 0) {
              msg = _sendQueue.Dequeue();
            }
          }
          if(msg != null) {
            SendRaw(msg, _sndBuf);
            busyTime = DateTime.Now.AddMilliseconds(msg.IsRequest ? 20 : 5);
            continue;
          }
          //if(_gwTopic != null && _gwTopic.value != null && (_gwTopic.value.state == State.Disconnected || _gwTopic.value.state == State.Lost)) {
          //  _gwTopic = null;
          //  Thread.Sleep(500);
          //  this.Dispose();
          //  Thread.Sleep(1000);
          //  _pl._startScan.Set();
          //  return;
          //}
          if(_advTick < DateTime.Now) {
            SendRaw(new MsAdvertise(gwIdx, 900), _sndBuf);
            _advTick = DateTime.Now.AddMinutes(15);
          }
          Thread.Sleep(15);
        }
      }
      catch(IOException ex) {
        if(_pl.verbose) {
          Log.Error("MsGSerial({0}).CommThread() - {1}", gwIdx, ex.ToString());
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
