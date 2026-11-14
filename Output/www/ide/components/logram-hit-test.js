// Что под точкой сцены и что попало в рамку - по сцене из logram-scene.js, в её же координатах
// (px сцены, см. LogramViewport.screenToScene). Без DOM: ответ зависит только от сцены и точки.
import { CELL, elementGeometry } from './logram-scene.js';

// The one hit-test shared by pointerdown (select/drag) and contextmenu - same priority
// for both, so a click and a right-click always agree on what's under the cursor: pin
// (its own CELL-square, "имеет приоритет над всеми") > wire (per segment, CELL wide) >
// block/variable (occupied footprint) > empty canvas (null).
export function hitTest(scene, p) {
  for(const pin of scene.pins) {
    if(Math.abs(p.x - pin.px) <= CELL / 2 && Math.abs(p.y - pin.py) <= CELL / 2) return { type: 'pin', pin };
  }
  for(const wire of scene.wires) {
    for(let i = 0; i < wire.points.length - 1; i++) {
      if(pointNearSegment(p, wire.points[i], wire.points[i + 1], CELL / 2)) return { type: 'wire', vid: wire.vid };
    }
  }
  for(const el of scene.elements) {
    const b = elementGeometry(el);
    if(p.x >= b.x0 && p.x <= b.x1 && p.y >= b.y0 && p.y <= b.y1) return { type: 'element', el };
  }
  return null;
}

// Every block/variable whose own occupied footprint (elementGeometry) intersects the box.
// A zero-size box - a plain click - degenerates into a point-in-rect test against every
// element, so the marquee needs no separate click-vs-drag threshold.
export function marqueeIntersections(scene, box) {
  const vids = new Set();
  for(const el of scene.elements) {
    const b = elementGeometry(el);
    if(b.x0 <= box.x1 && b.x1 >= box.x0 && b.y0 <= box.y1 && b.y1 >= box.y0) vids.add(el.vid);
  }
  return vids;
}

// Distance from `p` to segment a-b, projected onto the segment itself and clamped
// to its own ends (not extended past them with round/square caps) - "по длине
// сегмента и клетка поперёк": a CELL-wide rectangle exactly as long as the segment.
function pointNearSegment(p, a, b, halfWidth) {
  const dx = b.x - a.x, dy = b.y - a.y;
  const lenSq = dx * dx + dy * dy;
  if(lenSq === 0) return Math.hypot(p.x - a.x, p.y - a.y) <= halfWidth;
  const t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / lenSq;
  if(t < 0 || t > 1) return false;
  return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy)) <= halfWidth;
}
