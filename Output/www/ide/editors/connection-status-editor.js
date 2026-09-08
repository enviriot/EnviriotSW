import { LitElement, html, css } from '../../lib/lit-all.min.js';

const STATUS_LABELS = {
  connected: 'connected',
  connecting: 'connecting',
  reconnecting: 'reconnecting',
  error: 'error',
};

export class X13ConnectionStatusEditor extends LitElement {
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
    .status {
      align-items: center;
      display: inline-flex;
      gap: 6px;
      height: 24px;
      line-height: 22px;
      padding: 0 7px;
    }
    :host([selected]) .status { background: #fff; }
    .dot { border-radius: 50%; flex: 0 0 auto; height: 12px; width: 12px; }
    .dot.connected { background: var(--conn_connected); }
    .dot.connecting { background: var(--conn_connecting); }
    .dot.reconnecting { background: var(--conn_reconnecting); }
    .dot.error { background: var(--conn_error); }
    .dot.unknown { background: var(--conn_unknown); }
    .text { flex: 0 1 auto; overflow: hidden; text-align: center; text-overflow: ellipsis; }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = undefined;
  }

  render() {
    const kind = STATUS_LABELS[this.state] ? this.state : 'unknown';
    const label = STATUS_LABELS[this.state] || String(this.state ?? 'unknown');
    return html`
      <span class="status">
        <span class="dot ${kind}"></span>
        <span class="text">${label}</span>
      </span>`;
  }
}

customElements.define('x13-connection-status-editor', X13ConnectionStatusEditor);
