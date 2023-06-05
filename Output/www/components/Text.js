import { BaseComponent } from '/lib/symbiote.js';

class X13_text extends BaseComponent {
  init$ = {
    value: "",
    format: "",
    text_raw: "",
    fg_color:"black",
    bg_color: "white",
  };
  initCallback() {
    this.sub('value', this.update.bind(this));
    this.sub('format', this.update.bind(this));
    this.sub('fg_color', this.updateFg.bind(this));
    this.sub('bg_color', this.updateBg.bind(this));
  }
  update(val) {
    if (this.$.format) {
      if (typeof this.$.value === 'number' && isFinite(this.$.value)) {
        this.$.text_raw = this.$.value.format(this.$.format);
        return;
      }
    }
    this.$.text_raw = this.$.value;
  }
  updateFg(color) {
    this.ref.div.style.color = color;
  }
  updateBg(color) {
    this.ref.div.style.backgroundColor = color;
  }
}
//;color:{{fg_color}};background-color:{{bg_color}}
X13_text.template = /*html*/ `<div ref="div" style="display:inline">{{text_raw}}</div>`;
X13_text.bindAttributes({ "value": "value", "format": "format", "fg_color": "fg_color", "bg_color": "bg_color" });
X13_text.reg("x13-text");