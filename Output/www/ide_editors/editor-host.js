import { LitElement, html, css } from '../lib/lit-all.min.js';
import { DEFAULT_EDITOR_TAG, editorTagFor, normalizeEditorView, registeredEditorNames } from './editor-registry.js';

export class X13EditorHost extends LitElement {
  static properties = {
    row: { attribute: false },
    view: {},
    selected: { type: Boolean },
  };

  static styles = css`
    :host { display: inline; max-width: 100%; }
  `;

  constructor() {
    super();
    this.view = 'value';
    this.selected = false;
    this.#editor = null;
    this.#editorKey = null;
    this.#editorTag = null;
    this.#focused = false;
    this.#dependencies = null;
    this.#rowDependenciesOptions = undefined;
    this.#rowDependenciesName = undefined;
    this.#rowDependenciesCache = null;
  }

  render() { return html`<span id="slot"></span>`; }

  firstUpdated() { this.#syncEditor(); }
  updated() { this.#syncEditor(); }

  #syncEditor() {
    const key = editorKey(this.row);
    const tagName = editorTagFor(key);
    if(!this.#dependencies) this.#dependencies = new EditorDependencyHost(registeredEditorNames);
    if(!this.#editor || this.#editorKey !== key || this.#editorTag !== tagName) this.#createEditor(key, tagName);
    this.#editor.dependencies = this.#resolveDependencies();
    applyRowToEditor(this.#editor, this.row, this.selected || this.#focused, this.view);
  }

  #resolveDependencies() {
    const options = this.row?.options;
    if(!Array.isArray(options)) return this.#dependencies;
    if(options !== this.#rowDependenciesOptions || this.row.name !== this.#rowDependenciesName) {
      this.#rowDependenciesOptions = options;
      this.#rowDependenciesName = this.row.name;
      this.#rowDependenciesCache = rowDependencies(this.row, this.#dependencies);
    }
    return this.#rowDependenciesCache;
  }

  #createEditor(key, tagName) {
    const container = this.renderRoot.querySelector('#slot');
    if(!container) return;
    container.textContent = '';
    const commit = (newValue) => this.#commit(newValue);
    const rpc = (cmd) => this.#rpc(cmd);
    this.#editor = createEditorElement(tagName || DEFAULT_EDITOR_TAG, { commit, rpc });
    this.#editor.commit = commit;
    this.#editor.rpc = rpc;
    this.#editor.addEventListener('focusin', () => this.#setFocused(true));
    this.#editor.addEventListener('focusout', () => this.#setFocused(false));
    this.#editorKey = key;
    this.#editorTag = tagName || DEFAULT_EDITOR_TAG;
    container.appendChild(this.#editor);
  }

  #commit(newValue) {
    if(!this.row || this.row.readonly) return Promise.resolve();
    const detail = { vid: this.row.vid, value: newValue, promise: null };
    this.dispatchEvent(new CustomEvent('editor-commit', {
      bubbles: true,
      composed: true,
      detail,
    }));
    return detail.promise || Promise.resolve();
  }

  // Lets an editor trigger a req.rpc command against its own row's vid (e.g. the
  // DevicePLC editor's inline Build/Run/Start/Stop buttons) the same way commit()
  // lets it trigger req.commit - bubbles to app-shell.js's #onEditorRpc, which is the
  // only place that actually calls api.rpc.
  #rpc(cmd) {
    if(!this.row || !cmd) return Promise.resolve();
    const detail = { vid: this.row.vid, cmd, promise: null };
    this.dispatchEvent(new CustomEvent('editor-rpc', {
      bubbles: true,
      composed: true,
      detail,
    }));
    return detail.promise || Promise.resolve();
  }

  #setFocused(focused) {
    if(this.#focused === focused) return;
    this.#focused = focused;
    applyRowToEditor(this.#editor, this.row, this.selected || this.#focused, this.view);
  }


  #editor;
  #editorKey;
  #editorTag;
  #focused;
  #dependencies;
  #rowDependenciesOptions;
  #rowDependenciesName;
  #rowDependenciesCache;
}

function rowDependencies(row, fallback) {
  if(!Array.isArray(row?.options)) return fallback;
  return {
    loadEnum(sourceKey, owner, callback) {
      const key = sourceKey || row.name || '';
      queueMicrotask(() => callback({
        ok: true,
        name: key,
        source: key,
        items: row.options,
      }));
      return { dispose() {} };
    },
    loadEditorNames(owner, callback) {
      return fallback?.loadEditorNames ? fallback.loadEditorNames(owner, callback) : { dispose() {} };
    },
  };
}

export function editorKey(topicOrRow) {
  return topicOrRow?.editor || null;
}


export function applyRowToEditor(editor, row, selected = false, view = 'row') {
  if(!editor) return;
  editor.view = normalizeEditorView(view);
  editor.vid = row?.vid;
  editor.readonly = !!row?.readonly;
  editor.selected = !!selected;
  editor.value = row?.value;
  editor.state = row?.value;
  editor.enumSource = undefined;
  if(editor.localName === DEFAULT_EDITOR_TAG) {
    editor.state = row?.value;
    editor.displayValue = row?.displayValue;
    editor.editor = row?.editor;
  }
}

customElements.define('x13-editor-host', X13EditorHost);

export function createEditorElement(tagName, options = {}) {
  const tag = tagName || DEFAULT_EDITOR_TAG;
  const EditorCtor = customElements.get(tag);
  if(EditorCtor) {
    try { return new EditorCtor(options); }
    catch(error) { console.warn('editor constructor failed, falling back to document.createElement', tag, error); }
  }
  return document.createElement(tag);
}

export class EditorDependencyHost {
  constructor(editorNamesProvider = null) {
    this.#editorNamesProvider = typeof editorNamesProvider === 'function' ? editorNamesProvider : () => [];
  }

  loadEditorNames(owner, callback) {
    const names = normalizeEditorNames(this.#editorNamesProvider());
    queueMicrotask(() => callback(names));
    return { dispose() {} };
  }

  #editorNamesProvider;
}

export function normalizeEditorNames(editorNames) {
  return Array.isArray(editorNames)
    ? [...new Set(editorNames.filter((name) => typeof name === 'string'))].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'variant' }))
    : [];
}
