import { LitElement, html, css } from '../../lib/lit-all.min.js';

export class X13ContextMenu extends LitElement {
  static properties = {
    items: { attribute: false },
    x: { type: Number },
    y: { type: Number },
  };

  static styles = css`
    :host {
      display: block;
      left: 0;
      position: fixed;
      top: 0;
      z-index: 10000;
    }
    .menu {
      background: #fff;
      border: 1px solid #7f8c9a;
      box-shadow: 0 8px 18px rgba(15, 23, 42, 0.22);
      color: #111827;
      font: 13px Arial, sans-serif;
      max-width: calc(100vw - 8px);
      min-width: 180px;
      overflow: visible;
      padding: 3px 0;
      position: fixed;
      user-select: none;
    }
    .toolbar {
      align-items: center;
      border-bottom: 1px solid #d4dbe5;
      display: flex;
      gap: 2px;
      padding: 3px;
    }
    .toolbar button {
      align-items: center;
      background: #fff;
      border: 1px solid transparent;
      border-radius: 2px;
      display: inline-flex;
      height: 24px;
      justify-content: center;
      padding: 3px;
      width: 24px;
    }
    .toolbar button:not(:disabled):hover,
    .item > button:not(:disabled):hover {
      background: #e8f1ff;
      border-color: #8ab4f8;
    }
    button {
      color: inherit;
      cursor: default;
      font: inherit;
    }
    button:disabled {
      opacity: 0.45;
    }
    .separator {
      border-top: 1px solid #d4dbe5;
      height: 0;
      margin: 3px 0;
    }
    .item {
      position: relative;
    }
    .item > button {
      align-items: center;
      background: #fff;
      border: 1px solid transparent;
      box-sizing: border-box;
      display: grid;
      gap: 7px;
      grid-template-columns: 18px minmax(110px, 1fr) 12px;
      min-height: 24px;
      padding: 3px 8px 3px 5px;
      text-align: left;
      width: 100%;
    }
    .icon {
      height: 16px;
      width: 16px;
    }
    .check {
      color: #2563eb;
      font-weight: 700;
      text-align: center;
    }
    .label {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .arrow {
      color: #4b5563;
      text-align: right;
    }
    .submenu {
      background: #fff;
      border: 1px solid #7f8c9a;
      box-shadow: 0 8px 18px rgba(15, 23, 42, 0.18);
      display: none;
      left: calc(100% - 2px);
      min-width: 180px;
      overflow: visible;
      padding: 3px 0;
      position: absolute;
      top: -4px;
    }
    .menu.align-left .submenu {
      left: auto;
      right: calc(100% - 2px);
    }
    .item:hover > .submenu,
    .item:focus-within > .submenu {
      display: block;
    }
  `;

  constructor() {
    super();
    this.items = [];
    this.x = 0;
    this.y = 0;
    this.onOutsidePointerDown = (event) => this.#onOutsidePointerDown(event);
    this.onKeyDown = (event) => this.#onKeyDown(event);
  }

  connectedCallback() {
    super.connectedCallback();
    document.addEventListener('pointerdown', this.onOutsidePointerDown, true);
    window.addEventListener('keydown', this.onKeyDown, true);
  }

  disconnectedCallback() {
    document.removeEventListener('pointerdown', this.onOutsidePointerDown, true);
    window.removeEventListener('keydown', this.onKeyDown, true);
    super.disconnectedCallback();
  }

  updated() {
    this.#fitInViewport();
  }

  render() {
    return html`
      <div class="menu ${this.#alignLeft() ? 'align-left' : ''}" style=${this.#positionStyle()} role="menu" @pointerdown=${this.#stopEvent}>
        ${this.#renderItems(this.items || [])}
      </div>`;
  }

  #renderItems(items) {
    return items.map((item) => this.#renderItem(item || {}));
  }

  #renderItem(item) {
    if(item.kind === 'separator') return html`<div class="separator" role="separator"></div>`;
    if(item.kind === 'toolbar') {
      return html`<div class="toolbar" role="toolbar">
        ${(item.children || []).map((child) => this.#renderToolbarButton(child || {}))}
      </div>`;
    }
    return this.#renderCommandItem(item);
  }

  #renderToolbarButton(item) {
    return html`
      <button
        type="button"
        title=${item.hint || item.text || item.cmd || ''}
        ?disabled=${item.enabled === false}
        @click=${(e) => this.#select(e, item)}>
        ${item.icon ? html`<img class="icon" src=${item.icon} alt="" @error=${this.#iconError}>` : html``}
      </button>`;
  }

  #renderCommandItem(item) {
    const hasChildren = Array.isArray(item.children) && item.children.length > 0;
    return html`
      <div class="item" role="none">
        <button
          type="button"
          role="menuitem"
          title=${item.hint || item.text || item.cmd || ''}
          ?disabled=${item.enabled === false}
          @click=${(e) => this.#select(e, item)}>
          <span>${item.checked ? html`<span class="check">✓</span>` : this.#renderIcon(item.icon)}</span>
          <span class="label">${item.text || item.cmd || ''}</span>
          <span class="arrow">${hasChildren ? '›' : ''}</span>
        </button>
        ${hasChildren ? html`<div class="submenu" role="menu">${this.#renderItems(item.children)}</div>` : html``}
      </div>`;
  }

  #renderIcon(icon) {
    if(!icon) return html``;
    return html`<img class="icon" src=${icon} alt="" @error=${this.#iconError}>`;
  }

  #select(event, item) {
    event.preventDefault();
    event.stopPropagation();
    if(item.enabled === false || !item.cmd) return;
    this.dispatchEvent(new CustomEvent('menu-command', {
      bubbles: true,
      composed: true,
      detail: { cmd: item.cmd, willful: !!item.willful, item },
    }));
  }

  #positionStyle() {
    const x = Math.max(0, Number(this.x) || 0);
    const y = Math.max(0, Number(this.y) || 0);
    return `left:${x}px;top:${y}px`;
  }

  #alignLeft() {
    return (Number(this.x) || 0) > window.innerWidth / 2;
  }

  #fitInViewport() {
    const menu = this.renderRoot.querySelector('.menu');
    if(!menu) return;
    const margin = 4;
    const rect = menu.getBoundingClientRect();
    const maxLeft = Math.max(margin, window.innerWidth - rect.width - margin);
    const maxTop = Math.max(margin, window.innerHeight - rect.height - margin);
    menu.style.left = `${Math.min(Math.max(margin, Number(this.x) || 0), maxLeft)}px`;
    menu.style.top = `${Math.min(Math.max(margin, Number(this.y) || 0), maxTop)}px`;
  }

  #onOutsidePointerDown(event) {
    if(event.composedPath().includes(this)) return;
    this.#close();
  }

  #onKeyDown(event) {
    if(event.key !== 'Escape') return;
    event.preventDefault();
    this.#close();
  }

  #close() {
    this.dispatchEvent(new CustomEvent('menu-close', { bubbles: true, composed: true }));
  }

  #stopEvent(event) {
    event.stopPropagation();
  }

  #iconError(event) {
    event.currentTarget.style.visibility = 'hidden';
  }
}

customElements.define('x13-context-menu', X13ContextMenu);
