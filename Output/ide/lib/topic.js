import { WsIDE, TopicReq } from './connection.js';

class Topic {
  constructor(parent, name) {
    if (parent instanceof Topic) {
      this.conn = parent.conn;
      this.parent = parent;
      this.name = name;
      this.path = parent == this.conn.root ? ("/" + name) : (this.parent.path + "/" + name);
    } else {  // Root
      this.conn = new WsIDE(this);
      this.parent = null;
      this.name = "";
      this.path = "/";
    }
  }
  subscribe(p) {
    let ts;
    if (!p) {
      ts = this;
    } else if (p[0] == '/') {
      ts = this.conn.root;
    } else {
      ts = this;
      p = this.parent ? (this.path + "/" + p) : ("/" + p);
    }
    let req = new TopicReq(ts, p);
    this.conn.PostMsg(req);
    return req.Task;
  }
  GetChild(name, create) {
    if (!this.children) {
      if (create) {
        this.children = {};
      } else {
        return null;
      }
    }
    let t = this.children[name];
    if (t) {
      return t;
    } else if (create) {
      t = new Topic(this, name);
      this.children[name] = t;
      //ChangedReise(Art.addChild, t);
      return t;
    }
    return null;
  }
  ValuePublished(value) {
    this.state = value;
    //ChangedReise(Art.value, this);
  }
  ManifestPublished(manifest) {
    this.manifest = manifest;
    //bool send = true;
    //if (_manifest.ValueType == JSC.JSValueType.Object && _manifest.Value != null) {
    //  var tt = _manifest["type"];
    //  if (tt.ValueType == JSC.JSValueType.String && tt.Value != null) {
    //    _typeLoading = true;
    //    this.GetAsync("/$YS/TYPES/" + (tt.Value as string)).ContinueWith(TypeLoaded);
    //    send = false;
    //  }
    //}
    //if (send) {
    //  ChangedReise(Art.type, this);
    //}
  }
}

window.root = new Topic();