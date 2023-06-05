import { BaseComponent } from '/lib/symbiote.js';

class X13_button extends BaseComponent {
  init$ = {
    value: false,
    onUp: () => { this.$.value = false; wsBond.publish(this.dataset.value, this.$.value); },
    onDown: () => { this.$.value = true; wsBond.publish(this.dataset.value, this.$.value); }
  }
}
X13_button.template = /*html*/ '<button type="button" set="onmousedown:onDown;onmouseup:onUp"><slot></slot></button>';

X13_button.reg("x13-button");