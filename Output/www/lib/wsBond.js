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
    subscribes: {},
    onMessage: function (evt) {
      console.log(evt.data);
      let sa = evt.data.split('\t');
      if (sa[0] == "P" && sa.length > 2 && sa[2]) {
        let t = sa[1];
        let val = JSON.parse(sa[2]);
        if (wsBond.f.subscribes.hasOwnProperty(t)) {
          wsBond.f.subscribes[t].forEach((s) => {
            let v = val;
            if (s.format) {
              if (typeof v === 'number' && isFinite(v)) {
                v = v.format(s.format);
              }
            }
            s.o.$[s.p] = v;  // publish
          });
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
          if (at.endsWith(".format") || at.endsWith(".convert")) {
            continue;
          }
          if (!wsBond.f.subscribes[o.dataset[at]]) {
            wsBond.f.subscribes[o.dataset[at]] = new Set();
          }
          let sub = { o: o, p: at };
          if (o.dataset[at + ".format"]) { 
            sub.format = o.dataset[at + ".format"];
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