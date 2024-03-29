﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.Periphery {
  internal class DevicePLC : IMsExt {
    private static int _cntCom = 0;
    private readonly int _idx;
    private readonly Topic _owner;
    private readonly Action<byte[]> _pub;
    private readonly Topic _verbose;
    private bool _plcStoped;
    private bool PlcStoped {
      get {
        return _plcStoped;
      }
      set {
        _plcStoped = value;
        var st = value ? 2 : 1;
        if(!_owner.GetState().IsNumber || ((int)_owner.GetState()) != st) {
          _owner.SetState(st);
        }
      }
    }

    private int _offset, _st;  // 0 - idle, 1 - check CRC, 2- check CRC resp, 3-PLC stop, 4 - PLC stop resp, 5- write block, 6- write block resp, 7- PLC start resp
    private Chunk _curChunk;
    private SortedSet<Chunk> _prg;
    private uint _stackBottom;

    public DevicePLC(Topic owner, Action<byte[]> pub) {
      _idx = System.Threading.Interlocked.Increment(ref _cntCom);
      this._owner = owner;
      this._pub = pub;
      this._verbose = Topic.root.Get("/$YS/DevicePLC/verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
      _st = 0;
      _owner.SetState(0);
    }

    public bool Verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }
    public string Path { get { return _owner.path; } }

    #region RPC Members
    public X13.DevicePLC.EP_Compiler Build() {
      var st = _owner.Get("src", false, _owner);
      if(st == null || st.GetState().ValueType != JSC.JSValueType.String || !(st.GetState().Value is string src)) {
        src = string.Empty;
      }
      var c = new X13.DevicePLC.EP_Compiler();
      c.CMsg += C_CMsg;
      return c.Parse(src)?c:null;
    }
    public void StartPlc() {
      _st = 9;
      _pub(new byte[] { (byte)Cmd.PlcStartReq });
    }
    public void StopPlc() {
      _pub(new byte[] { (byte)Cmd.PlcStopReq });
    }
    public void Run(MsDevice dev) {
      var c = this.Build();
      if(c == null) {
        return;
      }
      if(c.ioList != null) {
        foreach(var v in c.ioList) {  // register IO variables
          var t = _owner.parent.Get(v, true, _owner);
          t.SetField("MQTT-SN.tag", v);
        }
      }
      var ch_t = JSC.JSObject.CreateObject();
      if(c.varList != null) {
        string n;
        JSC.JSObject o, mo, mqo;
        foreach(var kv in c.varList) {
          n = kv.Key.Replace('.', '_');
          o = JSC.JSObject.CreateObject();
          mo = JSC.JSObject.CreateObject();
          mqo = JSC.JSObject.CreateObject();
          mqo["tag"] = kv.Value;
          mo["MQTT-SN"] = mqo;
          o["manifest"] = mo;
          var nt = MsDevice.NTTable.FirstOrDefault(z => kv.Value.StartsWith(z.Item1));
          MsDevice.DType vt = nt==null?MsDevice.DType.Integer:nt.Item2;
          switch(vt) {
          case MsDevice.DType.Boolean:
            o["default"] = false;
            o["type"] = "Boolean";
            break;
          case MsDevice.DType.ByteArray:
            o["default"] = null;
            o["type"] = "ByteArray";
            break;
          case MsDevice.DType.String:
            o["default"] = string.Empty;
            o["type"] = "String";
            break;
          default:
            o["default"] = 0;
            o["type"] = "Integer";
            break;
          }
          o["menu"] = "plc";
          ch_t[n] = o;
        }
      }
      _owner.parent.SetField("Children", ch_t, _owner);
      string sTag;
      var varLst = _owner.parent.children.Where(z => (sTag = z.GetField("MQTT-SN.tag").Value as string) != null && sTag.StartsWith("M")).ToArray();
      var rereg = new List<Tuple<Topic, string>>();
      foreach(var t in varLst) {
        sTag = t.GetField("MQTT-SN.tag").Value as string;
        var vt = c.varList.FirstOrDefault(z => t.name == z.Key.Replace('.', '_'));
        if(vt.Key==null) {
          rereg.Add(new Tuple<Topic, string>(t, string.Empty));
        } else if(sTag!=vt.Value) {
          rereg.Add(new Tuple<Topic,string>(t, vt.Value));
        }
      }
      if(rereg.Any()) {
        dev.ReReg(rereg);
      }
      _stackBottom = (c.StackBottom + 3) / 4;
      _prg = new SortedSet<Chunk>();
      foreach(var kv in c.Hex) {
        var ch = new Chunk((int)kv.Key) {
          Data = kv.Value
        };
        ch.crcCur = Crc16.UpdateCrc(0xFFFF, ch.Data);
        _prg.Add(ch);
        if(System.Threading.Interlocked.CompareExchange(ref _st, 1, 0) == 0) {
          _curChunk = ch;
        }
      }
    }
    #endregion RPC Members

    private void C_CMsg(NiL.JS.MessageLevel level, NiL.JS.Core.CodeCoordinates coords, string message) {
      switch(level) {
      case NiL.JS.MessageLevel.Error:
      case NiL.JS.MessageLevel.CriticalWarning:
        Log.Error("{0} [{1}, {2}] {3}", _owner.path, coords.Line, coords.Column, message);
        break;
      case NiL.JS.MessageLevel.Warning:
        Log.Warning("{0} [{1}, {2}] {3}", _owner.path, coords.Line, coords.Column, message);
        break;
      default:
        Log.Info("{0} [{1}, {2}] {3}", _owner.path, coords.Line, coords.Column, message);
        break;
      }

    }

    #region IMsExt Member
    public void Recv(byte[] msgData) {
      if(msgData == null || msgData.Length == 0) {
        return;
      }
      bool processed = false;
      if(msgData[0] == (byte)Cmd.Idle) {
        PlcStoped = false;
        processed = true;
      } else if(_st == 2 && msgData[0] == (byte)Cmd.GetCRCResp) {
        if(_curChunk != null && msgData.Length == 4 && msgData[1] == 0) {
          _curChunk.crcDev = (msgData[3] << 8) | msgData[2];
          if(_curChunk.crcDev == _curChunk.crcCur) {
            _curChunk = null;
            _st = 1;
          } else {
            _st = _plcStoped ? 5 : 3;
            if(Verbose) {
              Log.Info("{0}.crc differ 0x{1:X4}:{2:X4}  cur={3:X4}, dev={4:X4}", _owner.path, _curChunk.offset, _curChunk.Data.Length, _curChunk.crcCur, _curChunk.crcDev);
            }
          }
          processed = true;
        }
      } else if(msgData[0] == (byte)Cmd.PlcStopResp) {
        if(msgData[1] != 0) {
          if(msgData.Length == 18) {
            processed = true;
            PlcStoped = true;
            Log.Warning("{0}.PlcStop({1}) SP={2:X4}, *SP={3:X4}, SFP={4:X4}, PC={5:X4}", _owner, ((ErrorCode)msgData[1]).ToString(), BitConverter.ToUInt32(msgData, 2), BitConverter.ToInt32(msgData, 6), BitConverter.ToUInt32(msgData, 10), BitConverter.ToUInt32(msgData, 14));
          } else {
            processed = false;
          }
          _st = 0;
        } else {
          PlcStoped = true;
          _st = _st==4?(_curChunk == null ? 1 : 5):0;
          processed = true;
        }
      } else if(_st == 6 && msgData.Length == 2 && msgData[0] == (byte)Cmd.WriteBlockResp) {
        if(msgData[1] == 0) {  // success
          _offset += 32;
          if(_offset >= _curChunk.Data.Length) {
            _curChunk.crcDev = _curChunk.crcCur;
            _curChunk = null;
            _st = 1;
            _offset = 0;
          } else {
            _st = 5;
          }
          processed = true;
        }
      } else if(_st == 7 && msgData[0] == (byte)Cmd.WriteStackBottomResp) {
        _st = 8;
        processed = true;
      } else if(_st == 9 && msgData[0] == (byte)Cmd.PlcStartResp && msgData[1]==0) {
        _st = 0;
        PlcStoped = false;
        processed = true;
      }
      if(!processed) {
        if(Verbose) {
          Log.Warning("{0}.Recv({1}) {2}-{3}", _owner, BitConverter.ToString(msgData), ((Cmd)msgData[0]), msgData.Length > 1 ? ((ErrorCode)msgData[1]).ToString() : "empty");
        }
        _st = 0;
      }
    }
    public void SendAck(byte[] buf) {
    }
    public void Tick() {
      byte[] buf;

      if(_st == 0 || _st == 9) {
        return;
      } else {
        if(_prg == null) {
          _st = 0;
          return;
        }
        if(_curChunk == null && _st < 7) {
          _st = 1;
        }
        if(_st == 1) {
          if(_curChunk == null) {
            _curChunk = _prg.FirstOrDefault(z => z.crcCur != z.crcDev);
            if(_curChunk == null) {
              if(_plcStoped) {
                _st = 7;
                _pub(new byte[] { (byte)Cmd.WriteStackBottomReq, (byte)_stackBottom, (byte)(_stackBottom >> 8), (byte)(_stackBottom >> 16), (byte)(_stackBottom >> 24) });
              } else {
                _st = 0;
              }
              return;
            }
          }
          _pub(new byte[] { (byte)Cmd.GetCRCReq, (byte)_curChunk.offset, (byte)(_curChunk.offset >> 8), (byte)_curChunk.Data.Length, (byte)(_curChunk.Data.Length >> 8) });
          _st = 2;
        } else if(_st == 3) {
          _pub(new byte[] { (byte)Cmd.PlcStopReq });
          _st = 4;
        } else if(_st == 5) {
          int len = _curChunk.Data.Length - _offset; //-V3125
          if(len > 32) {
            len = 32;
          }
          buf = new byte[len + 5];
          int addr = _curChunk.offset + _offset;
          buf[0] = (byte)(Cmd.WriteBlockReq);
          buf[1] = (byte)addr;
          buf[2] = (byte)(addr >> 8);
          Buffer.BlockCopy(_curChunk.Data, _offset, buf, 3, len);
          ushort crc = Crc16.UpdateCrc(0xFFFF, buf.Skip(3).Take(len).ToArray());
          buf[len + 3] = (byte)crc;
          buf[len + 4] = (byte)(crc >> 8);
          _st = 6;
          _pub(buf);
          if(Verbose) {
            Log.Info("{0}.write 0x{1:X4} {2}", _owner.path, addr, BitConverter.ToString(buf, 3, len));
          }
        } else if(_st == 8) {
          _st = 9;
          _pub(new byte[] { (byte)Cmd.PlcStartReq });
        }
      }
    }
    #endregion IMsExt Member

    #region IDisposable Member
    public void Dispose() {
      _owner.SetState(0);
    }
    #endregion IDisposable Member

    public override string ToString() {
      return (_owner!=null?_owner.path:"unk") + "["+_idx.ToString()+"]";
    }
    private enum Cmd : byte {
      Idle = 0,
      PlcStartReq = 1,      // 1
      PlcStartResp = 2,     // 2, 0
      PlcStopReq = 3,       // 3
      PlcStopResp = 4,      // 4, 0
      GetCRCReq = 5,        // 5, addrL, addrH, lenL, lenH
      GetCRCResp = 6,       // 6, 0, crcL, crcH
      WriteBlockReq = 7,    // 7, addrL, addrH, [data(length = packet lenght-5)], crcL, crcH
      WriteBlockResp = 8,   // 8, 0
      EraseBlockReq = 9,    // 9, addrL, addrH, lenL, lenH
      EraseBlockResp = 10,  //10, 0
      WriteStackBottomReq = 11,   // 11, sizeL, size1, size2, sizeH
      WriteStackBottomResp = 12,  // 12, 0
    }
    private enum ErrorCode : byte {
      Success = 0x00,

      UnknownOperation = 0x80,
      Programm_OutOfRange = 0x81,
      Ram_OutOfRange = 0x82,
      TestError = 0x83,
      Watchdog = 0x84,
      SFP_OutOfRange = 0x85,
      DivByZero = 0x86,
      SP_OutOfRange = 0x87,
      API_Unknown = 0x88,
      ResourceBusy = 0x89,
      ArgumentOutOfRange = 0x8A,

      LPM_OutOfRange = 0x90,

      WrongState = 0xFA,
      CrcError = 0xFB,
      OutOfRange = 0xFC,
      FormatError = 0xFD,
      UnknowmCmd = 0xFE,
    }
    private class Chunk : IComparable<Chunk> {
      public int offset;
      public int crcDev;
      public int crcCur;
      public byte[] Data;

      public Chunk(int offset) {
        this.offset = offset;
        crcDev = -1;
      }
      public override string ToString() {
        return offset.ToString("X4") + "[" + (Data == null ? "null" : Data.Length.ToString("X4")) + (crcCur == crcDev ? " ok" : " !");
      }
      public int CompareTo(Chunk other) {
        if(other == null) {
          return 1;
        }
        return this.offset.CompareTo(other.offset);
      }
    }
  }
}
