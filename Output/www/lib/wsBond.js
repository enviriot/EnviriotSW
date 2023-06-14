import { BaseComponent } from './symbiote.js';
import './stringformat.min.js';

window.wsBond = {
  publish: function (path, val) {
    if (path) {
      wsBond.ws.send('P\t' + path + '\t' + JSON.stringify(val));
      wsBond.f.processInpPublish(path, val);
    }
  },
  query: function (topics, start, stop, cb) {
    let id = 0;
    while (wsBond.f.querys[id]) {
      id++;
    }
    wsBond.f.querys[id] = { t: topics, start: start, stop: stop, cb: cb };
    if (wsBond.ws && wsBond.ws.readyState == wsBond.ws.OPEN) {
      wsBond.ws.send('A\t' + id + '\t' + JSON.stringify(topics) + '\t' + JSON.stringify(start) + '\t' + JSON.stringify(stop));
    }
  },
  f: {
    subscribes: {},
    querys: [],
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
              this.p.push({v: JSON.parse(arr[i].substring(0, loc)), c: this.hex2hsla(arr[i].substring(loc + 1)) });
            }
          }
        }
        convert(val) {
          if (typeof (val) === 'number' && isFinite(val)) {
            let c;
            if (val <= this.p[0].v) {
              c = this.p[0].c;
            } else if (val >= this.p[this.p.length - 1].v) {
              c = this.p[this.p.length - 1].c;
            } else {
              for (let i = 0; i < this.p.length - 1; i++) {
                if (val >= this.p[i].v && val < this.p[i + 1].v) {
                  let k = (val - this.p[i].v) / (this.p[i + 1].v - this.p[i].v);
                  c = [this.p[i].c[0] * (1 - k) + this.p[i + 1].c[0] * k,
                  this.p[i].c[1] * (1 - k) + this.p[i + 1].c[1] * k,
                  this.p[i].c[2] * (1 - k) + this.p[i + 1].c[2] * k,
                  this.p[i].c[3] * (1 - k) + this.p[i + 1].c[3] * k];
                  break;
                }
              }
            }
            return String.format("hsla({0:##0},{1:##0}%,{2:##0}%,{3:0.0#})", c[0], c[1] * 100, c[2] * 100, c[3]);
          } else {
            let kv = this.p.find(z => z.v === val);
            if (kv) {
              return String.format("hsla({0:##0},{1:##0}%,{2:##0}%,{3:0.0#})", kv.c[0], kv.c[1] * 100, kv.c[2] * 100, kv.c[3]);
            }
          }
        }
        hex2hsla(hex) {
          let ca = hex.match(/\w\w/g).map(x => parseInt(x, 16)/255);
          if (ca.length == 3) ca.unshift(1);
          let r = ca[1], g = ca[2], b = ca[3];
          let v = Math.max(r, g, b), c = v - Math.min(r, g, b), f = (1 - Math.abs(v + v - c - 1));
          let h = c && ((v == r) ? (g - b) / c : ((v == g) ? 2 + (b - r) / c : 4 + (r - g) / c));
          return [60 * (h < 0 ? h + 6 : h), f ? c / f : 0, (v + v - c) / 2, ca[0]];
        }
      }
    },
    processInpPublish(path, value) {
      if (wsBond.f.subscribes.hasOwnProperty(path)) {
        for (var it = wsBond.f.subscribes[path].values(), s = null; s = it.next().value;) { 
          try {
            s.o.$[s.p] = s.c ? s.c.convert(value) : value;
          } catch (error) {
            console.error("processInpPublish(" + path + ")[" + s.p + "] - " + error);
          }
        }
      }
    },
    processResponse(id, data) {
      if (typeof (id) !== 'number' || !isFinite(id) || !Array.isArray(data)) {
        return;
      }
      let cb = wsBond.f.querys[id].cb;
      if (cb) {
        wsBond.f.querys[id] = null;
        try {
          cb(data);
        } catch (error) {
          console.error("processResponse(" + id + ") - " + error);
        }
      }
    },
    onMessage: function (evt) {
      console.log(evt.data);
      let sa = evt.data.split('\t');
      if (sa[0] == "P" && sa.length > 2 && sa[2]) {
        wsBond.f.processInpPublish(sa[1], JSON.parse(sa[2]));
      } else if (sa[0] == 'A' && sa.length == 3) {
        wsBond.f.processResponse(JSON.parse(sa[1]), JSON.parse(sa[2]));
      } else if (sa[0] == 'I' && sa.length == 3) {
        document.cookie = 'sessionId=' + sa[1];
        if (sa[2] == 'true' || (sa[2] == 'null' && localStorage.getItem("userName") == null)) {
          //connected
          for (let path in wsBond.f.subscribes) {
            wsBond.ws.send('S\t' + path);
          }
          for (let id in wsBond.f.querys) {
            let aq = wsBond.f.querys[id];
            wsBond.ws.send('A\t' + id + '\t' + JSON.stringify(aq.t) + '\t' + JSON.stringify(aq.start) + '\t' + JSON.stringify(aq.stop));
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
    let fcr = function (oc, pn) {
      if (!oc[pn]) {
        oc[pn] = {};
      }
    }
    for (let el in els) {
      let o = els[el];
      if (o instanceof BaseComponent) {
        let oc = { o:o };
        let defaultTopic;
        for (let pni in o.dataset) {
          //console.log(els[el].localName + "[" + at + "]=" + els[el].dataset[at]);
          let di = pni.indexOf('.');
          if (di >= 0) {
            let pn = pni.substring(0, di);
            let cn = pni.substring(di + 1);
            if (wsBond.f.converters[cn]) {
              fcr(oc, pn);
              oc[pn].converter = new wsBond.f.converters[cn](o.dataset[pni]);
            }
          } else {
            fcr(oc, pni);
            oc[pni].topic = o.dataset[pni];
            if (!defaultTopic || pni == "value") {
              defaultTopic = o.dataset[pni];
            }
          }
        }
        for (let pn in oc) {
          if (pn == "o") {
            continue;
          }
          let pv = oc[pn];
          if (!pv.topic) {
            pv.topic = defaultTopic;
          }
          if (!wsBond.f.subscribes[pv.topic]) {
            wsBond.f.subscribes[pv.topic] = new Set();
          }
          let sub = { o: oc.o, p: pn };  // object, property
          if (pv.converter) {
            sub.c = pv.converter;
          }
          wsBond.f.subscribes[pv.topic].add(sub);
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
