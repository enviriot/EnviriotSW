///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace X13 {
  internal class Programm {
    private static void Main(string[] args) {
      string name = Assembly.GetExecutingAssembly().Location;
      string path = Path.GetDirectoryName(name);
      string cfgPath = Path.Combine(path, "../data/enviriot.xst");
      int flag = Environment.UserInteractive ? 0 : 1;
      for(int i = 0; i < args.Length; i++) {
        if(string.IsNullOrWhiteSpace(args[i])) {
          continue;
        }
        if(args[i].Length > 1 && (args[i][0] == '/' || args[i][0] == '-')) {
          switch(args[i][1]) {
          case 's':
            flag = 1;
            break;
          case 'i':
            flag = 2;
            break;
          case 'u':
            flag = 3;
            break;
          }
        } else if(File.Exists(args[i])) {
          cfgPath = Path.GetFullPath(args[i]);
        }
      }
      Directory.SetCurrentDirectory(path);
      if(flag != 1) {
        if(!CSWindowsServiceRecoveryProperty.Win32.AttachConsole(-1))  // Attach to a parent process console
          CSWindowsServiceRecoveryProperty.Win32.AllocConsole(); // Alloc a new console if none available
      }
      if(flag == 0) {
        var srv = new Programm(cfgPath);
        if(srv.Start()) {
          Console.ForegroundColor = ConsoleColor.Green;
          Console.WriteLine("Enviriot v.{0}", Assembly.GetExecutingAssembly().GetName().Version.ToString(4));
          Console.WriteLine("Press Enter to Exit");
          Console.ResetColor();
          Console.Read();
          srv.Stop();
        } else {
          Console.ForegroundColor = ConsoleColor.Magenta;
          Console.WriteLine("Enviriot start FAILED; press Enter to Exit");
          Console.ResetColor();
          Console.Read();
        }
        Console.ForegroundColor = ConsoleColor.Gray;
      } else if(flag == 1) {
        try {
          HAServer.Run(cfgPath);
        }
        catch(Exception ex) {
          Log.Error("{0}", ex.ToString());
        }

      } else if(flag == 2) {
        try {
          HAServer.InstallService(name);
        }
        catch(Exception ex) {
          Log.Error("{0}", ex.ToString());
        }
      } else if(flag == 3) {
        try {
          HAServer.UninstallService(name);
        }
        catch(Exception ex) {
          Log.Error("{0}", ex.ToString());
        }
      }
    }
    public static bool IsLinux {
      get {
        int p = (int)Environment.OSVersion.Platform;
        return (p == 4) || (p == 6) || (p == 128);
      }
    }

    private string _cfgPath;
    private Mutex _singleInstance;
    private Thread _thread;
    private AutoResetEvent _tick;
    private bool _terminate;
    private Timer _tickTimer;

    internal Programm(string cfgPath) {
      _cfgPath = cfgPath;
    }
    internal bool Start() {
      _singleInstance = new Mutex(true, "Global\\X13.enviriot");

      AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
      AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
      if(!_singleInstance.WaitOne(TimeSpan.Zero, true)) {
        Log.Error("only one instance at a time");
        _singleInstance = null;
        return false;
      }
      if(!LoadPlugins()) {
        return false;
      }
      _tick = new AutoResetEvent(false);
      _terminate = false;
      _thread = new Thread(new ThreadStart(PrThread));
      _thread.Priority = ThreadPriority.AboveNormal;
      _thread.IsBackground = false;
      _thread.Start();

      return true;
    }

    internal void Stop() {
      _terminate = true;
      _tick.Set();
      if(!_thread.Join(3500)) {
        _thread.Abort();
      }
      if(_singleInstance != null) {
        _singleInstance.ReleaseMutex();
      }
      Log.Finish();
    }
    private void PrThread() {
      if(!IsLinux) {
        int cpuCnt = System.Environment.ProcessorCount;
        if(cpuCnt > 1) {
          int r = CSWindowsServiceRecoveryProperty.Win32.SetThreadAffinityMask(CSWindowsServiceRecoveryProperty.Win32.GetCurrentThread(), 1 << (cpuCnt - 1));
        }
      }
      InitPlugins();
      StartPlugins();

      _tickTimer = new Timer(Tick, null, 200, 31);  // Tick = 1000/64 = 15.625 mS
      int i;
      do {
        _tick.WaitOne();
        JsExtLib.Tick();
        for(i = 0; i < _modules.Length; i++) {
          try {
            _modules[i].Tick();
          }
          catch(Exception ex) {
            Log.Error("{0}.Tick() - {1}", _modules[i].GetType().FullName, ex.ToString());
          }
        }
      } while(!_terminate);
      _tickTimer.Change(-1, -1);
      StopPlugins();
    }
    private void Tick(object o) {
      _tick.Set();
    }
    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
      try {
        Log.Error("unhandled Exception {0}", e.ExceptionObject.ToString());
      }
      catch {
      }
      try {
        this.Stop();
      }
      catch {
      }
    }
    private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args) {
      if(args.Name != null && !args.Name.Contains(".resources")) {
        Log.Error("AssemblyResolve failed: {0}", args.Name);
      }
      return null;
    }

    #region Plugins
#pragma warning disable 649
    [ImportMany(typeof(IPlugModul), RequiredCreationPolicy = CreationPolicy.Shared)]
    private IEnumerable<Lazy<IPlugModul, IPlugModulData>> _impModules;
#pragma warning restore 649
    private IPlugModul[] _modules;

    private bool LoadPlugins() {
      string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

      var catalog = new AggregateCatalog();
      catalog.Catalogs.Add(new AssemblyCatalog(Assembly.GetExecutingAssembly()));
      catalog.Catalogs.Add(new DirectoryCatalog(path));
      CompositionContainer _container = new CompositionContainer(catalog);
      try {
        _container.ComposeParts(this);
      }
      catch(CompositionException ex) {
        Log.Error("Load plugins - {0}", ex.ToString());
        return false;
      }
      _impModules = _impModules.OrderBy(z => z.Metadata.priority).ToArray();
      return true;
    }
    private void InitPlugins() {
      string pName;
      foreach(var i in _impModules) {
        if(!i.Value.enabled) {
          continue;
        }
        pName = i.Metadata.name ?? i.Value.GetType().FullName;
        try {
          i.Value.Init();
          Log.Debug("plugin {0} Initialized", pName);
        }
        catch(Exception ex) {
          Log.Error("Init plugin {0} failure - {1}", pName, ex.ToString());
          i.Value.enabled = false;
        }
      }
    }
    private void StartPlugins() {
      string pName;
      foreach(var i in _impModules) {
        if(!i.Value.enabled) {
          continue;
        }
        pName = i.Metadata.name ?? i.Value.GetType().FullName;
        try {
          i.Value.Start();
          Log.Debug("plugin {0} Started", pName);
        }
        catch(Exception ex) {
          Log.Error("Start plugin {0} failure - {1}", pName, ex.ToString());
          i.Value.enabled = false;
        }
      }
      _modules = _impModules.Where(z => z.Value.enabled).Select(z => z.Value).ToArray();
    }
    private void StopPlugins() {
      foreach(var i in _modules.Reverse()) {
        try {
          i.Stop();
        }
        catch(Exception ex) {
          Log.Error("Stop plugin {0} failure - {1}", i.GetType().FullName, ex.ToString());
        }
      }
    }
    #endregion Plugins
  }
}
