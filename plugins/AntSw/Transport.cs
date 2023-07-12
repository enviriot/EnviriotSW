///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.Periphery {
  internal class Transport : IDisposable {
    private static byte[] COMMAND_CODE_ARRAY = { (byte)CommandCode.Event, (byte)CommandCode.ExEvent, (byte)CommandCode.Fail };


    private AntSwPl _pl;
    private SerialPort _port;
    private byte[] _inBuf;
    private int _inCnt;
    private System.Collections.Concurrent.ConcurrentQueue<Command> _inCmds, _outCmd;

    public Transport(AntSwPl pl) {
      _pl = pl;
      _inBuf = new byte[512];
      _inCnt = 0;
      _inCmds = new System.Collections.Concurrent.ConcurrentQueue<Command>();
      _outCmd = new System.Collections.Concurrent.ConcurrentQueue<Command>();

      var pt = _pl.Owner.Get("port", true, _pl.Owner);
      string pn;
      if(pt.GetState().ValueType != NiL.JS.Core.JSValueType.String || string.IsNullOrEmpty(pn = pt.GetState().Value as string)) {
        pt.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
        pn = "com2";
        pt.SetState(pn, _pl.Owner);
      }
      try {
        _port = new SerialPort(pn, 115200, Parity.None, 8, StopBits.One);
        _port.ReadBufferSize = 512;
        _port.WriteBufferSize = 512;
        _port.ReadTimeout = 15;
        _port.Open();
      }
      catch(Exception ex) {
        Log.Error("AntSw.Open({0}) - {1}", pn, ex.Message);
        return;
      }
      ThreadPool.QueueUserWorkItem(CommThread);
    }
    public Command Read() {
      Command cmd;
      return _inCmds.TryDequeue(out cmd)?cmd:null;
    }
    public void Write(Command cmd) {
      _outCmd.Enqueue(cmd);
    }
    public bool IsOpen { get { return _port!=null && _port.IsOpen; } }

    private void CommThread(object o) {
      int b;
      Command cmd;
      string msg;
      try {
        do {
          while(_port!=null && _port.BytesToRead > 0) {
            b = _port.ReadByte();
            if(b<0) {
              Log.Warning("AntSw {0} - closed with error", _port.PortName);
              break;
            }
            if(b==0x0D) {
              if(_inCnt>3 && COMMAND_CODE_ARRAY.Contains(_inBuf[0])) {
                msg = ASCIIEncoding.ASCII.GetString(_inBuf, 1, _inCnt-1);
                if(_pl.Verbose) {
                  Log.Debug("AntSw R {0}{1}", (CommandCode)_inBuf[0], msg);
                }
                cmd = Command.Parse((CommandCode)_inBuf[0], msg);
                if(cmd!=null) {
                  _inCmds.Enqueue(cmd);
                } else if(_pl.Verbose) {
                  Log.Warning("AntSw R "+ msg);
                }
              } else if(_pl.Verbose) {
                msg = ASCIIEncoding.ASCII.GetString(_inBuf, 0, _inCnt-1);
                Log.Warning("AntSw R"+ msg);
              }
              _inCnt = 0;
            } else if(_inCnt>=_inBuf.Length) {
              if(_pl.Verbose) {
                msg = ASCIIEncoding.ASCII.GetString(_inBuf, 0, _inCnt-1);
                Log.Warning("AntSw R"+ msg);
              }
              _inCnt = -1;
            } else if(_inCnt>=0){
              char ch = (char)b;
              if(_inCnt==0?COMMAND_CODE_ARRAY.Contains((byte)b):(char.IsDigit(ch) || ch==',')) {
                _inBuf[_inCnt++] = (byte)b;
              } else {
                _inCnt = 0;
              }
            }
          }

          if(_outCmd.TryDequeue(out cmd)) {
            msg = cmd.ToString();
            if(_pl.Verbose) {
              Log.Debug("AntSw S " + msg);
            }
            _port.Write(msg+"\r");
          }
          Thread.Sleep(30);
        } while(_port!=null && _port.IsOpen);
      }
      catch(Exception ex) {
        Log.Error("AntSw.CommThread - " + ex.ToString());
      }
    }

    #region IDisposable  Member
    public void Dispose() {
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
    }
    #endregion IDisposable  Member
  }
  internal class Command {
    public static Command Parse(CommandCode cmd, string msg) {
      var sa = msg.Split(',');
      if(sa.Length<2) {
        return null;
      }
      byte addr, param;
      if(!byte.TryParse(sa[0], out addr)) {
        Log.Warning("AntSw R {0}{1} - bad Addr", cmd, msg);
        return null;
      }
      if(!byte.TryParse(sa[1], out param)) {
        Log.Warning("AntSw R {0}{1} - bad Param", cmd, msg);
        return null;
      }
      var r = new Command(cmd, addr, param);
      if(sa.Length > 2) {
        r.data = new ushort[sa.Length-2];
        UInt16 tmp;
        for(int i = 2; i< sa.Length; i++) {
          if(ushort.TryParse(sa[i], out tmp)) {
            r.data[i-2] = tmp;
          } else {
            Log.Warning("AntSw R {0}{1} - bad data[{2}]", cmd, msg, i-2);
            return null;
          }
        }
      }
      return r;
    }

    public Command(CommandCode code, byte addr, byte param, params UInt16[] data) {
      this.code = code;
      this.addr = addr;
      this.param = param;
      this.data = data;
    }

    public CommandCode code;
    public byte addr;
    public byte param;
    public UInt16[] data;

    public override string ToString() {
      string s = ((Char)(byte)this.code).ToString() + addr.ToString() + "," + param.ToString();
      if(data == null || data.Length==0) {
        return s;
      }
      StringBuilder sb = new StringBuilder(s);
      for(int i = 0; i < data.Length; i++) {
        sb.Append(",");
        sb.Append(data[i].ToString());
      }
      return sb.ToString();
    }
  }
  internal enum CommandCode : byte {
    Event = (byte)'E',
    Set = (byte)'S',
    Get = (byte)'G',
    ExEvent = (byte)'X',
    Fail = (byte)'F',
    Update = (byte)'U',
  }
}
