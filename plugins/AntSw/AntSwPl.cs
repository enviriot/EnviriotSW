///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Globalization;
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
    private const string OWNER_PATH = "/$YS/AntSw";
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
    // Remote-node EEPROM config editor (web_loc/Setup.html). One request, one
    // answer - which the dashboard protocol expresses directly: the page sends
    // "A <cid> /local/asw/cfg AntSw.CfgRemote {line}" and the answer comes back
    // against the same cid.
    //
    // This replaces a pair of topics per WS session under /$YS/AntSw/cfg/
    // {req,rsp}/<sid>. That existed only because the browser API of the time
    // had nowhere else to put the correlation: a topic tree is pub/sub, so
    // "whose answer is this" had to be encoded in the path, one child per
    // session, created and dropped as sessions came and went. The cid carries
    // the correlation itself now, so the topics went with it.
    private const string CFG_ACTION = "AntSw.CfgRemote";
    private CfgRemote _cfg;
    private Topic _cfgT;
    private bool _cfgRpcRegistered;
    private int _cfgSeq;
    // Outstanding RPC calls by the session id handed to CfgRemote, holding the
    // line so the reply can be trimmed back to just the result. CfgRemote
    // answers exactly once per Submit - on a parse error, on a timeout, or on
    // completion - which is what RPC.Register's async form requires.
    private readonly Dictionary<string, KeyValuePair<string, Action<JSC.JSValue>>> _cfgRpc
      = new Dictionary<string, KeyValuePair<string, Action<JSC.JSValue>>>();

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
      _owner = Topic.root.Get(OWNER_PATH);
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
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
      // Pre-create every output topic here, before anything subscribes.
      // Topic.Get() fires an EventKind.Created the first time a topic is
      // created, and a subscriber sees that create with GetState() still
      // unset. Whoever serializes it then has a value-less node on its hands -
      // the browser API of the time turned it into an empty "P\t<path>\t" that
      // JSON.parse() threw on. The browser reaches these topics through
      // asw_ws_gateway now, but the hazard belongs to the create event rather
      // than to any one consumer, so the pre-creation stays. This bites
      // intermediate container topics too:
      // Get("con1/status") auto-creates "con1" along the way, but only "status" ever
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
      // No access declaration is written from here. Which networks may read or
      // write these trees over /api/dashboard is stated in the topics' own
      // manifests (dashboard.netRO / dashboard.netRW, offered by the Inspector
      // as DashboardRO / DashboardRW) and is an operator setting: writing it
      // from Start() would put it back to whatever this code said on every
      // restart, and silently undo an edit made in the IDE. The tri-state is
      // unaffected either way - it gates writes at runtime inside Request().
      var localT = Topic.root.Get("/local", true, _owner);
      localT.SetState(0);
      var localAswT = localT.Get("asw", true, _owner);
      localAswT.SetState(0);
      _accessT = localAswT.Get("access", true, _owner);
      if(!_accessT.GetState().IsNumber) {
        // Boot default 1 (Local): usable from the LAN, closed to remote.
        // DB-persisted from here on, same as Transport's "port" — an
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
      // Once|Value, not Value alone: publication hands the event to the topic
      // itself under OnceOrAll, to its parent under Children|All and to further
      // ancestors under All (Topic.cs, Deliver) — so "Once" means "scope is
      // this topic itself", as opposed to Children/All, NOT "deliver a single
      // time". With Value alone the SubRec is registered but never invoked,
      // which silently left _access frozen at its boot value. The rewrite kept
      // this rule; only the code expressing it moved.
      _accessSub = _accessT.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, AccessChanged);

      // The topic the configuration action hangs off. Under /local because the
      // page that calls it, Setup.html, is the LAN-only build - but which
      // networks actually reach it is decided by that subtree's own
      // dashboard.netRW declaration, not from here.
      _cfgT = localAswT.Get("cfg", true, _owner);
      if(!_cfgT.CheckAttribute(Topic.Attribute.Required)) {
        // Declared once, not on every start: the descriptor is ours to publish,
        // but an operator who edited the label should keep it. Same
        // create-if-missing shape MQTT_SN uses for its own actions.
        var act = new JSL.Array(1);
        var a0 = JSC.JSObject.CreateObject();
        a0["name"] = CFG_ACTION;
        a0["text"] = "Remote node configuration";
        act[0] = a0;
        _cfgT.SetField("Action", act, _owner);
        _cfgT.SetState(0, _owner);
        // DB, like /local/asw/access next to it: without it neither the topic
        // nor the descriptor survives a restart, the guard above would be false
        // every time, and an edited label would be overwritten on every start.
        _cfgT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
      }
      if(!_cfgRpcRegistered) {
        // RPC.Register throws on a duplicate name and there is no way to undo
        // it, so this is guarded rather than left to the assumption that Start()
        // runs once per process.
        RPC.Register(CFG_ACTION, CfgRpc);
        _cfgRpcRegistered = true;
      }
      _reqSub = rt.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, Request);
      _transport = new Transport(this);
      _cfg = new CfgRemote(_transport, CfgReply);
    }

    /// <summary>One CFGGET/CFGSET line in, one answer out.</summary>
    /// <remarks>The argument is an object with one property, "line", holding
    /// the tab-separated command - the same text asw_ws_gateway passes to
    /// asw_core over IPC, so one cfglink.js serves both backends. It travels
    /// inside JSON rather than as a frame field of its own precisely because
    /// it contains tabs, which the frame itself is split on; JSON escapes them.
    /// <para>The answer is the result verbatim - "OK" and the values, or "ERR"
    /// and a reason - which PendingRpc turns into ok:true with that string as
    /// its data. An ERR is deliberately not reported as ok:false: it is the
    /// device's answer, not a failure of the call, and the pages have always
    /// parsed OK/ERR themselves.</para></remarks>
    private void CfgRpc(Topic t, JSC.JSValue arg, Action<JSC.JSValue> reply) {
      var cfg = _cfg;
      if(cfg == null) {
        reply(Err("offline", "The switch transport is not running"));
        return;
      }
      string line = arg == null ? null : arg.AsString("line", null);
      if(string.IsNullOrEmpty(line)) {
        reply(Err("bad_args", "No command line was supplied"));
        return;
      }
      string session;
      lock(_cfgRpc) {
        session = "rpc" + (++_cfgSeq).ToString(CultureInfo.InvariantCulture);
        _cfgRpc[session] = new KeyValuePair<string, Action<JSC.JSValue>>(line, reply);
      }
      cfg.Submit(session, line);
    }

    private static JSC.JSValue Err(string code, string message) {
      var o = JSC.JSObject.CreateObject();
      o["error"] = code;
      o["message"] = message;
      return o;
    }

    private void CfgReply(string session, string payload) {
      KeyValuePair<string, Action<JSC.JSValue>> pending;
      bool found;
      lock(_cfgRpc) {
        found = _cfgRpc.TryGetValue(session, out pending);
        if(found) _cfgRpc.Remove(session);
      }
      if(!found) {
        // A late answer after Stop() cleared the table, or a session this
        // plugin never handed out. Nowhere to send it, and no reason to log an
        // operator-visible error for something only shutdown produces.
        return;
      }
      // CfgRemote echoes the request line back ahead of the result, which is
      // what the old G/R pair correlated on. The cid does that now, so the echo
      // is trimmed and the caller gets the result alone.
      string line = pending.Key;
      string result = payload != null && line != null
                      && payload.Length > line.Length && payload.StartsWith(line, StringComparison.Ordinal)
                      ? payload.Substring(line.Length + 1)
                      : payload;
      pending.Value(result);
    }

    public void Stop() {
      _reqSub.Dispose();
      _accessSub.Dispose();
      // Answer whatever is still in flight. PendingRpc would eventually time
      // these out on its own, but that backstop is 30 seconds and says only
      // "did not answer"; a page waiting on a switch that has just been shut
      // down should hear so at once.
      KeyValuePair<string, Action<JSC.JSValue>>[] inFlight;
      lock(_cfgRpc) {
        inFlight = _cfgRpc.Values.ToArray();
        _cfgRpc.Clear();
      }
      foreach(var pending in inFlight) {
        pending.Value(Err("offline", "The switch transport stopped"));
      }
      var tr = Interlocked.Exchange(ref _transport, null);
      if(tr!=null) {
        tr.Dispose();
      }
    }

    // /local/asw/access, 0/1/2 — see state.h in SW/Server for the exact same
    // three states. Request() below gates on _access; across the link
    // asw_ws_gateway keeps its own cached copy of this topic and gates writes
    // from clients outside trusted_nets on it. Nothing else here derives from
    // this.
    private void AccessChanged(TopicEvent p, SubRec sr) {
      // StateChanged only: a Once subscribe also replays the current value once
      // at Subscribe() time (EventKind.Snapshot), and Start() has already read
      // _access straight from the topic by then — acting on that replay would
      // just re-publish the same value for nobody.
      // Author == _owner is our own re-assert below; ignoring it is what keeps
      // this from looping.
      if(p.Kind != EventKind.StateChanged || p.Author == _owner) {
        return;
      }
      var v = p.Source.GetState();
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
      // Write the accepted value back, from us rather than from the client.
      //
      // The Lock/Local/Remote tab was built against a web API that relayed a
      // write back to the session that made it: the tab sends a request and
      // redraws only when a value arrives. The dashboard endpoint suppresses
      // exactly that echo - DashboardSession.SubChangedCore drops any event
      // whose Author is the session's own owner - so without this the tab
      // would sit on its old state until the page was reloaded.
      //
      // Re-asserting an identical number does reach subscribers: CmdState
      // compares the old and new JSValue by INSTANCE, not by value (Cmd.cs),
      // and upstream's comment there says that is staying. Our own AntGen
      // relies on the same fact from the other side - it de-duplicates in the
      // plugin because repeated identical numbers otherwise publish on every
      // poll. Author is _owner, so the guard at the top of this method ignores
      // the notification and this cannot loop.
      _accessT.SetState(_access, _owner);
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
    // Lazy, and resolved here rather than in Init(): the server reads enabled
    // before Init() runs (Program.InitPlugins), so a field filled in by Start()
    // would still be null at that point.
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        if(Owner.GetState().ValueType != JSC.JSValueType.Boolean) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
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
        case 2:  // BUS_EV_RESET - the console says it has reset and lost its
                 // state. It sends this itself once its link is up (bus.c in
                 // Console) and after an address change. Nobody asked for it,
                 // so it is a warning; a release is not, and no longer shares
                 // this code.
          Log.Warning("AntSw.con" + addr.ToString()+" device reset");
          ClearConsole(addr);
          break;
        case 43: // BUS_EV_RELEASE_MAIN
        case 44: // BUS_EV_RELEASE_AUX
                 // ClearConsoleState() on the device: an ordinary band release.
                 // ClearSlot() also zeroes AntCfg[].oRxCfg/oTxCfg for the
                 // released band, but never pushes that via cons_bin_refresh
                 // (calls are commented out in parser.c), so rxcfg/txcfg have
                 // to be cleared here too.
                 // Behind Verbose, like every other Log.Debug in this plugin
                 // (Transport.cs does the same): the level alone suppresses
                 // nothing -- Log.Process writes every record to console and
                 // file, and its file threshold is the literal LogLevel.Debug.
                 // "Not verbose" is this plugin's own flag, /$YS/AntSw/verbose.
          if(Verbose) Log.Debug("AntSw.con" + addr.ToString()+" release");
          ClearConsole(addr);
          break;
        case 41: // BUS_EV_ERROR_OFFLINE - the mainboard's poll counter dropped
                 // it. Same news also reaches OnFail as an F frame; both call
                 // the same method so the two cannot drift.
          Log.Warning("AntSw.con" + addr.ToString()+" link lost");
          ConsoleOffline(addr);
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
        case 5:  // BUS_EV_ONLINE - the poll counter declared it present again.
                 // Not necessarily a reboot (a cable, a connector), but what it
                 // held is not trustworthy either way. sel/slot are cleared here
                 // too, as NodeStateLost() does on the device; leaving them was
                 // how a console came back showing a selection it no longer had.
          Log.Warning("AntSw.con" + addr.ToString()+" link up");
          ClearConsole(addr);
          break;
        }
      }
    }

    // "Whatever this node held is gone" - the four shapes of it. Kept as methods
    // because the same news arrives on two paths, an E event mirrored by
    // parseEvent and an F frame from a failed exchange, and handling them apart
    // is how they drift. Names match clear_console / console_offline /
    // remote_free / remote_offline in SW/Server/core/src/state.c.
    private void ConsoleState(byte addr, int status) {
      _di.Get("con"+addr.ToString()+"/status", true, _owner).SetState(status);
      _di.Get("con"+addr.ToString()+"/sel", true, _owner).SetState(0);
      _di.Get("con"+addr.ToString()+"/slot", true, _owner).SetState(0);
      _di.Get("con"+addr.ToString()+"/rxcfg", true, _owner).SetState(0);
      _rxCfg[addr-1] = 0;
      _di.Get("con"+addr.ToString()+"/txcfg", true, _owner).SetState(0);
      _txCfg[addr-1] = 0;
    }

    private void ClearConsole(byte addr) { ConsoleState(addr, 0); }        // present, idle
    private void ConsoleOffline(byte addr) { ConsoleState(addr, 255); }    // PLS_OFFLINE

    private void RemoteFree(int rem) {
      _remoteSt[rem] = 0;
      _remoteStAux[rem] = 0;
      PublishRemStatus(rem);
    }

    private void RemoteOffline(int rem) {
      _remoteSt[rem] = 255;
      _remoteStAux[rem] = 255;
      PublishRemStatus(rem);
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
      case 2:  // BUS_EV_RESET - the remote says it has reset and lost its state.
               // It sends this itself once its link is up (bus.c in Remote) and
               // after an address change. Nobody asked for it, so it is a
               // warning; a release is not, and no longer shares this code.
        Log.Warning("AntSw.rem" + (rem+1).ToString()+" device reset");
        RemoteFree(rem);
        break;
      case 43: // BUS_EV_RELEASE_MAIN
      case 44: // BUS_EV_RELEASE_AUX - ClearSlot() on the device, i.e. the
               // ordinary "an owner let go of this remote".
        if(Verbose) Log.Debug("AntSw.rem" + (rem+1).ToString()+" release");
        RemoteFree(rem);
        break;
      case 41: // BUS_EV_ERROR_OFFLINE - the poll counter dropped it.
        Log.Warning("AntSw.rem" + (rem+1).ToString()+" link lost");
        RemoteOffline(rem);
        break;
      case 3: // Ptt Off
        _di.Get("con"+con.ToString()+"/status", true, _owner).SetState(2);
        break;
      case 4: // Ptt On
        _di.Get("con"+con.ToString()+"/status", true, _owner).SetState(3);
        break;
      case 5:  // BUS_EV_ONLINE - the poll counter declared it present again.
               // Not necessarily a reboot, but what it held is not trustworthy.
        Log.Warning("AntSw.rem" + (rem+1).ToString()+" link up");
        RemoteFree(rem);
        break;
      }
    }
    // What the node actually complained about. Bare numbers mean a trip to
    // bus_def.h every time something goes wrong on the air, which is the worst
    // moment for it. Mirrors fail_name() in SW/Server/core/src/state.c.
    private static string FailName(ushort param) {
      switch(param) {
      case 0x21: return "unknown command";
      case 0x22: return "frame error";
      case 0x23: return "bus fail";
      case 0x24: return "timeout";
      case 0x25: return "unknown event";
      case 0x26: return "buffer overflow";
      case 0x27: return "bad ext event";
      case 0x28: return "bad ext parameter";
      case 0x29: return "offline";
      case 0x30: return "state inconsistent";
      case 0x31: return "switch state";
      case 0x32: return "PA state warning";
      case 0x33: return "PA state error";
      case 0x37: return "band switch";
      default:   return "unknown error";
      }
    }

    // Every failure the devices report, including the ones no branch below acts
    // on. OnFail() used to handle 41 and 48-51 and drop the rest without a
    // word, so a buffer overflow, a framing error, a PA fault or a dead antenna
    // matrix left no trace anywhere.
    //
    // Two exceptions, both deliberate:
    //   41       announces itself in the branches below, together with what it
    //            means for the node.
    //   0x27/28  are answers to our own config Get/Set. The operator already
    //            sees them as ERR from CFGGET/CFGSET, and a Setup page being
    //            read code by code would otherwise fill the log with them --
    //            so they stay behind Verbose.
    //
    // The mainboard's own bus layer reports with addr = (bus << 4), i.e. device
    // 0, which is neither a console nor a remote -- hence the last case.
    private void LogFail(byte addr, ushort param) {
      if(param == 41) {
        return;
      }
      string who;
      if(addr >= 1 && addr <= 8) {
        who = "con" + addr.ToString();
      } else if(addr >= 17 && addr <= 24) {
        who = "rem" + (addr - 16).ToString();
      } else if(addr == 32) {
        who = "mainboard";
      } else {
        who = "bus" + (addr >> 4).ToString();
      }
      string msg = "AntSw." + who + " error 0x" + param.ToString("X2") + " " + FailName(param);
      if(param == 0x27 || param == 0x28) {
        if(Verbose) Log.Debug(msg);
      } else {
        Log.Warning(msg);
      }
    }

    private void OnFail(Command cmd) {
      int addr;
      LogFail(cmd.addr, cmd.param);
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
          //
          // The same news also arrives as an E event when the mainboard's poll
          // counter drops the node; both paths call ConsoleOffline so the two
          // cannot drift.
          Log.Warning("AntSw.con" + cmd.addr.ToString()+" link lost");
          ConsoleOffline(cmd.addr);
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
          // The owner loses the remote, so it loses its selection with it -
          // sel/slot go too, which is what the device does (ReleaseSlot).
          addr = cmd.addr - 17;
          if(_remoteSt[addr]>=1 && _remoteSt[addr]<=8)
            ClearConsole(_remoteSt[addr]);
          Log.Warning("AntSw.rem" + (addr+1).ToString()+" link lost");
          RemoteOffline(addr);
          break;
        case 48:
        case 49:
        case 50:
        case 51:
          // A recoverable fault: the remote stays present, but free.
          addr = cmd.addr - 17;
          if(_remoteSt[addr]>=1 && _remoteSt[addr]<=8)
            ClearConsole(_remoteSt[addr]);
          RemoteFree(addr);
          break;
        }
        break;
      }
    }

    private void Request(TopicEvent p, SubRec sr) {
      byte con;
      int tmp;
      // _access == 0 (Lock) means no request is acted on at all, whoever sent
      // it — the LAN/remote distinction belongs to whatever served the client
      // (DashboardAcl here, trusted_nets in asw_ws_gateway), not to this.
      if(_access == 0 || p.Author==_owner || p.Source.path.Length < 17 || !p.Source.path.StartsWith("/export/req/con") || !byte.TryParse(p.Source.path.Substring(15, 1), out con) || con==0 || con > 8) {
        return;
      }
      switch(p.Source.name) {
      case "ptt":
        if(p.Source.GetState().ValueType==JSC.JSValueType.Boolean) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)(((bool)p.Source.GetState())?4:3)));  
        }
        p.Source.SetState(JSC.JSObject.Null, _owner);
        break;
      case "band":
        if(p.Source.GetState().IsNumber && (tmp = (int)p.Source.GetState())>0 && tmp <= 8) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)((tmp-1)*2 + 64)));  
        }
        p.Source.SetState(0, _owner);
        break;
      case "rxcfg":
        if(p.Source.GetState().IsNumber && (tmp = (int)p.Source.GetState())>0 && tmp <= 8) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)((tmp-1)*2 + 96)));  
        }
        p.Source.SetState(0, _owner);
        break;
      case "txcfg":
        if(p.Source.GetState().IsNumber && (tmp = (int)p.Source.GetState())>0 && tmp <= 8) {
          _transport.Write(new Command(CommandCode.Event, (byte)(32+con), (byte)((tmp-1)*2 + 97)));  
        }
        p.Source.SetState(0, _owner);
        break;
      }
    }

    public bool Verbose { get { return _verbose != null && (bool)_verbose.GetState(); } }
  }
}
