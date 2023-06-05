import { BaseComponent } from '/lib/symbiote.js';
import '/lib/stringformat.min.js';

window.wsBond = {
  publish: function (path, val) {
    if (path) {
      wsBond.ws.send('P\t' + path + '\t' + JSON.stringify(val));
    }
  },
  subscribe: function (path, cb) {
    if (!wsBond.f.data.has(path)) {
      wsBond.f.data.add(path);
      if (wsBond.ws != null && wsBond.ws.readyState == wsBond.ws.OPEN) {
        wsBond.ws.send('S\t' + path);
      }
    }
    return wsBond.f.data.sub(path, cb);
  },
  f: {
    subscribes: { },
    converters: {
      "format": class {
        constructor(fmt) { 
          this.fmt = fmt;
        }
        convert(val) {
           return ((typeof (val) === 'number' && isFinite(val)) || (val instanceof Date && !isNaN(val.valueOf())))?val.format(this.fmt):val;
        }
      },
      "color": class { 
        constructor(cfg) {
          let arr = cfg.split(';');
          this.p = [];
          let len = arr.length;
          for (let i = 0; i < len; i++) {
            let loc = arr[i].indexOf(':');
            if (loc > 0 && loc < arr[i].length) {
              let ca = arr[i].substring(loc + 1).match(/\w\w/g).map(x => parseInt(x, 16));
              if (ca.length == 3) ca.unshift(255);
              this.p.push({ v: JSON.parse(arr[i].substring(0, loc)), c: ca});
            }
          }
        }
        convert(val) { 
          if (typeof (val) === 'number' && isFinite(val)) { 
            let c;
            if (val < this.p[0].v) {
              c = this.p[0].c;
            } else if (val >= this.p[this.p.length - 1].v) {
              c = this.p[this.p.length - 1].c;
            } else {
              for (let i = 0; i < this.p.length-1; i++) { 
                if (val >= this.p[i].v && val < this.p[i + 1].v) { 
                  let k = (val - this.p[i].v) / (this.p[i+1].v - this.p[i].v);
                  c = [this.p[i].c[0] * (1-k) + this.p[i + 1].c[0] * k,
                       this.p[i].c[1] * (1-k) + this.p[i + 1].c[1] * k,
                       this.p[i].c[2] * (1-k) + this.p[i + 1].c[2] * k,
                       this.p[i].c[3] * (1-k) + this.p[i + 1].c[3] * k];
                  break;
                }
              }
            }
            return String.format("rgba({1:##0},{2:##0},{3:##0},{0:0.0#})", c[0]/255, c[1], c[2], c[3]);
          }
        }
      }
    },
    onMessage: function (evt) {
      console.log(evt.data);
      let sa = evt.data.split('\t');
      if (sa[0] == "P" && sa.length > 2 && sa[2]) {
        let t = sa[1];
        let val = JSON.parse(sa[2]);
        if (wsBond.f.subscribes.hasOwnProperty(t)) {
          wsBond.f.subscribes[t].forEach(s => s.o.$[s.p] = s.c ? s.c.convert(val) : val);
        }
      } else if (sa[0] == 'I' && sa.length == 3) {
        document.cookie = 'sessionId=' + sa[1];
        if (sa[2] == 'true' || (sa[2] == 'null' && localStorage.getItem("userName") == null)) {
          //connected
          for (let path in wsBond.f.subscribes) {
            wsBond.ws.send('S\t' + path);
          }
        }
      }
    },
    createWS: function () {
      if (wsBond.ws == null || wsBond.ws.readyState == wsBond.ws.CLOSED) {
        if (wsBond.ws != null) {
          wsBond.ws.close();
        }
        wsBond.ws = new WebSocket((window.location.protocol == "https:" ? "wss://" : "ws://") + window.location.host + "/api/v04");
        wsBond.ws.onopen = function (evt) {
          wsBond.ws.onmessage = wsBond.f.onMessage;
          wsBond.ws.onclose = function (evt) { setTimeout(wsBond.f.createWS, 1500); };
          wsBond.ws.onerror = function (evt) { setTimeout(wsBond.f.createWS, 1500); };
        };
        setTimeout(wsBond.f.createWS, 15000);
      }
    }
  }
};

document.onreadystatechange = function () {
  if (document.readyState === "complete") {
    let els = document.querySelectorAll('*');
    for (let el in els) {
      let o = els[el];
      if (o instanceof BaseComponent) {
        for (let at in o.dataset) {
          //console.log(els[el].localName + "[" + at + "]=" + els[el].dataset[at]);
          if (at.includes('.')) {
            continue;
          }
          if (!wsBond.f.subscribes[o.dataset[at]]) {
            wsBond.f.subscribes[o.dataset[at]] = new Set();
          }
          let sub = { o: o, p: at };
          for (let fmt in wsBond.f.converters) { 
            if (o.dataset[at + "." + fmt]) {
              sub.c = new wsBond.f.converters[fmt](o.dataset[at + "." + fmt]);
              break;
            }
          }
          wsBond.f.subscribes[o.dataset[at]].add(sub);
        }
      }
    }
    wsBond.f.createWS();
  }
};
window.unload = function (e) {
  if (wsBond.ws != null && wsBond.ws.readyState == wsBond.ws.OPEN) {
    wsBond.ws.close(1000);
  }
  wsBond.ws = null;
}; 