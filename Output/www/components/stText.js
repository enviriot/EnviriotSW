import { BaseComponent } from '../lib/symbiote.js';

class X13_stText extends BaseComponent {
  init$ = {
    value: "",
    fg_color: null,
    bg_color: null,
    status: 0,  // 0 - offline/unknown, 1 - online, 2 - sleep
  };
  initCallback() {
    this.sub('fg_color', color => this.ref.span.style.color = color);
    this.sub('bg_color', color => this.style.backgroundColor = color);
    this.sub('status', this.statusChanged.bind(this));
  }
  statusChanged(value) {
    if (value === true || value === "true") {
      value = 1;
    }
    this.className = "st" + ((value>=0 && value<=2)?(value|0):0).toString();
  }
}
X13_stText.rootStyles = /*css*/ `
x13-st_text {
  display: block;
  border-left: 5px solid #808080;
  padding: 3px 1.5em;
  text-align: center;
}

x13-st_text.st0 {
  border-left-color: #FF8080;
}

x13-st_text.st1 {
  border-left-color: #80FF80;
}

x13-st_text.st2 {
  border-left-color: #8080FF;
}`;

X13_stText.template = /*html*/ `<span ref="span">{{value}}</span>`;
X13_stText.bindAttributes({ "value": "value", "fg_color": "fg_color", "bg_color": "bg_color" });
X13_stText.reg("x13-st_text");