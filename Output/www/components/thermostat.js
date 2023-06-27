import { BaseComponent } from '../lib/symbiote.js';
import '../lib/stringformat.min.js';

class X13_thermostat extends BaseComponent {
  init$ = {
    temperature: 0,
    humidity: 0,
    setting: 0,
    valve: 0,
    sensor: false,
    actor: false,
  };
  initCallback() {
    this.sub('temperature', this.temperatureChanged.bind(this));
    this.sub('humidity', this.humidityChanged.bind(this));
    this.sub('setting', this.settingChanged.bind(this));
    this.sub('valve', this.valveChanged.bind(this));
    this.sub('sensor', this.sensorChanged.bind(this));
    this.sub('actor', this.actorChanged.bind(this));
  }
  temperatureChanged(value) {
    this.ref.ts_tmp.innerText = value.format("#0.0 °C");
  }
  humidityChanged(value) {
    this.ref.ts_hum.innerText = value.format("#0.0 '%'");
  }
  settingChanged(value) {
    this.ref.ts_set.innerText = value.format("#0.0 °C");
  }
  valveChanged(value) {
    if (typeof (value) !== 'number' || value<-100 || value>100) {
      return;
    }
    let style;
    if (value > 0) {
      let v = (value * 3.6).toFixed(0) + "deg";
      style = "from " + (180 - value * 1.8).toFixed(0) +"deg, #FFC080 0 " + v + ", #C0C0C0 " + v + " 0);";
    } else if (value < 0) {
      let v = (-value * 3.6).toFixed(0) + "deg";
      style = "from " + (360 + value * 1.8).toFixed(0) + "deg, #8080FF 0 " + v + ", #C0C0C0 " + v + " 0);";
    } else {
      style = "#C0C0C0 0 100%";
    }
    this.ref.ts_div.style = "background:radial-gradient(closest-side, white 32px, transparent 32px 40px), conic-gradient(" + style + ");";
  }
  sensorChanged(value) {
    this.ref.ts_hum.className = value?"":"ts_offline";
  }
  actorChanged(value) {
    this.ref.ts_set.className = value ? "" : "ts_offline";
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
    height:52px;
    border-radius: 50%;
    padding: 14px 8px 14px 8px;
  }
  x13-thermostat p {
    margin:0;
    text-align: center;
    font-size:12px;
    height:16px;
    line-height: 16px;
  }
  x13-thermostat .ts_tmp{ 
    font-size:16px;
    font-weight:bold;
    height:20px;
    line-height: 20px;
    border: 0px;
  }
  x13-thermostat .ts_offline{ 
    background: linear-gradient(to right, #00FF0000 12px, #FFE080 13px, #FFE080 51px, #00FF0000 52px);
  }`;
X13_thermostat.bindAttributes({ temperature: 'temperature', humidity: 'humidity', setting: 'setting', valve: 'valve', sensor: 'sensor', actor: 'actor' });
X13_thermostat.reg("x13-thermostat");