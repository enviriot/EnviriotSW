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
import { ViewStore, CombinedViewStore } from '../services/view-store.js';
import { LogStore } from '../services/log-store.js';
import { clamp, readPositiveNumber, readBoolean } from '../services/local-storage-utils.js';
import { getTopicPath, getView } from '../services/vid-helper.js';
import { setApiToken } from '../services/api-token.js';

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

// The three server-side views an Inspector document opens as one - each with its own controller
// and its own evnt.* stream, always opened and closed together. Table-driven because opening,
// closing, packet routing and the root-deleted check below all need these same three vids, and
// each of them used to spell the trio out for itself.
const INSPECTOR_PANES = ['inspstate', 'inspmanifest', 'inspchildren'];
// vid-views whose rows belong to the active document rather than to the Workspace tree. NOT
// document types, which is a different namespace: 'logram' is one side of an Inspector document,
// while 'catalog' and 'chart' happen to name a document and a view alike. A vid naming anything
// not listed here still falls through to the Workspace store, as it always has.
const DOCUMENT_VID_VIEWS = new Set([...INSPECTOR_PANES, 'logram', 'catalog', 'chart']);

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
    // Bumped by every document swap, so an auto-resolve still in flight can tell that its answer
    // is no longer wanted - see #openAuto.
    this.navToken = 0;
    // An auto-resolve is out and nothing is on screen yet. Only matters on a cold start, where
    // there is no previous document to keep showing and the logo would otherwise flash.
    this.navPending = false;
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

  // A descriptor is the one shape a URL, a history entry and a pending restore all reduce to:
  // {mode, path}, where mode names the document to open and null means "ask the server which one
  // this topic opens as". Opening one is the only way any of the three becomes a document.
  #applyRestore(restore) {
    if(!restore) return;
    if(restore.mode === 'catalog') {
      this.#openCatalog(ROOT_VID, 'none');
      return;
    }
    // A mode that survived into the URL was named outright by whoever wrote it - that is the only
    // reason it is there rather than being left to the server's own resolve.
    this.#navigate(restore.path, restore.mode, { requested: restore.mode !== null, history: 'none' });
  }

  // The one place a document is swapped. Closing whatever the previous one had open belongs here
  // rather than at each call site (it used to be three verbatim copies, plus a fourth inlined in
  // #openAuto), and so does putting the URL and history entry in step with what is now on screen.
  //
  // 'none' is for a swap that must not add to the history: restoring the document the URL already
  // names, and #adoptAltView's correction, which lands on the same address anyway. Every
  // other swap - including the bar's switch button - is a place you can go Back from.
  //
  // Only for swaps - installing a different document object whose open panes differ. Changing a
  // field of the document in place does NOT go through here (it would close the panes that
  // document still has open); see #openInspectorPanes and the reactivity note on #openLogram.
  #setDocument(doc, historyMode = 'push') {
    this.navToken++;
    this.navPending = false;
    this.#closeDocument(this.activeDocument);
    this.activeDocument = doc;
    if(historyMode !== 'push') return;
    const descriptor = documentDescriptor(doc);
    history.pushState(descriptorState(descriptor), '', descriptorUrl(descriptor));
  }

  // Closes exactly what the document actually had open, reading its own flags - so a caller can
  // hand it the document being navigated AWAY FROM after this.activeDocument was reassigned. This
  // is what makes Inspector's expand state document-scoped rather than session-scoped like
  // Workspace's: the backend controller feeding those panes is disposed along with the document.
  #closeDocument(doc) {
    if(!doc || !this.api) return;
    if(doc.inspectorOpened) for(const pane of INSPECTOR_PANES) this.#closeVid(`${pane}#${doc.path}`);
    if(doc.logramOpened) this.#closeVid(`logram#${doc.path}`);
    if(doc.chartOpened) this.#closeVid(`chart#${doc.path}`);
  }

  #closeVid(vid) {
    this.api.close(vid).catch((error) => console.warn('req.close failed', vid, error));
  }

  // Every navigation to a topic goes through here. mode names the document to open - 'inspector',
  // 'logram', 'chart' - or is null for "let the server decide", which #openAuto asks outright
  // (see ViewSession.HandleOpenAuto) and which is the normal case: opening from Workspace, a
  // breadcrumb click to an ancestor, parent-on-delete, a bare URL. A non-null mode comes only
  // from somewhere that genuinely knows better than the resolve would - ?mode= in the URL, or
  // the bar's switch button naming the view you are switching TO.
  //
  // `requested` marks a mode the user named outright, as both of those do. It is what keeps
  // #adoptAltView from overruling a deliberate choice of Inspector on a Logram-typed topic, and
  // what eventually pins that choice into the URL - see documentDescriptor for why only that one
  // combination is expressible there at all.
  //
  // `altView` carries over what the caller already knows about the topic's other view - only the
  // switch button does, by standing on that other view. Undefined means "nobody knows yet",
  // which is not the same as null, "this topic has none".
  #navigate(path, mode, { requested = false, altView = undefined, history: historyMode = 'push' } = {}) {
    if(!path) return;
    if(mode === 'chart') {
      this.#setChartDocument(path, historyMode);
      return;
    }
    if(mode === 'logram') {
      this.#setLogramDocument(path, historyMode);
      return;
    }
    if(mode === 'inspector') {
      this.#setInspectorDocument(path, {
        requestedMode: requested ? 'inspector' : null,
        altView,
      }, historyMode);
      return;
    }
    this.#openAuto(path, historyMode);
  }

  // A new tab gets the plain topic URL and resolves the document for itself - the mode the click
  // happened to carry is deliberately dropped, so a link opened this way behaves like any other
  // bare topic address.
  #openInNewTab(path) {
    if(!path) return;
    window.open(descriptorUrl({ mode: null, path }), '_blank');
  }

  // Asks the server which document this topic is and installs that one - see
  // ViewSession.HandleOpenAuto, which resolves Core/Logram from one field read and answers BEFORE
  // opening the side it resolved to. That order is what lets this wait: the answer names the
  // document, so the right one can be built and every row of it lands there, with no half-open
  // document standing in for an answer that has not arrived.
  //
  // Nothing is swapped until then, so the previous document stays on screen for the one round trip
  // rather than being replaced by an empty stand-in - and stays live, since it is #setDocument
  // below that closes it.
  async #openAuto(path, historyMode) {
    if(!this.api) return;
    const token = ++this.navToken;
    // Only visible on a cold start; every other navigation has a document to keep showing.
    this.navPending = !this.activeDocument;
    this.requestUpdate();
    try {
      const result = await this.api.open(path, null);
      // Navigated somewhere else while the answer was in flight. The server has opened this side
      // by now, or is about to, and nothing on screen refers to it - close it directly, since no
      // document was ever installed to carry the flags #closeDocument reads.
      if(token !== this.navToken) {
        if(result?.view === 'logram') this.#closeVid(`logram#${path}`);
        else for(const pane of INSPECTOR_PANES) this.#closeVid(`${pane}#${path}`);
        return;
      }
      if(result?.view === 'logram') this.#setLogramDocument(path, historyMode);
      else this.#setInspectorDocument(path, { requestedMode: null, altView: undefined }, historyMode);
    }
    catch(error) {
      console.warn('req.open (auto) failed', path, error);
      if(token === this.navToken) {
        this.navPending = false;
        this.requestUpdate();
      }
    }
  }

  // altView is the topic's other view, as the server names it on the document's own root row
  // ("logram", "chart" or nothing) - it decides which button the bar offers, and, for "logram",
  // whether this document is expressible in the URL at all (see documentDescriptor). It normally
  // stays undefined until that root row arrives and #adoptAltView picks it up; only the switch
  // button, which stands on the other view already, can hand it over up front.
  #setInspectorDocument(path, { requestedMode, altView }, historyMode = 'push') {
    const stores = {};
    for(const pane of INSPECTOR_PANES) stores[pane] = new ViewStore();
    this.#setDocument({
      view: 'inspector',
      path,
      requestedMode,
      stores,
      store: combinedInspectorStore(stores),
      altView,
      inspectorOpened: false,
      logramOpened: false,
    }, historyMode);
    this.#openInspectorPanes(path);
  }

  #setLogramDocument(path, historyMode = 'push') {
    const store = new ViewStore();
    this.#setDocument({
      view: 'logram',
      path,
      stores: { logram: store },
      store,
      inspectorOpened: false,
      logramOpened: false,
    }, historyMode);
    this.#openLogram(path);
  }

  // The State, Manifest and Children roots are three separate server-side views opened as one.
  // Their header rows are not synthesised here: the first req.expand on a root the session has not
  // seen creates the controller, and creating it sends the root row (TreeViewProviderBase
  // .GetOrCreateOwner -> SendControllerRoot), so the client would only be drawing a placeholder for
  // the one round trip until the real row replaced it - which the auto path above never did.
  //
  // Sets inspectorOpened in place rather than replacing the document: nothing #renderContent reads
  // changes here, and #closeDocument reads the flag off whatever object is current by then.
  #openInspectorPanes(path) {
    const doc = this.activeDocument;
    if(!doc || doc.path !== path || doc.inspectorOpened || !this.api) return;
    doc.inspectorOpened = true;
    for(const pane of INSPECTOR_PANES) {
      const vid = `${pane}#${path}`;
      this.api.expand(vid, true).catch((error) => console.warn('inspector initial req.expand failed', vid, error));
    }
  }

  #openLogram(path) {
    const doc = this.activeDocument;
    if(!doc || doc.path !== path || doc.logramOpened || !this.api) return;
    doc.logramOpened = true;
    this.api.open(`logram#${path}`, 'logram').catch(() => {
      if(this.activeDocument === doc) doc.logramOpened = false;
    });
  }

  // The Children root row is the open document's own topic, and the only row that carries
  // altView - so this is where the document normally learns what else that topic can be shown
  // as. It also keeps arriving: the server re-sends the row when the topic's fields change, so
  // turning archiving on or off, or retyping a topic to Core/Logram, takes the button with it
  // while the document stays open.
  //
  // Finding out the topic is Logram-typed is the one case that can mean the WRONG document is
  // open: unless Inspector was asked for outright, the server's own resolve would have opened
  // Logram, so correcting it is an ordinary swap to that document - which is the whole point of
  // Logram being a document rather than a sub-view: the correction is a navigation, not a mutation
  // of something already on screen.
  //
  // Under a requested 'inspector' the document stays put, and this is also the moment its address
  // becomes expressible: until the Logram side is known to exist, an Inspector document is just
  // what a bare topic URL opens, so only now does ?mode=inspector have to be written into it (see
  // documentDescriptor).
  #adoptAltView(vid, message) {
    const doc = this.activeDocument;
    if(!doc || doc.view !== 'inspector' || vid !== `inspchildren#${doc.path}`) return;
    // Only when the packet actually carries the key: an evnt.upd names just what changed, so a
    // row updated for some other reason must not be read as "this topic has no other view".
    if(!Object.prototype.hasOwnProperty.call(message, 'altView')) return;
    const altView = message.altView || null;
    if(altView === doc.altView) return;
    if(altView === 'logram' && doc.requestedMode !== 'inspector') {
      this.#setLogramDocument(doc.path, 'none');
      return;
    }
    const adopted = { ...doc, altView };
    this.activeDocument = adopted;
    const descriptor = documentDescriptor(adopted);
    history.replaceState(descriptorState(descriptor), '', descriptorUrl(descriptor));
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
    const vid = String(message.vid || '');
    const view = getView(vid);
    // Each of the document's trees keeps receiving only its own packets, which is what lets
    // CombinedViewStore render several of them as one list without them interfering.
    if(DOCUMENT_VID_VIEWS.has(view)) this.activeDocument?.stores?.[view]?.applyPacket(message);
    else this.store.applyPacket(message);
    if(view === 'inspchildren' && message.type !== 'evnt.del') this.#adoptAltView(vid, message);
    if(message.type === 'evnt.del') this.#onDocumentRootDeleted(vid);
  }

  // The topic backing the open document was deleted elsewhere (e.g. from Workspace) - its
  // controller detected the removal of its own root and pushed evnt.del for that root vid (see
  // TopicTreeController / StateTreeController / ManifestTreeController's HandleRootRemoved),
  // rather than just a row disappearing from within an already-open tree. Navigate to the parent
  // topic instead of leaving the document open on something that no longer exists. An Inspector
  // document's three controllers detect the same deletion independently and each send their own
  // evnt.del, but only the first one still matches the open document's path - once #navigate below
  // reassigns it to the parent, the other two no longer do, so this fires exactly once without
  // extra guard bookkeeping.
  #onDocumentRootDeleted(vid) {
    const doc = this.activeDocument;
    if(doc?.view !== 'inspector' && doc?.view !== 'logram' && doc?.view !== 'chart') return;
    // Exact vid equality, not the vid's topic path: a field row (inspstate#/A/B#f) names the very
    // same topic as the root it hangs under, and its removal is not the document's removal.
    const isOwnRoot = [...INSPECTOR_PANES, 'logram', 'chart'].some((view) => vid === `${view}#${doc.path}`);
    if(!isOwnRoot) return;
    const parentPath = doc.path === '/' ? '/' : (doc.path.split('/').slice(0, -1).join('/') || '/');
    this.#navigate(parentPath, null);
  }


  #renderContent() {
    const doc = this.activeDocument;
    if(doc?.view === 'catalog') {
      return html`<x13-catalog-document .api=${this.api} .document=${doc} .store=${doc.store}></x13-catalog-document>`;
    }
    if(doc?.view === 'inspector') {
      return html`<x13-inspector-document .path=${doc.path} .rootName=${this.serverName}
        .api=${this.api} .store=${doc.store} .altView=${doc.altView || null}
        @segment-command=${this.#onSegmentCommand}></x13-inspector-document>`;
    }
    if(doc?.view === 'logram') {
      return html`<x13-logram-document .path=${doc.path} .rootName=${this.serverName}
        .api=${this.api} .store=${doc.store}
        @segment-command=${this.#onSegmentCommand}></x13-logram-document>`;
    }
    if(doc?.view === 'chart') {
      return html`<x13-chart-document .path=${doc.path} .rootName=${this.serverName} .store=${doc.stores.chart}
        @segment-command=${this.#onSegmentCommand}></x13-chart-document>`;
    }
    // Nothing to show yet, but something is coming: the URL named a document and the session is
    // still connecting (pendingRestore), or its resolve is in flight (navPending). The logo is
    // for an empty shell, not for a document that is one round trip away.
    if(this.pendingRestore || this.navPending) return html``;
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
      this.#navigate(getTopicPath(row.vid), null);
      return;
    }
    if(cmd === 'open-tab') {
      this.#openInNewTab(getTopicPath(row.vid));
      return;
    }
    if(cmd === 'chart') {
      this.#navigate(getTopicPath(row.vid), 'chart');
      return;
    }
    if(cmd === 'catalog') this.#openCatalog(row.vid);
  }

  async #openCatalog(vid, historyMode = 'push') {
    if(!this.api) return;
    try {
      const opened = await this.api.open(vid, 'catalog');
      const rootVid = opened.vid || 'catalog#/';
      const store = new ViewStore();
      store.add({
        vid: rootVid,
        level: 0,
        expander: 1,
        icon: '/ide/icons/ty_topic.png',
        name: opened.title || 'Catalog',
        editor: 'Default',
        value: opened.data?.uri || '',
        readonly: true,
        info: opened.data?.uri || '',
        downloadEnabled: false,
        removeEnabled: false,
      });
      this.#setDocument({
        view: 'catalog',
        vid: rootVid,
        title: opened.title,
        data: opened.data,
        stores: { catalog: store },
        store,
      }, historyMode);
      this.api.expand(rootVid, true)
        .catch((error) => console.warn('catalog initial req.expand failed', rootVid, error));
    }
    catch(error) {
      console.warn('req.open catalog failed', vid, error);
    }
  }

  // The Chart document draws from two sources, and they answer different questions. Its history
  // comes over HTTP from /api/archivist, carrying the session token this.api's handshake already
  // handed us (ide/services/api-token.js). What has happened since that answer comes from the
  // `chart#` view opened here - one subscription to the topic's value (ChartViewProvider), whose
  // evnt.* land in stores.chart like any other document's rows.
  #setChartDocument(path, historyMode = 'push') {
    if(!path) return;
    const doc = { view: 'chart', path, stores: { chart: new ViewStore() }, chartOpened: true };
    this.#setDocument(doc, historyMode);
    this.api?.open(`chart#${path}`, 'chart').catch((error) => {
      console.warn('req.open chart failed', path, error);
      // The archive half still works without it; only the live tail is lost, and saying so here
      // keeps #closeDocument from closing a view the server never opened.
      if(this.activeDocument === doc) doc.chartOpened = false;
    });
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
      this.#openInNewTab(path);
      return;
    }
    // The bar's switch button: the other view of the topic already on screen. A named choice, so
    // it survives into the URL where it has to (see documentDescriptor), and an ordinary history
    // entry, so Back returns to the view you switched away from.
    //
    // Standing on the Logram document is itself proof that the topic has a Logram side, which is
    // the one thing the Inspector document it opens could not otherwise know until inspchildren
    // says so - see #adoptAltView.
    if(cmd === 'switch-view') {
      this.#navigate(path, mode, { requested: true, altView: this.activeDocument?.view === 'logram' ? 'logram' : undefined });
      return;
    }
    // mode arrives as undefined for ancestor segments (see breadcrumb-bar.js #emit) - pass it
    // through as-is rather than defaulting to Inspector, so #navigate can tell "known" apart from
    // "don't know yet" and route the latter through #openAuto's resolve.
    this.#navigate(path, mode === undefined ? null : mode);
  }

  // Runs before the connection exists, so it only records what to open and normalises the address
  // bar; #onConnected hands the descriptor to #applyRestore once there is a session to open it in.
  #restoreFromLocation() {
    const descriptor = descriptorFromLocation();
    if(!descriptor) return;
    this.pendingRestore = descriptor;
    history.replaceState(descriptorState(descriptor), '', descriptorUrl(descriptor));
  }

  #onPopState(e) {
    const descriptor = descriptorOfState(e.state);
    if(descriptor) {
      this.#requestDocument(descriptor);
      return;
    }
    // Back past the first document of this tab: nothing to show, and nothing should stay open
    // server-side for it either.
    this.pendingRestore = null;
    this.#closeDocument(this.activeDocument);
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

// The pane stores rendered as one list. The array order fixes their on-screen order (state ->
// Manifest -> children), matching ES's InspectorForm, which showed all three in one ListView.
function combinedInspectorStore(stores) {
  return new CombinedViewStore(INSPECTOR_PANES.map((pane) => stores[pane]));
}

// A descriptor - {mode, path} - is the single form URL, history entry and open document all
// convert to and from, so the address bar and what is on screen cannot drift apart: #setDocument
// writes both from the document it installs, and page load / popstate read them back into the very
// same shape #applyRestore opens.
//
// mode null means "whatever this topic opens as" and is therefore what a bare topic URL carries.
// Logram maps to it: a Logram document is what the server resolves a Logram-typed topic to, so
// pinning ?mode=logram would only restate the default. Inspector maps to it too, and keeps mapping
// to it until BOTH halves of the exception hold - it was asked for outright, and the topic really
// does have a Logram side for the resolve to have preferred. Until the second half is known, an
// Inspector document is simply what a bare topic URL opens, and saying ?mode=inspector would put a
// pin in the address of every ordinary topic anyone ever switched to Inspector from a Chart.
// #adoptAltView is what learns the second half, and rewrites the address when it does.
function documentDescriptor(doc) {
  if(doc.view === 'catalog') return { mode: 'catalog' };
  if(doc.view === 'chart') return { mode: 'chart', path: doc.path };
  if(doc.view === 'inspector' && doc.requestedMode === 'inspector' && doc.altView === 'logram') return { mode: 'inspector', path: doc.path };
  return { mode: null, path: doc.path };
}

// The state SHAPE is deliberately the one earlier builds wrote: history.state survives a reload,
// so a tab opened before Logram became a document of its own still has to go Back correctly.
function descriptorState(descriptor) {
  if(descriptor.mode === 'catalog') return { mode: 'catalog' };
  if(descriptor.mode === 'chart') return { chartPath: descriptor.path };
  return { inspectorPath: descriptor.path, forceMode: descriptor.mode === 'inspector' ? 'inspector' : null };
}

function descriptorOfState(state) {
  if(state?.chartPath) return { mode: 'chart', path: state.chartPath };
  if(state?.mode === 'catalog') return { mode: 'catalog' };
  if(state?.inspectorPath) return { mode: state.forceMode === 'inspector' ? 'inspector' : null, path: state.inspectorPath };
  return null;
}

// /ide.html/<path>, plus ?mode= for what the path alone cannot say. Catalog is rooted on no topic
// and is named by ?mode= alone.
function descriptorUrl(descriptor) {
  if(descriptor.mode === 'catalog') return '/ide.html/?mode=catalog';
  const segments = String(descriptor.path || '/').split('/').filter(Boolean).map(encodeURIComponent);
  const base = segments.length ? `/ide.html/${segments.join('/')}` : '/ide.html/';
  return descriptor.mode ? `${base}?mode=${descriptor.mode}` : base;
}

function descriptorFromLocation() {
  const mode = new URLSearchParams(location.search).get('mode');
  if(mode === 'catalog') return { mode: 'catalog' };
  const path = topicPathFromLocation(location.pathname);
  if(!path) return null;
  if(mode === 'chart') return { mode: 'chart', path };
  return { mode: mode === 'inspector' ? 'inspector' : null, path };
}

function topicPathFromLocation(pathname) {
  const prefix = '/ide.html/';
  if(!String(pathname || '').startsWith(prefix)) return null;
  const rest = pathname.slice(prefix.length);
  const segments = rest.split('/').filter(Boolean).map(decodeURIComponent);
  return segments.length ? `/${segments.join('/')}` : '/';
}
