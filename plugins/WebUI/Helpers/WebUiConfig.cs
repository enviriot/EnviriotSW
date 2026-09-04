///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.IO;
using System.Threading;
using NiL.JS.Extensions;
using X13.Repository;

namespace X13.WebUI.Helpers {
  // Everything under /$YS/WebUI, seeded once and then kept current by subscription.
  //
  // Grouped by the thing each setting belongs to, because three different things live behind
  // one port: Static serves the files every page is built from, WebIDE is the editor, Dashboard
  // the read-only surface. What stays at the root is what all three share - the port there is
  // only one of, and trustedProxies, which decides whether X-Real-IP may be believed at all and
  // so applies before any group is chosen.
  //
  // The values are read from the topics exactly once each, when they change. Before this, every
  // logged request re-read a topic to ask whether to log it, and every request and socket open
  // re-read the ACL topics - repository walks on the hot path to fetch something that changes
  // perhaps twice a year.
  internal sealed class WebUiConfig : IDisposable {
    // "local" resolves to the subnets of this machine's adapters, see NetworkAcl.
    public const string DefaultTrustedNets = "local";
    /// <summary>Largest import body accepted, in bytes. Anything larger answers 413.</summary>
    /// <remarks>The body is parsed in memory as one buffer, so this is the memory a single POST
    /// can claim - which is why there was a limit to add at all: there was none, and the sender
    /// chose. 16 MB is comfortably above a whole-tree .xst export, which is what people import.
    /// <para>A constant, not a setting. It was a Config topic with a floor and a ceiling around
    /// it, and the range existed only to keep the setting from being useful in the wrong
    /// direction: zero would have refused every import, and a value near int.MaxValue would have
    /// restored the very problem the limit was added for. A number nobody may set to a harmful
    /// value and nobody has asked to raise is a constant.</para></remarks>
    public const int MaxImportBytes = 16 * 1024 * 1024;
    public const string DefaultStaticPath = "..\\www";

    private const Topic.Attribute CfgAttr = Topic.Attribute.Required | Topic.Attribute.Config;
    // Readonly so it is not changed by accident. It stays a free-form path on purpose: behind a
    // proxy this legitimately points at a shared directory outside the install. A UI hint only -
    // Readonly is not enforced on the commit path.
    private const Topic.Attribute PathAttr = CfgAttr | Topic.Attribute.Readonly;

    private readonly Topic _owner;
    private SubRec[] _subs;

    // Written on the engine thread by the subscription callbacks, read from HTTP and socket
    // threads. Volatile rather than locked: each is a single reference or bool assignment, and
    // a reader that sees the previous value for one tick is reading a tracing flag or an ACL
    // that is about to be re-read on the next request anyway.
    private volatile bool _verboseStatic;
    private volatile bool _verboseIde;
    private volatile bool _verboseDashboard;
    private volatile string _trustedNets = DefaultTrustedNets;
    private volatile string _trustedProxies = string.Empty;
    private volatile string _staticPathRaw = DefaultStaticPath;

    public WebUiConfig(Topic owner) {
      _owner = owner ?? Topic.root.Get("/$YS/WebUI", true);
    }

    /// <summary>The static root, resolved to an absolute path.</summary>
    /// <remarks>Followed like the rest, but WebUiHost precomputes the prefix it compares every
    /// served path against, so a change here reaches the host only on a restart.</remarks>
    public string StaticPath {
      get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _staticPathRaw)); }
    }

    public bool VerboseStatic { get { return _verboseStatic; } }
    public bool VerboseIde { get { return _verboseIde; } }
    public bool VerboseDashboard { get { return _verboseDashboard; } }

    /// <summary>Networks allowed to reach the IDE. Empty falls back to the default.</summary>
    /// <remarks>Under WebIDE rather than at the root because that is the only thing it guards:
    /// WebUiHost.IsIdeSurface narrowed it to the IDE, and the dashboard is gated per topic by
    /// DashboardAcl instead. At the root it implied a reach it no longer has.</remarks>
    public string TrustedNets { get { return _trustedNets; } }

    public string TrustedProxies { get { return _trustedProxies; } }
    /// <summary>Creates whatever is missing, primes every value, then follows all of it.</summary>
    /// <remarks>The groups are not created explicitly: EnsureCfg's path makes them on the way,
    /// and Xst.Export keeps a parent whose children were exported, so a Config leaf carries its
    /// group into server.xst without the group needing attributes of its own.</remarks>
    public void Start() {
      _subs = new SubRec[] {
        JsExtLib.EnsureCfg(_owner, "Static/path", PathAttr, v => _staticPathRaw = string.IsNullOrWhiteSpace(v) ? DefaultStaticPath : v,
          DefaultStaticPath),
        JsExtLib.EnsureCfg(_owner, "Static/verbose", CfgAttr, v => _verboseStatic = v, false),
        JsExtLib.EnsureCfg(_owner, "WebIDE/verbose", CfgAttr, v => _verboseIde = v, false),
        JsExtLib.EnsureCfg(_owner, "Dashboard/verbose", CfgAttr, v => _verboseDashboard = v, false),
        // Cleared means "back to the default", not "no networks at all" - the latter would lock
        // the IDE out from everywhere but loopback on a stray delete.
        JsExtLib.EnsureCfg(_owner, "WebIDE/trustedNets", CfgAttr, v => _trustedNets = string.IsNullOrWhiteSpace(v) ? DefaultTrustedNets : v,
          DefaultTrustedNets),
        // Empty is meaningful here and must survive: it means X-Real-IP is believed from nobody.
        JsExtLib.EnsureCfg(_owner, "trustedProxies", CfgAttr, v => _trustedProxies = v ?? string.Empty,
          string.Empty),
      };
    }

    public void Dispose() {
      SubRec[] subs = Interlocked.Exchange(ref _subs, null);
      if(subs == null) return;
      foreach(SubRec sub in subs) {
        if(sub != null) sub.Dispose();
      }
    }

    /// <summary>The port to listen on, or 0 when none is configured.</summary>
    /// <remarks>Not followed: the listening socket is bound at Start and a later edit cannot move
    /// it without a restart, so caching a value nothing acts on would only make the field lie.
    /// Read straight from the topic at the one moment it is used.</remarks>
    public Topic PortTopic {
      get { return _owner.Get("port", true); }
    }
  }
}
