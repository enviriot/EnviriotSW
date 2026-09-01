import { X13TreeDocumentBase, treeHeaderStyles } from './tree-view-shared.js';
import { isDescendant } from '../services/vid-helper.js';

const NAME_WIDTH_KEY = 'x13.workspace.nameWidth';
const EXPANDED_KEY = 'x13.workspace.expanded';
const DEFAULT_NAME_WIDTH = 180;
const MIN_NAME_WIDTH = 110;

export class X13ViewWorkspace extends X13TreeDocumentBase {
  static properties = {
    ...X13TreeDocumentBase.properties,
    revealPath: { attribute: false },
  };

  static styles = treeHeaderStyles;

  constructor() {
    super({ nameWidthKey: NAME_WIDTH_KEY, defaultNameWidth: DEFAULT_NAME_WIDTH, minNameWidth: MIN_NAME_WIDTH });
    this.rememberedExpanded = readExpandedVids();
    this.activeExpanded = new Set();
    this.revealPath = null;
    this.pendingRevealVid = null;
  }

  render() {
    return this._renderTree();
  }

  updated(changed) {
    super.updated(changed);
    if(changed.has('revealPath') && this.revealPath?.path) this.#reveal(this.revealPath.path);
  }

  _subscribeStore() {
    this.activeExpanded.clear();
    this.pendingRevealVid = null;
    super._subscribeStore();
  }

  _onRowsChanged(rows, reset) {
    if(reset) this.activeExpanded.clear();
    this.#restoreRememberedExpanded(rows);
    this.#tryScrollToPendingReveal();
  }

  // "Show in Workspace" (from a Logram pin's context menu, see app-shell.js
  // #onSegmentCommand) - expands every ancestor of `path` and scrolls the target row
  // into view once it exists. Reuses the same rememberedExpanded/#restoreRememberedExpanded
  // machinery that already re-expands remembered nodes as their rows stream in
  // (normally on reload), just seeded with this path's ancestor chain instead - each
  // level's expand triggers evnt.add for the next level down, which re-enters
  // _onRowsChanged and cascades the rest on its own, so this only has to kick off
  // whatever's already loaded right now.
  #reveal(path) {
    if(!this.api || !path) return;
    const segments = path.split('/').filter(Boolean);
    // Root ("workspace#/") needs expanding same as any other ancestor - it isn't
    // auto-expanded server-side (TopicTreeController.BuildRow resolves its Expander
    // from the same _expansion.IsExpanded as every other row), so on a first-ever
    // visit (nothing in rememberedExpanded yet) nothing below it would ever stream in.
    const ancestorVids = ['workspace#/'];
    let ancestorPath = '';
    for(let i = 0; i < segments.length - 1; i++) {
      ancestorPath += `/${segments[i]}`;
      ancestorVids.push(`workspace#${ancestorPath}`);
    }
    let changed = false;
    for(const vid of ancestorVids) {
      if(!this.rememberedExpanded.has(vid)) {
        this.rememberedExpanded.add(vid);
        changed = true;
      }
    }
    if(changed) writeExpandedVids(this.rememberedExpanded);
    this.pendingRevealVid = `workspace#${path}`;
    this.#restoreRememberedExpanded(this.rows);
    this.#tryScrollToPendingReveal();
  }

  #tryScrollToPendingReveal() {
    if(!this.pendingRevealVid) return;
    const target = this.pendingRevealVid;
    this.updateComplete.then(() => {
      if(this.pendingRevealVid !== target) return;
      const rowEl = [...this.renderRoot.querySelectorAll('x13-view-row')].find((el) => el.row?.vid === target);
      if(!rowEl) return;
      this.pendingRevealVid = null;
      rowEl.scrollIntoView({ block: 'center' });
      rowEl.classList.add('reveal-flash');
      setTimeout(() => rowEl.classList.remove('reveal-flash'), 1200);
    });
  }

  _afterToggle(row, expand) {
    if(expand) {
      this.rememberedExpanded.add(row.vid);
      this.activeExpanded.add(row.vid);
    } else {
      this.rememberedExpanded.delete(row.vid);
      this.#forgetActiveSubtree(row.vid);
    }
    writeExpandedVids(this.rememberedExpanded);
  }

  #restoreRememberedExpanded(rows) {
    if(!this.api) return;
    for(const row of rows) {
      if(!row?.vid || !row.expander || row.expander === 2) continue;
      if(!this.rememberedExpanded.has(row.vid) || this.activeExpanded.has(row.vid)) continue;
      this.activeExpanded.add(row.vid);
      this.api.expand(row.vid, true).catch((error) => {
        this.activeExpanded.delete(row.vid);
        console.warn('restore req.expand failed', row.vid, error);
      });
    }
  }

  #forgetActiveSubtree(vid) {
    for(const activeVid of [...this.activeExpanded]) {
      if(activeVid === vid || isDescendant(vid, activeVid)) this.activeExpanded.delete(activeVid);
    }
  }
}

function readExpandedVids() {
  try {
    const parsed = JSON.parse(localStorage.getItem(EXPANDED_KEY) || '[]');
    return new Set(Array.isArray(parsed) ? parsed.filter((item) => typeof item === 'string').map(normalizeExpandedVid) : []);
  }
  catch {
    return new Set();
  }
}

function writeExpandedVids(values) {
  localStorage.setItem(EXPANDED_KEY, JSON.stringify([...values].sort()));
}

function normalizeExpandedVid(value) {
  if(value.startsWith('workspace#')) return value;
  if(value.startsWith('/')) return `workspace#${value}`;
  return value;
}

customElements.define('x13-view-workspace', X13ViewWorkspace);
