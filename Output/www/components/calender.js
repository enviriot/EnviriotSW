import { BaseComponent } from '../lib/symbiote.js';
import '../lib/stringformat.min.js';

class X13_calDay extends BaseComponent {
  init$ = {
    text: "",
    left: 0,
    right: 0,
    class: ""
  };
  initCallback() {
    this.sub('left', (val) => this.style.gridColumnStart = val);
    this.sub('right', (val) => this.style.gridColumnEnd = val);
    this.sub('class', this.classChanged.bind(this));
  }
  classChanged(val) {
    this.className = val;
  }
}
X13_calDay.template = /*html*/ `<div class=></div><span>{{text}}</span><div></div>`;
X13_calDay.reg("x13-cal-day");

class X13_calEvnt extends BaseComponent {
  init$ = {
    class: null,
    today: false,
    start: null,
    end: null,
    info: null,
  };
  initCallback() {
    this.sub('class', this.classChanged.bind(this));
    this.sub('today', this.classChanged.bind(this));
    this.sub('start', this.startChanged.bind(this));
    this.sub('end', this.endChanged.bind(this));
  }
  classChanged() {
    this.className = this.$.class + (this.$.today?" today":"");
  }
  startChanged() {
    if (this.$.start) {
      this.ref.start.textContent = this.$.start.format("d.MMM");
    }
  }
  endChanged() { 
    if (this.$.end) {
      this.ref.end.textContent = this.$.end.format("d.MMM");
    }
  }

}
X13_calEvnt.template = /*html*/ `<div></div><span ref="start"></span><span>{{info}}</span><span ref="end"></span>`;
X13_calEvnt.reg("x13-cal-evnt");

class X13_calender extends BaseComponent {
  constructor() {
    super();
    this.facts = [];
    this.drawTO = null;
  }
  init$ = {
    value: this.valueChanged,
    dayList: [],
    events: [],
  };
  initCallback() {
    this.drawTO = setTimeout(this.updateDayList.bind(this), 100);
  }
  valueChanged(path, value) {
    let pp = path.split("/");
    if (pp.length != 2) return;
    let pi = value.split(";");
    if (pi.length != 2) return;
    this.facts.push({ class: pp[0], path: path, mask: pi[0], info: pi[1] });
    if (this.drawTO) {
      clearTimeout(this.drawTO);
    }
    this.drawTO = setTimeout(this.updateDayList.bind(this), 50);
  }
  updateDayList() {
    let now = new Date();
    this.timer = setTimeout(this.updateDayList.bind(this), 86401500 - (((now.getHours() *60 +now.getMinutes()) * 60 + now.getSeconds()) * 1000 + now.getMilliseconds()));
    let today = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 12, 0, 0).getTime();
    let start = new Date(now.getFullYear(), now.getMonth() - 1, 1, 12, 0, 0);
    start = start.getTime() - ((start.getDay() == 0 ? 7 : start.getDay()) - 1) * 24 * 60 * 60 * 1000;
    let lst = [];
    let events = [];
    lst.push({ text: "", left: 1, right: 2, class: "week" });
    let j = 0, i = 0;
    let cur = new Date(start);
    
    if (cur.getDate() != 1) {
      lst.push({ text: "", left: 2, right: 3 });  // month = now.month - 2
      j = 1;
    }
    do {
      cur = new Date(start + (j * 7) * 24 * 60 * 60 * 1000);
      i = j + ((((new Date(cur.getFullYear(), cur.getMonth() + 1, 8, 12, 0, 0)).getTime() - cur.getTime()) / (24 * 60 * 60 * 1000) - 1) / 7) | 0;
      if (i > 14) {
        for (; j < 15; j++) {
          lst.push({ text: "", left: j + 2, right: j + 3 });
        }
      } else {
        let cl = "month";
        if (cur.getMonth() & 1) {
          cl += " odd";
        }
        lst.push({ text: cur.format("MMMM"), left: j + 2, right: i + 2, class: cl });
        j = i;
      }
    } while (j < 15)
    for (i = 0; i < 7; i++) {
      for (j = -1; j < 15; j++) {
        if (j == -1) {
          cur = new Date(start + i * 24 * 60 * 60 * 1000);
          lst.push({ text: cur.format("ddd"), left: 1, right: 2, class: "week" });
        } else {
          cur = new Date(start + (j * 7 + i) * 24 * 60 * 60 * 1000);
          let cl = "day";
          let isToday = false;
          if (cur.getMonth() & 1) {
            cl += " odd";
          }
          if (Math.abs(today - cur.getTime()) < 3601000) {  // sommertime
            cl += " today";
            isToday = true;
          }
          for (let k = 0; k < this.facts.length; k++) {
            let f = this.facts[k];
            if (this.checkEvent(f, cur)) {
              cl += " " + f.class;
              let e = events.find(z => z.path == f.path);
              if (e) {
                if (isToday) {
                  e.today = true;
                }
              } else {
                events.push({ class: f.class, today: isToday, start: f.start, end: f.end, info: f.info, path: f.path });
              }
            }
          }
          lst.push({ text: cur.getDate().toString(), left: j + 2, right: j + 3, class: cl });
        }
      }
    }
    this.$.dayList = lst;
    events.sort((a, b) => a.start.getTime() - b.start.getTime());
    this.$.events = events;
  }
  checkEvent(evnt, dt) {
    let p1 = evnt.mask.split("-");
    if (p1.length == 2) {
      let dp = p1[0].split(" ");
      evnt.start = new Date(+dp[0], +dp[1]-1, +dp[2], 6, 0, 0);
      dp = p1[1].split(" ");
      evnt.end = new Date(+dp[0], +dp[1] - 1, +dp[2], 18, 0, 0);
      return (dt.getTime() > evnt.start.getTime()) && (dt.getTime() < evnt.end.getTime());
    } else if (p1.length == 1) {
      let d1 = evnt.mask.split(" ");
      if (d1[0] != "*" && +d1[0] != dt.getFullYear()) return false;
      if (+d1[1] != (dt.getMonth()+1)) return false;
      if (+d1[2] != dt.getDate()) return false;
      evnt.start = dt;
      return true;
    }

  }
}
X13_calender.template = /*html*/ `
  <div class="calender" repeat="dayList" repeat-item-tag="x13-cal-day"></div>
  <div class="events" repeat="events" repeat-item-tag="x13-cal-evnt"></div>`;
X13_calender.bindAttributes({ "value": "value" });
X13_calender.reg("x13-calender");