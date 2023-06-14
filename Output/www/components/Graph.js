import { BaseComponent } from '../lib/symbiote.js';
import '../lib/dygraph.min.js';

var blockRedraw = false;
class X13_graph extends BaseComponent {
  constructor() {
    super();
    this.paths = [];
    this.data = [];
    this.reqTimer = null;
    this.reqBusy = false;
  }
  init$ = {
  };
  initCallback() {
    for (let l in this.dataset) { 
      this.add(l, NaN, true);
      this.paths.push(this.dataset[l]);
      this.sub(l, (val) => {
        this.updateData(l, val);
      });
    }
    let now = new Date().getTime();
    this.reqQuery();
    this.options = {
      connectSeparatedPoints: true,
      width: this.offsetWidth-10,
      height: this.offsetHeight - 10,
      drawCallback: this.drawCallback.bind(this),
      interactionModel: {
        mousedown: downV3,
        mousemove: moveV3,
        mouseup: upV3,
        mousewheel: scrollV3,
        dblclick: dblClickV3,
      },
      labels: ['x'].concat(Object.keys(this.dataset))
    };
    this.g = new Dygraph(this.ref.gr_hl, this.data, this.options);
  }
  updateData(idx, value) { 
    if (typeof (value) !== 'number' || !isFinite(value)) {
      return;
    }
    let row = [];
    for (let j = 0; j < this.options.labels.length;j++) {
      if (j == 0) {
        row.push(new Date());
      } else if (this.options.labels[j] == idx) {
        row.push(value);
      } else {
        row.push(null);
      }
    }
    this.data.push(row);
    this.g.updateOptions({ 'file': this.data });
  }
  responseData(arr) {
    this.reqBusy = false;
    if (arr.length == 0) {
      return;
    }
    for (let i in arr) {
      arr[i][0] = new Date(Date.parse(arr[i][0]));
      this.data.push(arr[i]);
    }
    this.data.sort((a, b) => a[0] - b[0]);
    this.g.updateOptions({ 'file': this.data });
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
    let dRange = me.xAxisExtremes();
    if (range[0] < dRange[0]) {
      this.reqQuery();
    }
    let grl = document.querySelectorAll('x13-graph');
    for (let idx in grl) {
      if (!grl[idx].g || (!corr && grl[idx].g == me)) continue;
      grl[idx].g.updateOptions({ dateWindow: range });
    }
    blockRedraw = false;
  }
  reqQuery() {
    if (this.reqBusy) { 
      return;
    }
    if (this.reqTimer) {
      clearTimeout(this.reqTimer);
    }
    this.reqTimer = setTimeout(this.doQuery.bind(this), 100);
  }
  doQuery() {
    this.reqTimer = null;
    let end , begin;
    if (this.data.length > 0) {
      begin = this.g.xAxisRange()[0];
      end = this.g.xAxisExtremes()[0];
    } else {
      end = (new Date()).getTime();
      begin = end - 10 * 60 * 1000;
    }
    wsBond.query(this.paths, new Date(begin), new Date(end), this.responseData.bind(this));
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
function dblClickV3(event, g, context) {
  g.resetZoom();
}

X13_graph.template = /*html*/ `<div ref="gr_hl"></div>`;
//X13_text.bindAttributes({ "value": "value", "fg_color": "fg_color", "bg_color": "bg_color" });
X13_graph.reg("x13-graph");