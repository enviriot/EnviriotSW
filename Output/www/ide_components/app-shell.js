import { LitElement, html, css } from '../lib/lit-all.min.js';
import './view-workspace.js';
import './catalog-document.js';
import './inspector-document.js';
import './chart-document.js';
import './logo-document.js';
import './log-panel.js';
import { WsClient } from '../ide_services/ws-client.js';
import { ViewApi } from '../ide_services/view-api.js';
import { ViewStore, CombinedViewStore } from '../ide_services/view-store.js';
import { LogStore } from '../ide_services/log-store.js';
import { clamp, readPositiveNumber, readBoolean } from '../ide_services/local-storage-utils.js';
import { getTopicPath } from '../ide_services/vid-helper.js';
import { setApiToken } from '../ide_services/api-token.js';

const WORKSPACE_WIDTH_KEY = 'x13.workspace.width';
const DEFAULT_WORKSPACE_WIDTH = 420;
const MIN_WORKSPACE_WIDTH = 260;
const MAX_WORKSPACE_WIDTH = 900;
const ROOT_VID = 'workspace#/';

const LOG_HEIGHT_KEY = 'x13.logpanel.height';
const LOG_COLLAPSED_KEY = 'x13.logpanel.collapsed';
const DEFAULT_LOG_HEIGHT = 220;
const MIN_LOG_HEIGHT = 80;
const MAX_LOG_HEIGHT = 700;
const COLLAPSED_LOG_HEIGHT = 28;

export class X13AppShell extends LitElement {
  static properties = {
    status: {},
    store: { attribute: false },
    workspaceWidth: { type: Number },
    activeDocument: { attribute: false },
    serverName: {},
    logHeight: { type: Number },
    logCollapsed: { type: Boolean },
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
    this.workspaceWidth = readPositiveNumber(WORKSPACE_WIDTH_KEY, DEFAULT_WORKSPACE_WIDTH);
    this.activeDocument = null;
    this.serverName = '';
    this.logStore = new LogStore();
    this.logHeight = readPositiveNumber(LOG_HEIGHT_KEY, DEFAULT_LOG_HEIGHT);
    this.logCollapsed = readBoolean(LOG_COLLAPSED_KEY, true);
    this.workspaceReveal = null;
    this.pendingRestore = null;
    this.onPopState = (e) => this.#onPopState(e);
  }

  connectedCallback() {
    super.connectedCallback();
    this.#syncRootStatus();
    this.#restoreFromLocation();
    window.addEventListener('popstate', this.onPopState);
    this.#start();
  }

  disconnectedCallback() {
    window.removeEventListener('popstate', this.onPopState);
    super.disconnectedCallback();
  }

  render() {
    const logHeight = this.logCollapsed ? COLLAPSED_LOG_HEIGHT : this.logHeight;
    return html`
      <main class="shell" style="--workspace-width:${this.workspaceWidth}px" @editor-commit=${this.#onEditorCommit} @editor-rpc=${this.#onEditorRpc} @workspace-menu-command=${this.#onWorkspaceMenuCommand} @log-panel-toggle=${this.#toggleLogCollapsed}>
        <div class="content-column" style="--log-height:${logHeight}px">
          <section class="content-pane">${this.#renderContent()}</section>
          <div class="log-splitter" role="separator" aria-orientation="horizontal" @pointerdown=${this.#startLogResize}></div>
          <x13-log-panel class="log-pane" .api=${this.api} .store=${this.logStore} .collapsed=${this.logCollapsed}></x13-log-panel>
        </div>
        <div class="splitter" role="separator" aria-orientation="vertical" @pointerdown=${this.#startResize}></div>
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
      setApiToken('');  // the token dies with the session that issued it
      return;
    }
    try {
      this.#resetRepositoryState();
      const hello = await this.api.hello();
      this.serverName = hello?.name || '';
      setApiToken(hello?.token);
    }
    catch(error) {
      console.warn('req.hello failed', error);
    }
    const restore = this.pendingRestore;
    this.pendingRestore = null;
    this.#applyRestore(restore);
  }

  #applyRestore(restore) {
    if(!restore) return;
    if(restore.view === 'inspector') this.#setInspectorDocument(restore.path, restore.isLogram, restore.forceMode);
    else if(restore.view === 'catalog') this.#openCatalog(ROOT_VID, { pushHistory: false });
    else if(restore.view === 'chart') this.#setChartDocument(restore.path);
  }

  // Only ONE of Inspector/Logram is ever open at a time (see #openInspectorPanes/
  // #openLogram and #toggleDocumentView, below) - loading both up front would mean
  // paying for the full Logram diagram payload, or the full children list, for a
  // subView that's never shown, since a document only ever displays one or the
  // other. isLogram is an optional hint (from the row that triggered navigation -
  // see #onWorkspaceMenuCommand) used to pick which side to load immediately,
  // without waiting on a round trip: when known true or false, the matching side
  // opens straight away. When left undefined (breadcrumb clicks to an ancestor,
  // parent-on-delete navigation, URL restore all only have a path) - and forceMode
  // isn't pinning the view either - #openAuto asks the server to resolve Core/Logram
  // itself via req.open{view:null} and open exactly the right side in one round
  // trip, rather than opening Inspector speculatively and switching after the fact.
  // forceMode:'inspector' (from ?mode=inspector) skips resolution entirely and
  // always shows Inspector, even for a Logram-typed topic.
  // Closes whichever side of the previous document was actually open first, so
  // expansion state/subscriptions don't outlive it - this is what makes Inspector's
  // expand state document-scoped rather than session-scoped like Workspace's.
  #setInspectorDocument(path, isLogram, forceMode = null) {
    if(this.activeDocument?.view === 'inspector') {
      this.#closeInspectorPanes(this.activeDocument);
      this.#closeLogram(this.activeDocument);
    }

    const forcedInspector = forceMode === 'inspector';
    const useAuto = !forcedInspector && isLogram === undefined;
    const resolvedIsLogram = forcedInspector ? false : !!isLogram;

    this.activeDocument = {
      view: 'inspector',
      path,
      forceMode,
      stateStore: useAuto ? new ViewStore() : null,
      manifestStore: useAuto ? new ViewStore() : null,
      childrenStore: useAuto ? new ViewStore() : null,
      logramStore: new ViewStore(),
      store: null,
      isLogram: useAuto ? undefined : resolvedIsLogram,
      inspectorOpened: false,
      logramOpened: false,
      subView: useAuto ? undefined : (resolvedIsLogram ? 'logram' : 'inspector'),
    };

    if(useAuto) this.#openAuto(path);
    else if(resolvedIsLogram) this.#openLogram(path);
    else this.#openInspectorPanes(path);
  }

  // Single round trip for the "don't know yet" case: server resolves Core/Logram
  // (one field read) and opens exactly that one side, pushing its evnt.add rows
  // before resp.open arrives - the pre-created stateStore/manifestStore/childrenStore
  // above exist precisely so those rows land correctly no matter which side wins,
  // since #onMessage routes them by vid prefix as soon as they arrive, ahead of this
  // promise resolving (see ViewSession.HandleOpenAuto on the server).
  async #openAuto(path) {
    const doc = this.activeDocument;
    if(!doc || doc.path !== path || !this.api) return;
    try {
      const result = await this.api.open(path, null);
      const isLogram = result?.view === 'logram';
      if(this.activeDocument !== doc) {
        // Navigated away while resolving - the server already opened this side (see
        // ViewSession.HandleOpenAuto) before we knew which one it'd be, and neither
        // #closeInspectorPanes nor #closeLogram fired for it back when we left (both
        // inspectorOpened/logramOpened were still false). Close it now so it doesn't
        // linger server-side.
        if(isLogram) this.api.close(`logram#${path}`).catch((error) => console.warn('req.close failed', `logram#${path}`, error));
        else {
          this.api.close(`inspstate#${path}`).catch((error) => console.warn('req.close failed', `inspstate#${path}`, error));
          this.api.close(`inspmanifest#${path}`).catch((error) => console.warn('req.close failed', `inspmanifest#${path}`, error));
          this.api.close(`inspchildren#${path}`).catch((error) => console.warn('req.close failed', `inspchildren#${path}`, error));
        }
        return;
      }
      this.activeDocument = {
        ...doc,
        isLogram,
        subView: isLogram ? 'logram' : 'inspector',
        inspectorOpened: !isLogram,
        logramOpened: isLogram,
        store: isLogram ? null : new CombinedViewStore([doc.stateStore, doc.manifestStore, doc.childrenStore]),
      };
    }
    catch(error) {
      console.warn('req.open (auto) failed', path, error);
    }
  }

  // Creates fresh per-document stores for the Inspector's State, Manifest and
  // Children root trees (mirrors #openCatalog's per-navigation ViewStore) - one
  // ViewStore per view so each keeps receiving only its own evnt.* packets (see
  // #onMessage), combined into a single list for rendering via CombinedViewStore -
  // the array order (state, manifest, children) fixes their on-screen order,
  // matching ES's InspectorForm (state -> Manifest -> children in one ListView).
  // Always builds fresh stores, even when re-opening the same path after a
  // toggle-away - the backend's TopicTreeController instance from the previous
  // open was disposed on close and loses its diff state, so a stale local store
  // could be left with rows that no longer exist server-side and never get an
  // evnt.del to clear them.
  #openInspectorPanes(path) {
    const doc = this.activeDocument;
    if(!doc || doc.path !== path || doc.inspectorOpened || !this.api) return;

    const stateVid = `inspstate#${path}`;
    const stateStore = new ViewStore();
    stateStore.add({
      vid: stateVid,
      level: 0,
      expander: 1,
      icon: '/ide_icons/ty_topic.png',
      name: 'State',
      editor: 'Default',
      value: null,
      readonly: true,
    });

    const manifestVid = `inspmanifest#${path}`;
    const manifestStore = new ViewStore();
    manifestStore.add({
      vid: manifestVid,
      level: 0,
      expander: 1,
      icon: '/ide_icons/ty_obj.png',
      name: 'Manifest',
      editor: 'Default',
      value: null,
      readonly: true,
    });

    const childrenVid = `inspchildren#${path}`;
    const childrenStore = new ViewStore();
    childrenStore.add({
      vid: childrenVid,
      level: 0,
      expander: 1,
      icon: '/ide_icons/ty_topic.png',
      name: 'Children',
      editor: 'Default',
      value: null,
      readonly: true,
    });

    this.activeDocument = {
      ...doc,
      stateStore,
      manifestStore,
      childrenStore,
      store: new CombinedViewStore([stateStore, manifestStore, childrenStore]),
      inspectorOpened: true,
    };
    this.api.expand(stateVid, true).catch((error) => console.warn('inspector initial req.expand failed', stateVid, error));
    this.api.expand(manifestVid, true).catch((error) => console.warn('inspector initial req.expand failed', manifestVid, error));
    this.api.expand(childrenVid, true).catch((error) => console.warn('inspector initial req.expand failed', childrenVid, error));
  }

  #closeInspectorPanes(doc) {
    if(!doc?.inspectorOpened || !this.api) return;
    const previousStateVid = `inspstate#${doc.path}`;
    const previousManifestVid = `inspmanifest#${doc.path}`;
    const previousChildrenVid = `inspchildren#${doc.path}`;
    this.api.close(previousStateVid).catch((error) => console.warn('req.close failed', previousStateVid, error));
    this.api.close(previousManifestVid).catch((error) => console.warn('req.close failed', previousManifestVid, error));
    this.api.close(previousChildrenVid).catch((error) => console.warn('req.close failed', previousChildrenVid, error));
  }

  #openLogram(path) {
    const doc = this.activeDocument;
    if(!doc || doc.path !== path || doc.logramOpened || !this.api) return;
    doc.logramOpened = true;
    this.api.open(`logram#${path}`, 'logram').catch(() => {
      if(this.activeDocument === doc) doc.logramOpened = false;
    });
  }

  // Mirrors #closeInspectorPanes - takes the document explicitly (rather than
  // always reading this.activeDocument) so callers can close what's being
  // navigated AWAY FROM after this.activeDocument has already been reassigned.
  #closeLogram(doc) {
    if(!doc?.logramOpened || !this.api) return;
    this.api.close(`logram#${doc.path}`).catch((error) => console.warn('req.close failed', `logram#${doc.path}`, error));
  }

  // Safety net, not the primary path anymore: #openAuto resolves Core/Logram before
  // ever opening Inspector, so the auto-switch branch below only fires if Inspector
  // was opened with an isLogram:false hint that turns out to be stale. Under
  // forceMode:'inspector' (?mode=inspector, or the toggle's forced override) the
  // view must stay put - but isLogram itself still needs to become true so
  // inspector-document.js's Inspector/Logram toggle button shows up at all; without
  // this, ?mode=inspector on a Logram-typed topic would land with no way back to
  // Logram short of editing the URL by hand.
  #maybeAdoptLogramHint(vid, message) {
    const doc = this.activeDocument;
    if(!doc || doc.view !== 'inspector' || doc.isLogram) return;
    if(vid !== `inspchildren#${doc.path}` || !message.isLogram) return;
    if(doc.forceMode) {
      this.activeDocument = { ...doc, isLogram: true };
      return;
    }
    this.#closeInspectorPanes(doc);
    this.activeDocument = { ...doc, isLogram: true, subView: 'logram', inspectorOpened: false };
    this.#openLogram(doc.path);
  }

  #requestDocument(restore) {
    if(this.status !== 'connected' || !this.api) {
      this.pendingRestore = restore;
      return;
    }
    this.#applyRestore(restore);
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
    if(message.type === 'evnt.log') {
      this.logStore.applyLog(message);
      return;
    }
    if(message.type === 'evnt.add' || message.type === 'evnt.upd' || message.type === 'evnt.del') {
      const vid = String(message.vid || '');
      if(vid.startsWith('catalog#')) this.activeDocument?.store?.applyPacket(message);
      else if(vid.startsWith('inspstate#')) this.activeDocument?.stateStore?.applyPacket(message);
      else if(vid.startsWith('inspmanifest#')) this.activeDocument?.manifestStore?.applyPacket(message);
      else if(vid.startsWith('inspchildren#')) {
        this.activeDocument?.childrenStore?.applyPacket(message);
        if(message.type !== 'evnt.del') this.#maybeAdoptLogramHint(vid, message);
      }
      else if(vid.startsWith('logram#')) this.activeDocument?.logramStore?.applyPacket(message);
      else this.store.applyPacket(message);
      if(message.type === 'evnt.del') this.#onDocumentRootDeleted(vid);
    }
  }

  // The topic backing the open Inspector document was deleted elsewhere (e.g. from
  // Workspace) - its State/Manifest/Children controller detected the removal of its
  // own root and pushed evnt.del for that root vid (see TopicTreeController /
  // StateTreeController / ManifestTreeController's HandleRootRemoved), rather than
  // just a row disappearing from within an already-open tree. Navigate to the parent
  // topic instead of leaving the document open on something that no longer exists.
  // All three controllers detect the same deletion independently and each send their
  // own evnt.del, but only the first one still matches activeDocument.path - once
  // #navigateInspector below reassigns it to the parent, the other two no longer do,
  // so this naturally fires exactly once without extra guard bookkeeping.
  #onDocumentRootDeleted(vid) {
    if(this.activeDocument?.view !== 'inspector') return;
    const path = this.activeDocument.path;
    const isOwnRoot = vid === `inspstate#${path}` || vid === `inspmanifest#${path}` || vid === `inspchildren#${path}` || vid === `logram#${path}`;
    if(!isOwnRoot) return;
    const parentPath = path === '/' ? '/' : (path.split('/').slice(0, -1).join('/') || '/');
    this.#navigateInspector(parentPath, false);
  }


  #renderContent() {
    if(this.activeDocument?.view === 'catalog') {
      return html`<x13-catalog-document .api=${this.api} .document=${this.activeDocument} .store=${this.activeDocument.store}></x13-catalog-document>`;
    }
    if(this.activeDocument?.view === 'inspector') {
      return html`<x13-inspector-document .path=${this.activeDocument.path} .rootName=${this.serverName}
        .api=${this.api} .store=${this.activeDocument.store}
        .isLogram=${this.activeDocument.isLogram} .subView=${this.activeDocument.subView} .logramStore=${this.activeDocument.logramStore}
        @segment-command=${this.#onSegmentCommand} @view-toggle=${this.#toggleDocumentView}></x13-inspector-document>`;
    }
    if(this.activeDocument?.view === 'chart') {
      return html`<x13-chart-document .path=${this.activeDocument.path} .rootName=${this.serverName}
        @segment-command=${this.#onSegmentCommand}></x13-chart-document>`;
    }
    if(this.pendingRestore) return html``;
    return html`<x13-logo-document></x13-logo-document>`;
  }

  async #onWorkspaceMenuCommand(e) {
    const row = e.detail?.row;
    const cmd = e.detail?.cmd;
    if(!row?.vid) return;
    if(cmd === 'open' || cmd === 'open-tab') {
      this.#navigateInspector(getTopicPath(row.vid), cmd === 'open-tab', !!row.isLogram);
      return;
    }
    if(cmd === 'chart') {
      this.#openChart(getTopicPath(row.vid));
      return;
    }
    if(cmd === 'catalog') this.#openCatalog(row.vid);
  }

  // Toggling now behaves like navigating to the same path with the opposite
  // isLogram hint: close whichever side is open, open the other - matches
  // #setInspectorDocument in never keeping both sides loaded at once. This trades
  // an instant flip for one more round trip per toggle, in exchange for not paying
  // for the unseen side's data (and live subscription) for as long as the document
  // stays open. Switching to Inspector here is always the *forced* override (the
  // toggle only ever shows on a Logram-typed doc, whose resolved default is Logram -
  // see inspector-document.js render), so it's recorded as forceMode:'inspector' and
  // mirrored into the URL (?mode=inspector) via replaceState, same shape #navigateInspector
  // uses - a reload or shared link then reopens on Inspector instead of re-resolving
  // back to Logram. Switching back to Logram clears the override, restoring the
  // resolved default.
  #toggleDocumentView() {
    const doc = this.activeDocument;
    if(doc?.view !== 'inspector') return;
    if(doc.subView === 'logram') {
      this.#closeLogram(doc);
      const forceMode = 'inspector';
      this.activeDocument = { ...doc, subView: 'inspector', logramOpened: false, forceMode };
      this.#openInspectorPanes(doc.path);
      history.replaceState({ inspectorPath: doc.path, forceMode }, '', pathToDocumentUrl(doc.path, forceMode));
    }
    else {
      this.#closeInspectorPanes(doc);
      const forceMode = null;
      this.activeDocument = { ...doc, subView: 'logram', inspectorOpened: false, logramStore: new ViewStore(), forceMode };
      this.#openLogram(doc.path);
      history.replaceState({ inspectorPath: doc.path, forceMode }, '', pathToDocumentUrl(doc.path, forceMode));
    }
  }

  async #openCatalog(vid, { pushHistory = true } = {}) {
    if(!this.api) return;
    if(this.activeDocument?.view === 'inspector') {
      this.#closeInspectorPanes(this.activeDocument);
      this.#closeLogram(this.activeDocument);
    }
    try {
      const document = await this.api.open(vid, 'catalog');
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
      if(pushHistory) history.pushState({ mode: 'catalog' }, '', '/ide.html/?mode=catalog');
      this.api.expand(document.vid || 'catalog#/', true)
        .catch((error) => console.warn('catalog initial req.expand failed', document.vid, error));
    }
    catch(error) {
      console.warn('req.open catalog failed', vid, error);
    }
  }

  // The Chart document has no server side at all: its data comes over HTTP from
  // /api/archivist, carrying the session token this.api's handshake already handed us
  // (ide_services/api-token.js), so there is no req.open to make and nothing to close
  // afterwards - which is also why #onDocumentRootDeleted has no chart case. Closing the
  // previous document's panes is still ours to do, exactly as #openCatalog does it.
  #openChart(path) {
    if(!path) return;
    this.#setChartDocument(path);
    history.pushState({ chartPath: path }, '', pathToDocumentUrl(path, 'chart'));
  }

  #setChartDocument(path) {
    if(this.activeDocument?.view === 'inspector') {
      this.#closeInspectorPanes(this.activeDocument);
      this.#closeLogram(this.activeDocument);
    }
    this.activeDocument = { view: 'chart', path };
  }

  #onSegmentCommand(e) {
    const { cmd, path, isLogram } = e.detail || {};
    if(!path) return;
    if(cmd === 'copy-path') {
      navigator.clipboard.writeText(path).catch((error) => console.warn('clipboard write failed', error));
      return;
    }
    // From a Logram pin's "Show in Workspace" (logram-document.js #showInWorkspace) -
    // a new object every time (even for the same path) so x13-view-workspace's
    // updated() sees a change and re-reveals, e.g. after the row scrolled out of view.
    if(cmd === 'show-in-workspace') {
      this.workspaceReveal = { path, token: Date.now() };
      return;
    }
    // isLogram arrives as undefined for ancestor segments (see inspector-document.js
    // #emit) - pass it through as-is rather than coercing to false, so #navigateInspector
    // can tell "known not a Logram" apart from "don't know yet" and route the latter
    // through #openAuto instead of guessing Inspector.
    this.#navigateInspector(path, cmd === 'open-tab', isLogram);
  }

  #navigateInspector(path, newTab, isLogram, forceMode = null) {
    const url = pathToDocumentUrl(path, forceMode);
    if(newTab) {
      window.open(url, '_blank');
      return;
    }
    this.#setInspectorDocument(path, isLogram, forceMode);
    history.pushState({ inspectorPath: path, forceMode }, '', url);
  }

  #restoreFromLocation() {
    const mode = new URLSearchParams(location.search).get('mode');
    if(mode === 'catalog') {
      this.pendingRestore = { view: 'catalog' };
      history.replaceState({ mode: 'catalog' }, '', '/ide.html/?mode=catalog');
      return;
    }
    const path = topicPathFromLocation(location.pathname);
    if(!path) return;
    if(mode === 'chart') {
      this.pendingRestore = { view: 'chart', path };
      history.replaceState({ chartPath: path }, '', location.pathname + location.search);
      return;
    }
    const forceMode = mode === 'inspector' ? 'inspector' : null;
    this.pendingRestore = { view: 'inspector', path, forceMode };
    history.replaceState({ inspectorPath: path, forceMode }, '', location.pathname + location.search);
  }

  #onPopState(e) {
    if(e.state?.inspectorPath) {
      this.#requestDocument({ view: 'inspector', path: e.state.inspectorPath, forceMode: e.state.forceMode || null });
      return;
    }
    if(e.state?.chartPath) {
      this.#requestDocument({ view: 'chart', path: e.state.chartPath });
      return;
    }
    if(e.state?.mode === 'catalog') {
      this.#requestDocument({ view: 'catalog' });
      return;
    }
    this.pendingRestore = null;
    this.activeDocument = null;
  }

  #onEditorCommit(e) {
    const vid = e.detail?.vid;
    if(!vid || !this.api) return;
    e.detail.promise = this.api.commit(vid, e.detail.value).catch((error) => {
      console.warn('req.commit failed', vid, error);
      throw error;
    });
  }

  // Mirrors #onEditorCommit for editors that need to trigger a req.rpc command
  // against their own row instead of (or alongside) committing a value - e.g. the
  // DevicePLC editor's inline Build/Run/Start/Stop buttons (view:'value' only).
  #onEditorRpc(e) {
    const { vid, cmd } = e.detail || {};
    if(!vid || !cmd || !this.api) return;
    e.detail.promise = this.api.rpc(vid, cmd).catch((error) => {
      console.warn('req.rpc failed', vid, cmd, error);
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

  #startLogResize(e) {
    if(this.logCollapsed) return;
    e.preventDefault();
    const startY = e.clientY;
    const startHeight = this.logHeight;
    const move = (ev) => {
      this.logHeight = clamp(startHeight - (ev.clientY - startY), MIN_LOG_HEIGHT, MAX_LOG_HEIGHT);
    };
    const up = () => {
      localStorage.setItem(LOG_HEIGHT_KEY, String(this.logHeight));
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  #toggleLogCollapsed() {
    this.logCollapsed = !this.logCollapsed;
    localStorage.setItem(LOG_COLLAPSED_KEY, String(this.logCollapsed));
  }
}

customElements.define('x13-app-shell', X13AppShell);

// The URL of a document rooted on a topic: /ide.html/<path>, plus ?mode= for the cases the path
// alone cannot tell apart. 'inspector' pins the Inspector view of a Logram-typed topic, whose
// resolved default would be Logram; 'chart' names a different document on the same topic.
function pathToDocumentUrl(path, mode = null) {
  const segments = String(path || '/').split('/').filter(Boolean).map(encodeURIComponent);
  const base = segments.length ? `/ide.html/${segments.join('/')}` : '/ide.html/';
  return mode === 'inspector' || mode === 'chart' ? `${base}?mode=${mode}` : base;
}

function topicPathFromLocation(pathname) {
  const prefix = '/ide.html/';
  if(!String(pathname || '').startsWith(prefix)) return null;
  const rest = pathname.slice(prefix.length);
  const segments = rest.split('/').filter(Boolean).map(decodeURIComponent);
  return segments.length ? `/${segments.join('/')}` : '/';
}
