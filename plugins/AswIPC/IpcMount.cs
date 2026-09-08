///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using X13.Repository;

namespace X13.AswIPC {
  /// <summary>One local subtree bound to one remote subtree of the station.
  ///
  /// Direction is not a flag. Each of the four things this link can do has its
  /// own list of prefixes, and naming a prefix in one of them IS the
  /// instruction:
  ///
  ///   IPC.out     state we own          -> PUB upward on local change
  ///   IPC.in      state we mirror       -> SUB upward, incoming PUB applied
  ///   IPC.accept  requests aimed at us  -> incoming CMD applied
  ///   IPC.send    requests we make      -> CMD upward on local change
  ///
  /// One mount can therefore cover both halves of a tree - carrying our own
  /// state outward AND mirroring the station's inward under the same root -
  /// which a single "do we own this subtree" flag could not express, because
  /// both halves anchor at one topic and a topic carries one set of fields.
  /// The seeded default uses one half of each: rotator state out, rotator
  /// commands in. The other two lists are absent there, not empty.
  ///
  /// Two roots, two jobs, and they must not be conflated:
  ///   IPC.root is where NAMES are anchored. Local "rot/R1/actual" under a
  ///     mount rooted at /export/out is /export/out/rot/R1/actual. It has to be
  ///     a whole path: cutting inside a segment would mangle every name
  ///     derived from it.
  ///   The four lists say WHAT CROSSES, and may cut anywhere -
  ///     "/export/out/con" selects con1..con8. This is the same prefix rule the
  ///     gateway applies, so a list here and a pub_prefix there mean the same
  ///     thing.
  ///
  /// A root lives under /export/out, /export/req or /local/. The third is the
  /// LAN-only half of the tree - reached by a browser the gateway decided is on
  /// the LAN, and by no other - and it carries out and accept but not in or
  /// send. The trees each list may name, and the line of the gateway behind
  /// each of them, are written out above Check().</summary>
  internal class IpcMount : IDisposable {
    private readonly AswIPCPl _pl;
    private readonly string[] _out, _in, _accept, _send;
    private SubRec _sr;

    public readonly Topic Owner;
    public readonly string Root;

    public IpcMount(AswIPCPl pl, Topic owner, string root,
                    string outList, string inList, string acceptList, string sendList) {
      _pl = pl;
      Owner = owner;
      Root = root.TrimEnd('/');
      _out = Split(outList);
      _in = Split(inList);
      _accept = Split(acceptList);
      _send = Split(sendList);

      Check(_out, "IPC.out", OUT_TREES);
      Check(_in, "IPC.in", IN_TREES);
      Check(_accept, "IPC.accept", ACCEPT_TREES);
      Check(_send, "IPC.send", SEND_TREES);

      // A local subscription is only worth having if something local can leave.
      if(_out.Length > 0 || _send.Length > 0) {
        _sr = Owner.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.All, Changed);
      }
    }

    private static string[] Split(string s) {
      if(string.IsNullOrEmpty(s)) {
        return new string[0];
      }
      return s.Split(',').Select(z => z.Trim()).Where(z => z.Length > 0).ToArray();
    }

    /* Which trees each list may name. Not one tree per direction any more,
     * because /local/ is a legal home for two of the four - and each of the
     * four answers to one concrete line of asw_ws_gateway, not to symmetry:
     *
     *   IPC.out     a backend's PUB is accepted under /export/out/ or /local/
     *               (gateway_main.c, publish path). /local/ is where the
     *               station's own LAN-only trees live, and the gateway's
     *               comment there names a future /local/cfg/ as the case it
     *               was written for.
     *   IPC.in      a backend may SUB to /export/out/ ONLY. /local/ is LAN-only
     *               and that is decided per browser connection by
     *               getpeername(); a source the gateway reaches over TCP has no
     *               such property, so there is nothing to check it against.
     *   IPC.accept  arrives by req_prefix, which the gateway does not restrict:
     *               a browser writes /export/req/, and from the LAN /local/.
     *   IPC.send    leaves under cmd_prefix, which every backend must keep
     *               under /export/req/ or the gateway refuses to start. Were
     *               /local/ allowed, a networked source could move
     *               /local/asw/access - the lock that decides whether remote
     *               clients may write to the station at all - without ever
     *               being on the LAN.
     *
     * So a /local/ mount carries out and accept, which is exactly what a
     * configuration tree needs, and is refused in and send for reasons that
     * are not this plugin's to relax. */
    private static readonly string[] OUT_TREES = { "/export/out", "/local/" };
    private static readonly string[] IN_TREES = { "/export/out" };
    private static readonly string[] ACCEPT_TREES = { "/export/req", "/local/" };
    private static readonly string[] SEND_TREES = { "/export/req" };

    private void Check(string[] list, string field, string[] trees) {
      foreach(var f in list) {
        if(!trees.Any(z => f.StartsWith(z, StringComparison.Ordinal))) {
          // The gateway polices exactly these trees (pub_prefix, cmd_prefix,
          // and the subscription root), so an entry in the wrong one would be
          // refused there and look like silence here.
          Log.Warning("AswIPC {0}.{1}: '{2}' is under none of {3} and will never work",
                      Owner.path, field, f, string.Join(", ", trees));
        } else if(!f.StartsWith(Root, StringComparison.Ordinal)) {
          Log.Warning("AswIPC {0}.{1}: '{2}' is outside this mount's root '{3}'",
                      Owner.path, field, f, Root);
        }
      }
    }

    /// <summary>Prefixes to state with SUB on every new connection. There is no
    /// UNSUB, so this is said once per connection and never retracted.</summary>
    public IEnumerable<string> Subscriptions { get { return _in; } }

    private static bool Match(string[] list, string path) {
      for(int i = 0; i < list.Length; i++) {
        if(path.StartsWith(list[i], StringComparison.Ordinal)) {
          return true;
        }
      }
      return false;
    }

    public bool AcceptsCommand(string remotePath) { return Match(_accept, remotePath); }
    public bool AcceptsPublish(string remotePath) { return Match(_in, remotePath); }

    #region remote -> local
    /// <summary>Write a value that arrived from the station into the local tree.
    /// Owner is passed as the originator so Changed() below can ignore it:
    /// without that, every value we accept would go straight back out as our
    /// own and the two ends would trade the same number forever.</summary>
    public bool Apply(string remotePath, string payload) {
      if(!remotePath.StartsWith(Root, StringComparison.Ordinal)) {
        return false;
      }
      string rel = remotePath.Substring(Root.Length).TrimStart('/');
      try {
        var val = JsLib.ParseJson(payload);
        var t = rel.Length == 0 ? Owner : Owner.Get(rel, true, Owner);
        if(t.CheckAttribute(Topic.Attribute.Internal)) {
          return false;
        }
        t.SetState(val, Owner);
        if(_pl.verbose) {
          Log.Debug("AswIPC R {0} = {1}", remotePath, payload);
        }
        return true;
      }
      catch(Exception ex) {
        Log.Warning("AswIPC {0} = {1} - {2}", remotePath, payload, ex.Message);
        return false;
      }
    }
    #endregion remote -> local

    #region local -> remote
    private void Changed(TopicEvent p, SubRec sr) {
      // Ours coming back to us. Apply() sets state with Owner as the originator
      // precisely so this test can tell the two apart.
      if(p.Author == Owner) {
        return;
      }
      // Changes only. The MQTT plugin also acts on EventKind.Snapshot, which
      // the repository raises once per existing topic when a subscription is
      // created - a replay of the current tree. That is wrong here: on a send
      // list those values would go out as CMD the moment the mount appears,
      // before any gateway has connected, so they would be logged as dropped
      // and lost. Current values reach a peer through Snapshot() when it
      // connects, which is the point at which anyone can receive them.
      //
      // Requests are deliberately not replayed on connect either: re-asserting
      // a band every time the link came back would let a reconnection change
      // the station's state on its own.
      if(p.Kind != EventKind.StateChanged) {
        return;
      }
      if(p.Source.CheckAttribute(Topic.Attribute.Internal)) {
        return;
      }
      string remote, payload;
      if(!Encode(p.Source, out remote, out payload)) {
        return;
      }
      if(Match(_out, remote)) {
        _pl.Publish(remote, payload);
      } else if(Match(_send, remote)) {
        _pl.Request(remote, payload);
      } else {
        return;
      }
      if(_pl.verbose) {
        Log.Debug("AswIPC S {0} = {1}", remote, payload);
      }
    }

    private bool Encode(Topic t, out string remote, out string payload) {
      remote = null;
      payload = null;
      if(!t.path.StartsWith(Owner.path, StringComparison.Ordinal)) {
        return false;
      }
      var st = t.GetState();
      if(st == null || !st.Defined) {
        // A topic that exists but was never given a value serialises to
        // nothing, and the far end would read a field with no content.
        return false;
      }
      string s = JsLib.Stringify(st);
      if(s == null) {
        return false;
      }
      // The wire is one line, tab separated. A raw tab or newline would not
      // corrupt the line, it would SPLIT it, and the far end would read the
      // remainder as a new command. JSON escapes both, so this can only fire if
      // Stringify ever stops being the encoder.
      if(s.IndexOf('\t') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) {
        Log.Warning("AswIPC {0}: value contains a separator, not sent", t.path);
        return false;
      }
      remote = Root + t.path.Substring(Owner.path.Length);
      payload = s;
      return true;
    }

    /// <summary>Everything we own, resent on demand. Answers a peer's GETSNAP
    /// and its first SUB, so a gateway that has just (re)connected sees the
    /// current azimuths instead of waiting for the next movement.</summary>
    public void Snapshot(IpcLink link) {
      if(_out.Length == 0) {
        return;
      }
      foreach(Topic t in Owner.all) {
        if(t.CheckAttribute(Topic.Attribute.Internal)) {
          continue;
        }
        string remote, payload;
        if(Encode(t, out remote, out payload) && Match(_out, remote)) {
          link.Publish(remote, payload);
        }
      }
    }
    #endregion local -> remote

    public override string ToString() {
      return string.Format("{0} <-> {1} [out={2} in={3} accept={4} send={5}]",
        Owner.path, Root, string.Join(";", _out), string.Join(";", _in),
        string.Join(";", _accept), string.Join(";", _send));
    }

    public void Dispose() {
      var sr = Interlocked.Exchange(ref _sr, null);
      if(sr != null) {
        sr.Dispose();
      }
    }
  }
}
