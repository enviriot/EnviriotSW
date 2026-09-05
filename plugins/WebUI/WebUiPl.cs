///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.ComponentModel.Composition;
using X13.Repository;
using X13.WebUI.Helpers;
using NiL.JS.Extensions;

namespace X13.WebUI {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 10)]
  [ExportMetadata("name", "WebUI")]
  internal sealed class WebUiPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/WebUI";
    private Topic _owner;
    private WebUiConfig _config;
    private WebUiHost _host;

    public void Init() {
    }

    public void Start() {
      _config = new WebUiConfig(Owner);
      _config.Start();
      // Before the host too, and for a stronger reason than the ACL below: with no port open
      // there is no live session yet, so every topic under /$YS/WebUI/clients is a leftover of
      // a run that did not get to remove its own (see ClientSession.PurgeStale).
      ClientSession.PurgeStale();
      // Before the host: the dashboard endpoint refuses every topic it has no rule for, so the
      // rules have to be in place by the time the first socket can arrive.
      DashboardAcl.Start();
      _host = new WebUiHost(_config);

      Topic portTopic = _config.PortTopic;
      // A read, not a seed - the topic was created by the Get above. AsInt keeps the 0 that
      // means "nothing configured" when the topic carries no integer.
      int configuredPort = portTopic.GetState().AsInt(0);

      if(configuredPort >= 1 && configuredPort <= 65535) {
        if(TryStart(configuredPort)) return;
        Log.Error("WebUI start failed: configured port {0} is unavailable", configuredPort);
        return;
      }
      foreach(int port in new int[] { 80, 8080, 8081, 8082, 8083, 8084, 8085, 8086, 8087, 8088, 8089 }) {
        if(TryStart(port)) {
          portTopic.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
          portTopic.SetState(port);
          return;
        }
      }
      Log.Error("WebUI start failed: no free port in [80, 8080..8089]");
    }

    // The view layer's only execution point. Everything a session does - frames from the socket,
    // repository callbacks, teardown - is queued and runs here, on the engine thread, which is
    // why that layer needs no locks. Priority 20 puts this after the repository's own tick
    // (priority 1), so the repository has finished dispatching before a pass starts.
    public void Tick() {
      WebUiHost.Pump();
    }

    public void Stop() {
      _host?.Stop();
      DashboardAcl.Stop();
      _config?.Dispose();
    }

    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        // Is<bool>, NOT AsBool: this decides whether the config topic has to be CREATED and
        // seeded, and a reader with a default cannot tell "not set yet" from "set to the
        // default", so the topic would never be created. Is is the type test without the
        // coercion - the same thing EnsureCfg does for every other setting here.
        if(!Owner.GetState().Is<bool>()) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
      }
    }

    private bool TryStart(int port) {
      return _host != null && _host.TryStart(port);
    }
  }
}
