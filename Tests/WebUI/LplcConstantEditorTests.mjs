// Run: node --experimental-vm-modules --test Tests/WebUI/LplcConstantEditorTests.mjs
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { createContext, SourceTextModule, SyntheticModule } from 'node:vm';
import { ViewStore } from '../../Output/www/ide/services/view-store.js';
import { DocumentSession } from '../../Output/www/ide/components/document-session.js';
import { CELL, CANVAS_PAD } from '../../Output/www/ide/components/logram-scene.js';

// Keep the real component, scene, viewport and store; replace only Lit/DOM integration.
const template = (strings, ...values) => ({ strings, values });
class LitElement {
  constructor() {
    this.updateComplete = Promise.resolve();
    this.renderRoot = { querySelector: () => null };
  }
  addController() {}
  requestUpdate() {}
  disconnectedCallback() {}
}
const context = createContext({
  console,
  document: { activeElement: { tagName: 'X13-LOGRAM-DOCUMENT' } },
  window: { addEventListener() {}, removeEventListener() {} },
  customElements: { define() {} },
});
const modules = new Map();
function load(url) {
  if(!modules.has(url.href)) {
    modules.set(url.href, (async () => {
      let module;
      if(url.pathname.endsWith('/lib/lit-all.min.js')) {
        module = new SyntheticModule(['LitElement', 'html', 'svg', 'css'], function() {
          this.setExport('LitElement', LitElement);
          for(const name of ['html', 'svg', 'css']) this.setExport(name, template);
        }, { context, identifier: url.href });
      }
      else if(['/context-menu.js', '/breadcrumb-bar.js'].some((name) => url.pathname.endsWith(name))) {
        module = new SyntheticModule([], () => {}, { context, identifier: url.href });
      }
      else {
        module = new SourceTextModule(await readFile(url, 'utf8'), { context, identifier: url.href });
      }
      await module.link((specifier, parent) => load(new URL(specifier, parent.identifier)));
      return module;
    })());
  }
  return modules.get(url.href);
}
const module = await load(new URL('../../Output/www/ide/components/logram-document.js', import.meta.url));
await module.evaluate();
const { X13LogramDocument } = module.namespace;
const displayModule = await load(new URL('../../Output/www/ide/editors/display-editors.js', import.meta.url));
const { X13DevicePlcEditor } = displayModule.namespace;

function* templates(value) {
  if(Array.isArray(value)) {
    for(const item of value) yield* templates(item);
  }
  else if(value?.strings && value?.values) {
    yield value;
    for(const item of value.values) yield* templates(item);
  }
}
function find(doc, marker) {
  return [...templates(doc.render())].find((node) => node.strings.join('').includes(marker));
}
function binding(node, suffix) {
  assert.ok(node, `Missing template for ${suffix}`);
  const index = node.strings.findIndex((part) => part.endsWith(suffix));
  assert.notEqual(index, -1, `Missing binding ${suffix}`);
  return node.values[index];
}
function event(currentTarget, key) {
  return {
    currentTarget, key, prevented: false, stopped: false,
    preventDefault() { this.prevented = true; },
    stopPropagation() { this.stopped = true; },
  };
}
function fixture(displayValue = '') {
  const doc = new X13LogramDocument();
  const commits = [], rpcs = [];
  const block = { vid: 'lplc#/dev/node/pa1/F1', editor: 'LogramBlock', x: 2, y: 3, width: 5, height: 2 };
  const pin = { vid: block.vid + '/in.B', editor: 'LogramPin', name: 'B', pinDirection: 'in', pinIndex: 1, constantEditable: true, displayValue };
  doc.view = 'lplc';
  doc.api = { commit: async (vid, value) => commits.push([vid, value]), rpc: async (...args) => rpcs.push(args) };
  doc.rows = [block, pin];
  doc.willUpdate(new Map([['rows', []]]));
  return { doc, block, pin, commits, rpcs };
}
function packetFixture(displayValue = '') {
  const f = fixture(displayValue);
  const session = new DocumentSession(() => f.doc.api);
  const document = session.graphDocument('lplc', '/dev/node/pa1', true);
  f.block.level = 1;
  f.pin.level = 2;
  f.doc.store = document.store;
  f.doc.updated(new Map([['store', null]]));
  const send = (packet) => {
    const previous = f.doc.rows;
    session.route(JSON.parse(JSON.stringify(packet)), document);
    f.doc.willUpdate(new Map([['rows', previous]]));
  };
  send({ type: 'evnt.add', vid: 'lplc#/dev/node/pa1', editor: 'LogramCanvas', level: 0 });
  return { ...f, send };
}
function edit(f) {
  const label = find(f.doc, 'class="pin-constant"');
  const click = event({ getBoundingClientRect: () => ({ right: 180, top: 60, width: 20, height: 20 }) });
  binding(label, '@dblclick=')(click);
  assert.equal(click.stopped, true);
  assert.equal(click.prevented, true);
  const input = {
    value: f.doc.constantState.value,
    validationMessage: '', focused: false, selected: false,
    setCustomValidity(value) { this.validationMessage = value; },
    reportValidity() { this.reported = true; },
    focus() { this.focused = true; },
    select() { this.selected = true; },
  };
  f.doc.renderRoot.querySelector = () => input;
  f.doc.updated(new Map([['constantState', null]]));
  const field = find(f.doc, 'class="constant-input"');
  return {
    input,
    key: (key) => binding(field, '@keydown=').call(f.doc, event(input, key)),
    blur: () => binding(field, '@blur=').call(f.doc, event(input)),
  };
}

for(const cmd of ['add-twi:0', 'add-twi:1', 'add-serial:1', 'add-serial:2', 'add-block:LPLC/Math/ADD', 'add-pin:OUT:4', 'add-var:Int32']) {
  test(`LPLC menu sends ${cmd} with the captured canvas position`, () => {
    const f = fixture();
    const vid = 'lplc#/dev/node/Logic';
    const item = { cmd, enabled: true, willful: false };
    f.doc.menuState = { vid, items: [item], x: 120, y: 80, top: 7, left: 12 };
    binding(find(f.doc, '<x13-context-menu'), '@menu-command=').call(f.doc, { detail: { cmd, willful: false, item } });
    assert.equal(f.rpcs.length, 1);
    assert.equal(f.rpcs[0][0], vid);
    assert.equal(f.rpcs[0][1], cmd);
    assert.equal(f.rpcs[0][2].top, 7);
    assert.equal(f.rpcs[0][2].left, 12);
    assert.equal(f.doc.menuState, null);
    assert.equal(f.doc.nameState, null);
  });
}

for(const value of ['SDA', 22, 0, false]) {
  test(`LPLC block menu forwards typed constructor value ${value}`, () => {
    const f = fixture();
    const vid = 'lplc#/dev/node/Logic/TWI0';
    const cmd = 'set-arg';
    const item = { cmd, enabled: true, willful: false, args: { name: 'Pin', value } };
    f.doc.menuState = { vid, items: [item], x: 120, y: 80 };
    binding(find(f.doc, '<x13-context-menu'), '@menu-command=').call(f.doc, { detail: { cmd, willful: false, item } });
    assert.deepEqual(f.rpcs, [[vid, cmd, item.args]]);
    assert.equal(f.doc.menuState, null);
    assert.equal(f.doc.nameState, null);
  });
}

for(const cmd of ['add-twi:0', 'add-serial:1', 'add-serial:2', 'set-arg']) {
  test(`server Logram does not dispatch the LPLC-only command ${cmd}`, () => {
    const f = fixture();
    f.doc.view = 'logram';
    const item = { cmd, enabled: true, willful: false };
    f.doc.menuState = { vid: 'logram#/diagram', items: [item], x: 120, y: 80, top: 7, left: 12 };
    binding(find(f.doc, '<x13-context-menu'), '@menu-command=').call(f.doc, { detail: { cmd, willful: false, item } });
    assert.deepEqual(f.rpcs, []);
  });
}

test('constructor row menu uses its virtual target and scalar editor sends a typed value', async () => {
  const f = packetFixture();
  f.send({ type: 'evnt.add', ...f.block, height: 2, constructorRows: [{ name: 'Seed', label: 'Seed', position: 1, value: 7, text: '7.00', format: '{0:F2}', editable: true }] });
  const menus = [];
  f.doc.api.menu = async vid => { menus.push(vid); return { items: [] }; };
  const row = find(f.doc, 'class="constructor-value"');
  const e = { ...event({}), clientX: 120, clientY: 80 };
  await binding(row, '@contextmenu=')(e);
  const vid = f.block.vid + '/args.Seed';
  assert.deepEqual(menus, [vid]);
  const item = { cmd: 'edit-arg', args: { name: 'Seed', value: 7 } };
  binding(find(f.doc, '<x13-context-menu'), '@menu-command=').call(f.doc, { detail: { cmd: 'edit-arg', item } });
  assert.equal(f.doc.argumentState.text, '7');
  const input = { value: '12.5', setCustomValidity() {}, reportValidity() {} };
  const field = find(f.doc, 'class="argument-input name-input"');
  binding(field, '@input=').call(f.doc, event(input));
  assert.equal(f.doc.argumentState.preview, '12.50');
  await binding(field, '@keydown=').call(f.doc, event(input, 'Enter'));
  assert.equal(f.rpcs[0][0], vid);
  assert.equal(f.rpcs[0][1], 'set-arg');
  assert.equal(f.rpcs[0][2].value, 12.5);
  assert.equal(f.doc.argumentState, null);
});

function constructorPointerFixture(t, argument = {}) {
  const f = packetFixture();
  const constructorRows = [{ name: 'Seed', label: 'Seed', position: 1, value: 7, text: '7', editable: true, ...argument }];
  f.send({ type: 'evnt.add', ...f.block, height: 3, constructorRows });
  f.send({ type: 'evnt.add', ...f.pin, pinIndex: 2 });
  const listeners = new Map();
  t.mock.method(context.window, 'addEventListener', (name, callback) => listeners.set(name, callback));
  t.mock.method(context.window, 'removeEventListener', (name, callback) => {
    if(listeners.get(name) === callback) listeners.delete(name);
  });
  const surface = { getBoundingClientRect: () => ({ left: 100, top: 50 }) };
  f.doc.renderRoot.querySelector = selector => selector === '.surface' ? surface : null;
  const scale = 1;
  const row = find(f.doc, 'class="constructor-value"');
  const down = ({ fraction = 0.5, button = 0, ctrlKey = false } = {}) => {
    const e = { ...event({}), button, ctrlKey,
      clientX: 100 + ((f.block.x + f.block.width * fraction) * CELL + CANVAS_PAD) * scale,
      clientY: 50 + ((f.block.y + 1) * CELL + CANVAS_PAD) * scale };
    const index = row.strings.findIndex(part => part.endsWith('@pointerdown='));
    if(index >= 0) row.values[index].call(f.doc, e);
    if(!e.stopped) binding(find(f.doc, 'class="viewport"'), '@pointerdown=').call(f.doc, e);
    return e;
  };
  return { ...f, row, listeners, scale, down };
}

for(const [state, argument] of [['editable', {}], ['readonly', { editable: false }], ['invalid', { error: 'Value is unavailable' }]]) {
  for(const [area, fraction] of [['label', 0.2], ['middle', 0.5], ['value', 0.8]]) {
    test(`constructor ${state} ${area} area starts a block drag and commits position only`, t => {
      const f = constructorPointerFixture(t, argument);
      const down = f.down({ fraction });
      assert.equal(down.stopped, false);
      assert.equal(down.prevented, true);
      assert.equal(f.doc.selected.type, 'element');
      assert.deepEqual([...f.doc.dragState.vids], [f.block.vid]);
      assert.equal(f.doc.wireDraft, null);
      f.listeners.get('pointermove')({ clientX: down.clientX + 1.6 * CELL * f.scale, clientY: down.clientY + 2.2 * CELL * f.scale });
      assert.ok(Math.abs(f.doc.dragState.dx - 1.6) < 1e-10);
      assert.ok(Math.abs(f.doc.dragState.dy - 2.2) < 1e-10);
      assert.equal(f.doc.rows.find(row => row.vid === f.block.vid).x, f.block.x);
      assert.deepEqual(f.commits, []);
      f.listeners.get('pointerup')();
      assert.equal(f.doc.dragState, null);
      assert.equal(f.listeners.size, 0);
      assert.equal(f.commits.length, 1);
      assert.equal(f.commits[0][0], f.block.vid);
      assert.deepEqual(JSON.parse(JSON.stringify(f.commits[0][1])), { top: f.block.y + 2, left: f.block.x + 2 });
      assert.deepEqual(f.rpcs, []);
      assert.equal(f.doc.argumentState, null);
    });
  }
}

test('constructor row ctrl-click toggles block selection without dragging or editing', t => {
  const f = constructorPointerFixture(t);
  f.down({ ctrlKey: true });
  assert.equal(f.doc.selected?.type, 'element');
  assert.deepEqual([...f.doc.selected.vids], [f.block.vid]);
  assert.equal(f.doc.dragState, null);
  assert.equal(f.listeners.size, 0);
  f.down({ ctrlKey: true });
  assert.equal(f.doc.selected, null);
  assert.deepEqual(f.commits, []);
  assert.deepEqual(f.rpcs, []);
});

test('constructor row right-click opens the argument menu without starting a drag', async t => {
  const f = constructorPointerFixture(t);
  const menus = [];
  f.doc.api.menu = async vid => { menus.push(vid); return { items: [] }; };
  const down = f.down({ button: 2 });
  assert.equal(f.doc.dragState, null);
  assert.equal(f.doc.selected, null);
  assert.equal(f.listeners.size, 0);
  const e = { ...event({}), clientX: down.clientX, clientY: down.clientY };
  await binding(f.row, '@contextmenu=').call(f.doc, e);
  assert.equal(e.prevented, true);
  assert.equal(e.stopped, true);
  assert.deepEqual(menus, [f.block.vid + '/args.Seed']);
  assert.equal(f.doc.menuState.vid, f.block.vid + '/args.Seed');
  assert.deepEqual(f.commits, []);
  assert.deepEqual(f.rpcs, []);
});

function configRows(bus = 0, sda = 'SDA', scl = null) {
  return [bus, sda, scl].map((value, index) => ({ name: ['TwiNr', 'SdaPin', 'SclPin'][index], label: ['TWI', 'SDA', 'SCL'][index],
    position: index + 1, value, text: value == null ? '<unset>' : String(value), format: null, editable: true,
    ...(value == null ? { error: 'required constructor argument is unset' } : {}) }));
}
for(const constructorRows of [configRows(0, null, 0), configRows(1, 'SDA_ALIAS', 'SCL'), configRows(12, 21, 22)]) {
  test(`constructor rows ${constructorRows[0].text} render below header without phantom ports`, async () => {
    const f = packetFixture();
    Object.assign(f.block, { name: 'RenamedController', height: 4, width: 8, icon: '/api/icons/LPLC/TwiMaster.svg', constructorRows });
    f.send({ type: 'evnt.add', ...f.block });
    const sceneModule = await load(new URL('../../Output/www/ide/components/logram-scene.js', import.meta.url));
    const { buildScene, blockGeometry } = sceneModule.namespace;
    const geometry = blockGeometry(f.block);
    const icon = find(f.doc, 'class="block-icon"');
    assert.equal(binding(icon, ' href='), f.block.icon);
    assert.equal(binding(icon, ' x=') + 8, (geometry.x0 + geometry.x1) / 2);
    assert.equal(binding(icon, ' y=') + 8, geometry.anchorY);
    const labels = [...templates(f.doc.render())].filter((node) => node.strings.join('').includes('class="constructor-value"'));
    assert.equal(labels.length, 3);
    labels.forEach((label, index) => {
      assert.ok(label.values.includes(constructorRows[index].label));
      assert.ok(label.values.includes(constructorRows[index].text));
      assert.equal(binding(label, 'text-anchor="end" x='), geometry.x1 - 6);
      assert.equal(binding(label, '<text x='), geometry.x0 + 6);
    });
    assert.equal(binding(find(f.doc, '<rect class="block-body '), ' height='), 70);
    assert.equal(buildScene(f.doc.rows).pins.length, 0);
    for(const name of ['pin-hit', 'pin-constant', 'constructor-title', 'constructor-divider']) {
      assert.equal(find(f.doc, `class="${name}"`), undefined);
    }
  });
}

for(const type of ['evnt.add', 'evnt.upd']) {
  test(`constructor values and icon update through ${type} without changing geometry`, () => {
    const f = packetFixture();
    Object.assign(f.block, { height: 4, constructorRows: configRows(1, 21, 22), icon: '/api/icons/LPLC/TwiMaster.svg' });
    f.send({ type: 'evnt.add', ...f.block });
    const constructorRows = configRows(0, 'SDA_ALIAS');
    const row = type === 'evnt.add' ? f.block : { vid: f.block.vid };
    f.send({ type, ...row, constructorRows, icon: '/ide/icons/ty_topic.png' });
    const labels = [...templates(f.doc.render())].filter((node) => node.strings.join('').includes('class="constructor-value"'));
    assert.ok(labels[1].values.includes('SDA_ALIAS'));
    assert.ok(labels[2].values.includes('<unset>'));
    assert.equal(binding(find(f.doc, 'class="block-icon"'), ' href='), '/ide/icons/ty_topic.png');
    assert.equal(binding(find(f.doc, '<rect class="block-body '), ' height='), 70);
    assert.deepEqual(f.doc.rows.find((row) => row.vid === f.block.vid).constructorRows, constructorRows);
    assert.deepEqual(f.commits, []);
    assert.deepEqual(f.rpcs, []);
  });
}

test('configuration values survive movement and disappear when a replacement omits them', () => {
  const f = packetFixture();
  const constructorRows = configRows(0, 0, 'SCL');
  f.send({ type: 'evnt.add', ...f.block, height: 4, constructorRows });
  f.send({ type: 'evnt.upd', vid: f.block.vid, x: 8, y: 7 });
  assert.deepEqual(f.doc.rows.find((row) => row.vid === f.block.vid).constructorRows, constructorRows);
  f.send({ type: 'evnt.add', ...f.block });
  assert.equal(find(f.doc, 'class="constructor-value"'), undefined);
  assert.equal(Object.hasOwn(f.doc.rows.find((row) => row.vid === f.block.vid), 'constructorRows'), false);
});

test('configuration presentation can be explicitly cleared by a null patch', () => {
  const f = packetFixture();
  f.send({ type: 'evnt.add', ...f.block, height: 4, constructorRows: configRows(0, 0, 4) });
  assert.ok(find(f.doc, 'class="constructor-value"'));
  f.send({ type: 'evnt.upd', vid: f.block.vid, constructorRows: null });
  assert.equal(find(f.doc, 'class="constructor-value"'), undefined);
  assert.equal(f.doc.rows.find((row) => row.vid === f.block.vid).constructorRows, null);
});

for(const type of ['evnt.add', 'evnt.upd']) {
  test(`constructor labels update via ${type} and Tick ports remain below their section`, () => {
    const f = packetFixture('5');
    f.block.name = 'Configured';
    f.block.height = 3;
    f.block.constructorArgs = ['Seed: 7'];
    f.pin.pinIndex = 2;
    f.send({ type: 'evnt.add', ...f.block });
    f.send({ type: 'evnt.add', ...f.pin });
    assert.equal(binding(find(f.doc, 'class="constructor-argument"'), ' y='), 75);
    assert.equal(binding(find(f.doc, 'class="pin-constant"'), ' y='), 90);
    assert.equal(binding(find(f.doc, 'class="constructor-divider"'), '\n            y1='), 81);
    const row = type === 'evnt.add' ? f.block : { vid: f.block.vid };
    f.send({ type, ...row, constructorArgs: ['Seed: 9'] });
    assert.ok(find(f.doc, 'class="constructor-argument"').values.includes('Seed: 9'));
    assert.equal(binding(find(f.doc, 'class="pin-constant"'), ' y='), 90);
    assert.deepEqual(f.commits, []);
    assert.deepEqual(f.rpcs, []);
  });
}

test('constructor labels survive position patches and disappear when a replacement omits them', () => {
  const f = packetFixture();
  f.send({ type: 'evnt.add', ...f.block, height: 3, constructorArgs: ['First: 1', 'Second: 0'] });
  f.send({ type: 'evnt.upd', vid: f.block.vid, x: 8, y: 7 });
  const label = find(f.doc, 'class="constructor-argument"');
  assert.ok(label.values.includes('First: 1'));
  assert.equal(binding(label, ' y='), 147);
  f.send({ type: 'evnt.add', ...f.block });
  assert.equal(find(f.doc, 'class="constructor-argument"'), undefined);
  assert.equal(Object.hasOwn(f.doc.rows.find((row) => row.vid === f.block.vid), 'constructorArgs'), false);
});

for(const value of [[], null]) {
  test(`constructor labels can be explicitly cleared by a ${JSON.stringify(value)} patch`, () => {
    const f = packetFixture();
    f.send({ type: 'evnt.add', ...f.block, constructorArgs: ['Seed: 7'] });
    assert.ok(find(f.doc, 'class="constructor-argument"'));
    f.send({ type: 'evnt.upd', vid: f.block.vid, constructorArgs: value });
    assert.equal(find(f.doc, 'class="constructor-argument"'), undefined);
    assert.deepEqual(f.doc.rows.find((row) => row.vid === f.block.vid).constructorArgs, value);
  });
}

test('blocks without constructor metadata keep their original presentation', () => {
  const f = packetFixture('5');
  f.send({ type: 'evnt.add', ...f.block });
  f.send({ type: 'evnt.add', ...f.pin });
  assert.equal(find(f.doc, 'class="constructor-title"'), undefined);
  assert.equal(find(f.doc, 'class="constructor-argument"'), undefined);
  assert.equal(binding(find(f.doc, 'class="pin-constant"'), ' y='), 72);
});

test('unlinked input label is gray, pin-centered and editable even when unset', () => {
  const f = fixture();
  const label = find(f.doc, 'class="pin-constant"');
  assert.equal(binding(label, ' y='), 72);
  assert.ok(label.strings.join('').includes('dominant-baseline="middle"'));
  assert.ok(label.values.includes('…'));
  assert.match(X13LogramDocument.styles.strings.join(''), /\.pin-constant\s*\{[^}]*fill:\s*#6b7280/);
  assert.equal(find(f.doc, 'class="pin-trace"'), undefined);
  const down = event({});
  binding(label, '@pointerdown=')(down);
  assert.equal(down.stopped, true);
  const editor = edit(f);
  assert.equal(f.doc.constantState.y, 70);
  assert.equal(editor.input.focused, true);
  assert.equal(editor.input.selected, true);
  assert.deepEqual(f.commits, []);
});

for(const [text, expected] of [['0', 0], ['-17', -17], ['3.5', 3.5], ['.25', 0.25], ['-2.5e-3', -0.0025]]) {
  test(`Enter commits ${text} as a number`, async () => {
    const f = fixture();
    const editor = edit(f);
    editor.input.value = text;
    await editor.key('Enter');
    assert.deepEqual(f.commits, [[f.pin.vid, expected]]);
    assert.equal(f.doc.constantState, null);
    await editor.blur();
    assert.equal(f.commits.length, 1);
  });
}

for(const text of ['', 'abc', 'NaN', 'Infinity', '1e309', '0x10', '1,5']) {
  test(`invalid numeric draft ${JSON.stringify(text)} is not saved`, async () => {
    const f = fixture('5');
    const editor = edit(f);
    editor.input.value = text;
    await editor.key('Enter');
    assert.deepEqual(f.commits, []);
    assert.ok(f.doc.constantState);
    assert.ok(editor.input.validationMessage);
    assert.equal(editor.input.reported, true);
    await editor.blur();
    assert.equal(f.doc.constantState, null);
    assert.deepEqual(f.commits, []);
  });
}

test('leaving a changed valid value saves it', async () => {
  const f = fixture('5');
  const editor = edit(f);
  editor.input.value = '-1.25';
  await editor.blur();
  assert.deepEqual(f.commits, [[f.pin.vid, -1.25]]);
  assert.equal(f.doc.constantState, null);
});

test('Escape cancels and typing Delete/Backspace cannot delete diagram elements', async () => {
  const f = fixture('5');
  f.doc.selected = { type: 'element', vids: new Set([f.block.vid]) };
  const editor = edit(f);
  editor.input.value = '9';
  for(const key of ['Delete', 'Backspace']) {
    await editor.key(key);
    f.doc.onKeyDown({ key });
  }
  assert.deepEqual(f.rpcs, []);
  await editor.key('Escape');
  await editor.blur();
  assert.equal(f.doc.constantState, null);
  assert.deepEqual(f.commits, []);
});

test('unchanged unset and boolean values are not silently converted to zero or numbers', async () => {
  for(const value of ['', 'true', 'false']) {
    const f = fixture(value);
    const editor = edit(f);
    await editor.key('Enter');
    assert.deepEqual(f.commits, []);
    assert.equal(f.doc.constantState, null);
  }
});

test('a newly linked or removed input cancels the editor', async () => {
  for(const removed of [false, true]) {
    const f = fixture('5');
    const editor = edit(f);
    editor.input.value = '8';
    if(removed) f.doc.rows = [f.block];
    else f.pin.constantEditable = false;
    f.doc.willUpdate(new Map([['rows', []]]));
    assert.equal(f.doc.constantState, null);
    await editor.key('Enter');
    assert.deepEqual(f.commits, []);
    assert.equal(find(f.doc, 'class="pin-constant"'), undefined);
  }
});

test('failed commits retain the editor and show the server error', async () => {
  const f = fixture('5');
  f.doc.api.commit = async () => { throw new Error('Input is linked'); };
  const editor = edit(f);
  editor.input.value = '7';
  await editor.key('Enter');
  assert.ok(f.doc.constantState);
  assert.equal(f.doc.constantState.saving, false);
  assert.equal(editor.input.value, '7');
  assert.equal(editor.input.validationMessage, 'Input is linked');
});

test('Enter and blur do not duplicate an in-flight commit', async () => {
  const f = fixture();
  let complete;
  f.doc.api.commit = (vid, value) => {
    f.commits.push([vid, value]);
    return new Promise((resolve) => { complete = resolve; });
  };
  const editor = edit(f);
  editor.input.value = '3';
  const pending = editor.key('Enter');
  await editor.key('Enter');
  await editor.blur();
  assert.equal(f.commits.length, 1);
  complete();
  await pending;
  assert.equal(f.doc.constantState, null);
});

test('regular Logram trace labels and variable-style elements do not get the editor', () => {
  const f = fixture('5');
  f.doc.view = 'logram';
  f.pin.trace = true;
  assert.equal(find(f.doc, 'class="pin-constant"'), undefined);
  assert.equal(binding(find(f.doc, 'class="pin-trace"'), ' y='), 64);
  f.doc.view = 'lplc';
  f.doc.rows = [{ ...f.block, editor: 'LogramVariable', constantEditable: true, displayValue: '5' }];
  f.doc.willUpdate(new Map([['rows', []]]));
  assert.equal(find(f.doc, 'class="pin-constant"'), undefined);
});

test('ViewStore retains eligibility and value changes without affecting legacy rows', () => {
  const store = new ViewStore();
  const row = { vid: 'lplc#/dev/node/pa1/F1/in.B', editor: 'LogramPin', level: 2 };
  store.add({ ...row, constantEditable: true, displayValue: '' });
  assert.equal(store.rows[0].constantEditable, true);
  assert.equal(store.rows[0].displayValue, '');
  store.update({ vid: row.vid, constantEditable: false, sourceVid: 'lplc#/dev/node/pa1/A1/out.Q' });
  assert.equal(store.rows[0].constantEditable, false);
  store.update({ vid: row.vid, constantEditable: true, displayValue: '3.5', sourceVid: '' });
  assert.equal(store.rows[0].constantEditable, true);
  assert.equal(store.rows[0].displayValue, '3.5');
  store.add({ ...row, constantEditable: false });
  assert.equal(store.rows[0].constantEditable, false);
  assert.equal(Object.hasOwn(store.rows[0], 'displayValue'), false);
  store.add(row);
  assert.equal(Object.hasOwn(store.rows[0], 'constantEditable'), false);
});

test('LPLC output trace renders beside the output without an inline editor', () => {
  const f = fixture('5');
  const output = { vid: f.block.vid + '/out.Q', editor: 'LogramPin', name: 'Q', pinDirection: 'out', pinIndex: 0, trace: true, displayValue: '12.5' };
  f.doc.rows.push(output);
  f.doc.willUpdate(new Map([['rows', []]]));
  const trace = find(f.doc, 'class="pin-trace"');
  assert.ok(trace.values.includes('12.5'));
  assert.equal(binding(trace, 'text-anchor='), 'start');
  assert.equal(binding(trace, ' y='), 40);
  assert.ok(find(f.doc, 'class="pin-constant"'), 'input constant editor remains available');
  output.trace = false;
  assert.equal(find(f.doc, 'class="pin-trace"'), undefined);
});

test('LPLC hardware Pin uses the existing variable-row output trace label', () => {
  const f = fixture();
  f.doc.rows = [{ ...f.block, editor: 'LogramVariable', name: 'ANALOG_PA0', trace: true, displayValue: '17' }];
  f.doc.willUpdate(new Map([['rows', []]]));
  const trace = find(f.doc, 'class="pin-trace"');
  assert.ok(trace.values.includes('17'));
  assert.ok(trace.strings.join('').includes('text-anchor="start"'));
  assert.equal(find(f.doc, 'class="pin-constant"'), undefined);
});

test('LPLC trace value patches and explicit off preserve existing routes', () => {
  const store = new ViewStore();
  const vid = 'lplc#/dev/node/Logic/OUT_PA5';
  const sourceVid = 'lplc#/dev/node/Logic/A1/out.Q';
  store.add({ vid, editor: 'LogramVariable', level: 1, trace: false, displayValue: '' });
  store.update({ vid, sourceVid, sourceLocal: true, sourcePath: [{ x: 2, y: 3 }, { x: 4, y: 3 }] });
  store.update({ vid, trace: true, displayValue: '0' });
  assert.equal(store.rows[0].trace, true);
  assert.equal(store.rows[0].displayValue, '0');
  const route = store.rows[0].sourcePath;
  store.update({ vid, trace: true, displayValue: '3.5' });
  assert.equal(store.rows[0].sourceVid, sourceVid);
  assert.equal(store.rows[0].sourcePath, route);
  store.update({ vid, trace: false, displayValue: '' });
  assert.equal(store.rows[0].trace, false);
  assert.equal(store.rows[0].displayValue, '');
  assert.equal(store.rows[0].sourceVid, sourceVid);
});

function plcFixture(plcModified = null) {
  const f = fixture('5');
  f.canvas = { vid: 'lplc#/dev/node/Logic', editor: 'LogramCanvas', level: 0, value: 2, plcModified };
  f.doc.rows = [f.canvas, ...f.doc.rows];
  f.doc.willUpdate(new Map([['rows', []]]));
  return f;
}

function compactEditor(f) {
  const controls = find(f.doc, 'class="plc-controls"');
  const editor = new X13DevicePlcEditor();
  editor.compact = binding(controls, '.compact=');
  editor.modified = binding(controls, '.modified=');
  editor.state = binding(controls, '.state=');
  editor.rpc = binding(controls, '.rpc=');
  return editor;
}

for(const [value, kind] of [[null, 'unknown'], [false, 'matches'], [true, 'modified']]) {
  test(`PLC comparison ${value} uses a ${kind} status icon with a tooltip`, () => {
    const f = plcFixture(value);
    const editor = compactEditor(f);
    assert.equal(editor.compact, true);
    const statuses = find(editor, 'aria-label="PLC status"');
    const kinds = statuses.strings.flatMap((part, index) => part.endsWith('class="plc-status ') ? [statuses.values[index]] : []);
    assert.deepEqual(Array.from(kinds), ['error', kind]);
    const titles = statuses.strings.flatMap((part, index) => part.endsWith(' title=') ? [statuses.values[index]] : []);
    assert.equal(titles[0], 'PLC: stop');
    assert.match(titles[1], /acknowledged/);
    assert.equal([...templates(editor.render())].filter((node) => node.strings.join('').includes('<svg')).length, 6);
    assert.equal(find(editor, 'class="text"'), undefined);
  });
}

test('the compact action icons send all four commands to the LPLC program root', async () => {
  const f = plcFixture(false);
  const editor = compactEditor(f);
  const buttons = [...templates(editor.render())].filter((node) => node.strings.join('').includes('class="plc-action"'));
  assert.equal(buttons.length, 4);
  const names = ['Build', 'Run', 'Start', 'Stop'];
  for(let i = 0; i < buttons.length; i++) {
    assert.equal(binding(buttons[i], 'aria-label='), names[i]);
    assert.ok(binding(buttons[i], 'title=').startsWith(names[i]));
    assert.ok([...templates(buttons[i])].some((node) => node.strings.join('').includes('<svg')));
    const click = event({});
    binding(buttons[i], '@click=')(click);
    assert.equal(click.prevented, true);
    assert.equal(click.stopped, true);
  }
  await Promise.resolve();
  assert.deepEqual(f.rpcs, names.map((name) => [f.canvas.vid, `action:MQTT_SN.PLC.${name}`]));
  assert.deepEqual(f.commits, []);
});

test('default pa0 controls retain their text buttons', () => {
  const editor = new X13DevicePlcEditor();
  const buttons = [...templates(editor.render())].filter((node) => node.strings.join('').includes('class="plc-action"'));
  assert.deepEqual(buttons.map((button) => button.values.at(-1)), ['Build', 'Run', 'Start', 'Stop']);
  assert.equal(find(editor, 'class="compact"'), undefined);
});

test('PLC groups follow Reset view and have spacing between groups', () => {
  const f = plcFixture();
  const document = f.doc.render();
  const index = document.values.findIndex((value) => value?.strings?.join('').includes('class="plc-controls"'));
  assert.ok(index >= 0);
  assert.match(document.strings[index], /title="Reset view">⟲<\/button>\s*$/);
  assert.match(document.strings[index + 1], /^\s*<\/div>/);
  assert.match(X13LogramDocument.styles.strings.join(''), /\.plc-controls\s*\{\s*margin-top:\s*12px/);
  const css = [...templates(X13DevicePlcEditor.styles)].map((t) => t.strings.join('')).join('');
  assert.match(css, /\.compact\s*\{\s*gap:\s*12px/);
  assert.equal(find(f.doc, 'class="plc-toolbar"'), undefined);
});

test('PLC actions stay out of ordinary Logram and leave inline constants available', () => {
  const f = plcFixture();
  edit(f);
  assert.ok(f.doc.constantState);
  assert.ok(find(f.doc, 'class="plc-controls"'));
  f.doc.view = 'logram';
  assert.equal(find(f.doc, 'class="plc-controls"'), undefined);
  f.doc.view = 'lplc';
  f.doc.rows = f.doc.rows.filter((row) => row !== f.canvas);
  assert.equal(find(f.doc, 'class="plc-controls"'), undefined);
});

test('PLC action failures are handled without an unhandled rejection', async () => {
  const f = plcFixture();
  const warnings = [];
  const original = context.console;
  context.console = { ...console, warn: (...args) => warnings.push(args) };
  try {
    f.doc.api.rpc = async () => { throw new Error('Device unavailable'); };
    await binding(find(f.doc, 'class="plc-controls"'), '.rpc=')('action:MQTT_SN.PLC.Run');
    assert.equal(warnings.length, 1);
    assert.equal(warnings[0][2], f.canvas.vid);
    assert.match(warnings[0][3].message, /unavailable/);
  }
  finally { context.console = original; }
});

test('ViewStore preserves all comparison states and canvas replacement keeps its descendants', () => {
  const f = plcFixture();
  const store = new ViewStore();
  store.add(f.canvas);
  store.add({ ...f.block, level: 1 });
  store.add({ ...f.pin, level: 2 });
  for(const value of [false, true, null]) {
    store.update({ vid: f.canvas.vid, plcModified: value });
    assert.equal(store.rows[0].plcModified, value);
    store.add({ ...f.canvas, plcModified: value, value: 1 });
    assert.equal(store.rows[0].plcModified, value);
    assert.equal(store.rows[0].value, 1);
    assert.equal(store.rows.length, 3);
    f.doc.rows = store.rows;
    f.doc.willUpdate(new Map([['rows', []]]));
    assert.ok(find(f.doc, 'class="plc-controls"'));
  }
  store.add({ vid: f.canvas.vid, editor: 'LogramCanvas', level: 0 });
  assert.equal(Object.hasOwn(store.rows[0], 'plcModified'), false);
});

test('opening builds only the current program once, not on rendering or status updates', () => {
  const f = plcFixture();
  const update = () => f.doc.updated(new Map([['rows', []]]));
  update();
  assert.deepEqual(f.rpcs, [[f.canvas.vid, 'action:MQTT_SN.PLC.Build']]);
  f.canvas.plcModified = false;
  f.doc.render();
  update();
  update();
  assert.equal(f.rpcs.length, 1);
  f.doc.rows = [];
  update();
  f.doc.rows = [f.canvas];
  update();
  assert.equal(f.rpcs.length, 2);
  const other = { ...f.canvas, vid: 'lplc#/dev/other/Logic' };
  f.doc.rows = [other];
  update();
  assert.deepEqual(f.rpcs[2], [other.vid, 'action:MQTT_SN.PLC.Build']);
  f.doc.disconnectedCallback();
  update();
  assert.equal(f.rpcs.length, 4);
});

test('automatic Build waits for the API and never applies to ordinary Logram', () => {
  const f = plcFixture();
  const api = f.doc.api;
  f.doc.api = null;
  f.doc.updated(new Map([['rows', []]]));
  assert.deepEqual(f.rpcs, []);
  f.doc.view = 'logram';
  f.doc.api = api;
  f.doc.updated(new Map([['api', null]]));
  assert.deepEqual(f.rpcs, []);
  f.doc.view = 'lplc';
  f.doc.updated(new Map([['view', 'logram']]));
  assert.deepEqual(f.rpcs, [[f.canvas.vid, 'action:MQTT_SN.PLC.Build']]);
});
