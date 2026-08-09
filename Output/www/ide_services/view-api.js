export class ViewApi {
  constructor(client) {
    this.client = client;
  }

  hello() {
    return this.client.request('req.hello');
  }

  expand(vid, expand) {
    return this.client.request('req.expand', { vid, expand: !!expand });
  }

  commit(vid, value) {
    return this.client.request('req.commit', { vid, value });
  }

  menu(vid) {
    return this.client.request('req.menu', { vid });
  }

  rpc(vid, cmd, args) {
    const payload = { vid, cmd };
    if(args !== undefined) payload.args = args;
    return this.client.request('req.rpc', payload);
  }

  open(vid, view) {
    return this.client.request('req.open', { vid, view });
  }
}
