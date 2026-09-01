///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // One topic's live value, for the Chart document to draw as it arrives. The archive behind the
  // chart comes over HTTP from /api/archivist; this is the other half - what has happened since
  // the last answer - and it exists because the IDE socket had no way to watch a single topic:
  // every live value on it rode a row belonging to some open tree, so a chart had to poll.
  //
  // Deliberately NOT the Inspector's State view, which also carries a topic's own value: its
  // controller is keyed by Topic inside the provider, so a chart and an open Inspector on the
  // same topic would share one, and either side's req.close would silently stop the other's
  // updates. An object-valued topic would also arrive here as a display summary string rather
  // than a value.
  //
  // The name matches the document that consumes it, the way "catalog" already names both a
  // document and a vid-view. The view itself knows nothing about charts.
  internal sealed class ChartViewProvider : ViewProviderBase {
    private const string ViewName = "chart";
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    private readonly Dictionary<string, SubRec> _watches;
    // Engine thread only. A callback queued before the session closed still runs afterwards.
    private bool _disposed;

    /// <param name="post">Queues work for the engine thread; null means "run it here, now",
    /// which is what the tests use.</param>
    public ChartViewProvider(Action<JSC.JSObject> send, Action<string, Action> post = null) {
      _send = send;
      _post = post ?? ((what, work) => work());
      _watches = new Dictionary<string, SubRec>(StringComparer.Ordinal);
    }

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    /// <summary>Starts watching the topic named by <paramref name="vid"/>.</summary>
    /// <remarks>SubMask.Once earns its place twice over. It is what makes the subscription see the
    /// topic's OWN changedState rather than only its children's (Topic.I.Subscribe), and it is
    /// what makes Repo enqueue the subscribe Perform itself (Repo.Tick) - which is how the current
    /// value reaches the client, with no separate read and no second code path to keep in step
    /// with the update one.
    /// <para>Re-opening an already-watched vid drops the old watch and subscribes again rather
    /// than returning early. Returning early would answer a second req.open with no evnt.add at
    /// all, leaving a client that reconnected and re-opened with a row it never received; this way
    /// there is still exactly one subscription per vid, and an Open always delivers a value.</para></remarks>
    public override ViewOpResult Open(string vid, string view) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }
      Drop(vid);
      _watches[vid] = topic.Subscribe(SubRec.SubMask.Value | SubRec.SubMask.Once, (perform, sub) => OnChanged(vid, perform));
      return ViewOpResult.Open(ViewName, vid, topic.name);
    }

    public override ViewOpResult Close(string vid) {
      Drop(vid);
      return ViewOpResult.Success();
    }

    public override void Dispose() {
      _disposed = true;
      foreach(SubRec sub in _watches.Values) sub.Dispose();
      _watches.Clear();
    }

    private void Drop(string vid) {
      SubRec sub;
      if(vid == null || !_watches.TryGetValue(vid, out sub)) return;
      _watches.Remove(vid);
      sub.Dispose();
    }

    private void OnChanged(string vid, Perform perform) {
      _post("callback " + vid, () => OnChangedCore(vid, perform));
    }

    private void OnChangedCore(string vid, Perform perform) {
      try {
        if(_disposed || perform == null || perform.src == null) return;
        // Closed while this callback sat on the pump - the watch is already gone and the client
        // has already been told it may forget the row.
        if(!_watches.ContainsKey(vid)) return;
        switch(perform.Art) {
        case Perform.E_Art.subscribe:
          // The first packet has to be an add: ViewStore.update returns without doing anything
          // for a vid it holds no row for (view-store.js), so an upd here would be dropped.
          SendValue(ViewMessageTypes.EvntAdd, vid, perform.src);
          break;
        case Perform.E_Art.changedState:
          SendValue(ViewMessageTypes.EvntUpd, vid, perform.src);
          break;
        case Perform.E_Art.remove:
          Drop(vid);
          _send(ViewProtocolSerializer.Del(vid));
          break;
        }
        // subAck falls through on purpose: it acknowledges the subscription, it is not a value.
      }
      catch(Exception ex) {
        // Full stack: an unexpected fault inside a tick callback is exactly the case where the
        // message alone does not identify what produced it.
        Log.Warning("ChartViewProvider.OnChanged({0}) - {1}", vid, ex.ToString());
      }
    }

    /// <summary>Sends the topic's value, every time, without diffing.</summary>
    /// <remarks>JsonTreeControllerBase.SendUpd deliberately sends nothing when a row is unchanged;
    /// that is right for a tree and wrong here. Writing the same number again is a sample like any
    /// other and has to become a point - the dashboard chart gets this for free, since symbiote's
    /// Data.pub has no equality check either. Nothing is remembered per row, which is also why
    /// this provider needs no ViewTargetRegistry at all.</remarks>
    private void SendValue(string type, string vid, Topic topic) {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(type, vid);
      if(type == ViewMessageTypes.EvntAdd) {
        dto["level"] = new JSL.Number(0);
        dto["name"] = topic.name ?? string.Empty;
      }
      JSC.JSValue state = topic.GetState();
      dto["value"] = state == null || !state.Defined ? JSC.JSValue.Null : RowProjector.ToWebStateValue(state);
      _send(dto);
    }
  }
}
