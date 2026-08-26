///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace X13 {
  internal class Program {
    private static bool _isLinux;

    private static void Main(string[] args) {
      string name = Assembly.GetExecutingAssembly().Location;
      string path = Path.GetDirectoryName(name);
      string cfgPath = Path.Combine(path, "../server.xst");
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
        // Attach to a parent process console, alloc a new one if none available
        if(!CSWindowsServiceRecoveryProperty.Win32.AttachConsole(-1) && !CSWindowsServiceRecoveryProperty.Win32.AllocConsole()) {
          Log.Debug("no console available - {0}", Marshal.GetLastWin32Error());
        }
      }
        int p = (int)Environment.OSVersion.Platform;
      _isLinux = (p == 4) || (p == 6) || (p == 128);

      if(flag == 0) {
        var srv = new Program(cfgPath);
        bool started;
        try {
          started = srv.Start();
        }
        catch(Exception ex) {
          Log.Error("{0}", ex.ToString());
          started = false;
        }
        if(started) {
          Console.ForegroundColor = ConsoleColor.Green;
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

      } else if(flag == 2 || flag == 3) {
        if(!IsElevated()) {
          Console.ForegroundColor = ConsoleColor.Magenta;
          Console.WriteLine("{0} the {1} service requires administrator rights.", flag == 2 ? "Installing" : "Removing", HAServer.SERVICE_NAME);
          Console.WriteLine("Restart this command from an elevated console.");
          Console.ResetColor();
          Environment.ExitCode = CSWindowsServiceRecoveryProperty.Win32.ERROR_ACCESS_DENIED;
          return;
        }
        try {
          if(flag == 2) {
            HAServer.InstallService(name);
          } else {
            HAServer.UninstallService(name);
          }
        }
        catch(Exception ex) {
          Log.Error("{0}", ex.ToString());
          Environment.ExitCode = 1;  // otherwise a failed install is indistinguishable from success
        }
      }
    }
    public static bool IsLinux { get { return _isLinux; } }

    /// <summary>True when the process can talk to the SCM as an administrator.</summary>
    /// <remarks>Errs towards true: the SCM call itself reports a precise error, so a failed or
    /// inapplicable check must never be the thing that blocks the operation.</remarks>
    private static bool IsElevated() {
      if(_isLinux) {
        return true;  // no UAC, and the SCM path does not apply there anyway
      }
      try {
        using(var id = WindowsIdentity.GetCurrent()) {
          return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
      }
      catch(Exception ex) {
        Log.Warning("IsElevated - {0}", ex.Message);
        return true;
      }
    }

    private Mutex _singleInstance;
    private Thread _thread;
    private AutoResetEvent _tick;
    private bool _terminate;
    private Timer _tickTimer;

    internal Program(string cfgPath) {
      X13.Repository.Repo.configPath = cfgPath;
      Log.Info("Enviriot v.{0}", Assembly.GetExecutingAssembly().GetName().Version.ToString(4));
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
      _tick = new AutoResetEvent(false);
      _terminate = false;
      _thread = new Thread(new ThreadStart(PrThread));
      _thread.Priority = ThreadPriority.Highest;
      _thread.Name = "MainTick";
      _thread.IsBackground = false;

      if(!LoadPlugins()) {
        return false;
      }

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
      DateTime now = DateTime.Now, today = now.Date, perfomanceDT = now.AddSeconds(10), gcTick = now.AddSeconds(5);
      Tuple<bool, DateTime, double> perf_cpu = new Tuple<bool, DateTime, double>(true, now, 0);

      if(!IsLinux) {
        int cpuCnt = System.Environment.ProcessorCount;
        if(cpuCnt > 1) {
          var mask = (UIntPtr)AffinityMask(cpuCnt, IntPtr.Size * 8);
          if(CSWindowsServiceRecoveryProperty.Win32.SetThreadAffinityMask(CSWindowsServiceRecoveryProperty.Win32.GetCurrentThread(), mask) == UIntPtr.Zero) {
            Log.Warning("SetThreadAffinityMask(0x{0:X}) failed - {1}", (ulong)mask, Marshal.GetLastWin32Error());
          }
        }
      }
      if(!InitPlugins() || !StartPlugins()) {
        StopPlugins();
        Log.Error("Fatal plugin startup failure, stopping server");
        Log.Finish();
        Environment.Exit(1);
        return;
      }

      _tickTimer = new Timer(Tick, null, 100, 15);  // Tick = 1000/64 = 15.625 mS
      int i;
      //int j=1920;
      //TimeSpan t = TimeSpan.Zero;
      //var sw = new System.Diagnostics.Stopwatch();
      //sw.Start();
      do {
        now = DateTime.Now;
        if(perfomanceDT < now) {
          perfomanceDT = now.AddSeconds(317);
          var perf = X13.Repository.Topic.root.Get("/$YS/Perfomance");
          if(perf.GetState().ValueType!=NiL.JS.Core.JSValueType.Boolean) {
            perf.SetAttribute(Repository.Topic.Attribute.DB | Repository.Topic.Attribute.Required);
            perf.SetState(false);
          } else if((bool)perf.GetState()) {
            perf.Get("GC").SetState(Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2));  // MB
            using(var proc = System.Diagnostics.Process.GetCurrentProcess()) {
              perf.Get("Memory").SetState(Math.Round(proc.PrivateMemorySize64 / 1048576.0, 2));  // MB
              perf.Get("Virtual").SetState(Math.Round(proc.VirtualMemorySize64 / 1048576.0, 2));  // MB
              var cpu = proc.TotalProcessorTime.TotalSeconds;
              if(perf_cpu.Item1) {
                perf.Get("CPU").SetState(Math.Round((cpu - perf_cpu.Item3)*100 / (now - perf_cpu.Item2).TotalSeconds, 2));  // Sec
              }
              perf_cpu = new Tuple<bool, DateTime, double>(true, now, cpu);
              perf.Get("Physical").SetState(Math.Round(proc.WorkingSet64 / 1048576.0, 2));  // MB
            }
            perf.Get("Updated").SetState(X13.JsExtLib.Context.ProxyValue(now));
          } else {
            perf_cpu = new Tuple<bool, DateTime, double>(false, now, 0);
          }
        }
        if(_isLinux && gcTick < now) {
          gcTick = now.AddSeconds(887);
          GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
        }
        _tick.WaitOne();
        //sw.Stop();
        //t+=sw.Elapsed;
        //if(--j<=0) {
        //  j=1920;
        //  Log.Debug("Tick = {0:0.000} mS", t.TotalMilliseconds/1920);
        //  t = TimeSpan.Zero;
        //}
        //sw.Restart();
        JsExtLib.Tick();
        for(i = 0; i < _modules.Length; i++) {
          try {
            _modules[i].Tick();
          }
          catch(Exception ex) {
            Log.Error("{0}.Tick() - {1}", _modules[i].GetType().FullName, ex.ToString());
          }
        }
        if(today!=now.Date) {
          today = now.Date;
          Log.Info("{0} v.{1}", today.ToLongDateString(), Assembly.GetExecutingAssembly().GetName().Version.ToString(4));
        }
      } while(!_terminate);
      _tickTimer.Change(-1, -1);
      //sw.Stop();
      StopPlugins();
    }
    /// <summary>Affinity mask selecting the last logical CPU.</summary>
    /// <remarks>The shift must happen in 64-bit arithmetic: C# masks the shift count of an int
    /// to 5 bits, so the old `1 &lt;&lt; (cpuCnt - 1)` silently produced 1 at 33 CPUs and
    /// int.MinValue at 64. The bit index is clamped to the pointer width because a mask wider
    /// than UIntPtr cannot be passed (and would throw on a 32-bit process).</remarks>
    internal static ulong AffinityMask(int cpuCnt, int pointerBits) {
      int bit = cpuCnt - 1;
      if(bit < 0) {
        bit = 0;
      }
      if(bit > pointerBits - 1) {
        bit = pointerBits - 1;
      }
      return 1UL << bit;
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
    private readonly List<IPlugModul> _started = new List<IPlugModul>();

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
    private bool InitPlugins() {
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
          return false;
        }
      }
      return true;
    }
    private bool StartPlugins() {
      string pName;
      foreach(var i in _impModules) {
        if(!i.Value.enabled) {
          continue;
        }
        pName = i.Metadata.name ?? i.Value.GetType().FullName;
        try {
          i.Value.Start();
          _started.Add(i.Value);
          Log.Debug("plugin {0} Started", pName);
        }
        catch(Exception ex) {
          Log.Error("Start plugin {0} failure - {1}", pName, ex.ToString());
          return false;
        }
      }
      _modules = _started.ToArray();
      return true;
    }
    private void StopPlugins() {
      for(int i = _started.Count - 1; i >= 0; i--) {
        try {
          _started[i].Stop();
        }
        catch(Exception ex) {
          Log.Error("Stop plugin {0} failure - {1}", _started[i].GetType().FullName, ex.ToString());
        }
      }
      _started.Clear();
    }
    #endregion Plugins
  }
}
