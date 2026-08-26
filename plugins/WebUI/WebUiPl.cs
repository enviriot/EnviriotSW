///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.ComponentModel.Composition;
using System.IO;
using X13.Repository;
using X13.WebUI.Helpers;
using NiL.JS.Extensions;

namespace X13.WebUI {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 10)]
  [ExportMetadata("name", "WebUI")]
  internal sealed class WebUiPl : IPlugModul {
    // "local" resolves to the subnets of this machine's adapters, see NetworkAcl.
    private const string DefaultTrustedNets = "local";

    private Topic _owner;
    private Topic _verbose;
    private Topic _trustedNets;
    private Topic _trustedProxies;
    private WebUiHost _host;
    private string _staticPath;

    public void Init() {
    }

    public void Start() {
      {  // Ensure StaticPath topic exists and is configured
        const string DefaultStaticPath = "..\\www";
        Topic t = Owner.Get("StaticPath", true);
        // Readonly so it is not changed by accident. It stays a free-form path on purpose:
        // behind a proxy this legitimately points at a shared directory outside the install.
        // Note this is a UI hint only - Readonly is not enforced on the commit path.
        if(!t.CheckAttribute(Topic.Attribute.Readonly)) {
          t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
        }
        // Deliberately a raw ValueType test, NOT AsBool/AsString: this decides whether the config
        // topic has to be CREATED and seeded. A reader with a default cannot tell "not set yet" from
        // "set to the default", so the topic would never be created. See todo.md.
        if(t.GetState().ValueType != JSC.JSValueType.String) {
          t.SetState(DefaultStaticPath);
        }
        string configured = t.GetState().AsString(null);
        _staticPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured ?? DefaultStaticPath));
      }
      EnsureVerboseTopic();
      EnsureAclTopics();
      // Before the host: the dashboard endpoint refuses every topic it has no rule for, so the
      // rules have to be in place by the time the first socket can arrive.
      DashboardAcl.Start();
      _host = new WebUiHost(_staticPath, IsVerbose, TrustedNets, TrustedProxies);

      int configuredPort = 0;

      Topic portTopic = Owner.Get("port", true);
      // A read, not a seed - the topic was created by the Get above. AsInt keeps whatever default
      // configuredPort already holds when the topic carries no integer.
      configuredPort = portTopic.GetState().AsInt(configuredPort);

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
    }

    public bool enabled {
      get {
        Topic t = Owner;
        // Deliberately a raw ValueType test, NOT AsBool/AsString: this decides whether the config
        // topic has to be CREATED and seeded. A reader with a default cannot tell "not set yet" from
        // "set to the default", so the topic would never be created. See todo.md.
        if(t.GetState().ValueType != JSC.JSValueType.Boolean) {
          t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          t.SetState(true);
          return true;
        }
        return (bool)t.GetState();
      }
      set { Owner.SetState(value); }
    }

    private Topic Owner {
      get { return _owner ?? (_owner = Topic.root.Get("/$YS/WebUI", true)); }
    }

    private void EnsureVerboseTopic() {
      _verbose = Owner.Get("verbose", true);
      // Deliberately a raw ValueType test, NOT AsBool/AsString: this decides whether the config
      // topic has to be CREATED and seeded. A reader with a default cannot tell "not set yet" from
      // "set to the default", so the topic would never be created. See todo.md.
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
    }

    private bool IsVerbose() {
      // A read, not a seed - so AsBool, unlike the ValueType tests in Init below.
      return _verbose != null && _verbose.GetState().AsBool(false);
    }

    private void EnsureAclTopics() {
      _trustedNets = Owner.Get("trustedNets", true);
      if(string.IsNullOrWhiteSpace(_trustedNets.GetState().AsString(null))) {
        _trustedNets.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        _trustedNets.SetState(DefaultTrustedNets);
      }
      _trustedProxies = Owner.Get("trustedProxies", true);
      // Deliberately a raw ValueType test, NOT AsBool/AsString: this decides whether the config
      // topic has to be CREATED and seeded. A reader with a default cannot tell "not set yet" from
      // "set to the default", so the topic would never be created. See todo.md.
      if(_trustedProxies.GetState().ValueType != JSC.JSValueType.String) {
        // Empty by default: X-Real-IP is then ignored for access decisions entirely.
        _trustedProxies.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
        _trustedProxies.SetState(string.Empty);
      }
    }

    // Read per check rather than cached at Start(), so editing the topic takes effect without
    // a restart. NetworkAcl only re-parses when the string actually changed.
    private string TrustedNets() {
      string value = _trustedNets == null ? null : _trustedNets.GetState().AsString(null);
      return string.IsNullOrWhiteSpace(value) ? DefaultTrustedNets : value;  // cleared => back to local
    }

    private string TrustedProxies() {
      string value = _trustedProxies == null ? null : _trustedProxies.GetState().AsString(null);
      return value ?? string.Empty;
    }

    private bool TryStart(int port) {
      return _host != null && _host.TryStart(port);
    }
  }
}
