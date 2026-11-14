///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;

namespace X13.WebUI {
  // Everything one wire-resolution pass accumulates as it routes wire after wire, so later
  // wires see what earlier ones took, plus the canvas every route has to stay inside.
  // Owns the pricing and the claiming outright rather than exposing its two maps: those
  // were six near-identical free functions (edge/cell x cost/claim/key) whose only real
  // differences were a bug - one walked its axis one cell short of the other, and one
  // overwrote an existing owner where the other kept the first claim.
  //
  // Shared by both graph views - Logram (LogramGraphController) and LPLC (LplcGraphController).
  // A source is any identity with value equality: Logram passes the source Topic, LPLC the
  // source's vid, since an LPLC block output is not a topic of its own.
  internal sealed class WireRoutingPass {
    // What one step over an untouched edge costs, what re-using an edge this same source
    // already claimed costs instead, and the surcharge for entering a cell some OTHER
    // source's wire already runs through. Named because the router's heuristic has to be
    // scaled by whichever step cost is actually reachable on a given route - get that
    // floor wrong and you silently lose either optimality or most of the search's
    // guidance (see MinStepCost and LogramWireRouter.Heuristic).
    private const int EdgeStepCost = 6;
    private const int ReusedEdgeStepCost = 1;
    private const int CrossingCost = 16;

    // Edges claimed this pass: lets several sinks fed by the same source converge onto
    // one shared trunk instead of each drawing its own overlapping line, while forcing
    // wires from DIFFERENT sources to route around each other rather than run in
    // parallel. Perpendicular crossings share no edge, so this map cannot see them at
    // all - that is what the per-cell map below is for.
    private readonly Dictionary<long, object> _edgeOwner = new Dictionary<long, object>();
    private readonly Dictionary<long, object> _cellOwner = new Dictionary<long, object>();
    // Sources with at least one claim - the only situation in which a step can cost
    // less than EdgeStepCost, so this is exactly what decides the admissible floor the
    // next route's heuristic may be scaled by.
    private readonly HashSet<object> _claimedSources = new HashSet<object>();
    internal int MaxX = LogramGraphController.MinCanvasCellsW;
    internal int MaxY = LogramGraphController.MinCanvasCellsH;

    // The cheapest a single step can possibly cost for this source right now. Only a
    // source that already has something to merge onto can reach the reuse discount;
    // handing the router the discount unconditionally stays admissible but throws away
    // most of its guidance.
    internal int MinStepCost(object source) {
      return _claimedSources.Contains(source) ? ReusedEdgeStepCost : EdgeStepCost;
    }

    // null = this edge belongs to a different source and must not be run along in
    // parallel (the router has to detour). Otherwise the base or discounted edge price,
    // plus the crossing surcharge when the cell being entered already carries another
    // source's wire - priced rather than blocked, so a perpendicular crossing stays
    // possible but is avoided when a comparable clear route exists.
    // object.Equals, not ==: on two `object`s == compares references, and LPLC's string
    // identities are built per wire, so equal sources would never be recognised as one.
    internal int? StepCost(int x1, int y1, int x2, int y2, object source) {
      object owner;
      int cost;
      if(!_edgeOwner.TryGetValue(EdgeKey(x1, y1, x2, y2), out owner)) cost = EdgeStepCost;
      else if(object.Equals(owner, source)) cost = ReusedEdgeStepCost;
      else return null;
      if(_cellOwner.TryGetValue(CellKey(x2, y2), out owner) && !object.Equals(owner, source)) cost += CrossingCost;
      return cost;
    }

    // Records a just-routed path so later wires in this pass see it. First claim wins
    // for edges as well as cells: a route that never went through StepCost at all (the
    // fallback) could otherwise reassign edges another source legitimately owned, which
    // then hard-blocks that source's own later wires off their own trunk.
    internal void Claim(List<LogramWireRouter.GridPoint> cells, object source) {
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

    // Outside the canvas the client actually draws (see LogramGraphController.ResolveWires /
    // MinCanvasCells). An element pushed hard against an edge can end up with every legal
    // first step out of bounds - that's intentional: the route fails and the fallback path is
    // the visible signal that the element sits where a wire cannot reach its pin.
    internal bool IsBlocked(int x, int y, HashSet<long> blocked) {
      if(x < 0 || y < 0 || x > MaxX || y > MaxY) return true;
      return blocked.Contains(CellKey(x, y));
    }

    // Every cell of one element's footprint: x from left through left + widthCells INCLUSIVE
    // (the width already carries the output-pin column), y from top through top + heightCells - 1.
    internal static void Block(HashSet<long> blocked, int left, int top, int widthCells, int heightCells) {
      int xMax = left + widthCells;
      int yMax = top + heightCells - 1;
      for(int x = left; x <= xMax; x++) {
        for(int y = top; y <= yMax; y++) blocked.Add(CellKey(x, y));
      }
    }

    private void ClaimCell(int x, int y, object source) {
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
}
