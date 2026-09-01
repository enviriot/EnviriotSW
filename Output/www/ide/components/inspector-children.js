import { X13TreeDocumentBase, treeHeaderStyles } from './tree-view-shared.js';

const NAME_WIDTH_KEY = 'x13.inspector.nameWidth';
const DEFAULT_NAME_WIDTH = 180;
const MIN_NAME_WIDTH = 110;

// The Inspector document's "Children" tree - a live topic subtree rooted at the
// document's own topic instead of the broker root. Unlike X13ViewWorkspace it
// intentionally has no _afterToggle/_onRowsChanged/_extraMenuCommand overrides:
// expand state lives only in this.rows/this.store for as long as the component is
// subscribed, nothing is written to localStorage, so it resets whenever the
// Inspector document navigates to a different topic or the page reloads.
export class X13InspectorChildren extends X13TreeDocumentBase {
  static properties = {
    ...X13TreeDocumentBase.properties,
    rootPath: {},
  };

  static styles = treeHeaderStyles;

  constructor() {
    super({ nameWidthKey: NAME_WIDTH_KEY, defaultNameWidth: DEFAULT_NAME_WIDTH, minNameWidth: MIN_NAME_WIDTH });
    this.rootPath = '/';
  }

  render() {
    return this._renderTree();
  }
}

customElements.define('x13-inspector-children', X13InspectorChildren);
