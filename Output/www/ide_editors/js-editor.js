import { LitElement, html, css } from '../lib/lit-all.min.js';
import { formatDefault } from './editor-format.js';

export class X13JsEditor extends LitElement {
  static properties = {
    view: {},
    readonly: { type: Boolean, reflect: true },
    selected: { type: Boolean, reflect: true },
    state: { attribute: false },
  };

  static styles = css`
    :host {
      display: inline-flex;
      max-width: 100%;
      width: 100%;
      vertical-align: middle;
    }
    textarea {
      background: transparent;
      border: 0;
      border-radius: 0;
      box-sizing: border-box;
      color: #1f2933;
      display: block;
      font: inherit;
      height: 24px;
      line-height: 22px;
      margin: 0;
      max-width: 100%;
      min-height: 24px;
      min-width: 0;
      outline: 0;
      overflow: auto;
      padding: 0 4px;
      resize: vertical;
      width: 100%;
      white-space: pre;
    }
    :host(:not([readonly])) textarea {
      border-left: 1px solid #000;
      border-right: 1px solid #000;
    }
    :host([selected]) textarea { background: #fff; }
    textarea:disabled { color: #475569; resize: none; }
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
  }

  willUpdate(changed) {
    if(changed.has('state')) {
      this.#draft = null;
      this.#lastSentDraft = null;
      this.#suppressBlur = false;
    }
  }

  render() {
    return html`<textarea
      rows="1"
      spellcheck="false"
      .value=${this.#currentText()}
      ?disabled=${this.readonly}
      @input=${this.#onInput}
      @keydown=${this.#onKeyDown}
      @blur=${this.#onBlur}></textarea>`;
  }

  restoreConfirmed(textarea = null) {
    this.#draft = null;
    this.#lastSentDraft = null;
    this.#suppressBlur = true;
    if(textarea) textarea.value = this.#format(this.state);
    this.requestUpdate();
  }

  #currentText() {
    return this.#draft ?? this.#format(this.state);
  }

  #format(value) {
    if(value === undefined || value === null) return '';
    if(typeof value === 'string') return value;
    return formatDefault(value);
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
    if(e.key !== 'Enter' || !(e.ctrlKey || e.metaKey)) return;
    e.preventDefault();
    this.#tryCommit();
    this.#suppressBlur = true;
  }

  #onBlur() {
    if(this.#suppressBlur) {
      this.#suppressBlur = false;
      return;
    }
    this.#tryCommit();
  }

  #tryCommit() {
    if(this.readonly || this.#draft === null) return;
    const text = this.#currentText();
    if(text === this.#lastSentDraft) return;
    if(text === this.#format(this.state)) {
      this.#draft = null;
      this.#lastSentDraft = null;
      this.requestUpdate();
      return;
    }
    this.#lastSentDraft = text;
    Promise.resolve(this.commit(text)).catch(() => {
      if(this.#lastSentDraft === text) this.restoreConfirmed();
    });
  }

  #draft;
  #lastSentDraft;
  #suppressBlur;
}

customElements.define('x13-js-editor', X13JsEditor);
