import { BaseComponent } from '/lib/symbiote.js';

class X13_button extends BaseComponent {
  init$ = {
    value: false,
    text: "button",
    onUp: () => { this.$.value = false; wsBond.publish(this.dataset.value, this.$.value); },
    onDown: () => { this.$.value = true; wsBond.publish(this.dataset.value, this.$.value); }
  }
}
X13_button.template = /*html*/ '<button set="onmousedown:onDown;onmouseup:onUp">{{text}}</button>';

X13_button.reg("x13-button");