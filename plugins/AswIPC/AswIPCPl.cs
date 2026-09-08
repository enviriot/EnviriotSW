///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Threading;
using X13.Repository;

namespace X13.AswIPC {
  /// <summary>Bridges Enviriot's topic tree to an ASW station over the IPC line
  /// protocol of SW/Server (include/asw/ipc_protocol.h).
  ///
  /// The station's antenna switch is driven by asw_core on the single-board
  /// computer wired to it; that serial port belongs to that process alone. This
  /// plugin carries what Enviriot still owns - rotators, and whatever else is
  /// wired to this machine - across to the station's web UI, and carries the
  /// station's own state back so Enviriot can see it.
  ///
  /// It listens; the gateway connects. That is the one place it deliberately
  /// departs from the MQTT plugin it is otherwise modelled on.
  ///
  /// Configuration lives entirely in topics, and the plugin writes a complete
  /// working default the first time it starts:
  ///
  ///   /$YS/AswIPC/bind     "0.0.0.0"   address to listen on
  ///   /$YS/AswIPC/port     5010
  ///   /$YS/AswIPC/verbose  false
  ///
  /// and two mount points, /export/out and /export/req, each carrying fields
  /// that say what crosses the link. Any topic that grows an IPC.root field
  /// becomes a mount, so the defaults can be moved or added to; they are only a
  /// starting point, not a fixed layout.
  ///
  /// AntSw is untouched and unrelated, but it drives its own serial port to a
  /// switch. Two masters on one bus is a race, not a fallback, so disable it
  /// (/$YS/AntSw = false) when the station is reached through this plugin.</summary>
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 8)]
  [ExportMetadata("name", "AswIPC")]
  public class AswIPCPl : IPlugModul {
    private const int DEF_PORT = 5010;
    private const string DEF_BIND = "0.0.0.0";
    private const string OWNER_PATH = "/$YS/AswIPC";
    /// <summary>The type a mount topic declares: the marker and the schema at once.</summary>
    /// <remarks>A path under /$YS/TYPES, the way every "type" field names a type
    /// (TypeHelper.ResolveTypeTopic). Assigning it is how an operator asks for a
    /// mount - TypeChanged below answers by writing IPC.root - and the same
    /// topic carries what the five fields mean, so "this topic is an IPC mount"
    /// is said once and in one place.</remarks>
    private const string TYPE_NAME = "Ext/IPC";
    private const string TYPE_PATH = "/$YS/TYPES/" + TYPE_NAME;

    /// <summary>What the descriptor below declares, and what an older one is measured against.</summary>
    private const int TYPE_VER = 2;

    /// <summary>The Ext/IPC descriptor: an icon, and what the five fields mean.</summary>
    /// <remarks>Created from code rather than in base.xst, so the server's own
    /// seed stays upstream's; it lands in the database as content, which is
    /// where the MQTT-SN types live too. "manifest" stamps the type onto a topic
    /// created FROM it (TopicRpcDispatcher.ExecuteAdd), which is the other way
    /// round to arrive at the same place.
    /// <para>The "mi" here IS what the Inspector reads. SchemaOverlay.ForManifest
    /// lays three sources over one another - the global /$YS/TYPES/Ext/Manifest
    /// catalog, the TYPE topic's state, and the topic's own manifest - and
    /// BuildAddItems applies that union at every level, the root included; its
    /// own comment records that the root used to be an exception and no longer
    /// is. So these descriptions reach exactly the topics that declare this type
    /// and no others, which is what the global catalog could not do: an entry
    /// there is offered on every topic in the tree or on none. That is why they
    /// used to be written into the catalog without a "default", inert except
    /// one level down, and why they need not be any more.</para>
    /// <para>Two levels, and both carry a "default", because BuildAddItems skips
    /// a descriptor without one: the outer offers IPC on a typed topic that has
    /// lost it, the inner offers whichever of the five are missing. The outer
    /// default is root alone - it is the one mandatory field, and creating the
    /// other four empty would say "asked for, and nothing matched" where nothing
    /// was asked for at all.</para></remarks>
    private const string TYPE_DESCRIPTOR = @"{
      ""ver"": 2,
      ""icon"": ""Folder"",
      ""hint"": ""IPC mount - assign this type and the plugin writes IPC.root"",
      ""willful"": true,
      ""manifest"": { ""type"": ""Ext/IPC"" },
      ""mi"": { ""IPC"": {
        ""default"": { ""root"": """" },
        ""hint"": ""which subtree crosses the link, and in which direction"",
        ""mi"": {
          ""root"":   { ""default"": """", ""hint"": ""/export/out, /export/req or /local/ - anchors the names, must be a whole path"" },
          ""out"":    { ""default"": """", ""hint"": ""prefixes whose state we publish outward, comma separated"" },
          ""in"":     { ""default"": """", ""hint"": ""prefixes whose state we mirror inward, comma separated"" },
          ""accept"": { ""default"": """", ""hint"": ""request prefixes we accept, comma separated"" },
          ""send"":   { ""default"": """", ""hint"": ""request prefixes we send, comma separated"" }
        }
      } }
    }";

    private Topic _owner;
    private SubRec _verboseSR, _mountSR, _endpointSR, _typeSR;
    private IpcServer _srv;
    private readonly object _srvLock = new object();
    private readonly Dictionary<Topic, IpcMount> _mounts = new Dictionary<Topic, IpcMount>();

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

      // Config, not DB: the two halves of Saved are mutually exclusive and pick
      // the store, and only Config state is written into server.xst.
      var verboseT = _owner.Get("verbose");
      if(verboseT.GetState().ValueType != JSC.JSValueType.Boolean) {
        verboseT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        verboseT.SetState(false);
      }
      _verboseSR = verboseT.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value,
        (p, s) => verbose = (_verboseSR.setTopic != null && _verboseSR.setTopic.GetState().As<bool>()));

      var bindT = _owner.Get("bind");
      if(bindT.GetState().ValueType != JSC.JSValueType.String) {
        bindT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        bindT.SetState(DEF_BIND);
      }
      var portT = _owner.Get("port");
      if(!portT.GetState().IsNumber) {
        portT.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        portT.SetState(DEF_PORT);
      }

      DeclareType();
      SeedDefaultMounts();

      // The listener comes up now, whatever the mounts say. An unconfigured
      // plugin that still answers on its port separates "not running" from
      // "nothing mapped" - two failures that look identical when the only
      // symptom is a refused connection.
      OpenEndpoint();
      _endpointSR = _owner.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.Children, EndpointChanged);

      // Any topic that grows the mount field becomes a mount point, exactly as
      // the MQTT plugin watches for MQTT.uri. There is no list to keep in step
      // with reality.
      _mountSR = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "IPC.root", MountChanged);

      // And a topic that declares our type asks to become one. Subscribed after
      // the mounts, so a topic that already has both is seen as a mount first
      // and TypeChanged has nothing left to do for it.
      _typeSR = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "type", TypeChanged);
    }

    public void Tick() {
    }

    public void Stop() {
      foreach(var sr in new[] { Interlocked.Exchange(ref _mountSR, null),
                                Interlocked.Exchange(ref _typeSR, null),
                                Interlocked.Exchange(ref _endpointSR, null) }) {
        if(sr != null) {
          sr.Dispose();
        }
      }
      lock(_mounts) {
        foreach(var m in _mounts.Values.ToArray()) {
          m.Dispose();
        }
        _mounts.Clear();
      }
      lock(_srvLock) {
        if(_srv != null) {
          _srv.Dispose();
          _srv = null;
        }
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

    #region defaults
    /// <summary>Publishes the Ext/IPC type, and replaces one an older build wrote.</summary>
    /// <remarks>Versioned rather than written once. "Never overwrite what is
    /// there" was the rule until the descriptor gained the field descriptions,
    /// and it would have kept every installation of the previous build on a
    /// descriptor that has none: the type would exist, the Inspector would find
    /// no schema in it, and nothing would say why. A schema change has to land.
    /// <para>The price is that an operator's edited hint is replaced when "ver"
    /// rises. That is the same bargain base.xst strikes for its own types -
    /// LiteDB_Pl drops the database copy of a subtree whose repository version
    /// is newer - and the reason it is a version rather than an unconditional
    /// write is the edit that survives everything in between.</para>
    /// <para>Its own key, not the manifest "version" field base.xst uses: that
    /// one is compared at database load against what base.xst declares, and
    /// base.xst declares no Ext/IPC. Nothing would ever read it.</para>
    /// <para>Nothing is written into /$YS/TYPES/Ext/Manifest any more. The entry
    /// that used to go there described the same five fields and had to be given
    /// no "default" to stay out of every topic's Add menu, the catalog being
    /// global; carried by the type, the descriptions reach the topics that
    /// declare it and no others, and can be offered properly. An installation of
    /// the previous build still has that entry in its database - inert, now that
    /// the same names arrive from a nearer source - and it is left to be removed
    /// by hand.</para></remarks>
    private void DeclareType() {
      var t = Topic.root.Get(TYPE_PATH, true, _owner);
      var st = t.GetState();
      bool had = st.IsObject();
      if(had && st["ver"].IsNumber && (int)st["ver"] >= TYPE_VER) {
        return;
      }
      t.SetState(JsLib.ParseJson(TYPE_DESCRIPTOR), _owner);
      t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB | Topic.Attribute.Internal);
      Log.Info(had ? "AswIPC: updated {0} to version {1}" : "AswIPC: declared {0}, version {1}",
               TYPE_PATH, TYPE_VER);
    }

    /// <summary>Turns "this topic is an Ext/IPC" into a mount by writing IPC.root.</summary>
    /// <remarks>This is the way a mount is made by hand, and it exists because
    /// a mount is four fields on two topics whose paths have to agree, and an
    /// operator rebuilding that by hand gets it wrong before he gets it right.
    /// Assigning a type is per-topic, so the gesture that means "make this a
    /// mount" is the one an operator makes on the topic itself.
    /// <para>Only IPC.root is written. The four direction lists stay empty
    /// because only the operator knows what should cross, and the Add menu
    /// inside IPC offers them once root is there. A topic that already has a
    /// root is left alone: this creates a mount, it does not repair one.</para>
    /// <para>Whether the root is a legal one is not decided here. MountChanged
    /// already refuses anything outside /export/out and /export/req and says so
    /// by path in the log, and one rule in one place beats two.</para></remarks>
    private void TypeChanged(TopicEvent p, SubRec sr) {
      if(p.Kind != EventKind.FieldChanged && p.Kind != EventKind.Snapshot) {
        return;
      }
      // Our own stamp on the seeded mounts, coming back to us.
      if(p.Author == _owner) {
        return;
      }
      var t = p.Source;
      if(Field(t, "type") != TYPE_NAME || !string.IsNullOrEmpty(Field(t, "IPC.root"))) {
        return;
      }
      t.SetField("IPC.root", t.path, _owner);
      Log.Info("AswIPC: {0} declares {1}, IPC.root written", t.path, TYPE_NAME);
    }

    /// <summary>True while the topic exists and declares a mount on itself.</summary>
    private static bool Mounted(string path) {
      Topic t;
      return Topic.root.Exist(path, out t) && !string.IsNullOrEmpty(Field(t, "IPC.root"));
    }

    /// <summary>Write a working configuration the first time, so the plugin is
    /// useful before anyone has read its documentation.
    ///
    /// The mounts are their own witness: this runs only while NEITHER
    /// /export/out nor /export/req declares an IPC.root, which is the state a
    /// fresh installation is in and nothing else arrives at by accident.
    /// Deleting one of the two is therefore stable - an operator who wants the
    /// link to carry rotator state but not rotator commands keeps that - and
    /// deleting both reads as "start over", which is what it looks like. An
    /// operator who wants the link to carry nothing at all switches the plugin
    /// off instead, which is the gesture that means it.
    ///
    /// This replaces a /$YS/AswIPC/configured flag, and the store is why. A
    /// flag has to be persisted somewhere, and the two candidates have
    /// different lifetimes: the settings beside it go to server.xst, while the
    /// mounts are manifest fields and live only in the database. A flag in
    /// server.xst therefore survived a wiped database that had taken the mounts
    /// with it, and asserted a configuration that was no longer there - the
    /// plugin came up with no mounts and refused to seed them. A flag in the
    /// database avoided that but said nothing the mounts do not already say.
    /// Asking the mounts cannot fall out of step with the mounts.
    ///
    /// An installation set up by an earlier build still carries that flag. It
    /// is read by nothing and deleted by nothing: removing an operator's topic
    /// on startup is not this method's business, and the one left behind is
    /// inert.</summary>
    private void SeedDefaultMounts() {
      if(Mounted("/export/out") || Mounted("/export/req")) {
        return;
      }
      /* Rotators, and nothing else.
       *
       * What is wired to this machine is rotators; that is the whole of what
       * Enviriot still owns and the only tree with no other writer. Everything
       * that used to be here has one: /export/out/con and /export/out/rem are
       * published by asw_core from its own bus reads, /export/out/ag by antgen
       * on the Pi, and /export/req/con is asw_core's to accept. A default that
       * mirrors any of them puts a second writer on a name that already has
       * one, which is the failure a mount can least afford. An operator who
       * wants a mirror asks for it by editing these fields; a default must
       * not.
       *
       * IPC.in and IPC.send are therefore absent rather than empty. An absent
       * field reads as "this direction was never asked for"; an empty string
       * would read as "asked for, and nothing matched". */
      var outT = Topic.root.Get("/export/out", true, _owner);
      outT.SetField("type", TYPE_NAME, _owner);
      outT.SetField("IPC.root", "/export/out", _owner);
      outT.SetField("IPC.out", "/export/out/rot", _owner);

      var reqT = Topic.root.Get("/export/req", true, _owner);
      reqT.SetField("type", TYPE_NAME, _owner);
      reqT.SetField("IPC.root", "/export/req", _owner);
      reqT.SetField("IPC.accept", "/export/req/rot", _owner);

      Log.Info("AswIPC: wrote the default configuration to /export/out and /export/req");
    }
    #endregion defaults

    #region endpoint
    private void OpenEndpoint() {
      IPAddress bind;
      var bindV = _owner.Get("bind").GetState();
      string host = bindV.ValueType == JSC.JSValueType.String ? bindV.Value as string : DEF_BIND;
      if(string.IsNullOrEmpty(host) || host == "*") {
        bind = IPAddress.Any;
      } else if(!IPAddress.TryParse(host, out bind)) {
        // A name would have to resolve to an address on THIS machine to be
        // bindable, so a typo is far likelier than a legitimate use.
        Log.Error("AswIPC: bind='{0}' is not an address to listen on", host);
        return;
      }
      var portV = _owner.Get("port").GetState();
      int port = portV.IsNumber ? (int)portV : DEF_PORT;
      if(port < 1 || port > 65535) {
        Log.Error("AswIPC: port={0} is out of range", port);
        return;
      }
      lock(_srvLock) {
        if(_srv != null) {
          _srv.Dispose();
        }
        _srv = new IpcServer(this, bind, port);
      }
    }

    private void EndpointChanged(TopicEvent p, SubRec sr) {
      if(p.Kind != EventKind.StateChanged) {
        return;
      }
      if(p.Source.name != "bind" && p.Source.name != "port") {
        return;
      }
      Log.Info("AswIPC: endpoint changed, restarting the listener");
      OpenEndpoint();
    }
    #endregion endpoint

    #region mounts
    private void MountChanged(TopicEvent p, SubRec sr) {
      if(p.Kind == EventKind.Created) {
        return;
      }
      // Rebuild rather than patch: everything about a mount is read once at
      // construction, and the protocol has no way to retract a subscription on
      // a live connection anyway.
      IpcMount old;
      lock(_mounts) {
        if(_mounts.TryGetValue(p.Source, out old)) {
          _mounts.Remove(p.Source);
        }
      }
      if(old != null) {
        old.Dispose();
      }
      if(p.Kind != EventKind.FieldChanged && p.Kind != EventKind.Snapshot) {
        return;
      }
      string root = Field(p.Source, "IPC.root");
      if(string.IsNullOrEmpty(root)) {
        if(old != null) {
          Log.Info("AswIPC: mount {0} removed", p.Source.path);
          DropLinks("mount removed");
        }
        return;
      }
      if(!root.StartsWith("/export/out", StringComparison.Ordinal)
         && !root.StartsWith("/export/req", StringComparison.Ordinal)
         && !root.StartsWith("/local/", StringComparison.Ordinal)) {
        /* Three trees, because the gateway relays three and no more: it
         * accepts a publication under /export/out/ or /local/ and routes a
         * write by req_prefix, and everything else it drops without a word.
         * A root anywhere else would make a mount that silently carries
         * nothing, which is worse than this line.
         *
         * /local/ is the LAN-only half - visible to a browser the gateway
         * decided is on the LAN, and to no other. It is where a mount belongs
         * whose contents are nobody's business remotely: configuration,
         * enable switches. Which of the four lists it may then use is
         * IpcMount's to say, and two of them it may not. */
        Log.Warning("AswIPC {0}: IPC.root='{1}' is under none of /export/out, /export/req, /local/ - ignored",
                    p.Source.path, root);
        return;
      }
      var m = new IpcMount(this, p.Source, root,
                           Field(p.Source, "IPC.out"), Field(p.Source, "IPC.in"),
                           Field(p.Source, "IPC.accept"), Field(p.Source, "IPC.send"));
      lock(_mounts) {
        _mounts[p.Source] = m;
      }
      Log.Info("AswIPC mount {0}", m);
      // A new mount's subscriptions have to be stated to the gateway, and there
      // is no verb for that on a live connection.
      DropLinks("mount changed");
    }

    private static string Field(Topic t, string name) {
      var v = t.GetField(name);
      return v == null ? null : v.Value as string;
    }

    private IpcMount[] Mounts() {
      lock(_mounts) {
        return _mounts.Values.ToArray();
      }
    }

    private void DropLinks(string why) {
      lock(_srvLock) {
        if(_srv != null) {
          _srv.DropLinks(why);
        }
      }
    }
    #endregion mounts

    #region dispatch, called from a link's thread
    internal void SendSubscriptions(IpcLink link) {
      foreach(var m in Mounts()) {
        foreach(var s in m.Subscriptions) {
          link.Send("SUB\t" + s + "\n");
        }
      }
    }

    internal void Snapshot(IpcLink link) {
      foreach(var m in Mounts()) {
        m.Snapshot(link);
      }
    }

    /// <summary>A request routed to us by the gateway. False means our mounts
    /// and the gateway's req_prefix disagree - a configuration error worth
    /// reporting rather than swallowing.</summary>
    internal bool OnCommand(string path, string payload) {
      foreach(var m in Mounts()) {
        if(m.AcceptsCommand(path) && m.Apply(path, payload)) {
          return true;
        }
      }
      Log.Warning("AswIPC: CMD {0} matches no mount", path);
      return false;
    }

    internal void OnPublish(string path, string payload) {
      foreach(var m in Mounts()) {
        if(m.AcceptsPublish(path)) {
          m.Apply(path, payload);
        }
      }
    }

    internal void Publish(string path, string payload) {
      lock(_srvLock) {
        if(_srv != null) {
          _srv.Publish(path, payload);
        }
      }
    }

    internal void Request(string path, string payload) {
      lock(_srvLock) {
        if(_srv != null) {
          _srv.Request(path, payload);
        }
      }
    }
    #endregion dispatch
  }
}
