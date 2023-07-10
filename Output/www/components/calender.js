import { BaseComponent } from '../lib/symbiote.js';

class X13_calender extends BaseComponent {
  init$ = {
    value: this.valueChanged,
  };
  initCallback() {
  }
  valueChanged(path, value) {
    console.log(path + "=" + JSON.stringify(value));
  }
}
X13_calender.template = /*html*/ `<div ref="root"></div>`;
X13_calender.bindAttributes({ "value": "value"});
X13_calender.reg("x13-calender");