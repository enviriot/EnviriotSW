///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.Periphery {
  internal class DevicePLC : IMsExt {
    private Topic _owner;
    private Action<byte[]> _pub;
    private Topic _verbose;
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

    private int _st;  // 0 - idle, 1 - check CRC, 2- check CRC resp, 3-PLC stop, 4 - PLC stop resp, 5- write block, 6- write block resp, 7- PLC start resp

    public DevicePLC(Topic owner, Action<byte[]> pub) {
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

    public bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }
    public string path { get { return _owner.path; } }
    public void StartPlc() {
      _st = 9;
      _pub(new byte[] { (byte)Cmd.PlcStartReq });
    }
    public void StopPlc() {
      _st = 4;
      _pub(new byte[] { (byte)Cmd.PlcStopReq });
    }

    #region IMsExt Member
    public void Recv(byte[] msgData) {
      if(msgData == null || msgData.Length == 0) {
        return;
      }
      bool processed = false;

      if(msgData[0] == (byte)Cmd.PlcStopResp) {
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
          _st = 1;
          processed = true;
        }
      } else if(_st == 9 && msgData[0] == (byte)Cmd.PlcStartResp && msgData[1]==0) {
        _st = 0;
        PlcStoped = false;
        processed = true;
      }
      if(!processed) {
        if(verbose) {
          Log.Warning("{0}.Recv({1}) {2}-{3}", _owner, BitConverter.ToString(msgData), ((Cmd)msgData[0]), msgData.Length > 1 ? ((ErrorCode)msgData[1]).ToString() : "empty");
        }
        _st = 0;
      }
    }
    #endregion IMsExt Member

    #region IDisposable Member
    public void Dispose() {
      _owner.SetState(0);
    }
    #endregion IDisposable Member

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

      LPM_OutOfRange = 0x90,

      WrongState = 0xFA,
      CrcError = 0xFB,
      OutOfRange = 0xFC,
      FormatError = 0xFD,
      UnknowmCmd = 0xFE,
    }
  }
}
