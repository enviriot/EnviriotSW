import { LitElement, html, css } from '../lib/lit-all.min.js';
import '../lib/dygraph.min.js';
import './breadcrumb-bar.js';
import { pickStep, snapWindow, planFetch, mergeRows, windowMoved } from '../components/graph-grid.js';
import { apiUrl } from '../ide_services/api-token.js';
import { readPositiveNumber } from '../ide_services/local-storage-utils.js';

// The other view of the same topic, for the bar's switch button (see breadcrumb-bar.js). Every
// topic a Chart can be opened on has an Inspector side, so it is unconditional here.
const INSPECTOR_VIEW = { label: 'Inspector', mode: 'inspector' };

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
// Below this the window is too narrow to be worth sliding after a live sample, and above it a
// sample landing within this fraction of a span past the right edge counts as "the user is
// watching the live end". Both are x13-graph's numbers (components/graph.js updateData).
const LIVE_MIN_SPAN_MS = 15000;
const LIVE_EDGE_FRACTION = 1 / 50;

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
    // The live tail: a one-row store fed by the `chart#` view (ChartViewProvider). Not
    // `{type: ...}` - the shell hands over the object itself, as it does for every other
    // document's stores.
    store: { attribute: false },
    period: { type: Number },
    message: {},
  };

  static styles = css`
    :host { box-sizing: border-box; display: flex; flex-direction: column; height: 100%; width: 100%; }
    /* Slotted into x13-breadcrumb-bar. min-width:0 is what lets it shrink to nothing instead of
       wrapping the bar - see the bar's own comment on why that matters here. */
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
    this.store = null;
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
    this.observer = null;
    this.redrawing = false;
    this.seriesPath = null;
    this.unsubscribe = null;
  }

  render() {
    return html`
      <x13-breadcrumb-bar .path=${this.path} .rootName=${this.rootName} .altView=${INSPECTOR_VIEW}>
        <div class="legend"></div>
        <select class="period" @change=${this.#onPeriodChange}>
          ${PERIODS.map((item) => html`
            <option value=${item.value} ?selected=${item.value === this.period}>${item.text}</option>`)}
        </select>
      </x13-breadcrumb-bar>
      <div class="plot">
        <div class="canvas"></div>
        ${this.message ? html`<div class="note">${this.message}</div>` : html``}
      </div>`;
  }

  firstUpdated() {
    this.#createGraph();
    this.observer = new ResizeObserver(() => this.#resize());
    this.observer.observe(this.renderRoot.querySelector('.plot'));
  }

  // The shell reuses one element across navigations (app-shell.js #renderContent renders the
  // same template with a different .path), so a new topic has to reset the series in place -
  // labels, cache and window all belong to the topic, not to the component. Compared against
  // seriesPath rather than changed.has('path'): the first render also "changes" path, and
  // firstUpdated has already built the graph on it by the time this runs.
  updated(changed) {
    if(this.g && this.path !== this.seriesPath) this.#resetSeries();
    if(changed.has('store')) this.#subscribeStore();
  }

  // One row, and the only thing wanted from it is that it changed: ChartViewProvider sends a
  // packet per sample without diffing, so every notification is a sample - including one that
  // repeats the previous value.
  #subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((rows) => {
      const row = rows[0];
      if(row) this.#onLiveValue(row.value);
    });
  }

  disconnectedCallback() {
    clearTimeout(this.reqTimer);
    this.reqTimer = null;
    this.unsubscribe?.();
    this.unsubscribe = null;
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

  // A sample straight off the subscription, drawn the moment it arrives - the same bargain
  // x13-graph's updateData strikes, and deliberately the whole of it.
  //
  // What this does NOT do is touch the cache. The cache records which span has been answered by
  // the archive and on which grid, and a live sample is neither: it sits at the instant it
  // arrived, not on the server's bucket boundary. Letting it move that boundary would tell
  // planFetch a span had been fetched when it had not, and mergeRows would then merge an
  // off-grid row into grid rows. So the live tail lives in this.data alone and the next archive
  // answer replaces it wholesale (#applyRows) - by which time the archive holds those samples
  // properly, bucketed.
  //
  // The x-coordinate is the time the value reached the browser, not the time it was sampled;
  // neither this transport nor the dashboard's carries a timestamp. The engine coalesces
  // repeated writes to one topic inside its 15.625 ms tick (Repo.EnquePerf / Perform.EqualsGr),
  // so at most one sample per tick arrives to be stamped.
  #onLiveValue(value) {
    if(typeof value !== 'number' || !isFinite(value) || !this.g) return;
    const stamp = new Date();
    this.data.push([stamp, value]);
    const options = { file: this.data };
    const range = this.g.xAxisRange();
    const span = range[1] - range[0];
    // Follow the live edge only while the user is standing at it. Panned into the past by more
    // than a fiftieth of a span, the window stays where it was put - there is nothing new to the
    // left, and moving it under the reader would be the rudest thing this chart could do.
    if(span > LIVE_MIN_SPAN_MS && (stamp.getTime() - range[1]) < span * LIVE_EDGE_FRACTION) {
      // Assigned before updateOptions, not after: the redraw re-enters #onDraw, which compares
      // against this.range - stale here would read as a user pan and queue an archive request
      // for every sample.
      this.range = [range[0] + (stamp.getTime() - range[1]), stamp.getTime()];
      options.dateWindow = this.range;
    }
    this.g.updateOptions(options);
  }

  #onDraw(me, initial) {
    // updateOptions inside drawCallback re-enters it; without the guard #onLiveValue's window
    // shift would recurse through here and queue a request per sample.
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
