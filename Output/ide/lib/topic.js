import { WsIDE, TopicReq } from './connection.js';

class Topic{
  #subs;
  constructor(parent, name) {
    this.#subs = new Set();
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
    this.status = 0;
  }
  pull(p) {
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
      this.notify("addChild", t);
      return t;
    }
    return null;
  }
  ValuePublished(value) {
    this.state = value;
    this.notify("value", this);
  }
  ManifestPublished(manifest) {
    this.manifest = manifest;
    let send = true;
    if (this.manifest) {
      let tt = this.manifest["type"];
      if (typeof (tt) === "string") {
        this.status = 2;
        this.pull("/$YS/TYPES/" + tt); //.then(TypeLoaded);
        send = false;
      }
    }
    if (send) {
      this.notify("type", this);
    }
  }
  subscribe(cb) {
    this.#subs.add(cb);
  }
  unsubscribe(cb) {
    this.#subs.delete(cb);
  }
  notify(event, data) {
    this.#subs.forEach(z => {
      try {
        z.call(null, event, data);
      }
      catch (e) {
        console.error(this.path + ".notify(" + event + ") - " + e);
      }
    });
  }
}

window.root = new Topic();