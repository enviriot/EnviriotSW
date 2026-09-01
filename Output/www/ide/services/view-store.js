import { compareVid, getParentVid } from './vid-helper.js';

export class ViewStore {
  constructor() {
    this.rows = [];
    this.listeners = new Set();
  }

  subscribe(callback) {
    this.listeners.add(callback);
    callback(this.rows);
    return () => this.listeners.delete(callback);
  }

  clear() {
    if(this.rows.length === 0) return;
    this.rows = [];
    this.#notify(true);
  }

  applyPacket(packet) {
    if(!packet || !packet.type) return;
    if(packet.type === 'evnt.add') this.add(packet);
    else if(packet.type === 'evnt.upd') this.update(packet);
    else if(packet.type === 'evnt.del') this.remove(packet.vid);
  }

  add(row) {
    if(!row || !row.vid) return;
    const existing = this.rows.findIndex((it) => it.vid === row.vid);
    if(existing >= 0) this.rows.splice(existing, 1, normalizeRow(row));
    else this.rows.splice(this.#insertIndex(row), 0, normalizeRow(row));
    this.#notify();
  }

  update(patch) {
    if(!patch || !patch.vid) return;
    const index = this.rows.findIndex((row) => row.vid === patch.vid);
    if(index < 0) return;
    const allowed = ['expander', 'icon', 'editor', 'value', 'readonly', 'altView', 'options', 'editorView', 'info', 'srcVer', 'actVer', 'downloadEnabled', 'removeEnabled',
      'x', 'y', 'width', 'height', 'pins', 'pinDirection', 'pinIndex', 'sourceVid', 'sourceLocal', 'sourcePath', 'trace', 'color', 'displayValue'];
    const next = { ...this.rows[index] };
    for(const key of allowed) {
      if(Object.prototype.hasOwnProperty.call(patch, key)) next[key] = patch[key];
    }
    this.rows.splice(index, 1, next);
    this.#notify();
  }

  remove(vid) {
    const index = this.rows.findIndex((row) => row.vid === vid);
    if(index < 0) return;
    const level = Number(this.rows[index].level) || 0;
    let end = index + 1;
    while(end < this.rows.length && (Number(this.rows[end].level) || 0) > level) end++;
    this.rows.splice(index, end - index);
    this.#notify();
  }

  #insertIndex(row) {
    const parentVid = getParentVid(row.vid, row.name);
    const parentIndex = this.rows.findIndex((candidate) => candidate.vid === parentVid);
    const rowLevel = Number(row.level) || 0;
    let start = parentIndex >= 0 ? parentIndex + 1 : 0;
    let end = this.rows.length;

    if(parentIndex >= 0) {
      const parentLevel = Number(this.rows[parentIndex].level) || 0;
      end = start;
      while(end < this.rows.length && (Number(this.rows[end].level) || 0) > parentLevel) end++;
    }

    for(let index = start; index < end;) {
      const candidate = this.rows[index];
      const candidateLevel = Number(candidate.level) || 0;
      if(candidateLevel === rowLevel && getParentVid(candidate.vid, candidate.name) === parentVid) {
        if(compareVid(row.vid, candidate.vid) < 0) return index;
        index = subtreeEnd(this.rows, index);
      } else {
        index++;
      }
    }
    return end;
  }

  #notify(reset = false) {
    const snapshot = this.rows.slice();
    for(const listener of this.listeners) listener(snapshot, reset);
  }
}

// Read-only facade over N ordered ViewStores, for rendering multiple independently
// event-routed trees (e.g. Inspector's State and Children roots) as one combined
// list. The only method X13TreeDocumentBase ever calls on a "store" is
// subscribe(callback), so this is a drop-in store for it - the array order fixes
// the on-screen order (state rows before children rows), and each real ViewStore
// still only ever receives evnt.* packets for its own vid prefix.
export class CombinedViewStore {
  constructor(stores) {
    this.stores = stores;
  }

  subscribe(callback) {
    const emit = () => callback(this.stores.flatMap((store) => store.rows));
    const unsubscribes = this.stores.map((store) => store.subscribe(emit));
    return () => unsubscribes.forEach((unsubscribe) => unsubscribe());
  }
}

function normalizeRow(row) {
  return {
    vid: row.vid,
    level: Number(row.level) || 0,
    expander: Number(row.expander) || 0,
    icon: row.icon || '',
    name: row.name || '',
    editor: row.editor || 'Default',
    value: Object.prototype.hasOwnProperty.call(row, 'value') ? row.value : '',
    readonly: !!row.readonly,
    ...(Object.prototype.hasOwnProperty.call(row, 'altView') ? { altView: row.altView } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'options') ? { options: row.options } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'editorView') ? { editorView: row.editorView } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'info') ? { info: row.info } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'srcVer') ? { srcVer: row.srcVer } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'actVer') ? { actVer: row.actVer } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'downloadEnabled') ? { downloadEnabled: !!row.downloadEnabled } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'removeEnabled') ? { removeEnabled: !!row.removeEnabled } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'x') ? { x: row.x } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'y') ? { y: row.y } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'width') ? { width: row.width } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'height') ? { height: row.height } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'pins') ? { pins: row.pins } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'pinDirection') ? { pinDirection: row.pinDirection } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'pinIndex') ? { pinIndex: row.pinIndex } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'sourceVid') ? { sourceVid: row.sourceVid } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'sourceLocal') ? { sourceLocal: row.sourceLocal } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'sourcePath') ? { sourcePath: row.sourcePath } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'color') ? { color: row.color } : {}),
    ...(Object.prototype.hasOwnProperty.call(row, 'displayValue') ? { displayValue: row.displayValue } : {}),
    trace: !!row.trace,
  };
}

function subtreeEnd(rows, index) {
  const level = Number(rows[index].level) || 0;
  let end = index + 1;
  while(end < rows.length && (Number(rows[end].level) || 0) > level) end++;
  return end;
}
