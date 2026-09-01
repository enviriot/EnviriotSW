///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Extensions;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // Read-only live view over one Logram diagram (a Core/Logram-typed topic). Reads
  // the same manifest/state conventions ES/Logram/LogramItems.cs draws from directly
  // (Logram.top/left for position, cctor.LoBind for wires, the resolved type's
  // Children.<pin>.ddr for pin schema/direction) - there is no separate diagram
  // format, see todo.md. One instance per open Logram document (mirrors
  // InspectorChildrenViewProvider's per-document TopicTreeController).
  //
  // Deliberately NOT built on TopicTreeController: Logram always shows the whole
  // diagram at once (no client-driven expand/collapse), is a fixed two-level tree
  // (diagram -> elements -> pins), and needs a graph-wide routing pass (see
  // LogramWireRouter) that has no equivalent in the generic tree machinery.
  internal sealed class LogramGraphController : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    private readonly Topic _root;
    private readonly string _viewName;
    private SubRec _sub;
    // No locks and no volatile: every path into this class - the subscription callback, the
    // snapshot flush, Dispose - now arrives through the session's queue and therefore runs on
    // the engine thread. The two locks that used to live here (_stateGate over the "what the
    // client already has" collections, _resendGate over the debounce timer) existed solely
    // because that timer fired SendSnapshot on a threadpool thread while OnChanged ran on the
    // repository thread. There is no second thread left to race with.
    private bool _disposed;
    // Set when a rebuild is owed, cleared by the flush that performs it - what replaced the
    // 30 ms debounce timer.
    private bool _snapshotDirty;
    // Guards the rebuild against nesting. Belongs here rather than on FlushSnapshot because
    // Open() calls SendSnapshot directly, which would leave that entry unguarded.
    private bool _sending;
    // Takes the controller, not its vid: the provider needs to remove the entry only if it is
    // still THIS controller's, and a vid cannot express that - a late callback from a torn-down
    // controller would evict the live replacement a concurrent Open stored for the same diagram.
    // See LogramViewProvider.ForgetRoot, which looks the entry up by Root and then compares.
    private readonly Action<LogramGraphController> _onRootGone;

    /// <param name="post">Queues work for the engine thread; null means "run it here, now".</param>
    internal LogramGraphController(Action<JSC.JSObject> send, Topic root, string viewName, Action<LogramGraphController> onRootGone = null, Action<string, Action> post = null) {
      _post = post ?? ((what, work) => work());
      _send = send;
      _root = root;
      _viewName = string.IsNullOrEmpty(viewName) ? "logram" : viewName;
      _onRootGone = onRootGone;
    }

    internal string RootVid { get { return Vid(_root); } }
    /// <summary>The diagram this controller renders - what LogramViewProvider keys _open by.</summary>
    /// <remarks>A Topic reference survives Topic.Move untouched (Topic.I.UpdatePath rewrites
    /// _path in place), which is exactly what RootVid does not - see _sentRootVid below.</remarks>
    internal Topic Root { get { return _root; } }

    internal void Open() {
      _sub = _root.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value | SubRec.SubMask.Field, OnChanged);
      SendSnapshot();
    }

    // Quiesce() is gone. It stopped the debounce timer and then waited out a snapshot the
    // threadpool had already dequeued, because app-shell.js dispatches on the vid prefix alone
    // (`vid.startsWith('logram#')` -> the ACTIVE document's store), so rows from a diagram the
    // user just closed landed in whichever one they opened next. Nothing runs in parallel now, so
    // the flag is the whole of it: work queued earlier and dequeued later sees it and returns.
    public void Dispose() {
      _disposed = true;
      _sub?.Dispose();
      _sub = null;
    }

    // Queued whole - see TopicTreeController.OnTopicChanged.
    private void OnChanged(Perform p, SubRec sub) {
      _post("callback " + (p == null || p.src == null ? RootVid : p.src.path), () => OnChangedCore(p, sub));
    }

    private void OnChangedCore(Perform p, SubRec sub) {
      try {
        // Disposing the subscription raises events that come straight back here, and
        // HandleRootRemoved disposes it from inside this method. Quiesce used to make that
        // unreachable by waiting the callback out under _stateGate.
        if(_disposed) return;
        if(p == null || p.src == null) return;
        // The diagram's own topic was deleted (e.g. from Workspace) - mirrors
        // TopicTreeController.HandleRootRemoved. SendSnapshot never notices this on
        // its own: it unconditionally treats RootVid as still current, so without
        // this the client would be left showing a diagram whose topic is gone,
        // with nothing to route it back to the parent (see app-shell.js
        // #onDocumentRootDeleted). Must be checked before the changedState branch
        // below, since a self-remove is also p.src == _root.
        if(p.Art == Perform.E_Art.remove && p.src == _root) {
          HandleRootRemoved();
          return;
        }
        // Live pin/variable value - cheap path, no graph rebuild (this is the
        // hot/frequent event as a running diagram ticks; everything else - topology
        // edits - is rare enough that a full resend is an acceptable trade-off for
        // simplicity). Sends color, not the raw value - see SendValueUpdate - and
        // only for something the client actually draws a dot for: a pin (grandchild -
        // parent isn't _root) or a variable (direct child whose type declares no pin
        // schema, see ChildrenSchema). A block's own state (if it even has one) has
        // no consumer client-side, so it's silently dropped rather than sent for
        // nothing. _knownVids guards a topic whose own first SendSnapshot hasn't
        // gone out yet.
        if(p.Art == Perform.E_Art.changedState) {
          // The _knownVids test and the update it gates have to be one atomic step - a
          // snapshot running concurrently rewrites that set wholesale (see _stateGate).
          if(p.src == _root || !_knownVids.Contains(Vid(p.src))) return;
          bool isPin = p.src.parent != null && p.src.parent != _root;
          if(isPin) SendValueUpdate(p.src, true);
          else if(ChildrenSchema(ResolveTypeState(p.src)) == null) SendValueUpdate(p.src, false);
          return;
        }
        // Pin context menu's "Trace" toggle (LogramPaletteBuilder.BuildPinMenu,
        // LogramViewProvider.ExecuteRpc "trace") - same cheap direct-update reasoning
        // as changedState above: it's purely cosmetic (never affects position/pins/
        // wires), so a full ScheduleSnapshot rebuild would be wasted work, and
        // wouldn't even resend the pin's row anyway (SendSnapshot's pin loop below
        // only ever sends a pin ONCE, on _knownVids's first sight of it - see the
        // comment there). _knownVids.Contains guards against a pin whose very first
        // SendSnapshot hasn't gone out yet (falls through to the generic
        // ScheduleSnapshot path below instead, same as any other field change).
        if(p.Art == Perform.E_Art.changedField && string.Equals(p.FieldPath, "Logram.trace", StringComparison.Ordinal)) {
          if(_knownVids.Contains(Vid(p.src))) {
            SendTraceUpdate(p.src);
            return;
          }
          // Not known yet - fall through to the generic ScheduleSnapshot path below,
          // same as any other field change.
        }
        // Subscribe() itself synchronously delivers one subscribe/subAck callback
        // (Topic.I.Publish's special-cased branch) before Open() calls SendSnapshot()
        // explicitly - ignore those (and unsubscribe) here to avoid a redundant
        // initial double-send; only react to genuine topology changes.
        if(p.Art == Perform.E_Art.create || p.Art == Perform.E_Art.remove || p.Art == Perform.E_Art.changedField || p.Art == Perform.E_Art.move) {
          ScheduleSnapshot();
        }
      }
      catch(Exception ex) {
        Log.Warning("LogramGraphController({0}).OnChanged - {1}", _root.path, ex.ToString());
      }
    }

    private void HandleRootRemoved() {
      string vid = RootVid;
      _disposed = true;   // was Quiesce() - see Dispose
      _send(ViewProtocolSerializer.Del(vid));
      _sub?.Dispose();
      _sub = null;
      // Tell the provider to forget us, the same release the three Inspector providers got in
      // Part 9 (JsonTreeControllerBase/TopicTreeController's onRootGone). Logram missed it
      // because it is not built on TreeViewProviderBase, so LogramViewProvider._open kept a
      // disposed controller - and any later req.* for that vid was dispatched into it instead
      // of returning a clean topic_not_found.
      if(_onRootGone != null) _onRootGone(this);
    }

    // Debounced: removing one topic fans out into a separate remove Perform for
    // every descendant (Repo.cs TickStep1: `foreach(Topic tmp in c.src.all) ...`),
    // each independently matching this deep (SubMask.All) subscription - deleting a
    // block with N pins would otherwise fire N+1 back-to-back full snapshots for one
    // logical delete. Any burst of structural changes arriving within the debounce
    // window collapses into a single resend once things settle.
    // Coalescing is structural now instead of timed: this runs inside a queued action, so the
    // flush it posts lands on the NEXT pump pass (Pump is bounded to what was queued on entry).
    // Every change in a tick posts its own flush; the first to run rebuilds and clears the flag,
    // the rest find it clean and return - so a burst still costs one routing pass. It is also
    // what keeps the rebuild out of the repository's own dispatch, which is what the timer used
    // to do by being asynchronous.
    private void ScheduleSnapshot() {
      if(_disposed) return;
      _snapshotDirty = true;
      _post("snapshot " + RootVid, FlushSnapshot);
    }

    // Cleared before the rebuild, so a change caused during it re-arms the flag instead of being
    // swallowed. SafeSendSnapshot is gone with the timer: an exception here used to be unhandled
    // on a threadpool thread and had already taken this server down once - now it surfaces in a
    // queued action, and WebUiHost.Pump names it and carries on with the rest of the queue.
    private void FlushSnapshot() {
      if(_disposed || !_snapshotDirty) return;
      _snapshotDirty = false;
      SendSnapshot();
    }

    // Rows the client currently has for this diagram, so a rebuild can tell it to
    // drop whatever no longer exists - see the evnt.del loop below. Pins never carry
    // any mutable identity field (direction/index are fixed once created; live value
    // rides SendValueUpdate separately), so membership in this set is enough to know
    // whether a pin still needs sending at all - only elements need the finer
    // position/size diff in _sentElementLayout below.
    private readonly HashSet<string> _knownVids = new HashSet<string>();
    /// <summary>What the client already has for one row, by the row's vid.</summary>
    /// <remarks>Was five parallel Dictionary&lt;string,string&gt; under one and the same key
    /// (element/pin fingerprint, colour, displayValue, wire fingerprint), which cost four
    /// textually identical stale sweeps and one invariant held by a comment - see MarkRowSent.
    /// <para>The two fingerprints are one field, not two: element vids and pin vids are disjoint
    /// (an element is a child of the diagram, a pin a grandchild), so no row ever carries both.
    /// <para>A null field means "never sent". Nothing legitimately sends null - ColorForValue
    /// returns a literal on every branch and the fingerprints are concatenations - so absence
    /// stays distinguishable from a sent empty value without a separate flag.</para>
    /// <para>A block row uses Fingerprint alone. That is not padding: a block has no colour of
    /// its own by design (see SerializeElementRow) and its wires hang on its pins.</para></remarks>
    private sealed class SentRow {
      // Identity+geometry of the row as last sent. Geometry alone was not enough: `editor`
      // follows IsBlock, `icon` and `name` follow the topic, and a pin's pinDirection/pinIndex
      // are derived from the type's Children.<pin>.ddr - all of which move when an element is
      // retyped or a type's schema is edited, while X/Y/W/H can stay put. The pin side
      // additionally used to send each row exactly once, so those fields could never be
      // corrected at all.
      public string Fingerprint;
      // Last colour/displayValue actually sent - SendValueUpdate diffs against these instead of
      // sending on every single changedState tick, since most value changes (e.g. a positive
      // counter incrementing) don't cross a ColorForValue bucket boundary and would repaint
      // nothing on screen either way.
      public string Color;
      public string DisplayValue;
      // Route as last sent (sourceVid|sourceLocal|path points) - the same diffing applied to
      // wires: routing recomputes for EVERY bound pin on EVERY structural change (a route can be
      // blocked by any element, not just its own two endpoints), but most of those
      // recomputations land on the exact same result.
      public string WireFingerprint;
    }
    private readonly Dictionary<string, SentRow> _sent = new Dictionary<string, SentRow>(StringComparer.Ordinal);

    /// <summary>The record for vid, creating it if this is the row's first send.</summary>
    private SentRow Sent(string vid) {
      SentRow row;
      if(!_sent.TryGetValue(vid, out row)) {
        row = new SentRow();
        _sent[vid] = row;
      }
      return row;
    }

    /// <summary>The record for vid, or null when nothing has been sent for it.</summary>
    private SentRow SentOrNull(string vid) {
      SentRow row;
      return _sent.TryGetValue(vid, out row) ? row : null;
    }

    /// <summary>Records that a whole row was just (re)sent as an evnt.add.</summary>
    /// <remarks>Clearing the wire is the point, and it is why this is a method rather than an
    /// assignment: an evnt.add REPLACES the row client-side (ViewStore.add splices it out),
    /// taking the wire fields ResolveWires had applied by evnt.upd with it, so the route has to
    /// be re-stated by the pass at the end of the same snapshot. Geometry-only changes used to be
    /// self-healing (the anchor cell moved too, so the wire changed anyway), but an identity-only
    /// change would have silently erased the wire for good. This used to be a five-line comment
    /// and a hand-written Remove into a second dictionary, repeated at both call sites.</remarks>
    private void MarkRowSent(string vid, string fingerprint) {
      SentRow row = Sent(vid);
      row.Fingerprint = fingerprint;
      row.WireFingerprint = null;
    }

    // Rebuild: recomputes the whole graph (cheap, no network cost) but only sends
    // element/pin rows that are actually new or changed - a drag commit's
    // Logram.top/left write touches ONE element, and previously this resent every
    // other unrelated element/pin's identity row too on every single drag tick.
    // Wire routing genuinely does need a full graph-wide recompute (a path can be
    // blocked by ANY element, not just the two endpoints - see LogramWireRouter), so
    // ResolveWires still runs unconditionally every time, but that only emits
    // evnt.upd for pins/variables that actually have a binding, which is already
    // cheap. What must never be skipped is telling the client about anything that
    // stopped existing (a deleted block/pin) - evnt.add/upd alone never removes a
    // stale row, so without the diff below a deleted element would keep rendering
    // forever (the client has no other way to learn it's gone).
    // The RootVid the canvas row was last sent under - NOT a one-shot flag. RootVid is derived
    // from _root.path, and Topic.Move rewrites that in place, so renaming the diagram (or any
    // folder above it) silently changes every vid this controller emits. With a plain latch the
    // canvas row was never re-sent under the new vid while the stale-row sweep still deleted the
    // old one - and since ViewStore.remove splices out every following row of a higher level, a
    // level-0 delete wiped the entire diagram the same snapshot had just rebuilt. The document
    // then stayed blank forever, because the caches below already held the new vids.
    private string _sentRootVid;

    private void SendSnapshot() {
      // A flush queued before Dispose can still be dequeued after it.
      if(_disposed || _sending) return;
      _sending = true;
      try {
        SendSnapshotCore();
      }
      finally {
        _sending = false;
      }
    }

    private void SendSnapshotCore() {
      string rootVid = RootVid;
      HashSet<string> currentVids = new HashSet<string>();
      currentVids.Add(rootVid);

      List<Topic> elements = new List<Topic>();
      // Visit order matters: RoutingPass accumulates its edge/cell claims as elements are
      // walked, so wires routed later see what earlier ones took. Topic.children already
      // supplies a name-ordered walk - Bill's two-argument ctor passes sorted:true, and only
      // the three-argument one can turn that off - so nothing extra is needed here, but the
      // dependency is real and this is the place that would notice if that default changed.
      foreach(Topic child in _root.children) elements.Add(child);

      List<ElementLayout> layouts = new List<ElementLayout>(elements.Count);
      foreach(Topic el in elements) layouts.Add(BuildLayout(el));

      foreach(ElementLayout layout in layouts) {
        currentVids.Add(Vid(layout.Topic));
        if(layout.IsBlock) {
          foreach(PinLayout pin in layout.Pins) currentVids.Add(Vid(pin.Topic));
        }
      }

      // Deletions BEFORE additions, which matters only in the rename case but is decisive
      // there: the stale set then still holds the old root, and ViewStore.remove would
      // otherwise cascade that level-0 delete over every row this snapshot had just added.
      foreach(string staleVid in _knownVids) {
        if(!currentVids.Contains(staleVid)) _send(ViewProtocolSerializer.Del(staleVid));
      }

      if(!string.Equals(_sentRootVid, rootVid, StringComparison.Ordinal)) {
        _send(SerializeCanvasRow());
        _sentRootVid = rootVid;
      }

      foreach(ElementLayout layout in layouts) {
        string vid = Vid(layout.Topic);
        string fingerprint = ElementFingerprint(layout);
        SentRow previous = SentOrNull(vid);
        if(previous == null || previous.Fingerprint != fingerprint) {
          _send(SerializeElementRow(layout));
          MarkRowSent(vid, fingerprint);
        }

        if(layout.IsBlock) {
          foreach(PinLayout pin in layout.Pins) {
            string pinVid = Vid(pin.Topic);
            string pinFingerprint = PinFingerprint(pin);
            SentRow previousPin = SentOrNull(pinVid);
            if(!_knownVids.Contains(pinVid) || previousPin == null || previousPin.Fingerprint != pinFingerprint) {
              _send(SerializePinRow(layout, pin));
              MarkRowSent(pinVid, pinFingerprint);
            }
          }
        }
      }

      _knownVids.Clear();
      foreach(string vid in currentVids) _knownVids.Add(vid);
      // One sweep. This was four textually identical copies - element, pin, wire, colour - each
      // walking its own dictionary with the very same predicate against the very same
      // currentVids, and the colour one already removing two dictionaries at once.
      List<string> stale = new List<string>();
      foreach(string vid in _sent.Keys) {
        if(!currentVids.Contains(vid)) stale.Add(vid);
      }
      foreach(string vid in stale) _sent.Remove(vid);

      ResolveWires(layouts);
    }

    // Sends color (always) and displayValue (only when traced, and only for
    // something Trace actually applies to - a variable has no menu/flag for it, see
    // BuildPinMenu) instead of the raw value: logram-document.js only ever turned
    // value into a dot fill or a Trace label, never anything else (checked every
    // consumer before doing this), so computing that once here and diffing against
    // the record's Color/DisplayValue means most ticks - anything that doesn't cross a
    // ColorForValue bucket, or change the formatted text - send nothing at all.
    private void SendValueUpdate(Topic topic, bool supportsTrace) {
      string vid = Vid(topic);
      JSC.JSValue state = RowProjector.ToWebStateValue(topic.GetState());
      string color = ColorForValue(state);
      bool traced = supportsTrace && topic.GetField("Logram.trace").AsBool(false);
      string displayValue = traced ? FormatTraceValue(state) : null;

      SentRow sent = SentOrNull(vid);
      bool colorChanged = sent == null || sent.Color != color;
      bool displayChanged = traced && (sent == null || sent.DisplayValue != displayValue);
      if(!colorChanged && !displayChanged) return;

      // Only the field that actually moved goes out - unlike SendTraceUpdate's
      // `trace` (which must always restate itself, see the comment there), the
      // client just keeps whatever color/displayValue it already has for any field
      // not present in the patch (ViewStore.update() only touches keys it's given).
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, vid);
      if(colorChanged) dto["color"] = color;
      if(displayChanged) dto["displayValue"] = displayValue;
      _send(dto);

      SentRow row = Sent(vid);
      if(colorChanged) row.Color = color;
      if(displayChanged) row.DisplayValue = displayValue;
    }

    // Unlike SendAdd's fields (suppressed when at their default - see SerializePinRow),
    // an evnt.upd always states the field outright, defaulted or not: this is the ONLY
    // update a toggle-back-to-off ever gets, so omitting `false` here would leave the
    // client showing a stale value label forever. Turning trace ON also needs a fresh
    // displayValue right away - SendValueUpdate's own diffing wouldn't otherwise emit
    // one until the value next actually ticks, leaving the label blank until then.
    private void SendTraceUpdate(Topic pin) {
      string vid = Vid(pin);
      bool traced = pin.GetField("Logram.trace").AsBool(false);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, vid);
      dto["trace"] = traced;
      if(traced) {
        string displayValue = FormatTraceValue(RowProjector.ToWebStateValue(pin.GetState()));
        dto["displayValue"] = displayValue;
        Sent(vid).DisplayValue = displayValue;
      }
      _send(dto);
    }

    // Everything the client is told about an element other than its live colour, which
    // the record's Color/SendValueUpdate track separately - putting colour in here would resend the
    // whole row on every value tick. Mirrors SerializeElementRow field for field.
    private string ElementFingerprint(ElementLayout layout) {
      return layout.X + "|" + layout.Y + "|" + layout.WidthCells + "|" + (layout.IsBlock ? layout.HeightCells : 1)
        + "|" + (layout.IsBlock ? EditorBlock : EditorVariable)
        + "|" + (IconResource.Resolve(layout.Topic, null) ?? string.Empty)
        + "|" + (layout.Topic.name ?? string.Empty);
    }

    // Trace and colour are deliberately absent for the same reason - SendTraceUpdate and
    // SendValueUpdate own those, and folding them in here would turn every toggle and every
    // value tick into a full row replacement.
    private static string PinFingerprint(PinLayout pin) {
      return pin.Direction + "|" + pin.Index + "|" + (pin.Topic.name ?? string.Empty);
    }

    // === Layout resolution (position + pin schema, no wire info yet) ===

    private ElementLayout BuildLayout(Topic el) {
      JSC.JSValue typeState = ResolveTypeState(el);
      JSC.JSValue children = ChildrenSchema(typeState);
      ElementLayout layout = new ElementLayout() {
        Topic = el,
        X = Cell(el.GetField("Logram.left")),
        Y = Cell(el.GetField("Logram.top")),
        IsBlock = children != null,
      };

      if(layout.IsBlock) {
        int inCount = 0, outCount = 0;
        int inMaxNameLen = 0, outMaxNameLen = 0;
        foreach(Topic pinTopic in el.children) {
          int ddr;
          if(!TryGetDdr(children, pinTopic.name, out ddr)) continue;
          bool isInput = ddr < 0;
          // ddr comes from the type descriptor and nothing validates it, while the index it
          // yields drives HeightCells and therefore both the routing grid's extent and, since
          // BlockedCells materialises every covered cell, the size of that set. Clamped for the
          // same reason Logram.top/left are (see Cell): an absurd ddr would otherwise push y
          // past CellKey's 16-bit field and alias unrelated cells.
          int index = Math.Min(MaxPinIndex, isInput ? (-ddr - 1) : (ddr - 1));
          if(isInput) {
            inCount = Math.Max(inCount, index + 1);
            inMaxNameLen = Math.Max(inMaxNameLen, pinTopic.name.Length);
          }
          else {
            outCount = Math.Max(outCount, index + 1);
            outMaxNameLen = Math.Max(outMaxNameLen, pinTopic.name.Length);
          }
          layout.Pins.Add(new PinLayout() {
            Topic = pinTopic,
            Direction = isInput ? "in" : "out",
            Index = index,
          });
        }
        layout.HeightCells = Math.Max(1, Math.Max(inCount, outCount));
        // Rough character-count size estimate (not WPF font metrics) - this is the
        // single source of truth for both wire-routing occupancy AND what the client
        // draws (sent verbatim as width/height on the element row, see
        // SerializeElementRow) - client and server must agree exactly, or routed
        // wires visibly miss the pins. 1 cell in the middle is reserved for the icon;
        // the instance's own name no longer factors in, only the pin labels do.
        int bodyWidthCells = Math.Max(3, (inMaxNameLen * 2 + 2) / 5 + 1 + (outMaxNameLen * 2 + 2) / 5);
        // A block's output pins sit one cell past the body (see GetPinCell) - baked in
        // here so WidthCells is the element's whole footprint (matching a variable's,
        // where WidthCells already reaches the output pin) and IsBlocked/GetPinCell
        // don't each need their own +1 for it.
        layout.WidthCells = bodyWidthCells + 1;
      }
      else {
        int nameLen = el.name != null ? el.name.Length : 0;
        layout.HeightCells = 1;
        layout.WidthCells = Math.Max(3, (nameLen + 3) * 2 / 5);
      }
      return layout;
    }

    // Nothing validates Logram.top/left on the way in - LogramViewProvider.Commit writes every
    // key it is handed verbatim, and the client's drag commit only recently learned to clamp -
    // so a coordinate can arrive negative or arbitrarily large. Both alias other cells once
    // RoutingPass.CellKey packs them as x * 65536 + y: CellKey(0, -1) and CellKey(-1, 65535)
    // are the same number, and a y past 65535 overflows into the x field. Clamping on the way
    // out of the manifest keeps the whole routing grid inside the stride no matter what is
    // stored; the ceiling is far beyond any real diagram.
    private const int MaxCell = 8191;
    // Far beyond any real block's pin count, and low enough that MaxCell + it stays inside
    // CellKey's stride even for an element placed at the far edge of the canvas.
    private const int MaxPinIndex = 255;

    private static int Cell(JSC.JSValue value) {
      int cell = value.AsInt(0);
      if(cell < 0) return 0;
      return cell > MaxCell ? MaxCell : cell;
    }

    private static JSC.JSValue ResolveTypeState(Topic instance) {
      string typePath = instance.GetField("type").AsString(null);
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      return typeTopic == null ? null : typeTopic.GetState();
    }

    // Non-null (and non-empty) only for genuine block types - a Logram "variable" type
    // (LoBlock/Variable/*) declares no Children schema at all, which is how blocks and
    // variables are told apart (mirrors LogramView.cs:129's cctor.LoBlock check, but
    // structurally - see todo.md for why the cctor merge itself isn't needed here).
    private static JSC.JSValue ChildrenSchema(JSC.JSValue typeState) {
      if(!typeState.IsObject()) return null;
      JSC.JSValue children = typeState["Children"];
      if(!children.IsObject()) return null;
      bool any = false;
      foreach(var entry in children) {
        if(entry.Value.Field("ddr").AsInt(0) != 0) { any = true; break; }
      }
      return any ? children : null;
    }

    private static bool TryGetDdr(JSC.JSValue children, string pinName, out int ddr) {
      ddr = 0;
      if(children == null) return false;
      JSC.JSValue pd = children[pinName];
      if(!pd.IsObject()) return false;
      ddr = pd.AsInt("ddr", 0);
      return ddr != 0;
    }

    // === Wire resolution + routing (needs every element's layout already known) ===

    private void ResolveWires(List<ElementLayout> layouts) {
      Dictionary<Topic, ElementLayout> byTopic = new Dictionary<Topic, ElementLayout>();
      foreach(ElementLayout l in layouts) byTopic[l.Topic] = l;

      RoutingPass pass = new RoutingPass();
      // Canvas bounds, mirroring logram-document.js's #canvasSize exactly (bounding box of
      // every element, floored at MinCanvasCells) - the port had dropped the original's
      // bounds test (ES/Logram/LogramItems.cs GetWeigt returns an over-threshold weight
      // outside the canvas) and without it A* was free to wander into negative and
      // arbitrarily distant coordinates: waypoints the client's SVG simply clips, found at
      // the cost of a search budget spent on cells that can never be part of a visible
      // route. Exactly one margin cell, and only on the right: an output pin sits at
      // X + WidthCells and is itself blocked (see BlockedCells), so a wire leaving the
      // RIGHTMOST element has to step into the column past it - without that column every
      // legal first step is out of bounds and the route fails. The client reserves the same
      // column (#canvasSize's `+ 1`), or a route through it would be drawn outside the SVG
      // viewport and clipped. No slack beyond that: the client sizes the canvas to its
      // content on every render, so more kept here would be room it no longer draws. The
      // mirror column on the left costs nothing to keep - it is simply cell 0, which
      // MIN_LEFT_CELL/MinLeftCell forbid elements from occupying.
      foreach(ElementLayout l in layouts) {
        pass.MaxX = Math.Max(pass.MaxX, l.X + l.WidthCells + 1);
        pass.MaxY = Math.Max(pass.MaxY, l.Y + l.HeightCells);
      }

      // Element footprints resolved once per pass instead of rescanned per probe: IsBlocked is
      // the router's per-candidate-step predicate, so with SearchLimit at 6000 states and four
      // neighbours each a single wire could ask it ~24000 times, and every one of those walked
      // the whole element list. Same occupancy rule as before, just precomputed - see
      // BlockedCells.
      HashSet<long> blocked = BlockedCells(layouts);

      foreach(ElementLayout layout in layouts) {
        if(layout.IsBlock) {
          foreach(PinLayout pin in layout.Pins) {
            if(pin.Direction != "in") continue;
            ResolveOneWire(pin.Topic, GetPinCell(layout, pin), blocked, byTopic, pass);
          }
        }
        else {
          ResolveOneWire(layout.Topic, GetVariableCell(layout, isSource: false), blocked, byTopic, pass);
        }
      }
    }

    // Every cell covered by an element, by the same rule IsBlocked used to evaluate inline:
    // x from l.X through l.X + l.WidthCells INCLUSIVE (WidthCells already carries the
    // output-pin column - see BuildLayout), y from l.Y through l.Y + l.HeightCells - 1.
    private static HashSet<long> BlockedCells(List<ElementLayout> layouts) {
      HashSet<long> blocked = new HashSet<long>();
      foreach(ElementLayout l in layouts) {
        int xMax = l.X + l.WidthCells;
        int yMax = l.Y + l.HeightCells - 1;
        for(int x = l.X; x <= xMax; x++) {
          for(int y = l.Y; y <= yMax; y++) blocked.Add(RoutingPass.CellKey(x, y));
        }
      }
      return blocked;
    }

    // Kept in sync with logram-document.js's MIN_CANVAS_CELLS - the same client/server
    // agreement the element width/height already need (see SerializeElementRow): route
    // outside what the client actually draws and the wire is silently clipped instead of
    // visibly wrong.
    //
    // The duplication itself is deliberate and stays: the client has to size the canvas on its
    // own, or growing it while a block is being dragged would need a server round trip. What
    // does NOT stay is the drift - ClientContractTests reads the constants back out of
    // logram-document.js and fails the build when they disagree. internal for that test.
    internal const int MinCanvasCellsW = 20;
    internal const int MinCanvasCellsH = 14;

    // Leftmost column a BLOCK may occupy - column 0 stays clear so a wire can reach an input
    // pin, which sits on the element's own left edge (see ResolveWires' bounds comment and
    // logram-document.js's MIN_LEFT_CELL, which clamps the client's own block placements).
    // Variables are exempt and may sit flush against the edge: a variable's usual source is
    // its binding to a topic outside the diagram, not a wire, and the left column is where
    // such an input belongs - hence ExecuteAddVariable floors at 0 instead. The floor here is
    // the server's own guard for the rpc arguments (see LogramViewProvider.ExecuteAddBlock),
    // so a request built by hand cannot park a block in the column nothing can wire through.
    internal const int MinLeftCell = 1;

    // The row-kind markers the client switches on (logram-document.js: #renderElement, the
    // element/canvas filters, the LogramPin skip). Named rather than inlined because the same
    // pair is written twice - once into the row, once into ElementFingerprint - and because
    // ClientContractTests checks each one still appears on the client side.
    internal const string EditorCanvas = "LogramCanvas";
    internal const string EditorBlock = "LogramBlock";
    internal const string EditorVariable = "LogramVariable";
    internal const string EditorPin = "LogramPin";

    // What one step over an untouched edge costs, what re-using an edge this same source
    // already claimed costs instead, and the surcharge for entering a cell some OTHER
    // source's wire already runs through. Named because the router's heuristic has to be
    // scaled by whichever step cost is actually reachable on a given route - get that
    // floor wrong and you silently lose either optimality or most of the search's
    // guidance (see RoutingPass.MinStepCost and LogramWireRouter.Heuristic).
    private const int EdgeStepCost = 6;
    private const int ReusedEdgeStepCost = 1;
    private const int CrossingCost = 16;

    // Everything one ResolveWires pass accumulates as it routes wire after wire, so later
    // wires see what earlier ones took, plus the canvas every route has to stay inside.
    // Owns the pricing and the claiming outright rather than exposing its two maps: those
    // were six near-identical free functions (edge/cell x cost/claim/key) whose only real
    // differences were a bug - one walked its axis one cell short of the other, and one
    // overwrote an existing owner where the other kept the first claim.
    private sealed class RoutingPass {
      // Edges claimed this pass: lets several sinks fed by the same source converge onto
      // one shared trunk instead of each drawing its own overlapping line, while forcing
      // wires from DIFFERENT sources to route around each other rather than run in
      // parallel. Perpendicular crossings share no edge, so this map cannot see them at
      // all - that is what the per-cell map below is for.
      private readonly Dictionary<long, Topic> _edgeOwner = new Dictionary<long, Topic>();
      private readonly Dictionary<long, Topic> _cellOwner = new Dictionary<long, Topic>();
      // Sources with at least one claim - the only situation in which a step can cost
      // less than EdgeStepCost, so this is exactly what decides the admissible floor the
      // next route's heuristic may be scaled by.
      private readonly HashSet<Topic> _claimedSources = new HashSet<Topic>();
      internal int MaxX = MinCanvasCellsW;
      internal int MaxY = MinCanvasCellsH;

      // The cheapest a single step can possibly cost for this source right now. Only a
      // source that already has something to merge onto can reach the reuse discount;
      // handing the router the discount unconditionally stays admissible but throws away
      // most of its guidance.
      internal int MinStepCost(Topic source) {
        return _claimedSources.Contains(source) ? ReusedEdgeStepCost : EdgeStepCost;
      }

      // null = this edge belongs to a different source and must not be run along in
      // parallel (the router has to detour). Otherwise the base or discounted edge price,
      // plus the crossing surcharge when the cell being entered already carries another
      // source's wire - priced rather than blocked, so a perpendicular crossing stays
      // possible but is avoided when a comparable clear route exists.
      internal int? StepCost(int x1, int y1, int x2, int y2, Topic source) {
        Topic owner;
        int cost;
        if(!_edgeOwner.TryGetValue(EdgeKey(x1, y1, x2, y2), out owner)) cost = EdgeStepCost;
        else if(owner == source) cost = ReusedEdgeStepCost;
        else return null;
        if(_cellOwner.TryGetValue(CellKey(x2, y2), out owner) && owner != source) cost += CrossingCost;
        return cost;
      }

      // Records a just-routed path so later wires in this pass see it. First claim wins
      // for edges as well as cells: a route that never went through StepCost at all (the
      // fallback) could otherwise reassign edges another source legitimately owned, which
      // then hard-blocks that source's own later wires off their own trunk.
      internal void Claim(List<LogramWireRouter.GridPoint> cells, Topic source) {
        _claimedSources.Add(source);
        for(int i = 0; i < cells.Count - 1; i++) {
          LogramWireRouter.GridPoint a = cells[i];
          LogramWireRouter.GridPoint b = cells[i + 1];
          // Guaranteed axis-aligned by the router (fallback included - see
          // LogramWireRouter.FallbackPath); skipped rather than trusted, because walking a
          // diagonal here would claim a whole row of edges the wire never occupies.
          if(a.X != b.X && a.Y != b.Y) continue;
          int dx = Math.Sign(b.X - a.X), dy = Math.Sign(b.Y - a.Y);
          ClaimCell(a.X, a.Y, source);
          int x = a.X, y = a.Y;
          while(x != b.X || y != b.Y) {
            int nx = x + dx, ny = y + dy;
            long edge = EdgeKey(x, y, nx, ny);
            if(!_edgeOwner.ContainsKey(edge)) _edgeOwner[edge] = source;
            ClaimCell(nx, ny, source);
            x = nx; y = ny;
          }
        }
      }

      private void ClaimCell(int x, int y, Topic source) {
        long key = CellKey(x, y);
        if(!_cellOwner.ContainsKey(key)) _cellOwner[key] = source;
      }

      // Integer keys, not the concatenated strings these used to build: the router asks
      // for a price on every candidate step of every expansion, so a string key per call
      // meant tens of thousands of short-lived allocations per wire per snapshot.
      // Coordinates are canvas-bounded (see IsBlocked) well inside the 16-bit stride.
      internal static long CellKey(int x, int y) {
        return (long)x * 65536L + y;
      }

      // An edge is identified by its lower/left endpoint plus its orientation, so the
      // same physical segment maps to one key regardless of which end a path reached first.
      private static long EdgeKey(int x1, int y1, int x2, int y2) {
        return (CellKey(Math.Min(x1, x2), Math.Min(y1, y2)) << 1) | (y1 == y2 ? 0L : 1L);
      }
    }


    private void ResolveOneWire(Topic sinkTopic, LogramWireRouter.GridPoint sinkCell, HashSet<long> blocked, Dictionary<Topic, ElementLayout> byTopic, RoutingPass pass) {
      string vid = Vid(sinkTopic);
      string sourcePath = sinkTopic.GetField("cctor.LoBind").AsString(null);
      Topic sourceTopic = string.IsNullOrEmpty(sourcePath) ? null : Topic.root.Get(sourcePath, false);

      string sourceVid = string.Empty;
      bool local = false;
      List<LogramWireRouter.GridPoint> cells = null;

      if(sourceTopic != null) {
        sourceVid = Vid(sourceTopic);
        // Mirrors loPin.SourceLoaded's ancestry check (LogramItems.cs:243-267): local
        // (drawable) only if the source is a direct child of this diagram (a
        // variable) or a grandchild (a block's pin) - anything else is "external".
        local = sourceTopic.parent == _root || (sourceTopic.parent != null && sourceTopic.parent.parent == _root);

        if(local) {
          ElementLayout sourceOwner;
          LogramWireRouter.GridPoint sourceCell;
          if(byTopic.TryGetValue(sourceTopic, out sourceOwner)) {
            // Source is itself an element (a variable) - anchor at its output side.
            sourceCell = GetVariableCell(sourceOwner, isSource: true);
          }
          else if(sourceTopic.parent != null && byTopic.TryGetValue(sourceTopic.parent, out sourceOwner)) {
            PinLayout sourcePin = sourceOwner.Pins.Find(p => p.Topic == sourceTopic);
            sourceCell = sourcePin != null ? GetPinCell(sourceOwner, sourcePin) : new LogramWireRouter.GridPoint(sourceOwner.X, sourceOwner.Y);
          }
          else {
            sourceCell = new LogramWireRouter.GridPoint(sinkCell.X - 1, sinkCell.Y);
          }

          // Sent in grid cells (FindPath's own native unit), not pixels - same reason
          // x/y stay in cells on the element row (see SerializeElementRow): the
          // client just multiplies by CELL once to draw, same as it already does for
          // element position, instead of the server doing that conversion for it.
          cells = LogramWireRouter.FindPath(sinkCell, sourceCell,
            (x, y) => IsBlocked(x, y, blocked, pass),
            (x1, y1, x2, y2) => pass.StepCost(x1, y1, x2, y2, sourceTopic),
            pass.MinStepCost(sourceTopic));
          pass.Claim(cells, sourceTopic);
        }
      }

      System.Text.StringBuilder fp = new System.Text.StringBuilder();
      fp.Append(sourceVid).Append('|').Append(local);
      if(cells != null) {
        foreach(LogramWireRouter.GridPoint p in cells) fp.Append('|').Append(p.X).Append(',').Append(p.Y);
      }
      string fingerprint = fp.ToString();

      SentRow sent = SentOrNull(vid);
      if(sent != null && sent.WireFingerprint == fingerprint) return;
      Sent(vid).WireFingerprint = fingerprint;

      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, vid);
      dto["sourceVid"] = sourceVid;
      dto["sourceLocal"] = local;
      if(cells != null) {
        JSL.Array pts = new JSL.Array();
        int i = 0;
        foreach(LogramWireRouter.GridPoint p in cells) {
          JSC.JSObject pt = JSC.JSObject.CreateObject();
          pt["x"] = new JSL.Number(p.X);
          pt["y"] = new JSL.Number(p.Y);
          pts[i++] = pt;
        }
        dto["sourcePath"] = pts;
      }
      _send(dto);
    }

    // WidthCells is already the element's whole footprint for both kinds (a block's
    // BuildLayout bakes its output-pin column into WidthCells, a variable's output
    // pin sits at WidthCells natively) - occupancy has to track that or the router
    // leaves the real pin cells unmarked as blocked. Rows match a variable's own
    // yMax now that a block's first pin sits flush on its top row (Y, see
    // GetPinCell) instead of one row below it - there's no header row left to give
    // the extra vertical margin variables still get above/below their single pin.
    private static bool IsBlocked(int x, int y, HashSet<long> blocked, RoutingPass pass) {
      // Outside the canvas the client actually draws (see ResolveWires/MinCanvasCells).
      // An element pushed hard against an edge can end up with every legal first step
      // out of bounds - that's intentional: the route fails and the fallback path is the
      // visible signal that the element sits where a wire cannot reach its pin.
      if(x < 0 || y < 0 || x > pass.MaxX || y > pass.MaxY) return true;
      return blocked.Contains(RoutingPass.CellKey(x, y));
    }

    // A block's first pin now sits flush on its own top row (Y) - the row used to be
    // reserved one below (Y+index+1) for the header text drawn above the pins, but
    // the client no longer draws a name inside the block (see logram-document.js
    // #renderBlock - the name is a hover title now), so that row is free for pins to
    // use directly. Outputs sit at WidthCells, same as a variable's (see
    // GetVariableCell) - the one-cell gap past the body is baked into WidthCells
    // itself now (see BuildLayout), not added here.
    // No isSink parameter: it was never read - the anchor follows the pin's own
    // Direction, so both call sites were passing a named argument documenting an intent
    // the body did not implement (flipping it would have changed nothing).
    private static LogramWireRouter.GridPoint GetPinCell(ElementLayout owner, PinLayout pin) {
      int x = pin.Direction == "in" ? owner.X : owner.X + owner.WidthCells;
      int y = owner.Y + pin.Index;
      return new LogramWireRouter.GridPoint(x, y);
    }

    private static LogramWireRouter.GridPoint GetVariableCell(ElementLayout owner, bool isSource) {
      int x = isSource ? owner.X + owner.WidthCells : owner.X;
      return new LogramWireRouter.GridPoint(x, owner.Y);
    }

    // === Serialization ===

    // level is the only one of the generic row fields any of the three Serialize*Row
    // methods below actually need - ViewStore's insertion/removal bookkeeping keys off
    // it. expander/readonly (and, on the element row, the embedded pins array -
    // logram-document.js derives pin geometry from the separate LogramPin rows
    // instead, grouped by vid prefix) have no consumer in logram-document.js, so
    // they're deliberately omitted rather than sent as unused filler. Canvas size
    // isn't sent at all - logram-document.js derives it from the elements' own
    // bounding box instead (see #canvasSize), so this row only ever needs sending
    // once, purely as the "LogramCanvas" row-kind marker (see #renderElement et al.).
    private JSC.JSObject SerializeCanvasRow() {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, RootVid);
      dto["level"] = new JSL.Number(0);
      dto["icon"] = IconResource.Resolve(_root, null) ?? string.Empty;
      dto["name"] = _root.name ?? string.Empty;
      dto["editor"] = EditorCanvas;
      return dto;
    }

    private JSC.JSObject SerializeElementRow(ElementLayout layout) {
      Topic el = layout.Topic;
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, Vid(el));
      dto["level"] = new JSL.Number(1);
      dto["icon"] = IconResource.Resolve(el, null) ?? string.Empty;
      dto["name"] = el.name ?? string.Empty;
      dto["editor"] = layout.IsBlock ? EditorBlock : EditorVariable;
      // Only a variable draws its own value-colored dots (#renderVariable) - a
      // block's own row has no such consumer (its pins carry color individually,
      // see SerializePinRow), so it's simply not computed for one.
      if(!layout.IsBlock) {
        string vid = Vid(el);
        string color = ColorForValue(RowProjector.ToWebStateValue(el.GetState()));
        dto["color"] = color;
        Sent(vid).Color = color;
      }
      dto["x"] = new JSL.Number(layout.X);
      dto["y"] = new JSL.Number(layout.Y);
      // Sent explicitly (not left for the client to re-derive) so the box the client
      // draws exactly matches the box IsBlocked/GetPinCell routed wires around -
      // otherwise client and server independently estimate slightly different sizes
      // and wires visibly miss the pins they're supposed to connect to. In grid cells,
      // like x/y - the client multiplies by CELL once to draw (see logram-document.js
      // #renderBlock/#renderVariable), same as it already does for position.
      dto["width"] = new JSL.Number(layout.WidthCells);
      dto["height"] = new JSL.Number(layout.IsBlock ? layout.HeightCells : 1);
      return dto;
    }

    private JSC.JSObject SerializePinRow(ElementLayout owner, PinLayout pin) {
      string vid = Vid(pin.Topic);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, vid);
      dto["level"] = new JSL.Number(2);
      // No icon field - unlike a block's own row (#icon renders el.icon as an
      // <image>), logram-document.js's pin rendering (#renderBlock's pinNodes) never
      // reads pin.icon at all; normalizeRow's `row.icon || ''` fallback covers the
      // absence the same as an explicit empty string would.
      dto["name"] = pin.Topic.name ?? string.Empty;
      dto["editor"] = EditorPin;
      JSC.JSValue state = RowProjector.ToWebStateValue(pin.Topic.GetState());
      string color = ColorForValue(state);
      dto["color"] = color;
      Sent(vid).Color = color;
      dto["pinDirection"] = pin.Direction;
      dto["pinIndex"] = new JSL.Number(pin.Index);
      // Default-suppressed like expander/readonly above (most pins are never traced) -
      // logram-document.js's normalizeRow treats an absent key as falsy. Kept current
      // afterwards by SendTraceUpdate (OnChanged's "Logram.trace" special case), not
      // by this method - a pin is only ever serialized here once (see SendSnapshot's
      // _knownVids-gated pin loop).
      bool traced = pin.Topic.GetField("Logram.trace").AsBool(false);
      if(traced) {
        string displayValue = FormatTraceValue(state);
        dto["trace"] = true;
        dto["displayValue"] = displayValue;
        Sent(vid).DisplayValue = displayValue;
      }
      return dto;
    }

    // === Value presentation (dot color / Trace text) ===

    // Mirrors logram-document.js's now-removed colorForValue exactly - moved here,
    // not duplicated, so the client never needs the raw value at all (checked every
    // consumer: dot fill and the Trace label are the only two, both now served by
    // color/displayValue instead - see SendValueUpdate/SerializePinRow/
    // SerializeElementRow) and the palette lives in exactly one place.
    private static string ColorForValue(JSC.JSValue value) {
      if(value.IsNullOrUndefined()) return "#9aa5b1";
      if(value.Is<bool>()) return value.AsBool(false) ? "#3ddc84" : "#9aa5b1";
      if(value.IsNumber) {
        double d = (double)value;
        return d > 0 ? "#2dbf9e" : (d == 0 ? "#9aa5b1" : "#2563eb");
      }
      if(value.Is<string>()) {
        string s = value.AsString(string.Empty);
        return s.StartsWith("¤BA", StringComparison.Ordinal) ? "#7c5cbf" : "#c9a227";
      }
      return "#ba55d3";
    }

    // Mirrors logram-document.js's now-removed formatTraceValue: magnitude-adaptive
    // decimal count (approximates ES's exact .NET format-string table from
    // LogramItems.cs loPin.Render without reproducing it verbatim) so a traced
    // number stays short regardless of scale, trailing zeros trimmed.
    private static string FormatTraceValue(JSC.JSValue value) {
      if(value.IsNullOrUndefined()) return string.Empty;
      if(value.IsNumber) {
        double d = (double)value;
        if(double.IsNaN(d) || double.IsInfinity(d)) return d.ToString(CultureInfo.InvariantCulture);
        if(d == Math.Truncate(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(d)));
        int decimals = Math.Max(0, Math.Min(5, 3 - magnitude));
        string text = d.ToString("F" + decimals, CultureInfo.InvariantCulture);
        if(text.Contains(".")) {
          text = text.TrimEnd('0');
          text = text.TrimEnd('.');
        }
        return text;
      }
      if(value.Is<bool>()) return value.AsBool(false) ? "true" : "false";
      if(value.Is<string>()) return value.AsString(string.Empty);
      return JsLib.Stringify(value);
    }

    private string Vid(Topic topic) {
      return _viewName + "#" + (topic == null ? "/" : topic.path);
    }

    private sealed class ElementLayout {
      internal Topic Topic;
      internal int X;
      internal int Y;
      internal int WidthCells;
      internal int HeightCells;
      internal bool IsBlock;
      internal List<PinLayout> Pins = new List<PinLayout>();
    }

    private sealed class PinLayout {
      internal Topic Topic;
      internal string Direction;
      internal int Index;
    }

  }
}
