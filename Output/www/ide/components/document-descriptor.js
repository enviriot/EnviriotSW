// Дескриптор документа и его перевод в адрес, запись истории и обратно. Чистые функции: из DOM
// только чтение location в descriptorFromLocation.

// A descriptor - {mode, path} - is the single form URL, history entry and open document all
// convert to and from, so the address bar and what is on screen cannot drift apart: the router's
// #setDocument writes both from the document it installs, and page load / popstate read them back
// into the very same shape DocumentRouter.applyRestore opens.
//
// mode null means "whatever this topic opens as" and is therefore what a bare topic URL carries.
// Logram maps to it: a Logram document is what the server resolves a Logram-typed topic to, so
// pinning ?mode=logram would only restate the default. Inspector maps to it too, and keeps mapping
// to it until BOTH halves of the exception hold - it was asked for outright, and the topic really
// does have a Logram side for the resolve to have preferred. Until the second half is known, an
// Inspector document is simply what a bare topic URL opens, and saying ?mode=inspector would put a
// pin in the address of every ordinary topic anyone ever switched to Inspector from a Chart.
// DocumentRouter.adoptAltView is what learns the second half, and rewrites the address when it does.
export function documentDescriptor(doc) {
  if(doc.view === 'catalog') return { mode: 'catalog' };
  if(doc.view === 'chart') return { mode: 'chart', path: doc.path };
  if(doc.view === 'inspector' && doc.requestedMode === 'inspector' && (doc.altView === 'logram' || doc.altView === 'lplc')) return { mode: 'inspector', path: doc.path };
  return { mode: null, path: doc.path };
}

// Что открыть, чтобы получить этот же документ в новой сессии. Не documentDescriptor: тот пишет
// адрес и даёт Inspector mode null, пока altView неизвестен, - и явно выбранный Inspector на
// Logram-топике переоткрылся бы как Logram. Остальное совпадает: Logram и не выбранный явно
// Inspector - то, во что топик и так разрешается, а каталог всегда один, от корня.
export function reopenDescriptor(doc) {
  if(doc.view === 'inspector' && doc.requestedMode === 'inspector') return { mode: 'inspector', path: doc.path };
  return documentDescriptor(doc);
}

// The state SHAPE is deliberately the one earlier builds wrote: history.state survives a reload,
// so a tab opened before Logram became a document of its own still has to go Back correctly.
export function descriptorState(descriptor) {
  if(descriptor.mode === 'catalog') return { mode: 'catalog' };
  if(descriptor.mode === 'chart') return { chartPath: descriptor.path };
  return { inspectorPath: descriptor.path, forceMode: descriptor.mode === 'inspector' ? 'inspector' : null };
}

export function descriptorOfState(state) {
  if(state?.chartPath) return { mode: 'chart', path: state.chartPath };
  if(state?.mode === 'catalog') return { mode: 'catalog' };
  if(state?.inspectorPath) return { mode: state.forceMode === 'inspector' ? 'inspector' : null, path: state.inspectorPath };
  return null;
}

// /ide.html/<path>, plus ?mode= for what the path alone cannot say. Catalog is rooted on no topic
// and is named by ?mode= alone.
export function descriptorUrl(descriptor) {
  if(descriptor.mode === 'catalog') return '/ide.html/?mode=catalog';
  const segments = String(descriptor.path || '/').split('/').filter(Boolean).map(encodeURIComponent);
  const base = segments.length ? `/ide.html/${segments.join('/')}` : '/ide.html/';
  return descriptor.mode ? `${base}?mode=${descriptor.mode}` : base;
}

export function descriptorFromLocation() {
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
