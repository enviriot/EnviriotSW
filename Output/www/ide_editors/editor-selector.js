import { LitElement, html, css } from '../lib/lit-all.min.js';

export class X13EditorSelector extends LitElement {
  static properties = {
    view: {},
    readonly: { type: Boolean, reflect: true },
    selected: { type: Boolean, reflect: true },
    state: { attribute: false },
    dependencies: { attribute: false },
  };

  static styles = css`
    :host {
      display: inline-flex;
      max-width: 100%;
      vertical-align: middle;
    }
    select {
      background: transparent;
      border: 0;
      border-radius: 0;
      box-sizing: border-box;
      color: #1f2933;
      font: inherit;
      height: 24px;
      line-height: 22px;
      max-width: 100%;
      min-width: 102px;
      outline: 0;
      padding: 0 4px;
    }
    :host(:not([readonly])) select {
      border-left: 1px solid #000;
      border-right: 1px solid #000;
    }
    :host([selected]) select { background: #fff; }
    select:disabled { color: #475569; }
    option.unknown { color: #64748b; }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = undefined;
    this.dependencies = undefined;
    this.#editorNames = [];
    this.#dependencyHandle = null;
    this.#dependencyToken = 0;
  }

  disconnectedCallback() {
    this.#dependencyHandle?.dispose();
    this.#dependencyHandle = null;
    super.disconnectedCallback();
  }

  willUpdate(changed) {
    if(changed.has('dependencies')) this.#loadEditorNames();
  }

  updated() {
    this.#syncSelectValue();
  }

  render() {
    const names = normalizedEditorNames(this.#editorNames, this.state);
    return html`<select
      .value=${stringValue(this.state)}
      ?disabled=${this.readonly || names.length === 0}
      @change=${this.#onChange}>
      ${names.map((name) => html`<option
        class=${name === this.state || this.#editorNames.includes(name) ? '' : 'unknown'}
        .value=${name}>${name || '(default)'}</option>`)}
    </select>`;
  }

  #loadEditorNames() {
    this.#dependencyHandle?.dispose();
    this.#dependencyHandle = null;
    const dependencies = this.dependencies;
    if(!dependencies || typeof dependencies.loadEditorNames !== 'function') return;
    const token = ++this.#dependencyToken;
    this.#dependencyHandle = dependencies.loadEditorNames(this, (names) => {
      if(token !== this.#dependencyToken) return;
      this.#editorNames = Array.isArray(names) ? names : [];
      this.requestUpdate();
    });
  }

  #syncSelectValue() {
    const select = this.renderRoot?.querySelector('select');
    if(select) select.value = stringValue(this.state);
  }

  #onChange(e) {
    if(this.readonly) return;
    const next = e.currentTarget.value;
    if(next === stringValue(this.state)) return;
    this.commit(next);
    this.requestUpdate();
  }

  #editorNames;
  #dependencyHandle;
  #dependencyToken;
}

export function normalizedEditorNames(editorNames, current) {
  const names = Array.isArray(editorNames) ? editorNames.filter((name) => typeof name === 'string') : [];
  const set = new Set(names);
  const value = stringValue(current);
  if(value && !set.has(value)) set.add(value);
  return [...set].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'variant' }));
}

function stringValue(value) {
  return typeof value === 'string' ? value : '';
}

customElements.define('x13-editor-selector', X13EditorSelector);
