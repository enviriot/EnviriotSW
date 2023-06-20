import { BaseComponent, Data } from '../lib/symbiote.js';
import '../lib/dygraph.min.js';

class X13_wheather extends BaseComponent {
  constructor() {
    super();
    this.t_path = [this.dataset.temperature];
    this.data = [];
    this.list = [];
    this.icons = {};
    this.oldTemp = 0;
  }
  init$ = {
    forecast: {},
    temperature: 0,
  }
  initCallback() {
    let now = new Date().getTime();
    this.sub('forecast', this.mapList.bind(this));
    this.sub('temperature', this.tempChanged.bind(this));
    let opt = {
      width: this.clientWidth - 10,
      height: this.clientHeight - 10,
      dateWindow: [now - 24 * 60 * 60 * 1000, now + 24 * 60 * 60 * 1000],
      connectSeparatedPoints: true,
      labels: ["time", "t1", "t2"],
      series: {
        "t2": { fillGraph: true, fillAlpha:0.05, }
      },
      colors: ['#009BDC', 'rgba(127,206,241,0.5)'],
      underlayCallback: this.underlayCB.bind(this),
    };
    let data = [
      [new Date(), 0, 0]
    ];
    this.g = new Dygraph(this.ref.wh_gr, data, opt);
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
  tempChanged(val) {
    if (this.g && this.data.length > 0 && Math.abs(val - this.oldTemp) > 0.15) {
      this.oldTemp = val;
      this.g.updateOptions({ 'file': this.data });
    }
  }
  mapList(data) {
    if (data.list) {
      let now = new Date();
      now = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), 30, 0);
      let req = "/api/arch04?p=" + encodeURIComponent(JSON.stringify(this.t_path))
        + "&b=" + encodeURIComponent(JSON.stringify(new Date(now.getTime() - 48 * 60 * 60 * 1000)))
        + "&e=" + encodeURIComponent(JSON.stringify(now))
        + "&c=48";
      fetch(req).then(t => t.json()).then(j => this.responseData(j)).catch(e => console.error(e));
      let list = [];
      for (let i in data.list) {
        let j = {};
        j.dt = new Date((data.list[i].dt) * 1000);
        j.t = data.list[i].main.temp;
        j.icon = data.list[i].weather[0].icon;
        if (!this.icons[j.icon]) {
          this.icons[j.icon] = new Image();
          this.icons[j.icon].src = 'https://openweathermap.org/img/w/' + j.icon + '.png';
        }
        j.wind = data.list[i].wind.speed;
        list.push(j);
      }
      this.list = list;
    }
  }
  responseData(arr) {
    if (arr.length == 0) {
      return;
    }
    let data = [];
    let i, j = 0;
    for (i in arr) {
      let v = arr[i][1];
      let dt = new Date(Date.parse(arr[i][0]) + 24 * 60 * 60 * 1000);
      if (j < this.list.length) {
        let dt_f = new Date(this.list[j].dt.getTime());
        if (dt > dt_f) {
          data.push([dt_f, this.list[j].t, null]);
          j++;
        }
      }
      data.push([dt, null, v]);
    }
    for (i in data) {
      let idx = +i + 24;
      if (idx < arr.length && !data[i][1]) {
        data[i][1] = arr[idx][1];
      }
    }
    this.data = data;
    this.g.updateOptions({ 'file': this.data });
  }
  underlayCB(canvas, area, g) {
    var nowX = g.toDomXCoord(new Date());
    canvas.fillStyle = "rgba(128, 255, 128, 0.25)";
    canvas.fillRect(nowX - 3, area.y, 6, area.h);
    canvas.fillStyle = "rgba(0, 0, 0, 1)";
    canvas.font = "40px sans-serif";
    canvas.textAlign = "center";
    canvas.fillText(this.$.temperature.format("0.0 °C"), nowX, 40, 60);
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
/*repeat-item-tag=""*/
/*<div repeat="list"><div class="wh-item"><div>{{t}}</div><img set="@src:icon;@alt:alt"></img><div>{{dt}}</div></div></div>*/
X13_wheather.template = /*html*/ '<div ref="wh_gr"></div>';
X13_wheather.bindAttributes({ "forecast": "forecast", "temperature": "temperature" });

X13_wheather.reg("x13-wheather");