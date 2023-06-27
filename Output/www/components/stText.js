import { BaseComponent } from '../lib/symbiote.js';

class X13_stText extends BaseComponent {
  init$ = {
    value: "",
    fg_color: null,
    bg_color: null,
    status: 0,  // 0 - offline/unknown, 1 - sensor offline, 2 - online, 3 - sleep
  };
  initCallback() {
    this.sub('fg_color', color => this.ref.span.style.color = color);
    this.sub('bg_color', color => this.style.backgroundColor = color);
    this.sub('status', this.statusChanged.bind(this));
    //this.cc = new wsBonf.f.converters.color('0:#FF8080;1:FFC080;2:#80FF80;3:#8080FF');
  }
  statusChanged(value) {
    this.className = "st" + ((value>=0 && value<=3)?(value|0):0).toString();
  }
}
X13_stText.template = /*html*/ `<span ref="span">{{value}}</span>`;
X13_stText.bindAttributes({ "value": "value", "fg_color": "fg_color", "bg_color": "bg_color" });
X13_stText.reg("x13-st_text");