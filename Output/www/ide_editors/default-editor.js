import { LitElement, html, css } from '../lib/lit-all.min.js';
import { formatDefault } from './editor-format.js';

export class X13DefaultEditor extends LitElement {
  static properties = {
    view: {},
    readonly: { type: Boolean },
    selected: { type: Boolean, reflect: true },
    state: { attribute: false },
    displayValue: {},
  };

  static styles = css`
    :host { display: inline; color: #334155; }
    .empty { color: #94a3b8; }
    span { border-radius: 0; }
    :host([selected]) span { background: #fff; }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = undefined;
    this.displayValue = undefined;
  }

  render() {
    const value = this.displayValue ?? formatDefault(this.state);
    return html`<span class=${value === '' ? 'empty' : ''}>${value}</span>`;
  }
}

customElements.define('x13-default-editor', X13DefaultEditor);
