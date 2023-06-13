import { BaseComponent } from '../lib/symbiote.js';
import '../lib/dygraph.min.js';

var blockRedraw = false;
class X13_graph extends BaseComponent {
  init$ = {
  };
  initCallback() {
    let paths = [];
    for (let l in this.dataset) { 
      this.add(l, NaN, true);
      paths.push(this.dataset[l]);
      this.sub(l, (val) => {
        this.updateData(l, val);
      });
    }
    let now = new Date().getTime();
    wsBond.query(paths, new Date(now - 2 * 24 * 60 * 60 * 1000), new Date(), this.responseData);
    this.data = [];
    for (let i = -15; i < 0; i++) {
      let row = []
      row.push(new Date(now + i * 60000));
      for (let l in this.dataset) {
        row.push(Math.random() * 255);
      }
      this.data.push(row);
    }
    this.options = {
      connectSeparatedPoints: true,
      width: this.offsetWidth-10,
      height: this.offsetHeight - 10,
      drawCallback: drawCallback,
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
  responseData(data) {
    
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
// Take the offset of a mouse event on the dygraph canvas and
// convert it to a pair of percentages from the bottom left.
// (Not top left, bottom is where the lower value is.)
function offsetToPercentage(g, offsetX, offsetY) {
  // This is calculating the pixel offset of the leftmost date.
  var xOffset = g.toDomCoords(g.xAxisRange()[0], null)[0];
  var yar0 = g.yAxisRange(0);

  // This is calculating the pixel of the higest value. (Top pixel)
  var yOffset = g.toDomCoords(null, yar0[1])[1];

  // x y w and h are relative to the corner of the drawing area,
  // so that the upper corner of the drawing area is (0, 0).
  var x = offsetX - xOffset;
  var y = offsetY - yOffset;

  // This is computing the rightmost pixel, effectively defining the
  // width.
  var w = g.toDomCoords(g.xAxisRange()[1], null)[0] - xOffset;

  // This is computing the lowest pixel, effectively defining the height.
  var h = g.toDomCoords(null, yar0[0])[1] - yOffset;

  // Percentage from the left.
  var xPct = w === 0 ? 0 : (x / w);
  // Percentage from the top.
  var yPct = h === 0 ? 0 : (y / h);

  // The (1-) part below changes it from "% distance down from the top"
  // to "% distance up from the bottom".
  return [xPct, (1 - yPct)];
}
function scrollV3(event, g, context) {
  var normal = event.detail ? event.detail * -1 : event.wheelDelta / 40;
  // For me the normalized value shows 0.075 for one click. If I took
  // that verbatim, it would be a 7.5%.
  var percentage = normal / 50;

  if (!(event.offsetX && event.offsetY)) {
    event.offsetX = event.layerX - event.target.offsetLeft;
    event.offsetY = event.layerY - event.target.offsetTop;
  }

  var percentages = offsetToPercentage(g, event.offsetX, event.offsetY);
  var xPct = percentages[0];
  var yPct = percentages[1];

  zoom(g, percentage, xPct, yPct);
  event.preventDefault();
}
// Adjusts [x, y] toward each other by zoomInPercentage%
// Split it so the left/bottom axis gets xBias/yBias of that change and
// tight/top gets (1-xBias)/(1-yBias) of that change.
//
// If a bias is missing it splits it down the middle.
function zoom(g, zoomInPercentage, xBias, yBias) {
  xBias = xBias || 0.5;
  yBias = yBias || 0.5;
  function adjustAxis(axis, zoomInPercentage, bias) {
    var delta = axis[1] - axis[0];
    var increment = delta * zoomInPercentage;
    var foo = [increment * bias, increment * (1 - bias)];
    return [axis[0] + foo[0], axis[1] - foo[1]];
  }
  var yAxes = g.yAxisRanges();
  var newYAxes = [];
  for (var i = 0; i < yAxes.length; i++) {
    newYAxes[i] = adjustAxis(yAxes[i], zoomInPercentage, yBias);
  }

  g.updateOptions({
    dateWindow: adjustAxis(g.xAxisRange(), zoomInPercentage, xBias),
    //valueRange: newYAxes[0]
  });
}
function dblClickV3(event, g, context) {
  g.resetZoom();
}
function drawCallback(me, initial) {
  if (blockRedraw || initial) return;
  blockRedraw = true;
  let range = me.xAxisRange();
  let grl = document.querySelectorAll('x13-graph');
  for (let idx in grl) {
    if (!grl[idx].g || grl[idx].g == me) continue;
    grl[idx].g.updateOptions({ dateWindow: range });
  }
  blockRedraw = false;
}
X13_graph.template = /*html*/ `<div ref="gr_hl"></div>`;
//X13_text.bindAttributes({ "value": "value", "fg_color": "fg_color", "bg_color": "bg_color" });
X13_graph.reg("x13-graph");