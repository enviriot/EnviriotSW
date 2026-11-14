// Серверная сторона документа: из каких view он состоит в текущей ViewSession, как их открыть и
// закрыть и в какое хранилище класть их пакеты. Адрес и историю не трогает - это DocumentRouter;
// что из пакета следует для навигации, только сообщает (route).
import { ViewStore, CombinedViewStore } from '../services/view-store.js';
import { getView } from '../services/vid-helper.js';

// The three server-side views an Inspector document opens as one - each with its own controller
// and its own evnt.* stream, always opened and closed together. Table-driven because opening,
// closing, packet routing and the root-deleted check below all need these same three vids, and
// each of them used to spell the trio out for itself.
const INSPECTOR_PANES = ['inspstate', 'inspmanifest', 'inspchildren'];
// vid-views whose rows belong to the active document rather than to the Workspace tree. NOT
// document types, which is a different namespace: 'inspstate' and the other two are panes of one
// Inspector document, while 'logram', 'lplc', 'catalog' and 'chart' happen to name a document and
// a view alike. A vid naming anything not listed here still falls through to the Workspace store,
// as it always has.
const DOCUMENT_VID_VIEWS = new Set([...INSPECTOR_PANES, 'logram', 'lplc', 'catalog', 'chart']);
// The graph documents: one server view each, named like the document, drawn by x13-logram-document.
const GRAPH_VIEWS = new Set(['logram', 'lplc']);

export class DocumentSession {
  // api появляется только после подключения (app-shell.js #start), поэтому берётся через функцию.
  constructor(getApi) {
    this.#getApi = getApi;
  }

  get ready() {
    return !!this.#getApi();
  }

  // Asks the server which document this topic is - see ViewSession.HandleOpenAuto, which resolves
  // Core/Logram from one field read and answers BEFORE opening the side it resolved to.
  resolve(path) {
    return this.#getApi().open(path, null);
  }

  // altView is the topic's other view, as the server names it on the document's own root row
  // ("logram", "chart" or nothing) - it decides which button the bar offers, and, for "logram",
  // whether this document is expressible in the URL at all (see documentDescriptor). It normally
  // stays undefined until that root row arrives and the router's adoptAltView picks it up; only
  // the switch button, which stands on the other view already, can hand it over up front.
  inspectorDocument(path, { requestedMode, altView }) {
    const stores = {};
    for(const pane of INSPECTOR_PANES) stores[pane] = new ViewStore();
    return {
      view: 'inspector',
      path,
      requestedMode,
      stores,
      store: combinedInspectorStore(stores),
      altView,
      inspectorOpened: false,
      logramOpened: false,
    };
  }

  // alreadyOpened - сторону Logram сервер уже открыл сам, разрешая топик (resolve).
  logramDocument(path, alreadyOpened = false) {
    return this.graphDocument('logram', path, alreadyOpened);
  }

  // A Logram or an LPLC program (view 'lplc', see LplcViewProvider) - the same document over a
  // different server view. The flag keeps its Logram name: close() and forget() read it for both.
  graphDocument(view, path, alreadyOpened = false) {
    const store = new ViewStore();
    return {
      view,
      path,
      stores: { [view]: store },
      store,
      inspectorOpened: false,
      logramOpened: alreadyOpened,
    };
  }

  // The Chart document draws from two sources, and they answer different questions. Its history
  // comes over HTTP from /api/archivist, carrying the session token the api's handshake already
  // handed us (ide/services/api-token.js). What has happened since that answer comes from the
  // `chart#` view opened in open() - one subscription to the topic's value (ChartViewProvider),
  // whose evnt.* land in stores.chart like any other document's rows.
  chartDocument(path) {
    return { view: 'chart', path, stores: { chart: new ViewStore() }, chartOpened: true };
  }

  // Каталогу ответ сервера нужен ещё до того, как документ создан: заголовок и адрес каталога
  // приходят в ответе на req.open.
  async catalogDocument(vid) {
    const opened = await this.#getApi().open(vid, 'catalog');
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
    return {
      view: 'catalog',
      vid: rootVid,
      title: opened.title,
      data: opened.data,
      stores: { catalog: store },
      store,
    };
  }

  // Открывает на сервере view документа, который роутер только что поставил на экран. Флаги
  // ставятся в самом документе, а не новым документом: ничего из того, что рисуется, они не
  // меняют, а close() читает их с того объекта, который к тому времени окажется уходящим. Отказ
  // сервера снимает флаг, чтобы close() не закрывал то, чего сервер не открыл.
  //
  // The State, Manifest and Children roots are three separate server-side views opened as one.
  // Their header rows are not synthesised here: the first req.expand on a root the session has not
  // seen creates the controller, and creating it sends the root row (TreeViewProviderBase
  // .GetOrCreateOwner -> SendControllerRoot), so the client would only be drawing a placeholder for
  // the one round trip until the real row replaced it - which the auto path never did.
  open(doc) {
    const api = this.#getApi();
    if(!doc || !api) return;
    if(doc.view === 'inspector') {
      if(doc.inspectorOpened) return;
      doc.inspectorOpened = true;
      for(const pane of INSPECTOR_PANES) {
        const vid = `${pane}#${doc.path}`;
        api.expand(vid, true).catch((error) => console.warn('inspector initial req.expand failed', vid, error));
      }
    }
    else if(GRAPH_VIEWS.has(doc.view)) {
      if(doc.logramOpened) return;
      doc.logramOpened = true;
      api.open(`${doc.view}#${doc.path}`, doc.view).catch(() => { doc.logramOpened = false; });
    }
    else if(doc.view === 'chart') {
      api.open(`chart#${doc.path}`, 'chart').catch((error) => {
        console.warn('req.open chart failed', doc.path, error);
        // The archive half still works without it; only the live tail is lost.
        doc.chartOpened = false;
      });
    }
    else if(doc.view === 'catalog') {
      api.expand(doc.vid, true).catch((error) => console.warn('catalog initial req.expand failed', doc.vid, error));
    }
  }

  // Closes exactly what the document actually had open, reading its own flags - so the router can
  // hand it the document being navigated AWAY FROM. This is what makes Inspector's expand state
  // document-scoped rather than session-scoped like Workspace's: the backend controller feeding
  // those panes is disposed along with the document.
  close(doc) {
    if(!doc || !this.#getApi()) return;
    if(doc.inspectorOpened) for(const pane of INSPECTOR_PANES) this.#closeVid(`${pane}#${doc.path}`);
    if(doc.logramOpened) this.#closeVid(`${doc.view}#${doc.path}`);
    if(doc.chartOpened) this.#closeVid(`chart#${doc.path}`);
  }

  // Navigated somewhere else while resolve's answer was in flight. The server has opened the side
  // it resolved to by now, or is about to, and nothing on screen refers to it - close it directly,
  // since no document was ever installed to carry the flags close() reads.
  closeResolved(path, view) {
    if(GRAPH_VIEWS.has(view)) this.#closeVid(`${view}#${path}`);
    else for(const pane of INSPECTOR_PANES) this.#closeVid(`${pane}#${path}`);
  }

  // View документа принадлежали сессии, которая кончилась, и умерли вместе с ней. Забытые флаги не
  // дают close() отправить за них req.close в новую сессию, которая их не открывала.
  forget(doc) {
    if(!doc) return;
    doc.inspectorOpened = false;
    doc.logramOpened = false;
    doc.chartOpened = false;
  }

  // Пакет evnt.* одного из деревьев документа кладётся в хранилище этого дерева. null - пакет не
  // документа, он для Workspace. Иначе ответ - что из пакета следует для навигации: altView -
  // корень Children назвал другую сторону топика не так, как документ знал (undefined - не назвал
  // или не изменилась), rootDeleted - удалён сам топик документа.
  route(message, doc) {
    const vid = String(message.vid || '');
    const view = getView(vid);
    if(!DOCUMENT_VID_VIEWS.has(view)) return null;
    // Each of the document's trees keeps receiving only its own packets, which is what lets
    // CombinedViewStore render several of them as one list without them interfering.
    doc?.stores?.[view]?.applyPacket(message);
    const deleted = message.type === 'evnt.del';
    return {
      altView: !deleted && view === 'inspchildren' ? changedAltView(doc, vid, message) : undefined,
      rootDeleted: deleted && isOwnRoot(doc, vid),
    };
  }

  #closeVid(vid) {
    this.#getApi().close(vid).catch((error) => console.warn('req.close failed', vid, error));
  }

  #getApi;
}

// The pane stores rendered as one list. The array order fixes their on-screen order (state ->
// Manifest -> children), matching ES's InspectorForm, which showed all three in one ListView.
function combinedInspectorStore(stores) {
  return new CombinedViewStore(INSPECTOR_PANES.map((pane) => stores[pane]));
}

// The Children root row is the open document's own topic, and the only row that carries
// altView - so this is where the document normally learns what else that topic can be shown
// as. It also keeps arriving: the server re-sends the row when the topic's fields change, so
// turning archiving on or off, or retyping a topic to Core/Logram, takes the button with it
// while the document stays open. What the router does with it - see DocumentRouter.adoptAltView.
function changedAltView(doc, vid, message) {
  if(!doc || doc.view !== 'inspector' || vid !== `inspchildren#${doc.path}`) return undefined;
  // Only when the packet actually carries the key: an evnt.upd names just what changed, so a
  // row updated for some other reason must not be read as "this topic has no other view".
  if(!Object.prototype.hasOwnProperty.call(message, 'altView')) return undefined;
  const altView = message.altView || null;
  return altView === doc.altView ? undefined : altView;
}

// The topic backing the open document was deleted elsewhere (e.g. from Workspace) - its
// controller detected the removal of its own root and pushed evnt.del for that root vid (see
// TopicTreeController / StateTreeController / ManifestTreeController's HandleRootRemoved),
// rather than just a row disappearing from within an already-open tree. Exact vid equality, not
// the vid's topic path: a field row (inspstate#/A/B#f) names the very same topic as the root it
// hangs under, and its removal is not the document's removal.
function isOwnRoot(doc, vid) {
  if(doc?.view !== 'inspector' && !GRAPH_VIEWS.has(doc?.view) && doc?.view !== 'chart') return false;
  return [...INSPECTOR_PANES, ...GRAPH_VIEWS, 'chart'].some((view) => vid === `${view}#${doc.path}`);
}
