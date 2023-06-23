import { BaseComponent, Data } from '../lib/symbiote.js';
import '../lib/dygraph.min.js';

class X13_wheather extends BaseComponent {
  constructor() {
    super();
    this.t_path = [this.dataset.temperature];
    this.data = [];
    this.icons = {};
    this.timer = null;
  }
  init$ = {
    forecast: {},
    temperature: 0,
  }
  initCallback() {
    let now = new Date();
    now = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 0, 0).getTime();
    let opt = {
      width: this.clientWidth - 10,
      height: this.clientHeight - 10,
      //title: this.$.temperature.format("0.0 °C"),
      dateWindow: [now - 24 * 60 * 60 * 1000, now],
      connectSeparatedPoints: true,
      legend: 'always',
      labels: ["time", "history", "current", "forecast"],
      labelsDiv: this.ref.wh_le,
      labelsSeparateLines: false,
      ylabel: '°C',
      series: {
        history: { fillGraph: true, fillAlpha:0.05, }
      },
      colors: ['rgba(127,206,241,0.5)', '#009BDC', '#C080FF'],
      underlayCallback: this.underlayCB.bind(this),
      interactionModel: {
        dblclick: dblClickV3,
      },
    };
    this.g = new Dygraph(this.ref.wh_gr, [[new Date(), 0, 0, 0]], opt);
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
    if (this.g) {
      this.ref.wh_hd.innerText = this.$.temperature.format("0.0 °C");
      //this.g.updateOptions({ 'title': this.$.temperature.format("0.0 °C") });
    }
  }
  forecastChanged(data) {
    if (data && Array.isArray(data) && data.length>0) {
      for (let i = 0; i < data.length; i++) {
        let dt1 = new Date(data[i].dt);
        data[i].dt = new Date(dt1.getTime() - 24 * 60 * 60 * 1000);
        this.addData(data[i].dt, 3, data[i].t);
        if (!this.icons[data[i].i]) {
          this.icons[data[i].i] = new Image();
          this.icons[data[i].i].src = '/img/' + data[i].i + '.png';
        }
      }
      this.g.updateOptions({ 'file': this.data });
    }
  }
  reqArchive() {
    let now = new Date();
    this.timer = setTimeout(this.reqArchive.bind(this), 3600000 - ((now.getMinutes() * 60 + now.getSeconds()) * 1000 + now.getMilliseconds()));
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
    let opt = {};
    let i;
    let now = new Date();
    now = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 0, 0).getTime();

    if (this.data.length > 1) {
      for (i = this.data.length - 1; i >= 0; i--) {
        if (this.data[i][1]) {
          opt.dateWindow = [now - 24 * 60 * 60 * 1000, now];
          break;
        }
      }
    }

    for (i in arr) {
      let v = arr[i][1];
      let dt = Date.parse(arr[i][0]);
      this.addData(new Date(dt + 24 * 60 * 60 * 1000), 1, v);
      this.addData(new Date(dt), 2, v);
    }
    opt['file'] = this.data;

    this.g.updateOptions(opt);
  }
  addData(dt, idx, value) {
    let i;
    let now = new Date();
    now = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 0, 0);
    let rangeMin = now.getTime() - 24 * 60 * 60 * 1000;
    while (this.data.length>0 && this.data[0][0].getTime() < rangeMin) {
      this.data.shift();
    }
    for (i = 0; i < this.data.length; i++) {
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
    let width = (area.w / 50) | 0;
    canvas.textAlign = "center";
    canvas.font = "12px sans-serif";
    for (let i = 0; i < this.$.forecast.length; i++) {
      let item = this.$.forecast[i];
      let cx = g.toDomXCoord(item.dt);
      if (cx + width < area.x + area.w) {
        if (i == 0 || item.i != this.$.forecast[i - 1].i) {
          let img = this.icons[item.i];
          if (img.complete && img.width > 0) {
            canvas.drawImage(img, cx - width, area.y + area.h / 2 - width, width * 2, width * 2);
          } else {
            canvas.fillStyle = 'red';
            canvas.fillText(item.i, cx, area.y + area.h / 2);
          }
        }
        if (item.u > 0.25) {
          canvas.beginPath();
          canvas.arc(cx, area.y, (width * Math.min(0.5, 0.25 + item.u / 20)).toFixed(0), 0, Math.PI);
          canvas.fillStyle = 'hsl(' + (Math.max(300, 498 - item.u * 18) % 360).toFixed(0) + ',100%, 50%)';
          canvas.fill();

        }
        canvas.fillStyle = 'hsl(' + Math.min(359, 180 + item.w * 3).toFixed(0) + ',100%, 50%)';
        let h = area.h * Math.min(0.4, item.w / 240);
        canvas.fillRect(cx -  2, area.y + area.h - h, 4, h);
      }
    }
  }
}

function dblClickV3(event, g, context) {
  let now = new Date();
  now = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 0, 0).getTime();
  g.updateOptions({ dateWindow: [now - 24 * 60 * 60 * 1000, now] });
}

X13_wheather.template = /*html*/ '<table><tr><td></td><td><p ref="wh_hd"></p></td><td><div ref="wh_le"></div></td></tr><tr class="content"><td colspan="3"><div ref="wh_gr"></div></td></tr></table>';
X13_wheather.bindAttributes({ "forecast": "forecast", "temperature": "temperature" });

X13_wheather.reg("x13-wheather");