// Масштаб и сдвиг холста Logram и перевод экранных координат в координаты сцены.
import { CELL, CANVAS_PAD } from './logram-scene.js';

const MIN_SCALE = 0.5;
const MAX_SCALE = 5;
// Where the view comes to rest (#resetPan): the canvas's top-left corner sits this far in
// from the panel's own corner. A pan offset and nothing more - the canvas itself carries no
// margin, it is sized to its content.
const VIEW_MARGIN_CELLS = 3;

// ReactiveController хоста-документа. Состояние живёт здесь, а не в свойствах элемента, поэтому
// перерисовку хоста просит сам контроллер - каждым методом, который состояние меняет. Хост рисует
// .viewport (обработчик колёсика, панорамирование) и внутри него .surface (transform); их же
// контроллер и измеряет.
export class LogramViewport {
  constructor(host) {
    this.host = host;
    this.scale = 2.5;
    this.#resetPan();
    host.addController(this);
  }

  // CSS transform для .surface.
  get transform() {
    return `translate(${this.tx}px,${this.ty}px) scale(${this.scale})`;
  }

  // Client px -> content coordinates (cell coordinates * CELL - what element rows and wire
  // waypoints are drawn in). The surface's box starts CANVAS_PAD outside the content, where
  // its viewBox origin sits (see canvasSize in logram-scene.js), so that pad comes back off
  // here once instead of at each of the call sites downstream.
  screenToScene(clientX, clientY) {
    const rect = this.host.renderRoot.querySelector('.surface').getBoundingClientRect();
    const scale = this.scale || 1;
    return { x: (clientX - rect.left) / scale - CANVAS_PAD, y: (clientY - rect.top) / scale - CANVAS_PAD };
  }

  reset() {
    this.scale = 1;
    this.#resetPan();
    this.host.requestUpdate();
  }

  onWheel(e) {
    if(!e.ctrlKey) return;
    e.preventDefault();
    this.zoomBy(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX, e.clientY);
  }

  // Keeps the point under (clientX, clientY) fixed on screen while the scale changes
  // - .surface's transform-origin is a plain 0,0 (see its own CSS rule), so without
  // this the content would visibly drift out from under the cursor on every wheel-
  // zoom tick instead of zooming "into" wherever the pointer is. Falls back to
  // anchoring on the viewport's own center when no cursor position is given (the +/-
  // buttons don't have one) - better than the old unanchored behavior, which drifted
  // toward the surface's origin corner on every click.
  zoomBy(factor, clientX, clientY) {
    const newScale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, this.scale * factor));
    if(newScale === this.scale) return;
    let anchorX = clientX, anchorY = clientY;
    if(anchorX == null || anchorY == null) {
      const rect = this.host.renderRoot.querySelector('.viewport')?.getBoundingClientRect();
      anchorX = rect ? rect.left + rect.width / 2 : 0;
      anchorY = rect ? rect.top + rect.height / 2 : 0;
    }
    const p = this.screenToScene(anchorX, anchorY);
    // screenToScene answers in content coordinates, but the anchor has to be measured from
    // the surface box's own corner - and the viewBox puts that CANVAS_PAD outside the
    // content, so the pad goes back on here. Without it every tick would drift by
    // CANVAS_PAD * (newScale - scale) px, which is the very thing this method exists to avoid.
    this.tx -= (p.x + CANVAS_PAD) * (newScale - this.scale);
    this.ty -= (p.y + CANVAS_PAD) * (newScale - this.scale);
    this.scale = newScale;
    this.host.requestUpdate();
  }

  // Ctrl+drag over empty canvas (see logram-document.js #onSurfacePointerDown). e is the
  // pointerdown on .viewport itself, which also carries the grabbing cursor while it lasts.
  startPan(e) {
    if(e.button !== 0) return;
    e.preventDefault();
    const startX = e.clientX, startY = e.clientY;
    const startTx = this.tx, startTy = this.ty;
    const viewport = e.currentTarget;
    viewport.classList.add('panning');
    const move = (ev) => {
      this.tx = startTx + (ev.clientX - startX);
      this.ty = startTy + (ev.clientY - startY);
      this.host.requestUpdate();
    };
    const up = () => {
      viewport.classList.remove('panning');
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  // The view's resting position: the canvas's top-left corner, VIEW_MARGIN_CELLS in from
  // the panel's own corner. Deliberately a corner and not the middle: the canvas has no
  // server-side size, it's derived from the element rows - and those arrive *after* the
  // LogramCanvas row that used to trigger centering, so "the middle" was computed against
  // a still-empty MIN_CANVAS_CELLS canvas and put every diagram down and to the right, with
  // no second attempt once the real rows landed. A corner needs no size at all, so nothing
  // here has to wait for data.
  #resetPan() {
    this.tx = VIEW_MARGIN_CELLS * CELL;
    this.ty = VIEW_MARGIN_CELLS * CELL;
  }
}
