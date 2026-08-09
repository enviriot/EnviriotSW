import { LitElement, html, css } from '../lib/lit-all.min.js';
import './view-workspace.js';
import './catalog-document.js';
import { WsClient } from '../ide_services/ws-client.js';
import { ViewApi } from '../ide_services/view-api.js';
import { ViewStore } from '../ide_services/view-store.js';
import { clamp, readPositiveNumber } from '../ide_services/local-storage-utils.js';

const WORKSPACE_WIDTH_KEY = 'x13.workspace.width';
const DEFAULT_WORKSPACE_WIDTH = 420;
const MIN_WORKSPACE_WIDTH = 260;
const MAX_WORKSPACE_WIDTH = 900;
const ROOT_VID = 'workspace#/';

export class X13AppShell extends LitElement {
  static properties = {
    status: {},
    store: { attribute: false },
    workspaceWidth: { type: Number },
    activeDocument: { attribute: false },
  };

  static styles = css`
    :host { display: block; min-height: 100vh; }
    .shell {
      background: #f6f8fb;
      box-sizing: border-box;
      display: grid;
      grid-template-columns: minmax(0, 1fr) 8px var(--workspace-width);
      min-height: 100vh;
    }
    .workspace-pane {
      background: #fff;
      border-left: 1px solid #cfd8e3;
      min-width: 0;
      overflow: hidden;
    }
    .splitter {
      background: linear-gradient(90deg, #c7d2df 0, #c7d2df 1px, #eef3f8 1px, #eef3f8 3px, #93a4b8 3px, #93a4b8 5px, #eef3f8 5px, #eef3f8 7px, #c7d2df 7px);
      cursor: col-resize;
      width: 8px;
    }
    .splitter:hover {
      background: linear-gradient(90deg, #93b4df 0, #93b4df 1px, #dbeafe 1px, #dbeafe 3px, #2563eb 3px, #2563eb 5px, #dbeafe 5px, #dbeafe 7px, #93b4df 7px);
    }
    .content-pane {
      align-items: center;
      color: #7b8794;
      display: flex;
      justify-content: center;
      min-width: 0;
    }
    .segment-wheel {
      position: relative;
      width: 70vh;
      height: 70vh;
    }
    .segment-wheel img {
      position: absolute;
      top: 0;
      left: 0;
      width: 70vh;
      height: 70vh;
      opacity: 0;
      transition: opacity 0.3s ease;
    }
    .segment-wheel img.visible {
      opacity: 0.7;
    }
  `;

  constructor() {
    super();
    this.status = 'connecting';
    this.store = new ViewStore();
    this.workspaceWidth = readPositiveNumber(WORKSPACE_WIDTH_KEY, DEFAULT_WORKSPACE_WIDTH);
    this.activeDocument = null;
  }

  connectedCallback() {
    super.connectedCallback();
    this.#syncRootStatus();
    this.#start();
  }

  render() {
    return html`
      <main class="shell" style="--workspace-width:${this.workspaceWidth}px" @editor-commit=${this.#onEditorCommit} @workspace-menu-command=${this.#onWorkspaceMenuCommand}>
        <section class="content-pane">${this.#renderContent()}</section>
        <div class="splitter" role="separator" aria-orientation="vertical" @pointerdown=${this.#startResize}></div>
        <section class="workspace-pane" aria-label="Workspace">
          <x13-view-workspace .api=${this.api} .store=${this.store}></x13-view-workspace>
        </section>
      </main>`;
  }

  async #start() {
    this.client = new WsClient();
    this.api = new ViewApi(this.client);
    this.client.addEventListener('status', (e) => { this.#setStatus(e.detail); this.#onConnected(e.detail); });
    this.client.addEventListener('message', (e) => this.#onMessage(e.detail));
    this.client.connect();
  }

  async #onConnected(status) {
    if(status !== 'connected') return;
    try {
      this.#resetRepositoryState();
      await this.api.hello();
    }
    catch(error) {
      console.warn('req.hello failed', error);
    }
  }

  #setStatus(status) {
    this.status = status;
    this.#syncRootStatus();
    this.requestUpdate();
  }

  #resetRepositoryState() {
    this.store.clear();
  }

  #syncRootStatus() {
    const existingRoot = this.store.rows.find((row) => row.vid === ROOT_VID);
    this.store.add({
      vid: ROOT_VID,
      level: 0,
      expander: existingRoot?.expander || 0,
      icon: existingRoot?.icon || '/ide_icons/ty_topic.png',
      name: existingRoot?.name || '',
      editor: 'ConnectionStatus',
      value: this.status,
      readonly: true,
    });
  }

  #onMessage(message) {
    if(message.type === 'evnt.add' || message.type === 'evnt.upd' || message.type === 'evnt.del') {
      if(String(message.vid || '').startsWith('catalog#')) this.activeDocument?.store?.applyPacket(message);
      else this.store.applyPacket(message);
    }
  }


  #revealedSegments = 0;
  #segmentsTimerStarted = false;

  #startSegmentReveal() {
    if(this.#segmentsTimerStarted) return;
    this.#segmentsTimerStarted = true;
    this.#revealedSegments = 1;
    let delay = 600;
    let elapsed = 0;
    for(let i = 1; i < 6; i++) {
      elapsed += delay;
      setTimeout(() => {
        this.#revealedSegments = i + 1;
        this.requestUpdate();
      }, elapsed);
      delay -= 100;
    }
  }

  #renderContent() {
    if(this.activeDocument?.view === 'catalog') {
      return html`<x13-catalog-document .api=${this.api} .document=${this.activeDocument} .store=${this.activeDocument.store}></x13-catalog-document>`;
    }
    this.#startSegmentReveal();
    return html`
      <div class="segment-wheel">
        ${[0, 1, 2, 3, 4, 5].map((i) => html`
          <img src="/ide_icons/segment.svg" style="transform: rotate(${360 - i * 60}deg)" class=${i < this.#revealedSegments ? 'visible' : ''}>
        `)}
      </div>`;
  }

  async #onWorkspaceMenuCommand(e) {
    const row = e.detail?.row;
    const cmd = e.detail?.cmd;
    if(cmd !== 'catalog' || !row?.vid || !this.api) return;
    try {
      const document = await this.api.open(row.vid, 'catalog');
      document.store = new ViewStore();
      document.store.add({
        vid: document.vid || 'catalog#/',
        level: 0,
        expander: 1,
        icon: '/ide_icons/ty_topic.png',
        name: document.title || 'Catalog',
        editor: 'Default',
        value: document.data?.uri || '',
        readonly: true,
        info: document.data?.uri || '',
        downloadEnabled: false,
        removeEnabled: false,
      });
      this.activeDocument = document;
      this.api.expand(document.vid || 'catalog#/', true)
        .catch((error) => console.warn('catalog initial req.expand failed', document.vid, error));
    }
    catch(error) {
      console.warn('req.open catalog failed', row.vid, error);
    }
  }

  #onEditorCommit(e) {
    const vid = e.detail?.vid;
    if(!vid || !this.api) return;
    e.detail.promise = this.api.commit(vid, e.detail.value).catch((error) => {
      console.warn('req.commit failed', vid, error);
      throw error;
    });
  }

  #startResize(e) {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = this.workspaceWidth;
    const move = (ev) => {
      this.workspaceWidth = clamp(startWidth - (ev.clientX - startX), MIN_WORKSPACE_WIDTH, MAX_WORKSPACE_WIDTH);
    };
    const up = () => {
      localStorage.setItem(WORKSPACE_WIDTH_KEY, String(this.workspaceWidth));
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }
}

customElements.define('x13-app-shell', X13AppShell);
