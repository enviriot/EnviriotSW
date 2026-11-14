const MAX_ENTRIES = 5000;
const HISTORY_PAGE_SIZE = 50;
const FILE_ID = /^\d{8}:[0-9a-f]{32}:\d{20}$/;
const LOCAL_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$/;

// Границы истории независимы от приходящего параллельно живого потока.
export class LogStore {
  constructor() {
    this.entries = [];
    this.listeners = new Set();
    this.historyLoading = false;
    this.historyExhausted = false;
    this.error = '';
    this.ids = new Set();
    this.generation = 0;
    this.beforeId = undefined;
    this.gapBoundary = null;
    this.gapBefore = undefined;
    this.status = 'connecting';
    this.connectedOnce = false;
  }

  subscribe(callback) {
    this.listeners.add(callback);
    callback(this.entries);
    return () => this.listeners.delete(callback);
  }

  applyLog(entry) {
    try {
      this.#insert(normalizeEntry(entry, false));
      if(this.entries.length > MAX_ENTRIES) {
        const removed = this.entries.splice(0, this.entries.length - MAX_ENTRIES);
        for(const item of removed) if(item.id) this.ids.delete(item.id);
      }
    } catch(error) {
      this.#fail(error);
      return;
    }
    this.#notify();
  }

  prependHistory(items) {
    if(!Array.isArray(items)) throw new Error('Invalid log history response');
    const records = items.map((item) => normalizeEntry(item, true));
    for(let i = 1; i < records.length; i++) {
      if(records[i - 1].id >= records[i].id) throw new Error('Log history is not ordered by record ID');
    }
    for(const entry of records) this.#insert(entry);
    this.#notify();
  }

  clear() {
    this.generation++;
    this.entries = [];
    this.ids.clear();
    this.beforeId = undefined;
    this.gapBoundary = null;
    this.gapBefore = undefined;
    this.historyLoading = false;
    this.historyExhausted = false;
    this.error = '';
    this.#notify();
  }

  async loadHistory(api) {
    if(this.historyLoading || (this.historyExhausted && !this.gapBoundary)) return;
    if(this.gapBoundary) return this.#recoverGap(api);
    const generation = this.generation;
    const before = this.beforeId ?? this.entries.find((entry) => entry.id)?.id;
    this.historyLoading = true;
    this.error = '';
    this.#notify();
    try {
      const items = await this.#page(api, before);
      if(generation !== this.generation) return;
      this.prependHistory(items);
      if(items.length === 0) this.historyExhausted = true;
      else this.beforeId = items[0].id;
    } catch(error) {
      if(generation === this.generation) this.#fail(error);
    } finally {
      if(generation === this.generation) {
        this.historyLoading = false;
        this.#notify();
      }
    }
  }

  connectionChanged(status, api) {
    if(status === this.status) return Promise.resolve();
    const previous = this.status;
    this.status = status;
    if(status !== 'connected') {
      if(previous === 'connected') {
        this.gapBoundary ??= [...this.entries].reverse().find((entry) => entry.id)?.id ?? null;
      }
      this.generation++;
      this.historyLoading = false;
      this.#notify();
      return Promise.resolve();
    }
    const recovering = this.connectedOnce;
    this.connectedOnce = true;
    if(recovering && this.gapBoundary) {
      // Повторный разрыв во время догрузки мог добавить ещё один пропуск сверху.
      this.gapBefore = undefined;
      return this.#recoverGap(api);
    }
    return Promise.resolve();
  }

  async #recoverGap(api) {
    if(this.historyLoading || !this.gapBoundary) return;
    const generation = this.generation;
    const boundary = this.gapBoundary;
    this.historyLoading = true;
    this.error = '';
    this.#notify();
    try {
      while(this.gapBoundary === boundary) {
        const items = await this.#page(api, this.gapBefore);
        if(generation !== this.generation) return;
        if(items.length === 0) throw new Error('Log history gap could not be restored; its boundary is no longer available');
        const reached = items.some((entry) => entry.id === boundary);
        this.prependHistory(items);
        if(reached) {
          this.gapBoundary = null;
          this.gapBefore = undefined;
        } else {
          if(items[0].id < boundary) throw new Error('Log history boundary was replaced or removed');
          this.gapBefore = items[0].id;
        }
      }
    } catch(error) {
      if(generation === this.generation) this.#fail(error);
    } finally {
      if(generation === this.generation) {
        this.historyLoading = false;
        this.#notify();
      }
    }
  }

  async #page(api, beforeId) {
    if(!api) throw new Error('Log history is not connected');
    const result = await api.log(beforeId, HISTORY_PAGE_SIZE);
    if(!Array.isArray(result?.items)) throw new Error('Invalid log history response');
    const items = result.items.map((item) => normalizeEntry(item, true));
    if(beforeId && items.some((entry) => entry.id >= beforeId)) {
      throw new Error('Log history did not move backwards');
    }
    return items;
  }

  #insert(entry) {
    if(entry.id && this.ids.has(entry.id)) return;
    const at = entry.id ? this.entries.findIndex((other) => other.id && other.id > entry.id) : -1;
    if(at < 0) this.entries.push(entry);
    else this.entries.splice(at, 0, entry);
    if(entry.id) this.ids.add(entry.id);
  }

  #fail(error) {
    this.error = error?.message || String(error);
    console.warn('Log history failed', error);
    this.#notify();
  }

  #notify() {
    const snapshot = this.entries.slice();
    for(const listener of this.listeners) listener(snapshot);
  }
}

function normalizeEntry(entry, history) {
  if(!entry || (entry.id !== null && (typeof entry.id !== 'string' || !FILE_ID.test(entry.id)))
      || (history && entry.id === null) || typeof entry.dt !== 'string' || !LOCAL_TIME.test(entry.dt)
      || !Number.isInteger(entry.ll) || entry.ll < 0 || entry.ll > 3 || typeof entry.msg !== 'string') {
    throw new Error('Invalid log record');
  }
  return { id: entry.id, dt: entry.dt, ll: entry.ll, msg: entry.msg };
}

export function formatLogTime(stamp) {
  return typeof stamp === 'string' && LOCAL_TIME.test(stamp) ? stamp.slice(11) : '';
}

export function logDateKey(stamp) {
  return typeof stamp === 'string' && LOCAL_TIME.test(stamp) ? stamp.slice(0, 10) : '';
}

export function formatLogDate(stamp) {
  const key = logDateKey(stamp);
  if(!key) return '';
  const [year, month, day] = key.split('-').map(Number);
  const calendar = new Date(0);
  calendar.setUTCFullYear(year, month - 1, day);
  calendar.setUTCHours(12, 0, 0, 0);
  return calendar.toLocaleDateString(undefined, {
    timeZone: 'UTC', weekday: 'long', year: 'numeric', month: 'long', day: 'numeric',
  });
}
