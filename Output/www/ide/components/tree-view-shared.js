import { LitElement, html, css, repeat } from '../../lib/lit-all.min.js';
import './view-row.js';
import './context-menu.js';
import { clamp, readPositiveNumber } from '../services/local-storage-utils.js';
import { getTopicPath } from '../services/vid-helper.js';
import { apiUrl } from '../services/api-token.js';

export const MIN_VALUE_WIDTH = 110;
export const CUT_CLIPBOARD_PREFIX = 'x13-webui-topic-cut:';

// Shared by every "tree document" (Workspace, Inspector's Children) - the header
// grid + name-column resizer chrome, identical across trees regardless of what's
// rooted where.
export const treeHeaderStyles = css`
  :host { display: block; height: 100%; }
  .tree {
    background: white;
    height: 100%;
    min-height: 300px;
    overflow: auto;
  }
  .header {
    align-items: center;
    background: #f3f6fa;
    border-bottom: 1px solid #cfd8e3;
    box-sizing: border-box;
    color: #556575;
    display: grid;
    font-size: 12px;
    grid-template-columns: var(--name-width) 1fr;
    height: 24px;
    z-index: 2;
  }
  .header .name-head {
    box-sizing: border-box;
    position: relative;
    text-align: center;
  }
  .name-resizer {
    background: linear-gradient(90deg, transparent 0, transparent 2px, #7b8da1 2px, #7b8da1 4px, transparent 4px);
    cursor: col-resize;
    height: 100%;
    position: absolute;
    right: -5px;
    top: 0;
    width: 10px;
    z-index: 3;
  }
  .value-head { text-align: center; }
`;

export function maxNameWidthFor(treeElement, minNameWidth = 110, minValueWidth = MIN_VALUE_WIDTH) {
  const treeWidth = treeElement.renderRoot.querySelector('.tree')?.clientWidth || treeElement.clientWidth;
  return Math.max(minNameWidth, treeWidth - minValueWidth);
}

export function cutClipboardText(row) {
  const path = getTopicPath(row?.vid);
  return `${CUT_CLIPBOARD_PREFIX}${JSON.stringify({
    vid: row?.vid || '',
    path,
    name: row?.name || '',
  })}`;
}

// Shape-only validation - deliberately NOT tied to any one view's vid prefix, so a
// topic cut from one tree (e.g. Workspace) can be pasted into another (e.g.
// Inspector's Children) and vice versa.
export function parseCutClipboardText(text) {
  if(!String(text || '').startsWith(CUT_CLIPBOARD_PREFIX)) return null;
  try {
    const payload = JSON.parse(String(text).slice(CUT_CLIPBOARD_PREFIX.length));
    if(!payload || typeof payload !== 'object') return null;
    if(typeof payload.vid !== 'string' || typeof payload.path !== 'string') return null;
    if(!payload.vid.includes('#') || !payload.path.startsWith('/')) return null;
    return {
      sourceVid: payload.vid,
      sourcePath: payload.path,
      name: typeof payload.name === 'string' ? payload.name : '',
    };
  }
  catch {
    return null;
  }
}

export function cloneMenuItemWithPasteState(item, pasteEnabled) {
  const copy = { ...(item || {}) };
  if(Array.isArray(copy.children)) {
    copy.children = copy.children.map((child) => cloneMenuItemWithPasteState(child, pasteEnabled));
  }
  if(copy.cmd === 'paste') copy.enabled = pasteEnabled;
  return copy;
}

// Import/export are plain HTTP actions against a topic path - not vid-view-specific,
// so they work identically for any tree row regardless of which tree (Workspace,
// Inspector's Children) it came from.
export function exportXst(row) {
  const path = getTopicPath(row?.vid) || '/';
  const link = document.createElement('a');
  link.href = apiUrl(`/api/export${encodeExportPath(path)}`);
  link.download = `${safeExportFileName(row?.name || 'root')}.xst`;
  link.click();
}

export async function importXst() {
  const file = await pickXstFile();
  if(!file) return;
  const form = new FormData();
  form.append('file', file, file.name);
  try {
    const response = await fetch(apiUrl('/api/import'), { method: 'POST', body: form });
    const text = await response.text();
    if(!response.ok) throw new Error(text || `HTTP ${response.status}`);
  }
  catch(error) {
    console.warn('HTTP import failed', file.name, error);
  }
}

function encodeExportPath(path) {
  const normalized = String(path || '/');
  return normalized.split('/').map((part, index) => index === 0 ? '' : encodeURIComponent(part)).join('/') || '/';
}

function safeExportFileName(name) {
  return String(name || 'topic').replace(/[\/:*?"<>|]+/g, '_');
}

function pickXstFile() {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.xst,.xml';
    input.addEventListener('change', () => resolve(input.files?.[0] || null), { once: true });
    input.click();
  });
}

// Behavior shared by every live-topic tree document: store subscription, the
// header/resizer/row/menu render fragments, row toggle/menu/rename plumbing,
// clipboard cut/paste, and import/export. Deliberately excludes expand-state
// persistence, which is Workspace-only (its expand state survives reloads;
// Inspector's Children tree intentionally does not) - subclasses opt into that via
// the _afterToggle / _onRowsChanged hooks below.
export class X13TreeDocumentBase extends LitElement {
  static properties = {
    api: { attribute: false },
    store: { attribute: false },
    rows: { attribute: false },
    nameWidth: { type: Number },
    menuState: { attribute: false },
    renameState: { attribute: false },
    willfulAddState: { attribute: false },
  };

  static styles = treeHeaderStyles;

  constructor({ nameWidthKey = null, defaultNameWidth = 180, minNameWidth = 110 } = {}) {
    super();
    this.rows = [];
    this._nameWidthKey = nameWidthKey;
    this._defaultNameWidth = defaultNameWidth;
    this._minNameWidth = minNameWidth;
    this.nameWidth = nameWidthKey ? readPositiveNumber(nameWidthKey, defaultNameWidth) : defaultNameWidth;
    this.unsubscribe = null;
    this.menuState = null;
    this.renameState = null;
    this.willfulAddState = null;
  }

  updated(changed) {
    if(changed.has('store')) this._subscribeStore();
  }

  disconnectedCallback() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.menuState = null;
    this.renameState = null;
    this.willfulAddState = null;
    super.disconnectedCallback();
  }

  _subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.menuState = null;
    this.renameState = null;
    this.willfulAddState = null;
    this.rows = [];
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((rows, reset) => {
      this.rows = rows;
      this._onRowsChanged?.(rows, reset);
    });
  }

  _renderTree() {
    return html`
      <div class="tree" style="--name-width:${this.nameWidth}px">
        ${this._renderTreeHeader()}
        ${this._renderRows()}
        ${this._renderMenu()}
      </div>`;
  }

  _renderTreeHeader() {
    return html`
      <div class="header">
        <span class="name-head">Name<span class="name-resizer" @pointerdown=${this._startNameResize}></span></span>
        <span class="value-head">Value</span>
      </div>`;
  }

  _renderRows() {
    return repeat(this.rows, (row) => row.vid, (row, index) => html`
      <x13-view-row
        .row=${row}
        .rowIndex=${index}
        .nameWidth=${this.nameWidth}
        .editingName=${this._isNameEditing(row)}
        .editNameValue=${this._nameEditInitialValue(row)}
        @toggle-row=${this._toggle}
        @row-menu=${this._openMenu}
        @row-open=${this._onRowOpen}
        @rename-row=${this._renameRow}
        @rename-cancel=${this._cancelRename}>
      </x13-view-row>`);
  }

  _renderMenu() {
    return this.menuState ? html`
      <x13-context-menu
        .items=${this.menuState.items}
        .x=${this.menuState.x}
        .y=${this.menuState.y}
        @menu-command=${this._menuCommand}
        @menu-close=${this._closeMenu}>
      </x13-context-menu>` : html``;
  }

  _toggle = (e) => {
    const row = e.detail?.row;
    if(!row || !this.api) return;
    const expand = !!e.detail.expand;
    this._afterToggle?.(row, expand);
    this.api.expand(row.vid, expand).catch((error) => console.warn('req.expand failed', row.vid, expand, error));
  };

  _openMenu = async (e) => {
    const row = e.detail?.row;
    if(!row?.vid || !this.api) return;
    e.preventDefault?.();
    try {
      const response = await this.api.menu(row.vid);
      const cutPayload = await this._readCutPayload();
      this.menuState = {
        row,
        items: this._withPasteState(Array.isArray(response.items) ? response.items : [], cutPayload),
        cutPayload,
        x: Number(e.detail.x) || 0,
        y: Number(e.detail.y) || 0,
      };
    }
    catch(error) {
      console.warn('req.menu failed', row.vid, error);
      this.menuState = null;
    }
  };

  _menuCommand = async (e) => {
    const cmd = e.detail?.cmd;
    const row = this.menuState?.row;
    const willful = !!e.detail?.willful;
    const cutPayload = this.menuState?.cutPayload || null;
    this._closeMenu();
    if(!cmd || !row?.vid) return;

    if(cmd === 'copy-path') {
      await this._writeClipboard(getTopicPath(row.vid));
      return;
    }
    if(cmd === 'cut') {
      await this._writeClipboard(cutClipboardText(row));
      return;
    }
    if(cmd === 'paste') {
      if(!this.api || !cutPayload?.sourceVid || !cutPayload?.sourcePath) return;
      try {
        await this.api.rpc(row.vid, 'paste', {
          sourceVid: cutPayload.sourceVid,
          sourcePath: cutPayload.sourcePath,
        });
        await this._writeClipboard('');
      }
      catch(error) {
        console.warn('req.rpc paste failed', row.vid, cutPayload, error);
      }
      return;
    }
    if(cmd === 'import') {
      await importXst();
      return;
    }
    if(cmd === 'export') {
      exportXst(row);
      return;
    }
    if(cmd === 'rename') {
      this.willfulAddState = null;
      this.renameState = { vid: row.vid, originalName: row.name || '' };
      return;
    }
    if(cmd.startsWith('add:') && willful) {
      this.renameState = null;
      this.willfulAddState = { vid: row.vid, cmd, item: e.detail.item };
      return;
    }
    if(cmd === 'open' || cmd === 'open-tab' || cmd === 'catalog' || cmd === 'chart') {
      this.dispatchEvent(new CustomEvent('workspace-menu-command', {
        bubbles: true,
        composed: true,
        detail: { row, cmd, willful, item: e.detail.item },
      }));
      return;
    }

    if(!this.api) return;
    try {
      await this.api.rpc(row.vid, cmd);
    }
    catch(error) {
      console.warn('req.rpc failed', row.vid, cmd, error);
    }
  };

  _renameRow = async (e) => {
    const row = e.detail?.row;
    const name = e.detail?.name;
    const willfulAddState = this.willfulAddState;
    this.renameState = null;
    this.willfulAddState = null;
    if(!row?.vid || !this.api || !name) return;

    if(willfulAddState?.vid === row.vid) {
      try {
        await this.api.rpc(row.vid, willfulAddState.cmd, { name });
      }
      catch(error) {
        console.warn('req.rpc willful add failed', row.vid, willfulAddState.cmd, name, error);
      }
      return;
    }

    if(name === row.name) return;
    try {
      await this.api.rpc(row.vid, 'rename', { name });
    }
    catch(error) {
      console.warn('req.rpc rename failed', row.vid, name, error);
    }
  };

  _cancelRename = () => {
    this.renameState = null;
    this.willfulAddState = null;
  };

  _isNameEditing(row) {
    return !!row?.vid && (this.renameState?.vid === row.vid || this.willfulAddState?.vid === row.vid);
  }

  _nameEditInitialValue(row) {
    if(!row?.vid) return undefined;
    if(this.willfulAddState?.vid === row.vid) return '';
    if(this.renameState?.vid === row.vid) return row.name || '';
    return undefined;
  }

  _closeMenu = () => {
    this.menuState = null;
  };

  _onRowOpen = (e) => {
    const row = e.detail?.row;
    if(!row?.vid) return;
    this.dispatchEvent(new CustomEvent('workspace-menu-command', {
      bubbles: true,
      composed: true,
      detail: { row, cmd: 'open', willful: false },
    }));
  };

  async _writeClipboard(text) {
    try {
      await navigator.clipboard.writeText(text || '');
    }
    catch(error) {
      console.warn('clipboard write failed', error);
    }
  }

  async _readCutPayload() {
    try {
      return parseCutClipboardText(await navigator.clipboard.readText());
    }
    catch(error) {
      console.warn('clipboard read failed', error);
      return null;
    }
  }

  _withPasteState(items, cutPayload) {
    return items.map((item) => cloneMenuItemWithPasteState(item, !!cutPayload));
  }

  _startNameResize = (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = this.nameWidth;
    const move = (ev) => {
      this.nameWidth = clamp(startWidth + ev.clientX - startX, this._minNameWidth, maxNameWidthFor(this, this._minNameWidth));
    };
    const up = () => {
      if(this._nameWidthKey) localStorage.setItem(this._nameWidthKey, String(this.nameWidth));
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  };
}
