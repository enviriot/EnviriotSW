import { BaseComponent } from '/lib/symbiote.js';

class X13_text extends BaseComponent {
  init$ = {
    value: "",
    fg_color:null,
    bg_color:null,
  };
  initCallback() {
    this.sub('fg_color', color => this.ref.div.style.color = color);
    this.sub('bg_color', color => this.ref.div.style.backgroundColor = color);
  }
}
X13_text.template = /*html*/ `<div ref="div" style="display:inline">{{value}}</div>`;
X13_text.bindAttributes({ "value": "value", "fg_color": "fg_color", "bg_color": "bg_color" });
X13_text.reg("x13-text");