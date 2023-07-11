import { BaseComponent } from '../lib/symbiote.js';

class X13_checkbox extends BaseComponent {
  init$ = {
    value: false,
    bg_color:null,
  }
  initCallback() {
    this.sub('bg_color', color => this.style.backgroundColor = color);
    this.sub('value', this.valueChanged.bind(this));
    this.onclick = () => { 
      this.$.value = !this.$.value;
      wsBond.publish(this.dataset.value, this.$.value);
    };
  }
  valueChanged(value) {
    this.style.color = value ? "black" : "#40808080";
    //this.style.borderColor = value ? "#C0FFC0" : "#40C0C0C0";
  }
}
X13_checkbox.template = /*html*/ '<slot></slot>';
X13_checkbox.bindAttributes({ value: "value", bg_color: "bg_color" });
X13_checkbox.reg("x13-checkbox");