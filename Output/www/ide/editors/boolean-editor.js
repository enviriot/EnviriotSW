import { LitElement, html, css } from '../../lib/lit-all.min.js';

export class X13BooleanEditor extends LitElement {
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
    button {
      align-items: center;
      background: transparent;
      border: 0;
      border-radius: 0;
      box-sizing: border-box;
      color: #475569;
      cursor: pointer;
      display: inline-flex;
      gap: 6px;
      justify-content: left;
      text-align: center;
      font: inherit;
      line-height: 22px;
      margin: 0;
      width: 5em;
      outline: 0;
      height: 24px;
      padding: 0 7px;
    }
    :host(:not([readonly])) button {
      border-left: 1px solid #000;
      border-right: 1px solid #000;
    }
    button:disabled { cursor: default; }
    :host([selected]) button { background: #fff; }
    .dot { border-radius: 50%; flex: 0 0 auto; height: 12px; width: 12px; }
    .dot.true { background: var(--bool_true); }
    .dot.false { background: var(--bool_false); }
    .text { flex: 0 1 auto; overflow: hidden; text-align: center; text-overflow: ellipsis; }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = false;
  }

  render() {
    const value = this.state === true;
    const label = value ? 'true' : 'false';
    return html`
      <button
        role="switch"
        aria-checked=${String(value)}
        ?disabled=${this.readonly}
        @click=${this.#toggle}
        @keydown=${this.#onKeyDown}>
        <span class="dot ${value ? 'true' : 'false'}"></span>
        <span class="text">${label}</span>
      </button>`;
  }

  #toggle() {
    if(this.readonly) return;
    this.commit(!(this.state === true));
  }

  #onKeyDown(e) {
    if(e.key !== 'Enter' && e.key !== ' ') return;
    e.preventDefault();
    this.#toggle();
  }
}

customElements.define('x13-boolean-editor', X13BooleanEditor);
