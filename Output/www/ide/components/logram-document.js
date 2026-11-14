import { LitElement, html, css, svg } from '../../lib/lit-all.min.js';
import './context-menu.js';
import './breadcrumb-bar.js';
import '../editors/display-editors.js';
import { CELL, MIN_LEFT_CELL, blockGeometry, variableGeometry, buildScene, canvasSize, wirePoints, wirePathD } from './logram-scene.js';
import { hitTest, marqueeIntersections } from './logram-hit-test.js';
import { LogramViewport } from './logram-viewport.js';
import { formatConstructorValue, parseConstructorValue } from '../services/value-format.js';

// The other view of the same topic, for the bar's switch button. A Logram document is only ever
// opened on a Logram-typed topic, so the Inspector side always exists and the button is constant.
const INSPECTOR_VIEW = { label: 'Inspector', mode: 'inspector' };

// The LPLC editor's own commands (LplcPaletteBuilder) - forwarded to the server as they are.
const LPLC_RPC_COMMAND = /^(?:show-in:|hide-in:|show-out:|hide-out:)|^(?:unbind|hide)$/;

// Документ Logram: Lit-рендеринг и команды. Где что стоит на экране - logram-scene.js, что под
// курсором - logram-hit-test.js, масштаб и сдвиг холста - logram-viewport.js.
//
// view picks the server side: 'logram' (LogramViewProvider) or 'lplc' (LplcViewProvider, an ESP
// node's LogramPLC program). Both send the same rows, so the scene is shared; what differs is the
// commands - LPLC has no topic drop and no trace, but pin show/hide and named additions - and an
// LPLC pin, which is a key of its block rather than a topic, navigates to that block.
export class X13LogramDocument extends LitElement {
  static properties = {
    view: {},
    path: {},
    rootName: {},
    api: { attribute: false },
    store: { attribute: false },
    rows: { attribute: false },
    selected: { attribute: false },
    dragState: { attribute: false },
    wireDraft: { attribute: false },
    marquee: { attribute: false },
    menuState: { attribute: false },
    nameState: { attribute: false },
    constantState: { attribute: false },
    argumentState: { attribute: false },
  };

  static styles = css`
    :host { box-sizing: border-box; display: flex; flex-direction: column; height: 100%; width: 100%; }
    /* The canvas box. position:relative and overflow:hidden moved here from :host when the
       breadcrumb bar joined it above - .zoom-controls belong to the canvas, not to the document,
       and would otherwise position against the bar as well and sit over it. Nothing else moved
       with them: every coordinate this component computes comes from getBoundingClientRect or
       elementFromPoint (see LogramViewport.screenToScene), which are client coordinates and do
       not care which ancestor establishes the containing block. */
    .body { flex: 1 1 auto; min-height: 0; overflow: hidden; position: relative; }
    .plc-controls { margin-top: 12px; }
    .surface { user-select: none; }
    /* Base cursor is pointer (empty canvas, per hitTest's own rules) - pan-in-progress
       (ctrl+drag, see #onSurfacePointerDown) overrides to grabbing; the four hoverable
       hit-test targets (pin/wire/element) below all override back to default, since
       their actual behavior (select/menu/drag) isn't a simple "click here" affordance. */
    .viewport { background: #eef3f8; cursor: pointer; height: 100%; overflow: hidden; width: 100%; }
    .viewport.panning { cursor: grabbing; }
    .surface { transform-origin: 0 0; }
    /* Constant labels can extend left of the routing grid without changing its coordinates. */
    .surface.lplc { overflow: visible; }
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
    .block-body.configuration-error { stroke: #dc2626; stroke-width: 2; }
    .block-body.selected, .variable-body.selected { fill: #ff6347; }
    .variable-body { fill: #4682b4; rx: 4; }
    .variable-label { fill: #fff; font-family: Consolas, monospace; font-size: 11px; pointer-events: none; }
    .pin-label { fill: #243447; font-family: Consolas, monospace; font-size: 10px; pointer-events: none; }
    .pin-trace { fill: #b45309; font-family: Consolas, monospace; font-size: 10px; font-weight: 700; pointer-events: none; }
    .pin-constant { fill: #6b7280; font-family: Consolas, monospace; font-size: 10px; font-weight: 700; cursor: text; pointer-events: all; }
    .block-icon { pointer-events: none; }
    .constructor-title, .constructor-argument { fill: #fff; font-family: Consolas, monospace; font-size: 10px; pointer-events: none; }
    .constructor-argument rect { pointer-events: all; }
    .constructor-argument.pin-error text { fill: #fee2e2; font-weight: 700; }
    .constructor-title { font-weight: 700; }
    .constructor-divider { stroke: #cbd5e1; stroke-dasharray: 3 2; pointer-events: none; }
    .pin-dot { pointer-events: none; }
    .pin-dot.selected { stroke: #ff6347; stroke-width: 2; }
    .pin-error .pin-dot { fill: #dc2626; }
    .pin-error .pin-label { fill: #b91c1c; font-weight: 700; }
    .wire { fill: none; pointer-events: none; stroke-width: 2; }
    .wire.selected { stroke: #ff6347 !important; stroke-width: 3; }
    /* wire-hit's own stroke-width (CELL, "a cell across") only approximates
       pointNearSegment's real per-segment rectangle test at corners (SVG stroke
       joins/caps aren't a true capsule) - close enough for :hover cursor feedback;
       hitTest (used for the actual click/menu/drag logic) does the exact math. */
    .pin-hit, .wire-hit, .block-body, .variable-body { cursor: default; }
    .pin-hit { fill: transparent; }
    .wire-hit { fill: none; stroke: transparent; stroke-width: 18; }
    .wire-draft { fill: none; pointer-events: none; stroke: #2563eb; stroke-dasharray: 4 3; stroke-width: 2; }
    .marquee { fill: rgba(37, 99, 235, 0.1); pointer-events: none; stroke: #2563eb; stroke-dasharray: 4 3; }
    .grid-bg { fill: #fff; stroke: #cfd8e3; }
    .grid-dots { pointer-events: none; }
    .name-input { border: 1px solid #2563eb; font-family: Consolas, monospace; font-size: 12px; padding: 2px 4px; position: fixed; z-index: 10; }
    .constant-input { box-sizing: border-box; border: 1px solid #9ca3af; color: #6b7280; font-family: Consolas, monospace; padding: 2px 4px; position: fixed; z-index: 10; text-align: right; transform: translateY(-50%); }
    .constant-input:invalid { border-color: #dc2626; }
  `;

  constructor() {
    super();
    this.view = 'logram';
    this.path = '/';
    this.rootName = '';
    this.rows = [];
    this.#viewport = new LogramViewport(this);
    this.#scene = buildScene(this.rows);
    this.selected = null;
    this.dragState = null;
    this.wireDraft = null;
    this.marquee = null;
    this.menuState = null;
    this.nameState = null;
    this.constantState = null;
    this.argumentState = null;
    this.unsubscribe = null;
    this.builtRoot = null;
    this.onKeyDown = (e) => this.#handleKeyDown(e);
  }

  connectedCallback() {
    super.connectedCallback();
    window.addEventListener('keydown', this.onKeyDown);
  }

  // Сцена строится один раз на изменение строк. Hit-test между приходом строк и перерисовкой
  // отвечает по прежней сцене - той, что пока и нарисована.
  willUpdate(changed) {
    if(changed.has('rows')) {
      this.#scene = buildScene(this.rows);
      if(this.constantState && !this.rows.some((row) => row.vid === this.constantState.vid && row.constantEditable)) this.constantState = null;
      if(this.argumentState && !this.rows.some(row => row.vid === this.argumentState.blockVid && row.constructorRows?.some(a => a.name === this.argumentState.name && a.editable))) this.argumentState = null;
    }
    if(changed.has('view') && this.view !== 'lplc') this.constantState = null;
    if(changed.has('view') && this.view !== 'lplc') this.argumentState = null;
  }

  updated(changed) {
    if(changed.has('store')) this.#subscribeStore();
    if(changed.has('rows') || changed.has('view') || changed.has('api') || changed.has('store')) this.#buildOpenedProgram();
    if(changed.has('nameState') && this.nameState) this.renderRoot.querySelector('.name-input')?.focus();
    if(changed.has('argumentState') && this.argumentState && !changed.get('argumentState')) this.renderRoot.querySelector('.argument-input')?.focus();
    if(changed.has('constantState') && this.constantState && changed.get('constantState')?.vid !== this.constantState.vid) {
      const input = this.renderRoot.querySelector('.constant-input');
      input?.focus();
      input?.select();
    }
  }

  disconnectedCallback() {
    window.removeEventListener('keydown', this.onKeyDown);
    this.constantState = null;
    this.argumentState = null;
    this.builtRoot = null;
    this.unsubscribe?.();
    this.unsubscribe = null;
    super.disconnectedCallback();
  }

  render() {
    const { pinsByParent, rowsByVid } = this.#scene;
    const plc = this.view === 'lplc' ? this.rows.find((row) => row.editor === 'LogramCanvas') : null;
    let elements = this.#scene.elements;
    if(this.dragState) {
      // Live preview during a drag: override every dragged row's position by the same
      // (unsnapped) in-progress delta, without touching the store - the actual commit
      // only happens once per row, on pointerup. A shared dx/dy (not per-row absolute
      // coords) keeps a multi-selection's relative layout intact regardless of which
      // element in the set was actually grabbed. canvasSize sees this too, so the
      // surface grows live as a block is dragged past its current edge, instead of
      // only on the next commit (as the server-persisted-size approach this replaced
      // could only ever manage).
      const { vids, dx, dy } = this.dragState;
      elements = elements.map((el) => vids.has(el.vid) ? { ...el, x: (el.x || 0) + dx, y: (el.y || 0) + dy } : el);
    }
    const { x: boxX, y: boxY, width, height } = canvasSize(elements);

    return html`
      <x13-breadcrumb-bar .path=${this.path} .rootName=${this.rootName} .currentMode=${this.view}
        .altView=${INSPECTOR_VIEW}></x13-breadcrumb-bar>
      <div class="body">
        <div class="viewport" @pointerdown=${this.#onSurfacePointerDown} @wheel=${(e) => this.#viewport.onWheel(e)} @contextmenu=${this.#onSurfaceContextMenu}
          @dragover=${this.#onSurfaceDragOver} @drop=${this.#onSurfaceDrop}>
          <!-- The viewBox origin is negative by CANVAS_PAD, and that is the whole of how the
               white field gets its half-cell on the top and left: content keeps drawing at its
               own cell coordinates and nothing inside has to know. Width/height match the
               viewBox, so the SVG's own scale stays 1 and .surface's CSS transform is still the
               only zoom in play. -->
          <svg class="surface ${this.view === 'lplc' ? 'lplc' : ''}" width=${width} height=${height} viewBox="${boxX} ${boxY} ${width} ${height}"
            style="transform:${this.#viewport.transform}">
            <defs>
              <!-- x/y=-CELL/2 shifts where each tile repeat lands, without moving the mark
                   within its own tile (still centered at local CELL/2, so nothing here
                   needs overflow:visible) - net effect is the mark itself renders half a
                   cell up-left of where it used to, landing on the grid's actual corners
                   (multiples of CELL, where element x/y=0 sits) instead of a cell's
                   center. -->
              <pattern id="grid-cross" x=${-CELL / 2} y=${-CELL / 2} width=${CELL} height=${CELL} patternUnits="userSpaceOnUse">
                <path d="M ${CELL / 2 - 1.5} ${CELL / 2} h 3 M ${CELL / 2} ${CELL / 2 - 1.5} v 3" stroke="#dde2e8" stroke-opacity="0.6" stroke-width="1"></path>
              </pattern>
            </defs>
            <rect class="grid-bg" x=${boxX} y=${boxY} width=${width} height=${height}></rect>
            <rect class="grid-dots" x=${boxX} y=${boxY} width=${width} height=${height} fill="url(#grid-cross)"></rect>
            ${elements.map((el) => this.#renderElement(el, pinsByParent.get(el.vid) || [], rowsByVid))}
            ${this.wireDraft ? svg`<line class="wire-draft" x1=${this.wireDraft.originX} y1=${this.wireDraft.originY} x2=${this.wireDraft.x} y2=${this.wireDraft.y}></line>` : svg``}
            ${this.marquee ? svg`<rect class="marquee" x=${this.marquee.x0} y=${this.marquee.y0} width=${this.marquee.x1 - this.marquee.x0} height=${this.marquee.y1 - this.marquee.y0}></rect>` : svg``}
          </svg>
        </div>
        <div class="zoom-controls">
          <button @click=${() => this.#viewport.zoomBy(1.2)} title="Zoom in">+</button>
          <button @click=${() => this.#viewport.zoomBy(1 / 1.2)} title="Zoom out">-</button>
          <button @click=${() => this.#viewport.reset()} title="Reset view">⟲</button>
          ${plc ? this.#renderPlcActions(plc) : html``}
        </div>
      </div>
      ${this.menuState ? html`
        <x13-context-menu .items=${this.menuState.items} .x=${this.menuState.x} .y=${this.menuState.y}
          @menu-command=${this.#onMenuCommand} @menu-close=${this.#closeMenu}></x13-context-menu>` : html``}
      ${this.nameState ? html`
        <input class="name-input" style="left:${this.nameState.x}px;top:${this.nameState.y}px" placeholder=${this.nameState.placeholder}
          @keydown=${this.#onNameKeyDown} @blur=${this.#cancelName}>` : html``}
      ${this.argumentState ? html`
        <input class="argument-input name-input" aria-label="Constructor argument" style="left:${this.argumentState.x}px;top:${this.argumentState.y}px"
          .value=${this.argumentState.text} title=${this.argumentState.preview || ''}
          @pointerdown=${e => e.stopPropagation()} @input=${this.#onArgumentInput}
          @keydown=${this.#onArgumentKeyDown} @blur=${() => { this.argumentState = null; }}>` : html``}
      ${this.constantState ? html`
        <input class="constant-input" inputmode="decimal" aria-label="Input constant" title="Enter to save; Escape to cancel"
          style="left:${this.constantState.x}px;top:${this.constantState.y}px;width:${this.constantState.width}px;font-size:${this.constantState.fontSize}px"
          .value=${this.constantState.value} .readOnly=${this.constantState.saving}
          @input=${(e) => e.currentTarget.setCustomValidity('')}
          @keydown=${this.#onConstantKeyDown} @blur=${this.#onConstantBlur}>` : html``}`;
  }

  #renderPlcActions(plc) {
    return html`
      <x13-device-plc-editor class="plc-controls" view="value" .compact=${true} .modified=${plc.plcModified}
        .state=${plc.value} .rpc=${(cmd) => this.#runPlcAction(plc.vid, cmd)}></x13-device-plc-editor>`;
  }

  #buildOpenedProgram() {
    const root = this.view === 'lplc' ? this.rows.find((row) => row.editor === 'LogramCanvas')?.vid : null;
    if(!root) {
      this.builtRoot = null;
      return;
    }
    if(!this.api || this.builtRoot === root) return;
    this.builtRoot = root;
    this.#runPlcAction(root, 'action:MQTT_SN.PLC.Build');
  }

  #runPlcAction(vid, cmd) {
    if(!this.api) return;
    return this.api.rpc(vid, cmd).catch((error) => console.warn('lplc action failed', cmd, vid, error));
  }

  #renderElement(el, pins, rowsByVid) {
    return el.editor === 'LogramBlock' ? this.#renderBlock(el, pins, rowsByVid) : this.#renderVariable(el, rowsByVid);
  }

  // x/y/width/height come straight from the server in grid cells (LogramGraphController
  // serializes the exact same numbers it routed wires against) - deriving our own
  // estimate here would drift from that and wires would visibly miss the pins.
  //
  // The pixels come from blockGeometry: anchorY is the first pin row (GetPinCell's
  // Y+index - there is no header row reserved above it) and outputs sit at x1
  // (X+WidthCells) - body/pins/outputs all move together with that anchor, or the
  // routed wires (computed against GetPinCell) drift off the drawn pins again. x1
  // already reaches the output pins (the one-cell gap past the body is baked into
  // WidthCells server-side, see LogramGraphController.BuildLayout), so it doubles as
  // the body's own right edge.
  //
  // The body is its own hit target - no separate transparent rect under it: the two
  // used to be computed apart and disagreed (see blockGeometry).
  #renderBlock(el, pins, rowsByVid) {
    const g = blockGeometry(el);
    const width = g.x1 - g.x0;
    const wires = [];
    const pinNodes = pins.map((pin) => {
      const isInput = pin.pinDirection === 'in';
      const px = isInput ? g.x0 : g.x1;
      const py = g.anchorY + (pin.pinIndex || 0) * CELL;
      if(pin.sourceVid) wires.push(this.#renderWire(pin, px, py, rowsByVid));
      const pinSelected = this.selected?.type === 'pin' && this.selected.vid === pin.vid;
      return svg`
        <g class=${pin.pinError ? 'pin-error' : ''} @dblclick=${(e) => { e.stopPropagation(); this.#openInInspector(pin.vid); }}>
          ${pin.pinError ? svg`<title>${isInput ? 'Input' : 'Output'} "${pin.name}" is not in the block's pin catalog.</title>` : svg``}
          <rect class="pin-hit" data-vid=${pin.vid} data-side=${pin.pinDirection} x=${px - CELL / 2} y=${py - CELL / 2} width=${CELL} height=${CELL}></rect>
          <circle class="pin-dot ${pinSelected ? 'selected' : ''}" cx=${px} cy=${py} r="3" fill=${pin.sourceVid && !pin.sourceLocal ? '#9aa5b1' : (pin.color || '#9aa5b1')}></circle>
          <text class="pin-label" x=${isInput ? px + 6 : px - 6} y=${py + 3} text-anchor=${isInput ? 'start' : 'end'}>${pin.name}</text>
          ${this.view === 'lplc' && isInput && pin.constantEditable ? svg`
            <text class="pin-constant" x=${px - 6} y=${py} text-anchor="end" dominant-baseline="middle"
              @pointerdown=${(e) => e.stopPropagation()} @dblclick=${(e) => this.#editConstant(e, pin)}>${pin.displayValue || '…'}<title>Double-click to edit the input constant</title></text>`
            : pin.trace ? svg`<text class="pin-trace" x=${isInput ? px - 6 : px + 6} y=${py - 8} text-anchor=${isInput ? 'end' : 'start'}>${pin.displayValue || ''}</text>` : svg``}
        </g>`;
    });
    const isSelected = this.#isElementSelected(el.vid);
    return svg`
      <g @dblclick=${() => this.#openInInspector(el.vid)}>
        ${wires}
        <rect class="block-body ${isSelected ? 'selected' : ''} ${el.configurationError ? 'configuration-error' : ''}" x=${g.x0} y=${g.y0} width=${width} height=${g.y1 - g.y0} rx="3">
          <title>${el.configurationError ? el.name + ': ' + el.configurationError : el.name}</title>
        </rect>
        ${this.view === 'lplc' && el.constructorRows?.length ? this.#renderArguments(el, g, pins) : el.constructorArgs?.length ? svg`
          <text class="constructor-title" x=${g.x0 + 6} y=${g.anchorY + 3}>${el.name} · constructor</text>
          ${el.constructorArgs.map((label, index) => svg`
            <text class="constructor-argument" x=${g.x0 + 6} y=${g.anchorY + (index + 1) * CELL + 3}>${label}</text>`)}
          ${pins.length ? svg`<line class="constructor-divider" x1=${g.x0 + 3} x2=${g.x1 - 3}
            y1=${g.anchorY + (el.constructorArgs.length + 0.5) * CELL} y2=${g.anchorY + (el.constructorArgs.length + 0.5) * CELL}></line>` : svg``}
        ` : el.icon ? svg`<image class="block-icon" href=${el.icon} x=${g.x0 + width / 2 - 7} y=${g.y0} width="16" height="16"></image>` : svg``}
        ${pinNodes}
      </g>`;
  }

  #renderArguments(el, g, pins) {
    const last = Math.max(...el.constructorRows.map(a => a.position));
    return svg`
      ${el.icon ? svg`<image class="block-icon" href=${el.icon} x=${(g.x0 + g.x1) / 2 - 8} y=${g.anchorY - 8} width="16" height="16"></image>` : svg``}
      ${el.constructorRows.map(argument => svg`
        <g class=${argument.error ? 'constructor-argument pin-error' : 'constructor-argument'}
          @dblclick=${e => { e.preventDefault(); e.stopPropagation(); }}
          @contextmenu=${e => { e.preventDefault(); e.stopPropagation(); return this.#openContextMenu(e, el.vid + '/args.' + argument.name); }}>
          <title>${argument.error || argument.label}</title>
          <rect x=${g.x0 + 3} y=${g.anchorY + argument.position * CELL - CELL / 2} width=${g.x1 - g.x0 - 6} height=${CELL} fill="transparent"></rect>
          <text x=${g.x0 + 6} y=${g.anchorY + argument.position * CELL + 3}>${argument.label}</text>
          <text class="constructor-value" text-anchor="end" x=${g.x1 - 6} y=${g.anchorY + argument.position * CELL + 3}>${argument.text}</text>
        </g>`)}
      ${pins.length ? svg`<line class="constructor-divider" x1=${g.x0 + 3} x2=${g.x1 - 3}
        y1=${g.anchorY + (last + 0.5) * CELL} y2=${g.anchorY + (last + 0.5) * CELL}></line>` : svg``}`;
  }


  // A variable's pin anchors sit at exactly (x, y*CELL) / (x+width, y*CELL) - no
  // extra half-cell offset - to match GetVariableCell's routing anchors on the
  // server and BuildLayout's block pins (py = (y+index)*CELL, also unoffset); the
  // body rect is instead the one shifted, so it still looks vertically centered on
  // the dots. Getting this wrong is what made "enable" render below A01's row.
  //
  // The Trace label uses #renderBlock's output-pin formula verbatim (+6 outboard,
  // -8 above the anchor row, anchored start) and hangs off the OUT anchor: a
  // variable has two anchors but one topic, one value and one `trace` flag, so the
  // label has to pick a side, and the side a value leaves by is the one that reads
  // as "this is what this variable now holds". Server side it is the same field on
  // the same kind of row - see LogramGraphController.SerializeElementRow.
  #renderVariable(el, rowsByVid) {
    const g = variableGeometry(el);
    const wires = el.sourceVid ? [this.#renderWire(el, g.x0, g.anchorY, rowsByVid)] : [];
    const isSelected = this.#isElementSelected(el.vid);
    return svg`
      <g @dblclick=${() => this.#openInInspector(el.vid)}>
        ${wires}
        <rect class="variable-body ${isSelected ? 'selected' : ''}" x=${g.x0} y=${g.y0} width=${g.x1 - g.x0} height=${g.y1 - g.y0} rx="4"></rect>
        <text class="variable-label" x=${g.x0 + 5} y=${g.y1 - 3}>${el.name}</text>
        <rect class="pin-hit" data-vid=${el.vid} data-side="in" x=${g.x0 - CELL / 2} y=${g.anchorY - CELL / 2} width=${CELL} height=${CELL}></rect>
        <circle class="pin-dot" cx=${g.x0} cy=${g.anchorY} r="3" fill=${el.sourceVid && !el.sourceLocal ? '#9aa5b1' : (el.color || '#9aa5b1')}></circle>
        <rect class="pin-hit" data-vid=${el.vid} data-side="out" x=${g.x1 - CELL / 2} y=${g.anchorY - CELL / 2} width=${CELL} height=${CELL}></rect>
        <circle class="pin-dot" cx=${g.x1} cy=${g.anchorY} r="3" fill=${el.color || '#9aa5b1'}></circle>
        ${el.trace ? svg`<text class="pin-trace" x=${g.x1 + 6} y=${g.anchorY - 8} text-anchor="start">${el.displayValue || ''}</text>` : svg``}
      </g>`;
  }

  // Points come from wirePoints (logram-scene.js) - the same ones hit-testing walks.
  // Colored by the SOURCE's color (matching WPF, where a wire's Pen came from the
  // upstream/output pin's brush), not the sink's own - both source and sink rows
  // already carry a server-computed `color` (see LogramGraphController.ColorForValue),
  // same field the source's own dot uses, so this is never out of sync with it.
  #renderWire(row, sinkX, sinkY, rowsByVid) {
    if(!row.sourceLocal) return svg``;
    const points = wirePoints(row, sinkX, sinkY);
    // wire-hit stays straight-segmented - hitTest/pointNearSegment test against the
    // exact same points, so rounding it too would only make the hit math approximate
    // for no benefit (see the .wire-hit CSS comment above). Only the visible line (.wire)
    // gets the rounded treatment.
    const d = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ');
    const sourceRow = rowsByVid.get(row.sourceVid);
    const isSelected = this.selected?.type === 'wire' && this.selected.vid === row.vid;
    return svg`
      <path class="wire-hit" data-vid=${row.vid} d=${d}></path>
      <path class="wire ${isSelected ? 'selected' : ''}" d=${wirePathD(points)} stroke=${(sourceRow ? sourceRow.color : row.color) || '#9aa5b1'}></path>`;
  }

  #subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.builtRoot = null;
    this.constantState = null;
    this.rows = [];
    this.#viewport.reset();
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((rows) => {
      this.rows = rows;
    });
  }

  #openInInspector(vid) {
    const path = this.#topicPath(vid);
    this.dispatchEvent(new CustomEvent('segment-command', { detail: { cmd: 'open', path }, bubbles: true, composed: true }));
  }

  // The topic a row stands for. An LPLC pin (lplc#<block>/in.A, lplc#<block>/out.Q - see
  // LplcModel.PinVid) is a key of its block's LPLC.in/LPLC.out, so it stands for the block.
  #topicPath(vid) {
    const path = vid.slice(vid.indexOf('#') + 1);
    if(this.view !== 'lplc') return path;
    const slash = path.lastIndexOf('/');
    const last = path.slice(slash + 1);
    return slash > 0 && (last.startsWith('in.') || last.startsWith('out.')) ? path.slice(0, slash) : path;
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
        const p = this.#viewport.screenToScene(ev.clientX, ev.clientY);
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

  #pinAtPoint(clientX, clientY) {
    const el = this.renderRoot.elementFromPoint(clientX, clientY);
    if(!el || !el.classList?.contains('pin-hit')) return null;
    return { vid: el.dataset.vid, side: el.dataset.side };
  }

  // What's under the pointer, by the one hit-test (logram-hit-test.js) that pointerdown
  // (#onSurfacePointerDown) and contextmenu (#onSurfaceContextMenu) share.
  #hitAt(clientX, clientY) {
    return hitTest(this.#scene, this.#viewport.screenToScene(clientX, clientY));
  }

  // Compatibility = opposite polarity, same rule ES used (LogramView.cs's mouse-up:
  // finish pin must have the opposite IsInput of the drag's start) - source is
  // whichever end is "out", the other end's own vid becomes the sink whose
  // Logram.bind gets set (see LogramViewProvider.ExecuteRpc's "bind" handler).
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
    // LPLC variables are the device's own M registers, never bound to a dragged topic.
    if(this.view !== 'logram') return;
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
    if(this.view !== 'logram') return;
    const sourceVid = e.dataTransfer.getData('application/x13-vid');
    if(!sourceVid) return;
    e.preventDefault();
    if(!this.api) return;
    const canvasVid = this.rows.find((row) => row.editor === 'LogramCanvas')?.vid;
    if(!canvasVid) return;
    const p = this.#viewport.screenToScene(e.clientX, e.clientY);
    // Floored at 0, not MIN_LEFT_CELL: this drop creates a variable, and those are exempt
    // (see the constant) - dropping a topic against the left edge is the normal way to put
    // a diagram's input where it reads as one.
    const left = Math.max(0, Math.floor(p.x / CELL));
    const top = Math.max(0, Math.round(p.y / CELL));
    try {
      await this.api.rpc(canvasVid, 'add-variable', { top, left, source: sourceVid });
    }
    catch(error) {
      console.warn('logram req.rpc add-variable failed', sourceVid, error);
    }
  }

  // Single contextmenu entry point for the whole surface - hitTest picks pin/wire/
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
    const hit = this.#hitAt(e.clientX, e.clientY);
    if(hit?.type === 'element') { await this.#openContextMenu(e, hit.el.vid); return; }
    if(hit?.type === 'pin') { await this.#openContextMenu(e, hit.pin.vid); return; }
    if(hit?.type === 'wire') return;
    const canvasVid = this.rows.find((row) => row.editor === 'LogramCanvas')?.vid;
    if(!canvasVid) return;
    const p = this.#viewport.screenToScene(e.clientX, e.clientY);
    const left = Math.max(MIN_LEFT_CELL, Math.floor(p.x / CELL));
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
    const { cmd, willful, item } = e.detail || {};
    const menuState = this.menuState;
    this.menuState = null;
    if(!cmd || !menuState?.vid || !this.api) return;
    if(this.view === 'lplc' && cmd === 'set-arg') {
      this.api.rpc(menuState.vid, cmd, item?.args).catch(error => console.warn('constructor edit failed', error));
      return;
    }
    if(this.view === 'lplc' && cmd === 'edit-arg') {
      const blockVid = menuState.vid.replace(/\/args\.[^/]+$/, '');
      const name = item?.args?.name;
      const argument = this.rows.find(r => r.vid === blockVid)?.constructorRows?.find(a => a.name === name);
      if(!argument?.editable) return;
      this.argumentState = { vid: menuState.vid, blockVid, name, x: menuState.x, y: menuState.y,
        format: argument?.format, text: item?.args?.value == null ? '' : String(item.args.value) };
      return;
    }
    // A willful LPLC entry (a variable, a Func output - see LplcPaletteBuilder) asks for its name
    // first; Logram's menus keep sending theirs as they always have.
    const ask = willful && this.view === 'lplc';
    if(cmd.startsWith('add-block:') || (this.view === 'lplc' && (cmd.startsWith('add-var:') || cmd.startsWith('add-pin:') || cmd.startsWith('add-twi:') || cmd.startsWith('add-serial:')))) {
      const args = { top: menuState.top, left: menuState.left };
      if(ask) {
        this.#askName(menuState, cmd, args, item);
        return;
      }
      this.api.rpc(menuState.vid, cmd, args)
        .catch((error) => console.warn('logram req.rpc add-block failed', cmd, error));
      return;
    }
    if(ask) {
      this.#askName(menuState, cmd, {}, item);
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
    if(cmd === 'delete' || cmd === 'trace' || cmd.startsWith('add:') || (this.view === 'lplc' && LPLC_RPC_COMMAND.test(cmd))) {
      this.api.rpc(menuState.vid, cmd).catch((error) => console.warn('logram req.rpc failed', cmd, menuState.vid, error));
    }
  }

  // An inline name field where the menu was. Enter sends the command with the name - empty leaves
  // naming to the server (LplcViewProvider auto-names an addition); Escape or leaving it cancels.
  #askName(menuState, cmd, args, item) {
    this.constantState = null;
    this.nameState = { vid: menuState.vid, cmd, args, x: menuState.x, y: menuState.y, placeholder: item?.text || 'Name' };
  }

  #onNameKeyDown(e) {
    // The window's Delete/Backspace handler must not see typing: activeElement is retargeted to
    // this host, so its INPUT check cannot tell.
    e.stopPropagation();
    if(e.key === 'Escape') {
      this.#cancelName();
      return;
    }
    if(e.key !== 'Enter') return;
    const state = this.nameState;
    this.nameState = null;
    if(!state || !this.api) return;
    const name = e.target.value.trim();
    const args = name ? { ...state.args, name } : state.args;
    this.api.rpc(state.vid, state.cmd, args).catch((error) => console.warn('logram req.rpc failed', state.cmd, state.vid, error));
  }

  #onArgumentInput(e) {
    e.currentTarget.setCustomValidity('');
    try {
      const value = parseConstructorValue(e.currentTarget.value);
      this.argumentState = { ...this.argumentState, text: e.currentTarget.value, preview: formatConstructorValue(value, this.argumentState.format).text };
    } catch { this.argumentState = { ...this.argumentState, text: e.currentTarget.value, preview: '' }; }
  }

  async #onArgumentKeyDown(e) {
    e.stopPropagation();
    if(e.key === 'Escape') { this.argumentState = null; return; }
    if(e.key !== 'Enter' || !this.argumentState) return;
    e.preventDefault();
    const state = this.argumentState;
    const input = e.currentTarget;
    try {
      const value = parseConstructorValue(input.value);
      await this.api.rpc(state.vid, 'set-arg', { name: state.name, value });
      if(this.argumentState === state) this.argumentState = null;
    } catch(error) {
      input.setCustomValidity(String(error?.message || error));
      input.reportValidity();
    }
  }

  #cancelName() {
    this.nameState = null;
  }

  #editConstant(e, pin) {
    e.preventDefault();
    e.stopPropagation();
    if(this.view !== 'lplc' || pin.pinDirection !== 'in' || !pin.constantEditable || !this.api) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const fontSize = 10 * this.#viewport.scale;
    const width = Math.max(90, rect.width + fontSize * 2);
    this.menuState = null;
    this.nameState = null;
    this.constantState = { vid: pin.vid, value: pin.displayValue || '', x: Math.max(4, rect.right - width), y: rect.top + rect.height / 2, width, fontSize, saving: false };
  }

  #onConstantKeyDown(e) {
    e.stopPropagation();
    if(e.key === 'Escape') {
      e.preventDefault();
      this.constantState = null;
    }
    else if(e.key === 'Enter') {
      e.preventDefault();
      return this.#commitConstant(e.currentTarget, false);
    }
  }

  #onConstantBlur(e) {
    return this.#commitConstant(e.currentTarget, true);
  }

  async #commitConstant(input, cancelInvalid) {
    const state = this.constantState;
    if(!state || state.saving || !this.api) return;
    if(!this.rows.some((row) => row.vid === state.vid && row.constantEditable)) {
      this.constantState = null;
      return;
    }
    const text = input.value.trim();
    if(text === state.value.trim()) {
      this.constantState = null;
      return;
    }
    const value = Number(text);
    if(!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(text) || !Number.isFinite(value)) {
      if(cancelInvalid) this.constantState = null;
      else {
        input.setCustomValidity('Enter a finite integer or floating-point number.');
        input.reportValidity();
      }
      return;
    }
    const pending = { ...state, saving: true };
    this.constantState = pending;
    try {
      await this.api.commit(state.vid, value);
      if(this.constantState === pending) this.constantState = null;
    }
    catch(error) {
      if(this.constantState !== pending) return;
      this.constantState = { ...pending, saving: false };
      await this.updateComplete;
      input.setCustomValidity(error?.message || 'Unable to save the input constant.');
      input.reportValidity();
    }
  }

  // Mirrors #openInInspector's event shape (same vid-to-path slice, same bubbling
  // segment-command up through inspector-document.js to app-shell.js), just a
  // different cmd - app-shell.js#onSegmentCommand routes 'show-in-workspace' to the
  // Workspace tree instead of navigating the Inspector.
  #showInWorkspace(vid) {
    const path = this.#topicPath(vid);
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
  // wire selection alone (ctrl+click only ever targets hitTest's 'element' branch,
  // see #onSurfacePointerDown) - selecting down to zero collapses back to `null`
  // rather than an empty-but-truthy selection object, so `this.selected` stays a
  // reliable falsy check everywhere else (#handleKeyDown, #isElementSelected).
  #toggleElementSelection(vid) {
    const vids = this.selected?.type === 'element' ? new Set(this.selected.vids) : new Set();
    if(vids.has(vid)) vids.delete(vid); else vids.add(vid);
    this.selected = vids.size > 0 ? { type: 'element', vids } : null;
  }

  // Single pointerdown entry point for the whole surface (attached once, on
  // .viewport itself - not on any individual pin/wire/element shape) - hitTest picks
  // the target by the same priority the contextmenu handler uses, so a click always
  // agrees with what a right-click would have targeted. Ctrl only does something over
  // an element (toggle) or empty canvas (pan, regardless of what's under the cursor -
  // checked before hit-testing so it always wins there); ctrl+pin/wire falls through
  // to their normal handling, unchanged. A plain drag over empty canvas starts a
  // marquee select instead of panning.
  #onSurfacePointerDown(e) {
    if(e.button !== 0) return;
    const hit = this.#hitAt(e.clientX, e.clientY);
    if(e.ctrlKey) {
      if(hit?.type === 'element') { this.#toggleElementSelection(hit.el.vid); return; }
      if(!hit) { this.#viewport.startPan(e); return; }
    }
    if(hit?.type === 'pin') { this.#onPinPointerDown(e, hit.pin.vid, hit.pin.side, hit.pin.px, hit.pin.py, hit.pin.selectable); return; }
    if(hit?.type === 'wire') { this.#selectWire(e, hit.vid); return; }
    if(hit?.type === 'element') { this.#startElementDrag(e, hit.el); return; }
    this.#startMarqueeSelect(e);
  }

  // Dragging a rubber-band over empty canvas selects every block/variable the box
  // touches (marqueeIntersections), replacing the current selection - a plain click
  // finds none and clears the selection, so no separate click-vs-drag threshold is
  // needed here (unlike #onPinPointerDown's, which has to disambiguate a click from
  // starting a wire).
  #startMarqueeSelect(e) {
    const start = this.#viewport.screenToScene(e.clientX, e.clientY);
    this.marquee = { x0: start.x, y0: start.y, x1: start.x, y1: start.y };
    const move = (ev) => {
      const p = this.#viewport.screenToScene(ev.clientX, ev.clientY);
      this.marquee = { x0: Math.min(start.x, p.x), y0: Math.min(start.y, p.y), x1: Math.max(start.x, p.x), y1: Math.max(start.y, p.y) };
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      const box = this.marquee;
      this.marquee = null;
      const vids = marqueeIntersections(this.#scene, box);
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
    const scale = this.#viewport.scale || 1;
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
        // Clamped like both drop handlers above: a drag past the top edge would otherwise
        // store a negative coordinate, which puts the element outside the canvas the server
        // routes inside (LogramGraphController.IsBlocked) and aliases its routing-grid cells.
        // A block's left stops one column earlier still, at MIN_LEFT_CELL; a variable's
        // does not - it may sit flush against the edge (see the constant's own comment).
        const top = Math.max(0, (row.y || 0) + roundDy);
        const left = Math.max(row.editor === 'LogramBlock' ? MIN_LEFT_CELL : 0, (row.x || 0) + roundDx);
        this.api.commit(vid, { top, left }).catch((error) => console.warn('logram req.commit failed', vid, error));
      }
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  #handleKeyDown(e) {
    if(e.key !== 'Delete' && e.key !== 'Backspace') return;
    if(['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName)) return;
    if(this.nameState || this.constantState || !this.selected || !this.api) return;
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

  #viewport;
  #scene;
}

customElements.define('x13-logram-document', X13LogramDocument);
