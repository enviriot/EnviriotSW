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
      title: this.$.title,
      dateWindow: this.range,
      connectSeparatedPoints: true,
      legend: 'follow',
      series: series,
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
    window.addEventListener('resize', this.resized.bind(this), true);
  }
  disconnectedCallback() {
    this.g.destroy();
  }
  resized() {
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
    let opt = { 'file': this.data };
    let range = this.g.xAxisRange();
    if (range[1] - range[0] > 15000 && (row[0].getTime() - range[1]) < ((range[1] - range[0]) / 50)) {
      this.range = [range[0] - range[1] + row[0].getTime(), row[0].getTime()];
      opt["dateWindow"] = this.range;
    }
    this.g.updateOptions(opt);
  }
  responseData(arr) {
    this.reqBusy = false;
    if (arr.length == 0) {
      return;
    }
    for (let i in arr) {
      arr[i][0] = new Date(Date.parse(arr[i][0]));
    }
    this.data = arr;
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
    if (Math.abs(this.range[0] - range[0]) > 60000 || Math.abs(this.range[1] - range[1]) > 60000) {
      this.range = range;
      this.reqQuery();
    }
    let grl = document.querySelectorAll('x13-graph');
    for (let idx in grl) {
      if (!grl[idx].g || (!corr && grl[idx].g == me)) continue;
      let gro = grl[idx].g.xAxisRange();
      if (corr || Math.abs(gro[0] - range[0]) > 60000 || Math.abs(gro[1] - range[1]) > 60000) {
        grl[idx].g.updateOptions({ dateWindow: range });
      }
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
    this.reqTimer = setTimeout(this.doQuery.bind(this), 50);
  }
  doQuery() {
    this.reqTimer = null;
    let range = this.g.xAxisRange();
    let req = "/api/arch04?p=" + encodeURIComponent(JSON.stringify(this.paths))
      + "&b=" + encodeURIComponent(JSON.stringify(new Date(range[0])))
      + "&e=" + encodeURIComponent(JSON.stringify(new Date(range[1])))
      + "&c=500";
    fetch(req).then(t => t.json()).then(j => this.responseData(j)).catch(e => console.error(e));
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

X13_graph.template = /*html*/ `<div ref="gr_hl"></div>`;
X13_graph.bindAttributes({ "period": "period", title: "title", ylabel: "ylabel", y2label: "y2label" });
X13_graph.reg("x13-graph");