import { BaseComponent } from '/lib/symbiote.js';

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
            s.o.$[s.p] = val;
          });
        }
        //wsBond.f.data.pub(sa[1], JSON.parse(sa[2]))
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
      if (els[el] instanceof BaseComponent) {
        for (let at in els[el].dataset) {
          //console.log(els[el].localName + ".[" + at + "]=" + els[el].dataset[at]);
          if (!wsBond.f.subscribes[els[el].dataset[at]]) {
            wsBond.f.subscribes[els[el].dataset[at]] = new Set();
          }
          wsBond.f.subscribes[els[el].dataset[at]].add({ o: els[el], p: at});
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