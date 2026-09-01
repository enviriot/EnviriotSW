import { LitElement, html, css } from '../../lib/lit-all.min.js';
import './breadcrumb-bar.js';
import './inspector-children.js';

// The bar's switch button for each thing a topic can also be shown as. The server names one per
// row - never both, Logram wins - so this is a lookup, not a list; see RowProjector.ResolveAltView.
// A Logram topic that is also archived therefore offers Logram here, and still reaches its chart
// through the Workspace context menu, which is built from the field rather than from this row.
const ALT_VIEW_BUTTONS = {
  logram: { label: 'Logram', mode: 'logram' },
  chart: { label: 'Chart', mode: 'chart' },
};

export class X13InspectorDocument extends LitElement {
  static properties = {
    path: {},
    rootName: {},
    api: { attribute: false },
    store: { attribute: false },
    altView: {},
  };

  static styles = css`
    :host { box-sizing: border-box; display: flex; flex-direction: column; height: 100%; width: 100%; }
    x13-inspector-children {
      flex: 1 1 auto;
      min-height: 0;
    }
  `;

  constructor() {
    super();
    this.path = '/';
    this.rootName = '';
    this.api = null;
    this.store = null;
    this.altView = null;
  }

  render() {
    return html`
      <x13-breadcrumb-bar .path=${this.path} .rootName=${this.rootName} .currentMode=${'inspector'}
        .altView=${ALT_VIEW_BUTTONS[this.altView] || null}></x13-breadcrumb-bar>
      <x13-inspector-children .api=${this.api} .store=${this.store} .rootPath=${this.path}></x13-inspector-children>`;
  }
}

customElements.define('x13-inspector-document', X13InspectorDocument);
