import { LitElement, html, css } from '../../lib/lit-all.min.js';
import './context-menu.js';

// The topic a document is rooted on, drawn as clickable path segments, plus the two things every
// document rooted on a topic needs on that same line: a slot for its own controls (Chart's legend
// and period picker), and a button offering the OTHER view of the same topic.
//
// Inspector and Chart used to draw this bar from two copies of the same markup and the same CSS,
// and the copy in chart-document.js had no context menu, so Chart's segments offered a plain click
// and nothing else. The switch button is here for the same reason: with Inspector, Logram and
// Chart as three separate documents, a button living in each of them would be that same
// duplication again, one level down. The bar does not know what the other view IS - the document
// hands it a label and a mode - so nothing about Logram leaks in here.
export class X13BreadcrumbBar extends LitElement {
  static properties = {
    path: {},
    rootName: {},
    currentMode: {},
    altView: { attribute: false },
    menuState: { attribute: false },
  };

  static styles = css`
    :host { display: block; flex: 0 0 auto; }
    /* Wraps: a deep path in a narrow pane has to grow the bar downwards, never push it sideways
       past the pane. Slotted content is expected not to take part in that - Chart's legend shrinks
       to nothing instead (min-width:0 on its own rule), so the line it sits on cannot be broken by
       whatever value happens to be under the cursor, which would make the bar - and with it the
       chart - change height on every mouse move. */
    .bar {
      align-items: center;
      background: #f3f6fa;
      border-bottom: 1px solid #cfd8e3;
      display: flex;
      flex-wrap: wrap;
      font-size: 13px;
      gap: 2px;
      padding: 6px 10px;
    }
    /* The slot's own box would sit between .bar and the slotted elements and keep them out of the
       flex layout - display:contents removes it, so what a document slots in lays out exactly as
       it did when each document wrote these elements into its own bar. */
    slot { display: contents; }
    .segment {
      background: transparent;
      border: 1px solid transparent;
      border-radius: 0px;
      color: #1f2937;
      cursor: default;
      font: inherit;
      padding: 3px 6px;
    }
    .segment:hover {
      background: #e8f1ff;
      border-color: #8ab4f8;
    }
    .segment.current {
      font-weight: 600;
    }
    .sep {
      color: #9aa7b6;
      padding: 0 1px;
    }
    /* margin-left:auto is what pushes it to the far end of the line, past however many segments
       the path has. Harmless when a document also slots in something that grows (Chart's legend):
       the auto margin then absorbs an empty remainder. */
    .view-toggle {
      background: #fff;
      border: 1px solid #aebccd;
      border-radius: 3px;
      color: #243447;
      cursor: pointer;
      font: inherit;
      font-size: 12px;
      margin-left: auto;
      padding: 3px 8px;
    }
  `;

  constructor() {
    super();
    this.path = '/';
    this.rootName = '';
    this.currentMode = undefined;
    this.altView = null;
    this.menuState = null;
  }

  render() {
    const segments = this.#segments();
    return html`
      <div class="bar">
        ${segments.map((segment, index) => html`
          ${index > 0 ? html`<span class="sep">/</span>` : html``}
          <button
            type="button"
            class="segment ${index === segments.length - 1 ? 'current' : ''}"
            @click=${() => this.#emit('open', segment.path)}
            @contextmenu=${(e) => this.#openMenu(e, segment)}>
            ${segment.name || '/'}
          </button>
        `)}
        ${this.menuState ? html`
          <x13-context-menu
            .items=${this.menuState.items}
            .x=${this.menuState.x}
            .y=${this.menuState.y}
            @menu-command=${this.#menuCommand}
            @menu-close=${this.#closeMenu}>
          </x13-context-menu>` : html``}
        <slot></slot>
        ${this.altView ? html`
          <button type="button" class="view-toggle" @click=${this.#switchView}>${this.altView.label}</button>` : html``}
      </div>`;
  }

  #segments() {
    const parts = String(this.path || '/').split('/').filter(Boolean);
    const segments = [{ name: this.rootName, path: '/' }];
    let current = '';
    for(const part of parts) {
      current += `/${part}`;
      segments.push({ name: part, path: current });
    }
    return segments;
  }

  #openMenu(e, segment) {
    e.preventDefault();
    this.menuState = {
      segment,
      items: [
        { cmd: 'open', text: 'Open' },
        { cmd: 'open-tab', text: 'Open in new tab' },
        { cmd: 'copy-path', text: 'Copy path' },
      ],
      x: e.clientX,
      y: e.clientY,
    };
  }

  #menuCommand(e) {
    const cmd = e.detail?.cmd;
    const segment = this.menuState?.segment;
    this.#closeMenu();
    if(!cmd || !segment) return;
    this.#emit(cmd, segment.path);
  }

  #closeMenu() {
    this.menuState = null;
  }

  // A different cmd from the segments' 'open' on purpose: this names a view rather than a topic, so
  // app-shell.js opens it outright instead of asking the server what the path resolves to - and
  // knows the switch was deliberate, which is what decides whether it reaches the URL.
  #switchView() {
    this.dispatchEvent(new CustomEvent('segment-command', {
      bubbles: true,
      composed: true,
      detail: { cmd: 'switch-view', path: this.path, mode: this.altView?.mode },
    }));
  }

  // The bar only ever knows path strings, not which document each segment would open - but for the
  // CURRENT (last) segment that is simply the document already showing, which hands it over as
  // currentMode. That is what makes re-clicking the last segment stay where you are rather than
  // re-resolving to whatever the server considers this topic's default view. Ancestor segments
  // pass undefined (genuinely unknown, not "known to be Inspector"), and so does the last segment
  // of a document that has no opinion, so app-shell.js routes them through its single-round-trip
  // resolve instead of guessing.
  #emit(cmd, path) {
    const mode = path === this.path ? this.currentMode : undefined;
    this.dispatchEvent(new CustomEvent('segment-command', {
      bubbles: true,
      composed: true,
      detail: { cmd, path, mode },
    }));
  }
}

customElements.define('x13-breadcrumb-bar', X13BreadcrumbBar);
