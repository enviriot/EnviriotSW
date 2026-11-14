import { LitElement, html, css } from '../../lib/lit-all.min.js';

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

const PLC_ACTIONS = [
  ['Build', 'action:MQTT_SN.PLC.Build'],
  ['Run', 'action:MQTT_SN.PLC.Run'],
  ['Start', 'action:MQTT_SN.PLC.Start'],
  ['Stop', 'action:MQTT_SN.PLC.Stop'],
];

export class X13DevicePlcEditor extends X13DisplayEditor {
  static properties = {
    compact: { type: Boolean },
    modified: { attribute: false },
  };

  static styles = [X13DisplayEditor.styles, css`
    .compact, .compact-group { display: flex; flex-direction: column; gap: 4px; }
    .compact { gap: 12px; }
    .compact svg { height: 16px; width: 16px; }
    .compact .plc-action, .plc-status {
      align-items: center; box-sizing: border-box; display: flex; justify-content: center;
      background: #fff; border: 1px solid #aebccd; border-radius: 3px; height: 26px; width: 26px; padding: 0;
    }
    .plc-status.ok, .plc-status.matches { color: #2f7d20; }
    .plc-status.error { color: #b91c1c; }
    .plc-status.modified { color: #b45309; }
    .plc-status.unknown { color: #64748b; }
  `];

  render() {
    const status = devicePlcStatus(this.state);
    if(this.compact) return this.#renderCompact(status);
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

  #renderCompact(status) {
    const kind = this.modified === true ? 'modified' : this.modified === false ? 'matches' : 'unknown';
    const comparison = kind === 'modified' ? 'Diagram changed or invalid. Build to compare with the last acknowledged upload.'
      : kind === 'matches' ? 'Matches the last program whose upload was acknowledged by the device.'
      : 'Comparison unknown. Build to compare with the last acknowledged upload.';
    const runtime = `PLC: ${status.label}${status.via ? ` (${status.via})` : ''}`;
    return html`
      <div class="compact">
        <div class="compact-group" role="group" aria-label="PLC status">
          <span class="plc-status ${status.kind}" role="status" title=${runtime} aria-label=${runtime}>${plcIcon(status.label)}</span>
          <span class="plc-status ${kind}" role="status" title=${comparison} aria-label=${comparison}>${plcIcon(kind)}</span>
        </div>
        <div class="compact-group" role="group" aria-label="PLC actions">
          ${PLC_ACTIONS.map(([label, cmd]) => html`
            <button type="button" class="plc-action" title=${label === 'Run' ? 'Run: build, upload and start' : label}
              aria-label=${label} @click=${(e) => this.#onAction(e, cmd)}>${plcIcon(label)}</button>`)}
        </div>
      </div>`;
  }

  #onAction(e, cmd) {
    e.preventDefault();
    e.stopPropagation();
    this.rpc(cmd);
  }
}

function plcIcon(name) {
  const paths = {
    Build: 'M4 21 14 11 M12 3 9 6 18 15 21 12 Z M4 18 7 21',
    Run: 'M4 16v5h12v-5 M10 16V3 M6 7l4-4 4 4 M18 4l4 4-4 4z',
    Start: 'M7 4 20 12 7 20 Z',
    Stop: 'M5 5H19V19H5Z',
    run: 'M7 4 20 12 7 20 Z',
    stop: 'M5 5H19V19H5Z',
    matches: 'M4 12l5 5L20 6',
    modified: 'M3 8h18 M3 16h18 M16 3 8 21',
    unknown: 'M8 8a4 4 0 1 1 6 3.5c-2 1-2 2-2 3.5 M12 19v1',
  };
  return html`<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2"
    stroke-linecap="round" stroke-linejoin="round"><path d=${paths[name] || paths.unknown}></path></svg>`;
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
customElements.define('x13-device-plc-editor', X13DevicePlcEditor);
