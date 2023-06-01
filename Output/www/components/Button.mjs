import { BaseComponent } from '/lib/symbiote.mjs';

class X13_button extends BaseComponent {
  init$ = {
    value: false,
    text: "button",
    onUp: () => { this.$.value = false; window.Lank.publish(this.id, this.$.value); },
    onDown: () => { this.$.value = true; window.Lank.publish(this.id, this.$.value); }
  }
}
X13_button.template = /*html*/ '<button set="onmousedown:onDown;onmouseup:onUp">{{text}}</button>';

X13_button.reg("x13-button");