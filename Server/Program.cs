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
          // Reachable at last. While PrThread answered a failed startup with Environment.Exit(1),
          // the window simply vanished and this branch never ran.
          srv.Stop();
          Environment.ExitCode = 1;   // so a script that launched this can tell
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

    /// <summary>How long a pass of the engine loop takes, published to /$YS/Performance.</summary>
    /// <remarks>The loop has about 17 mS to do a pass and nothing measured whether it did: what stood
    /// here was a commented-out Stopwatch that printed an average to the debug log by hand, which
    /// is exactly the shape of a measurement nobody takes.</remarks>
    private long _tickTicks;
    private long _periodTicks;
    private long _periodMax;
    private int _periodCount;
    private long _lastPass;
    private long _tickMax;
    private int _tickCount;
    private readonly FaultThrottle _faults = new FaultThrottle();
    private Mutex _singleInstance;
    private Thread _thread;
    private AutoResetEvent _tick;
    private volatile bool _terminate;
    private Timer _tickTimer;

    /// <summary>0 while nobody is tearing down, 1 once somebody is. See Stop().</summary>
    /// <remarks>Reset at the top of Start rather than by Stop, so that the latch also makes a repeated Stop a
    /// no-op: only a fresh start makes the teardown available again.</remarks>
    private int _stopping;

    /// <summary>How PrThread tells Start() whether the server actually came up.</summary>
    /// <remarks>Start() used to return true the moment the thread was created, while Init and
    /// Start of every plugin still lay ahead of it on that thread - so the one thing the caller
    /// asked was answered before it could be known.
    /// <para>Only the ANSWER crosses threads; the work does not move. Running InitPlugins on the
    /// caller's thread would be the obvious way to make Start() honest and would break the script
    /// engine: NiL.JS binds a compiled Function to the context active on the compiling thread,
    /// and Repo.Init compiles the scripts in server.xst. See ActivateEngineOnThisThread.</para>
    /// <para>The wait is bounded but generous. Startup legitimately takes a while - LiteDB_Pl.Init
    /// copies the whole database to a backup before opening it - and reporting a failure that has
    /// not happened would be worse than the lie this replaces.</para></remarks>
    internal const int StartupTimeoutMs = 120000;
    private readonly ManualResetEvent _startupDone = new ManualResetEvent(false);
    private volatile bool _startupOk;

    /// <summary>How long the engine thread gets to finish, StopPlugins included.</summary>
    /// <remarks>It was 3500 ms, and the arithmetic did not work: StopPlugins runs ON this thread,
    /// and PersistentStorage and Archivist each wait up to five seconds for their own worker - ten
    /// seconds between them before the other five plugins are counted. So the outer limit expired
    /// first as a matter of course, and the abort below landed in the middle of a plugin closing
    /// its database: the shutdown that most needed to finish cleanly was the one guaranteed not
    /// to. An outer bound has to exceed the inner ones it contains.</remarks>
    private const int ShutdownTimeoutMs = 20000;

    /// <summary>/$YS/Performance - publish process counters into the tree every 317 seconds.</summary>
    /// <remarks>The topic was spelled "Perfomance" until now. Renamed rather than kept: it is a
    /// name users read in the tree, and nothing in the repository, the wire protocol or the
    /// clients refers to it - the only mention in the whole codebase was the Get() below.
    /// <para>No migration: an existing "Perfomance" topic stays where it is, carrying whatever it
    /// carried, and has to be deleted by hand. Someone who had it set to true finds the counters
    /// off after the upgrade until they set the new one - the same call already made when the
    /// /$YS/WebUI regrouping dropped its own migration once it had run.</para></remarks>
    private bool _performance;
    private Repository.Topic _performanceT;
    private Repository.SubRec _performanceSR;

    internal Program(string cfgPath) {
      X13.Repository.Repo.configPath = cfgPath;
      Log.Info("Enviriot v.{0}", Assembly.GetExecutingAssembly().GetName().Version.ToString(4));
    }
    internal bool Start() {
      // First, and not next to the other resets below: Start can fail before it reaches them -
      // the mutex may be held, LoadPlugins may throw - and Main calls Stop on the way out either
      // way. Leaving the latch set from a previous run would make that Stop a no-op and strand
      // the AppDomain handlers this method is about to register.
      _stopping = 0;

      // false, not true: a named mutex is recursive for the thread that owns it, so creating it
      // owned and then waiting on it took the ownership count to two while Stop releases once.
      // The mutex stayed held after Stop, and a second Start in the same process would have been
      // told there was already an instance running. Ownership comes from the WaitOne below, once.
      _singleInstance = new Mutex(false, "Global\\X13.enviriot");

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

      if(!_startupDone.WaitOne(StartupTimeoutMs)) {
        // Not a failure that has been reported - a startup that has not finished answering. The
        // thread is left running: it may still come up, and tearing it down from here would race
        // whatever plugin is currently in its Init.
        Log.Error("Server startup has not completed within {0} s; see the log for the plugin it is waiting on", StartupTimeoutMs / 1000);
        return false;
      }
      return _startupOk;
    }

    /// <summary>Undoes Start, and survives being called when Start never got that far.</summary>
    /// <remarks>Both guards are needed now that the handles below are released: Start returns
    /// early when the mutex is already held or LoadPlugins fails, leaving _thread unstarted or
    /// _tick null, and Main calls Stop on the way out regardless.
    /// <para>The latch is not tidiness, and the null checks below are no substitute for it: two
    /// threads pass every one of them. Stop has a second caller that is easy to miss - Start
    /// registers CurrentDomain_UnhandledException, that handler calls Stop, and the unregistration
    /// is at the far end of a join that lasts up to ShutdownTimeoutMs. Shutdown is exactly when a
    /// plugin's worker thread dies of having its database closed under it, so the arrangement is
    /// Main stopping while a dying thread starts stopping too. Unguarded, the second one disposes
    /// _tick and the container from under the engine thread the first is still joining - and
    /// PrThread's _tick.WaitOne() has no guard of its own to survive that.</para></remarks>
    internal void Stop() {
      if(Interlocked.CompareExchange(ref _stopping, 1, 0) != 0) {
        return;
      }
      _terminate = true;
      AutoResetEvent tickEv = _tick;
      if(tickEv != null) {
        tickEv.Set();
      }
      if(_thread != null && _thread.IsAlive && !_thread.Join(ShutdownTimeoutMs)) {
        // Named before it happens, because an abort lands wherever the thread was: StopPlugins
        // runs on this thread, so the instruction interrupted can be inside a plugin closing its
        // database. Kept only because this thread is IsBackground = false - it is what holds the
        // process up, so abandoning it would hang the exit rather than finish it.
        Log.Error("Engine thread did not stop within {0} s; aborting it", ShutdownTimeoutMs / 1000);
        _thread.Abort();
      }
      // EnsureCfg hands ownership of the subscription to the caller. Plain, not Interlocked:
      // the Join above has already ended the only other thread that could touch it.
      if(_performanceSR != null) {
        _performanceSR.Dispose();
        _performanceSR = null;
      }
      // Symmetric with Start, so a second Start in the same process finds nothing left over. It
      // mattered little while Stop was only ever the last thing before exit, and it is what makes
      // the sequence testable at all.
      Timer tickTimer = _tickTimer;
      _tickTimer = null;
      if(tickTimer != null) {
        tickTimer.Dispose();
      }
      AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
      AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
      // Released in a try because ownership of a mutex belongs to a THREAD: Start takes it with
      // WaitOne on the caller's, and ReleaseMutex throws ApplicationException for anybody else.
      // Stop does not always run on that thread - the unhandled-exception handler runs on whichever
      // thread is dying - and the throw used to escape from here into that handler's blanket catch,
      // so on the one path where the log matters most, everything below was skipped: the tick event
      // undisposed, the plugin container never disposed, and Log.Finish() - the flush that would
      // have written out what the crash was - not called. Disposing regardless is what actually
      // frees the handle; the release is the polite half, and the process is ending either way.
      if(_singleInstance != null) {
        try {
          _singleInstance.ReleaseMutex();
        }
        catch(ApplicationException) {
        }
        _singleInstance.Dispose();
        _singleInstance = null;
      }
      // After the thread has joined: PrThread waits on it every pass, and disposing a handle
      // something is blocked on is exactly the fault this ordering exists to avoid elsewhere.
      AutoResetEvent tick = _tick;
      _tick = null;
      if(tick != null) {
        tick.Dispose();
      }
      // Last, and after StopPlugins has run on the engine thread: disposing the container disposes
      // the parts it composed, and a plugin has to have had its own Stop() before that.
      CompositionContainer container = _container;
      _container = null;
      if(container != null) {
        try {
          container.Dispose();
        }
        catch(Exception ex) {
          Log.Warning("Plugin container dispose - {0}", ex.Message);
        }
      }
      Log.Finish();
    }
    private void PrThread() {
      DateTime now = DateTime.Now, today = now.Date, performanceDT = now.AddSeconds(10), gcTick = now.AddSeconds(5);
      Tuple<bool, DateTime, double> perf_cpu = new Tuple<bool, DateTime, double>(true, now, 0);

      // This thread owns the script engine, said out loud rather than left to chance. NiL.JS keeps
      // the active-context stack in a [ThreadStatic] field, and a Function captures
      // Context.CurrentContext when it is COMPILED (BaseLibrary/Function.cs) - falling back to
      // NiL.JS's own DefaultGlobalContext when there is none. That fallback is silent and it is
      // the wrong global: setTimeout, console, File and Arch are defined on ours alone, so a
      // script compiled on the wrong thread would simply find them undefined.
      // Scripts are compiled from Repo.Init -> Import(server.xst) below, so the claim has to be
      // staked before that. It used to depend on which thread happened to touch JsExtLib first.
      JsExtLib.ActivateEngineOnThisThread();

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
        // Reported, not acted on. Environment.Exit(1) stood here: a worker thread deciding the
        // fate of the process, which meant the console host's own "start FAILED" branch was
        // unreachable, the service died without telling the SCM, and this path could not be
        // covered by a test - the runner would have exited with it.
        _startupOk = false;
        _startupDone.Set();
        return;
      }

      // After StartPlugins, because Topic.root does not exist until Repo.Init has run, and before
      // the tick timer, so the first pass 10 seconds from now already reads a settled value.
      _performanceT = Repository.Topic.root.Get("/$YS/Performance", true);
      _performanceSR = JsExtLib.EnsureCfg(Repository.Topic.root.Get("/$YS", true), "Performance",
        Repository.Topic.Attribute.DB | Repository.Topic.Attribute.Required, v => _performance = v, false);

      // 5, not 15, and the difference is measurable. The Windows timer granule is 15.625 mS and
      // System.Threading.Timer schedules the next fire relative to the callback, not on a fixed
      // grid: a request of 15 falls 0.625 mS short of the next granule, so any dispatch slip past
      // that costs a WHOLE granule and the beat takes 31.25 instead. Measured: request 15 gave a
      // 19.05 mS period (52 Hz), request 10 gave 17.40, request 5 gives 16.98 - about 59 Hz.
      // A smaller request only buys slack; it cannot buy a shorter granule.
      //
      // So this is not 64 Hz and never was - /$YS/Performance/Period now says what it is. Getting
      // to 15.625 would need timeBeginPeriod, which raises the timer resolution for the whole
      // machine and its power profile with it; that is a bigger decision than a control loop that
      // runs at 59 Hz instead of 64.
      // Not armed once a teardown has begun. Stop() disposes the timer only after joining this
      // thread, so the ordinary shutdown cannot orphan one - but it skips the join when the thread
      // is not yet alive, and abandons it when the join times out and the abort does not take. This
      // line sits at the far end of a startup Start() may have given up waiting for two minutes
      // ago, so both are reachable, and what they leave behind is a timer nobody owns firing Tick
      // into a disposed _tick. Re-read after the assignment: the check alone still loses to a Stop
      // that lands between the two, and then it is this thread that has to clean up.
      if(!_terminate) {
        _tickTimer = new Timer(Tick, null, 100, 5);
        if(_terminate) {
          Timer orphan = _tickTimer;
          _tickTimer = null;
          if(orphan != null) {
            orphan.Dispose();
          }
        }
      }
      // Everything a caller of Start() was promised is now true: plugins initialised, started,
      // and the tick armed. Only here does Start() stop waiting.
      _startupOk = true;
      _startupDone.Set();
      int i;
      do {
        now = DateTime.Now;
        if(performanceDT < now) {
          performanceDT = now.AddSeconds(317);
          if(_performance) {
            Repository.Topic perf = _performanceT;
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
            // What the loop is actually for: it has one beat to do a pass, and until now nothing
            // measured whether it did. Average and worst since the last publication, plus the
            // longest single script callback - the one thing inside a pass that a user writes.
            perf.Get("Tick").SetState(Math.Round(_tickCount == 0 ? 0 : _tickTicks * 1000.0 / (_tickCount * (double)System.Diagnostics.Stopwatch.Frequency), 3));  // mS, average
            perf.Get("TickMax").SetState(Math.Round(_tickMax * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3));  // mS
            perf.Get("Period").SetState(Math.Round(_periodCount == 0 ? 0 : _periodTicks * 1000.0 / (_periodCount * (double)System.Diagnostics.Stopwatch.Frequency), 3));  // mS, average
            perf.Get("PeriodMax").SetState(Math.Round(_periodMax * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3));  // mS
            perf.Get("Script").SetState(Math.Round(X13.JsExtLib.TakeMaxCallbackMs(), 3));  // mS
            perf.Get("Updated").SetState(X13.JsExtLib.Context.ProxyValue(now));
          } else {
            perf_cpu = new Tuple<bool, DateTime, double>(false, now, 0);
          }
          // Reset either way: counted while nobody looks, the worst pass would otherwise be the
          // worst since the process started and would say nothing about the last five minutes.
          _tickTicks = 0;
          _tickMax = 0;
          _tickCount = 0;
          _periodTicks = 0;
          _periodMax = 0;
          _periodCount = 0;
        }
        if(_isLinux && gcTick < now) {
          gcTick = now.AddSeconds(887);
          GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
        }
        _tick.WaitOne();
        long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // How often a pass happens, as against how long one takes. The timer fires into an
        // AutoResetEvent every 15 mS, and that event does not count: a Set onto an already-set
        // event is lost, so a pass that overruns does not catch up afterwards - it silently skips
        // beats. The period is where that shows, and a stall nothing else sees - a GC pause, the
        // thread not being scheduled - shows here and not in the duration.
        if(_lastPass != 0) {
          long period = passStart - _lastPass;
          _periodTicks += period;
          _periodCount++;
          if(period > _periodMax) {
            _periodMax = period;
          }
        }
        _lastPass = passStart;
        // Guarded, and it was not. JsExtLib.Tick runs the script timers, and anything escaping it
        // left this loop for good: the engine thread ended, the process carried on answering, and
        // nothing ticked again - the same shape the repository's own tick had before it grew a
        // finally, one level further out.
        try {
          JsExtLib.Tick();
        }
        catch(Exception ex) {
          _faults.Report(true, "JsExtLib.Tick", null, ex);
        }
        for(i = 0; i < _modules.Length; i++) {
          long started = System.Diagnostics.Stopwatch.GetTimestamp();
          try {
            _modules[i].Tick();
          }
          catch(Exception ex) {
            // A plugin that throws goes on being ticked. Stopping it would turn one bad pass into
            // a subsystem that never runs again - an MQTT that never reconnects - with nothing
            // able to bring it back, and these faults are usually transient. What it no longer
            // gets is a full stack trace on every beat: the throttle writes one and counts
            // the rest, and says how many there were once they stop.
            _faults.Report(false, _modules[i].GetType().FullName + ".Tick",
              ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms", ex);
          }
        }
        _faults.Flush(now);
        long passTicks = System.Diagnostics.Stopwatch.GetTimestamp() - passStart;
        _tickTicks += passTicks;
        _tickCount++;
        if(passTicks > _tickMax) {
          _tickMax = passTicks;
        }
        if(today!=now.Date) {
          today = now.Date;
          Log.Info("{0} v.{1}", today.ToLongDateString(), Assembly.GetExecutingAssembly().GetName().Version.ToString(4));
        }
      } while(!_terminate);
      // Null when the arming above stood aside, and StopPlugins is far too important to lose to
      // the NullReferenceException that used to follow: it runs on this thread and nowhere else.
      Timer tickTimer = _tickTimer;
      if(tickTimer != null) {
        try {
          tickTimer.Change(-1, -1);
        }
        catch(ObjectDisposedException) {
          // Stop() got here first, which is the outcome this line wanted anyway.
        }
      }
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
    private CompositionContainer _container;

    /// <summary>How far the server got with one plugin, starting from whether it runs at all.</summary>
    /// <remarks>IPlugModul.enabled is a startup decision and nothing else: it is read once, here,
    /// and a topic edited afterwards changes nothing until the next start. That is what makes it
    /// the first rung of this ladder rather than a separate flag to keep in agreement with it.
    /// <para>The "-ing" states are where the fix lives: a plugin was recorded only after Start()
    /// returned, so one that threw halfway through, holding its port, its thread and its database,
    /// was the single plugin StopPlugins would never call. Teardown applies to everything above
    /// Enabled - that is, everything something has been called on.</para></remarks>
    private enum PlugState {
      Disabled = 0,   // found by MEF, and /$YS/<name> says no
      Enabled,        // it runs, nothing called yet
      Initializing,   // Init() entered - resources may already be held
      Initialized,    // Init() returned
      Starting,       // Start() entered
      Started,        // Start() returned
    }
    private sealed class Plug {
      public readonly IPlugModul Modul;
      public readonly string Name;
      public PlugState State;

      public Plug(IPlugModul modul, string name) {
        Modul = modul;
        Name = name;
      }
    }
    /// <summary>Every discovered plugin in priority order, each with how far it got.</summary>
    /// <remarks>Disabled ones are kept rather than skipped: the list is then the whole roster,
    /// and the one test every consumer makes is a state, not a state plus a membership check.</remarks>
    private readonly List<Plug> _plugins = new List<Plug>();

    private bool LoadPlugins() {
      string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

      var catalog = new AggregateCatalog();
      catalog.Catalogs.Add(new AssemblyCatalog(Assembly.GetExecutingAssembly()));
      catalog.Catalogs.Add(new DirectoryCatalog(path));
      // Kept rather than dropped on the floor. The container owns the catalogs and the shared
      // parts it created - the plugins themselves - so letting the only reference go meant the
      // composition lived to the end of the process because nothing could release it, not because
      // anything had decided it should. Disposed in Stop, after StopPlugins: MEF disposes any part
      // implementing IDisposable, and a plugin must have had its own Stop() first.
      _container = new CompositionContainer(catalog);
      try {
        _container.ComposeParts(this);
      }
      catch(CompositionException ex) {
        Log.Error("Load plugins - {0}", ex.ToString());
        return false;
      }
      // Name breaks the tie, because priority does not: AntSw and MQTT both declare 8, and a plain
      // OrderBy is stable, so their order was whatever DirectoryCatalog happened to enumerate -
      // filesystem order, which nothing guarantees and which differs between machines. Ordinal, so
      // the answer does not depend on the machine's culture either.
      _impModules = _impModules
        .OrderBy(z => z.Metadata.priority)
        .ThenBy(z => z.Metadata.name ?? string.Empty, StringComparer.Ordinal)
        .ToArray();
      return true;
    }
    /// <summary>Builds the roster and initialises it in one pass, because it cannot be two.</summary>
    /// <remarks>Reading every plugin's enabled up front and initialising afterwards would be the
    /// tidier shape, and it does not work: enabled answers from the tree, and there is no tree
    /// until Repo.Init runs - Repo being priority 1, the first entry of this very loop. So each
    /// plugin's enabled is read only once it is that plugin's turn, with everything before it
    /// already initialised.</remarks>
    private bool InitPlugins() {
      foreach(var i in _impModules) {
        var p = new Plug(i.Value, i.Metadata.name ?? i.Value.GetType().FullName);
        _plugins.Add(p);
        if(!i.Value.enabled) {
          p.State = PlugState.Disabled;
          Log.Debug("plugin {0} disabled", p.Name);
          continue;
        }
        p.State = PlugState.Enabled;
        try {
          p.State = PlugState.Initializing;   // set before the call, not after
          p.Modul.Init();
          p.State = PlugState.Initialized;
          Log.Debug("plugin {0} Initialized", p.Name);
        }
        catch(Exception ex) {
          Log.Error("Init plugin {0} failure - {1}", p.Name, ex.ToString());
          return false;
        }
      }
      return true;
    }
    /// <summary>Starts what Init went through, in the same order and without asking again.</summary>
    /// <remarks>The state decides, not a second read of enabled: that property answers from the
    /// tree, so asking twice let a topic edited between the two passes produce a plugin that was
    /// initialised and never started, or started without ever being initialised.</remarks>
    private bool StartPlugins() {
      for(int i = 0; i < _plugins.Count; i++) {
        Plug p = _plugins[i];
        if(p.State != PlugState.Initialized) {
          continue;   // disabled, and nothing else can be here after a successful InitPlugins
        }
        try {
          p.State = PlugState.Starting;
          p.Modul.Start();
          p.State = PlugState.Started;
          Log.Debug("plugin {0} Started", p.Name);
        }
        catch(Exception ex) {
          Log.Error("Start plugin {0} failure - {1}", p.Name, ex.ToString());
          return false;
        }
      }
      _modules = _plugins.Where(z => z.State == PlugState.Started).Select(z => z.Modul).ToArray();
      return true;
    }
    /// <summary>Undoes every state that was reached, in reverse, whether or not it completed.</summary>
    /// <remarks>Stop() is the only teardown IPlugModul has - there is no Deinit - so a plugin that
    /// was initialised but never started is stopped here too. That may hand Stop() a half-built
    /// object, which is why each call is caught separately and the state is named in the message:
    /// a logged error from one plugin's teardown is a better outcome than the port, thread or
    /// database file another one is still holding.</remarks>
    private void StopPlugins() {
      for(int i = _plugins.Count - 1; i >= 0; i--) {
        Plug p = _plugins[i];
        if(p.State <= PlugState.Enabled) {
          continue;   // nothing was ever called on it
        }
        try {
          p.Modul.Stop();
        }
        catch(Exception ex) {
          Log.Error("Stop plugin {0} failure, reached {1} - {2}", p.Name, p.State, ex.ToString());
        }
        p.State = PlugState.Enabled;
      }
      _plugins.Clear();
    }
    #endregion Plugins
  }
}
