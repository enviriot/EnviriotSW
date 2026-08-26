import { BaseComponent } from '../lib/symbiote.js';
import '../lib/stringformat.min.js';

class X13_range extends BaseComponent {
  init$ = {
    value: false,
    format: null,
    min: 0,
    max: 100,
    actual: null,
    step: 1,
  }
  initCallback() {
    this.ref.r1.addEventListener("input", this.onInput.bind(this));
    this.ref.bminus.addEventListener("click", () => { this.ref.r1.stepDown(); this.onInput(); });
    this.ref.bplus.addEventListener("click", () => { this.ref.r1.stepUp(); this.onInput(); });
    this.sub("value", this.valueChanged.bind(this));
    this.sub("format", this.valueChanged.bind(this));
    this.sub("min", this.rangeChanged.bind(this));
    this.sub("max", this.rangeChanged.bind(this));
    this.sub("actual", this.actualChanged.bind(this));
    //style.setProperty('--text', '"MY CSS TEXT"');
  }
  valueChanged(v) {
    if (typeof (this.$.value) === "number") {
      this.ref.vst.innerHTML = this.$.format ? this.$.value.format(this.$.format) : this.$.value;
    }
  }
  onInput(event) {
    wsBond.publish(this.dataset.value, this.ref.r1.valueAsNumber);
  }
  rangeChanged(value) {
    let r = Math.abs(this.$.max - this.$.min);
    if (r > 0) {
      let bs = Math.pow(10, Math.round(Math.log10(r / 10)));
      this.$.step = bs / 10;
      let l = this.$.format ? this.$.step.format(this.$.format) : this.$.step.toString();
      this.ref.bminus.value = "-" + l;
      this.ref.bplus.value = "+" + l;
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
  <div style="width: 100%;display: grid;grid-template-columns: 15mm auto 15mm;grid-template-rows: auto auto auto;">
  <input type="button" ref="bminus" style="grid-area:1/1/4/2;"></input>
  <span ref="vst" style="text-align:center;grid-area:1/2/2/3;"></span>
  <input type="range" ref="r1" style="grid-area:2/2/3/3;margin:0 2%" set="min:min;value:value;max:max;step:step" list="range_ds">
  <datalist ref="dl" id="range_ds"></datalist>
  <progress ref="pr" max="100" style="grid-area:3/2/4/3;margin:0 2%;width:96%;"></progress>
  <input type="button" ref="bplus" style="grid-area:1/3/4/4"></input></div>`;
X13_range.bindAttributes({ min: "min", value: "value", max: "max", format: "format", actual: "actual" });
X13_range.reg("x13-range");