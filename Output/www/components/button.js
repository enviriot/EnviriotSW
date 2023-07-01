import { BaseComponent } from '../lib/symbiote.js';

class X13_button extends BaseComponent {
  init$ = {
    value: false,
    bg_color:null,
    fg_color:null,
  }
  initCallback() {
    this.sub('fg_color', color => this.style.color = color);
    this.sub('bg_color', color => this.style.backgroundColor = color);
    this.onclick = () => { 
      wsBond.publish(this.dataset.value, true);
      setTimeout(this.clearValue.bind(this), 100); 
    };
  }
  clearValue(){
    wsBond.publish(this.dataset.value, false);
  }
}
X13_button.template = /*html*/ '<slot></slot>';
X13_button.rootStyles = /*css*/ `
x13-button{
  display: block;
  min-width:16mm;
  min-height:12mm;
  border-radius:20%;
  text-align: center;
  font-size:10mm;
}`;
X13_button.bindAttributes({ value: "value", fg_color: "fg_color", bg_color: "bg_color" });
X13_button.reg("x13-button");