function compareMessages(a, b) {
  let pa = !a?null:a.Path;
  let pb = !b?null:b.Path;
  if (!pa) return !pb ? 0 : 1;
  if (!pb) return -1;
  let sa = pa.split('/').filter((z) => !!z);
  let sb = pb.split('/').filter((z) => !!z);
  let ia = 0, ib = 0;
  while (ia < sa.length || ib < sb.length) {
    if (ia >= sa.length) return -1;
    if (ib >= sb.length) return 1;
    let r = sa[ia].localeCompare(sb[ib]);
    if (r != 0) return r;
    ia++;
    ib++;
  } 
  return 0;
}
function binarySearch(arr, el, compare_fn) {
  let m = 0;
  let n = arr.length - 1;
  while (m <= n) {
    let k = (n + m) >> 1;
    let cmp = compare_fn(el, arr[k]);
    if (cmp > 0) {
      m = k + 1;
    } else if (cmp < 0) {
      n = k - 1;
    } else {
      return k;
    }
  }
  return ~m;
}
class WsIDE {
  #ws;  // WebSocket
  #msgId;  // Message ID generator
  #msgs;   // Messages queue
  #msgState;  // 1 - connected, 2 - busy
  #requests;  // requests storage

  constructor(root) {
    this.#msgs = [];
    this.#msgId = 1;
    this.#requests = [];
    this.root = root;
    window.addEventListener("load", this.#onWindowLoad.bind(this));
    window.addEventListener("beforeunload", this.#onWindowBeforeUnload.bind(this));

  }
  PostMsg(msg) {
    let idx = binarySearch(this.#msgs, msg, compareMessages);
    this.#msgs.splice(idx<0?~idx:(idx+1), 0, msg);
    this.#ProcessMQ();
    
  }
  #ProcessMQ() {
    if (this.#msgState != 1) return;
    this.#msgState = 2;
    while (this.#msgs.length > 0) {
      let msg = this.#msgs.shift();
      msg.Process();
    }
    this.#msgState = 1;
  }
  SendReq(cmd, req, ...args) {
    let mid = this.#msgId++;
    let arr = [args.length + 2];
    arr[0] = cmd;
    arr[1] = mid;
    for (let i = 0; i < args.length; i++) {
      arr[i + 2] = args[i];
    }
    req.msgId = mid;
    this.#requests.push(req);
    let rj = JSON.stringify(arr);
    this.#ws.send(rj);
    console.log("S " + rj);
  }
  //SendCmd(int cmd, params JSC.JSValue[] arg)

  #onWindowLoad(event) {
    this.#createWS();
  }
  #onWindowBeforeUnload(event) {
    if (this.#ws != null && this.#ws.readyState == this.#ws.OPEN) {
      this.#ws.close(1000);
    }
    this.#ws = null;
  }
  #createWS() {
    if (this.#ws == null || this.#ws.readyState == this.#ws.CLOSED) {
      if (this.#ws != null) {
        this.#ws.close();
      }
      this.#ws = new WebSocket((window.location.protocol == "https:" ? "wss://" : "ws://") + window.location.host + "/ide/v01");
      this.#ws.onmessage = this.#onMessage.bind(this);
      this.#ws.onerror = this.#onWsFail.bind(this);
      this.#ws.onclose = this.#onWsFail.bind(this);
      setTimeout(this.#createWS.bind(this), 15000);
    }
  }
  #onMessage(event) {
    console.log("R " + event.data);
    let jo = JSON.parse(event.data);
    if (!Array.isArray(jo) || jo.length == 0 || typeof (jo[0]) !== "number") {
      return;
    }
    let msgId, idx;
    switch (jo[0]) {
      case 1: // [Hello, (<string> server name)]
        if (jo.length > 1 && typeof (jo[1]) === "string") {
          if (!this.alias) {
            this.alias = jo[1];
            console.info("Connected to " + this.alias);
          }
          this.#msgState = 1;
          this.root.pull("/$YS/TYPES/Ext/Manifest")
            .then(t => {
              this.typeManifest = t;
              return this.root.pull("/$YS/TYPES/Core");
            })
            .then(t => {
              this.coreTypes = t;
              for (var tc in t.children) {
                t.children[tc].pull(null);
              }
            }); 
        }
        break;
      case 3:  // [Response, msgId, success, [parameter | error]]
        msgId = jo[1];
        idx = this.#requests.findIndex(z => z.msgId == msgId);
        if (idx >= 0) {
          let req = this.#requests[idx];
          req.msgId = null;
          this.#requests.splice(idx, 1);
          req.Response(jo[2], jo[3]);
          this.PostMsg(req);
        }
        break;
      case 4:  // [SubscribeResp, path, state, manifest]
      case 8:  // [CreateResp, path, state, manifest]
        if (jo.length >= 2 && typeof (jo[1]) === "string") {
          if (jo.length == 4) {
            this.#updateTree(jo[0], jo[1], jo[2], jo[3]);
          } else {
            this.#updateTree(jo[0], jo[1]);
          }
        } else {
          console.warn("Synax error " + event.data);
        }
        break;
      case 5:  // [SubAck, msgId, state, manifest, [children]]
        msgId = jo[1];
        idx = this.#requests.findIndex(z => z.msgId == msgId);
        if (idx >= 0) {
          let req = this.#requests[idx];
          req.msgId = null;
          this.#requests.splice(idx, 1);
          this.#updateTree(jo[0], req.Path, jo[2], jo[3]);
          for (let ch in jo[4]) {
            this.#updateTree(jo[0], (req.Path == "/" ? "" : req.Path) + "/" + jo[4][ch]);
          }
          req.Response(true, true);
          this.PostMsg(req);
        }
        break;
      case 6:  // [Publish, path, state]
        if ((jo.length == 2 || jo.length == 3) && typeof (jo[1]) == "string") {
          this.#updateTree(jo[0], jo[1], jo[2]);
        } else {
          console.warn("Synax error " + event.data);
        }
        break;
      //case 10:  // [Move, oldPath, newParent, newName]
      //  if (msg.Count != 4 || msg[1].ValueType != JSC.JSValueType.String || msg[2].ValueType != JSC.JSValueType.String || msg[3].ValueType != JSC.JSValueType.String) {
      //    Log.Warning("Synax error {0}", msg);
      //    break;
      //  }
      //  App.PostMsg(new DTopic.ClientEvent(this.root, msg[1].Value as string, cmd, msg[2], msg[3]));
      //  break;
      case 12:  // [Remove, path]
        if (jo.length == 2 && typeof (jo[1]) == "string") {
          this.#updateTree(jo[0], jo[1]);
        } else {
          console.warn("Synax error " + event.data);
        }
        break;
      case 14:  // [ManifestChanged, path, manifest]
        if ((jo.length == 2 || jo.length == 3) && typeof (jo[1]) == "string") {
          this.#updateTree(jo[0], jo[1], null, jo[2]);
        } else {
          console.warn("Synax error " + event.data);
        }
        break;
      //case 90:  // [Log, DateTime, level, message]
      //  if (msg.Count != 4 || msg[1].ValueType != JSC.JSValueType.Date || !msg[2].IsNumber || msg[3].ValueType != JSC.JSValueType.String) {
      //    Log.Warning("Synax error {0}", msg);
      //    break;
      //  }
      //  Log.AddEntry((LogLevel)(int)msg[2], (msg[1].Value as JSL.Date).ToDateTime(), msg[3].Value as string);
      //  break;
      case 99:  // EsSocket Closed
        break;
    }
  }
  #onWsFail(event) {
    this.#msgState = 0;
    setTimeout(this.#createWS.bind(this), 1500);
  }
  #updateTree(cmd, path, state, manifest) {
    let ps = path.split("/");
    let cur = this.root;
    let next;
    for (let i = 0; i < ps.length; i++) {
      if (!ps[i]) continue;
      next = cur.GetChild(ps[i], true);
      if (!next) {  // Topic not exist
        return;
      }
      cur = next;
    }
    if (typeof(state) != "undefined") {
      cur.ValuePublished(state);
    }
    if (manifest) {
      cur.ManifestPublished(manifest);
    }
  }
}

class TopicReq {
  #cur;
  #path;
  #create;
  #state;
  #manifest;
  #prom;
  #resolve
  #reject;
  #typeLoaded_cb;

  constructor(cur, path, state, manifest) {
    this.#cur = cur;
    this.#path = path;
    this.#create = !!state || !!manifest;
    if (this.#create) {
      this.#state = state;
      this.#manifest = manifest;
    }
    this.#prom = new Promise((res, rej) => { this.#resolve = res; this.#reject = rej; })
    this.#typeLoaded_cb = this.#typeLoaded.bind(this);
  }

  get Task() {
    return this.#prom;
  }
  get Path() { return this.#cur.Path; }
  Process() {
    if (this.#cur.status == 0) {
      if(this.#cur.pull_req){
        if(!this.#cur.pull_req.wreqs){
          this.#cur.pull_req.wreqs = [];
        }
        this.#cur.pull_req.wreqs.push(this);
      } else{
        this.#cur.pull_req = this;
        this.#cur.conn.SendReq(5, this, this.#cur.Path);
      }
      return;
    }
    if (this.#path == null || this.#path.length <= this.#cur.Path.length) {
      if (this.#cur.status < 0) {
        this.#resolve(null);
        this.#reqFinished();
      } else {
        if (this.#cur.status == 2) {
          this.#cur.subscribe(this.#typeLoaded_cb);
        } else {
          this.#resolve(this.#cur);
          this.#reqFinished();
        }
      }
      return;
    }
    this.#reqFinished();
    let idx1 = this.#cur.Path == '/' ? 1 : (this.#cur.Path.length + 1);
    let idx2 = this.#path.indexOf('/', idx1);
    if (idx2 < 0) {
      idx2 = this.#path.length;
    }
    let name = this.#path.substring(idx1, idx2);

    let next = this.#cur.GetChild(name, false);
    if (!next) {
      if (this.#create) {
        //this.#create = false;  // ???????????
        if (this.#path.Length <= idx2 && this.#state != null) {
          this.#cur.conn.SendReq(8, this, this.#path.Substring(0, idx2), this.#state, this.#manifest);
        } else {
          this.#cur.conn.SendReq(8, this, this.#path.Substring(0, idx2));
        }
      } else {
        this.#resolve(null);
      }
      return;
    }
    this.#cur = next;
    this.#cur.conn.PostMsg(this);
  }
  Response(success, value) {
    if (success) {   // value == null after connect
      if (value === false) {
        this.#cur.status = -1; // deleted
        let parent = this.#cur.parent;
        if (parent) {
          parent.RemoveChild(this.#cur);
          this.#cur.notify("RemoveChild", this.#cur);
          parent.notify("RemoveChild", this.#cur);
        }
      } else if (value === true) {
        if(this.#cur.status == 0){
          this.#cur.status = 1; // loaded
        }
      }
    } else {
      this.#reject(value ?? "TopicReqError");
    }
  }
  #reqFinished(){
    if(this.#cur.pull_req==this){
      this.#cur.pull_req = null;
      if(this.wreqs){
        for(let r in this.wreqs){
          this.#cur.conn.PostMsg(this.wreqs[r]);
        }
        this.wreqs = null;
      }
    }
  }
  #typeLoaded(a, t) {
    if (a == "type" && t == this.#cur && this.#cur.status!=2) {
      this.#cur.unsubscribe(this.#typeLoaded_cb);
      this.#resolve(this.#cur);
    }
  }
}
export { WsIDE, TopicReq, binarySearch};
