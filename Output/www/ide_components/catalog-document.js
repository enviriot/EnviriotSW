import { LitElement, html, css, repeat } from '../lib/lit-all.min.js';

export class X13CatalogDocument extends LitElement {
  static properties = {
    api: { attribute: false },
    document: { attribute: false },
    store: { attribute: false },
    rows: { attribute: false },
  };

  static styles = css`
    :host {
      box-sizing: border-box;
      display: block;
      height: 100%;
      width: 100%;
    }
    .catalog {
      background: #fff;
      box-sizing: border-box;
      color: #243447;
      height: 100%;
      overflow: auto;
      padding: 0 0;
      width: 100%;
    }
    .uri {
      color: #687789;
      font-size: 12px;
      font-weight: 400;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .grid {
      border: 1px solid #cfd8e3;
      border-bottom: 0;
      border-left: 0;
      min-width: 640px;
    }
    .row {
      align-items: stretch;
      display: grid;
      grid-template-columns: minmax(180px, 1fr) minmax(200px, 2fr) 110px 110px;
      min-height: 30px;
    }
    .row {
      font-size: 13px;
    }
    .row:nth-child(odd) { background: #F5FBFF; }
    .row:nth-child(even) { background: #D6F0FF; }
    .cell {
      align-items: center;
      box-sizing: border-box;
      display: flex;
      min-width: 0;
      padding: 4px 8px;
    }
    .name-cell {
      align-items: stretch;
      padding: 0;
    }
    .expander-area {
      align-items: center;
      background: #fff;
      border-right: 1px solid #000;
      box-sizing: border-box;
      display: flex;
      flex-shrink: 0;
      height: 100%;
      padding: 4px 8px 4px calc(7px + var(--indent));
    }
    .twisty {
      background: transparent;
      border: 0;
      color: #3f4c59;
      cursor: pointer;
      font-size: 20px;
      height: 22px;
      line-height: 18px;
      padding: 0;
      width: 18px;
    }
    .twisty:disabled { cursor: default; }
    .name {
      align-items: center;
      display: flex;
      padding: 4px 8px;
    }
    .name,
    .info {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .button-text,
    .version {
      display: block;
      line-height: 1.1;
    }
    .version {
      font-size: 10px;
      margin-top: 2px;
    }
    button.action {
      border: 1px solid #aebccd;
      border-radius: 4px;
      box-sizing: border-box;
      color: #243447;
      cursor: pointer;
      font: inherit;
      min-height: 22px;
      padding: 2px 8px;
      width: 92px;
    }
    button.action:disabled {
      background: #eef2f6;
      color: #91a0b1;
      cursor: default;
    }
    button.download:not(:disabled) { background: #d6f5d6; }
    button.remove:not(:disabled) { background: #ffe4bd; }
  `;

  constructor() {
    super();
    this.rows = [];
    this.unsubscribe = null;
  }

  updated(changed) {
    if(changed.has('store')) this.#subscribeStore();
  }

  disconnectedCallback() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    super.disconnectedCallback();
  }

  render() {
    const title = this.document?.title || 'Catalog';
    const uri = this.document?.data?.uri || '';
    return html`
      <section class="catalog">
        <div class="grid">
          ${repeat(this.rows, (row) => row.vid, (row) => this.#renderRow(row))}
        </div>
      </section>`;
  }

  #renderRow(row) {
    const indent = Math.max(0, Number(row.level) || 0) * 12;
    const isFolder = !!row.expander;
    return html`
      <div class="row" style="--indent:${indent}px">
        <span class="cell name-cell">
          <span class="expander-area">
            <button class="twisty" ?disabled=${!row.expander} @click=${() => this.#toggle(row)}>
              ${row.expander ? (row.expander === 2 ? '▾' : '▸') : ''}
            </button>
          </span>
          <span class="name" title=${row.name || ''}>${row.name || ''}</span>
        </span>
        <span class="cell info" title=${row.info || row.value || ''}>${row.info || row.value || ''}</span>
        <span class="cell">
          ${isFolder ? html`` : html`
            <button class="action download" ?disabled=${!row.downloadEnabled} @click=${() => this.#rpc(row, 'download')}>
              <span class="button-text">Download</span>
              <span class="version">${row.srcVer || ''}</span>
            </button>`}
        </span>
        <span class="cell">
          ${isFolder ? html`` : html`
            <button class="action remove" ?disabled=${!row.removeEnabled} @click=${() => this.#rpc(row, 'remove')}>
              <span class="button-text">Remove</span>
              <span class="version">${row.actVer || ''}</span>
            </button>`}
        </span>
      </div>`;
  }

  #subscribeStore() {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.rows = [];
    if(!this.store) return;
    this.unsubscribe = this.store.subscribe((rows) => {
      this.rows = rows;
    });
  }

  #toggle(row) {
    if(!row?.vid || !this.api || !row.expander) return;
    this.api.expand(row.vid, row.expander !== 2).catch((error) => console.warn('catalog req.expand failed', row.vid, error));
  }

  #rpc(row, cmd) {
    if(!row?.vid || !this.api) return;
    this.api.rpc(row.vid, cmd).catch((error) => console.warn('catalog req.rpc failed', row.vid, cmd, error));
  }
}

customElements.define('x13-catalog-document', X13CatalogDocument);
