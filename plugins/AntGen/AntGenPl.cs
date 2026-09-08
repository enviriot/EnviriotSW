///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.AntGen {
  /// <summary>Reads a 4O3A Antenna Genius, publishes what it sees, and now
  /// makes the one write this station needs: which antenna the active port
  /// uses. Everything else — which band, which port — is still AG's own call,
  /// driven by the FLEX it follows; this plugin never touches that.
  ///
  /// Published under /export/out/ag/&lt;name&gt;/ so it crosses to the station
  /// through the existing allow-list; the gateway relays /export/out/ and
  /// nothing else invents an exception.
  ///
  ///   enable          0/1 — is the connection wanted at all
  ///   link            0/1 — is the device answering RIGHT NOW
  ///   active          which port the radio is on, 0 if neither
  ///   portA/band      band id, 0 = none
  ///   portA/bandName  "20m" — from the device's own table
  ///   portA/ant       antenna id, which is also the physical port number
  ///   portA/antName   "LOGO"
  ///   portA/tx        1 while transmitting
  ///   portA/raw       all six fields, unparsed
  ///   band/&lt;id&gt;/…, ant/&lt;id&gt;/… the device's tables
  ///
  /// /export/req/ag/&lt;name&gt;/portA/ant, .../portB/ant — write an antenna id
  /// (1-8) to switch that PHYSICAL port; 0 means no standing request. Mirrors
  /// the out-side portA/ant one for one, on purpose: port identity here is
  /// the device's own (its port 1 stays "portA" no matter which radio is
  /// plugged into it right now — see AgStatus), not "whichever port is
  /// currently active", which an earlier "band"/"logo" symbolic version
  /// resolved through the antenna/band tables. That indirection was cut after
  /// it did not switch anything on the real device; a direct id write is
  /// fewer places for such a thing to go wrong, and is what this replaces.
  ///
  /// Enforced once per poll (DesiredAntenna, called from AgClient), not only
  /// on the write that changed the request — so a standing request outlives a
  /// band change on that port, since nothing in the capture showed AG
  /// re-selecting an antenna on its own when the band changed underneath it.
  ///
  /// The Antenna Genius accepts ONE client at a time, so /$YS/AntGen/enable is
  /// operational, not a debug switch: clearing it drops the socket at once and
  /// hands the device to the vendor's software or to whoever needs to
  /// reconfigure it, without stopping Enviriot. It is read at startup like any
  /// other setting and takes effect the moment it changes.
  ///
  /// `link` is not decoration. Without it "band 0" from a healthy device that
  /// is simply idle is indistinguishable from "band 0" because nothing has
  /// answered for an hour, and those call for opposite reactions.
  ///
  /// `raw` is kept for the same reason: fields 0, 2, 4 and the trailing one
  /// never moved in either capture, so they are recorded rather than guessed
  /// at. A week of them is worth more than any amount of reasoning.</summary>
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 8)]
  [ExportMetadata("name", "AntGen")]
  public class AntGenPl : IPlugModul, IAgSink {
    private const string DEF_HOST = "192.168.1.204";
    private const int DEF_PORT = 9007;
    private const int DEF_POLL = 500;
    private const string DEF_NAME = "ag1";
    private const string OWNER_PATH = "/$YS/AntGen";

    private Topic _owner, _root;
    private SubRec _verboseSR, _cfgSR, _reqSR;
    private AgClient _client;
    private readonly object _lock = new object();

    /// <summary>Standing antenna request per physical port, 0 = none. Guarded
    /// by _lock: read from AgClient's poll thread via DesiredAntenna(),
    /// written from Enviriot's repo thread via ReqChanged().</summary>
    private int _reqA, _reqB;

    /* Last published value per topic. Enviriot's de-duplication is by
     * reference, not by value, so setting the same number twice publishes
     * twice — at two polls a second that is a flood of "changes" that never
     * changed. Every setter here goes through Pub(). */
    private readonly Dictionary<string, string> _last = new Dictionary<string, string>();
    private readonly Dictionary<int, AgBand> _bands = new Dictionary<int, AgBand>();
    private readonly Dictionary<int, AgAntenna> _ants = new Dictionary<int, AgAntenna>();

    public bool verbose;

    #region IPlugModul Members
    public void Init() {
    }

    public void Start() {
      /* No SetState here: this very topic holds the plugin's own enable flag,
       * which the `enabled` property below reads. Writing 0 over it made the
       * getter see a non-boolean and helpfully set it back to true, so a
       * plugin the operator had switched off came back on at the next start. */
      _owner = Topic.root.Get(OWNER_PATH);

      Cfg("enable", true);
      Cfg("verbose", false);
      Cfg("host", DEF_HOST);
      Cfg("port", DEF_PORT);
      Cfg("name", DEF_NAME);
      Cfg("poll_ms", DEF_POLL);

      _verboseSR = _owner.Get("verbose").Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value,
        (p, s) => verbose = (_verboseSR.setTopic != null && _verboseSR.setTopic.GetState().As<bool>()));
      _cfgSR = _owner.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.Children, CfgChanged);

      Restart();
    }

    public void Tick() {
    }

    public void Stop() {
      foreach(var sr in new[] { Interlocked.Exchange(ref _verboseSR, null),
                                Interlocked.Exchange(ref _cfgSR, null),
                                Interlocked.Exchange(ref _reqSR, null) }) {
        if(sr != null) {
          sr.Dispose();
        }
      }
      AgClient c;
      lock(_lock) {
        c = _client;
        _client = null;
      }
      if(c != null) {
        c.Dispose();          /* outside the lock — see Restart() */
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

    #region configuration
    /// <summary>Create a setting with a working default the first time.
    /// The lesson from AswIPC: a plugin that waits for the operator to guess
    /// which topic to create is not configurable, it is broken.</summary>
    private void Cfg(string name, JSC.JSValue def) {
      var t = _owner.Get(name);
      var st = t.GetState();
      bool empty = st == null || !st.Defined
                   || (def.ValueType == JSC.JSValueType.Boolean && st.ValueType != JSC.JSValueType.Boolean)
                   || (def.IsNumber && !st.IsNumber)
                   || (def.ValueType == JSC.JSValueType.String && st.ValueType != JSC.JSValueType.String);
      if(empty) {
        t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        t.SetState(def);
      }
    }

    private string CfgStr(string name, string def) {
      var v = _owner.Get(name).GetState();
      return v.ValueType == JSC.JSValueType.String ? (v.Value as string) ?? def : def;
    }

    private int CfgInt(string name, int def) {
      var v = _owner.Get(name).GetState();
      return v.IsNumber ? (int)v : def;
    }

    private void CfgChanged(TopicEvent p, SubRec sr) {
      if(p.Kind != EventKind.StateChanged) {
        return;
      }
      if(p.Source.name == "verbose") {
        return;                       /* has its own subscription, costs nothing */
      }
      Log.Info("AntGen: {0} changed, restarting the client", p.Source.name);
      Restart();
    }

    /// <summary>One subscription on the whole /export/req/ag/&lt;name&gt; subtree
    /// (see Restart()), dispatching by path the way AntSwPl.Request() does for
    /// its own /export/req tree. p.Author == _owner filters out our own resets
    /// at Restart() — those are not the operator asking for anything.</summary>
    private void ReqChanged(TopicEvent p, SubRec sr) {
      if(p.Kind != EventKind.StateChanged || p.Author == _owner || p.Source.name != "ant") {
        return;
      }
      var parent = p.Source.parent;
      int portNo;
      if(parent != null && parent.name == "portA") {
        portNo = 1;
      } else if(parent != null && parent.name == "portB") {
        portNo = 2;
      } else {
        return;
      }
      var v = p.Source.GetState();
      int want = v.IsNumber ? (int)v : 0;
      if(want != 0 && (want < 1 || want > 8)) {
        Log.Warning("AntGen: antenna request {0} for port {1} is outside 1-8 - ignored", want, portNo);
        return;
      }
      lock(_lock) {
        if(portNo == 1) {
          _reqA = want;
        } else {
          _reqB = want;
        }
      }
      Log.Info("AntGen: port {0} antenna request -> {1}", portNo, want == 0 ? "(none)" : want.ToString());
    }

    private bool Enabled {
      get {
        var v = _owner.Get("enable").GetState();
        return v.ValueType != JSC.JSValueType.Boolean || (bool)v;
      }
    }

    private void Restart() {
      string host = CfgStr("host", DEF_HOST);
      int port = CfgInt("port", DEF_PORT);
      int poll = CfgInt("poll_ms", DEF_POLL);
      string name = CfgStr("name", DEF_NAME);

      /* Stop the old client with NO lock held. Dispose waits for the worker
       * thread, and that thread takes _lock in every callback — holding it
       * here would have the two wait on each other until the join timed out,
       * leaving a thread that still owns the one connection the device
       * allows. Exactly the situation the enable switch exists to prevent. */
      AgClient old;
      lock(_lock) {
        old = _client;
        _client = null;
      }
      if(old != null) {
        old.Dispose();
      }
      {
        var oldReqSR = Interlocked.Exchange(ref _reqSR, null);
        if(oldReqSR != null) {
          oldReqSR.Dispose();
        }
      }

      lock(_lock) {
        _last.Clear();
        _bands.Clear();
        _ants.Clear();

        /* Every node on the path needs a value. Topic.Get() raises a create
         * that reaches browsers before any state exists, and an empty value is
         * what the browser's JSON.parse throws on — the same trap AntSw
         * documents for /export/out. */
        var ag = Topic.root.Get("/export/out", true, _owner);
        ag.SetState(0);
        ag = ag.Get("ag", true, _owner);
        ag.SetState(0);
        _root = ag.Get(name, true, _owner);
        _root.SetState(0);
        Pub("link", 0);
        Pub("active", 0);
        foreach(var pn in new[] { "portA", "portB" }) {
          var pt = _root.Get(pn, true, _owner);
          pt.SetState(0);
        }

        /* The only input this plugin accepts, under /export/req/ like every
         * other station write — created and subscribed even while disabled,
         * so the operator can set a request before the link comes up.
         *
         * One subscription on the SUBTREE, not one per leaf: a leaf's own
         * Subscribe(Value) with no All/Children never delivered a live change
         * here (matching Once|Value, which is a one-shot read, not what a
         * leaf-only Value subscription needs to become a standing one).
         * AntSw's own request tree confirms the working shape:
         * rt.Subscribe(SubMask.All | SubMask.Value, Request) on the PARENT,
         * dispatching by p.Source inside the callback — mirrored below.
         *
         * Reset to 0 (no request) on every Restart(), the same way AntSw
         * resets its own /export/req tree in Start() — a request tree holds a
         * live command, not a persisted setting, so there is nothing to carry
         * forward from before a reconnect or a config change. */
        var req = Topic.root.Get("/export/req", true, _owner);
        req.SetState(0);
        req = req.Get("ag", true, _owner);
        req.SetState(0);
        req = req.Get(name, true, _owner);
        req.SetState(0);
        _reqA = _reqB = 0;
        var reqAT = req.Get("portA", true, _owner);
        reqAT.SetState(0);
        reqAT.Get("ant", true, _owner).SetState(0, _owner);
        var reqBT = req.Get("portB", true, _owner);
        reqBT.SetState(0);
        reqBT.Get("ant", true, _owner).SetState(0, _owner);
        _reqSR = req.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, ReqChanged);

        /* Published alongside the state so the station sees it: /$YS is not
         * exported, and "link=0" alone cannot distinguish a switch that was
         * turned off from one that stopped answering. */
        Pub("enable", Enabled ? 1 : 0);
        if(!Enabled) {
          /* The device accepts a single client. Off means the socket is gone,
           * so the vendor software — or whoever needs to reconfigure the box —
           * can have it. Dispose above has already closed it. */
          Log.Info("AntGen: disabled, the connection to {0}:{1} is released", host, port);
          return;
        }
        if(string.IsNullOrEmpty(host)) {
          Log.Warning("AntGen: host is empty, nothing to connect to");
          return;
        }
        _client = new AgClient(this, host, port, poll);
      }
    }
    #endregion configuration

    #region publishing
    /// <summary>Publish only on change. See _last.</summary>
    private void Pub(string rel, JSC.JSValue val) {
      if(_root == null) {
        return;
      }
      string s = JsLib.Stringify(val) ?? "null";
      string prev;
      if(_last.TryGetValue(rel, out prev) && prev == s) {
        return;
      }
      _last[rel] = s;
      var t = _root.Get(rel, true, _owner);
      t.SetState(val, _owner);
      if(verbose) {
        Log.Debug("AntGen {0} = {1}", t.path, s);
      }
    }

    private void PubPort(string pn, AgPort p) {
      AgBand b;
      AgAntenna a;
      Pub(pn + "/band", p.Band);
      Pub(pn + "/bandName", _bands.TryGetValue(p.Band, out b) ? b.Name : "");
      Pub(pn + "/ant", p.Antenna);
      Pub(pn + "/antName", _ants.TryGetValue(p.Antenna, out a) ? a.Name : "");
      Pub(pn + "/tx", p.Tx ? 1 : 0);
      Pub(pn + "/raw", p.Raw);
    }
    #endregion publishing

    #region IAgSink
    void IAgSink.AgTable(AgFrame f) {
      lock(_lock) {
        switch(f.Op) {
        case AgProto.CODE_SW_VERSION: {
            var fl = f.Fields;
            Pub("fw", fl.Length > 1 ? fl[1] : f.Payload);
            break;
          }
        case AgProto.CODE_NAME: {
            var fl = f.Fields;
            Pub("dev", fl.Length > 2 ? fl[2] : f.Payload);
            break;
          }
        case AgProto.CODE_BAND: {
            AgBand b;
            if(AgBand.TryParse(f.Payload, out b)) {
              _bands[b.Id] = b;
              var t = _root.Get("band/" + b.Id.ToString(CultureInfo.InvariantCulture), true, _owner);
              t.SetState(0);
              Pub("band/" + b.Id + "/name", b.Name);
              Pub("band/" + b.Id + "/from", (double)b.From);
              Pub("band/" + b.Id + "/to", (double)b.To);
            }
            break;
          }
        case AgProto.CODE_ANTENNA: {
            AgAntenna a;
            if(AgAntenna.TryParse(f.Payload, out a)) {
              _ants[a.Id] = a;
              var t = _root.Get("ant/" + a.Id.ToString(CultureInfo.InvariantCulture), true, _owner);
              t.SetState(0);
              Pub("ant/" + a.Id + "/name", a.Name);
              Pub("ant/" + a.Id + "/mask", a.BandMask);
              Pub("ant/" + a.Id + "/bands", BandNames(a));
            }
            break;
          }
        case AgProto.CODE_RADIO: {
            var fl = f.Fields;
            if(fl.Length > 2) {
              var t = _root.Get("radio/" + fl[0], true, _owner);
              t.SetState(0);
              Pub("radio/" + fl[0] + "/addr", fl[1] + ":" + fl[2]);
            }
            break;
          }
        }
      }
    }

    /// <summary>"20m,17m,15m,12m,10m" — the band mask spelled out. The mask is
    /// published too, but a hex number is not something to read off a screen
    /// while deciding whether the table matches the antenna field.</summary>
    private string BandNames(AgAntenna a) {
      var sb = new StringBuilder();
      for(int i = 1; i <= 15; i++) {
        if(!a.CoversBand(i)) {
          continue;
        }
        AgBand b;
        if(sb.Length > 0) {
          sb.Append(',');
        }
        sb.Append(_bands.TryGetValue(i, out b) ? b.Name : i.ToString(CultureInfo.InvariantCulture));
      }
      return sb.ToString();
    }

    void IAgSink.AgStatus(AgStatus st) {
      lock(_lock) {
        PubPort("portA", st.A);
        PubPort("portB", st.B);
        Pub("active", st.ActivePort);
        Pub("tail", st.Tail);
      }
    }

    void IAgSink.AgLink(bool up, AgFrameReader rd) {
      lock(_lock) {
        Pub("link", up ? 1 : 0);
        if(!up) {
          /* Say nothing about the switch while we cannot see it. Zeroing the
           * ports would read as "no band selected", which is a statement about
           * the station we are in no position to make. link=0 is the whole
           * message. */
          return;
        }
        if(rd != null) {
          Pub("preamble_odd", rd.OddPreamble ?? "");
        }
      }
    }

    /// <summary>Called from AgClient's poll thread, once per poll, for each
    /// physical port in turn. Just the standing request for that port, or 0 —
    /// no table lookup, no symbolic mode. AgClient itself already refuses to
    /// send it while that port is transmitting or has no band selected.</summary>
    int IAgSink.DesiredAntenna(int port, AgPort cur) {
      lock(_lock) {
        return port == 1 ? _reqA : _reqB;
      }
    }
    #endregion IAgSink
  }
}
