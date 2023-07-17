import { BaseComponent } from '../lib/symbiote.js';

class X13_range extends BaseComponent {
  init$ = {
    value: false,
    min: 0,
    max: 100,
    actual:null,
    step:1,
  }
  initCallback() {
    this.ref.r1.addEventListener("input", this.onInput.bind(this));
    this.ref.bminus.addEventListener("click", () => { this.ref.r1.stepDown(); this.onInput(); });
    this.ref.bplus.addEventListener("click", () => { this.ref.r1.stepUp(); this.onInput(); });
    this.sub("min", this.rangeChanged.bind(this));
    this.sub("max", this.rangeChanged.bind(this));
    this.sub("actual", this.actualChanged.bind(this));
    //style.setProperty('--text', '"MY CSS TEXT"');
  }
  onInput(event) {
    wsBond.publish(this.dataset.value, this.ref.r1.valueAsNumber);
  }
  rangeChanged(value) {
    let r = Math.abs(this.$.max - this.$.min);
    if (r > 0) {
      let bs = Math.pow(10, Math.round(Math.log10(r / 10)));
      this.$.step = bs / 10;
      this.ref.bminus.value = "-" + this.$.step.toString();
      this.ref.bplus.value = "+" + this.$.step.toString();
      let cur = Math.ceil(this.$.min / bs) * bs;
      this.ref.dl.innerHTML = '';
      do { 
        let option = document.createElement('option');
        option.value = cur;
        this.ref.dl.appendChild(option);
        cur += bs;
      } while (cur <= this.$.max)
    }
  }
  actualChanged(value) {
    if (typeof (value) === "number") {
      this.ref.pr.value = 100 * (value - this.$.min) / (this.$.max - this.$.min);
      this.ref.pr.hidden = false;
    } else {
      this.ref.pr.hidden = true;
    }
  }
}
X13_range.template = /*html*/ `
  <datalist ref="dl" id="range_ds"></datalist>
  <input type="range" ref="r1" set="min:min;value:value;max:max;step:step" list="range_ds">
  <progress ref="pr" max="100"></progress>
  <div><input type="button" ref="bminus"></input><span>{{value}}</span><input type="button" ref="bplus"></input></div>`;
X13_range.bindAttributes({ min: "min", value: "value", max: "max", actual:"actual" });
X13_range.reg("x13-range");