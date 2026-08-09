import { LitElement, html, css } from '../lib/lit-all.min.js';
import '../ide_editors/editor-host.js';

export class X13ViewRow extends LitElement {
  static properties = {
    row: { attribute: false },
    rowIndex: { type: Number },
    nameWidth: { type: Number },
    editingName: { type: Boolean },
    editNameValue: {},
  };

  static styles = css`
    .row {
      align-items: stretch;
      box-sizing: border-box;
      display: grid;
      grid-template-columns: var(--name-width) minmax(80px, 1fr);
      min-height: 24px;
      white-space: nowrap;
    }
    .row.odd { --data-bg: #F5FBFF; }
    .row.even { --data-bg: #D6F0FF; }
    .name-cell {
      align-items: stretch;
      background: var(--data-bg);
      box-sizing: border-box;
      display: flex;
      min-width: 0;
      overflow: hidden;
    }
    .expander {
      align-items: center;
      background: #fff;
      border-right: 1px solid #000;
      box-sizing: border-box;
      display: flex;
      flex-shrink: 0;
      height: 100%;
      padding-left: var(--indent-width);
    }
    .twisty {
      background: transparent;
      border: 0;
      color: #3f4c59;
      cursor: pointer;
      font-size: 20px;
      height: 24px;
      padding: 0;
      width: 18px;
    }
    .twisty:disabled { cursor: default; }
    .icon-cell {
      align-items: center;
      display: flex;
      flex-shrink: 0;
      justify-content: center;
      width: 18px;
    }
    .icon { flex: 0 0 auto; height: 16px; width: 16px; }
    .name {
      align-items: center;
      display: flex;
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
      padding-left: 4px;
    }
    .name-text {
      font-weight: 500;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .name-input {
      background: #fff;
      border: 1px solid #2563eb;
      box-sizing: border-box;
      font: inherit;
      height: 20px;
      min-width: 0;
      outline: none;
      padding: 0 3px;
      width: 100%;
    }
    .value {
      align-items: center;
      background: var(--data-bg);
      color: #475569;
      display: flex;
      min-width: 0;
      overflow: hidden;
      padding-left: 0;
    }
    .value x13-editor-host {
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
    }
  `;

  constructor() {
    super();
    this.rowIndex = 0;
    this.nameWidth = 180;
    this.editingName = false;
    this.editNameValue = undefined;
    this.suppressNextBlur = false;
  }

  updated(changed) {
    if(changed.has('editingName') && this.editingName) this.updateComplete.then(() => this.#focusNameInput());
  }

  render() {
    const row = this.row || {};
    const icon = row.icon || '/ide_icons/ty_topic.png';
    const indentWidth = Math.max(0, Number(row.level) || 0) * 7;
    return html`
      <div class="row ${this.rowIndex % 2 ? 'odd' : 'even'}" style="--indent-width:${indentWidth}px;--name-width:${this.nameWidth}px" @contextmenu=${this.#openMenu}>
        <span class="name-cell">
          <span class="expander">
            <button class="twisty" ?disabled=${!row.expander} @click=${this.#toggle}>
              ${row.expander ? (row.expander === 2 ? '▾' : '▸') : ''}
            </button>
          </span>
          <span class="icon-cell"><img class="icon" src=${icon} alt="" @error=${this.#iconError}></span>
          <span class="name">
            ${this.editingName ? html`
              <input class="name-input" .value=${this.editNameValue ?? row.name ?? ''} @keydown=${this.#nameKeyDown} @blur=${this.#nameBlur}>
            ` : html`
              <span class="name-text" title=${row.name || ''}>${row.name || ''}</span>
            `}
          </span>
        </span>
        <span class="value"><x13-editor-host view="row" .row=${row}></x13-editor-host></span>
      </div>`;
  }

  #toggle() {
    if(!this.row?.expander) return;
    this.dispatchEvent(new CustomEvent('toggle-row', {
      bubbles: true,
      composed: true,
      detail: { row: this.row, expand: this.row.expander !== 2 },
    }));
  }

  #openMenu(e) {
    if(!this.row?.vid) return;
    e.preventDefault();
    this.dispatchEvent(new CustomEvent('row-menu', {
      bubbles: true,
      composed: true,
      detail: { row: this.row, x: e.clientX, y: e.clientY },
    }));
  }

  #focusNameInput() {
    const input = this.renderRoot.querySelector('.name-input');
    if(!input) return;
    input.focus();
    input.select();
  }

  #nameKeyDown(e) {
    e.stopPropagation();
    if(e.key === 'Enter') {
      e.preventDefault();
      this.suppressNextBlur = true;
      this.#commitName(e.currentTarget.value);
    } else if(e.key === 'Escape') {
      e.preventDefault();
      this.suppressNextBlur = true;
      this.dispatchEvent(new CustomEvent('rename-cancel', { bubbles: true, composed: true, detail: { row: this.row } }));
    }
  }

  #nameBlur(e) {
    if(this.suppressNextBlur) {
      this.suppressNextBlur = false;
      return;
    }
    this.#commitName(e.currentTarget.value);
  }

  #commitName(value) {
    this.dispatchEvent(new CustomEvent('rename-row', {
      bubbles: true,
      composed: true,
      detail: { row: this.row, name: String(value || '').trim() },
    }));
  }

  #iconError(e) {
    if(e.currentTarget.src.endsWith('/ide_icons/ty_topic.png')) return;
    e.currentTarget.src = '/ide_icons/ty_topic.png';
  }
}

customElements.define('x13-view-row', X13ViewRow);
