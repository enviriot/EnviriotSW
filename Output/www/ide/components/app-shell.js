import { LitElement, html, css } from '../../lib/lit-all.min.js';
import './view-workspace.js';
import './catalog-document.js';
import './inspector-document.js';
import './logram-document.js';
import './chart-document.js';
import './logo-document.js';
import './log-panel.js';
import { WsClient } from '../services/ws-client.js';
import { ViewApi } from '../services/view-api.js';
import { ViewStore } from '../services/view-store.js';
import { LogStore } from '../services/log-store.js';
import { getTopicPath, WORKSPACE_ROOT_VID } from '../services/vid-helper.js';
import { setApiToken } from '../services/api-token.js';
import { ShellLayout } from './shell-layout.js';
import { DocumentSession } from './document-session.js';
import { DocumentRouter } from './document-router.js';

// Оболочка IDE - композиция: подключение, Workspace, журнал и документ на экране. Какой документ
// на экране и как он связан с адресом - DocumentRouter, его серверная сторона - DocumentSession,
// размеры панелей - ShellLayout.
export class X13AppShell extends LitElement {
  static properties = {
    status: {},
    store: { attribute: false },
    serverName: {},
    workspaceReveal: { attribute: false },
  };

  static styles = css`
    :host { display: block; height: 100vh; }
    .shell {
      background: #f6f8fb;
      box-sizing: border-box;
      display: grid;
      grid-template-columns: minmax(0, 1fr) 8px var(--workspace-width);
      grid-template-rows: minmax(0, 1fr);
      height: 100%;
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
    .content-column {
      display: grid;
      grid-template-rows: minmax(0, 1fr) 8px var(--log-height);
      min-height: 0;
      min-width: 0;
    }
    .content-pane {
      align-items: center;
      display: flex;
      justify-content: center;
      min-height: 0;
      min-width: 0;
      overflow: auto;
    }
    .log-splitter {
      background: linear-gradient(180deg, #c7d2df 0, #c7d2df 1px, #eef3f8 1px, #eef3f8 3px, #93a4b8 3px, #93a4b8 5px, #eef3f8 5px, #eef3f8 7px, #c7d2df 7px);
      cursor: row-resize;
      height: 8px;
    }
    .log-splitter:hover {
      background: linear-gradient(180deg, #93b4df 0, #93b4df 1px, #dbeafe 1px, #dbeafe 3px, #2563eb 3px, #2563eb 5px, #dbeafe 5px, #dbeafe 7px, #93b4df 7px);
    }
    .log-pane {
      min-height: 0;
      overflow: hidden;
    }
  `;

  constructor() {
    super();
    this.status = 'connecting';
    this.store = new ViewStore();
    this.serverName = '';
    this.logStore = new LogStore();
    this.workspaceReveal = null;
    this.#layout = new ShellLayout(this);
    this.#session = new DocumentSession(() => this.api);
    this.#router = new DocumentRouter(this, this.#session, () => this.status === 'connected' && !!this.api);
  }

  connectedCallback() {
    super.connectedCallback();
    this.#syncRootStatus();
    this.#start();
  }

  render() {
    const layout = this.#layout;
    return html`
      <main class="shell" style="--workspace-width:${layout.workspaceWidth}px" @editor-commit=${this.#onEditorCommit} @editor-rpc=${this.#onEditorRpc} @workspace-menu-command=${this.#onWorkspaceMenuCommand} @log-panel-toggle=${() => layout.toggleLogCollapsed()}>
        <div class="content-column" style="--log-height:${layout.logPaneHeight}px">
          <section class="content-pane">${this.#renderContent()}</section>
          <div class="log-splitter" role="separator" aria-orientation="horizontal" @pointerdown=${(e) => layout.startLogResize(e)}></div>
          <x13-log-panel class="log-pane" .api=${this.api} .store=${this.logStore} .collapsed=${layout.logCollapsed}></x13-log-panel>
        </div>
        <div class="splitter" role="separator" aria-orientation="vertical" @pointerdown=${(e) => layout.startResize(e)}></div>
        <section class="workspace-pane" aria-label="Workspace">
          <x13-view-workspace .api=${this.api} .store=${this.store} .revealPath=${this.workspaceReveal}></x13-view-workspace>
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
    if(status !== 'connected') {
      this.logStore.connectionChanged(status, this.api);
      setApiToken('');  // the token dies with the session that issued it
      return;
    }
    const socket = this.client.socket;
    // Документ на экране пережил разрыв, а его view - нет: ViewSession живёт ровно одно
    // подключение. Без переоткрытия он остался бы с прежними данными и без подписок - выглядел бы
    // живым, но не обновлялся. Флаги сбрасываются до hello, чтобы и навигация, успевшая случиться
    // за время hello, не закрывала в новой сессии то, чего та не открывала.
    const stale = this.#router.document;
    this.#session.forget(stale);
    try {
      this.#resetRepositoryState();
      const hello = await this.api.hello();
      if(this.status !== 'connected' || this.client.socket !== socket) return;
      this.serverName = hello?.name || '';
      setApiToken(hello?.token);
    }
    catch(error) {
      console.warn('req.hello failed', error);
    }
    if(this.status !== 'connected' || this.client.socket !== socket) return;
    this.logStore.connectionChanged(status, this.api);
    this.#router.reopen(stale);
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
    const existingRoot = this.store.rows.find((row) => row.vid === WORKSPACE_ROOT_VID);
    this.store.add({
      vid: WORKSPACE_ROOT_VID,
      level: 0,
      expander: existingRoot?.expander || 0,
      icon: existingRoot?.icon || '/ide/icons/ty_topic.png',
      name: existingRoot?.name || '',
      editor: 'ConnectionStatus',
      value: this.status,
      readonly: true,
    });
  }

  #onMessage(message) {
    if(message.type === 'evnt.log') {
      this.logStore.applyLog(message);
      return;
    }
    if(message.type !== 'evnt.add' && message.type !== 'evnt.upd' && message.type !== 'evnt.del') return;
    const effects = this.#session.route(message, this.#router.document);
    if(!effects) {
      this.store.applyPacket(message);
      return;
    }
    if(effects.altView !== undefined) this.#router.adoptAltView(effects.altView);
    if(effects.rootDeleted) this.#router.rootDeleted();
  }

  #renderContent() {
    const doc = this.#router.document;
    if(doc?.view === 'catalog') {
      return html`<x13-catalog-document .api=${this.api} .document=${doc} .store=${doc.store}></x13-catalog-document>`;
    }
    if(doc?.view === 'inspector') {
      return html`<x13-inspector-document .path=${doc.path} .rootName=${this.serverName}
        .api=${this.api} .store=${doc.store} .altView=${doc.altView || null}
        @segment-command=${this.#onSegmentCommand}></x13-inspector-document>`;
    }
    if(doc?.view === 'logram' || doc?.view === 'lplc') {
      return html`<x13-logram-document .view=${doc.view} .path=${doc.path} .rootName=${this.serverName}
        .api=${this.api} .store=${doc.store}
        @segment-command=${this.#onSegmentCommand}></x13-logram-document>`;
    }
    if(doc?.view === 'chart') {
      return html`<x13-chart-document .path=${doc.path} .rootName=${this.serverName} .store=${doc.stores.chart}
        @segment-command=${this.#onSegmentCommand}></x13-chart-document>`;
    }
    // Nothing to show yet, but something is coming (DocumentRouter.pending). The logo is for an
    // empty shell, not for a document that is one round trip away.
    if(this.#router.pending) return html``;
    return html`<x13-logo-document></x13-logo-document>`;
  }

  async #onWorkspaceMenuCommand(e) {
    const row = e.detail?.row;
    const cmd = e.detail?.cmd;
    if(!row?.vid) return;
    // Always through the server's own resolve: it opens the right side in the same round trip
    // (ViewSession.HandleOpenAuto), so a hint carried on the row would only let the client
    // duplicate a decision already made for it - and would have to be kept on every row of the
    // tree to be available here. The document learns what else its topic can be shown as from
    // its own root row, which is the one row that carries it.
    if(cmd === 'open') {
      this.#router.navigate(getTopicPath(row.vid), null);
      return;
    }
    if(cmd === 'open-tab') {
      this.#router.openInNewTab(getTopicPath(row.vid));
      return;
    }
    if(cmd === 'chart') {
      this.#router.navigate(getTopicPath(row.vid), 'chart');
      return;
    }
    if(cmd === 'catalog') this.#router.openCatalog(row.vid);
  }

  #onSegmentCommand(e) {
    const { cmd, path, mode } = e.detail || {};
    if(!path) return;
    if(cmd === 'copy-path') {
      navigator.clipboard.writeText(path).catch((error) => console.warn('clipboard write failed', error));
      return;
    }
    // From a Logram pin's "Show in Workspace" (logram-document.js #showInWorkspace) - a new object
    // every time (even for the same path) so x13-view-workspace's updated() sees a change and
    // re-reveals, e.g. after the row scrolled out of view.
    if(cmd === 'show-in-workspace') {
      this.workspaceReveal = { path, token: Date.now() };
      return;
    }
    if(cmd === 'open-tab') {
      this.#router.openInNewTab(path);
      return;
    }
    // The bar's switch button: the other view of the topic already on screen. A named choice, so
    // it survives into the URL where it has to (see documentDescriptor), and an ordinary history
    // entry, so Back returns to the view you switched away from.
    //
    // Standing on the Logram document is itself proof that the topic has a Logram side, which is
    // the one thing the Inspector document it opens could not otherwise know until inspchildren
    // says so - see DocumentRouter.adoptAltView.
    if(cmd === 'switch-view') {
      const view = this.#router.document?.view;
      this.#router.navigate(path, mode, { requested: true, altView: view === 'logram' || view === 'lplc' ? view : undefined });
      return;
    }
    // mode arrives as undefined for ancestor segments (see breadcrumb-bar.js #emit) - pass it
    // through as-is rather than defaulting to Inspector, so navigate can tell "known" apart from
    // "don't know yet" and route the latter through the router's resolve.
    this.#router.navigate(path, mode === undefined ? null : mode);
  }

  #onEditorCommit(e) {
    const vid = e.detail?.vid;
    if(!vid || !this.api) return;
    e.detail.promise = this.api.commit(vid, e.detail.value).catch((error) => {
      console.warn('req.commit failed', vid, error);
      throw error;
    });
  }

  // Mirrors #onEditorCommit for editors that need to trigger a req.rpc command against their own
  // row instead of (or alongside) committing a value - e.g. the DevicePLC editor's inline
  // Build/Run/Start/Stop buttons (view:'value' only).
  #onEditorRpc(e) {
    const { vid, cmd } = e.detail || {};
    if(!vid || !cmd || !this.api) return;
    e.detail.promise = this.api.rpc(vid, cmd).catch((error) => {
      console.warn('req.rpc failed', vid, cmd, error);
      throw error;
    });
  }

  #layout;
  #session;
  #router;
}

customElements.define('x13-app-shell', X13AppShell);
