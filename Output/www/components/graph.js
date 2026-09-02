import { BaseComponent } from '../lib/symbiote.js';
import '../lib/dygraph.min.js';
import { pickStep, snapWindow, planFetch, mergeRows, windowMoved } from './graph-grid.js';

// Points asked for per request. The server clamps at 10000 anyway; 500 is about one per four
// pixels on a wide chart, which is as much as anyone can see.
const POINTS = 500;
// How much either side of the view to fetch along with it. Half a view each way means an ordinary
// pan lands inside what is already held and asks the server for nothing at all.
const MARGIN = 0.5;

var blockRedraw = false;

class X13_graph extends BaseComponent {
  constructor() {
    super();
    this.paths = [];
    this.data = [];
    this.reqTimer = null;
    // The in-flight request, or null. Replaces a reqBusy flag that was read but never set to true,
    // so the guard it stood for had never once fired and requests piled up unopposed.
    this.reqCtl = null;
    // What has already been fetched, and on which grid. Only usable because the server now answers
    // on an absolute grid: two windows of the same step put their points at the same instants, so
    // rows from one request can be reused by the next instead of being thrown away.
    this.cache = null;
    this.labels = [];
    this.labels.push("x");
  }
  init$ = {
    period: 2,  // in days
    ylabel: "",
    y2label: "",
    title:"",
  };
  initCallback() {
    let row = [];
    let series = {};
    row.push(new Date());
    for (let l in this.dataset) {
      let di = l.indexOf('.');
      let pn;
      if (di >= 0) {
        pn = l.substring(0, di);
        let cn = l.substring(di + 1);
        if (cn == "y2") {
          series[pn] = { axis: cn };
        }
      } else {
        pn = l;
      }
      this.labels.push(pn);
      this.add(pn, NaN, true);
      this.paths.push(this.dataset[l]);
      this.sub(pn, (val) => {
        this.updateData(pn, val);
      });
      row.push(null);
    }
    let now = (new Date()).getTime();
    this.range = [now - this.$.period * 24 * 60 * 60 * 1000, now];
    let options = {
      width: this.clientWidth - 10,
      height: this.clientHeight - 10,
      dateWindow: this.range,
      connectSeparatedPoints: true,
      legend: 'always',
      series: series,
      labelsDiv: this.ref.gr_ri,
      labelsSeparateLines: false,
      labels: this.labels,
      ylabel: this.$.ylabel,
      y2label: this.$.y2label,
      interactionModel: {
        mousedown: downV3,
        mousemove: moveV3,
        mouseup: upV3,
        mousewheel: scrollV3,
        dblclick: this.dblClickV3.bind(this),
        touchstart: Dygraph.defaultInteractionModel.touchstart,
        touchmove: Dygraph.defaultInteractionModel.touchmove,
        touchend: Dygraph.defaultInteractionModel.touchend,
      },
      drawCallback: this.drawCallback.bind(this),
    };
    this.reqQuery();
    this.g = new Dygraph(this.ref.gr_hl, [row], options);
    // The bound function is kept: bind() returns a new one every call, so the listener added here
    // could never be removed by passing this.resized.bind(this) again - the element stayed
    // reachable from window for as long as the page lived, once per attach.
    this.onResize = this.resized.bind(this);
    window.addEventListener('resize', this.onResize, true);
  }
  disconnectedCallback() {
    if (this.onResize) {
      window.removeEventListener('resize', this.onResize, true);
      this.onResize = null;
    }
    // The debounce and the request in flight go too. Both end in a callback that draws into the
    // chart, and the chart is about to stop existing; leaving them meant a detached element kept
    // alive by a timer, then a call into a destroyed dygraph when it fired.
    if (this.reqTimer) {
      clearTimeout(this.reqTimer);
      this.reqTimer = null;
    }
    if (this.reqCtl) {
      this.reqCtl.abort();
      this.reqCtl = null;
    }
    if (this.g) {
      this.g.destroy();
      this.g = null;
    }
  }
  resized() {
    // Guarded as well as unregistered: a resize already dispatched reaches the handler after
    // destroy(), and dygraph does not survive being resized once it is gone.
    if (!this.g) {
      return;
    }
    if (this.g.width_ != this.clientWidth - 10) {
      this.g.resize(this.clientWidth - 10, this.clientHeight - 10);
    }
  }
  updateData(idx, value) { 
    if (typeof (value) !== 'number' || !isFinite(value)) {
      return;
    }
    let row = [];
    for (let j = 0; j < this.labels.length;j++) {
      if (j == 0) {
        row.push(new Date());
      } else if (this.labels[j] == idx) {
        row.push(value);
      } else {
        row.push(null);
      }
    }
    this.data.push(row);
    if (!this.g) {
      return;   // a live sample arriving after disconnectedCallback
    }
    let opt = { 'file': this.data };
    let range = this.g.xAxisRange();
    if (range[1] - range[0] > 15000 && (row[0].getTime() - range[1]) < ((range[1] - range[0]) / 50)) {
      this.range = [range[0] - range[1] + row[0].getTime(), row[0].getTime()];
      opt["dateWindow"] = this.range;
    }
    this.g.updateOptions(opt);
  }
  responseData(arr, plan, step, target) {
    for (let i in arr) {
      arr[i][0] = new Date(Date.parse(arr[i][0]));
    }
    if (plan.mode === 'replace' || !this.cache || this.cache.step !== step) {
      this.cache = { step: step, from: plan.from, to: plan.to, rows: arr };
    } else {
      // Trimmed to the fetched margin so the cache cannot grow without bound while someone drags
      // along a year of history.
      let from = Math.min(this.cache.from, plan.from);
      let to = Math.max(this.cache.to, plan.to);
      if (from < target[0]) { from = target[0]; }
      if (to > target[1]) { to = target[1]; }
      this.cache = { step: step, from: from, to: to, rows: mergeRows(this.cache.rows, arr, from, to) };
    }
    if (this.cache.rows.length == 0) {
      return;
    }
    // A copy, because updateData appends live samples to this.data and those are not on the grid;
    // letting them into the cache would put them into the next merge.
    this.data = this.cache.rows.slice();
    if (this.g) {
      this.g.updateOptions({ 'file': this.data });
    }
  }
  drawCallback(me, initial) {
    if (blockRedraw || initial) return;
    blockRedraw = true;
    let range = me.xAxisRange();
    let corr = false;
    let now = (new Date).getTime();
    if (range[1] > now) {
      range[1] = now;
      corr = true;
    }
    if (windowMoved(this.range, range)) {
      this.range = [range[0], range[1]];
      this.reqQuery();
    }
    let grl = document.querySelectorAll('x13-graph');
    for (let idx in grl) {
      if (!grl[idx].g || (!corr && grl[idx].g == me)) continue;
      let gro = grl[idx].g.xAxisRange();
      if (corr || windowMoved(gro, range)) {
        // The neighbour's own range has to be moved with it. It cannot do that itself: updateOptions
        // below runs its drawCallback while blockRedraw is still set, so that call returns without
        // touching anything. Leaving it stale is what produced the duplicate requests visible in the
        // server log - the neighbour queried once from here, then again the moment its answer
        // arrived and redrew it, because by then it was comparing the new window against the old one
        // it had never been told about.
        grl[idx].range = [range[0], range[1]];
        grl[idx].g.updateOptions({ dateWindow: range });
        grl[idx].reqQuery();
      }
    }
    blockRedraw = false;
  }
  reqQuery() {
    if (this.reqTimer) {
      clearTimeout(this.reqTimer);
    }
    this.reqTimer = setTimeout(this.doQuery.bind(this), 50);
  }
  doQuery() {
    this.reqTimer = null;
    if (!wsBond.apiToken) {
      // The handshake has not landed yet, so there is no token to send. reqQuery re-arms the
      // same 50 ms debounce this method is already driven by.
      this.reqQuery();
      return;
    }
    // The window agreed in drawCallback, NOT a fresh xAxisRange(). Re-reading it here meant the
    // chart under the mouse used whatever it had drifted to during the 50 ms debounce, while its
    // neighbours used the window they had been handed - so the three asked for windows up to half
    // an hour apart and drew them, which was visible on the page. There is nothing to gain from the
    // fresher value either: the fetch already covers half a window beyond the view on each side.
    let range = this.range;
    let span = range[1] - range[0];
    let step = pickStep(span, POINTS);
    let view = snapWindow(range[0], range[1], step);
    let target = snapWindow(view[0] - span * MARGIN, view[1] + span * MARGIN, step);
    let plan = planFetch(this.cache, view[0], view[1], target[0], target[1], step);
    if (plan.mode === 'none') {
      // Already held. dygraph clips this.data to its own dateWindow, so the new view is on screen
      // without anything being asked for or redrawn - which is the whole point of the margin.
      return;
    }
    // One request per chart at a time. Whatever is still in flight was asked for a window the user
    // has already left, so its answer would only be drawn and then immediately overdrawn; dropping
    // it also keeps a fast gesture from queueing a browser connection per step.
    // Note what this does NOT do: the server has already begun that query and will finish it. What
    // bounds the work over there is asking less often, which is what MOVED_FRACTION is for.
    if (this.reqCtl) {
      this.reqCtl.abort();
    }
    let ctl = new AbortController();
    this.reqCtl = ctl;
    // The count is derived from the step rather than fixed at POINTS, so that a slice fetched to
    // extend the cache is answered on the SAME grid as the rows it will be merged with. Sending
    // 500 for a narrow slice would make the server choose a finer step, and the two would not line
    // up at all.
    let count = Math.max(1, Math.round((plan.to - plan.from) / step));
    let req = "/api/archivist?t=" + encodeURIComponent(wsBond.apiToken)
      + "&p=" + encodeURIComponent(JSON.stringify(this.paths))
      + "&b=" + encodeURIComponent(JSON.stringify(new Date(plan.from)))
      + "&e=" + encodeURIComponent(JSON.stringify(new Date(plan.to)))
      + "&c=" + count;
    fetch(req, { signal: ctl.signal })
      .then(t => t.json())
      .then(j => {
        if (this.reqCtl === ctl) {          // still the current one, so this answer is the answer
          this.reqCtl = null;
          this.responseData(j, plan, step, target);
        }
      })
      .catch(e => {
        if (this.reqCtl === ctl) {
          this.reqCtl = null;
        }
        if (e.name !== 'AbortError') {      // superseding a request is not a failure
          console.error(e);
        }
      });
  }
  dblClickV3(event, g, context) {
    let now = (new Date()).getTime();
    g.updateOptions({ dateWindow: [now - this.$.period * 24 * 60 * 60 * 1000, now] });
  }
}

function downV3(event, g, context) {
  context.initializeMouseDown(event, g, context);
  if (event.altKey || event.shiftKey) {
    Dygraph.startZoom(event, g, context);
  } else {
    Dygraph.startPan(event, g, context);
  }
}
function moveV3(event, g, context) {
  if (context.isPanning) {
    Dygraph.movePan(event, g, context);
  } else if (context.isZooming) {
    Dygraph.moveZoom(event, g, context);
  }
}
function upV3(event, g, context) {
  if (context.isPanning) {
    Dygraph.endPan(event, g, context);
  } else if (context.isZooming) {
    Dygraph.endZoom(event, g, context);
  }
}
function scrollV3(event, g, context) {
  let percentage = event.detail ? event.detail * -0.1 : event.wheelDelta / 400;

  if (!event.offsetX) {
    event.offsetX = event.layerX - event.target.offsetLeft;
  }
  let axis = g.xAxisRange();
  let xOffset = g.toDomCoords(axis[0], null)[0];
  let w = g.toDomCoords(g.xAxisRange()[1], null)[0] - xOffset;
  let bias = (w === 0 ? 0 : ((event.offsetX - xOffset) / w)) || 0.5;
  let increment = (axis[1] - axis[0]) * percentage;
  let foo = [increment * bias, increment * (1 - bias)];
  let wnd = [axis[0] + foo[0], axis[1] - foo[1]];

  g.updateOptions({ dateWindow: wnd });

  event.preventDefault();
}

X13_graph.template = /*html*/ `<div class="gr_top"><div ref="gr_title">{{title}}</div><div ref="gr_ri"></div></div><div ref="gr_hl"></div>`;
X13_graph.bindAttributes({ "period": "period", title: "title", ylabel: "ylabel", y2label: "y2label" });
X13_graph.reg("x13-graph");