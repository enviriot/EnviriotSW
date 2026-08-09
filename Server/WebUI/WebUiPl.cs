///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.ComponentModel.Composition;
using System.IO;
using X13.Repository;
using NiL.JS.Extensions;

namespace X13.WebUI {
  [Export(typeof(IPlugModul))]
  [ExportMetadata("priority", 20)]
  [ExportMetadata("name", "WebUI")]
  internal sealed class WebUiPl : IPlugModul {
    private Topic _owner;
    private Topic _verbose;
    private WebUiHost _host;
    private string _staticPath;

    public void Init() {
    }

    public void Start() {
      {  // Ensure StaticPath topic exists and is configured
        const string DefaultStaticPath = "..\\www";
        Topic t = Owner.Get("StaticPath", true);
        if(t.GetState().ValueType != JSC.JSValueType.String) {
          t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
          t.SetState(DefaultStaticPath);
        }
        string configured = t.GetState().As<string>();
        _staticPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured ?? DefaultStaticPath));
      }
      EnsureVerboseTopic();
      _host = new WebUiHost(_staticPath, IsVerbose);

      int configuredPort = 0;

      Topic portTopic = Owner.Get("port", true);
      if(portTopic.GetState().ValueType == JSC.JSValueType.Integer) {
        configuredPort = (int)portTopic.GetState();
      }

      if(configuredPort >= 1 && configuredPort <= 65535) {
        if(TryStart(configuredPort)) return;
        Log.Error("WebUI start failed: configured port {0} is unavailable", configuredPort);
        return;
      }
      foreach(int port in new int[] { 80, 8080, 8081, 8082, 8083, 8084, 8085, 8086, 8087, 8088, 8089 }) {
        if(TryStart(port)) {
          portTopic.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
          portTopic.SetState(port);
          return;
        }
      }
      Log.Error("WebUI start failed: no free port in [80, 8080..8089]");
    }

    public void Tick() {
    }

    public void Stop() {
      _host?.Stop();
    }

    public bool enabled {
      get {
        Topic t = Owner;
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
      return _verbose != null && _verbose.GetState().ValueType == JSC.JSValueType.Boolean && (bool)_verbose.GetState();
    }

    private bool TryStart(int port) {
      return _host != null && _host.TryStart(port);
    }
  }
}
