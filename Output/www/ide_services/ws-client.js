export class WsClient extends EventTarget {
  constructor(url = defaultWsUrl()) {
    super();
    this.url = url;
    this.socket = null;
    this.nextId = 1;
    this.pending = new Map();
    this.reconnectTimer = null;
    this.nextDelay = INITIAL_RECONNECT_DELAY_MS;
    this.connectStarted = false;
  }

  connect() {
    if(this.connectStarted) return;
    this.connectStarted = true;
    this.#status('connecting');
    this.#openSocket();
  }

  request(type, payload = {}) {
    const id = this.nextId++;
    const message = { id, type, ...payload };
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try {
        this.send(message);
      } catch(error) {
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  send(message) {
    if(!this.socket || this.socket.readyState !== WebSocket.OPEN) throw new Error('WebSocket is not open');
    this.socket.send(JSON.stringify(message));
  }

  #openSocket() {
    const socket = new WebSocket(this.url);
    this.socket = socket;
    socket.addEventListener('open', () => {
      if(socket !== this.socket) return;
      this.nextDelay = INITIAL_RECONNECT_DELAY_MS;
      this.#status('connected');
    });
    socket.addEventListener('close', () => {
      if(socket !== this.socket) return;
      this.#rejectPending(socketClosedError());
      this.#status('reconnecting');
      this.#scheduleReconnect();
    });
    socket.addEventListener('error', () => {
      if(socket !== this.socket) return;
      this.#status('error');
      this.#scheduleReconnect();
    });
    socket.addEventListener('message', (e) => {
      if(socket !== this.socket) return;
      this.#onMessage(e);
    });
  }

  #scheduleReconnect() {
    if(this.reconnectTimer !== null) return;
    const delay = this.nextDelay;
    this.nextDelay = Math.min(this.nextDelay * 2, MAX_RECONNECT_DELAY_MS);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.socket = null;
      this.#status('reconnecting');
      this.#openSocket();
    }, delay);
  }

  #rejectPending(error) {
    for(const p of this.pending.values()) p.reject(error);
    this.pending.clear();
  }

  #status(status) {
    this.dispatchEvent(new CustomEvent('status', { detail: status }));
  }

  #onMessage(e) {
    let message;
    try {
      message = JSON.parse(e.data);
    } catch(error) {
      console.warn('Bad WebSocket JSON', error);
      return;
    }
    if(message.id && this.pending.has(message.id)) {
      const p = this.pending.get(message.id);
      this.pending.delete(message.id);
      if(message.ok === false) p.reject(message);
      else p.resolve(message);
      return;
    }
    this.dispatchEvent(new CustomEvent('message', { detail: message }));
  }
}

const INITIAL_RECONNECT_DELAY_MS = 500;
const MAX_RECONNECT_DELAY_MS = 10000;

function socketClosedError() {
  return {
    code: 'socket_closed',
    message: 'WebSocket connection closed',
  };
}

function defaultWsUrl() {
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${protocol}//${location.host}/api/ide`;
}
