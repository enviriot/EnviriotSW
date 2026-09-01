///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using X13.Repository;

namespace X13.WebUI.Helpers {
  // One connected client, represented in the tree by a topic under /$YS/WebUI/clients.
  //
  // The topic is not decoration: it is the Perform.Prim this session's writes carry, and Prim
  // is the only thing a subscriber can compare against to tell its own echo from someone
  // else's change (the repository does not filter delivery - every plugin filters itself, see
  // ArchivistPl.SubFunc). Without a topic per session there is nothing to compare.
  //
  // Named ClientSession, not Session as in H04: ViewSession lives next door.
  //
  // Engine thread only. EnsureOwner and Dispose touch the repository, and both are reached
  // through WebUiHost.Post - which also removes concurrent teardown as a concept, the same way
  // it did for ViewSession.
  internal sealed class ClientSession : IDisposable {
    private const string ClientsPath = "/$YS/WebUI/clients";

    private readonly Action<string, Action> _post;
    private Topic _owner;
    private string _host;
    private int _renamed;

    public readonly string id;
    public readonly IPAddress ip;

    // Authentication is not implemented; kept so log lines and ToString read the same as H04's.
#pragma warning disable 649
    public string userName;
#pragma warning restore 649

    public ClientSession(IPAddress address, Action<string, Action> post) {
      id = Guid.NewGuid().ToString();
      ip = address ?? IPAddress.None;
      _host = "[" + ip.ToString() + "]";
      _post = post ?? ((what, work) => work());
    }

    /// <summary>The session's topic, or null while it has not been needed yet.</summary>
    public Topic owner { get { return _owner; } }

    /// <summary>Creates the session's topic if it does not exist yet. Engine thread.</summary>
    /// <remarks>Deliberately lazy and deliberately explicit. /api/dashboard accepts anyone -
    /// access is decided per topic, not at the door - so creating a topic in OnOpen would let
    /// any peer on the network fill /$YS/WebUI/clients just by connecting. The dashboard calls
    /// this only once a frame has passed DashboardAcl; the IDE, already gated by network,
    /// calls it on open.</remarks>
    public Topic EnsureOwner() {
      if(_owner != null) return _owner;

      Topic clients = Topic.root.Get(ClientsPath);
      _owner = clients.Get(UniqueName(clients, ip.ToString()));
      _owner.ClearAttribute(Topic.Attribute.Saved);
      _owner.SetState(_host);
      StartHostLookup();
      return _owner;
    }

    /// <summary>Removes every session topic left behind by a previous run. Engine thread.</summary>
    /// <remarks>Dispose already removes a session's own topic, so a client that disconnects
    /// cleanly leaves nothing behind - but a kill, a crash and even an ordinary shutdown all
    /// miss it: WebUiPl.Stop closes the sockets, and the disposal their OnClose queues is never
    /// pumped (Tick has stopped, and Repo.Stop exports the config rather than dispatching what
    /// is left in the queue).
    ///
    /// Those topics then come BACK on the next start, which is what makes them pile up instead
    /// of merely outliving one run. The state does not survive - EnsureOwner clears Saved, so
    /// LiteDB_Pl drops the state row - but a topic's MANIFEST is written whatever its
    /// attributes are, and Load recreates a topic for every manifest row it finds. One dead run
    /// adds its clients to the ones the run before it left.
    ///
    /// Called from WebUiPl.Start before the host opens its port: no socket can be open yet, so
    /// everything under clients is by definition stale and nothing live is swept up with it.</remarks>
    internal static void PurgeStale() {
      Topic clients = Topic.root.Get(ClientsPath, false);
      if(clients == null) return;
      // Materialized: Remove marks the topic disposed and queues the removal, and walking the
      // live child collection while doing that is the kind of thing that works until it does not.
      foreach(Topic stale in clients.children.ToArray()) stale.Remove();
    }

    /// <summary>Resolves the peer's name off the engine thread, then renames the topic.</summary>
    /// <remarks>H04 called Dns.GetHostEntry straight from the socket thread inside the session
    /// constructor, so every connection paid the reverse lookup - up to seconds of it, on a
    /// thread that had a client waiting. The topic now appears immediately under the address
    /// and moves to the host name later, if one comes back at all.</remarks>
    /// <summary>The reverse lookup itself. Null disables renaming altogether.</summary>
    /// <remarks>A seam for tests, which set it to null: the topic then keeps the name it was
    /// created under, so assertions about that name are deterministic, and nothing posts repository
    /// work from a pool thread while the test drives the engine thread itself.</remarks>
    internal static Func<IPAddress, string> HostNameResolver = address => Dns.GetHostEntry(address).HostName;

    private void StartHostLookup() {
      Func<IPAddress, string> resolve = HostNameResolver;
      if(resolve == null || Interlocked.Exchange(ref _renamed, 1) != 0) return;
      IPAddress address = ip;
      Task.Run(() => {
        string hostName;
        try {
          hostName = resolve(address);
        }
        catch(Exception) {
          return;  // no reverse record: the address-based name it already has is the answer
        }
        string label = (hostName ?? string.Empty).Split('.')[0];
        if(string.IsNullOrEmpty(label)) return;
        _post("client session rename " + label, () => Rename(label, hostName));
      });
    }

    private void Rename(string label, string hostName) {
      Topic topic = _owner;
      if(topic == null || topic.disposed) return;
      Topic clients = Topic.root.Get(ClientsPath);
      _host = string.Format("{0}[{1}]", hostName, ip);
      topic.Move(clients, UniqueName(clients, label));
      topic.SetState(_host);
    }

    private static string UniqueName(Topic parent, string prefix) {
      int i = 1;
      while(parent.Exist(prefix + i.ToString())) i++;
      return prefix + i.ToString();
    }

    public override string ToString() {
      return (string.IsNullOrEmpty(userName) ? "anonymus" : userName) + "@" + _host;
    }

    public void Dispose() {
      Topic topic = Interlocked.Exchange(ref _owner, null);
      if(topic != null && !topic.disposed) topic.Remove();
    }
  }
}
