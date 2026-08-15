import { LitElement, html, css } from '../lib/lit-all.min.js';
import '../ide_editors/editor-host.js';
import { getView } from '../ide_services/vid-helper.js';

// Rows from the Inspector's State/Manifest trees get the editor host's "value" view
// (multi-line-capable String/JS editors that auto-grow up to 70vh - see
// scalar-editors.js/js-editor.js) instead of the compact single-line "row" view every
// other tree (Workspace, Inspector's Children, Catalog) uses by default. A row can
// also opt in individually via row.editorView (set server-side, e.g.
// InspectorChildrenViewProvider gives a DevicePLC document's "src" child topic
// "value" even though it's a Children row) - that always wins over the vid-based default.
const VALUE_VIEW_PREFIXES = new Set(['inspstate', 'inspmanifest']);

function resolveEditorView(row) {
  if(row.editorView === 'value' || row.editorView === 'row') return row.editorView;
  return VALUE_VIEW_PREFIXES.has(getView(row.vid)) ? 'value' : 'row';
}

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
      cursor: pointer;
      display: grid;
      grid-template-columns: var(--name-width) minmax(80px, 1fr);
      min-height: 24px;
      position: relative;
      white-space: nowrap;
    }
    .row.odd { --data-bg: #F5FBFF; }
    .row.even { --data-bg: #D6F0FF; }
    .row.root { border-top: 3px solid #91B8C6; }
    /* "Show in Workspace" (view-workspace.js #tryScrollToPendingReveal) toggles this
       class on the host directly (imperative DOM, not a reactive prop - purely a
       transient visual ping, not worth a Lit property/re-render for). ::after paints
       over name-cell/value's own opaque backgrounds instead of under them. */
    :host(.reveal-flash) .row::after {
      animation: reveal-flash 0.6s ease-out 2;
      background: rgba(251, 191, 36, 0.45);
      content: '';
      inset: 0;
      pointer-events: none;
      position: absolute;
    }
    @keyframes reveal-flash {
      0%, 100% { opacity: 0; }
      50% { opacity: 1; }
    }
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
      cursor: text;
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
    .value.value-growable {
      align-items: flex-start;
      overflow: visible;
    }
    .value x13-editor-host {
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
    }
    .value-growable x13-editor-host {
      overflow: visible;
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
    const isRoot = (Number(row.level) || 0) === 0;
    const editorView = resolveEditorView(row);
    return html`
      <div class="row ${this.rowIndex % 2 ? 'odd' : 'even'} ${isRoot ? 'root' : ''}" style="--indent-width:${indentWidth}px;--name-width:${this.nameWidth}px"
        @contextmenu=${this.#openMenu}>
        <span class="name-cell" @dblclick=${this.#onNameDoubleClick}>
          <span class="expander">
            <button class="twisty" ?disabled=${!row.expander} @click=${this.#toggle}>
              ${row.expander ? (row.expander === 2 ? '▾' : '▸') : ''}
            </button>
          </span>
          <span class="icon-cell"><img class="icon" src=${icon} alt="" @error=${this.#iconError}></span>
          <span class="name" draggable=${!!(row.vid && !this.editingName)} @dragstart=${this.#onDragStart}>
            ${this.editingName ? html`
              <input class="name-input" .value=${this.editNameValue ?? row.name ?? ''} @keydown=${this.#nameKeyDown} @blur=${this.#nameBlur}>
            ` : html`
              <span class="name-text" title=${row.name || ''}>${row.name || ''}</span>
            `}
          </span>
        </span>
        <span class="value ${editorView === 'value' ? 'value-growable' : ''}"><x13-editor-host view=${editorView} .row=${row}></x13-editor-host></span>
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

  // Generic drag source for every tree this row is used in (Workspace, Inspector
  // Children, Catalog) - currently only Logram's canvas listens for a drop (see
  // logram-document.js #onSurfaceDrop, drag-a-topic-in creates a bound variable,
  // mirroring ES/Logram/LogramView.cs LogramView_Drop's DTopic branch), so a drop
  // anywhere else is simply a no-op. The vid (not a bare topic path) lets the drop
  // target resolve it exactly like any other vid it already handles (see
  // VidHelper.GetTopicPath), regardless of which tree it was dragged from.
  #onDragStart(e) {
    if(!this.row?.vid) return;
    e.dataTransfer.setData('application/x13-vid', this.row.vid);
    e.dataTransfer.effectAllowed = 'link';
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

  #onNameDoubleClick(e) {
    if(e.target.closest('.expander')) return;
    if(this.editingName || !this.row?.vid) return;
    this.dispatchEvent(new CustomEvent('row-open', {
      bubbles: true,
      composed: true,
      detail: { row: this.row },
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
