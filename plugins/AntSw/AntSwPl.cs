///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using System.Threading;
using System.IO.Ports;

namespace X13.Periphery {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 8)]
  [ExportMetadata("name", "AntSw")]
  public class AntSwPl : IPlugModul {
    private Topic _owner, _verbose, _di;
    private Transport _transport;
    private int _st;
    private byte[] _remoteSt, _rxCfg, _txCfg;
    private SubRec _reqSub;
    private Topic _enableT;
    private DateTime _to;

    #region IPlugModul Members
    public void Init() {
      _st = 0;
      _remoteSt = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 };
      _rxCfg = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };
      _txCfg = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    public void Start() {
      _owner = Topic.root.Get("/$YS/AntSw");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
      _enableT = _owner.Get("remote");
      if(_enableT.GetState().ValueType != JSC.JSValueType.Boolean) {
        _enableT.SetAttribute(Topic.Attribute.DB | Topic.Attribute.Required);
        _enableT.SetState(true);
      }

      var rt = Topic.root.Get("/export/req", true, _owner);
      Topic con;
      for(int i=1; i<=8; i++) {
        con = rt.Get("con"+i.ToString(), true, _owner);
        con.Get("ptt").SetState(JSC.JSObject.Null);
        con.Get("rxcfg").SetState(0);
        con.Get("txcfg").SetState(0);
      }
      _di = Topic.root.Get("/export/out", true, _owner);
      _reqSub = rt.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, Request);
      _transport = new Transport(this);
    }
    public void Stop() {
      _reqSub.Dispose();
      var tr = Interlocked.Exchange(ref _transport, null);
      if(tr!=null) {
        tr.Dispose();
      }
    }

    public void Tick() {
      if(!_transport.IsOpen) {
        return;
      }
      Command cmd;
      while((cmd = _transport.Read())!=null) {
        switch(cmd.code){
        case CommandCode.ExEvent:
          GetResponse(cmd);
          break;
        case CommandCode.Event:
          OnEvent(cmd);
          break;
        case CommandCode.Fail:
          OnFail(cmd);
          break;
        }
      }
      switch(_st) {
      case 0:
        _transport.Write(new Command(CommandCode.Get, 32, 128));  // Get Remote statuses
        _st = 1;
        _to = DateTime.Now.AddSeconds(6);
        break;
      case 2:
        _transport.Write(new Command(CommandCode.Get, 32, 129));  // Get Console statuses
        _st = 3;
        _to = DateTime.Now.AddSeconds(6);
        break;
      case 4:
        _transport.Write(new Command(CommandCode.Get, 32, 130));  // Get RxAntCfg
        _st = 5;
        _to = DateTime.Now.AddSeconds(6);
        break;
      case 6:
        _transport.Write(new Command(CommandCode.Get, 32, 131));  // Get TxAntCfg
        _st = 7;
        _to = DateTime.Now.AddSeconds(6);
        break;
      case 1:
      case 3:
      case 5:
      case 7:
        if(DateTime.Now > _to) {
          Log.Warning("AntSw Timeout. State = {0}", _st);
          _st = 0;
        }
        break;
      }
    }
    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/AntSw", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        var en = Topic.root.Get("/$YS/AntSw", true);
        en.SetState(value);
      }
    }
    #endregion IPlugModul Members

    private void GetResponse(Command cmd) {
      if(_st==1 && cmd.addr==32 && cmd.param==128 && cmd.data!=null && cmd.data.Length==8) {
        for(int i = 0; i<8; i++) {
          _di.Get("rem"+(i+1).ToString()+"/status", true, _owner).SetState(cmd.data[i]);
          _remoteSt[i] = (byte)cmd.data[i];
        }
        _st = 2;
      } else if(_st==3 && cmd.addr==32 && cmd.param==129 && cmd.data!=null && cmd.data.Length==8) {
        for(int i = 0; i<8; i++) {
          _di.Get("con"+(i+1).ToString()+"/status", true, _owner).SetState(cmd.data[i]);
        }
        _st = 4;
      } else if(_st==5 && cmd.addr==32 && cmd.param==130 && cmd.data!=null && cmd.data.Length==8) {
        for(int i = 0; i<8; i++) {
          _di.Get("con"+(i+1).ToString()+"/rxcfg", true, _owner).SetState(cmd.data[i]);
          _rxCfg[i] = (byte)cmd.data[i];
        }
        _st = 6;
      } else if(_st==7 && cmd.addr==32 && cmd.param==131 && cmd.data!=null && cmd.data.Length==8) {
        for(int i = 0; i<8; i++) {
          _di.Get("con"+(i+1).ToString()+"/txcfg", true, _owner).SetState(cmd.data[i]);
          _txCfg[i] = (byte)cmd.data[i];
        }
        _st = 8;
      }
    }
    private void OnEvent(Command cmd) {
      switch(cmd.addr) {
      case 0:
        for(byte i=1; i<=8; i++) {
          OnEventConsole(cmd, i);
        }
        break;
      case 1:
      case 2:
      case 3:
      case 4:
      case 5:
      case 6:
      case 7:
      case 8:
        OnEventConsole(cmd, cmd.addr);
        break;
      case 17:
      case 18:
      case 19:
      case 20:
      case 21:
      case 22:
      case 23:
      case 24:
        OnEventRemote(cmd);
        break;
      case 32:
        OnEventMain(cmd);
        break;
      }
    }
    private void OnEventMain(Command cmd) {
      switch(cmd.param) {
      case 2:  // Reset
        for(int i = 0; i < 8; i++) {
          _di.Get("rem"+(i+1).ToString()+"/status", true, _owner).SetState(255);
          _remoteSt[i] = 255;
          _di.Get("con"+(i+1).ToString()+"/status", true, _owner).SetState(0);
          _di.Get("con"+(i+1).ToString()+"/rxcfg", true, _owner).SetState(0);
          _rxCfg[i] = 0;
          _di.Get("con"+(i+1).ToString()+"/txcfg", true, _owner).SetState(0);
          _txCfg[i] = 0;
        }
        Log.Warning("AntSw.main reset");
        break;
      }
    }
    private void OnEventConsole(Command cmd, byte addr) {
      if(cmd.param>=64 && cmd.param<=95) {
        int rem = (cmd.param - 64) / 4;
        if((cmd.param & 3)==0) {
          if(_remoteSt[rem] == addr) {
            _di.Get("rem"+(rem+1).ToString()+"/status", true, _owner).SetState(0);
            _remoteSt[rem] = 0;
            _di.Get("con"+addr.ToString()+"/status", true, _owner).SetState(0);
            _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
            _rxCfg[addr-1] = 0;
            _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
            _txCfg[addr-1] = 0;
          }
        }else if((cmd.param & 3)==1) {
          _di.Get("rem"+(rem+1).ToString()+"/status", true, _owner).SetState(addr);
          _remoteSt[rem] = addr;
          _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(1);
          _rxCfg[addr-1] = 1;
          _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(1);
          _txCfg[addr-1] = 1;
        }
      } else if(cmd.param>=96 && cmd.param<=127) {
        int cfg = (cmd.param - 92) / 4;
        switch(cmd.param & 3){
        case 0: // off
          if(_rxCfg[addr-1] == cfg) {
            _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
            _rxCfg[addr-1] = 0;
          }
          if(_txCfg[addr-1] == cfg) {
            _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
            _txCfg[addr-1] = 0;
          }
          break;
        case 1: // green
          _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(cfg);
          _rxCfg[addr-1] = (byte)cfg;
          if(_txCfg[addr-1] == cfg) {
            _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
            _txCfg[addr-1] = 0;
          }
          break;
        case 2: // red
          if(_rxCfg[addr-1] == cfg) {
            _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
            _rxCfg[addr-1] = 0;
          }
          _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(cfg);
          _txCfg[addr-1] = (byte)cfg;
          break;
        case 3: // yellow
          _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(cfg);
          _rxCfg[addr-1] = (byte)cfg;
          _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(cfg);
          _txCfg[addr-1] = (byte)cfg;
          break;
        }
      } else {
        switch(cmd.param) {
        case 5: // Device Online
          _di.Get("con"+(addr).ToString()+"/status", true, _owner).SetState(0);
          _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
          _rxCfg[addr-1] = 0;
          _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
          _txCfg[addr-1] = 0;
          Log.Warning("AntSw.con" + addr.ToString()+"/status = 0");
          break;
        }
      }
    }
    private void OnEventRemote(Command cmd) {
      byte rem = (byte)(cmd.addr-17);
      byte con = _remoteSt[rem];
      switch(cmd.param) {
      case 3: // Ptt Off
        _di.Get("con"+con.ToString()+"/status", true, _owner).SetState(2);
        break;
      case 4: // Ptt On
        _di.Get("con"+con.ToString()+"/status", true, _owner).SetState(3);
        break;
      case 5: // Device Online
        _di.Get("rem"+(rem+1).ToString()+"/status", true, _owner).SetState(0);
        Log.Warning("AntSw.rem" + (rem+1).ToString()+"/status = 0");
        break;
      }
    }
    private void OnFail(Command cmd) {
      int addr;
      switch(cmd.addr) {
      case 1:
      case 2:
      case 3:
      case 4:
      case 5:
      case 6:
      case 7:
      case 8:
        switch(cmd.param) {
        case 41:
          addr = cmd.addr-1;
          _di.Get("con"+(addr+1).ToString()+"/status", true, _owner).SetState(0);
          _di.Get("con"+(addr+1).ToString()+"/rxcfg", true, _owner).SetState(0);
          _rxCfg[addr] = 0;
          _di.Get("con"+(addr+1).ToString()+"/txcfg", true, _owner).SetState(0);
          _txCfg[addr] = 0;
          break;
        }
        break;
      case 17:
      case 18:
      case 19:
      case 20:
      case 21:
      case 22:
      case 23:
      case 24:
        switch(cmd.param) {
        case 41:
          addr = cmd.addr - 17;
          if(_remoteSt[addr]>=1 && _remoteSt[addr]<=8) {
            int con = _remoteSt[addr]-1;
            _di.Get("con"+(con+1).ToString()+"/status", true, _owner).SetState(0);
            _di.Get("con"+(con+1).ToString()+"/rxcfg", true, _owner).SetState(0);
            _rxCfg[con] = 0;
            _di.Get("con"+(con+1).ToString()+"/txcfg", true, _owner).SetState(0);
            _txCfg[con] = 0;
          }
          _di.Get("rem"+(addr+1).ToString()+"/status", true, _owner).SetState(255);
          _remoteSt[addr] = 255;
          break;
        case 48:
        case 49:
        case 50:
        case 51:
          addr = cmd.addr - 17;
          if(_remoteSt[addr]>=1 && _remoteSt[addr]<=8) {
            int con = _remoteSt[addr]-1;
            _di.Get("con"+(con+1).ToString()+"/status", true, _owner).SetState(0);
            _di.Get("con"+(con+1).ToString()+"/rxcfg", true, _owner).SetState(0);
            _rxCfg[con] = 0;
            _di.Get("con"+(con+1).ToString()+"/txcfg", true, _owner).SetState(0);
            _txCfg[con] = 0;
          }
          _di.Get("rem"+(addr+1).ToString()+"/status", true, _owner).SetState(0);
          _remoteSt[addr] = 0;
          break;
        }
        break;
      }
    }

    private void Request(Perform p, SubRec sr) {
      byte con;
      int tmp;
      var en = _enableT.GetState();
      if((en.ValueType == JSC.JSValueType.Boolean && !((bool)en)) || p.prim==_owner || p.src.path.Length < 17 || !p.src.path.StartsWith("/export/req/con") || !byte.TryParse(p.src.path.Substring(15, 1), out con) || con==0 || con > 8) {
        return;
      }
      switch(p.src.name) {
      case "ptt":
        if(p.src.GetState().ValueType==JSC.JSValueType.Boolean) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)(((bool)p.src.GetState())?4:3)));  
        }
        p.src.SetState(JSC.JSObject.Null, _owner);
        break;
      case "band":
        if(p.src.GetState().IsNumber && (tmp = (int)p.src.GetState())>0 && tmp <= 8) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)((tmp-1)*2 + 64)));  
        }
        p.src.SetState(0, _owner);
        break;
      case "rxcfg":
        if(p.src.GetState().IsNumber && (tmp = (int)p.src.GetState())>0 && tmp <= 8) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)((tmp-1)*2 + 96)));  
        }
        p.src.SetState(0, _owner);
        break;
      case "txcfg":
        if(p.src.GetState().IsNumber && (tmp = (int)p.src.GetState())>0 && tmp <= 8) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)((tmp-1)*2 + 97)));  
        }
        p.src.SetState(0, _owner);
        break;
      }
    }

    public Topic Owner { get { return _owner; } }
    public bool Verbose { get { return _verbose != null && (bool)_verbose.GetState(); } }
  }
}
