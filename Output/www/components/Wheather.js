import { BaseComponent, Data } from '../lib/symbiote.js';
import '../lib/dygraph.min.js';

class X13_wheather extends BaseComponent {
  constructor() {
    super();
    this.t_path = [this.dataset.temperature];
    this.data = [];
    this.list = [];
    this.icons = {};
    this.timer = null;
  }
  init$ = {
    forecast: {},
    temperature: 0,
  }
  initCallback() {
    let now = new Date().getTime();
    let opt = {
      width: this.clientWidth - 10,
      height: this.clientHeight - 10,
      'title': this.$.temperature.format("0.0 °C"),
      dateWindow: [now - 24 * 60 * 60 * 1000, now + 24 * 60 * 60 * 1000],
      connectSeparatedPoints: true,
      labels: ["time", "history", "current", "forecast"],
      ylabel: '°C',
      series: {
        "history": { fillGraph: true, fillAlpha:0.05, }
      },
      colors: ['rgba(127,206,241,0.5)', '#009BDC', '#C080FF'],
      underlayCallback: this.underlayCB.bind(this),
      interactionModel: {
        mousedown: downV3,
        mousemove: moveV3,
        mouseup: upV3,
        mousewheel: scrollV3,
        dblclick: dblClickV3,
        touchstart: Dygraph.defaultInteractionModel.touchstart,
        touchmove: Dygraph.defaultInteractionModel.touchmove,
        touchend: Dygraph.defaultInteractionModel.touchend,
      },
    };
    let data = [
      [new Date(), 0, 0, 0]
    ];
    this.g = new Dygraph(this.ref.wh_gr, data, opt);
    this.sub('temperature', this.tempChanged.bind(this));
    this.sub('forecast', this.forecastChanged.bind(this));
    window.addEventListener('resize', this.resized.bind(this), true);
    this.timer = setTimeout(this.reqArchive.bind(this), 30);
  }
  disconnectedCallback() {
    this.g.destroy();
  }
  resized() {
    if (this.g.width_ != this.clientWidth - 10) {
      this.g.resize(this.clientWidth - 10, this.clientHeight - 10);
    }
  }
  tempChanged(val) {
    if (this.g ) {
      this.g.updateOptions({ 'title': this.$.temperature.format("0.0 °C") });
    }
  }
  forecastChanged(data) {
    if (data && Array.isArray(data) && data.length>0) {
      let list = [];
      for (let i in data) {
        let j = {};
        j.dt = new Date((data[i].dt) * 1000);
        this.addData(j.dt, 3, data[i].main.temp);
        j.icon = data[i].weather[0].icon;
        if (!this.icons[j.icon]) {
          this.icons[j.icon] = new Image();
          this.icons[j.icon].src = 'https://openweathermap.org/img/w/' + j.icon + '.png';
        }
        j.wind = data[i].wind.speed;
        list.push(j);
      }
      this.list = list;
      this.g.updateOptions({ 'file': this.data });
    }
  }
  reqArchive() {
    let now = new Date();
    this.timer = setTimeout(this.reqArchive.bind(this), 3600500 - ((now.getMinutes() * 60 + now.getSeconds()) * 1000 + now.getMilliseconds()));
    now = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 30, 0);
    let req = "/api/arch04?p=" + encodeURIComponent(JSON.stringify(this.t_path))
      + "&b=" + encodeURIComponent(JSON.stringify(new Date(now.getTime() - 48 * 60 * 60 * 1000)))
      + "&e=" + encodeURIComponent(JSON.stringify(now))
      + "&c=48";
    fetch(req).then(t => t.json()).then(j => this.responseData(j)).catch(e => console.error(e));
  }
  responseData(arr) {
    if (arr.length == 0) {
      return;
    }
    for (let i in arr) {
      let v = arr[i][1];
      let dt = Date.parse(arr[i][0]);
      this.addData(new Date(dt + 24 * 60 * 60 * 1000), 1, v);
      this.addData(new Date(dt), 2, v);
    }
    this.g.updateOptions({ 'file': this.data });
  }
  addData(dt, idx, value) {
    let i;
    let now = new Date();
    let rangeMin = now.getTime() - 48 * 60 * 60 * 1000;
    while (this.data.length>0 && this.data[0][0].getTime() < rangeMin) {
      this.data.shift();
    }
    rangeMin = now.getTime() - 15 * 60 * 1000;
    for (i = 0; i < this.data.length; i++) {
      if (this.data[i][0].getTime() < rangeMin) {
        this.data[i][3] = NaN;
      }
      let delta = (this.data[i][0].getTime() - dt.getTime())/1000;
      if (Math.abs(delta) < 15) {
        this.data[i][idx] = value;
        break;
      } else if (delta > 0) {
        let row = [dt, null, null, null];
        row[idx] = value;
        this.data.splice(i, 0, row);
        break;
      }
    }
    if (i == this.data.length) {
      let row = [dt, null, null, null];
      row[idx] = value;
      this.data.push(row);
    }
  }
  underlayCB(canvas, area, g) {
    var nowX = g.toDomXCoord(new Date());
    canvas.fillStyle = "rgba(128, 255, 128, 0.25)";
    canvas.fillRect(nowX - 3, area.y, 6, area.h);
    canvas.fillStyle = "rgba(0, 0, 0, 1)";
    canvas.textAlign = "center";
    canvas.font = "20px sans-serif";
    for (let i in this.list) {
      let item = this.list[i];
      let cx = g.toDomXCoord(item.dt);
      if (cx + 25 < area.x + area.w) {
        canvas.drawImage(this.icons[item.icon], cx - 25, area.h / 2 - 30);
        canvas.fillText(item.wind.format("0.0 м/с"), cx, area.h - 15, 50);
      }
    }
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
  let now = (new Date()).getTime();
  g.updateOptions({ dateWindow: [now - 24 * 60 * 60 * 1000, now + 24 * 60 * 60 * 1000] });
}

X13_wheather.template = /*html*/ '<div ref="wh_gr"></div>';
X13_wheather.bindAttributes({ "forecast": "forecast", "temperature": "temperature" });

X13_wheather.reg("x13-wheather");