import './default-editor.js';
import './boolean-editor.js';
import './scalar-editors.js';
import './attribute-editor.js';
import './js-editor.js';
import './display-editors.js';
import './enum-editor.js';
import './editor-selector.js';
import './connection-status-editor.js';

const EDITOR_TAGS = Object.freeze({
  Attribute: 'x13-attribute-editor',
  Boolean: 'x13-boolean-editor',
  String: 'x13-string-editor',
  Integer: 'x13-integer-editor',
  Double: 'x13-double-editor',
  Hexadecimal: 'x13-hexadecimal-editor',
  Time: 'x13-time-editor',
  ByteArray: 'x13-byte-array-editor',
  JS: 'x13-js-editor',
  Version: 'x13-version-editor',
  MsStatus: 'x13-ms-status-editor',
  PortStatus: 'x13-port-status-editor',
  EsStatus: 'x13-es-status-editor',
  DevicePLC: 'x13-device-plc-editor',
  Enum: 'x13-enum-editor',
  Editor: 'x13-editor-selector',
  ConnectionStatus: 'x13-connection-status-editor',
});

export const DEFAULT_EDITOR_TAG = 'x13-default-editor';

export function editorTagFor(editorName) {
  if(!editorName) return DEFAULT_EDITOR_TAG;
  const tag = EDITOR_TAGS[editorName];
  if(!tag) return DEFAULT_EDITOR_TAG;
  return customElements.get(tag) ? tag : DEFAULT_EDITOR_TAG;
}

export function registeredEditorNames() {
  return Object.keys(EDITOR_TAGS);
}

export function normalizeEditorView(view) {
  if(view === 'row' || view === 'logram') return 'row';
  if(view === 'value') return 'value';
  return 'value';
}
