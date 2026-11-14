// Сцена Logram: из строк документа - где что стоит на экране, в px. Без Lit и без DOM: рисование
// (logram-document.js), hit-test и рамка выделения (logram-hit-test.js) берут одни и те же числа и
// разойтись не могут.

export const CELL = 18;
// How far a block's drawn body reaches above its first pin row and below its last one:
// a hair under the half-cell the layout actually reserves, so two blocks placed on
// adjacent rows keep a 2px seam between them instead of their edges touching and
// reading as one shape.
const BLOCK_EDGE = CELL / 2 - 1;
// Half a cell of white beyond the content on every side (canvasSize). The SVG viewport
// clips at the canvas edge, so without it a wire routed along the topmost row - or an
// element's own border - draws with half its stroke outside and comes out visibly thinner
// than the same line one cell further in.
export const CANVAS_PAD = CELL / 2;
const MIN_CANVAS_CELLS = { w: 20, h: 14 };
// The leftmost column a BLOCK may be placed in - column 0 is kept clear for wires. An
// input pin sits ON its element's own left edge, so a wire reaching it has to occupy the
// cell one to the left first, and the router calls x < 0 off-canvas
// (LogramGraphController.IsBlocked): a block at column 0 gets no route in at all, only
// the straight fallback line through whatever is in the way. The right edge gets the
// same clearance from canvasSize's spare column instead - there is no element to move
// there, so the canvas simply grows. Vertically neither is needed: a wire approaches a
// pin horizontally, and the rows above/below a pin are free canvas already.
//
// Variables are deliberately exempt and may sit flush against the edge: a variable is
// how a diagram reaches a topic outside itself, its usual source is that binding rather
// than a wire (nothing is drawn for a non-local source, see logram-document.js
// #renderWire), and the left column is exactly where such an input belongs.
export const MIN_LEFT_CELL = 1;
// How far a wire's drawn line stops short of a corner before curving through it (see
// wirePathD, which clamps it to half of the shorter adjacent run so adjacent curves can
// never overlap). At half a CELL the clamp binds on every single-cell run, so a tight
// zigzag draws as one continuous S-curve with no straight portion left between corners -
// deliberate, but it is also the point of maximum divergence from the un-rounded polyline
// that .wire-hit and pointNearSegment (logram-hit-test.js) still hit-test against.
const WIRE_CORNER_RADIUS = CELL / 2;

// Где элемент стоит на экране, в px. Единственное место, где клетки сервера (x/y/width/height)
// становятся пикселями: рисование (logram-document.js), hit-test и рамка выделения (через
// elementGeometry), якоря pin-ов (pinAnchors) берут одно и то же и разойтись не могут. Пока
// считали каждый сам, рисунок блока шёл от первой строки pin-ов вниз, а hit-test центрировал его на
// ней же - и у высокого блока клик промахивался по низу и попадал в пустоту над ним.
//
// anchorY - строка первого pin, привязанная к раскладке сервера (GetPinCell: Y + index), чтобы
// провода попадали в точки pin-ов. Тело блока (y0..y1) начинается на BLOCK_EDGE выше первой строки
// pin-ов и кончается на BLOCK_EDGE ниже последней; тело переменной центрировано на её якоре.
export function blockGeometry(el) {
  const x0 = (el.x || 0) * CELL;
  const anchorY = (el.y || 0) * CELL;
  const lastPinY = anchorY + ((el.height || 2) - 1) * CELL;
  return { x0, x1: x0 + (el.width || 2) * CELL, anchorY, y0: anchorY - BLOCK_EDGE, y1: lastPinY + BLOCK_EDGE };
}

export function variableGeometry(el) {
  const x0 = (el.x || 0) * CELL;
  const anchorY = (el.y || 0) * CELL;
  const half = (el.height || 1) * CELL / 2 - (CELL / 2 - BLOCK_EDGE);
  return { x0, x1: x0 + (el.width || 2) * CELL, anchorY, y0: anchorY - half, y1: anchorY + half };
}

// Занятое элементом место на экране - ровно нарисованное тело. Сырой след в клетках, без сдвига, -
// это LogramGraphController.IsBlocked на сервере, занятость для трассировки проводов; там своя задача.
export function elementGeometry(el) {
  return el.editor === 'LogramBlock' ? blockGeometry(el) : variableGeometry(el);
}

export function isElementRow(row) {
  return row.editor === 'LogramBlock' || row.editor === 'LogramVariable';
}

// Всё, что нужно и рисованию, и hit-test, - одним проходом по строкам. Документ строит сцену один
// раз на изменение строк, а не на каждое нажатие мыши.
export function buildScene(rows) {
  const elements = rows.filter(isElementRow);
  const { pinsByParent, rowsByVid } = indexRows(rows);
  const pins = pinAnchors(elements, pinsByParent);
  return { elements, pinsByParent, rowsByVid, pins, wires: wireSegments(pins) };
}

// Groups pin rows by owning element vid and indexes every row by its own vid -
// shared between drawing and hit-testing so both agree on exactly the same
// pin/element grouping, rather than two independent derivations drifting apart.
function indexRows(rows) {
  const pinsByParent = new Map();
  const rowsByVid = new Map();
  for(const row of rows) {
    rowsByVid.set(row.vid, row);
    if(row.editor !== 'LogramPin') continue;
    const parentVid = row.vid.slice(0, row.vid.lastIndexOf('/'));
    if(!pinsByParent.has(parentVid)) pinsByParent.set(parentVid, []);
    pinsByParent.get(parentVid).push(row);
  }
  return { pinsByParent, rowsByVid };
}

// Every pin anchor (in px), for both hitTest and wireSegments - the same geometry
// #renderBlock/#renderVariable draw from, so hit-testing never drifts from what's
// actually on screen. `wireRow` is the row whose sourcePath/sourceVid describes a wire
// ENDING at this anchor (a block pin's own row, or - only on a variable's "in" side,
// never "out", since both sides share one row - the variable's own row) - null where
// an anchor can't be a wire sink.
function pinAnchors(elements, pinsByParent) {
  const pins = [];
  for(const el of elements) {
    const g = elementGeometry(el);
    if(el.editor === 'LogramBlock') {
      for(const row of pinsByParent.get(el.vid) || []) {
        const isInput = row.pinDirection === 'in';
        pins.push({ vid: row.vid, side: row.pinDirection, px: isInput ? g.x0 : g.x1, py: g.anchorY + (row.pinIndex || 0) * CELL, selectable: true, wireRow: row });
      }
    }
    else {
      pins.push({ vid: el.vid, side: 'in', px: g.x0, py: g.anchorY, selectable: false, wireRow: el });
      pins.push({ vid: el.vid, side: 'out', px: g.x1, py: g.anchorY, selectable: false, wireRow: null });
    }
  }
  return pins;
}

// One entry per drawn wire (not per segment - hitTest walks a wire's own segments
// itself), from the same points #renderWire draws.
function wireSegments(pins) {
  const wires = [];
  for(const pin of pins) {
    const row = pin.wireRow;
    if(!row || !row.sourceVid || !row.sourceLocal) continue;
    wires.push({ vid: row.vid, points: wirePoints(row, pin.px, pin.py) });
  }
  return wires;
}

// sourcePath points are server-routed (LogramWireRouter.FindPath) waypoints in
// grid cells - FindPath's own native unit, same reason element x/y stay in cells
// (see canvasSize's comment) - multiplied by CELL here, same as everywhere else a
// cell coordinate becomes a pixel one. sinkX/sinkY are already px (the pin anchor) -
// only a fallback when a path hasn't arrived yet (e.g. this pin row landed before
// its wire's evnt.upd).
export function wirePoints(row, sinkX, sinkY) {
  return Array.isArray(row.sourcePath) && row.sourcePath.length > 0
    ? row.sourcePath.map((p) => ({ x: (p.x || 0) * CELL, y: (p.y || 0) * CELL }))
    : [{ x: sinkX, y: sinkY }, { x: sinkX, y: sinkY }];
}

// The box the surface draws: the elements' own bounding box (x/y/width/height all in grid
// cells, same units the server routed against - see logram-document.js #renderBlock's
// comment on why they stay in cells), floored at MIN_CANVAS_CELLS so a new/empty diagram
// isn't a sliver, plus CANVAS_PAD of white on every side, and one spare CELL past the
// rightmost element. That `+ 1` is wire room, not decoration: a block's width already
// reaches its output pins (baked into WidthCells server-side, see
// LogramGraphController.BuildLayout), so a wire leaving the rightmost block's output has to
// step into the column beyond it, and without that column the step is off-canvas and the
// route fails. MIN_LEFT_CELL keeps the mirror column free on the left. Nothing is added on
// the top or bottom (see MIN_LEFT_CELL) and no spare margin beyond that: this runs on every
// render, mid-drag included, so the canvas simply follows a block dragged or dropped past
// its current edge instead of keeping room in reserve for one. The server bounds its wire
// search by the same box (LogramGraphController.ResolveWires, which adds the same one
// column) - a route outside it would be clipped by the SVG viewport rather than drawn.
export function canvasSize(elements) {
  let rightCells = 0, bottomCells = 0;
  for(const el of elements) {
    const w = el.width || 2;
    const h = el.height || 2;
    rightCells = Math.max(rightCells, (el.x || 0) + w);
    bottomCells = Math.max(bottomCells, (el.y || 0) + h);
  }
  const widthCells = Math.max(MIN_CANVAS_CELLS.w, rightCells + 1);
  const heightCells = Math.max(MIN_CANVAS_CELLS.h, bottomCells);
  return { x: -CANVAS_PAD, y: -CANVAS_PAD, width: widthCells * CELL + 2 * CANVAS_PAD, height: heightCells * CELL + 2 * CANVAS_PAD };
}

// Rounds off each interior corner of an otherwise-straight polyline with a small
// quadratic curve instead of a sharp right angle - every corner here is a 90-degree
// grid turn (see LogramWireRouter), so "round it" just means stopping the straight
// run short of the corner by WIRE_CORNER_RADIUS and curving through the corner point
// to the same distance into the next run. The radius is clamped to half of whichever
// adjacent run is shorter, so two corners close together (a short zigzag) never
// overlap or overshoot past the run's own midpoint. Purely cosmetic - the underlying
// points, and hit-testing (wirePathD is only used for the visible .wire path, never
// .wire-hit), are untouched.
export function wirePathD(points) {
  if(points.length <= 2) return points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ');
  let d = `M${points[0].x},${points[0].y}`;
  for(let i = 1; i < points.length - 1; i++) {
    const prev = points[i - 1], cur = points[i], next = points[i + 1];
    const inLen = Math.hypot(cur.x - prev.x, cur.y - prev.y);
    const outLen = Math.hypot(next.x - cur.x, next.y - cur.y);
    // A repeated waypoint would divide by zero below, and a single NaN in `d` makes
    // the browser drop the WHOLE path - the wire would vanish while .wire-hit (built
    // from the raw points) stayed clickable. Degrade to a sharp corner instead.
    if(inLen === 0 || outLen === 0) { d += ` L${cur.x},${cur.y}`; continue; }
    const r = Math.min(WIRE_CORNER_RADIUS, inLen / 2, outLen / 2);
    const inX = cur.x - (cur.x - prev.x) / inLen * r;
    const inY = cur.y - (cur.y - prev.y) / inLen * r;
    const outX = cur.x + (next.x - cur.x) / outLen * r;
    const outY = cur.y + (next.y - cur.y) / outLen * r;
    d += ` L${inX},${inY} Q${cur.x},${cur.y} ${outX},${outY}`;
  }
  const last = points[points.length - 1];
  d += ` L${last.x},${last.y}`;
  return d;
}
