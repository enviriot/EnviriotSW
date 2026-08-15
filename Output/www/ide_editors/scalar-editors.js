import { LitElement, html, css } from '../lib/lit-all.min.js';
import { formatDefault, autoGrowTextarea } from './editor-format.js';

export class X13ScalarTextEditor extends LitElement {
  static properties = {
    view: {},
    readonly: { type: Boolean, reflect: true },
    selected: { type: Boolean, reflect: true },
    state: { attribute: false },
  };

  static styles = css`
    :host {
      cursor: text;
      display: inline-flex;
      max-width: 100%;
      vertical-align: middle;
    }
    :host([readonly]) {
      cursor: pointer;
    }
    :host([wide]) {
      width: 100%;
    }
    :host([numeric]) {
      max-width: 100%;
    }
    input {
      background: transparent;
      border: 0;
      border-radius: 0;
      box-sizing: border-box;
      color: #1f2933;
      font: inherit;
      height: 24px;
      line-height: 22px;
      min-width: 0;
      outline: 0;
      padding: 0 4px;
      width: 100%;
    }
    input.text-editor { text-align: left; }
    input.number-editor,
    input.time-editor {
      max-width: 100%;
      min-width: 102px;
      width: auto;
    }
    input.number-editor { text-align: right; }
    input.time-editor { text-align: center; }
    :host(:not([readonly])) input {
      border-left: 1px solid #000;
      border-right: 1px solid #000;
    }
    :host([selected]) input { background: #fff; }
    input:disabled { color: #475569; }
    :host([multiline]) {
      width: 100%;
    }
    textarea.text-editor-multiline {
      background: transparent;
      border: 0;
      border-radius: 0;
      box-sizing: border-box;
      color: #1f2933;
      display: block;
      font: inherit;
      line-height: 22px;
      margin: 0;
      max-height: 70vh;
      min-height: 24px;
      min-width: 0;
      outline: 0;
      overflow-y: auto;
      padding: 3px 4px;
      resize: none;
      white-space: pre-wrap;
      width: 100%;
      word-break: break-word;
    }
    :host(:not([readonly])) textarea.text-editor-multiline {
      border-left: 1px solid #000;
      border-right: 1px solid #000;
    }
    :host([selected]) textarea.text-editor-multiline { background: #fff; }
    textarea.text-editor-multiline:disabled { color: #475569; }
  `;

  constructor(options = {}) {
    super();
    this.view = options.view || 'value';
    this.commit = typeof options.commit === 'function' ? options.commit : () => {};
    this.readonly = true;
    this.selected = false;
    this.state = undefined;
    this.#draft = null;
    this.#lastSentDraft = null;
    this.#suppressBlur = false;
    this.#pendingSelection = null;
    this.#pendingInputText = null;
  }

  willUpdate(changed) {
    this.toggleAttribute('wide', this.fullWidth());
    this.toggleAttribute('numeric', this.numericLayout());
    this.toggleAttribute('multiline', this.#isMultiline());
    if(changed.has('state')) {
      this.#draft = null;
      this.#lastSentDraft = null;
      this.#suppressBlur = false;
    }
  }

  updated(changed) {
    if(changed.has('state')) this.#restorePendingSelection();
    if(this.#isMultiline()) this.#growTextarea();
  }

  render() {
    if(this.#isMultiline()) {
      return html`<textarea
        class="text-editor-multiline"
        rows="1"
        .value=${this.#currentText()}
        ?disabled=${this.readonly}
        @input=${this.#onMultilineInput}
        @keydown=${this.#onMultilineKeyDown}
        @blur=${this.#onBlur}></textarea>`;
    }
    return html`<input
      class=${this.inputClass()}
      .value=${this.#currentText()}
      ?disabled=${this.readonly}
      @beforeinput=${this.#onBeforeInput}
      @input=${this.#onInput}
      @keydown=${this.#onKeyDown}
      @wheel=${this.#onWheel}
      @blur=${this.#onBlur}>`;
  }

  inputClass() { return 'text-editor'; }
  fullWidth() { return true; }
  numericLayout() { return false; }
  // Only String opts in (scalar-editors.js bottom) - a multi-line box makes no sense
  // for Integer/Double/Hexadecimal/Time/ByteArray, so this is off by default here.
  multilineInValueView() { return false; }
  parse(text) { return { ok: true, value: text }; }
  format(value) { return formatDefault(value); }
  allowedInputPattern() { return /[\s\S]/; }
  wheelStep(_text, _caret) { return null; }
  formatWheelValue(value, _text, _caret) { return this.format(value); }
  wheelValue(parsedValue, direction, step, _text, _caret) {
    return parsedValue + direction * step;
  }

  restoreConfirmed(input = null) {
    this.#draft = null;
    this.#lastSentDraft = null;
    this.#suppressBlur = true;
    if(input) input.value = this.format(this.state);
    if(input && this.#isMultiline()) autoGrowTextarea(input);
    this.requestUpdate();
  }

  #isMultiline() {
    return this.view === 'value' && this.multilineInValueView();
  }

  #growTextarea() {
    autoGrowTextarea(this.renderRoot?.querySelector('textarea.text-editor-multiline'));
  }

  #onMultilineInput(e) {
    this.#draft = e.currentTarget.value;
    this.#suppressBlur = false;
    autoGrowTextarea(e.currentTarget);
  }

  // Escape reverts, Ctrl/Cmd+Enter commits, plain Enter inserts a newline (default
  // textarea behavior, not prevented) - same convention js-editor.js already uses.
  #onMultilineKeyDown(e) {
    if(e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      this.restoreConfirmed(e.currentTarget);
      return;
    }
    if(e.key !== 'Enter' || !(e.ctrlKey || e.metaKey)) return;
    e.preventDefault();
    this.#tryCommit();
    this.#suppressBlur = true;
  }

  #currentText() {
    return this.#draft ?? this.format(this.state);
  }

  #onBeforeInput(e) {
    if(this.readonly || !e.data) return;
    const pattern = this.allowedInputPattern();
    for(const ch of e.data) {
      pattern.lastIndex = 0;
      if(!pattern.test(ch)) {
        e.preventDefault();
        return;
      }
    }
  }

  #onInput(e) {
    this.#draft = e.currentTarget.value;
    this.#suppressBlur = false;
  }

  #onKeyDown(e) {
    if(e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      this.restoreConfirmed(e.currentTarget);
      return;
    }
    if(e.key !== 'Enter') return;
    e.preventDefault();
    this.#tryCommit();
    this.#suppressBlur = true;
  }

  #onWheel(e) {
    if(this.readonly) return;
    const input = e.currentTarget;
    const text = input.value;
    const caret = input.selectionStart ?? text.length;
    const step = this.wheelStep(text, caret);
    if(step === null || step === 0) return;
    const parsed = this.parse(text);
    if(!parsed.ok || typeof parsed.value !== 'number' || !Number.isFinite(parsed.value)) return;
    e.preventDefault();
    const direction = e.deltaY < 0 ? 1 : -1;
    const nextValue = this.wheelValue(parsed.value, direction, step, text, caret);
    if(typeof nextValue !== 'number' || !Number.isFinite(nextValue)) return;
    this.#pendingSelection = { start: caret, end: caret };
    this.#pendingInputText = this.formatWheelValue(nextValue, text, caret);
    input.value = this.#pendingInputText;
    this.#restorePendingSelection();
    this.commit(nextValue);
  }

  #onBlur() {
    if(this.#suppressBlur) {
      this.#suppressBlur = false;
      return;
    }
    this.#tryCommit();
  }

  #restorePendingSelection() {
    if(!this.#pendingSelection) return;
    const input = this.renderRoot?.querySelector('input');
    if(!input) return;
    if(this.#pendingInputText !== null) input.value = this.#pendingInputText;
    const start = Math.min(this.#pendingSelection.start, input.value.length);
    const end = Math.min(this.#pendingSelection.end, input.value.length);
    try { input.setSelectionRange(start, end); }
    catch { }
  }

  #tryCommit() {
    if(this.readonly || this.#draft === null) return;
    const text = this.#currentText();
    if(text === this.#lastSentDraft) return;
    const parsed = this.parse(text);
    if(!parsed.ok) {
      this.restoreConfirmed();
      return;
    }
    if(scalarEditorValuesEqual(parsed.value, this.state)) {
      this.#draft = null;
      this.#lastSentDraft = null;
      this.requestUpdate();
      return;
    }
    this.#lastSentDraft = text;
    Promise.resolve(this.commit(parsed.value)).catch(() => {
      if(this.#lastSentDraft === text) this.restoreConfirmed();
    });
  }

  #draft;
  #lastSentDraft;
  #suppressBlur;
  #pendingSelection;
  #pendingInputText;
}


function scalarEditorValuesEqual(left, right) {
  if(left === right) return true;
  if(typeof left === 'number' && typeof right === 'number') {
    return Object.is(left, right) || Math.abs(left - right) < Number.EPSILON;
  }
  return false;
}

export class X13StringEditor extends X13ScalarTextEditor {
  parse(text) { return { ok: true, value: String(text ?? '') }; }
  multilineInValueView() { return true; }
}

export class X13IntegerEditor extends X13ScalarTextEditor {
  inputClass() { return 'number-editor'; }
  fullWidth() { return false; }
  numericLayout() { return true; }
  allowedInputPattern() { return /[0-9+-]/; }

  parse(text) {
    const value = String(text ?? '').trim();
    if(value === '') return { ok: true, value: 0 };
    if(!/^[-+]?\d+$/.test(value)) return { ok: false };
    const parsed = Number.parseInt(value, 10);
    return Number.isSafeInteger(parsed) ? { ok: true, value: parsed } : { ok: false };
  }

  wheelStep(text, caret) {
    return decimalIntegerStep(text, caret);
  }
}

export class X13DoubleEditor extends X13ScalarTextEditor {
  inputClass() { return 'number-editor'; }
  fullWidth() { return false; }
  numericLayout() { return true; }
  allowedInputPattern() { return /[0-9+\-.eE]/; }

  parse(text) {
    const value = String(text ?? '').trim();
    if(value === '') return { ok: true, value: 0 };
    const parsed = Number(value);
    return Number.isFinite(parsed) ? { ok: true, value: parsed } : { ok: false };
  }

  wheelStep(text, caret) {
    return decimalFloatStep(text, caret);
  }
}

export class X13HexadecimalEditor extends X13ScalarTextEditor {
  allowedInputPattern() { return /[0-9a-fA-FxX+-]/; }

  format(value) {
    if(typeof value !== 'number' || !Number.isFinite(value)) return formatDefault(value);
    const sign = value < 0 ? '-' : '';
    return `${sign}0x${Math.trunc(Math.abs(value)).toString(16).toUpperCase()}`;
  }

  parse(text) {
    let value = String(text ?? '').trim();
    if(value === '') return { ok: true, value: 0 };
    let sign = 1;
    if(value.startsWith('-')) { sign = -1; value = value.slice(1); }
    else if(value.startsWith('+')) { value = value.slice(1); }
    if(value.startsWith('0x') || value.startsWith('0X')) value = value.slice(2);
    if(!/^[0-9a-fA-F]+$/.test(value)) return { ok: false };
    const parsed = Number.parseInt(value, 16) * sign;
    return Number.isSafeInteger(parsed) ? { ok: true, value: parsed } : { ok: false };
  }

  wheelStep(text, caret) {
    return hexadecimalStep(text, caret);
  }

  wheelValue(parsedValue, direction, step, text, caret) {
    const edited = editHexDigit(text, caret, direction);
    return edited ?? parsedValue + direction * step;
  }

  formatWheelValue(value, text, _caret) {
    if(typeof value !== 'number' || !Number.isFinite(value)) return this.format(value);
    const sign = value < 0 ? '-' : '';
    const source = String(text ?? '').trim().replace(/^[+-]/, '').replace(/^0x/i, '');
    const width = Math.max(1, source.replace(/[^0-9a-fA-F]/g, '').length);
    const digits = Math.trunc(Math.abs(value)).toString(16).toUpperCase().padStart(width, '0');
    return `${sign}0x${digits}`;
  }
}



export class X13TimeEditor extends X13ScalarTextEditor {
  inputClass() { return 'time-editor'; }
  fullWidth() { return false; }
  numericLayout() { return true; }
  allowedInputPattern() { return /[0-9:.]/; }

  format(value) {
    const date = dateFromState(value);
    if(!date) return '';
    return timeTextFromDate(date);
  }

  parse(text) {
    const parsed = parseTimeText(text);
    if(!parsed) return { ok: false };
    const base = editableBaseDate(this.state);
    const date = new Date(base.getFullYear(), base.getMonth(), base.getDate(), parsed.hours, parsed.minutes, parsed.seconds, parsed.milliseconds);
    return { ok: true, value: date.toISOString() };
  }
}

export class X13ByteArrayEditor extends X13ScalarTextEditor {
  allowedInputPattern() { return /[0-9a-fA-F,:\-\s]/; }

  format(value) {
    const bytes = byteArrayBytes(value);
    if(!bytes) return formatDefault(value);
    return bytes.map(byteToHex).join('-');
  }

  parse(text) {
    const value = String(text ?? '').trim();
    if(value === '') return { ok: true, value: byteArrayLiteral([]) };
    const parts = value.split(/[,:\-\s]+/).filter(part => part.length > 0);
    if(parts.length === 0) return { ok: true, value: byteArrayLiteral([]) };
    const bytes = [];
    for(const part of parts) {
      if(!/^[0-9a-fA-F]{1,2}$/.test(part)) return { ok: false };
      const parsed = Number.parseInt(part, 16);
      if(!Number.isInteger(parsed) || parsed < 0 || parsed > 255) return { ok: false };
      bytes.push(parsed);
    }
    return { ok: true, value: byteArrayLiteral(bytes) };
  }
}


function dateFromState(value) {
  if(value instanceof Date && !Number.isNaN(value.getTime())) return value;
  if(typeof value === 'string') {
    const date = new Date(value);
    if(!Number.isNaN(date.getTime())) return date;
  }
  return null;
}

function editableBaseDate(value) {
  const date = dateFromState(value);
  if(!date || date.getFullYear() === 1001) return new Date();
  return date;
}

function timeTextFromDate(date) {
  return `${pad2(date.getHours())}:${pad2(date.getMinutes())}:${pad2(date.getSeconds())}`;
}

function parseTimeText(text) {
  const value = String(text ?? '').trim();
  const match = /^(\d{1,2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?$/.exec(value);
  if(!match) return null;
  const hours = Number.parseInt(match[1], 10);
  const minutes = Number.parseInt(match[2], 10);
  const seconds = match[3] === undefined ? 0 : Number.parseInt(match[3], 10);
  const milliseconds = match[4] === undefined ? 0 : Number.parseInt(match[4].padEnd(3, '0'), 10);
  if(hours < 0 || hours > 23 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59 || milliseconds < 0 || milliseconds > 999) return null;
  return { hours, minutes, seconds, milliseconds };
}

function pad2(value) {
  return String(value).padStart(2, '0');
}

function byteArrayBytes(value) {
  if(typeof value === 'string' && value.startsWith('¤BA')) return bytesFromBase64(value.slice(3));
  if(Array.isArray(value) && value.every(isByte)) return value;
  if(value && Array.isArray(value.bytes) && value.bytes.every(isByte)) return value.bytes;
  if(value && Array.isArray(value.data) && value.data.every(isByte)) return value.data;
  return null;
}

function isByte(value) {
  return Number.isInteger(value) && value >= 0 && value <= 255;
}

function byteToHex(value) {
  return value.toString(16).toUpperCase().padStart(2, '0');
}

function byteArrayLiteral(bytes) {
  return `¤BA${base64FromBytes(bytes)}`;
}

function base64FromBytes(bytes) {
  let binary = '';
  for(const byte of bytes) binary += String.fromCharCode(byte);
  if(typeof btoa === 'function') return btoa(binary);
  if(typeof Buffer !== 'undefined') return Buffer.from(bytes).toString('base64');
  throw new Error('base64 encoder is not available');
}

function bytesFromBase64(base64) {
  try {
    let binary;
    if(typeof atob === 'function') binary = atob(base64);
    else if(typeof Buffer !== 'undefined') return Array.from(Buffer.from(base64, 'base64'));
    else return null;
    const bytes = [];
    for(let i = 0; i < binary.length; i++) bytes.push(binary.charCodeAt(i));
    return bytes;
  }
  catch { return null; }
}

function decimalIntegerStep(text, caret) {
  const index = digitLeftOfCaret(text, caret, /[0-9]/);
  if(index < 0) return null;
  const right = countMatchingToRight(text, index, /[0-9]/);
  return Math.pow(10, right);
}

function decimalFloatStep(text, caret) {
  const exponentIndex = Math.max(text.indexOf('e'), text.indexOf('E'));
  const mantissaEnd = exponentIndex >= 0 ? exponentIndex : text.length;
  const index = digitLeftOfCaret(text.slice(0, mantissaEnd), Math.min(caret, mantissaEnd), /[0-9]/);
  if(index < 0) return null;
  const dotIndex = text.indexOf('.');
  if(dotIndex >= 0 && index > dotIndex) {
    const decimalsBeforeDigit = countMatchingBetween(text, dotIndex + 1, index, /[0-9]/);
    return Math.pow(10, -(decimalsBeforeDigit + 1));
  }
  const integerEnd = dotIndex >= 0 ? dotIndex : mantissaEnd;
  const right = countMatchingBetween(text, index + 1, integerEnd, /[0-9]/);
  return Math.pow(10, right);
}


function editHexDigit(text, caret, direction) {
  const parsed = parseHexText(text);
  if(!parsed || parsed.sign < 0) return null;
  const absoluteIndex = Math.min(caret, text.length) - 1;
  const digitIndex = absoluteIndex - parsed.digitsStart;
  if(digitIndex < 0 || digitIndex >= parsed.digits.length) return null;
  const current = Number.parseInt(parsed.digits[digitIndex], 16);
  if(!Number.isFinite(current)) return null;
  const digits = parsed.digits.split('');
  let carry = direction;
  for(let i = digitIndex; i >= 0 && carry !== 0; i--) {
    const next = Number.parseInt(digits[i], 16) + carry;
    if(next < 0) {
      digits[i] = 'F';
      carry = -1;
    }
    else if(next > 15) {
      digits[i] = '0';
      carry = 1;
    }
    else {
      digits[i] = next.toString(16).toUpperCase();
      carry = 0;
    }
  }
  if(carry > 0) digits.unshift('1');
  else if(carry < 0) return 0;
  return Number.parseInt(digits.join(''), 16) * parsed.sign;
}

function parseHexText(text) {
  let value = String(text ?? '').trim();
  let sign = 1;
  if(value.startsWith('-')) { sign = -1; value = value.slice(1); }
  else if(value.startsWith('+')) value = value.slice(1);
  const prefixLength = value.startsWith('0x') || value.startsWith('0X') ? 2 : 0;
  const digits = prefixLength ? value.slice(2) : value;
  if(!/^[0-9a-fA-F]+$/.test(digits)) return null;
  const trimmed = String(text ?? '').trim();
  const signOffset = trimmed.startsWith('-') || trimmed.startsWith('+') ? 1 : 0;
  return { sign, digits: digits.toUpperCase(), digitsStart: signOffset + prefixLength };
}

function hexadecimalStep(text, caret) {
  const index = digitLeftOfCaret(text, caret, /[0-9a-fA-F]/);
  if(index < 0) return null;
  const right = countMatchingToRight(text, index, /[0-9a-fA-F]/);
  return Math.pow(16, right);
}

function digitLeftOfCaret(text, caret, pattern) {
  const index = Math.min(caret, text.length) - 1;
  if(index < 0) return -1;
  pattern.lastIndex = 0;
  return pattern.test(text[index]) ? index : -1;
}

function countMatchingToRight(text, index, pattern) {
  return countMatchingBetween(text, index + 1, text.length, pattern);
}

function countMatchingBetween(text, start, end, pattern) {
  let count = 0;
  for(let i = start; i < end; i++) {
    pattern.lastIndex = 0;
    if(pattern.test(text[i])) count++;
  }
  return count;
}


customElements.define('x13-string-editor', X13StringEditor);
customElements.define('x13-integer-editor', X13IntegerEditor);
customElements.define('x13-double-editor', X13DoubleEditor);
customElements.define('x13-hexadecimal-editor', X13HexadecimalEditor);
customElements.define('x13-time-editor', X13TimeEditor);
customElements.define('x13-byte-array-editor', X13ByteArrayEditor);
