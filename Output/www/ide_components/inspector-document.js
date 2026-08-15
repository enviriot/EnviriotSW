import { LitElement, html, css } from '../lib/lit-all.min.js';
import './context-menu.js';
import './inspector-children.js';
import './logram-document.js';

export class X13InspectorDocument extends LitElement {
  static properties = {
    path: {},
    rootName: {},
    api: { attribute: false },
    store: { attribute: false },
    isLogram: { type: Boolean },
    subView: {},
    logramStore: { attribute: false },
    menuState: { attribute: false },
  };

  static styles = css`
    :host { box-sizing: border-box; display: flex; flex-direction: column; height: 100%; width: 100%; }
    .bar {
      align-items: center;
      background: #f3f6fa;
      border-bottom: 1px solid #cfd8e3;
      display: flex;
      flex: 0 0 auto;
      flex-wrap: wrap;
      font-size: 13px;
      gap: 2px;
      padding: 6px 10px;
    }
    x13-inspector-children,
    x13-logram-document {
      flex: 1 1 auto;
      min-height: 0;
    }
    .view-toggle {
      background: #fff;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #243447;
      cursor: pointer;
      font: inherit;
      font-size: 12px;
      margin-left: auto;
      padding: 3px 8px;
    }
    .segment {
      background: transparent;
      border: 1px solid transparent;
      border-radius: 0px;
      color: #1f2937;
      cursor: default;
      font: inherit;
      padding: 3px 6px;
    }
    .segment:hover {
      background: #e8f1ff;
      border-color: #8ab4f8;
    }
    .segment.current {
      font-weight: 600;
    }
    .sep {
      color: #9aa7b6;
      padding: 0 1px;
    }
  `;

  constructor() {
    super();
    this.path = '/';
    this.rootName = '';
    this.api = null;
    this.store = null;
    this.isLogram = false;
    this.subView = 'inspector';
    this.logramStore = null;
    this.menuState = null;
  }

  render() {
    const segments = this.#segments();
    const showingLogram = this.isLogram && this.subView === 'logram';
    return html`
      <div class="bar">
        ${segments.map((segment, index) => html`
          ${index > 0 ? html`<span class="sep">/</span>` : html``}
          <button
            type="button"
            class="segment ${index === segments.length - 1 ? 'current' : ''}"
            @click=${() => this.#openSegment(segment)}
            @contextmenu=${(e) => this.#openMenu(e, segment)}>
            ${segment.name || '/'}
          </button>
        `)}
        ${this.menuState ? html`
          <x13-context-menu
            .items=${this.menuState.items}
            .x=${this.menuState.x}
            .y=${this.menuState.y}
            @menu-command=${this.#menuCommand}
            @menu-close=${this.#closeMenu}>
          </x13-context-menu>` : html``}
        ${this.isLogram ? html`
          <button type="button" class="view-toggle" @click=${this.#toggleView}>
            ${showingLogram ? 'Inspector' : 'Logram'}
          </button>` : html``}
      </div>
      ${showingLogram
        ? html`<x13-logram-document .api=${this.api} .store=${this.logramStore}></x13-logram-document>`
        : html`<x13-inspector-children .api=${this.api} .store=${this.store} .rootPath=${this.path}></x13-inspector-children>`}`;
  }

  #toggleView() {
    this.dispatchEvent(new CustomEvent('view-toggle', { bubbles: true, composed: true }));
  }

  #segments() {
    const parts = String(this.path || '/').split('/').filter(Boolean);
    const segments = [{ name: this.rootName, path: '/' }];
    let current = '';
    for(const part of parts) {
      current += `/${part}`;
      segments.push({ name: part, path: current });
    }
    return segments;
  }

  #openSegment(segment) {
    this.#emit('open', segment.path);
  }

  #openMenu(e, segment) {
    e.preventDefault();
    this.menuState = {
      segment,
      items: [
        { cmd: 'open', text: 'Open' },
        { cmd: 'open-tab', text: 'Open in new tab' },
        { cmd: 'copy-path', text: 'Copy path' },
      ],
      x: e.clientX,
      y: e.clientY,
    };
  }

  #menuCommand(e) {
    const cmd = e.detail?.cmd;
    const segment = this.menuState?.segment;
    this.#closeMenu();
    if(!cmd || !segment) return;
    this.#emit(cmd, segment.path);
  }

  #closeMenu() {
    this.menuState = null;
  }

  // The breadcrumb only ever knows path strings, not each segment's topic type - but
  // for the CURRENT (last) segment, that's already this.isLogram (known synchronously
  // from the open document, no lookup needed). Passing it through here is what makes
  // re-clicking "L1" while viewing its Logram sub-view stay on Logram instead of
  // silently reverting to Inspector; ancestor segments pass undefined (genuinely
  // unknown, not "known false") so app-shell.js#setInspectorDocument routes them
  // through #openAuto's single-round-trip resolve instead of guessing Inspector.
  #emit(cmd, path) {
    const isLogram = path === this.path ? this.isLogram : undefined;
    this.dispatchEvent(new CustomEvent('segment-command', {
      bubbles: true,
      composed: true,
      detail: { cmd, path, isLogram },
    }));
  }
}

customElements.define('x13-inspector-document', X13InspectorDocument);
