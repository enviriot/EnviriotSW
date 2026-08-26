import { BaseComponent } from '../lib/symbiote.js';

class X13_signal extends BaseComponent {
  init$ = {
    value: "gray",
    label: "",
  }
  initCallback() {
    this.sub('value', color => this.ref.sign.style.backgroundColor = color);
  }
}

X13_signal.template = /*html*/ `<label>{{label}}</label><span ref="sign"></span>`;
X13_signal.bindAttributes({ value: "value", label:"label"});
X13_signal.reg("x13-signal");
