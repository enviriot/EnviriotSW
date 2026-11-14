import assert from 'node:assert/strict';
import { test } from 'node:test';
import { LogStore, formatLogTime, logDateKey, formatLogDate } from '../../Output/www/ide/services/log-store.js';
import { ViewApi } from '../../Output/www/ide/services/view-api.js';

const record = (offset, msg = 'same') => ({
  id: `20261111:0123456789abcdef0123456789abcdef:${String(offset).padStart(20, '0')}`,
  dt: '2026-11-11T02:30:00.123', ll: 1, msg,
});
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail; });
  return { promise, resolve, reject };
};

test('API sends beforeId, not a timestamp or a separate cursor', async () => {
  const calls = [];
  const api = new ViewApi({ request: (type, payload) => { calls.push({ type, payload }); return Promise.resolve({ items: [] }); } });
  await api.log(undefined, 50);
  await api.log(record(100).id, 50);
  assert.deepEqual(calls, [
    { type: 'req.log', payload: { count: 50 } },
    { type: 'req.log', payload: { count: 50, beforeId: record(100).id } },
  ]);
});

test('empty browser starts at the file tail and pages until an empty response', async () => {
  const store = new LogStore();
  const calls = [];
  const pages = [[record(100), record(200)], [record(50)], []];
  const api = { log: async (beforeId) => { calls.push(beforeId); return { items: pages.shift() }; } };
  await store.loadHistory(api);
  assert.equal(store.historyExhausted, false);
  await store.loadHistory(api);
  assert.equal(store.historyExhausted, false);
  await store.loadHistory(api);
  assert.equal(store.historyExhausted, true);
  assert.deepEqual(calls, [undefined, record(100).id, record(50).id]);
  assert.deepEqual(store.entries.map(e => e.id), [50, 100, 200].map(n => record(n).id));
});

test('live records establish the initial boundary without using filtered rows', async () => {
  const store = new LogStore();
  store.applyLog({ ...record(100), ll: 0 });
  store.applyLog(record(200));
  const request = deferred();
  let boundary;
  const loading = store.loadHistory({ log: (id) => { boundary = id; return request.promise; } });
  store.applyLog(record(300));
  request.resolve({ items: [record(50)] });
  await loading;
  assert.equal(boundary, record(100).id);
  assert.equal(store.beforeId, record(50).id);
  assert.deepEqual(store.entries.map(e => e.id), [50, 100, 200, 300].map(n => record(n).id));
});

test('live-only messages are neither boundaries nor duplicates of each other', async () => {
  const store = new LogStore();
  const live = { ...record(1), id: null };
  store.applyLog(live);
  store.applyLog(live);
  let boundary = 'not-called';
  await store.loadHistory({ log: async (id) => { boundary = id; return { items: [] }; } });
  assert.equal(boundary, undefined);
  assert.equal(store.entries.length, 2);
});

test('merge keeps different IDs with equal time/text but removes history/live overlap', () => {
  const store = new LogStore();
  store.applyLog(record(200));
  store.prependHistory([record(100), record(200)]);
  store.applyLog(record(100));
  assert.deepEqual(store.entries.map(e => e.id), [record(100).id, record(200).id]);
});

test('clear cancels pending responses and resets pagination', async () => {
  const store = new LogStore();
  const pending = deferred();
  const load = store.loadHistory({ log: () => pending.promise });
  store.clear();
  pending.resolve({ items: [record(100)] });
  await load;
  assert.equal(store.entries.length, 0);
  assert.equal(store.beforeId, undefined);
  assert.equal(store.historyLoading, false);
  assert.equal(store.historyExhausted, false);
});

test('reconnect fills the gap separately while new live entries arrive', async () => {
  const store = new LogStore();
  await store.connectionChanged('connected');
  store.applyLog(record(100));
  store.applyLog(record(200));
  await store.connectionChanged('reconnecting');
  store.applyLog(record(600));
  const first = deferred();
  const calls = [];
  const api = { log: (before) => {
    calls.push(before);
    return calls.length === 1 ? first.promise : Promise.resolve({ items: [record(200), record(300)] });
  } };
  const recovery = store.connectionChanged('connected', api);
  store.applyLog(record(700));
  first.resolve({ items: [record(400), record(500), record(600)] });
  await recovery;
  assert.deepEqual(calls, [undefined, record(400).id]);
  assert.deepEqual(store.entries.map(e => e.id), [100, 200, 300, 400, 500, 600, 700].map(n => record(n).id));
  assert.equal(store.beforeId, undefined);
  assert.equal(store.gapBoundary, null);
});

test('lost reconnect boundary is reported, not called a complete recovery', async () => {
  const store = new LogStore();
  await store.connectionChanged('connected');
  store.applyLog(record(200));
  await store.connectionChanged('reconnecting');
  await store.connectionChanged('connected', { log: async () => ({ items: [] }) });
  assert.match(store.error, /could not be restored/);
  assert.equal(store.gapBoundary, record(200).id);
  assert.equal(store.historyExhausted, false);
});

test('another disconnect cancels stale recovery and restarts from the latest page', async () => {
  const store = new LogStore();
  await store.connectionChanged('connected');
  store.applyLog(record(100));
  await store.connectionChanged('reconnecting');
  const first = deferred();
  const stale = store.connectionChanged('connected', { log: () => first.promise });
  store.applyLog(record(300));
  await store.connectionChanged('reconnecting');
  const calls = [];
  await store.connectionChanged('connected', { log: async before => {
    calls.push(before);
    return { items: [record(100), record(200), record(300), record(400)] };
  } });
  first.resolve({ items: [record(500)] });
  await stale;
  assert.deepEqual(calls, [undefined]);
  assert.deepEqual(store.entries.map(e => e.id), [100, 200, 300, 400].map(n => record(n).id));
  assert.equal(store.gapBoundary, null);
});

test('failed or malformed history is visible and does not exhaust history', async () => {
  const store = new LogStore();
  await store.loadHistory({ log: async () => { throw { message: 'File logging is disabled' }; } });
  assert.equal(store.error, 'File logging is disabled');
  assert.equal(store.historyExhausted, false);
  await store.loadHistory({ log: async () => ({}) });
  assert.match(store.error, /Invalid log history response/);
  assert.equal(store.historyExhausted, false);
});

test('non-progressing pages are rejected before insertion', async () => {
  const store = new LogStore();
  store.applyLog(record(100));
  await store.loadHistory({ log: async () => ({ items: [record(100)] }) });
  assert.match(store.error, /did not move backwards/);
  assert.equal(store.entries.length, 1);
});

test('server wall time is displayed literally even in another timezone', () => {
  const previous = process.env.TZ;
  process.env.TZ = 'Pacific/Honolulu';
  try {
    const stamp = '2026-03-29T02:30:00.123';
    assert.equal(formatLogTime(stamp), '02:30:00.123');
    assert.equal(logDateKey(stamp), '2026-03-29');
    assert.ok(formatLogDate(stamp).includes('2026'));
  } finally {
    if(previous === undefined) delete process.env.TZ;
    else process.env.TZ = previous;
  }
});
