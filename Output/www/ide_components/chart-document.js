import { LitElement, html, css } from '../lib/lit-all.min.js';
import '../lib/dygraph.min.js';
import { pickStep, snapWindow, planFetch, mergeRows, windowMoved } from '../components/graph-grid.js';
import { apiUrl } from '../ide_services/api-token.js';
import { readPositiveNumber } from '../ide_services/local-storage-utils.js';

const PERIOD_KEY = 'x13.chart.period';
const HOUR = 60 * 60 * 1000;
const DAY = 24 * HOUR;
const PERIODS = [
  { text: '1 hour', value: HOUR },
  { text: '6 hours', value: 6 * HOUR },
  { text: '1 day', value: DAY },
  { text: '2 days', value: 2 * DAY },
  { text: '1 week', value: 7 * DAY },
  { text: '1 month', value: 30 * DAY },
  { text: '1 year', value: 365 * DAY },
];
const DEFAULT_PERIOD = DAY;

// The numbers x13-graph settled on, and for the same reasons - see components/graph.js. 500
// points is about one per four pixels on a wide chart; fetching half a view beyond each edge
// means an ordinary pan lands inside what is already held and asks the server for nothing.
const POINTS = 500;
const MARGIN = 0.5;
const QUERY_DEBOUNCE_MS = 50;
// How often the chart looks at whether it has fallen behind "now". Not a poll of the topic: it
// does anything at all only while the view is parked at the right-hand edge (see #tick).
const TAIL_INTERVAL_MS = 10000;

// The Chart document: one topic's archived history, opened from the "Chart" entry the server
// puts in a tree's context menu for topics whose Arch.enable is on (MenuBuilder.cs).
//
// Not <x13-graph>. That component belongs to the dashboard stack: it takes its topics from
// data-* attributes, subscribes to live values through wsBond (a second websocket, on the
// dashboard's per-topic access model rather than the IDE's), and synchronises every other chart
// on the page through document.querySelectorAll. None of that applies here. What IS shared is
// the grid arithmetic - pickStep/snapWindow/planFetch/mergeRows/windowMoved in
// components/graph-grid.js - and that is imported rather than reimplemented, because its step
// ladder is chosen so the server's bucket rounding is a no-op and both sides agree on where
// points sit. It is also the only part of either chart the test suite can reach.
export class X13ChartDocument extends LitElement {
  static properties = {
    path: {},
    rootName: {},
    period: { type: Number },
    message: {},
  };

  static styles = css`
    :host { box-sizing: border-box; display: flex; flex-direction: column; height: 100%; width: 100%; }
    /* Wraps, as inspector-document's bar does: a deep path in a narrow pane has to grow the bar
       downwards, never push it sideways past the pane. The legend does not take part - it shrinks
       to nothing instead (min-width:0 below), so the line it sits on cannot be broken by whatever
       value happens to be under the cursor, which would make the bar - and with it the chart -
       change height on every mouse move. */
    .bar {
      align-items: center;
      background: #f3f6fa;
      border-bottom: 1px solid #cfd8e3;
      display: flex;
      flex: 0 0 auto;
      flex-wrap: wrap;
      font-size: 13px;
      gap: 2px;
      padding: 6px 10px;
    }
    .segment {
      background: transparent;
      border: 1px solid transparent;
      border-radius: 0px;
      color: #1f2937;
      cursor: default;
      font: inherit;
      padding: 3px 6px;
    }
    .segment:hover {
      background: #e8f1ff;
      border-color: #8ab4f8;
    }
    .segment.current {
      font-weight: 600;
    }
    .sep {
      color: #9aa7b6;
      padding: 0 1px;
    }
    .legend {
      color: #243447;
      flex: 1 1 auto;
      margin: 0 12px;
      min-width: 0;
      overflow: hidden;
      text-align: right;
      white-space: nowrap;
    }
    .period {
      background: #fff;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #243447;
      flex: 0 0 auto;
      font: inherit;
      font-size: 12px;
      padding: 2px 4px;
    }
    .plot {
      background: #fff;
      flex: 1 1 auto;
      min-height: 0;
      /* The chart is sized in script, one frame behind the box it lives in. Clipping here keeps
         that gap from ever reaching the shell: .content-pane scrolls, so a canvas even briefly
         wider than its pane would put scrollbars on the document - and, through the shell's
         auto-height grid, on the window too. */
      overflow: hidden;
      position: relative;
    }
    /* Sized by dygraph, which pins width/height inline - hence top/left rather than inset:0,
       which would only look as if this box tracked its parent. What keeps it out of the flex
       layout, and so unable to stretch .plot, is being taken out of flow. */
    .canvas {
      left: 0;
      position: absolute;
      top: 0;
    }
    .note {
      background: rgba(255, 255, 255, 0.9);
      border: 1px solid #cfd8e3;
      border-radius: 3px;
      color: #556575;
      font-size: 13px;
      left: 50%;
      padding: 8px 14px;
      position: absolute;
      top: 45%;
      transform: translate(-50%, -50%);
    }

    /* dygraph's own stylesheet. It has to live here rather than in ide_app.css: dygraph builds
       its legend and axis labels inside this component's shadow root, which page CSS does not
       reach. Copied from index.css, where the dashboard pages carry the same block. */
    /* No .dygraph-legend rule: dygraph sets that class only on a legend div it created itself
       (plugins/legend.js activate). We hand it labelsDiv, so it takes our .legend as it is and
       leaves it alone - the rule would never match. These two do match: dygraph writes both
       classes straight into the legend's HTML. */
    .dygraph-legend-line {
      border-bottom-style: solid;
      border-bottom-width: 2px;
      bottom: .5ex;
      display: inline-block;
      height: 1px;
      padding-left: 1em;
      position: relative;
      text-align: left;
    }
    .dygraph-legend-dash {
      border-bottom-style: solid;
      border-bottom-width: 2px;
      bottom: .5ex;
      display: inline-block;
      height: 1px;
      position: relative;
    }
    .dygraph-axis-label {
      color: black;
      line-height: normal;
      overflow: hidden;
      z-index: 10;
    }
    .dygraph-title {
      font-weight: bold;
      text-align: center;
      z-index: 10;
    }
    .dygraph-xlabel {
      text-align: center;
    }
    .dygraph-label-rotate-left {
      text-align: center;
      transform: rotate(90deg);
    }
    .dygraph-label-rotate-right {
      text-align: center;
      transform: rotate(-90deg);
    }
  `;

  constructor() {
    super();
    this.path = '/';
    this.rootName = '';
    this.period = readPositiveNumber(PERIOD_KEY, DEFAULT_PERIOD);
    this.message = '';
    this.g = null;
    this.range = [0, 0];
    // Rows already held, and the grid they sit on. Only usable because the server answers on an
    // absolute grid: two windows of the same step put their points at the same instants, so a
    // pan can reuse what the previous request brought instead of throwing it away.
    this.cache = null;
    this.data = [];
    this.reqTimer = null;
    this.reqCtl = null;
    this.tailTimer = null;
    this.observer = null;
    this.redrawing = false;
    this.seriesPath = null;
  }

  render() {
    const segments = this.#segments();
    return html`
      <div class="bar">
        ${segments.map((segment, index) => html`
          ${index > 0 ? html`<span class="sep">/</span>` : html``}
          <button
            type="button"
            class="segment ${index === segments.length - 1 ? 'current' : ''}"
            @click=${() => this.#emit('open', segment.path)}>
            ${segment.name || '/'}
          </button>
        `)}
        <div class="legend"></div>
        <select class="period" @change=${this.#onPeriodChange}>
          ${PERIODS.map((item) => html`
            <option value=${item.value} ?selected=${item.value === this.period}>${item.text}</option>`)}
        </select>
      </div>
      <div class="plot">
        <div class="canvas"></div>
        ${this.message ? html`<div class="note">${this.message}</div>` : html``}
      </div>`;
  }

  firstUpdated() {
    this.#createGraph();
    this.observer = new ResizeObserver(() => this.#resize());
    this.observer.observe(this.renderRoot.querySelector('.plot'));
    this.tailTimer = setInterval(() => this.#tick(), TAIL_INTERVAL_MS);
  }

  // The shell reuses one element across navigations (app-shell.js #renderContent renders the
  // same template with a different .path), so a new topic has to reset the series in place -
  // labels, cache and window all belong to the topic, not to the component. Compared against
  // seriesPath rather than changed.has('path'): the first render also "changes" path, and
  // firstUpdated has already built the graph on it by the time this runs.
  updated() {
    if(this.g && this.path !== this.seriesPath) this.#resetSeries();
  }

  disconnectedCallback() {
    clearTimeout(this.reqTimer);
    clearInterval(this.tailTimer);
    this.reqTimer = null;
    this.tailTimer = null;
    this.reqCtl?.abort();
    this.reqCtl = null;
    this.observer?.disconnect();
    this.observer = null;
    this.g?.destroy();
    this.g = null;
    super.disconnectedCallback();
  }

  #createGraph() {
    const host = this.renderRoot.querySelector('.canvas');
    const plot = this.renderRoot.querySelector('.plot');
    const now = Date.now();
    this.seriesPath = this.path;
    this.range = [now - this.period, now];
    // One all-null seed row rather than []. Not because dygraph refuses an empty file - it has
    // accepted one as "no data yet" since 2.2.1 (#597) - but because of what it turns that into:
    // parseArray_ rewrites [] as [[0]], a single column, and then compares the column count
    // against our two labels and calls console.error("Mismatch between number of labels...")
    // every time. A row of [Date, null] has the two columns the labels promise, so it passes
    // silently, and the first answer replaces it.
    this.data = [[new Date(now), null]];
    this.g = new window.Dygraph(host, this.data, {
      width: plot.clientWidth,
      height: plot.clientHeight,
      dateWindow: this.range,
      connectSeparatedPoints: true,
      labels: ['x', this.#seriesName()],
      labelsDiv: this.renderRoot.querySelector('.legend'),
      labelsSeparateLines: false,
      legend: 'always',
      interactionModel: {
        mousedown: onDown,
        mousemove: onMove,
        mouseup: onUp,
        mousewheel: onWheel,
        dblclick: () => this.#resetWindow(),
        touchstart: window.Dygraph.defaultInteractionModel.touchstart,
        touchmove: window.Dygraph.defaultInteractionModel.touchmove,
        touchend: window.Dygraph.defaultInteractionModel.touchend,
      },
      drawCallback: (me, initial) => this.#onDraw(me, initial),
    });
    this.#requestQuery();
  }

  #resetSeries() {
    this.reqCtl?.abort();
    this.reqCtl = null;
    this.cache = null;
    this.message = '';
    this.seriesPath = this.path;
    const now = Date.now();
    this.range = [now - this.period, now];
    this.data = [[new Date(now), null]];  // two columns, as in #createGraph - see the note there
    this.g.updateOptions({ labels: ['x', this.#seriesName()], file: this.data, dateWindow: this.range });
    this.#requestQuery();
  }

  #seriesName() {
    const parts = String(this.path || '/').split('/').filter(Boolean);
    return parts.length ? parts[parts.length - 1] : '/';
  }

  #segments() {
    const parts = String(this.path || '/').split('/').filter(Boolean);
    const segments = [{ name: this.rootName, path: '/' }];
    let current = '';
    for(const part of parts) {
      current += `/${part}`;
      segments.push({ name: part, path: current });
    }
    return segments;
  }

  // The same event inspector-document.js raises from its breadcrumb, so app-shell.js
  // #onSegmentCommand routes it without having to know a Chart document exists. isLogram stays
  // undefined on purpose - a chart knows nothing about the topic's type, and undefined is what
  // sends the shell through its single-round-trip resolve instead of guessing Inspector.
  #emit(cmd, path) {
    this.dispatchEvent(new CustomEvent('segment-command', {
      bubbles: true,
      composed: true,
      detail: { cmd, path, isLogram: undefined },
    }));
  }

  #onPeriodChange(e) {
    const value = Number(e.target.value);
    if(!Number.isFinite(value) || value <= 0) return;
    this.period = value;
    localStorage.setItem(PERIOD_KEY, String(value));
    this.#resetWindow();
  }

  #resetWindow() {
    const now = Date.now();
    this.range = [now - this.period, now];
    this.g?.updateOptions({ dateWindow: this.range });
    this.#requestQuery();
  }

  // Measured off .plot, the box that has the space, never off .canvas. Dygraph pins width and
  // height inline on the element it draws into, so .canvas reports whatever size it was last
  // given - asking it how much room there is hands back the answer from before the resize and
  // g.resize becomes a no-op. That is not a cosmetic slip: the pinned canvas keeps its old size
  // while the pane shrinks around it, overflows into .content-pane, and puts a horizontal and a
  // vertical scrollbar on the document - and the shell's auto-height grid passes the vertical
  // one on to the window. Every splitter drag went through here.
  #resize() {
    const plot = this.renderRoot.querySelector('.plot');
    if(!this.g || !plot || !plot.clientWidth || !plot.clientHeight) return;
    this.g.resize(plot.clientWidth, plot.clientHeight);
  }

  // Keeps the right-hand edge on "now" while the user is looking at the live end, and does
  // nothing once they pan into the past - there is no new data to the left. A live subscription
  // would be the obvious alternative, but the IDE has no way to subscribe to a topic outside an
  // open tree, and the re-query path is here regardless.
  #tick() {
    if(document.hidden || !this.g) return;
    const now = Date.now();
    const span = this.range[1] - this.range[0];
    if(now - this.range[1] > span * MARGIN) return;
    this.range = [now - span, now];
    // Pulling the cache's right-hand boundary back one step is what makes this a refresh rather
    // than a scroll. Without it planFetch answers "already covered" - the fetched margin runs
    // well past now - and the window would slide along showing nothing new. What sits at that
    // boundary is a bucket that stopped at "now" instead of at the end of its interval, so it
    // has to be asked for again; mergeRows lets the newer copy win the tie.
    if(this.cache) {
      const tail = Math.max(this.cache.from, this.range[1] - this.cache.step);
      if(tail < this.cache.to) this.cache.to = tail;
    }
    this.g.updateOptions({ dateWindow: this.range });
    this.#requestQuery();
  }

  #onDraw(me, initial) {
    // updateOptions inside drawCallback re-enters it; without the guard #tick's window shift
    // would recurse through here and queue a request per frame.
    if(initial || this.redrawing) return;
    this.redrawing = true;
    const range = me.xAxisRange();
    const now = Date.now();
    if(range[1] > now) range[1] = now;
    if(windowMoved(this.range, range)) {
      this.range = [range[0], range[1]];
      this.#requestQuery();
    }
    this.redrawing = false;
  }

  #requestQuery() {
    clearTimeout(this.reqTimer);
    this.reqTimer = setTimeout(() => this.#query(), QUERY_DEBOUNCE_MS);
  }

  async #query() {
    this.reqTimer = null;
    if(!this.g || !this.path) return;
    // The window agreed in #onDraw, not a fresh xAxisRange(): re-reading it here would use
    // whatever the chart drifted to during the debounce, and the fetch already covers half a
    // window beyond the view on each side anyway.
    const range = this.range;
    const span = range[1] - range[0];
    const step = pickStep(span, POINTS);
    const view = snapWindow(range[0], range[1], step);
    const target = snapWindow(view[0] - span * MARGIN, view[1] + span * MARGIN, step);
    const plan = planFetch(this.cache, view[0], view[1], target[0], target[1], step);
    if(plan.mode === 'none') return;  // already held; dygraph clips this.data to its own window

    // One request at a time. Whatever is still in flight was asked for a window the user has
    // already left, so its answer would only be drawn and immediately overdrawn.
    this.reqCtl?.abort();
    const ctl = new AbortController();
    this.reqCtl = ctl;
    // Derived from the step rather than fixed at POINTS: a slice fetched to extend the cache has
    // to come back on the SAME grid as the rows it will be merged with, and the server picks its
    // grid from the count it is given.
    const count = Math.max(1, Math.round((plan.to - plan.from) / step));
    const url = apiUrl('/api/archivist'
      + '?p=' + encodeURIComponent(JSON.stringify([this.path]))
      + '&b=' + encodeURIComponent(JSON.stringify(new Date(plan.from)))
      + '&e=' + encodeURIComponent(JSON.stringify(new Date(plan.to)))
      + '&c=' + count);
    try {
      const response = await fetch(url, { signal: ctl.signal });
      if(this.reqCtl !== ctl) return;  // superseded while the answer was on the wire
      if(!response.ok) {
        this.message = await describeFailure(response);
        return;
      }
      this.#applyRows(await response.json(), plan, step, target);
    }
    catch(error) {
      if(error.name === 'AbortError') return;  // superseding a request is not a failure
      console.warn('archivist request failed', this.path, error);
      this.message = 'History request failed';
    }
    finally {
      if(this.reqCtl === ctl) this.reqCtl = null;
    }
  }

  #applyRows(rows, plan, step, target) {
    for(const row of rows) row[0] = new Date(Date.parse(row[0]));
    if(plan.mode === 'replace' || !this.cache || this.cache.step !== step) {
      this.cache = { step: step, from: plan.from, to: plan.to, rows: rows };
    } else {
      // Trimmed to the fetched margin, so the cache cannot grow without bound while someone
      // drags along a year of history.
      const from = Math.max(Math.min(this.cache.from, plan.from), target[0]);
      const to = Math.min(Math.max(this.cache.to, plan.to), target[1]);
      this.cache = { step: step, from: from, to: to, rows: mergeRows(this.cache.rows, rows, from, to) };
    }
    if(this.cache.rows.length === 0) {
      // Clear the plot as well as saying so. A zoom is a "replace", and leaving the previous
      // window's points on screen under a "no samples" note reads as a chart that simply
      // stopped responding.
      this.message = 'No samples in this window';
      this.data = [[new Date(this.range[1]), null]];  // two columns - see the note in #createGraph
      this.g.updateOptions({ file: this.data });
      return;
    }
    this.message = '';
    this.data = this.cache.rows.slice();
    this.g.updateOptions({ file: this.data });
  }
}

// A refusal has to say which of the two locks closed. 404 is deliberate on the server's side -
// an invalid token is answered as if the endpoint did not exist - so it is reported as what it
// actually means here: the session that issued the token is gone.
async function describeFailure(response) {
  if(response.status === 403) return 'No access to this topic history';
  if(response.status === 404) return 'The session has expired - reload the page';
  let code = '';
  try {
    const body = await response.json();
    code = body?.error?.code || '';
  }
  catch {
    code = '';
  }
  if(code === 'archivist_unavailable') return 'No archive provider is loaded';
  return `History request failed (HTTP ${response.status})`;
}

// Pan with the mouse, zoom with Alt or Shift - the same bargain x13-graph struck, because a
// history chart is panned far more often than it is zoomed.
function onDown(event, g, context) {
  context.initializeMouseDown(event, g, context);
  if(event.altKey || event.shiftKey) {
    window.Dygraph.startZoom(event, g, context);
  } else {
    window.Dygraph.startPan(event, g, context);
  }
}

function onMove(event, g, context) {
  if(context.isPanning) window.Dygraph.movePan(event, g, context);
  else if(context.isZooming) window.Dygraph.moveZoom(event, g, context);
}

function onUp(event, g, context) {
  if(context.isPanning) window.Dygraph.endPan(event, g, context);
  else if(context.isZooming) window.Dygraph.endZoom(event, g, context);
}

function onWheel(event, g, context) {
  const percentage = event.detail ? event.detail * -0.1 : event.wheelDelta / 400;
  const axis = g.xAxisRange();
  const xOffset = g.toDomCoords(axis[0], null)[0];
  const width = g.toDomCoords(axis[1], null)[0] - xOffset;
  const offsetX = event.offsetX || (event.layerX - event.target.offsetLeft);
  const bias = (width === 0 ? 0 : ((offsetX - xOffset) / width)) || 0.5;
  const increment = (axis[1] - axis[0]) * percentage;
  g.updateOptions({ dateWindow: [axis[0] + increment * bias, axis[1] - increment * (1 - bias)] });
  event.preventDefault();
}

customElements.define('x13-chart-document', X13ChartDocument);
