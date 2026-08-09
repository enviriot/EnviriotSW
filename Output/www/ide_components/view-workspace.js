import { LitElement, html, css, repeat } from '../lib/lit-all.min.js';
import './view-row.js';
import './context-menu.js';
import { clamp, readPositiveNumber } from '../ide_services/local-storage-utils.js';
import { getTopicPath, isDescendant } from '../ide_services/vid-helper.js';

const NAME_WIDTH_KEY = 'x13.workspace.nameWidth';
const EXPANDED_KEY = 'x13.workspace.expanded';
const DEFAULT_NAME_WIDTH = 180;
const MIN_NAME_WIDTH = 110;
const MIN_VALUE_WIDTH = 110;
const CUT_CLIPBOARD_PREFIX = 'x13-webui-topic-cut:';

export class X13ViewWorkspace extends LitElement {
  static properties = {
    api: { attribute: false },
    store: { attribute: false },
    rows: { attribute: false },
    nameWidth: { type: Number },
    menuState: { attribute: false },
    renameState: { attribute: false },
    willfulAddState: { attribute: false },
  };

  static styles = css`
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

  constructor() {
    super();
    this.rows = [];
    this.nameWidth = readPositiveNumber(NAME_WIDTH_KEY, DEFAULT_NAME_WIDTH);
    this.rememberedExpanded = readExpandedVids();
    this.activeExpanded = new Set();
    this.unsubscribe = null;
    this.menuState = null;
    this.renameState = null;
    this.willfulAddState = null;
  }

  updated(changed) {
    if(changed.has('store')) this.#subscribeStore();
  }

  disconnectedCallback() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.menuState = null;
    this.renameState = null;
    this.willfulAddState = null;
    super.disconnectedCallback();
  }

  render() {
    return html`
      <div class="tree" style="--name-width:${this.nameWidth}px">
        <div class="header">
          <span class="name-head">Name<span class="name-resizer" @pointerdown=${this.#startNameResize}></span></span>
          <span class="value-head">Value</span>
        </div>
        ${repeat(this.rows, (row) => row.vid, (row, index) => html`
          <x13-view-row
            .row=${row}
            .rowIndex=${index}
            .nameWidth=${this.nameWidth}
            .editingName=${this.#isNameEditing(row)}
            .editNameValue=${this.#nameEditInitialValue(row)}
            @toggle-row=${this.#toggle}
            @row-menu=${this.#openMenu}
            @rename-row=${this.#renameRow}
            @rename-cancel=${this.#cancelRename}>
          </x13-view-row>`)}
        ${this.menuState ? html`
          <x13-context-menu
            .items=${this.menuState.items}
            .x=${this.menuState.x}
            .y=${this.menuState.y}
            @menu-command=${this.#menuCommand}
            @menu-close=${this.#closeMenu}>
          </x13-context-menu>` : html``}
      </div>`;
  }

  #subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.menuState = null;
    this.renameState = null;
    this.willfulAddState = null;
    this.rows = [];
    this.activeExpanded.clear();
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((rows, reset) => {
      this.rows = rows;
      if(reset) this.activeExpanded.clear();
      this.#restoreRememberedExpanded(rows);
    });
  }

  #toggle(e) {
    const row = e.detail?.row;
    if(!row || !this.api) return;
    const expand = !!e.detail.expand;
    if(expand) {
      this.rememberedExpanded.add(row.vid);
      this.activeExpanded.add(row.vid);
    } else {
      this.rememberedExpanded.delete(row.vid);
      this.#forgetActiveSubtree(row.vid);
    }
    writeExpandedVids(this.rememberedExpanded);
    this.api.expand(row.vid, expand).catch((error) => console.warn('req.expand failed', row.vid, expand, error));
  }

  #restoreRememberedExpanded(rows) {
    if(!this.api) return;
    for(const row of rows) {
      if(!row?.vid || !row.expander || row.expander === 2) continue;
      if(!this.rememberedExpanded.has(row.vid) || this.activeExpanded.has(row.vid)) continue;
      this.activeExpanded.add(row.vid);
      this.api.expand(row.vid, true).catch((error) => {
        this.activeExpanded.delete(row.vid);
        console.warn('restore req.expand failed', row.vid, error);
      });
    }
  }

  #forgetActiveSubtree(vid) {
    for(const activeVid of [...this.activeExpanded]) {
      if(activeVid === vid || isDescendant(vid, activeVid)) this.activeExpanded.delete(activeVid);
    }
  }

  async #openMenu(e) {
    const row = e.detail?.row;
    if(!row?.vid || !this.api) return;
    e.preventDefault?.();
    try {
      const response = await this.api.menu(row.vid);
      const cutPayload = await this.#readCutPayload();
      this.menuState = {
        row,
        items: this.#withPasteState(Array.isArray(response.items) ? response.items : [], cutPayload),
        cutPayload,
        x: Number(e.detail.x) || 0,
        y: Number(e.detail.y) || 0,
      };
    }
    catch(error) {
      console.warn('req.menu failed', row.vid, error);
      this.menuState = null;
    }
  }

  async #menuCommand(e) {
    const cmd = e.detail?.cmd;
    const row = this.menuState?.row;
    const willful = !!e.detail?.willful;
    const cutPayload = this.menuState?.cutPayload || null;
    this.#closeMenu();
    if(!cmd || !row?.vid) return;

    if(cmd === 'copy-path') {
      await this.#writeClipboard(getTopicPath(row.vid));
      return;
    }
    if(cmd === 'cut') {
      await this.#writeClipboard(this.#cutClipboardText(row));
      return;
    }
    if(cmd === 'paste') {
      if(!this.api || !cutPayload?.sourceVid || !cutPayload?.sourcePath) return;
      try {
        await this.api.rpc(row.vid, 'paste', {
          sourceVid: cutPayload.sourceVid,
          sourcePath: cutPayload.sourcePath,
        });
        await this.#writeClipboard('');
      }
      catch(error) {
        console.warn('req.rpc paste failed', row.vid, cutPayload, error);
      }
      return;
    }
    if(cmd === 'import') {
      await this.#importXst();
      return;
    }
    if(cmd === 'export') {
      this.#exportXst(row);
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
    if(cmd === 'open' || cmd === 'open-tab' || cmd === 'catalog') {
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
  }

  async #renameRow(e) {
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
  }

  #cancelRename() {
    this.renameState = null;
    this.willfulAddState = null;
  }

  #isNameEditing(row) {
    return !!row?.vid && (this.renameState?.vid === row.vid || this.willfulAddState?.vid === row.vid);
  }

  #nameEditInitialValue(row) {
    if(!row?.vid) return undefined;
    if(this.willfulAddState?.vid === row.vid) return '';
    if(this.renameState?.vid === row.vid) return row.name || '';
    return undefined;
  }

  #closeMenu() {
    this.menuState = null;
  }

  async #writeClipboard(text) {
    try {
      await navigator.clipboard.writeText(text || '');
    }
    catch(error) {
      console.warn('clipboard write failed', error);
    }
  }

  async #readCutPayload() {
    try {
      return parseCutClipboardText(await navigator.clipboard.readText());
    }
    catch(error) {
      console.warn('clipboard read failed', error);
      return null;
    }
  }

  #cutClipboardText(row) {
    const path = getTopicPath(row?.vid);
    return `${CUT_CLIPBOARD_PREFIX}${JSON.stringify({
      vid: row?.vid || '',
      path,
      name: row?.name || '',
    })}`;
  }

  #withPasteState(items, cutPayload) {
    return items.map((item) => cloneMenuItemWithPasteState(item, !!cutPayload));
  }

  async #importXst() {
    const file = await pickXstFile();
    if(!file) return;
    const form = new FormData();
    form.append('file', file, file.name);
    try {
      const response = await fetch('/api/import', { method: 'POST', body: form });
      const text = await response.text();
      if(!response.ok) throw new Error(text || `HTTP ${response.status}`);
    }
    catch(error) {
      console.warn('HTTP import failed', file.name, error);
    }
  }

  #exportXst(row) {
    const path = getTopicPath(row?.vid) || '/';
    const link = document.createElement('a');
    link.href = `/api/export${encodeExportPath(path)}`;
    link.download = `${safeExportFileName(row?.name || 'root')}.xst`;
    link.click();
  }

  #startNameResize(e) {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = this.nameWidth;
    const move = (ev) => {
      this.nameWidth = clamp(startWidth + ev.clientX - startX, MIN_NAME_WIDTH, maxNameWidthFor(this));
    };
    const up = () => {
      localStorage.setItem(NAME_WIDTH_KEY, String(this.nameWidth));
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
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

function cloneMenuItemWithPasteState(item, pasteEnabled) {
  const copy = { ...(item || {}) };
  if(Array.isArray(copy.children)) {
    copy.children = copy.children.map((child) => cloneMenuItemWithPasteState(child, pasteEnabled));
  }
  if(copy.cmd === 'paste') copy.enabled = pasteEnabled;
  return copy;
}

function parseCutClipboardText(text) {
  if(!String(text || '').startsWith(CUT_CLIPBOARD_PREFIX)) return null;
  try {
    const payload = JSON.parse(String(text).slice(CUT_CLIPBOARD_PREFIX.length));
    if(!payload || typeof payload !== 'object') return null;
    if(typeof payload.vid !== 'string' || typeof payload.path !== 'string') return null;
    if(!payload.vid.startsWith('workspace#') || !payload.path.startsWith('/')) return null;
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

function readExpandedVids() {
  try {
    const parsed = JSON.parse(localStorage.getItem(EXPANDED_KEY) || '[]');
    return new Set(Array.isArray(parsed) ? parsed.filter((item) => typeof item === 'string').map(normalizeExpandedVid) : []);
  }
  catch {
    return new Set();
  }
}

function writeExpandedVids(values) {
  localStorage.setItem(EXPANDED_KEY, JSON.stringify([...values].sort()));
}

function normalizeExpandedVid(value) {
  if(value.startsWith('workspace#')) return value;
  if(value.startsWith('/')) return `workspace#${value}`;
  return value;
}

function maxNameWidthFor(treeElement) {
  const treeWidth = treeElement.renderRoot.querySelector('.tree')?.clientWidth || treeElement.clientWidth;
  return Math.max(MIN_NAME_WIDTH, treeWidth - MIN_VALUE_WIDTH);
}

customElements.define('x13-view-workspace', X13ViewWorkspace);
