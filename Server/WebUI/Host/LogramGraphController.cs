///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI.Host {
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
    private const int ResendDebounceMs = 30;

    private readonly Action<JSC.JSObject> _send;
    private readonly Topic _root;
    private readonly string _viewName;
    private readonly object _resendGate = new object();
    // Guards every "what has the client already been told" collection below
    // (_knownVids, _sentElementLayout, _sentWireFingerprint, _sentColor,
    // _sentDisplayValue, _canvasSent). Two threads reach them: the repository thread via
    // OnChanged, and a threadpool thread via the debounce timer's SendSnapshot - which
    // clears and repopulates _knownVids wholesale, so an OnChanged landing inside that
    // window either missed a live vid (dropping a pin's colour update) or threw from a
    // HashSet mid-resize. Two SendSnapshot runs could also overlap, since ScheduleSnapshot
    // only disposes the PENDING timer and cannot stop one already running.
    //
    // Held across _send: that writes the socket synchronously and never re-enters this
    // controller, and OnChanged already sent on the repository thread before this lock
    // existed, so the only new cost is waiting out a snapshot in progress.
    // Deliberately separate from _resendGate, which stays a short leaf lock (nothing
    // takes _stateGate while holding it, or the other way round).
    private readonly object _stateGate = new object();
    private SubRec _sub;
    private Timer _resendTimer;
    private bool _disposed;

    internal LogramGraphController(Action<JSC.JSObject> send, Topic root, string viewName) {
      _send = send;
      _root = root;
      _viewName = string.IsNullOrEmpty(viewName) ? "logram" : viewName;
    }

    internal string RootVid { get { return Vid(_root); } }

    internal void Open() {
      _sub = _root.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value | SubRec.SubMask.Field, OnChanged);
      SendSnapshot();
    }

    public void Dispose() {
      lock(_resendGate) {
        _disposed = true;
        _resendTimer?.Dispose();
        _resendTimer = null;
      }
      _sub?.Dispose();
      _sub = null;
    }

    private void OnChanged(Perform p, SubRec sub) {
      try {
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
          lock(_stateGate) {
            if(p.src == _root || !_knownVids.Contains(Vid(p.src))) return;
            bool isPin = p.src.parent != null && p.src.parent != _root;
            if(isPin) SendValueUpdate(p.src, true);
            else if(ChildrenSchema(ResolveTypeState(p.src)) == null) SendValueUpdate(p.src, false);
          }
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
          bool handled = false;
          lock(_stateGate) {
            if(_knownVids.Contains(Vid(p.src))) {
              SendTraceUpdate(p.src);
              handled = true;
            }
          }
          if(handled) return;
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
        Log.Warning("LogramGraphController({0}).OnChanged - {1}", _root.path, ex.Message);
      }
    }

    private void HandleRootRemoved() {
      lock(_resendGate) {
        _disposed = true;
        _resendTimer?.Dispose();
        _resendTimer = null;
      }
      _send(ViewProtocolSerializer.Del(RootVid));
      _sub?.Dispose();
      _sub = null;
    }

    // Debounced: removing one topic fans out into a separate remove Perform for
    // every descendant (Repo.cs TickStep1: `foreach(Topic tmp in c.src.all) ...`),
    // each independently matching this deep (SubMask.All) subscription - deleting a
    // block with N pins would otherwise fire N+1 back-to-back full snapshots for one
    // logical delete. Any burst of structural changes arriving within the debounce
    // window collapses into a single resend once things settle.
    private void ScheduleSnapshot() {
      lock(_resendGate) {
        if(_disposed) return;
        _resendTimer?.Dispose();
        _resendTimer = new Timer(_ => SafeSendSnapshot(), null, ResendDebounceMs, Timeout.Infinite);
      }
    }

    // SendSnapshot runs on a Timer threadpool thread, where an escaping exception is an
    // unhandled exception on a threadpool thread - it takes the whole process down, not
    // just this one view. OnChanged already has exactly this guard for the repository
    // thread; the timer path went without one even though it is the heavier of the two
    // (it is what runs the routing pass, and is the path that has already downed this
    // server once).
    private void SafeSendSnapshot() {
      try {
        SendSnapshot();
      }
      catch(Exception ex) {
        Log.Warning("LogramGraphController({0}).SendSnapshot - {1}", _root.path, ex.Message);
      }
    }

    // Rows the client currently has for this diagram, so a rebuild can tell it to
    // drop whatever no longer exists - see the evnt.del loop below. Pins never carry
    // any mutable identity field (direction/index are fixed once created; live value
    // rides SendValueUpdate separately), so membership in this set is enough to know
    // whether a pin still needs sending at all - only elements need the finer
    // position/size diff in _sentElementLayout below.
    private readonly HashSet<string> _knownVids = new HashSet<string>();
    private readonly Dictionary<string, SentLayout> _sentElementLayout = new Dictionary<string, SentLayout>();

    // Last color/displayValue actually sent per pin/variable vid - SendValueUpdate
    // diffs against these instead of sending on every single changedState tick, since
    // most value changes (e.g. a positive counter incrementing) don't cross a
    // ColorForValue bucket boundary and would repaint nothing on screen either way.
    private readonly Dictionary<string, string> _sentColor = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _sentDisplayValue = new Dictionary<string, string>();

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
    private bool _canvasSent;

    private void SendSnapshot() {
      lock(_stateGate) {
        SendSnapshotLocked();
      }
    }

    private void SendSnapshotLocked() {
      HashSet<string> currentVids = new HashSet<string>();
      currentVids.Add(RootVid);
      if(!_canvasSent) {
        _send(SerializeCanvasRow());
        _canvasSent = true;
      }

      List<Topic> elements = new List<Topic>();
      foreach(Topic child in _root.children) elements.Add(child);

      List<ElementLayout> layouts = new List<ElementLayout>(elements.Count);
      foreach(Topic el in elements) layouts.Add(BuildLayout(el));

      foreach(ElementLayout layout in layouts) {
        string vid = Vid(layout.Topic);
        currentVids.Add(vid);

        SentLayout current = new SentLayout() {
          X = layout.X,
          Y = layout.Y,
          Width = layout.WidthCells,
          Height = layout.IsBlock ? layout.HeightCells : 1,
        };
        SentLayout previous;
        bool unchanged = _sentElementLayout.TryGetValue(vid, out previous) && previous.Equals(current);
        if(!unchanged) {
          _send(SerializeElementRow(layout));
          _sentElementLayout[vid] = current;
        }

        if(layout.IsBlock) {
          foreach(PinLayout pin in layout.Pins) {
            string pinVid = Vid(pin.Topic);
            currentVids.Add(pinVid);
            if(!_knownVids.Contains(pinVid)) _send(SerializePinRow(layout, pin));
          }
        }
      }

      foreach(string staleVid in _knownVids) {
        if(!currentVids.Contains(staleVid)) _send(ViewProtocolSerializer.Del(staleVid));
      }
      _knownVids.Clear();
      foreach(string vid in currentVids) _knownVids.Add(vid);
      List<string> staleLayoutVids = new List<string>();
      foreach(string vid in _sentElementLayout.Keys) {
        if(!currentVids.Contains(vid)) staleLayoutVids.Add(vid);
      }
      foreach(string vid in staleLayoutVids) _sentElementLayout.Remove(vid);
      List<string> staleWireVids = new List<string>();
      foreach(string vid in _sentWireFingerprint.Keys) {
        if(!currentVids.Contains(vid)) staleWireVids.Add(vid);
      }
      foreach(string vid in staleWireVids) _sentWireFingerprint.Remove(vid);
      List<string> staleColorVids = new List<string>();
      foreach(string vid in _sentColor.Keys) {
        if(!currentVids.Contains(vid)) staleColorVids.Add(vid);
      }
      foreach(string vid in staleColorVids) {
        _sentColor.Remove(vid);
        _sentDisplayValue.Remove(vid);
      }

      ResolveWires(layouts);
    }

    // Sends color (always) and displayValue (only when traced, and only for
    // something Trace actually applies to - a variable has no menu/flag for it, see
    // BuildPinMenu) instead of the raw value: logram-document.js only ever turned
    // value into a dot fill or a Trace label, never anything else (checked every
    // consumer before doing this), so computing that once here and diffing against
    // _sentColor/_sentDisplayValue means most ticks - anything that doesn't cross a
    // ColorForValue bucket, or change the formatted text - send nothing at all.
    private void SendValueUpdate(Topic topic, bool supportsTrace) {
      string vid = Vid(topic);
      JSC.JSValue state = WorkspaceRowProjector.ToWebStateValue(topic.GetState());
      string color = ColorForValue(state);
      bool traced = supportsTrace && JsLib.ofBool(topic.GetField("Logram.trace"), false);
      string displayValue = traced ? FormatTraceValue(state) : null;

      string previousColor;
      bool colorChanged = !_sentColor.TryGetValue(vid, out previousColor) || previousColor != color;
      bool displayChanged = false;
      if(traced) {
        string previousDisplay;
        displayChanged = !_sentDisplayValue.TryGetValue(vid, out previousDisplay) || previousDisplay != displayValue;
      }
      if(!colorChanged && !displayChanged) return;

      // Only the field that actually moved goes out - unlike SendTraceUpdate's
      // `trace` (which must always restate itself, see the comment there), the
      // client just keeps whatever color/displayValue it already has for any field
      // not present in the patch (ViewStore.update() only touches keys it's given).
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, vid);
      if(colorChanged) dto["color"] = color;
      if(traced && displayChanged) dto["displayValue"] = displayValue;
      _send(dto);

      if(colorChanged) _sentColor[vid] = color;
      if(traced && displayChanged) _sentDisplayValue[vid] = displayValue;
    }

    // Unlike SendAdd's fields (suppressed when at their default - see SerializePinRow),
    // an evnt.upd always states the field outright, defaulted or not: this is the ONLY
    // update a toggle-back-to-off ever gets, so omitting `false` here would leave the
    // client showing a stale value label forever. Turning trace ON also needs a fresh
    // displayValue right away - SendValueUpdate's own diffing wouldn't otherwise emit
    // one until the value next actually ticks, leaving the label blank until then.
    private void SendTraceUpdate(Topic pin) {
      string vid = Vid(pin);
      bool traced = JsLib.ofBool(pin.GetField("Logram.trace"), false);
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, vid);
      dto["trace"] = traced;
      if(traced) {
        string displayValue = FormatTraceValue(WorkspaceRowProjector.ToWebStateValue(pin.GetState()));
        dto["displayValue"] = displayValue;
        _sentDisplayValue[vid] = displayValue;
      }
      _send(dto);
    }

    // === Layout resolution (position + pin schema, no wire info yet) ===

    private ElementLayout BuildLayout(Topic el) {
      JSC.JSValue typeState = ResolveTypeState(el);
      JSC.JSValue children = ChildrenSchema(typeState);
      ElementLayout layout = new ElementLayout() {
        Topic = el,
        X = JsLib.OfInt(el.GetField("Logram.left"), 0),
        Y = JsLib.OfInt(el.GetField("Logram.top"), 0),
        IsBlock = children != null,
      };

      if(layout.IsBlock) {
        int inCount = 0, outCount = 0;
        int inMaxNameLen = 0, outMaxNameLen = 0;
        foreach(Topic pinTopic in el.children) {
          int ddr;
          if(!TryGetDdr(children, pinTopic.name, out ddr)) continue;
          bool isInput = ddr < 0;
          int index = isInput ? (-ddr - 1) : (ddr - 1);
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

    private static JSC.JSValue ResolveTypeState(Topic instance) {
      string typePath = JsLib.OfString(instance.GetField("type"), null);
      Topic typeTopic = TypeHelper.ResolveTypeTopic(typePath);
      return typeTopic == null ? null : typeTopic.GetState();
    }

    // Non-null (and non-empty) only for genuine block types - a Logram "variable" type
    // (LoBlock/Variable/*) declares no Children schema at all, which is how blocks and
    // variables are told apart (mirrors LogramView.cs:129's cctor.LoBlock check, but
    // structurally - see todo.md for why the cctor merge itself isn't needed here).
    private static JSC.JSValue ChildrenSchema(JSC.JSValue typeState) {
      if(typeState == null || typeState.ValueType != JSC.JSValueType.Object || typeState.Value == null) return null;
      JSC.JSValue children = typeState["Children"];
      if(children == null || children.ValueType != JSC.JSValueType.Object || children.Value == null) return null;
      bool any = false;
      foreach(var entry in children) {
        if(JsLib.OfInt(JsLib.GetField(entry.Value, "ddr"), 0) != 0) { any = true; break; }
      }
      return any ? children : null;
    }

    private static bool TryGetDdr(JSC.JSValue children, string pinName, out int ddr) {
      ddr = 0;
      if(children == null) return false;
      JSC.JSValue pd = children[pinName];
      if(pd == null || pd.ValueType != JSC.JSValueType.Object || pd.Value == null) return false;
      ddr = JsLib.OfInt(pd, "ddr", 0);
      return ddr != 0;
    }

    // === Wire resolution + routing (needs every element's layout already known) ===

    private void ResolveWires(List<ElementLayout> layouts) {
      Dictionary<Topic, ElementLayout> byTopic = new Dictionary<Topic, ElementLayout>();
      foreach(ElementLayout l in layouts) byTopic[l.Topic] = l;

      RoutingPass pass = new RoutingPass();
      // Canvas bounds, mirroring logram-document.js's #canvasSize exactly (bounding box
      // of every element + CanvasMarginCells, floored at MinCanvasCells) - the port had
      // dropped the original's bounds test (ES/Logram/LogramItems.cs GetWeigt returns an
      // over-threshold weight outside the canvas) and without it A* was free to wander
      // into negative and arbitrarily distant coordinates: waypoints the client's SVG
      // simply clips, found at the cost of a search budget spent on cells that can never
      // be part of a visible route.
      foreach(ElementLayout l in layouts) {
        pass.MaxX = Math.Max(pass.MaxX, l.X + l.WidthCells + CanvasMarginCells);
        pass.MaxY = Math.Max(pass.MaxY, l.Y + l.HeightCells + CanvasMarginCells);
      }

      foreach(ElementLayout layout in layouts) {
        if(layout.IsBlock) {
          foreach(PinLayout pin in layout.Pins) {
            if(pin.Direction != "in") continue;
            ResolveOneWire(pin.Topic, GetPinCell(layout, pin), layouts, byTopic, pass);
          }
        }
        else {
          ResolveOneWire(layout.Topic, GetVariableCell(layout, isSource: false), layouts, byTopic, pass);
        }
      }
    }

    // Kept in sync with logram-document.js's CANVAS_MARGIN_CELLS / MIN_CANVAS_CELLS -
    // the same client/server agreement the element width/height already need (see
    // SerializeElementRow): route outside what the client actually draws and the wire is
    // silently clipped instead of visibly wrong.
    private const int CanvasMarginCells = 3;
    private const int MinCanvasCellsW = 20;
    private const int MinCanvasCellsH = 14;

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
      private static long CellKey(int x, int y) {
        return (long)x * 65536L + y;
      }

      // An edge is identified by its lower/left endpoint plus its orientation, so the
      // same physical segment maps to one key regardless of which end a path reached first.
      private static long EdgeKey(int x1, int y1, int x2, int y2) {
        return (CellKey(Math.Min(x1, x2), Math.Min(y1, y2)) << 1) | (y1 == y2 ? 0L : 1L);
      }
    }

    // Fingerprint of what was last actually sent for one sink's wire binding
    // (sourceVid|sourceLocal|path points) - the same identity/position diffing
    // SendSnapshot does for elements, applied here too: routing recomputes for
    // EVERY bound pin on EVERY structural change (a route can be blocked by any
    // element, not just its own two endpoints), but most of those recomputations
    // land on the exact same result - e.g. deleting one block on the far side of an
    // 8-block diagram still re-evaluated every other wire, and previously resent all
    // of them regardless of whether the route actually moved.
    private readonly Dictionary<string, string> _sentWireFingerprint = new Dictionary<string, string>();

    private void ResolveOneWire(Topic sinkTopic, LogramWireRouter.GridPoint sinkCell, List<ElementLayout> layouts, Dictionary<Topic, ElementLayout> byTopic, RoutingPass pass) {
      string vid = Vid(sinkTopic);
      string sourcePath = JsLib.OfString(sinkTopic.GetField("cctor.LoBind"), null);
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
            (x, y) => IsBlocked(x, y, layouts, pass),
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

      string previousFingerprint;
      if(_sentWireFingerprint.TryGetValue(vid, out previousFingerprint) && previousFingerprint == fingerprint) return;
      _sentWireFingerprint[vid] = fingerprint;

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
    private static bool IsBlocked(int x, int y, List<ElementLayout> layouts, RoutingPass pass) {
      // Outside the canvas the client actually draws (see RoutingPass/CanvasMarginCells).
      // An element pushed hard against an edge can end up with every legal first step
      // out of bounds - that's intentional: the route fails and the fallback path is the
      // visible signal that the element sits where a wire cannot reach its pin.
      if(x < 0 || y < 0 || x > pass.MaxX || y > pass.MaxY) return true;
      foreach(ElementLayout l in layouts) {
        int yMin = l.Y;
        int yMax = l.Y + l.HeightCells - 1;
        int xMax = l.X + l.WidthCells;
        if(x >= l.X && x <= xMax && y >= yMin && y <= yMax) return true;
      }
      return false;
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
      dto["editor"] = "LogramCanvas";
      return dto;
    }

    private JSC.JSObject SerializeElementRow(ElementLayout layout) {
      Topic el = layout.Topic;
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, Vid(el));
      dto["level"] = new JSL.Number(1);
      dto["icon"] = IconResource.Resolve(el, null) ?? string.Empty;
      dto["name"] = el.name ?? string.Empty;
      dto["editor"] = layout.IsBlock ? "LogramBlock" : "LogramVariable";
      // Only a variable draws its own value-colored dots (#renderVariable) - a
      // block's own row has no such consumer (its pins carry color individually,
      // see SerializePinRow), so it's simply not computed for one.
      if(!layout.IsBlock) {
        string vid = Vid(el);
        string color = ColorForValue(WorkspaceRowProjector.ToWebStateValue(el.GetState()));
        dto["color"] = color;
        _sentColor[vid] = color;
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
      dto["editor"] = "LogramPin";
      JSC.JSValue state = WorkspaceRowProjector.ToWebStateValue(pin.Topic.GetState());
      string color = ColorForValue(state);
      dto["color"] = color;
      _sentColor[vid] = color;
      dto["pinDirection"] = pin.Direction;
      dto["pinIndex"] = new JSL.Number(pin.Index);
      // Default-suppressed like expander/readonly above (most pins are never traced) -
      // logram-document.js's normalizeRow treats an absent key as falsy. Kept current
      // afterwards by SendTraceUpdate (OnChanged's "Logram.trace" special case), not
      // by this method - a pin is only ever serialized here once (see SendSnapshot's
      // _knownVids-gated pin loop).
      bool traced = JsLib.ofBool(pin.Topic.GetField("Logram.trace"), false);
      if(traced) {
        string displayValue = FormatTraceValue(state);
        dto["trace"] = true;
        dto["displayValue"] = displayValue;
        _sentDisplayValue[vid] = displayValue;
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
      if(value == null || !value.Defined || value.IsNull) return "#9aa5b1";
      if(value.ValueType == JSC.JSValueType.Boolean) return JsLib.ofBool(value, false) ? "#3ddc84" : "#9aa5b1";
      if(value.IsNumber) {
        double d = (double)value;
        return d > 0 ? "#2dbf9e" : (d == 0 ? "#9aa5b1" : "#2563eb");
      }
      if(value.ValueType == JSC.JSValueType.String) {
        string s = value.Value as string ?? string.Empty;
        return s.StartsWith("¤BA", StringComparison.Ordinal) ? "#7c5cbf" : "#c9a227";
      }
      return "#ba55d3";
    }

    // Mirrors logram-document.js's now-removed formatTraceValue: magnitude-adaptive
    // decimal count (approximates ES's exact .NET format-string table from
    // LogramItems.cs loPin.Render without reproducing it verbatim) so a traced
    // number stays short regardless of scale, trailing zeros trimmed.
    private static string FormatTraceValue(JSC.JSValue value) {
      if(value == null || !value.Defined || value.IsNull) return string.Empty;
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
      if(value.ValueType == JSC.JSValueType.Boolean) return JsLib.ofBool(value, false) ? "true" : "false";
      if(value.ValueType == JSC.JSValueType.String) return value.Value as string ?? string.Empty;
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

    // What was last actually sent to the client for one element's row - compared
    // against the freshly computed layout each rebuild to decide whether a resend is
    // needed at all (see SendSnapshot).
    private struct SentLayout {
      internal int X;
      internal int Y;
      internal int Width;
      internal int Height;

      internal bool Equals(SentLayout other) {
        return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
      }
    }
  }
}
