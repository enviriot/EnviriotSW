import { BaseComponent } from '../lib/symbiote.js';
import { binarySearch } from '../lib/connection.js';

function comparePaths(a, b) {
  let pa = !a?null:a.Path;
  let pb = !b?null:b.Path;
  if (!pa) return !pb ? 0 : 1;
  if (!pb) return -1;
  let sa = pa.split('/').filter((z) => !!z);
  let sb = pb.split('/').filter((z) => !!z);
  let ia = 0, ib = 0;
  while (true) {
    if (ia >= sa.length) return ib >= sb.length?0:-1;
    if (ib >= sb.length) return 1;
    let r = sa[ia].localeCompare(sb[ib]);
    if (r != 0) return r;
    ia++;
    ib++;
  } 
  return NaN;
}

function JSV2Type(value) {
  if(!value) {
    return typeof(undefined);
  }
  switch(typeof(value)) {
    case "string":
      if(value && value.vength > 3 && value[0] == '¤') {
        switch(v.Substring(1, 2)) {
          case "TR": return "TopicReference";
          case "VR": return "Version";
        }
      }        
      break;
    case "object":
      //if(value is ByteArray || value.Value is ByteArray) {
      //  return "ByteArray";
      //}
      break;
  }
  return typeof(value);
}

var icons = {
  "Attribute":"attr.png",
  "Boolean": "ty_bool.png",
  "boolean": "ty_bool.png",
  "ByteArray": "ty_byteArray.png",
  "children": "children.png",
  "Date": "ty_dt.png",
  "Double": "ty_double.png",
  "Editor": "ic_editor.png",
  "EsConnection": "ty_es.png",
  "Hexadecimal": "ed_hex.png",
  "Integer": "ty_int.png",
  "JS": "ty_js.png",
  "Null": "ty_null.png",
  "number": "ty_double.png",
  "Object": "ty_obj.png",
  "object": "ty_obj.png",
  "String": "ty_str.png",
  "string": "ty_str.png",
  "Time": "ed_time.png",
  "Version": "ty_version.png",
  "log_deb": "log_deb.png",
  "log_ok": "log_ok.png",
  "log_err": "log_err.png"  
}
function GetIcon(n){
  if(!n || typeof(n) !== "string") return "component/Images/ty_topic.png";
  if(n.startsWith("data:image/png;base64,")) return n;
  if(icons.hasOwnProperty(n)) return "component/Images/" + icons[n];
  return n;
}

class X13TreeItem extends BaseComponent{
  initCallback() {
    this.style.display = "table-row";
    this.ref.indent.onclick = () => this.$.cb("indent", this.$);
  }
}
X13TreeItem.template = /*html*/ `
  <td style="display:flex;">
    <span ref="indent" style="display:block;width:1em;cursor:pointer" set="style.marginLeft:ml;">{{indent}}</span>
    <img width="16" set="@src:icon;">
    <span>{{name}}</span></td>
  <td>{{value}}</td>
`;
X13TreeItem.reg("x13-tree-item");

class X13_tree extends BaseComponent {
  #allItems = [];
  #cb = this.callback.bind(this);
  init$ = {
    items: [],
  }
  initCallback() {  
    this.pullRoot();
  }
  async pullRoot(){
    let root=await window.root.pull(null);
    this.addItem(root, true);
    this.showChildren(root);
  }
  addItem(t, expanded) {
    let addChildren = false;
    let idx = binarySearch(this.#allItems, t, comparePaths);
    if(idx<0){
      idx=~idx;
      let ni = { 
        name: t.name, 
        Path: t.Path, 
        icon:null, 
        value:t.state, 
        ml:(t.level).toString() + "em", 
        indent:t.children?(expanded?"▼":"►"):"●",
        src:t,
        cb:this.#cb,
      };
      this.#allItems.splice(idx, 0, ni);
      t.subscribe(this.#cb);
      this.#typeUpdated(t);
    } else {
      addChildren = t.children && this.#allItems[idx].indent=="▼";
    }
    let idx2=binarySearch(this.$.items, t, comparePaths);
    if(idx2<0){
      this.$.items.splice(~idx2, 0, this.#allItems[idx]);
      this.notify("items");
    }
    if(addChildren) {
      this.showChildren(t);
    }
  }
  callback(event, t){
      console.log("cb(" + event + ", " + t.Path +")");
    switch(event){
      case "indent":
        if(t.src.children) {
          if(t.indent == "▼"){
            this.hideChildren(t.src);          
          } else {
            this.showChildren(t.src);
          }
        } else {
          t.indent = "●";
        }
        break;
      case "type":
        this(this.#typeUpdated(t));
        break;
    }
  }
  async showChildren(t){
    let idx = binarySearch(this.$.items, t, comparePaths);
    if(idx < 0) return;
    this.$.items[idx].indent = "▼";    
    for(let tn in t.children){
      let tc = await t.pull(tn);
      this.addItem(tc, false);
    }
  }
  hideChildren(t){
    let idx = binarySearch(this.$.items, t, comparePaths);
    if(idx < 0) return;
    this.$.items[idx].indent = "►";
    let p = t.Path=="/"?"/":(t.Path + "/");
    idx++;
    while(idx < this.$.items.length && this.$.items[idx].Path.startsWith(p)) {
      this.$.items.splice(idx,1);
    }
    this.notify("items");
  }
  #typeUpdated(t){
    let nv=null, ni=null;
    let m = t.manifest;
    if(m && typeof(m) === "object"){
      let vv = m["editor"];
      if(vv && typeof(vv) === "string") {
        nv = vv;
      }
      let iv = m["icon"];
      if(iv && typeof(iv) === "string") {
        ni = GetIcon(iv);
      }
    }
    if(!nv){
      nv = JSV2Type(t.state);
    }
    if(!ni) {
      if(!t.state) {
        //ni = GetIcon((this is InTopic) ? string.Empty : "Null");  // Folder or Null
        ni = GetIcon(null);
      } else {
        ni = GetIcon(nv);
      }
    }
    let idx = binarySearch(this.#allItems, t, comparePaths);
    if(idx>=0){
      this.#allItems[idx].icon = ni;
    }
  }
}

X13_tree.template = /*html*/ `<table style="table-layout:fixed;width:100%;">
<thead><tr>
<th style="width:calc(70% + 2em);">Name</th>
<th>Value</th></tr></thead>
<tbody repeat="items" repeat-item-tag="x13-tree-item" style="align-content:left">
</tbody>
</table>`;
//X13_tree.bindAttributes({ value: "value", label:"label"});
X13_tree.reg("x13-tree");
