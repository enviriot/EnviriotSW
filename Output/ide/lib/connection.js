class WsIDE {
  #ws;
  #msgId;
  #msgs;
  #msgState;
  #requests;

  constructor(root) {
    this.#msgs = [];
    this.#msgId = 1;
    this.#requests = [];
    this.root = root;
    window.addEventListener("load", this.#onWindowLoad.bind(this));
    window.addEventListener("beforeunload", this.#onWindowBeforeUnload.bind(this));

  }
  PostMsg(msg) {
    this.#msgs.push(msg);
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
    this.#ws.send(JSON.stringify(arr));
    //this.Send(new ClRequest(mid, arr, req));
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
    console.log(event.data);
    let jo = JSON.parse(event.data);
    if (!Array.isArray(jo) || jo.length == 0 || typeof (jo[0]) !== "number") {
      return;
    }
    switch (jo[0]) {
      case 1: // [Hello, (<string> server name)]
        if (jo.length > 1 && typeof (jo[1]) === "string") {
          if (!this.alias) {
            this.alias = jo[1];
            console.info("Connected to " + this.alias);
          }
          this.#msgState = 1;
          this.root.subscribe("/$YS/TYPES/Ext/Manifest")
            .then(t => {
              //this.TypeManifest = t;
              return this.root.subscribe("/$YS/TYPES/Core");
            })
            .then(t => {
              //this.CoreTypes = t;
              for (var tc in t.children) {
                t.children[tc].subscribe(null);
              }
            });
        }
        break;
      case 3:  // [Response, msgId, success, [parameter | error]]
        let msgId = jo[1];
        let idx = this.#requests.findIndex(z => z.msgId == msgId);
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
        if (jo.length < 2 || typeof (jo[1]) != "string") {
          console.warn("Synax error " + event.data);
          break;
        }
        if (jo.length == 4) {
          this.#updateTree(jo[0], jo[1], jo[2], jo[3]);
        } else {
          this.#updateTree(jo[0], jo[1]);
        }
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
    if (state) {
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
  //private List<TopicReq> _reqs;

  constructor(cur, path, state, manifest) {
    this.#cur = cur;
    this.#path = path;
    this.#create = !!state || !!manifest;
    if (this.#create) {
      this.#state = state;
      this.#manifest = manifest;
    }
    this.#prom = new Promise((res, rej) => { this.#resolve = res; this.#reject = rej; })
  }

  get Task() {
    return this.#prom;
  }
  Process() {
    let idx1 = this.#cur.path.length;
    if (idx1 > 1) {
      idx1++;
    }
    if (this.#path == null || this.#path.length <= this.#cur.path.length) {
      //    if (_cur._disposed) {
      //      _tcs.SetResult(null);
      //      lock(_cur) {
      //        _cur._req = null;
      //        if (this._reqs != null) {
      //          foreach(var r in _reqs) {
      //            App.PostMsg(r);
      //          }
      //        }
      //      }
      //    } else 
      if (this.#cur.children || this.#cur.state || this.#cur.manifest) {
        //      if (_cur._typeLoading) {
        //        _cur.changed += TypeLoaded;
        //      } else {
        this.#resolve(this.#cur);
        //        _tcs.SetResult(_cur);
        //      }
        //      lock(_cur) {
        //        _cur._req = null;
        //        if (this._reqs != null) {
        //          foreach(var r in _reqs) {
        //            App.PostMsg(r);
        //          }
        //        }
        //      }
      } else {
        //      lock(_cur) {
        //        if (_cur._req != null && _cur._req != this) { //-V3054
        //          if (_cur._req._reqs == null) {
        //            _cur._req._reqs = new List < TopicReq > ();
        //          }
        //          _cur._req._reqs.Add(this);
        //          return;
        //        } else {
        //          _cur._req = this;
        //        }
        //      }
        this.#cur.conn.SendReq(4, this, this.#cur.path, 3);
      }
      return;
    }

    if (!this.#cur.children && !this.#cur.state && !this.#cur.manifest) {
      //lock(_cur) {
      //  if (_cur._req != null && _cur._req != this) { //-V3054
      //    if (_cur._req._reqs == null) {
      //      _cur._req._reqs = new List < TopicReq > ();
      //    }
      //    _cur._req._reqs.Add(this);
      //    return;
      //  } else {
      //    _cur._req = this;
      //  }
      //}
      this.#cur.conn.SendReq(4, this, this.#cur.path, 3);
      return;
    }
    let next = null;
    let idx2 = this.#path.indexOf('/', idx1);
    if (idx2 < 0) {
      idx2 = this.#path.length;
    }
    let name = this.#path.substring(idx1, idx2);

    next = this.#cur.GetChild(name, false);
    if (!next) {
      if (this.#create) {
        this.#create = false;
        if (this.#path.Length <= idx2 && this.#state != null) {
          this.#cur.conn.SendReq(8, this, this.#path.Substring(0, idx2), this.#state, this.#manifest);
        } else {
          this.#cur.conn.SendReq(8, this, this.#path.Substring(0, idx2));
        }
      } else {
        this.#resolve(null);
        //lock(_cur) {
        //  _cur._req = null;
        //  if (this._reqs != null) {
        //    foreach(var r in _reqs) {
        //      App.PostMsg(r);
        //    }
        //  }
        //}
      }
      return;
    }
    this.#cur = next;
    this.#cur.conn.PostMsg(this);
  }
  Response(success, value) {
    if (success) {   // value == null after connect
      if (value === false) {
        //_cur._disposed = true;
        //var parent = _cur.parent;
        //if (parent != null) {
        //  parent.RemoveChild(_cur);
        //  _cur.ChangedReise(Art.RemoveChild, _cur);
        //  parent.ChangedReise(Art.RemoveChild, _cur);
        //}
      }
    } else {
      this.#reject(value ?? "TopicReqError");
    }
  }
  //private void TypeLoaded(Art a, DTopic t) {
  //  if (a == Art.type && t == _cur && !_cur._typeLoading) {
  //    _cur.changed -= TypeLoaded;
  //    _tcs.SetResult(_cur);
  //  }
  //}
}
export { WsIDE, TopicReq };
