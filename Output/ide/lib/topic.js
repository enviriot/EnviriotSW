import { WsIDE, TopicReq } from './connection.js';

class Topic{
  #subs;
  #path;
  #typeTopicChanged_m;
  
  constructor(parent, name) {
    this.#subs = new Set();
    this.#typeTopicChanged_m = this.#typeTopicChanged.bind(this);
    if (parent instanceof Topic) {
      this.conn = parent.conn;
      this.parent = parent;
      this.name = name;
      this.level = parent.level + 1;
      this.#path = parent == this.conn.root ? ("/" + name) : (this.parent.#path + "/" + name);
    } else {  // Root
      this.conn = new WsIDE(this);
      this.parent = null;
      this.name = "";
      this.#path = "/";
      this.level = 0;
    }
    this.status = 0;
  }
  get Path() { return this.#path;}
  pull(p) {
    // test
    let ts;
    if (!p) {
      ts = this;
    } else if (p[0] == '/') {
      ts = this.conn.root;
    } else {
      ts = this;
      p = this.parent ? (this.#path + "/" + p) : ("/" + p);
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
    if(this.typeTopic){
      this.typeTopic.unsubscribe(this.#typeTopicChanged_m);
    }
    if (this.manifest) {
      let tt = this.manifest["type"];
      if (typeof (tt) === "string") {
        this.status = 2;
        this.pull("/$YS/TYPES/" + tt).then(this.#typeLoaded.bind(this));
        send = false;
      }
    }
    if (send) {
      this.notify("type", this);
    }
  }
  #typeLoaded(t){
    this.typeTopic = t; 
    this.status = 3;
    this.typeTopic.subscribe(this.#typeTopicChanged_m);
    this.#typeTopicChanged("value", this.typeTopic);
  }
  #typeTopicChanged(e, t){
    if(e=="value"){
      this.manifest.__proto__ = t.state;
      //this.#protoDeep(this.manifest, t.state);
      this.notify("type", this);
    }
  }
/*  #protoDeep(m, p){
    if(m && typeof(m) === "object") {
      m.__proto__ = p;
      let pv_c;
      for(let key in m) {
        this.#protoDeep(m[key], (p && typeof(pv_c = p[key]) === "object")?pv_c:null);
      }
    }
  }*/
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
        console.error(this.#path + ".notify(" + event + ") - " + e);
      }
    });
  }
}

window.root = new Topic();