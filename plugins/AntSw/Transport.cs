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
    // Binary frame format, mirrors CONS/cons_bin.c on the device:
    //   0xA5 | Cmd/Resp | Addr | Ev/EvEx(1-2B) | Len(0/1/2B) | Data[0..Len-1](1-2B each)
    private const byte BIN_SYNC = 0xA5;

    private const byte CMDRESP_RESP     = 0x80;
    private const byte CMDRESP_EVEX     = 0x40;
    private const byte CMDRESP_HAS_DATA = 0x20;
    private const byte CMDRESP_DATA16   = 0x10;
    private const byte CMDRESP_CMD_MASK = 0x0F;

    private const int MAX_DATA = 140;   // CONS_MAX_PARM on the device

    private AntSwPl _pl;
    private SerialPort _port;
    private System.Collections.Concurrent.ConcurrentQueue<Command> _inCmds, _outCmd;

    public Transport(AntSwPl pl) {
      _pl = pl;
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
      try {
        do {
          while(_port!=null && _port.BytesToRead > 0) {
            b = _port.ReadByte();
            if(b<0) {
              Log.Warning("AntSw {0} - closed with error", _port.PortName);
              break;
            }
            cmd = ParseByte((byte)b);
            if(cmd!=null) {
              if(_pl.Verbose) {
                Log.Debug("AntSw R {0}", cmd);
              }
              _inCmds.Enqueue(cmd);
            }
          }

          if(_outCmd.TryDequeue(out cmd)) {
            var bytes = Encode(cmd);
            if(_pl.Verbose) {
              Log.Debug("AntSw S {0}", cmd);
            }
            _port.Write(bytes, 0, bytes.Length);
          }
          Thread.Sleep(30);
        } while(_port!=null && _port.IsOpen);
      }
      catch(Exception ex) {
        Log.Error("AntSw.CommThread - " + ex.ToString());
      }
    }

    #region Receive state machine (mirrors cons_bin_in in CONS/cons_bin.c)
    private enum RxState { Sync, CmdResp, Addr, Ev1, Ev2, LenH, Len, Data8, Data16Hi, Data16Lo, Dispatch }

    private RxState _rxState = RxState.Sync;
    private byte _rxCmdResp;
    private byte _rxAddr;
    private ushort _rxEv;
    private ushort _rxLen;
    private byte _rxHi;
    private List<ushort> _rxData = new List<ushort>();
    private List<byte> _rxRaw = new List<byte>();

    private string RawHex() {
      return string.Join(" ", _rxRaw.Select(x => x.ToString("X2")));
    }

    private Command ParseByte(byte b) {
      if(_rxState == RxState.Sync) {
        if(b==BIN_SYNC) {
          _rxRaw.Clear();
          _rxRaw.Add(b);
          _rxState = RxState.CmdResp;
        } else if(_pl.Verbose) {
          Log.Debug("AntSw R stray byte 0x{0:X2} (waiting for sync)", b);
        }
        return null;
      }
      _rxRaw.Add(b);

      switch(_rxState) {
      case RxState.CmdResp:
        _rxCmdResp = b;
        _rxState = RxState.Addr;
        break;

      case RxState.Addr:
        _rxAddr = b;
        _rxState = RxState.Ev1;
        break;

      case RxState.Ev1:
        _rxEv = b;
        _rxLen = 0;
        _rxData.Clear();
        if((_rxCmdResp & CMDRESP_EVEX)!=0) {
          _rxState = RxState.Ev2;
        } else if((_rxCmdResp & CMDRESP_HAS_DATA)!=0) {
          _rxState = RxState.Len;
        } else {
          _rxState = RxState.Dispatch;
        }
        break;

      case RxState.Ev2:
        _rxEv = (ushort)((_rxEv << 8) | b);
        _rxState = (_rxCmdResp & CMDRESP_HAS_DATA)!=0 ? RxState.LenH : RxState.Dispatch;
        break;

      case RxState.LenH:
        _rxLen = (ushort)(b << 8);
        _rxState = RxState.Len;
        break;

      case RxState.Len:
        _rxLen |= b;
        if(_rxLen > MAX_DATA) {
          Log.Warning("AntSw R dropped: frame too long (cmdresp=0x{0:X2}, addr={1}, ev={2}, len={3}) raw=[{4}] - check device-side Len encoding for this message type",
            _rxCmdResp, _rxAddr, _rxEv, _rxLen, RawHex());
          _rxState = RxState.Sync;      // frame too long - resync
        } else if(_rxLen==0) {
          _rxState = RxState.Dispatch;
        } else {
          _rxState = (_rxCmdResp & CMDRESP_DATA16)!=0 ? RxState.Data16Hi : RxState.Data8;
        }
        break;

      case RxState.Data8:
        _rxData.Add(b);
        if(_rxData.Count >= _rxLen) {
          _rxState = RxState.Dispatch;
        }
        break;

      case RxState.Data16Hi:
        _rxHi = b;
        _rxState = RxState.Data16Lo;
        break;

      case RxState.Data16Lo:
        _rxData.Add((ushort)((_rxHi << 8) | b));
        _rxState = (_rxData.Count >= _rxLen) ? RxState.Dispatch : RxState.Data16Hi;
        break;
      }

      if(_rxState != RxState.Dispatch) {
        return null;
      }
      _rxState = RxState.Sync;
      return BuildCommand();
    }

    private static CommandCode? MapRxCode(byte cmdLow) {
      switch(cmdLow) {
      case 0x01: return CommandCode.Event;    // E - event echo
      case 0x0F: return CommandCode.Fail;     // F - error
      case 0x08: return CommandCode.ExEvent;  // X - extended response
      case 0x0D: return CommandCode.Debug;    // D - debug
      default: return null;
      }
    }

    private Command BuildCommand() {
      var code = MapRxCode((byte)(_rxCmdResp & CMDRESP_CMD_MASK));
      if(code==null) {
        Log.Warning("AntSw R dropped: unrecognized cmd (cmdresp=0x{0:X2}, addr={1}, ev={2}, data=[{3}]) raw=[{4}]",
          _rxCmdResp, _rxAddr, _rxEv, string.Join(",", _rxData), RawHex());
        return null;
      }
      return new Command(code.Value, _rxAddr, _rxEv, _rxData.ToArray());
    }
    #endregion Receive state machine

    #region Send encoding (mirrors cons_bin_response in CONS/cons_bin.c)
    private static byte[] Encode(Command cmd) {
      byte cmdLow;
      switch(cmd.code) {
      case CommandCode.Event:  cmdLow = 0x01; break;
      case CommandCode.Set:    cmdLow = 0x02; break;
      case CommandCode.Get:    cmdLow = 0x03; break;
      case CommandCode.Update: cmdLow = 0x04; break;
      default:
        throw new ArgumentException("AntSw: command " + cmd.code + " cannot be sent to the device");
      }

      bool evex = cmd.param > 0xFF;
      bool hasData = cmd.data!=null && cmd.data.Length>0;
      bool data16 = hasData && cmd.data.Any(v => v>0xFF);

      byte cmdresp = cmdLow;
      if(evex) {
        cmdresp |= CMDRESP_EVEX;
      }
      if(hasData) {
        cmdresp |= CMDRESP_HAS_DATA;
      }
      if(data16) {
        cmdresp |= CMDRESP_DATA16;
      }

      var buf = new List<byte>(8 + (hasData ? cmd.data.Length*2 : 0));
      buf.Add(BIN_SYNC);
      buf.Add(cmdresp);
      buf.Add(cmd.addr);

      if(evex) {
        buf.Add((byte)(cmd.param >> 8));
        buf.Add((byte)(cmd.param & 0xFF));
      } else {
        buf.Add((byte)cmd.param);
      }

      if(hasData) {
        int len = cmd.data.Length;
        if(evex) {
          buf.Add((byte)(len >> 8));
          buf.Add((byte)(len & 0xFF));
        } else {
          buf.Add((byte)len);
        }
        foreach(var v in cmd.data) {
          if(data16) {
            buf.Add((byte)(v >> 8));
            buf.Add((byte)(v & 0xFF));
          } else {
            buf.Add((byte)(v & 0xFF));
          }
        }
      }
      return buf.ToArray();
    }
    #endregion Send encoding

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
    public Command(CommandCode code, byte addr, UInt16 param, params UInt16[] data) {
      this.code = code;
      this.addr = addr;
      this.param = param;
      this.data = data;
    }

    public CommandCode code;
    public byte addr;
    public UInt16 param;
    public UInt16[] data;

    public override string ToString() {
      var sb = new StringBuilder();
      sb.Append((char)(byte)code).Append(addr).Append(',');
      if(param >= 256) {
        // Composite ExEv: <BUS_LOCRQ_* base> | ((idx+1) << 8) - print as idx:sidx
        sb.Append((param & 0xFF) - 1).Append(':').Append(param >> 8);
      } else {
        sb.Append(param);
      }
      if(data!=null) {
        for(int i = 0; i<data.Length; i++) {
          sb.Append(',').Append(data[i]);
        }
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
    Debug = (byte)'D',
  }
}
