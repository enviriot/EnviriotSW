import { LitElement, html, css } from '../lib/lit-all.min.js';

const FLAGS = Object.freeze([
  { bit: 1, label: 'Req', title: 'Required' },
  { bit: 2, label: 'RO', title: 'Readonly' },
  { bit: 4, label: 'DB', title: 'Save in DB' },
  { bit: 8, label: 'Cfg', title: 'Save in Config' },
  { bit: 64, label: 'Int', title: 'Internal Topic' },
]);

const DB_MASK = 4;
const CONFIG_MASK = 8;
const DB_CONFIG_MASK = DB_MASK | CONFIG_MASK;

export class X13AttributeEditor extends LitElement {
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
    .flags {
      display: inline-flex;
      max-width: 100%;
      overflow: hidden;
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
      font: inherit;
      gap: 4px;
      height: 24px;
      line-height: 22px;
      margin: 0 3px;
      min-width: 44px;
      outline: 0;
      overflow: hidden;
      padding: 0 6px;
      text-align: center;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    :host(:not([readonly])) button {
      border-left: 1px solid #000;
      border-right: 1px solid #000;
    }
    button:disabled { cursor: default; }
    .dot { border-radius: 50%; flex: 0 0 auto; height: 12px; width: 12px; }
    .dot.true { background: var(--bool_true); }
    .dot.false { background: var(--bool_false); }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = 0;
  }

  render() {
    const attr = attributeValue(this.state);
    return html`
      <span class="flags">
        ${FLAGS.map(flag => html`
          <button
            type="button"
            title=${flag.title}
            aria-pressed=${String(flagChecked(attr, flag.bit))}
            ?disabled=${this.readonly}
            @click=${() => this.#toggle(flag.bit)}>
            <span class="dot ${flagChecked(attr, flag.bit) ? 'true' : 'false'}"></span>${flag.label}</button>`)}
      </span>`;
  }

  #toggle(bit) {
    if(this.readonly) return;
    const current = attributeValue(this.state);
    const next = toggleAttributeFlag(current, bit);
    if(next !== current) this.commit(next);
  }
}

export function attributeValue(value) {
  if(typeof value !== 'number' || !Number.isFinite(value)) return 0;
  return Math.trunc(value);
}

export function flagChecked(attr, bit) {
  if(bit === DB_MASK) return (attr & DB_CONFIG_MASK) === DB_MASK;
  return (attr & bit) !== 0;
}

export function toggleAttributeFlag(attr, bit) {
  const current = attributeValue(attr);
  if(bit === DB_MASK) {
    return flagChecked(current, DB_MASK)
      ? (current & ~DB_MASK)
      : ((current & ~CONFIG_MASK) | DB_MASK);
  }
  if(bit === CONFIG_MASK) {
    return flagChecked(current, CONFIG_MASK)
      ? (current & ~CONFIG_MASK)
      : ((current & ~DB_MASK) | CONFIG_MASK);
  }
  return (current & bit) !== 0 ? (current & ~bit) : (current | bit);
}

customElements.define('x13-attribute-editor', X13AttributeEditor);
