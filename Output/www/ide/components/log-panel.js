import { LitElement, html, css, repeat } from '../../lib/lit-all.min.js';
import { readBoolean } from '../services/local-storage-utils.js';
import { formatLogTime, logDateKey, formatLogDate } from '../services/log-store.js';

const SHOW_DEBUG_KEY = 'x13.logpanel.showDebug';

const LEVEL_INFO = {
  0: { label: 'Debug', color: '#9aa5b1' },
  1: { label: 'Info', color: '#2563eb' },
  2: { label: 'Warning', color: '#c77c11' },
  3: { label: 'Error', color: '#d64545' },
};

export class X13LogPanel extends LitElement {
  static properties = {
    api: { attribute: false },
    store: { attribute: false },
    collapsed: { type: Boolean },
    entries: { attribute: false },
    filterText: {},
    filterActive: { type: Boolean },
    showDebug: { type: Boolean },
    historyLoading: { type: Boolean },
    historyExhausted: { type: Boolean },
    historyError: {},
  };

  static styles = css`
    :host { box-sizing: border-box; display: flex; flex-direction: column; height: 100%; min-height: 0; overflow: hidden; }
    .topbar {
      align-items: center;
      background: #eef3f8;
      border-top: 1px solid #cfd8e3;
      box-sizing: border-box;
      display: flex;
      flex: 0 0 auto;
      gap: 8px;
      padding: 3px 8px;
    }
    .toggle {
      background: transparent;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #3f4c59;
      cursor: pointer;
      flex: 0 0 auto;
      font-size: 11px;
      line-height: 1;
      margin-left: auto;
      padding: 2px 6px;
    }
    .last-line {
      align-items: center;
      color: #243447;
      display: flex;
      flex: 1 1 auto;
      font-family: Consolas, monospace;
      font-size: 12px;
      gap: 6px;
      min-width: 0;
      overflow: hidden;
    }
    .last-line .msg { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .toggle-btn {
      background: #fff;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #3f4c59;
      cursor: pointer;
      flex: 0 0 auto;
      font-size: 12px;
      padding: 2px 8px;
    }
    .toggle-btn.active { background: #2563eb; border-color: #2563eb; color: #fff; }
    .filter-group { align-items: center; display: flex; flex: 1 1 auto; gap: 4px; min-width: 120px; }
    .filter {
      border: 1px solid #cfd8e3;
      border-radius: 3px;
      flex: 1 1 auto;
      font: inherit;
      font-size: 12px;
      min-width: 60px;
      padding: 2px 6px;
    }
    .filter.inactive { color: #91a0b1; }
    .mini-btn {
      background: transparent;
      border: 0;
      color: #c77c11;
      cursor: pointer;
      flex: 0 0 auto;
      font-weight: 700;
      padding: 0 4px;
    }
    button.action {
      background: #fff;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #243447;
      cursor: pointer;
      flex: 0 0 auto;
      font-size: 12px;
      padding: 2px 8px;
    }
    button.action:disabled { color: #91a0b1; cursor: default; }
    button.clear { color: #c77c11; font-weight: 700; }
    .body { background: #fff; flex: 1 1 auto; font-family: Consolas, monospace; font-size: 12px; min-height: 0; overflow: auto; }
    .group-header {
      background: #5f9ea0;
      color: #f0fffa;
      font-size: 11px;
      padding: 2px 8px;
      position: sticky;
      top: 0;
    }
    .row { align-items: center; display: flex; gap: 8px; padding: 1px 8px; }
    .row:hover { background: #f5fbff; }
    .row .time, .last-line .time { color: #91a0b1; flex: 0 0 auto; }
    .level-dot { border-radius: 50%; flex: 0 0 auto; height: 12px; width: 12px; }
    .row .msg { flex: 1 1 auto; overflow-wrap: anywhere; white-space: pre-wrap; }
    .history-error { padding: 6px 10px; color: #b91c1c; background: #fef2f2; white-space: pre-wrap; }
  `;

  constructor() {
    super();
    this.collapsed = false;
    this.entries = [];
    this.filterText = '';
    this.filterActive = false;
    this.showDebug = readBoolean(SHOW_DEBUG_KEY, false);
    this.historyLoading = false;
    this.historyExhausted = false;
    this.historyError = '';
    this.unsubscribe = null;
  }

  updated(changed) {
    if(changed.has('store')) this.#subscribeStore();
    if(changed.has('entries') && !this.collapsed) this.#stickToBottomIfNeeded(changed);
  }

  disconnectedCallback() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    super.disconnectedCallback();
  }

  render() {
    return this.collapsed ? this.#renderCollapsed() : this.#renderExpanded();
  }

  #renderCollapsed() {
    const last = this.#lastVisibleEntry();
    const level = last ? (LEVEL_INFO[last.ll] || LEVEL_INFO[1]) : null;
    return html`
      <div class="topbar">
        <span class="last-line">
          ${last ? html`
            <span class="time">${formatTime(last.dt)}</span>
            <span class="level-dot" style="background:${level.color}" title=${level.label}></span>
            <span class="msg">${last.msg}</span>` : ''}
        </span>
        <button class="toggle" @click=${this.#toggleCollapse} title="Expand">▲</button>
      </div>`;
  }

  #renderExpanded() {
    const groups = this.#groupedRows();
    return html`
      <div class="topbar">
        <button class="toggle-btn ${this.showDebug ? '' : 'active'}" title=${this.showDebug ? 'Hide debug entries' : 'Show debug entries'} @click=${this.#onToggleDebug}>${this.showDebug ? 'Debug' : 'Info'}</button>
        <div class="filter-group">
          <input class="filter ${this.filterActive ? '' : 'inactive'}" placeholder="Filter..." .value=${this.filterText} @input=${this.#onFilterInput} />
          <button class="mini-btn" title="Clear filter" @click=${this.#onClearFilter}>×</button>
          <button class="toggle-btn ${this.filterActive ? 'active' : ''}" title="Enable/disable filter" @click=${this.#onToggleFilterActive}>Filter</button>
        </div>
        <button class="action" ?disabled=${this.historyLoading || this.historyExhausted} @click=${this.#loadHistory}>History</button>
        <button class="action clear" title="Clear log" @click=${this.#clear}>×</button>
        <button class="toggle" @click=${this.#toggleCollapse} title="Collapse">▼</button>
      </div>
      ${this.historyError ? html`<div class="history-error" role="alert">${this.historyError}</div>` : ''}
      <div class="body">
        ${repeat(groups, (group) => group.key, (group) => html`
          <div class="group-header">${group.label}</div>
          ${repeat(group.rows, (row) => row.index, (row) => this.#renderRow(row.entry))}`)}
      </div>`;
  }

  #renderRow(entry) {
    const level = LEVEL_INFO[entry.ll] || LEVEL_INFO[1];
    return html`
      <div class="row">
        <span class="time">${formatTime(entry.dt)}</span>
        <span class="level-dot" style="background:${level.color}" title=${level.label}></span>
        <span class="msg">${entry.msg}</span>
      </div>`;
  }

  #groupedRows() {
    const groups = [];
    let currentKey = null;
    let currentGroup = null;
    this.#filteredEntries().forEach((entry, index) => {
      const key = formatDateKey(entry.dt);
      if(key !== currentKey) {
        currentKey = key;
        currentGroup = { key, label: formatDateLabel(entry.dt), rows: [] };
        groups.push(currentGroup);
      }
      currentGroup.rows.push({ index, entry });
    });
    return groups;
  }

  // Collapsed view intentionally ignores the text filter (only the Debug/Info level
  // toggle applies) - the last line should reflect what's actually happening, not
  // what a leftover filter string would hide.
  #lastVisibleEntry() {
    for(let i = this.entries.length - 1; i >= 0; i--) {
      const entry = this.entries[i];
      if(!this.showDebug && entry.ll === 0) continue;
      return entry;
    }
    return null;
  }

  #filteredEntries() {
    const filter = this.filterActive ? this.filterText.trim().toLowerCase() : '';
    return this.entries.filter((entry) => {
      if(!this.showDebug && entry.ll === 0) return false;
      if(filter && !entry.msg?.toLowerCase().includes(filter)) return false;
      return true;
    });
  }

  #subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.entries = [];
    this.historyLoading = false;
    this.historyExhausted = false;
    this.historyError = '';
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((entries) => {
      this.entries = entries;
      this.historyLoading = this.store.historyLoading;
      this.historyExhausted = this.store.historyExhausted && !this.store.gapBoundary;
      this.historyError = this.store.error;
    });
  }

  #stickToBottomIfNeeded() {
    const body = this.renderRoot.querySelector('.body');
    if(!body) return;
    const wasAtBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 24;
    if(wasAtBottom) requestAnimationFrame(() => { body.scrollTop = body.scrollHeight; });
  }

  #toggleCollapse() {
    this.dispatchEvent(new CustomEvent('log-panel-toggle', { bubbles: true, composed: true }));
  }

  #onFilterInput(e) {
    this.filterText = e.target.value;
  }

  #onToggleFilterActive() {
    this.filterActive = !this.filterActive;
  }

  #onClearFilter() {
    this.filterText = '';
  }

  #onToggleDebug() {
    this.showDebug = !this.showDebug;
    localStorage.setItem(SHOW_DEBUG_KEY, String(this.showDebug));
  }

  #clear() {
    this.store?.clear();
  }

  async #loadHistory() {
    if(!this.api || this.historyLoading || this.historyExhausted) return;
    await this.store?.loadHistory(this.api);
  }
}

customElements.define('x13-log-panel', X13LogPanel);

function formatTime(iso) {
  return formatLogTime(iso);
}

function formatDateKey(iso) {
  return logDateKey(iso);
}

function formatDateLabel(iso) {
  return formatLogDate(iso);
}
