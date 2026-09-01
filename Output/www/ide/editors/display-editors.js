import { LitElement, html, css } from '../../lib/lit-all.min.js';
import { formatDefault } from './editor-format.js';

class X13DisplayEditor extends LitElement {
  static properties = {
    view: {},
    readonly: { type: Boolean, reflect: true },
    selected: { type: Boolean, reflect: true },
    state: { attribute: false },
  };

  static styles = css`
    :host {
      display: inline-flex;
      max-width: 100%;
      vertical-align: middle;
    }
    .display {
      align-items: center;
      border-radius: 0;
      box-sizing: border-box;
      color: #334155;
      display: inline-flex;
      gap: 4px;
      min-height: 24px;
      min-width: 0;
      overflow: hidden;
      padding: 0 4px;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    :host([selected]) .display { background: #fff; }
    .invalid { color: #c2410c; }
    .link-like { color: #1d4ed8; text-decoration: underline; text-underline-offset: 2px; }
    .badge {
      border-radius: 6px;
      display: inline-block;
      flex: 0 0 auto;
      height: 12px;
      width: 12px;
    }
    .ok { background: #2f7d20; }
    .error { background: #b91c1c; }
    .info { background: #2563eb; }
    .unknown { background: #64748b; }
    .text { overflow: hidden; text-overflow: ellipsis; }
    .via { color: #000; font-weight: bold; }
    .via-muted { color: #64748b; }
    .plc-actions { display: inline-flex; gap: 2px; margin-left: 4px; }
    .plc-action {
      background: #fff;
      border: 1px solid #94a3b8;
      border-radius: 3px;
      color: #334155;
      cursor: pointer;
      flex: 0 0 auto;
      font: inherit;
      font-size: 11px;
      line-height: 16px;
      padding: 1px 6px;
    }
    .plc-action:hover { background: #eef2f7; }
    .plc-action:active { background: #dde5ee; }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.rpc = typeof options.rpc === 'function' ? options.rpc : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = undefined;
  }

  renderDisplay(content, classes = '') {
    return html`<span class="display ${classes}">${content}</span>`;
  }
}

export class X13VersionEditor extends X13DisplayEditor {
  render() {
    const text = legacyPrefixedText(this.state, '¤VR');
    if(text !== null) return this.renderDisplay(html`<span class="text">${text}</span>`);
    return this.renderDisplay(html`<span class="text">###-##</span>`, 'invalid');
  }
}

export class X13MsStatusEditor extends X13DisplayEditor {
  render() {
    const status = msStatus(this.state);
    const via = status.via ? html`<span class="via">${status.via}</span>` : null;
    return this.renderDisplay(html`
      <span class="badge ${status.kind}"></span>
      ${via}`);
  }
}

export class X13PortStatusEditor extends X13DisplayEditor {
  render() {
    const status = portStatus(this.state);
    return this.renderDisplay(html`
      <span class="badge ${status.kind}"></span>
      <span class="text">${status.label}</span>`);
  }
}

export class X13EsStatusEditor extends X13DisplayEditor {
  render() {
    const status = esStatus(this.state);
    return this.renderDisplay(html`
      <span class="badge ${status.kind}"></span>
      <span class="text">${status.label}</span>`);
  }
}

const PLC_ACTIONS = [
  ['Build', 'action:MQTT_SN.PLC.Build'],
  ['Run', 'action:MQTT_SN.PLC.Run'],
  ['Start', 'action:MQTT_SN.PLC.Start'],
  ['Stop', 'action:MQTT_SN.PLC.Stop'],
];

export class X13DevicePlcEditor extends X13DisplayEditor {
  render() {
    const status = devicePlcStatus(this.state);
    const via = status.via ? html`<span class="via-muted">${status.via}</span>` : null;
    const actions = this.view === 'value' ? html`
      <span class="plc-actions">
        ${PLC_ACTIONS.map(([label, cmd]) => html`
          <button type="button" class="plc-action" @click=${(e) => this.#onAction(e, cmd)}>${label}</button>`)}
      </span>` : null;
    return this.renderDisplay(html`
      <span class="badge ${status.kind}"></span>
      ${via}
      <span class="text">${status.label}</span>
      ${actions}`);
  }

  #onAction(e, cmd) {
    e.preventDefault();
    e.stopPropagation();
    this.rpc(cmd);
  }
}

function legacyPrefixedText(value, prefix) {
  if(typeof value !== 'string' || !value.startsWith(prefix)) return null;
  return value.slice(prefix.length);
}

function msStatus(value) {
  let st = -1;
  let via = '';
  if(typeof value === 'number') st = Math.trunc(value);
  else if(value && typeof value === 'object') {
    if(typeof value.st === 'number') st = Math.trunc(value.st);
    if(typeof value.via === 'string') via = value.via;
  }

  if(st === 0) return { kind: 'error', label: 'offline', via: '' };
  if(st === 1) return { kind: 'ok', label: via ? 'online via' : 'online', via };
  if(st === 2) return { kind: 'info', label: via ? 'sleep via' : 'sleep', via };
  return { kind: 'unknown', label: 'unknown', via: '' };
}

function portStatus(value) {
  if(value && typeof value === 'object') {
    if(typeof value.connected === 'boolean') return value.connected ? { kind: 'ok', label: 'connected' } : { kind: 'error', label: 'disconnected' };
    if(typeof value.state === 'string') return portStatus(value.state);
    if(typeof value.status === 'string') return portStatus(value.status);
    if(typeof value.st === 'number') return portStatus(value.st);
  }
  if(typeof value === 'boolean') return value ? { kind: 'ok', label: 'connected' } : { kind: 'error', label: 'disconnected' };
  if(typeof value === 'number') {
    if(value === 1) return { kind: 'ok', label: 'connected' };
    if(value === 0) return { kind: 'error', label: 'disconnected' };
  }
  if(typeof value === 'string') {
    const text = value.trim();
    const normalized = text.toLowerCase();
    if(['connected', 'online', 'ready', 'open', 'true', '1'].includes(normalized)) return { kind: 'ok', label: text || 'connected' };
    if(['disconnected', 'offline', 'closed', 'false', '0'].includes(normalized)) return { kind: 'error', label: text || 'disconnected' };
    if(text) return { kind: 'info', label: text };
  }
  return { kind: 'unknown', label: formatDefault(value) || 'unknown' };
}

function esStatus(value) {
  if(value && typeof value === 'object') {
    const address = typeof value.Address === 'string' ? value.Address : '';
    const dns = typeof value.Dns === 'string' ? value.Dns : '';
    const port = typeof value.Port === 'number' ? String(Math.trunc(value.Port)) : '';
    const host = dns || address;
    if(host && port) return { kind: 'ok', label: `${host}:${port}` };
    if(host) return { kind: 'ok', label: host };
  }
  return { kind: 'unknown', label: formatDefault(value) || 'unknown' };
}

function devicePlcStatus(value) {
  let st;
  let via = '';
  if(typeof value === 'number') st = Math.trunc(value);
  else if(value && typeof value === 'object') {
    if(typeof value.st === 'number') st = Math.trunc(value.st);
    if(typeof value.via === 'string') via = value.via;
  }

  if(st === 1) return { kind: 'ok', label: 'run', via };
  if(st === 2) return { kind: 'error', label: 'stop', via };
  return { kind: 'unknown', label: 'unknown', via };
}

customElements.define('x13-version-editor', X13VersionEditor);
customElements.define('x13-ms-status-editor', X13MsStatusEditor);
customElements.define('x13-port-status-editor', X13PortStatusEditor);
customElements.define('x13-es-status-editor', X13EsStatusEditor);
customElements.define('x13-device-plc-editor', X13DevicePlcEditor);
