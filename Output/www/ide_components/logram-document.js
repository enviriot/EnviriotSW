import { LitElement, html, css, svg } from '../lib/lit-all.min.js';
import './context-menu.js';

const CELL = 16;
const MIN_SCALE = 0.5;
const MAX_SCALE = 5;
// Canvas has no server-side size (LogramGraphController doesn't send one, see
// #canvasSize) - it's the elements' own bounding box plus this much empty cell
// space around it, so there's always room to drop a new block or pan a bit
// without the surface's edge (and its SVG clip) being right against content.
const CANVAS_MARGIN_CELLS = 3;
const MIN_CANVAS_CELLS = { w: 20, h: 14 };
// How far a wire's drawn line stops short of a corner before curving through it (see
// #wirePathD, which clamps it to half of the shorter adjacent run so adjacent curves can
// never overlap). At half a CELL the clamp binds on every single-cell run, so a tight
// zigzag draws as one continuous S-curve with no straight portion left between corners -
// deliberate, but it is also the point of maximum divergence from the un-rounded polyline
// that .wire-hit and #pointNearSegment still hit-test against.
const WIRE_CORNER_RADIUS = CELL / 2;

export class X13LogramDocument extends LitElement {
  static properties = {
    api: { attribute: false },
    store: { attribute: false },
    rows: { attribute: false },
    scale: { type: Number },
    tx: { type: Number },
    ty: { type: Number },
    selected: { attribute: false },
    dragState: { attribute: false },
    wireDraft: { attribute: false },
    marquee: { attribute: false },
    menuState: { attribute: false },
  };

  static styles = css`
    :host { display: block; height: 100%; overflow: hidden; position: relative; width: 100%; }
    .surface { user-select: none; }
    /* Base cursor is pointer (empty canvas, per #hitTest's own rules) - pan-in-progress
       (ctrl+drag, see #onSurfacePointerDown) overrides to grabbing; the four hoverable
       hit-test targets (pin/wire/element) below all override back to default, since
       their actual behavior (select/menu/drag) isn't a simple "click here" affordance. */
    .viewport { background: #eef3f8; cursor: pointer; height: 100%; overflow: hidden; width: 100%; }
    .viewport.panning { cursor: grabbing; }
    .surface { transform-origin: 0 0; }
    .zoom-controls { display: flex; flex-direction: column; gap: 4px; position: absolute; right: 8px; top: 8px; }
    .zoom-controls button {
      background: #fff;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #243447;
      cursor: pointer;
      font-size: 14px;
      height: 26px;
      width: 26px;
    }
    .block-body { fill: #4682b4; }
    .block-body.selected, .variable-body.selected { fill: #ff6347; }
    .variable-body { fill: #4682b4; rx: 4; }
    .variable-label { fill: #fff; font-family: Consolas, monospace; font-size: 11px; pointer-events: none; }
    .pin-label { fill: #243447; font-family: Consolas, monospace; font-size: 10px; pointer-events: none; }
    .pin-trace { fill: #b45309; font-family: Consolas, monospace; font-size: 10px; font-weight: 700; pointer-events: none; }
    .block-icon { pointer-events: none; }
    .pin-dot { pointer-events: none; }
    .pin-dot.selected { stroke: #ff6347; stroke-width: 2; }
    .wire { fill: none; pointer-events: none; stroke-width: 2; }
    .wire.selected { stroke: #ff6347 !important; stroke-width: 3; }
    /* wire-hit's own stroke-width (CELL, "a cell across") only approximates
       #pointNearSegment's real per-segment rectangle test at corners (SVG stroke
       joins/caps aren't a true capsule) - close enough for :hover cursor feedback;
       #hitTest (used for the actual click/menu/drag logic) does the exact math. */
    .pin-hit, .wire-hit, .element-hit, .block-body, .variable-body { cursor: default; }
    .pin-hit, .element-hit { fill: transparent; }
    .wire-hit { fill: none; stroke: transparent; stroke-width: 16; }
    .wire-draft { fill: none; pointer-events: none; stroke: #2563eb; stroke-dasharray: 4 3; stroke-width: 2; }
    .marquee { fill: rgba(37, 99, 235, 0.1); pointer-events: none; stroke: #2563eb; stroke-dasharray: 4 3; }
    .grid-bg { fill: #fff; stroke: #cfd8e3; }
    .grid-dots { pointer-events: none; }
  `;

  constructor() {
    super();
    this.rows = [];
    this.scale = 2.5;
    this.tx = 0;
    this.ty = 0;
    this.selected = null;
    this.dragState = null;
    this.wireDraft = null;
    this.marquee = null;
    this.menuState = null;
    this.unsubscribe = null;
    // Set whenever a new document's rows haven't started arriving yet (see
    // #subscribeStore) - checked on every update() until the LogramCanvas row
    // actually arrives (#tryCenterView), so centering happens exactly once per
    // opened document, whichever render that data lands on.
    this.pendingCenter = false;
    this.onKeyDown = (e) => this.#handleKeyDown(e);
  }

  connectedCallback() {
    super.connectedCallback();
    window.addEventListener('keydown', this.onKeyDown);
  }

  updated(changed) {
    if(changed.has('store')) this.#subscribeStore();
    if(this.pendingCenter) this.#tryCenterView();
  }

  disconnectedCallback() {
    window.removeEventListener('keydown', this.onKeyDown);
    this.unsubscribe?.();
    this.unsubscribe = null;
    super.disconnectedCallback();
  }

  render() {
    let elements = this.rows.filter((row) => row.editor === 'LogramBlock' || row.editor === 'LogramVariable');
    if(this.dragState) {
      // Live preview during a drag: override every dragged row's position by the same
      // (unsnapped) in-progress delta, without touching the store - the actual commit
      // only happens once per row, on pointerup. A shared dx/dy (not per-row absolute
      // coords) keeps a multi-selection's relative layout intact regardless of which
      // element in the set was actually grabbed. #canvasSize sees this too, so the
      // surface grows live as a block is dragged past its current edge, instead of
      // only on the next commit (as the server-persisted-size approach this replaced
      // could only ever manage).
      const { vids, dx, dy } = this.dragState;
      elements = elements.map((el) => vids.has(el.vid) ? { ...el, x: (el.x || 0) + dx, y: (el.y || 0) + dy } : el);
    }
    const { width, height } = this.#canvasSize(elements);
    const { pinsByParent, rowsByVid } = this.#sceneRows();

    return html`
      <div class="viewport" @pointerdown=${this.#onSurfacePointerDown} @wheel=${this.#onWheel} @contextmenu=${this.#onSurfaceContextMenu}
        @dragover=${this.#onSurfaceDragOver} @drop=${this.#onSurfaceDrop}>
        <svg class="surface" width=${width} height=${height}
          style="transform:translate(${this.tx}px,${this.ty}px) scale(${this.scale})">
          <defs>
            <!-- x/y=-8 shifts where each tile repeat lands, without moving the mark
                 within its own tile (still centered at local 8,8, so nothing here
                 needs overflow:visible) - net effect is the mark itself renders 8px
                 up-left of where it used to, landing on the grid's actual corners
                 (multiples of CELL, where element x/y=0 sits) instead of a cell's
                 center. -->
            <pattern id="grid-cross" x="-8" y="-8" width=${CELL} height=${CELL} patternUnits="userSpaceOnUse">
              <path d="M 6.5 8 h 3 M 8 6.5 v 3" stroke="#dde2e8" stroke-opacity="0.6" stroke-width="1"></path>
            </pattern>
          </defs>
          <rect class="grid-bg" x="0" y="0" width=${width} height=${height}></rect>
          <rect class="grid-dots" x="0" y="0" width=${width} height=${height} fill="url(#grid-cross)"></rect>
          ${elements.map((el) => this.#renderElement(el, pinsByParent.get(el.vid) || [], rowsByVid))}
          ${this.wireDraft ? svg`<line class="wire-draft" x1=${this.wireDraft.originX} y1=${this.wireDraft.originY} x2=${this.wireDraft.x} y2=${this.wireDraft.y}></line>` : svg``}
          ${this.marquee ? svg`<rect class="marquee" x=${this.marquee.x0} y=${this.marquee.y0} width=${this.marquee.x1 - this.marquee.x0} height=${this.marquee.y1 - this.marquee.y0}></rect>` : svg``}
        </svg>
      </div>
      <div class="zoom-controls">
        <button @click=${() => this.#zoomBy(1.2)} title="Zoom in">+</button>
        <button @click=${() => this.#zoomBy(1 / 1.2)} title="Zoom out">-</button>
        <button @click=${this.#resetView} title="Reset view">⟲</button>
      </div>
      ${this.menuState ? html`
        <x13-context-menu .items=${this.menuState.items} .x=${this.menuState.x} .y=${this.menuState.y}
          @menu-command=${this.#onMenuCommand} @menu-close=${this.#closeMenu}></x13-context-menu>` : html``}`;
  }

  // Bounding box of the given elements (x/y/width/height all in grid cells, same
  // units render() uses to draw them - see #renderBlock's comment on why they stay
  // in cells), plus CANVAS_MARGIN_CELLS of empty space and floored at
  // MIN_CANVAS_CELLS so a new/empty diagram isn't a sliver. No extra `+ 1` needed on
  // the right any more - a block's width already reaches its output pins (baked into
  // WidthCells server-side, see LogramGraphController.BuildLayout).
  #canvasSize(elements) {
    let rightCells = 0, bottomCells = 0;
    for(const el of elements) {
      const w = el.width || 2;
      const h = el.height || 2;
      rightCells = Math.max(rightCells, (el.x || 0) + w);
      bottomCells = Math.max(bottomCells, (el.y || 0) + h);
    }
    const widthCells = Math.max(MIN_CANVAS_CELLS.w, rightCells + CANVAS_MARGIN_CELLS);
    const heightCells = Math.max(MIN_CANVAS_CELLS.h, bottomCells + CANVAS_MARGIN_CELLS);
    return { width: widthCells * CELL, height: heightCells * CELL };
  }

  #renderElement(el, pins, rowsByVid) {
    return el.editor === 'LogramBlock' ? this.#renderBlock(el, pins, rowsByVid) : this.#renderVariable(el, rowsByVid);
  }

  // Groups pin rows by owning element vid and indexes every row by its own vid -
  // shared between render() (drawing) and #hitTest (mouse) so both agree on exactly
  // the same pin/element grouping, rather than two independent derivations drifting
  // apart.
  #sceneRows() {
    const pinsByParent = new Map();
    const rowsByVid = new Map();
    for(const row of this.rows) {
      rowsByVid.set(row.vid, row);
      if(row.editor !== 'LogramPin') continue;
      const parentVid = row.vid.slice(0, row.vid.lastIndexOf('/'));
      if(!pinsByParent.has(parentVid)) pinsByParent.set(parentVid, []);
      pinsByParent.get(parentVid).push(row);
    }
    return { pinsByParent, rowsByVid };
  }

  // x/y/width/height come straight from the server in grid cells (LogramGraphController
  // serializes the exact same numbers it routed wires against) - deriving our own
  // estimate here would drift from that and wires would visibly miss the pins.
  //
  // bodyTop is the pin-anchor box's own top - matches GetPinCell's Y+index (a
  // block's first pin sits flush on its own top row, there's no header row
  // reserved above it any more) and outputs at X+WidthCells - body/pins/outputs
  // all have to move together with that anchor, or the routed wires (computed
  // against GetPinCell) drift off the drawn pins again. `width` already reaches the
  // output pins (the one-cell gap past the body is baked into WidthCells
  // server-side now, see LogramGraphController.BuildLayout), so it doubles as the
  // body's own drawn width - no separate `bodyWidth` any more.
  #renderBlock(el, pins, rowsByVid) {
    const x = (el.x || 0) * CELL;
    const bodyTop = (el.y || 0) * CELL;
    const width = (el.width || 2) * CELL;
    const height = (el.height || 2) * CELL;
    // Drawn top is nudged 8px above the pin-anchor box (bodyTop, which stays tied to
    // the server layout so wires keep landing on the pin dots) - height is left as-is,
    // so the drawn bottom shifts up those same 8px off its own natural edge. Purely a
    // visual trim on top of the server layout, which stays untouched.
    const visualTop = bodyTop - 8;
    const wires = [];
    const pinNodes = pins.map((pin) => {
      const isInput = pin.pinDirection === 'in';
      const px = isInput ? x : x + width;
      const py = bodyTop + (pin.pinIndex || 0) * CELL;
      if(pin.sourceVid) wires.push(this.#renderWire(pin, px, py, rowsByVid));
      const pinSelected = this.selected?.type === 'pin' && this.selected.vid === pin.vid;
      return svg`
        <g @dblclick=${(e) => { e.stopPropagation(); this.#openInInspector(pin.vid); }}>
          <rect class="pin-hit" data-vid=${pin.vid} data-side=${pin.pinDirection} x=${px - CELL / 2} y=${py - CELL / 2} width=${CELL} height=${CELL}></rect>
          <circle class="pin-dot ${pinSelected ? 'selected' : ''}" cx=${px} cy=${py} r="3" fill=${pin.sourceVid && !pin.sourceLocal ? '#9aa5b1' : (pin.color || '#9aa5b1')}></circle>
          <text class="pin-label" x=${isInput ? px + 6 : px - 6} y=${py + 3} text-anchor=${isInput ? 'start' : 'end'}>${pin.name}</text>
          ${pin.trace ? svg`<text class="pin-trace" x=${isInput ? px - 6 : px + 6} y=${py - 8} text-anchor=${isInput ? 'end' : 'start'}>${pin.displayValue || ''}</text>` : svg``}
        </g>`;
    });
    const isSelected = this.#isElementSelected(el.vid);
    return svg`
      <g @dblclick=${() => this.#openInInspector(el.vid)}>
        <rect class="element-hit" x=${x} y=${bodyTop - height / 2} width=${width} height=${height}></rect>
        ${wires}
        <rect class="block-body ${isSelected ? 'selected' : ''}" x=${x} y=${visualTop} width=${width} height=${height} rx="3">
          <title>${el.name}</title>
        </rect>
        ${el.icon ? svg`<image class="block-icon" href=${el.icon} x=${x + width / 2 - 7} y=${visualTop} width="16" height="16"></image>` : svg``}
        ${pinNodes}
      </g>`;
  }

  // A variable's pin anchors sit at exactly (x, y*CELL) / (x+width, y*CELL) - no
  // extra half-cell offset - to match GetVariableCell's routing anchors on the
  // server and BuildLayout's block pins (py = (y+index)*CELL, also unoffset); the
  // body rect is instead the one shifted, so it still looks vertically centered on
  // the dots. Getting this wrong is what made "enable" render below A01's row.
  #renderVariable(el, rowsByVid) {
    const x = (el.x || 0) * CELL;
    const anchorY = (el.y || 0) * CELL;
    const width = (el.width || 2) * CELL;
    const bodyHeight = (el.height || 1) * CELL - 3;
    const bodyY = anchorY - bodyHeight / 2;
    const wires = el.sourceVid ? [this.#renderWire(el, x, anchorY, rowsByVid)] : [];
    const isSelected = this.#isElementSelected(el.vid);
    return svg`
      <g @dblclick=${() => this.#openInInspector(el.vid)}>
        <rect class="element-hit" x=${x} y=${anchorY - CELL / 2} width=${width} height=${CELL}></rect>
        ${wires}
        <rect class="variable-body ${isSelected ? 'selected' : ''}" x=${x} y=${bodyY} width=${width} height=${bodyHeight} rx="4"></rect>
        <text class="variable-label" x=${x + 5} y=${bodyY + bodyHeight - 3}>${el.name}</text>
        <rect class="pin-hit" data-vid=${el.vid} data-side="in" x=${x - CELL / 2} y=${anchorY - CELL / 2} width=${CELL} height=${CELL}></rect>
        <circle class="pin-dot" cx=${x} cy=${anchorY} r="3" fill=${el.sourceVid && !el.sourceLocal ? '#9aa5b1' : (el.color || '#9aa5b1')}></circle>
        <rect class="pin-hit" data-vid=${el.vid} data-side="out" x=${x + width - CELL / 2} y=${anchorY - CELL / 2} width=${CELL} height=${CELL}></rect>
        <circle class="pin-dot" cx=${x + width} cy=${anchorY} r="3" fill=${el.color || '#9aa5b1'}></circle>
      </g>`;
  }

  // sourcePath points are server-routed (LogramWireRouter.FindPath) waypoints in
  // grid cells - FindPath's own native unit, same reason element x/y stay in cells
  // (see #canvasSize's comment) - multiplied by CELL here, same as everywhere else a
  // cell coordinate becomes a pixel one. sinkX/sinkY are already px (passed in from
  // #renderBlock/#renderVariable) - only a fallback when a path hasn't arrived yet
  // (e.g. this pin row landed before its wire's evnt.upd).
  // Colored by the SOURCE's color (matching WPF, where a wire's Pen came from the
  // upstream/output pin's brush), not the sink's own - both source and sink rows
  // already carry a server-computed `color` (see LogramGraphController.ColorForValue),
  // same field the source's own dot uses, so this is never out of sync with it.
  #renderWire(row, sinkX, sinkY, rowsByVid) {
    if(!row.sourceLocal) return svg``;
    const points = Array.isArray(row.sourcePath) && row.sourcePath.length > 0
      ? row.sourcePath.map((p) => ({ x: (p.x || 0) * CELL, y: (p.y || 0) * CELL }))
      : [{ x: sinkX, y: sinkY }, { x: sinkX, y: sinkY }];
    // wire-hit stays straight-segmented - #hitTest/#pointNearSegment test against the
    // exact same points, so rounding it too would only make the hit math approximate
    // for no benefit (see the class's own comment above). Only the visible line (.wire)
    // gets the rounded treatment.
    const d = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ');
    const sourceRow = rowsByVid.get(row.sourceVid);
    const isSelected = this.selected?.type === 'wire' && this.selected.vid === row.vid;
    return svg`
      <path class="wire-hit" data-vid=${row.vid} d=${d}></path>
      <path class="wire ${isSelected ? 'selected' : ''}" d=${this.#wirePathD(points)} stroke=${(sourceRow ? sourceRow.color : row.color) || '#9aa5b1'}></path>`;
  }

  // Rounds off each interior corner of an otherwise-straight polyline with a small
  // quadratic curve instead of a sharp right angle - every corner here is a 90-degree
  // grid turn (see LogramWireRouter), so "round it" just means stopping the straight
  // run short of the corner by WIRE_CORNER_RADIUS and curving through the corner point
  // to the same distance into the next run. The radius is clamped to half of whichever
  // adjacent run is shorter, so two corners close together (a short zigzag) never
  // overlap or overshoot past the run's own midpoint. Purely cosmetic - the underlying
  // points, and hit-testing (#wirePathD is only used for the visible .wire path, never
  // .wire-hit), are untouched.
  #wirePathD(points) {
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

  #subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.rows = [];
    this.scale = 1;
    this.tx = 0;
    this.ty = 0;
    this.pendingCenter = true;
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((rows) => {
      this.rows = rows;
    });
  }

  // Runs on every update() while pendingCenter is set (see constructor) - a no-op
  // until the LogramCanvas row actually arrives from the server (SendSnapshot
  // always sends it first, ahead of any element/pin rows - see
  // LogramGraphController.SendSnapshot), so this fires on the very first render
  // that has anything at all to center against, rather than the still-empty one.
  #tryCenterView() {
    const canvasRow = this.rows.find((row) => row.editor === 'LogramCanvas');
    if(!canvasRow) return;
    this.pendingCenter = false;
    const elements = this.rows.filter((row) => row.editor === 'LogramBlock' || row.editor === 'LogramVariable');
    const { width, height } = this.#canvasSize(elements);
    this.#centerView(width, height);
  }

  #centerView(width, height) {
    const rect = this.renderRoot.querySelector('.viewport')?.getBoundingClientRect();
    this.tx = rect ? (rect.width - width * this.scale) / 2 : 0;
    this.ty = rect ? (rect.height - height * this.scale) / 2 : 0;
  }

  #openInInspector(vid) {
    const path = vid.slice(vid.indexOf('#') + 1);
    this.dispatchEvent(new CustomEvent('segment-command', { detail: { cmd: 'open', path }, bubbles: true, composed: true }));
  }

  #selectWire(e, vid) {
    e.stopPropagation();
    this.selected = { type: 'wire', vid };
  }

  // Single gesture covers both "click to select a pin" (for Delete, see
  // #handleKeyDown) and "drag from a pin to draw a wire" (Phase 3), disambiguated by
  // movement past a small threshold - mirrors ES's actual behavior (LogramView's
  // OnMouseMove only starts a loBinding once the pointer has actually moved off a
  // pin; a plain click-without-move just selects). selectable is false for a
  // variable's two dots (in/out) - a variable has one topic, not two selectable
  // targets, so only the drag-a-wire half applies to them; block pins get both.
  #onPinPointerDown(e, vid, side, originX, originY, selectable) {
    if(e.button !== 0) return;
    e.stopPropagation();
    const startClientX = e.clientX, startClientY = e.clientY;
    let dragging = false;
    const move = (ev) => {
      if(!dragging && Math.hypot(ev.clientX - startClientX, ev.clientY - startClientY) > 4) {
        dragging = true;
        this.selected = null;
      }
      if(dragging) {
        const p = this.#toSvgPoint(ev.clientX, ev.clientY);
        this.wireDraft = { fromVid: vid, fromSide: side, originX, originY, x: p.x, y: p.y };
      }
    };
    const up = (ev) => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      if(dragging) {
        this.wireDraft = null;
        const target = this.#pinAtPoint(ev.clientX, ev.clientY);
        if(target && target.vid !== vid) this.#tryConnect(vid, side, target.vid, target.side);
      }
      else if(selectable) {
        this.selected = (this.selected?.type === 'pin' && this.selected.vid === vid) ? null : { type: 'pin', vid };
      }
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  #toSvgPoint(clientX, clientY) {
    const rect = this.renderRoot.querySelector('.surface').getBoundingClientRect();
    const scale = this.scale || 1;
    return { x: (clientX - rect.left) / scale, y: (clientY - rect.top) / scale };
  }

  #pinAtPoint(clientX, clientY) {
    const el = this.renderRoot.elementFromPoint(clientX, clientY);
    if(!el || !el.classList?.contains('pin-hit')) return null;
    return { vid: el.dataset.vid, side: el.dataset.side };
  }

  // Every pin anchor (in px), for both #hitTest and #wireSegments - mirrors the same
  // px/py math #renderBlock/#renderVariable use to draw, so hit-testing never drifts
  // from what's actually on screen. `wireRow` is the row whose sourcePath/sourceVid
  // describes a wire ENDING at this anchor (a block pin's own row, or - only on a
  // variable's "in" side, never "out", since both sides share one row - the
  // variable's own row) - null where an anchor can't be a wire sink.
  #pinAnchors(elements, pinsByParent) {
    const pins = [];
    for(const el of elements) {
      const x = (el.x || 0) * CELL, y = (el.y || 0) * CELL;
      const width = (el.width || 2) * CELL;
      if(el.editor === 'LogramBlock') {
        for(const row of pinsByParent.get(el.vid) || []) {
          const isInput = row.pinDirection === 'in';
          pins.push({ vid: row.vid, side: row.pinDirection, px: isInput ? x : x + width, py: y + (row.pinIndex || 0) * CELL, selectable: true, wireRow: row });
        }
      }
      else {
        pins.push({ vid: el.vid, side: 'in', px: x, py: y, selectable: false, wireRow: el });
        pins.push({ vid: el.vid, side: 'out', px: x + width, py: y, selectable: false, wireRow: null });
      }
    }
    return pins;
  }

  // One entry per drawn wire (not per segment - #hitTest walks a wire's own segments
  // itself) - reuses the pin anchors' own px/py as the fallback single-segment
  // endpoint, same as #renderWire does.
  #wireSegments(pinAnchors) {
    const wires = [];
    for(const pin of pinAnchors) {
      const row = pin.wireRow;
      if(!row || !row.sourceVid || !row.sourceLocal) continue;
      const points = Array.isArray(row.sourcePath) && row.sourcePath.length > 0
        ? row.sourcePath.map((p) => ({ x: (p.x || 0) * CELL, y: (p.y || 0) * CELL }))
        : [{ x: pin.px, y: pin.py }, { x: pin.px, y: pin.py }];
      wires.push({ vid: row.vid, points });
    }
    return wires;
  }

  // The element's occupied footprint in px (x0/y0 .. x1/y1), centered on its own
  // anchor row rather than starting at it - shifted up by half its own height, same
  // as #renderBlock's visualTop/#renderVariable's bodyY already draw it (a block's
  // sole pin, for the common 1-row case, ends up dead center of the box, not pinned
  // to its top edge) - the raw un-shifted grid footprint is what
  // LogramGraphController.IsBlocked uses server-side for wire-routing occupancy,
  // which is a different concern from where the shape actually sits on screen.
  #elementBounds(el) {
    const x0 = (el.x || 0) * CELL;
    const width = (el.width || 2) * CELL;
    const height = (el.height || (el.editor === 'LogramVariable' ? 1 : 2)) * CELL;
    const y0 = (el.y || 0) * CELL - height / 2;
    return { x0, y0, x1: x0 + width, y1: y0 + height };
  }

  // Distance from `p` to segment a-b, projected onto the segment itself and clamped
  // to its own ends (not extended past them with round/square caps) - "по длине
  // сегмента и клетка поперёк": a CELL-wide rectangle exactly as long as the segment.
  #pointNearSegment(p, a, b, halfWidth) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const lenSq = dx * dx + dy * dy;
    if(lenSq === 0) return Math.hypot(p.x - a.x, p.y - a.y) <= halfWidth;
    const t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / lenSq;
    if(t < 0 || t > 1) return false;
    return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy)) <= halfWidth;
  }

  // The one hit-test shared by pointerdown (select/drag, #onSurfacePointerDown) and
  // contextmenu (#onSurfaceContextMenu) - same priority for both, so a click and a
  // right-click always agree on what's under the cursor: pin (its own CELL-square,
  // "имеет приоритет над всеми") > wire (per segment, CELL wide) > block/variable
  // (occupied footprint) > empty canvas (null).
  #hitTest(clientX, clientY) {
    const p = this.#toSvgPoint(clientX, clientY);
    const elements = this.rows.filter((row) => row.editor === 'LogramBlock' || row.editor === 'LogramVariable');
    const { pinsByParent } = this.#sceneRows();
    const pins = this.#pinAnchors(elements, pinsByParent);
    for(const pin of pins) {
      if(Math.abs(p.x - pin.px) <= CELL / 2 && Math.abs(p.y - pin.py) <= CELL / 2) return { type: 'pin', pin };
    }
    for(const wire of this.#wireSegments(pins)) {
      for(let i = 0; i < wire.points.length - 1; i++) {
        if(this.#pointNearSegment(p, wire.points[i], wire.points[i + 1], CELL / 2)) return { type: 'wire', vid: wire.vid };
      }
    }
    for(const el of elements) {
      const b = this.#elementBounds(el);
      if(p.x >= b.x0 && p.x <= b.x1 && p.y >= b.y0 && p.y <= b.y1) return { type: 'element', el };
    }
    return null;
  }

  // Compatibility = opposite polarity, same rule ES used (LogramView.cs's mouse-up:
  // finish pin must have the opposite IsInput of the drag's start) - source is
  // whichever end is "out", the other end's own vid becomes the sink whose
  // cctor.LoBind gets set (see LogramViewProvider.ExecuteRpc's "bind" handler).
  #tryConnect(fromVid, fromSide, toVid, toSide) {
    if(!this.api || fromSide === toSide) return;
    const sourceVid = fromSide === 'out' ? fromVid : toVid;
    const sinkVid = fromSide === 'out' ? toVid : fromVid;
    this.api.rpc(sinkVid, 'bind', { source: sourceVid }).catch((error) => console.warn('logram req.rpc bind failed', sinkVid, sourceVid, error));
  }

  // Allows the drop below to fire at all (browsers reject a drop on any element that
  // never declared itself a valid target) - only for a drag that actually carries our
  // own vid payload (view-row.js #onDragStart), so dragging arbitrary OS files/text
  // over the canvas doesn't show a false "you can drop here" cue.
  #onSurfaceDragOver(e) {
    if(!e.dataTransfer.types.includes('application/x13-vid')) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'link';
  }

  // Mirrors ES's drag-a-topic-onto-the-canvas (ES/Logram/LogramView.cs
  // LogramView_Drop's DTopic branch, see LogramViewProvider.ExecuteAddVariable for
  // the server half that actually creates it) - dropping a Workspace/Inspector row
  // (any tree using view-row.js, see its #onDragStart) creates a variable bound to
  // the dragged topic, positioned at the drop's own grid cell (same cell math as
  // #onSurfaceContextMenu's add-block flow).
  async #onSurfaceDrop(e) {
    const sourceVid = e.dataTransfer.getData('application/x13-vid');
    if(!sourceVid) return;
    e.preventDefault();
    if(!this.api) return;
    const canvasVid = this.rows.find((row) => row.editor === 'LogramCanvas')?.vid;
    if(!canvasVid) return;
    const p = this.#toSvgPoint(e.clientX, e.clientY);
    const left = Math.max(0, Math.floor(p.x / CELL));
    const top = Math.max(0, Math.round(p.y / CELL));
    try {
      await this.api.rpc(canvasVid, 'add-variable', { top, left, source: sourceVid });
    }
    catch(error) {
      console.warn('logram req.rpc add-variable failed', sourceVid, error);
    }
  }

  // Single contextmenu entry point for the whole surface - #hitTest picks pin/wire/
  // element/canvas by the same priority pointerdown uses, so right-click always
  // agrees with what a click would have selected/dragged. A wire hit gets no menu
  // yet (LogramPaletteBuilder has no wire-menu builder server-side) - right-clicking
  // a wire currently just does nothing, same as before this change.
  //
  // Canvas (no hit): req.menu on the diagram's own root vid returns the LoBlock
  // registry (see LogramPaletteBuilder.BuildAddMenu), grouped into submenus by
  // registry folder. ES replaces this with a dedicated drag-and-drop palette panel
  // (see ES/Logram/LogramForm.xaml.cs) - the web client uses a plain right-click menu
  // instead. The click's grid cell is captured here (not re-derived on command
  // execution, since the menu may stay open while the pointer moves) and carried
  // through menuState to #onMenuCommand.
  async #onSurfaceContextMenu(e) {
    e.preventDefault();
    if(!this.api) return;
    const hit = this.#hitTest(e.clientX, e.clientY);
    if(hit?.type === 'element') { await this.#openContextMenu(e, hit.el.vid); return; }
    if(hit?.type === 'pin') { await this.#openContextMenu(e, hit.pin.vid); return; }
    if(hit?.type === 'wire') return;
    const canvasVid = this.rows.find((row) => row.editor === 'LogramCanvas')?.vid;
    if(!canvasVid) return;
    const p = this.#toSvgPoint(e.clientX, e.clientY);
    const left = Math.max(0, Math.floor(p.x / CELL));
    const top = Math.max(0, Math.round(p.y / CELL));
    try {
      const { items } = await this.api.menu(canvasVid);
      this.menuState = { items, x: e.clientX, y: e.clientY, vid: canvasVid, top, left };
    }
    catch(error) {
      console.warn('logram req.menu failed', canvasVid, error);
    }
  }

  // Right-click on an existing block/variable, or one of a block's own pins
  // (LogramViewProvider.BuildMenu tells root/pin/element vids apart by resolved
  // topic type/schema - see LogramPaletteBuilder.IsPin).
  async #openContextMenu(e, vid) {
    try {
      const { items } = await this.api.menu(vid);
      this.menuState = { items, x: e.clientX, y: e.clientY, vid };
    }
    catch(error) {
      console.warn('logram req.menu failed', vid, error);
    }
  }

  #onMenuCommand(e) {
    const { cmd } = e.detail || {};
    const menuState = this.menuState;
    this.menuState = null;
    if(!cmd || !menuState?.vid || !this.api) return;
    if(cmd.startsWith('add-block:')) {
      this.api.rpc(menuState.vid, cmd, { top: menuState.top, left: menuState.left })
        .catch((error) => console.warn('logram req.rpc add-block failed', cmd, error));
      return;
    }
    // Pin context menu (LogramPaletteBuilder.BuildPinMenu) - Open/Show in Workspace
    // are generic topic navigation, handled entirely client-side (no req.rpc);
    // Trace/Delete are real server commands (LogramViewProvider.ExecuteRpc / the
    // generic WorkspaceRpcDispatcher.Execute it falls back to).
    if(cmd === 'open') {
      this.#openInInspector(menuState.vid);
      return;
    }
    if(cmd === 'show-in-workspace') {
      this.#showInWorkspace(menuState.vid);
      return;
    }
    if(cmd === 'delete' || cmd === 'trace' || cmd.startsWith('add:')) {
      this.api.rpc(menuState.vid, cmd).catch((error) => console.warn('logram req.rpc failed', cmd, menuState.vid, error));
    }
  }

  // Mirrors #openInInspector's event shape (same vid-to-path slice, same bubbling
  // segment-command up through inspector-document.js to app-shell.js), just a
  // different cmd - app-shell.js#onSegmentCommand routes 'show-in-workspace' to the
  // Workspace tree instead of navigating the Inspector.
  #showInWorkspace(vid) {
    const path = vid.slice(vid.indexOf('#') + 1);
    this.dispatchEvent(new CustomEvent('segment-command', { detail: { cmd: 'show-in-workspace', path }, bubbles: true, composed: true }));
  }

  #closeMenu() {
    this.menuState = null;
  }

  #clearSelection() {
    this.selected = null;
  }

  #isElementSelected(vid) {
    return this.selected?.type === 'element' && this.selected.vids.has(vid);
  }

  // Ctrl+click on a block/variable (LogramView's usual multi-select gesture) adds/
  // removes just that one vid from the current element selection, leaving any pin/
  // wire selection alone (ctrl+click only ever targets #hitTest's 'element' branch,
  // see #onSurfacePointerDown) - selecting down to zero collapses back to `null`
  // rather than an empty-but-truthy selection object, so `this.selected` stays a
  // reliable falsy check everywhere else (#handleKeyDown, #isElementSelected).
  #toggleElementSelection(vid) {
    const vids = this.selected?.type === 'element' ? new Set(this.selected.vids) : new Set();
    if(vids.has(vid)) vids.delete(vid); else vids.add(vid);
    this.selected = vids.size > 0 ? { type: 'element', vids } : null;
  }

  // Single pointerdown entry point for the whole surface (attached once, on
  // .viewport itself - not on any individual pin/wire/element shape) - #hitTest picks
  // the target by the same priority the contextmenu handler uses, so a click always
  // agrees with what a right-click would have targeted. Ctrl only does something over
  // an element (toggle) or empty canvas (pan, regardless of what's under the cursor -
  // checked before hit-testing so it always wins there); ctrl+pin/wire falls through
  // to their normal handling, unchanged. A plain drag over empty canvas starts a
  // marquee select instead of panning.
  #onSurfacePointerDown(e) {
    if(e.button !== 0) return;
    const hit = this.#hitTest(e.clientX, e.clientY);
    if(e.ctrlKey) {
      if(hit?.type === 'element') { this.#toggleElementSelection(hit.el.vid); return; }
      if(!hit) { this.#startPan(e); return; }
    }
    if(hit?.type === 'pin') { this.#onPinPointerDown(e, hit.pin.vid, hit.pin.side, hit.pin.px, hit.pin.py, hit.pin.selectable); return; }
    if(hit?.type === 'wire') { this.#selectWire(e, hit.vid); return; }
    if(hit?.type === 'element') { this.#startElementDrag(e, hit.el); return; }
    this.#startMarqueeSelect(e);
  }

  // Dragging a rubber-band over empty canvas selects every block/variable whose own
  // occupied footprint (#elementBounds) intersects the box, replacing the current
  // selection - a plain click (box never grows past its own zero-size starting point)
  // degenerates into a point-in-rect test against every element, correctly finding
  // none and clearing the selection, so no separate click-vs-drag threshold is needed
  // here (unlike #onPinPointerDown's, which has to disambiguate a click from starting
  // a wire).
  #startMarqueeSelect(e) {
    const start = this.#toSvgPoint(e.clientX, e.clientY);
    this.marquee = { x0: start.x, y0: start.y, x1: start.x, y1: start.y };
    const move = (ev) => {
      const p = this.#toSvgPoint(ev.clientX, ev.clientY);
      this.marquee = { x0: Math.min(start.x, p.x), y0: Math.min(start.y, p.y), x1: Math.max(start.x, p.x), y1: Math.max(start.y, p.y) };
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      const box = this.marquee;
      this.marquee = null;
      const elements = this.rows.filter((row) => row.editor === 'LogramBlock' || row.editor === 'LogramVariable');
      const vids = new Set();
      for(const el of elements) {
        const b = this.#elementBounds(el);
        if(b.x0 <= box.x1 && b.x1 >= box.x0 && b.y0 <= box.y1 && b.y1 >= box.y0) vids.add(el.vid);
      }
      this.selected = vids.size > 0 ? { type: 'element', vids } : null;
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  // Position stays unsnapped (fractional cells) during the drag for smooth visual
  // feedback - only the final pointerup commit rounds to a whole cell, matching ES's
  // SetLocation(vector, save:false) / (..., save:true) split. Dragging any element
  // that's already part of a multi-selection moves the whole group by the same
  // rounded delta (looked up per-vid from `this.rows` at commit time, not from a
  // per-element drag-start snapshot, since the drag never mutates the store); grabbing
  // an element outside the current selection replaces it with just that one, same as
  // a normal single click would - but only committed to `this.selected` up front when
  // it WASN'T already selected, so a plain click-without-drag on an already-selected
  // member of a group collapses the group down to that one element (the same
  // "click an already-selected item in a multi-select" convention as most desktop
  // apps), while a click-without-drag on an untouched selection just re-confirms it.
  #startElementDrag(e, el) {
    e.preventDefault();
    const alreadySelected = this.#isElementSelected(el.vid);
    if(!alreadySelected) this.selected = { type: 'element', vids: new Set([el.vid]) };
    if(!this.api) return;
    const vids = [...this.selected.vids];
    const startClientX = e.clientX, startClientY = e.clientY;
    const scale = this.scale || 1;
    this.dragState = { vids: new Set(vids), dx: 0, dy: 0 };
    const move = (ev) => {
      const dx = (ev.clientX - startClientX) / (CELL * scale);
      const dy = (ev.clientY - startClientY) / (CELL * scale);
      this.dragState = { vids: new Set(vids), dx, dy };
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      const { dx, dy } = this.dragState || { dx: 0, dy: 0 };
      this.dragState = null;
      const roundDx = Math.round(dx), roundDy = Math.round(dy);
      if(roundDx === 0 && roundDy === 0) {
        if(alreadySelected) this.selected = { type: 'element', vids: new Set([el.vid]) };
        return;
      }
      for(const vid of vids) {
        const row = this.rows.find((r) => r.vid === vid);
        if(!row) continue;
        this.api.commit(vid, { top: (row.y || 0) + roundDy, left: (row.x || 0) + roundDx }).catch((error) => console.warn('logram req.commit failed', vid, error));
      }
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  #handleKeyDown(e) {
    if(e.key !== 'Delete' && e.key !== 'Backspace') return;
    if(['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName)) return;
    if(!this.selected || !this.api) return;
    const { type } = this.selected;
    if(type === 'element') {
      const vids = [...this.selected.vids];
      this.selected = null;
      for(const vid of vids) this.api.rpc(vid, 'delete').catch((error) => console.warn('logram req.rpc delete failed', vid, error));
    }
    else if(type === 'pin') {
      const vid = this.selected.vid;
      this.selected = null;
      this.api.rpc(vid, 'delete').catch((error) => console.warn('logram req.rpc delete failed', vid, error));
    }
    else if(type === 'wire') {
      const vid = this.selected.vid;
      this.selected = null;
      this.api.rpc(vid, 'unbind').catch((error) => console.warn('logram req.rpc unbind failed', vid, error));
    }
  }

  #startPan(e) {
    if(e.button !== 0) return;
    e.preventDefault();
    const startX = e.clientX, startY = e.clientY;
    const startTx = this.tx, startTy = this.ty;
    const viewport = e.currentTarget;
    viewport.classList.add('panning');
    const move = (ev) => {
      this.tx = startTx + (ev.clientX - startX);
      this.ty = startTy + (ev.clientY - startY);
    };
    const up = () => {
      viewport.classList.remove('panning');
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  #onWheel(e) {
    if(!e.ctrlKey) return;
    e.preventDefault();
    this.#zoomBy(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX, e.clientY);
  }

  // Keeps the point under (clientX, clientY) fixed on screen while the scale changes
  // - .surface's transform-origin is a plain 0,0 (see its own CSS rule), so without
  // this the content would visibly drift out from under the cursor on every wheel-
  // zoom tick instead of zooming "into" wherever the pointer is. Falls back to
  // anchoring on the viewport's own center when no cursor position is given (the +/-
  // buttons don't have one) - better than the old unanchored behavior, which drifted
  // toward the surface's origin corner on every click.
  #zoomBy(factor, clientX, clientY) {
    const newScale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, this.scale * factor));
    if(newScale === this.scale) return;
    let anchorX = clientX, anchorY = clientY;
    if(anchorX == null || anchorY == null) {
      const rect = this.renderRoot.querySelector('.viewport')?.getBoundingClientRect();
      anchorX = rect ? rect.left + rect.width / 2 : 0;
      anchorY = rect ? rect.top + rect.height / 2 : 0;
    }
    const p = this.#toSvgPoint(anchorX, anchorY);
    this.tx -= p.x * (newScale - this.scale);
    this.ty -= p.y * (newScale - this.scale);
    this.scale = newScale;
  }

  #resetView() {
    this.scale = 1;
    const elements = this.rows.filter((row) => row.editor === 'LogramBlock' || row.editor === 'LogramVariable');
    const { width, height } = this.#canvasSize(elements);
    this.#centerView(width, height);
  }
}

customElements.define('x13-logram-document', X13LogramDocument);
