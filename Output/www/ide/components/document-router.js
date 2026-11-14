// Какой документ на экране и как он связан с адресом и историей браузера: навигация, разрешение
// топика в документ, popstate, восстановление по адресу и после переподключения. Серверную сторону
// документа - его view и пакеты - ведёт DocumentSession. ReactiveController: документ рисует хост,
// а перерисовку при смене документа просит роутер.
import { documentDescriptor, reopenDescriptor, descriptorState, descriptorOfState, descriptorUrl, descriptorFromLocation } from './document-descriptor.js';
import { WORKSPACE_ROOT_VID } from '../services/vid-helper.js';

export class DocumentRouter {
  // isReady - есть ли сессия, в которой документ можно открыть прямо сейчас.
  constructor(host, session, isReady) {
    this.host = host;
    this.session = session;
    this.#isReady = isReady;
    this.#document = null;
    this.pendingRestore = null;
    // Bumped by every document swap, so an auto-resolve still in flight can tell that its answer
    // is no longer wanted - see #openAuto.
    this.navToken = 0;
    // An auto-resolve is out and nothing is on screen yet. Only matters on a cold start, where
    // there is no previous document to keep showing and the logo would otherwise flash.
    this.navPending = false;
    this.onPopState = (e) => this.#onPopState(e);
    host.addController(this);
  }

  hostConnected() {
    this.#restoreFromLocation();
    window.addEventListener('popstate', this.onPopState);
  }

  hostDisconnected() {
    window.removeEventListener('popstate', this.onPopState);
  }

  get document() {
    return this.#document;
  }

  // Документа ещё нет, но он в пути: адрес назвал документ, а сессия ещё подключается
  // (pendingRestore), или его разрешение в полёте (navPending).
  get pending() {
    return !!this.pendingRestore || this.navPending;
  }

  // Every navigation to a topic goes through here. mode names the document to open - 'inspector',
  // 'logram', 'chart' - or is null for "let the server decide", which #openAuto asks outright
  // (see ViewSession.HandleOpenAuto) and which is the normal case: opening from Workspace, a
  // breadcrumb click to an ancestor, parent-on-delete, a bare URL. A non-null mode comes only
  // from somewhere that genuinely knows better than the resolve would - ?mode= in the URL, or
  // the bar's switch button naming the view you are switching TO.
  //
  // `requested` marks a mode the user named outright, as both of those do. It is what keeps
  // adoptAltView from overruling a deliberate choice of Inspector on a Logram-typed topic, and
  // what eventually pins that choice into the URL - see documentDescriptor for why only that one
  // combination is expressible there at all.
  //
  // `altView` carries over what the caller already knows about the topic's other view - only the
  // switch button does, by standing on that other view. Undefined means "nobody knows yet",
  // which is not the same as null, "this topic has none".
  navigate(path, mode, { requested = false, altView = undefined, history: historyMode = 'push' } = {}) {
    if(!path) return;
    if(mode === 'chart') {
      this.#setChartDocument(path, historyMode);
      return;
    }
    if(mode === 'logram' || mode === 'lplc') {
      this.#setGraphDocument(mode, path, historyMode);
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
  openInNewTab(path) {
    if(!path) return;
    window.open(descriptorUrl({ mode: null, path }), '_blank');
  }

  async openCatalog(vid, historyMode = 'push') {
    if(!this.session.ready) return;
    try {
      const doc = await this.session.catalogDocument(vid);
      this.#setDocument(doc, historyMode);
      this.session.open(doc);
    }
    catch(error) {
      console.warn('req.open catalog failed', vid, error);
    }
  }

  // A descriptor is the one shape a URL, a history entry and a pending restore all reduce to:
  // {mode, path}, where mode names the document to open and null means "ask the server which one
  // this topic opens as". Opening one is the only way any of the three becomes a document.
  applyRestore(restore) {
    if(!restore) return;
    if(restore.mode === 'catalog') {
      this.openCatalog(WORKSPACE_ROOT_VID, 'none');
      return;
    }
    // A mode that survived into the URL was named outright by whoever wrote it - that is the only
    // reason it is there rather than being left to the server's own resolve.
    this.navigate(restore.path, restore.mode, { requested: restore.mode !== null, history: 'none' });
  }

  // После hello новой сессии: то, что ждало подключения, а если не ждало ничего - документ, который
  // пережил разрыв (stale; его view хост уже забыл через DocumentSession.forget). Переоткрывается на
  // месте, без записи в историю: адрес уже его. Если за время hello на экран пришёл другой документ,
  // он открыт уже в новой сессии и трогать его незачем.
  reopen(stale) {
    const reopen = stale && stale === this.#document ? reopenDescriptor(stale) : null;
    const restore = this.pendingRestore ?? reopen;
    this.pendingRestore = null;
    this.applyRestore(restore);
  }

  // The Children root row named the topic's other view differently from what the document knew
  // (DocumentSession.route reports it).
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
  adoptAltView(altView) {
    const doc = this.#document;
    if(!doc) return;
    if(isGraphView(altView) && doc.requestedMode !== 'inspector') {
      this.#setGraphDocument(altView, doc.path, 'none');
      return;
    }
    const adopted = { ...doc, altView };
    this.#document = adopted;
    this.host.requestUpdate();
    const descriptor = documentDescriptor(adopted);
    history.replaceState(descriptorState(descriptor), '', descriptorUrl(descriptor));
  }

  // Топик документа удалён (DocumentSession.route) - навигация к родителю вместо документа на том,
  // чего больше нет. Каждый из трёх контроллеров Inspector присылает свой evnt.del, и пока ответ
  // resolve не пришёл, документ на экране прежний - так что сюда приходят все три. Лишние
  // разрешения не вредят: каждое следующее поднимает navToken, и ответы прежних #openAuto
  // закрывает как устаревшие.
  rootDeleted() {
    const doc = this.#document;
    if(!doc) return;
    const parentPath = doc.path === '/' ? '/' : (doc.path.split('/').slice(0, -1).join('/') || '/');
    this.navigate(parentPath, null);
  }

  // The one place a document is swapped. Closing whatever the previous one had open belongs here
  // rather than at each call site (it used to be three verbatim copies, plus a fourth inlined in
  // #openAuto), and so does putting the URL and history entry in step with what is now on screen.
  //
  // 'none' is for a swap that must not add to the history: restoring the document the URL already
  // names, and adoptAltView's correction, which lands on the same address anyway. Every
  // other swap - including the bar's switch button - is a place you can go Back from.
  //
  // Only for swaps - installing a different document object whose open panes differ. Changing a
  // field of the document in place does NOT go through here (it would close the panes that
  // document still has open); see DocumentSession.open and adoptAltView.
  #setDocument(doc, historyMode = 'push') {
    this.navToken++;
    this.navPending = false;
    this.session.close(this.#document);
    this.#document = doc;
    this.host.requestUpdate();
    if(historyMode !== 'push') return;
    const descriptor = documentDescriptor(doc);
    history.pushState(descriptorState(descriptor), '', descriptorUrl(descriptor));
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
    if(!this.session.ready) return;
    const token = ++this.navToken;
    // Only visible on a cold start; every other navigation has a document to keep showing.
    this.navPending = !this.#document;
    this.host.requestUpdate();
    try {
      const result = await this.session.resolve(path);
      if(token !== this.navToken) {
        this.session.closeResolved(path, result?.view);
        return;
      }
      if(isGraphView(result?.view)) this.#setGraphDocument(result.view, path, historyMode, true);
      else this.#setInspectorDocument(path, { requestedMode: null, altView: undefined }, historyMode);
    }
    catch(error) {
      console.warn('req.open (auto) failed', path, error);
      if(token === this.navToken) {
        this.navPending = false;
        this.host.requestUpdate();
      }
    }
  }

  #setInspectorDocument(path, { requestedMode, altView }, historyMode = 'push') {
    const doc = this.session.inspectorDocument(path, { requestedMode, altView });
    this.#setDocument(doc, historyMode);
    this.session.open(doc);
  }

  // view - 'logram' or 'lplc' (an ESP node's LogramPLC program): the same document over its own
  // server view, see DocumentSession.graphDocument.
  #setGraphDocument(view, path, historyMode = 'push', alreadyOpened = false) {
    const doc = this.session.graphDocument(view, path, alreadyOpened);
    this.#setDocument(doc, historyMode);
    this.session.open(doc);
  }

  #setChartDocument(path, historyMode = 'push') {
    if(!path) return;
    const doc = this.session.chartDocument(path);
    this.#setDocument(doc, historyMode);
    this.session.open(doc);
  }

  #request(restore) {
    if(!this.#isReady()) {
      this.pendingRestore = restore;
      return;
    }
    this.applyRestore(restore);
  }

  // Runs before the connection exists, so it only records what to open and normalises the address
  // bar; reopen() hands the descriptor to applyRestore once there is a session to open it in.
  #restoreFromLocation() {
    const descriptor = descriptorFromLocation();
    if(!descriptor) return;
    this.pendingRestore = descriptor;
    history.replaceState(descriptorState(descriptor), '', descriptorUrl(descriptor));
  }

  #onPopState(e) {
    const descriptor = descriptorOfState(e.state);
    if(descriptor) {
      this.#request(descriptor);
      return;
    }
    // Back past the first document of this tab: nothing to show, and nothing should stay open
    // server-side for it either.
    this.pendingRestore = null;
    this.session.close(this.#document);
    this.#document = null;
    this.host.requestUpdate();
  }

  #isReady;
  #document;
}

// The views a topic resolves to as a graph document rather than the Inspector.
export function isGraphView(view) {
  return view === 'logram' || view === 'lplc';
}
