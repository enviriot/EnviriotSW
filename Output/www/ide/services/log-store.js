const MAX_ENTRIES = 5000;

// Flat, append-only log stream - deliberately not ViewStore (whose #insertIndex /
// level / parent-vid logic exists for tree rows and doesn't apply here).
export class LogStore {
  constructor() {
    this.entries = [];
    this.listeners = new Set();
  }

  subscribe(callback) {
    this.listeners.add(callback);
    callback(this.entries);
    return () => this.listeners.delete(callback);
  }

  applyLog(entry) {
    if(!entry) return;
    this.entries.push({ dt: entry.dt, ll: entry.ll, msg: entry.msg });
    if(this.entries.length > MAX_ENTRIES) this.entries.splice(0, this.entries.length - MAX_ENTRIES);
    this.#notify();
  }

  prependHistory(items) {
    if(!Array.isArray(items) || items.length === 0) return;
    this.entries.unshift(...items.map((item) => ({ dt: item.dt, ll: item.ll, msg: item.msg })));
    this.#notify();
  }

  clear() {
    if(this.entries.length === 0) return;
    this.entries = [];
    this.#notify();
  }

  #notify() {
    const snapshot = this.entries.slice();
    for(const listener of this.listeners) listener(snapshot);
  }
}
