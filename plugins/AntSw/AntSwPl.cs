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
    // Startup bulk-poll sequence - every BUS_LOCRQ_* array param the driver
    // consumes (see ApplyLocRq). Firmware answers Get(32, param) with the
    // full 8-element array (parser.c's ParserGet), same as the composite
    // ExEv push used for live updates - so both paths share ApplyLocRq.
    private static readonly byte[] _pollSeq = { 128, 129, 130, 131, 132, 133, 134, 135, 136 };
    private int _pollIdx;
    private byte[] _remoteSt, _remoteStAux, _rxCfg, _txCfg;
    private SubRec _reqSub;
    private DateTime _to;
    // The one and only representation of "who may drive the switch right now",
    // 0=Lock / 1=Local / 2=Remote — same value, same encoding as SW/Server's
    // asw_state_t.access (see its state.h) and as the wire topic below, so
    // there is nothing to keep in sync. _access is just the cached number;
    // _accessT is the topic it is published on and written from.
    // Replaced an earlier arrangement of three separate flags
    // (/$YS/AntSw/remote + /local/asw/remote_enable + the tri-state), which
    // was the same fact stored three times.
    private Topic _accessT;
    private int _access;
    private SubRec _accessSub;
    // Remote-node EEPROM config editor (web_loc/SetupRemote.html). Requests arrive
    // per WS session on /$YS/AntSw/cfg/req/<sid> and are answered on
    // .../rsp/<sid> — see CfgRemote and ApiV04's "G" handler. That subtree is
    // under /$YS/, so it is not under /export, /Public or /local and therefore
    // can never be reached from a browser directly, whatever the filters say.
    private CfgRemote _cfg;
    private Topic _cfgReqT, _cfgRspT;
    private SubRec _cfgReqSub;

    #region IPlugModul Members
    public void Init() {
      _st = 0;
      _pollIdx = 0;
      _remoteSt = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 };
      _remoteStAux = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 };
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
      var rt = Topic.root.Get("/export/req", true, _owner);
      rt.SetState(0);
      Topic con;
      for(int i=1; i<=8; i++) {
        con = rt.Get("con"+i.ToString(), true, _owner);
        con.SetState(0);
        con.Get("ptt").SetState(JSC.JSObject.Null);
        con.Get("rxcfg").SetState(0);
        con.Get("txcfg").SetState(0);
      }
      _di = Topic.root.Get("/export/out", true, _owner);
      // Pre-create every output topic here, before a browser is typically
      // subscribed. Topic.Get() fires a Perform.E_Art.create the first time a
      // topic is created, and ApiV04.SubChanged() forwards *every* Perform to
      // subscribers - including that create, whose GetState() is still
      // unset at that point. That serializes to an empty value ("P\t<path>\t"
      // with nothing after the last tab), which the browser's JSON.parse()
      // throws on. This bites intermediate container topics too: Get("con1/
      // status") auto-creates "con1" along the way, but only "status" ever
      // gets SetState() - "con1" itself is left state-less forever unless we
      // explicitly set it too. So every node on the path needs a value here,
      // not just the leaves.
      _di.SetState(0);
      Topic coni, remi;
      for(int i=1; i<=8; i++) {
        coni = _di.Get("con"+i.ToString(), true, _owner);
        coni.SetState(0);
        coni.Get("status", true, _owner).SetState(0);
        coni.Get("sel", true, _owner).SetState(0);
        coni.Get("slot", true, _owner).SetState(0);
        coni.Get("rxcfg", true, _owner).SetState(0);
        coni.Get("txcfg", true, _owner).SetState(0);
        remi = _di.Get("rem"+i.ToString(), true, _owner);
        remi.SetState(0);
        remi.Get("status", true, _owner).SetState(0);
        remi.Get("pwrFwd", true, _owner).SetState(0);
        remi.Get("pwrRev", true, _owner).SetState(0);
      }
      // LAN CIDR for /local/* WS access — same create-if-missing/DB-persisted
      // pattern Transport.cs already uses for "port". ApiV04.CheckAccess reads
      // it via /local's own "WebUI.Filter" field, which is where it looks up
      // any top-level topic's access filter.
      // Counterpart of SW/Server's gateway_config.json "trusted_nets", but a
      // SINGLE CIDR, not a comma-separated list: WebUI.Filter is parsed by
      // CheckAccess as one "a.b.c.d/bits" and teaching it a list is a change
      // to shared Enviriot code, not to this plugin. Deliberate known gap.
      var cidrT = _owner.Get("lan_cidr", true, _owner);
      string cidr;
      if(cidrT.GetState().ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(cidr = cidrT.GetState().Value as string)) {
        cidrT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
        cidr = "192.168.0.0/16";
        cidrT.SetState(cidr, _owner);
      }
      var localT = Topic.root.Get("/local", true, _owner);
      localT.SetField("WebUI.Filter", cidr, _owner);
      localT.SetState(0);
      var localAswT = localT.Get("asw", true, _owner);
      localAswT.SetState(0);
      _accessT = localAswT.Get("access", true, _owner);
      if(!_accessT.GetState().IsNumber) {
        // Boot default 1 (Local): usable from the LAN, closed to remote.
        // DB-persisted from here on, same as "port"/"lan_cidr" above — an
        // operator's chosen state (e.g. 2/Remote) survives a restart instead
        // of silently resetting to 1 every time. This is a deliberate
        // difference from SW/Server, which always boots from its config file;
        // see SW/Server/README.md.
        _accessT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
        _accessT.SetState(1, _owner);
      }
      _access = _accessT.GetState().IsNumber ? (int)_accessT.GetState() : 1;
      if(_access < 0 || _access > 2) {
        _access = 1;
        _accessT.SetState(_access, _owner);
      }
      // Once|Value, not Value alone: Topic.Publish only ever calls a subscriber
      // whose mask carries Once or All (Topic.cs, the OnceOrAll test) — "Once"
      // meaning "scope is this topic itself", as opposed to Chldren/All, NOT
      // "deliver a single time". With Value alone the SubRec is registered but
      // never invoked, which silently left _access frozen at its boot value.
      // Same combination ApiV04 uses for an exact-path subscribe.
      _accessSub = _accessT.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, AccessChanged);

      var cfgT = _owner.Get("cfg", true, _owner);
      cfgT.SetState(0);
      _cfgReqT = cfgT.Get("req", true, _owner);
      _cfgReqT.SetState(0);
      _cfgRspT = cfgT.Get("rsp", true, _owner);
      _cfgRspT.SetState(0);
      _reqSub = rt.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, Request);
      _transport = new Transport(this);
      _cfg = new CfgRemote(_transport, CfgReply);
      // Subscribed last, after _cfg exists — CfgRequested calls into it, and
      // the subscribe itself can deliver immediately.
      // All|Value: one child per WS session, created by ApiV04 as sessions
      // come and go, so this has to cover the subtree rather than one topic.
      // Per-session and not one shared request topic on purpose: Repo's
      // EnquePerf collapses several setState performs for the same topic
      // inside one tick, which would silently swallow one of two concurrent
      // requests.
      _cfgReqSub = _cfgReqT.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, CfgRequested);
    }

    private void CfgRequested(Perform p, SubRec sr) {
      if(_cfg == null || p.Art != Perform.E_Art.changedState || p.Prim == _owner || p.src == _cfgReqT) {
        return;
      }
      var v = p.src.GetState();
      if(v.ValueType != JSC.JSValueType.String) {
        return;
      }
      // Topic name is the session id; ApiV04 listens on the matching rsp child.
      _cfg.Submit(p.src.name, v.Value as string);
    }

    private void CfgReply(string session, string payload) {
      _cfgRspT.Get(session, true, _owner).SetState(payload, _owner);
    }

    public void Stop() {
      _reqSub.Dispose();
      _accessSub.Dispose();
      _cfgReqSub.Dispose();
      var tr = Interlocked.Exchange(ref _transport, null);
      if(tr!=null) {
        tr.Dispose();
      }
    }

    // /local/asw/access, 0/1/2 — see state.h in SW/Server for the exact same
    // three states. Request() below gates on _access, ApiV04 reads the topic
    // for its /export/req write ACL; nothing else derives from this.
    private void AccessChanged(Perform p, SubRec sr) {
      // changedState only: a Once subscribe also replays the current value once
      // at Subscribe() time (Art == subscribe), and Start() has already read
      // _access straight from the topic by then — acting on that replay would
      // just re-publish the same value for nobody.
      // Prim == _owner is our own re-assert below; ignoring it is what keeps
      // this from looping.
      if(p.Art != Perform.E_Art.changedState || p.Prim == _owner) {
        return;
      }
      var v = p.src.GetState();
      int n = v.IsNumber ? (int)v : -1;
      if(n < 0 || n > 2) {
        // Garbage from a client. asw_core just drops such a CMD and stays
        // authoritative; do the same here — but the topic itself already holds
        // the bad value at this point, so put the real state back into it.
        // This write always differs from what's stored, so it always
        // propagates; the client sees its bad value snap back.
        _accessT.SetState(_access, _owner);
        return;
      }
      _access = n;
      // No write-back on the normal path: the client that wrote this gets its
      // confirmation from ApiV04.SubChanged, which does not suppress self-echo
      // under /local/ (see the comment there). Re-asserting the same value
      // here instead would have depended on Repo's setState dedup — which
      // compares JSValues by REFERENCE, so whether a re-assert of an identical
      // small int propagates at all is a NiL.JS implementation detail. Not
      // something to hang the UI on.
    }

    public void Tick() {
      // _cfg is built at the very end of Start(), just after _transport — the
      // host's timer can fire in between, so don't assume it exists yet.
      if(_cfg == null || !_transport.IsOpen) {
        return;
      }
      Command cmd;
      while((cmd = _transport.Read())!=null) {
        // Config request/response first: it only ever reacts to a frame from
        // the node it is currently waiting on, so this is a no-op the rest of
        // the time and never competes with the handlers below for a frame.
        _cfg.OnCommand(cmd);
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
      // Runs alongside the startup poll rather than after it — see CfgRemote.Tick
      // for why waiting for the poll would be the wrong kind of careful.
      _cfg.Tick();
      if(_pollIdx >= _pollSeq.Length) {
        return;
      }
      switch(_st) {
      case 0:
        _transport.Write(new Command(CommandCode.Get, 32, _pollSeq[_pollIdx]));
        _st = 1;
        _to = DateTime.Now.AddSeconds(6);
        break;
      case 1:
        if(DateTime.Now > _to) {
          Log.Warning("AntSw Timeout. Param = {0}", _pollSeq[_pollIdx]);
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

    // rem{N}/status is a simplified 0=off/1=available/2=occupied summary - the
    // web has no use for exactly which console holds the band (see _remoteSt/
    // _remoteStAux, kept internally for the rxcfg/txcfg owner redirect), and a
    // raw Main-only address made an Aux-only occupancy read back as "available".
    private void PublishRemStatus(int idx) {
      byte main = _remoteSt[idx];
      byte aux = _remoteStAux[idx];
      int state;
      if(main==255) {
        state = 0;
      } else if((main>=1 && main<=8) || (aux>=1 && aux<=8)) {
        state = 2;
      } else {
        state = 1;
      }
      _di.Get("rem"+(idx+1).ToString()+"/status", true, _owner).SetState(state);
    }

    private void GetResponse(Command cmd) {
      if(_st==1 && cmd.addr==32 && _pollIdx<_pollSeq.Length && cmd.param==_pollSeq[_pollIdx] && cmd.data!=null && cmd.data.Length==8) {
        // Bulk Get(32, param) response, requested by Tick()'s startup poll -
        // firmware answers with the full 8-element array (parser.c's
        // ParserGet), one element per console/remote index. Apply each
        // element through the same path a live incremental push would take.
        byte sidx = (byte)cmd.param;
        for(int i = 0; i<8; i++) {
          ApplyLocRq(sidx, i, cmd.data[i]);
        }
        _pollIdx++;
        _st = 0;
      } else if(cmd.addr==32 && cmd.data!=null && cmd.data.Length==1 && (cmd.param >> 8)>=1 && (cmd.param >> 8)<=8) {
        // Incremental push from device's cons_bin_refresh():
        //   ExEv = <BUS_LOCRQ_* base> | ((idx+1) << 8), single data value.
        // Arrives asynchronously whenever a single slot's state changes, so
        // it is not gated on _st/_pollIdx.
        int idx = (cmd.param >> 8) - 1;
        ApplyLocRq((byte)(cmd.param & 0xFF), idx, cmd.data[0]);
      }
    }

    // Applies one BUS_LOCRQ_* element (sidx = param & 0xFF, idx = console/
    // remote index 0-7) to the topic tree. Shared by the startup bulk poll
    // (one call per array element) and the live incremental push (one call
    // per event) - both carry exactly the same (sidx, idx, val) shape.
    private void ApplyLocRq(byte sidx, int idx, ushort val) {
      switch(sidx) {
      case 128:  // BUS_LOCRQ_REMOTE_STAT
        {
          byte prevOwner = _remoteSt[idx];
          _remoteSt[idx] = (byte)val;
          PublishRemStatus(idx);
          if(val>=1 && val<=8 && prevOwner!=val) {
            // Freshly acquired by this console (device defaults the Main slot's
            // antenna to index 0 in AcquireSlot() - no need to wait for a device
            // confirmation, the driver already knows the protocol default).
            _di.Get("con"+val.ToString()+"/rxcfg", true, _owner).SetState(1);
            _rxCfg[val-1] = 1;
            _di.Get("con"+val.ToString()+"/txcfg", true, _owner).SetState(1);
            _txCfg[val-1] = 1;
          }
        }
        break;
      case 129:  // BUS_LOCRQ_CONSOLE_ISTAT
        _di.Get("con"+(idx+1).ToString()+"/status", true, _owner).SetState(val);
        break;
      case 130:  // BUS_LOCRQ_RX_ANT_CFG - indexed by Remote, redirect to owning console (Main)
        {
          byte owner = _remoteSt[idx];
          if(owner>=1 && owner<=8) {
            _di.Get("con"+owner.ToString()+"/rxcfg", true, _owner).SetState(val+1);
            _rxCfg[owner-1] = (byte)(val+1);
          }
        }
        break;
      case 131:  // BUS_LOCRQ_TX_ANT_CFG - indexed by Remote, redirect to owning console (Main)
        {
          byte owner = _remoteSt[idx];
          if(owner>=1 && owner<=8) {
            _di.Get("con"+owner.ToString()+"/txcfg", true, _owner).SetState(val+1);
            _txCfg[owner-1] = (byte)(val+1);
          }
        }
        break;
      case 132:  // BUS_LOCRQ_CONSOLE_STATE (Main-selected Remote)
        _di.Get("con"+(idx+1).ToString()+"/sel", true, _owner).SetState(val);
        break;
      case 133:  // BUS_LOCRQ_AUX_REMOTE_STAT - Aux owner, no address published on its
                 // own (see PublishRemStatus), but it can flip rem{N}/status between
                 // available/occupied even though the Main owner didn't change.
        {
          byte prevAuxOwner = _remoteStAux[idx];
          _remoteStAux[idx] = (byte)val;
          PublishRemStatus(idx);
          if(val>=1 && val<=8 && prevAuxOwner!=val) {
            // Freshly acquired Aux slot - same protocol default as Main, see case 128.
            _di.Get("con"+val.ToString()+"/rxcfg", true, _owner).SetState(1);
            _rxCfg[val-1] = 1;
            _di.Get("con"+val.ToString()+"/txcfg", true, _owner).SetState(1);
            _txCfg[val-1] = 1;
          }
        }
        break;
      case 134:  // BUS_LOCRQ_AUX_RX_ANT_CFG - indexed by Remote, redirect to owning console
        {
          byte owner = _remoteStAux[idx];
          if(owner>=1 && owner<=8) {
            _di.Get("con"+owner.ToString()+"/rxcfg", true, _owner).SetState(val+1);
            _rxCfg[owner-1] = (byte)(val+1);
          }
        }
        break;
      case 135:  // BUS_LOCRQ_AUX_TX_ANT_CFG - indexed by Remote, redirect to owning console
        {
          byte owner = _remoteStAux[idx];
          if(owner>=1 && owner<=8) {
            _di.Get("con"+owner.ToString()+"/txcfg", true, _owner).SetState(val+1);
            _txCfg[owner-1] = (byte)(val+1);
          }
        }
        break;
      case 136:  // BUS_LOCRQ_CONSOLE_SLOT
        _di.Get("con"+(idx+1).ToString()+"/slot", true, _owner).SetState(val);
        break;
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
      case 2:  // Reset - mainboard reboot: everything reverts to parser_init() defaults
        for(int i = 0; i < 8; i++) {
          _remoteSt[i] = 255;
          _remoteStAux[i] = 255;
          PublishRemStatus(i);
          // parser_init()'s default for this table is PLS_OFFLINE, not IDLE
          // (parser.c:806) - after a reboot the mainboard has heard from
          // nobody, and 0 would claim all eight consoles are present.
          _di.Get("con"+(i+1).ToString()+"/status", true, _owner).SetState(255);
          _di.Get("con"+(i+1).ToString()+"/sel", true, _owner).SetState(0);
          _di.Get("con"+(i+1).ToString()+"/slot", true, _owner).SetState(0);
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
            _remoteSt[rem] = 0;
            PublishRemStatus(rem);
            _di.Get("con"+addr.ToString()+"/status", true, _owner).SetState(0);
            _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
            _rxCfg[addr-1] = 0;
            _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
            _txCfg[addr-1] = 0;
          }
        }else if((cmd.param & 3)==1) {
          _remoteSt[rem] = addr;
          PublishRemStatus(rem);
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
        case 2: // Reset - ClearConsoleState() on the device: ConSel/ConSlot/ConStat -> defaults.
                // ClearSlot() also zeroes AntCfg[].oRxCfg/oTxCfg for the released band, but
                // never pushes that via cons_bin_refresh (calls are commented out in parser.c),
                // so rxcfg/txcfg have to be cleared here too, same as case 5 below.
          _di.Get("con"+addr.ToString()+"/status", true, _owner).SetState(0);
          _di.Get("con"+addr.ToString()+"/sel", true, _owner).SetState(0);
          _di.Get("con"+addr.ToString()+"/slot", true, _owner).SetState(0);
          _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
          _rxCfg[addr-1] = 0;
          _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
          _txCfg[addr-1] = 0;
          Log.Warning("AntSw.con" + addr.ToString()+" reset");
          break;
        case 3: // Ptt Off (console released PTT, TX -> RX) - cons_bin_log(5, cAddr, PTT_MAIN_OFF), bus 0 in parser.c
          _di.Get("con"+addr.ToString()+"/status", true, _owner).SetState(2);
          // The remote only reports FWD/REV power while it's actually
          // transmitting - it never sends an explicit "power = 0" on PTT
          // release, so the last (often near-max) reading would otherwise
          // stick in the topic forever. Reset it here for whichever remote
          // this console currently holds (Main/Ext or Aux).
          for(int i = 0; i < 8; i++) {
            if(_remoteSt[i]==addr || _remoteStAux[i]==addr) {
              _di.Get("rem"+(i+1).ToString()+"/pwrFwd", true, _owner).SetState(0);
              _di.Get("rem"+(i+1).ToString()+"/pwrRev", true, _owner).SetState(0);
            }
          }
          break;
        case 4: // Ptt On (remote confirmed TX active) - cons_bin_log(5, adr, PTT_MAIN_ON), bus 1 in parser.c
          _di.Get("con"+addr.ToString()+"/status", true, _owner).SetState(3);
          break;
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
      if(cmd.param>=16 && cmd.param<=23) {  // BUS_EV_PWR_FWD_BASE..MAX - forward power, level 0-7
        _di.Get("rem"+(rem+1).ToString()+"/pwrFwd", true, _owner).SetState(cmd.param-16);
        return;
      }
      if(cmd.param>=24 && cmd.param<=31) {  // BUS_EV_PWR_REV_BASE..MAX - reflected power, level 0-7
        _di.Get("rem"+(rem+1).ToString()+"/pwrRev", true, _owner).SetState(cmd.param-24);
        return;
      }
      switch(cmd.param) {
      case 2: // Reset - this remote (re)came online: RemMain/RemAux -> ADDR_NONE
        _remoteSt[rem] = 0;
        _remoteStAux[rem] = 0;
        PublishRemStatus(rem);
        Log.Warning("AntSw.rem" + (rem+1).ToString()+" reset");
        break;
      case 3: // Ptt Off
        _di.Get("con"+con.ToString()+"/status", true, _owner).SetState(2);
        break;
      case 4: // Ptt On
        _di.Get("con"+con.ToString()+"/status", true, _owner).SetState(3);
        break;
      case 5: // Device Online
        _remoteSt[rem] = 0;
        PublishRemStatus(rem);
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
          // "Node has gone offline", so the status must say OFFLINE (PLS_t
          // 0xFF), not IDLE: 0 is a console that is present and doing nothing.
          // The firmware sets ConStat[ci] = PLS_OFFLINE on this same event
          // (FW/Mainboard/Source/PARSER/parser.c:1064), and the startup poll of
          // 129 reports 0xFF for an absent console. The remote branch below has
          // always stored 255 for the same event.
          addr = cmd.addr-1;
          _di.Get("con"+(addr+1).ToString()+"/status", true, _owner).SetState(255);
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
          _remoteSt[addr] = 255;
          _remoteStAux[addr] = 255;
          PublishRemStatus(addr);
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
          _remoteSt[addr] = 0;
          _remoteStAux[addr] = 0;
          PublishRemStatus(addr);
          break;
        }
        break;
      }
    }

    private void Request(Perform p, SubRec sr) {
      byte con;
      int tmp;
      // _access == 0 (Lock) means no request is acted on at all, whoever sent
      // it — the LAN/remote distinction is ApiV04's job, not this one's.
      if(_access == 0 || p.Prim==_owner || p.src.path.Length < 17 || !p.src.path.StartsWith("/export/req/con") || !byte.TryParse(p.src.path.Substring(15, 1), out con) || con==0 || con > 8) {
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
