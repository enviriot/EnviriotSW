///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;

namespace X13.WebUI {
  // Server-side port of the wire-routing half of ES/Logram/LogramItems.cs's
  // loBinding.FindPath + PriorityQueue<T> - an A* search over the CELL_SIZE grid that
  // routes an orthogonal (Manhattan) path from one pin to another, steering around
  // element footprints. Ported here (not to JS) because routing needs the whole
  // diagram's layout known at once (GetWeigt in the original checks occupancy across
  // the entire canvas, not just the two endpoints) - only the server has that
  // synchronously, see todo.md's Logram phase 1 notes.
  //
  // Simplified from the original: occupancy is tracked per EDGE (the segment between
  // two adjacent grid points), not per cell+direction like the original's MapGet/
  // MapSet - two wires sharing a lattice point without sharing an edge (a perpendicular
  // crossing) never conflict, but two wires that would run parallel over the same edge
  // do: edgeCost's caller (LogramGraphController) blocks that edge outright when a
  // different signal source already claimed it, and prices it cheaply when the same
  // source did, which is what lets sinks fed by one source converge onto a shared trunk
  // instead of drawing separate overlapping lines.
  internal static class LogramWireRouter {
    // No CellSize constant here any more: nothing in the solution referenced it (the
    // client keeps its own CELL = 16 and everything on the wire is sent in grid cells),
    // so it was a second, unenforced copy of a value that must match the client's -
    // authoritative-looking and never actually checked against anything.

    // Charged on top of the step's own cost whenever a step changes direction - mirrors
    // loBinding.FindPath's turn penalty (originally +8, raised here to strongly favor
    // straighter routes). Unlike the original this is a genuine EDGE cost rather than a
    // fudge applied to a (x,y)-keyed node, because direction is part of the search state
    // below - see StateKey.
    private const int TurnPenalty = 32;

    // Cap on how many distinct search states get expanded before giving up and falling
    // back (see FallbackPath). Counts states, not cells - a cell can be entered from up
    // to 4 directions, so this is deliberately larger than the 3000 the old (x,y)-keyed
    // search used, while the far stronger heuristic below means a typical route settles
    // in a small fraction of it.
    private const int SearchLimit = 6000;

    internal struct GridPoint {
      public int X;
      public int Y;
      public GridPoint(int x, int y) { X = x; Y = y; }
    }

    // isBlocked(x,y) - true when cell (x,y) may not be routed through: covered by an
    // element footprint other than the path's own start/finish, or outside the canvas
    // (the caller owns both tests - see LogramGraphController.IsBlocked).
    // edgeCost(x1,y1,x2,y2) - cost of the step between two adjacent cells: null means
    // the edge is claimed by a different wire and must not be crossed in parallel
    // (forces a detour, so two unrelated wires can only ever meet at a perpendicular
    // crossing); otherwise the returned cost replaces the flat per-step cost, so the
    // caller can price re-using an edge already drawn for the same source cheaply.
    // minStepCost - the cheapest value edgeCost can possibly return for this particular
    // call, which is what the heuristic is scaled by; passing anything higher than the
    // true minimum makes the heuristic inadmissible and the result no longer optimal.
    internal static List<GridPoint> FindPath(GridPoint startCell, GridPoint finishCell, Func<int, int, bool> isBlocked, Func<int, int, int, int, int?> edgeCost, int minStepCost) {
      PriorityQueue<PathNode> open = new PriorityQueue<PathNode>(new ComparePathNode());
      // Every node ever created, indexed by its own Self - the path is reconstructed by
      // walking Parent indices, NOT by searching back through closed nodes for one whose
      // coordinates match a stored PX/PY. That search was ambiguous once a cell could be
      // closed more than once (it returned the latest such node, not this node's actual
      // predecessor) and could walk in a cycle; a parent INDEX is always strictly smaller
      // than its child's, so the walk below is unambiguous and provably terminates.
      List<PathNode> nodes = new List<PathNode>();
      Dictionary<long, int> bestG = new Dictionary<long, int>();
      HashSet<long> expanded = new HashSet<long>();

      PathNode start;
      start.X = startCell.X;
      start.Y = startCell.Y;
      start.Dir = -1;
      start.G = 0;
      start.F = Heuristic(startCell.X, startCell.Y, finishCell, minStepCost);
      start.Parent = -1;
      start.Self = 0;
      nodes.Add(start);
      bestG[StateKey(start.X, start.Y, start.Dir)] = 0;
      open.Push(start);

      int finishIndex = -1;
      while(open.Count > 0) {
        PathNode cur = open.Pop();
        // Lazy deletion instead of a decrease-key: a state can sit in the heap more than
        // once (a cheaper route to it was found after the first push). The heuristic is
        // consistent and every step costs at least minStepCost, so the FIRST pop of a
        // state is its optimal one and any later copy is simply dropped here.
        if(!expanded.Add(StateKey(cur.X, cur.Y, cur.Dir))) continue;
        if(cur.X == finishCell.X && cur.Y == finishCell.Y) {
          finishIndex = cur.Self;
          break;
        }
        if(expanded.Count > SearchLimit) break;

        for(int i = 0; i < Directions.GetLength(0); i++) {
          int nx = cur.X + Directions[i, 0];
          int ny = cur.Y + Directions[i, 1];
          bool isEndpoint = (nx == startCell.X && ny == startCell.Y) || (nx == finishCell.X && ny == finishCell.Y);
          if(!isEndpoint && isBlocked(nx, ny)) continue;

          // The segment touching a pin must be horizontal (pins sit on an element's
          // left/right edge, never its top/bottom): no vertical step out of the start
          // pin's cell, and none into the finish pin's cell. Both tests are on the CELL,
          // not on "is this the start node" - keyed on the node, a route could step off
          // the pin horizontally and immediately back onto it, arriving in a state whose
          // direction was no longer the start's, and leave vertically from there. That
          // spur cost more but was still the cheapest option often enough to show up,
          // and Simplify then collapsed the out-and-back into nothing, emitting a
          // polyline that left the pin vertically after all. An element placed hard
          // against the canvas edge can be left with no legal move at all - deliberate:
          // the resulting fallback route is the signal that the element sits somewhere a
          // wire cannot reach its pin.
          bool atStartCell = cur.X == startCell.X && cur.Y == startCell.Y;
          bool intoFinishCell = nx == finishCell.X && ny == finishCell.Y;
          if(Directions[i, 1] != 0 && (atStartCell || intoFinishCell)) continue;

          // No doubling straight back on itself. Directions is ordered so that opposites
          // sum to 3 (X+ 0 <-> X- 3, Y- 1 <-> Y+ 2), hence the 3 - Dir. This costs no
          // reachable route: a reversal revisits the cell just left, and splicing the two
          // steps out is always strictly cheaper (it drops two steps plus the reversal's
          // own guaranteed turn, and can add back at most one turn), so no optimal route
          // ever contained one. What it does remove is the whole class of out-and-back
          // spurs - the shape that previously let a route launder its direction at a pin,
          // and the only shape that could put a zero-length segment in front of Simplify.
          if(cur.Dir >= 0 && i == 3 - cur.Dir) continue;

          int? stepCost = edgeCost(cur.X, cur.Y, nx, ny);
          if(stepCost == null) continue;
          int newG = cur.G + stepCost.Value;
          if(cur.Dir >= 0 && cur.Dir != i) newG += TurnPenalty;

          long nextKey = StateKey(nx, ny, i);
          if(expanded.Contains(nextKey)) continue;
          int previousG;
          if(bestG.TryGetValue(nextKey, out previousG) && previousG <= newG) continue;
          bestG[nextKey] = newG;

          PathNode next;
          next.X = nx;
          next.Y = ny;
          next.Dir = i;
          next.G = newG;
          next.F = newG + Heuristic(nx, ny, finishCell, minStepCost);
          next.Parent = cur.Self;
          next.Self = nodes.Count;
          nodes.Add(next);
          open.Push(next);
        }
      }

      if(finishIndex < 0) return FallbackPath(startCell, finishCell);

      List<GridPoint> reversed = new List<GridPoint>();
      for(int index = finishIndex; index >= 0; index = nodes[index].Parent) {
        reversed.Add(new GridPoint(nodes[index].X, nodes[index].Y));
      }
      reversed.Reverse();
      // start == finish reconstructs to a single node, but every consumer treats the
      // result as a polyline - restate it as a degenerate two-point one, same as
      // FallbackPath does for its own degenerate case.
      if(reversed.Count < 2) reversed.Add(new GridPoint(finishCell.X, finishCell.Y));
      return Simplify(reversed);
    }

    // Packs (x, y, entry direction) into one search-state key. Direction belongs in the
    // STATE, not merely carried along for the turn test, because TurnPenalty makes a
    // step's cost depend on how the previous cell was entered: keyed on (x,y) alone, the
    // same cell reached straight-on and reached via a turn are one node with two
    // different costs, so a node already closed could later be reached more cheaply and
    // had to be closed a second time - which is what corrupted the old parent-pointer
    // walk. With direction in the key each state has exactly one optimal cost and is
    // expanded exactly once. dir is -1 (the start node) or 0..3, hence the +1 and the
    // 8-wide field; x/y are canvas-bounded well below 65536 by the caller.
    private static long StateKey(int x, int y, int dir) {
      return ((long)x * 65536L + y) * 8L + (dir + 1);
    }

    // Consistent (hence admissible) A* heuristic: Manhattan distance scaled by the
    // cheapest a single step can possibly cost on THIS call. One step changes Manhattan
    // distance by at most 1 and costs at least minStepCost, so h(n) <= c(n,n') + h(n')
    // always holds - which is what makes the first pop of a state optimal and lets the
    // search skip already-expanded states outright.
    //
    // Scaling matters more than it looks: the caller's cheapest step is normally the
    // base edge cost, but drops to the same-source reuse discount once that source has
    // a trunk to merge onto, and the floor has to follow it or the heuristic
    // overestimates. Scaling to the discount unconditionally (as an earlier fix did) is
    // safe but leaves the search with a small fraction of the available guidance -
    // effectively Dijkstra - which is what made long routes exhaust SearchLimit and
    // fall back to a straight line. Deliberately NOT strengthened with a "+1 turn if
    // both axes differ" term: that is admissible but not consistent, and would put
    // state reopening back on the table for a marginal gain.
    private static int Heuristic(int x, int y, GridPoint finish, int minStepCost) {
      return minStepCost * (Math.Abs(x - finish.X) + Math.Abs(y - finish.Y));
    }

    // Used when no route exists (boxed in, or SearchLimit hit). Deliberately a Z - out
    // horizontally from the start pin, across, then horizontally into the finish pin -
    // rather than the single diagonal segment this used to return. Every segment is
    // axis-aligned, which is the invariant LogramGraphController's ClaimEdges/ClaimCells
    // walk relies on: a diagonal made them claim a long horizontal run of edges the wire
    // never occupied, and those phantom claims then hard-blocked later wires into
    // fallbacks of their own, cascading a whole diagram into straight lines. It also
    // keeps the horizontal-at-the-pin rule at both ends, so a fallback still LOOKS like
    // a wire rather than announcing itself as a diagonal error state.
    private static List<GridPoint> FallbackPath(GridPoint start, GridPoint finish) {
      List<GridPoint> points = new List<GridPoint>();
      AddPoint(points, start);
      if(start.Y != finish.Y) {
        int mid = (start.X + finish.X) / 2;
        AddPoint(points, new GridPoint(mid, start.Y));
        AddPoint(points, new GridPoint(mid, finish.Y));
      }
      AddPoint(points, finish);
      // A degenerate route (start == finish) would otherwise collapse to a single point,
      // which every consumer treats as a polyline - restate it directly rather than
      // through AddPoint, whose whole job is to drop exactly this duplicate.
      if(points.Count < 2) points.Add(finish);
      return points;
    }

    // Skips a point identical to the previous one, so the Z above collapses cleanly when
    // start and finish already share a column - consumers (ClaimEdges' axis walk, the
    // client's corner rounding) divide by segment length and must never see a zero-length one.
    private static void AddPoint(List<GridPoint> points, GridPoint p) {
      if(points.Count > 0) {
        GridPoint last = points[points.Count - 1];
        if(last.X == p.X && last.Y == p.Y) return;
      }
      points.Add(p);
    }

    // Merges consecutive collinear points into their endpoints only, so a straight
    // run of grid steps becomes a single segment (purely a payload-size optimization;
    // the unsimplified polyline would render identically).
    //
    // "Same axis" is not enough on its own - the step direction has to match in SIGN
    // too, or a 180-degree reversal (out one cell and straight back) reads as collinear
    // and both of its points get dropped, silently turning the emitted polyline into a
    // different path than the one that was actually costed and validated.
    private static List<GridPoint> Simplify(List<GridPoint> points) {
      if(points.Count <= 2) return points;
      List<GridPoint> result = new List<GridPoint> { points[0] };
      for(int i = 1; i < points.Count - 1; i++) {
        GridPoint prev = result[result.Count - 1];
        GridPoint cur = points[i];
        GridPoint next = points[i + 1];
        bool collinear =
          (prev.X == cur.X && cur.X == next.X && Math.Sign(cur.Y - prev.Y) == Math.Sign(next.Y - cur.Y)) ||
          (prev.Y == cur.Y && cur.Y == next.Y && Math.Sign(cur.X - prev.X) == Math.Sign(next.X - cur.X));
        if(!collinear) result.Add(cur);
      }
      result.Add(points[points.Count - 1]);
      return result;
    }

    // 0=X+, 1=Y-, 2=Y+, 3=X-
    private static readonly int[,] Directions = { { 1, 0 }, { 0, -1 }, { 0, 1 }, { -1, 0 } };

    private struct PathNode {
      public int F;
      public int G;
      public int X;
      public int Y;
      public int Dir;     // index into Directions this cell was entered by; -1 at the start
      public int Parent;  // index into FindPath's `nodes`; -1 at the start
      public int Self;    // this node's own index into that same list
    }

    private sealed class ComparePathNode : IComparer<PathNode> {
      public int Compare(PathNode x, PathNode y) {
        if(x.F > y.F) return 1;
        if(x.F < y.F) return -1;
        return 0;
      }
    }

    // Minimal binary-heap priority queue, ported from ES/Logram/PriorityQueue.cs.
    private sealed class PriorityQueue<T> {
      private readonly List<T> _items = new List<T>();
      private readonly IComparer<T> _comparer;

      internal PriorityQueue(IComparer<T> comparer) { _comparer = comparer; }

      internal int Count { get { return _items.Count; } }

      internal T this[int index] { get { return _items[index]; } }

      internal void Push(T item) {
        int p = _items.Count;
        _items.Add(item);
        while(p > 0) {
          int parent = (p - 1) / 2;
          if(_comparer.Compare(_items[p], _items[parent]) < 0) {
            Swap(p, parent);
            p = parent;
          }
          else break;
        }
      }

      internal T Pop() {
        T result = _items[0];
        _items[0] = _items[_items.Count - 1];
        _items.RemoveAt(_items.Count - 1);
        int p = 0;
        while(true) {
          int pn = p, l = 2 * p + 1, r = 2 * p + 2;
          if(l < _items.Count && _comparer.Compare(_items[p], _items[l]) > 0) p = l;
          if(r < _items.Count && _comparer.Compare(_items[p], _items[r]) > 0) p = r;
          if(p == pn) break;
          Swap(p, pn);
        }
        return result;
      }

      private void Swap(int i, int j) {
        T tmp = _items[i];
        _items[i] = _items[j];
        _items[j] = tmp;
      }
    }
  }
}
