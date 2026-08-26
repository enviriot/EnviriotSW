import { BaseComponent } from '../lib/symbiote.js';

class X13_status extends BaseComponent {
  init$ = {
    value: null,
  }
  initCallback() {
    this.sub('value', this.valueChanged.bind(this));
  }
  valueChanged(val) {
    let cl;
    if (val === 0 || val === false) {
      cl = "offline";
    } else if (val === 1 || val === true) {
      cl = "online";
    } else if (val === 2) {
      cl = "sleep";
    } else {
      cl = "unknown"
    }
    this.ref.div.className = "status_" + cl;
  }
}
X13_status.rootStyles = /*css*/ `
x13-status>div {
    width:16px;
    height:16px;
    border-radius: 50%;
    background-color: gray;
}
x13-status>div.status_unknown {background-color:lightgray;}
x13-status>div.status_offline {background-color:#FF8080;}
x13-status>div.status_online {background-color:#80FF80;}
x13-status>div.status_sleep {background-color:#8080FF;}
`;

X13_status.template = /*html*/ `<div ref="div"></div>`;
X13_status.bindAttributes({ value: "value" });
X13_status.reg("x13-status");
