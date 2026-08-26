import { BaseComponent } from '../lib/symbiote.js';
import '../lib/stringformat.min.js';

class X13_thermostat extends BaseComponent {
  init$ = {
    temperature: 0,
    humidity: null,
    setting: null,
    valve: 0,
    status: false,
  };
  initCallback() {
    this.sub('temperature', v => this.ref.ts_tmp.innerText = v.format("#0.0 °C"));
    this.sub('humidity', v => this.ref.ts_hum.innerText = (typeof (v) === 'number' && isFinite(v)) ? v.format("#0.0 '%'") : null);
    this.sub('setting', v => this.ref.ts_set.innerText = (typeof (v) === 'number' && isFinite(v)) ? v.format("#0.0 °C") : null);
    this.sub('valve', this.valveStatusChanged.bind(this));
    this.sub('status', this.valveStatusChanged.bind(this));
  }
  valveStatusChanged(val) {
    let value = this.$.valve;
    let status = this.$.status;
    if (typeof (value) !== 'number' || value<-100 || value>100) {
      return;
    }
    let style;
    if (value > 0) {
      let v = (value * 3.6).toFixed(0) + "deg";
      style = "from " + (180 - value * 1.8).toFixed(0) +"deg, #FFC080 0 " + v + ", #C0C0C0 " + v + " 0);";
    } else if (value < 0) {
      let v = (-value * 3.6).toFixed(0) + "deg";
      style = "from " + (360 + value * 1.8).toFixed(0) + "deg, #80C0FF 0 " + v + ", #C0C0C0 " + v + " 0);";
    } else {
      style = "#C0C0C0 0 100%";
    }
    let bg = (status === true || status === 1 || status==="true") ? "white 30px" : "#FFFFE0 30px, #FF4040, 32px";
    this.ref.ts_div.style = "background:radial-gradient(closest-side, " + bg + ", transparent 38px), conic-gradient(" + style + ");";
  }
}
X13_thermostat.template = /*html*/ `
  <div ref="ts_div">
    <p ref="ts_set"></p>
    <p ref="ts_tmp" class="ts_tmp"></p>
    <p ref="ts_hum"></p>
  </div>`;

X13_thermostat.rootStyles = /*css*/ `
  x13-thermostat > div {
    display:grid;
    width:64px;
    height:50px;
    border-radius: 50%;
    padding: 15px 8px 15px 8px;
  }
  x13-thermostat p {
    margin:0;
    text-align: center;
    font-size:14px;
    height:16px;
    line-height: 16px;
  }
  x13-thermostat .ts_tmp{ 
    font-size:18px;
    height:18px;
    line-height:18px;
    font-weight:bold;
  }`;
X13_thermostat.bindAttributes({ temperature: 'temperature', humidity: 'humidity', setting: 'setting', valve: 'valve', status: 'status' });
X13_thermostat.reg("x13-thermostat");