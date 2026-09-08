import { LitElement, html, css } from '../../lib/lit-all.min.js';

export class X13EnumEditor extends LitElement {
  static properties = {
    view: {},
    readonly: { type: Boolean, reflect: true },
    selected: { type: Boolean, reflect: true },
    state: { attribute: false },
    dependencies: { attribute: false },
    enumSource: { attribute: false },
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
    this.enumSource = undefined;
    this.#enumResult = null;
    this.#dependencyHandle = null;
    this.#dependencyToken = 0;
  }

  disconnectedCallback() {
    this.#dependencyHandle?.dispose();
    this.#dependencyHandle = null;
    super.disconnectedCallback();
  }

  willUpdate(changed) {
    if(changed.has('enumSource') || changed.has('dependencies')) this.#loadOptions();
  }

  updated() {
    this.#syncSelectValue();
  }

  render() {
    const items = this.#displayItems();
    return html`<select
      .value=${valueKey(this.state)}
      style=${this.#selectedStyle()}
      ?disabled=${this.readonly || !this.#enumResult?.ok}
      @change=${this.#onChange}>
      ${items.map(item => html`<option
        class=${item.unknown ? 'unknown' : ''}
        .value=${valueKey(item.value)}
        style=${optionStyle(item)}
        ?disabled=${item.disabled}
        title=${item.title || ''}>${item.label}</option>`)}
    </select>`;
  }

  #loadOptions() {
    this.#dependencyHandle?.dispose();
    this.#dependencyHandle = null;
    this.#enumResult = null;
    const dependencies = this.dependencies;
    if(!dependencies || typeof dependencies.loadEnum !== 'function') return;
    const token = ++this.#dependencyToken;
    this.#dependencyHandle = dependencies.loadEnum(this.enumSource, this, (result) => {
      if(token !== this.#dependencyToken) return;
      this.#enumResult = result;
      this.requestUpdate();
    });
  }

  #displayItems() {
    const items = Array.isArray(this.#enumResult?.items) ? [...this.#enumResult.items] : [];
    if(!this.#enumResult) return [{ value: this.state, label: 'loading...', disabled: true }];
    if(!this.#enumResult.ok && items.length === 0) return [{ value: this.state, label: this.state === undefined ? '' : String(this.state), disabled: true, unknown: true }];
    if(!items.some(item => valuesEqual(item.value, this.state))) {
      items.unshift({ value: this.state, label: this.state === undefined ? '' : String(this.state), unknown: true });
    }
    return items;
  }

  #syncSelectValue() {
    const select = this.renderRoot?.querySelector('select');
    if(select) select.value = valueKey(this.state);
  }

  #selectedStyle() {
    const selected = this.#displayItems().find(item => valuesEqual(item.value, this.state));
    return optionStyle(selected);
  }

  #onChange(e) {
    if(this.readonly || !this.#enumResult?.ok) return;
    const selected = this.#displayItems().find(item => valueKey(item.value) === e.currentTarget.value);
    if(!selected || selected.disabled || selected.unknown) {
      this.requestUpdate();
      return;
    }
    if(valuesEqual(selected.value, this.state)) return;
    this.commit(selected.value);
  }

  #enumResult;
  #dependencyHandle;
  #dependencyToken;
}

function optionStyle(item) {
  if(!item?.background) return '';
  return `background:${cssValue(item.background)};${foregroundStyle(item.background)}`;
}

function foregroundStyle(background) {
  const normalized = String(background || '').trim().toLowerCase();
  if(normalized === 'black' || normalized === 'navy' || normalized === 'darkblue' || normalized === 'darkgreen' || normalized === 'darkred') return 'color:white;';
  return '';
}

function cssValue(value) {
  return String(value).replace(/[;{}]/g, '');
}

function valueKey(value) {
  return `${typeof value}:${JSON.stringify(value)}`;
}

function valuesEqual(left, right) {
  if(left === right) return true;
  if(typeof left === 'number' && typeof right === 'number') return Object.is(left, right);
  return false;
}

customElements.define('x13-enum-editor', X13EnumEditor);
