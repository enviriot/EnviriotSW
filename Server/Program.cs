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
  /// <summary>Точка входа: консольный хост, служба Windows и движковый поток.</summary>
  /// <remarks>Один экземпляр на машину - через глобальный мьютекс. 
  /// Движковый поток тикает по таймеру и вызывает Tick() плагинов по возрастанию priority;
  /// он же раз в несколько минут публикует счётчики в /$YS/Performance.
  /// Start отвечает вызвавшему не в момент создания потока, а когда плагины подняты либо один из них отказал.</remarks>
  internal class Program {
    private static bool _isLinux;

    private static void Main(string[] args) {
      string name = Assembly.GetExecutingAssembly().Location;
      string path = Path.GetDirectoryName(name);
      string cfgPath = Path.Combine(path, "../server.xst");
      int flag = Environment.UserInteractive ? 0 : 1;
      for (int i = 0; i < args.Length; i++) {
        if (string.IsNullOrWhiteSpace(args[i])) {
          continue;
        }
        if (args[i].Length > 1 && (args[i][0] == '/' || args[i][0] == '-')) {
          switch (args[i][1]) {
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
        } else if (File.Exists(args[i])) {
          cfgPath = Path.GetFullPath(args[i]);
        }
      }
      Directory.SetCurrentDirectory(path);
      if (flag != 1) {
        // Подключаемся к консоли родительского процесса. Если она недоступна, создаём новую.
        if (!CSWindowsServiceRecoveryProperty.Win32.AttachConsole(-1) && !CSWindowsServiceRecoveryProperty.Win32.AllocConsole()) {
          Log.Debug("no console available - {0}", Marshal.GetLastWin32Error());
        }
      }
      int p = (int)Environment.OSVersion.Platform;
      _isLinux = (p == 4) || (p == 6) || (p == 128);

      if (flag == 0) {
        var srv = new Program(cfgPath);
        bool started;
        try {
          started = srv.Start();
        }
        catch (Exception ex) {
          Log.Error("{0}", ex.ToString());
          started = false;
        }
        if (started) {
          Console.ForegroundColor = ConsoleColor.Green;
          Console.WriteLine("Press Enter to Exit");
          Console.ResetColor();
          Console.Read();
          srv.Stop();
        } else {
          srv.Stop();
          Environment.ExitCode = 1;   // чтобы запустивший процесс скрипт мог определить ошибку
          Console.ForegroundColor = ConsoleColor.Magenta;
          Console.WriteLine("Enviriot start FAILED; press Enter to Exit");
          Console.ResetColor();
          Console.Read();
        }
        Console.ForegroundColor = ConsoleColor.Gray;
      } else if (flag == 1) {
        try {
          HAServer.Run(cfgPath);
        }
        catch (Exception ex) {
          Log.Error("Service host failure - {0}", ex.ToString());
          Environment.ExitCode = 1;
        }
        finally {
          Log.Finish();
        }
      } else if (flag == 2 || flag == 3) {
        if (!IsElevated()) {
          Console.ForegroundColor = ConsoleColor.Magenta;
          Console.WriteLine("{0} the {1} service requires administrator rights.", flag == 2 ? "Installing" : "Removing", HAServer.SERVICE_NAME);
          Console.WriteLine("Restart this command from an elevated console.");
          Console.ResetColor();
          Environment.ExitCode = CSWindowsServiceRecoveryProperty.Win32.ERROR_ACCESS_DENIED;
          return;
        }
        try {
          if (flag == 2) {
            HAServer.InstallService(name);
          } else {
            HAServer.UninstallService(name);
          }
        }
        catch (Exception ex) {
          Log.Error("{0}", ex.ToString());
          Environment.ExitCode = 1;  // иначе неудачная установка неотличима от успешной
        }
      }
    }
    public static bool IsLinux { get { return _isLinux; } }

    /// <summary>Возвращает true, если процесс может обращаться к SCM с правами администратора.</summary>
    private static bool IsElevated() {
      if (_isLinux) {
        return true;  // UAC отсутствует, а путь через SCM здесь всё равно неприменим
      }
      try {
        using (var id = WindowsIdentity.GetCurrent()) {
          return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
      }
      catch (Exception ex) {
        Log.Warning("IsElevated - {0}", ex.Message);
        return true;
      }
    }

    /// <summary>Длительность одного прохода цикла движка, публикуемая в /$YS/Performance.</summary>
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

    /// <summary>0, пока остановка не началась; 1 после её начала. См. Stop().</summary>
    /// <remarks>Значение сбрасывается в начале Start, а не в Stop, поэтому защёлка также делает повторный
    /// вызов Stop пустой операцией: возможность остановки вновь появляется только после нового запуска.</remarks>
    private int _stopping;

    /// <summary>Способ передачи из PrThread в Start() результата фактического запуска сервера.</summary>
    /// <remarks>
    /// <para>Между потоками передаётся только ОТВЕТ; сама работа не переносится. Очевидный вариант с выполнением
    /// InitPlugins в вызывающем потоке сделал бы Start() честным, но нарушил бы работу скриптового движка:
    /// NiL.JS связывает скомпилированную Function с контекстом, активным в потоке компиляции,
    /// а Repo.Init компилирует скрипты из server.xst. См. ActivateEngineOnThisThread.</para></remarks>
    internal const int StartupTimeoutMs = 120000;
    private readonly ManualResetEvent _startupDone = new ManualResetEvent(false);
    private volatile bool _startupOk;

    /// <summary>Время, отведённое потоку движка на завершение, включая StopPlugins.</summary>
    private const int ShutdownTimeoutMs = 20000;

    /// <summary>/$YS/Performance: публикация счётчиков процесса в дереве каждые 317 секунд.</summary>
    private bool _performance;
    private Repository.Topic _performanceT;
    private Repository.SubRec _performanceSR;

    private static readonly string _version = Assembly.GetExecutingAssembly().GetName().Version.ToString(4);
    private static readonly string _commit = GetCommit();

    /// <summary>Коммит, из которого собрана эта сборка, либо null.</summary>
    private static string GetCommit() {
      foreach (AssemblyMetadataAttribute meta in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()) {
        if (meta.Key == "Commit") {
          return meta.Value;
        }
      }
      return null;
    }

    internal Program(string cfgPath) {
      X13.Repository.Repo.configPath = cfgPath;
      Log.Info("Enviriot v.{0}, commit {1}", _version, _commit ?? "-");
    }
    internal bool Start() {
      // Сначала, отдельно от остальных сбросов ниже: Start может завершиться ошибкой раньше, чем дойдёт до них,
      // например если мьютекс уже занят или LoadPlugins выбросит исключение. Main в любом случае вызывает Stop.
      // Если оставить защёлку установленной после предыдущего запуска, Stop станет пустой операцией и не снимет
      // обработчики AppDomain, которые этот метод сейчас зарегистрирует.
      _stopping = 0;

      // Только под Windows. Под Mono именованные объекты синхронизации живут в пространстве имён ПРОЦЕССА
      // false, а не true: именованный мьютекс рекурсивен для владеющего им потока. Создание с владением,
      // а затем WaitOne увеличивали счётчик владения до двух, тогда как Stop освобождал его один раз.
      _singleInstance = _isLinux ? null : new Mutex(false, "Global\\X13.enviriot");

      AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
      AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
      if (_singleInstance != null && !_singleInstance.WaitOne(TimeSpan.Zero, true)) {
        Log.Error("only one instance at a time");
        _singleInstance = null;
        return false;
      }
      _tick = new AutoResetEvent(false);
      _terminate = false;
      _thread = new Thread(new ThreadStart(PrThread)) {
        Priority = ThreadPriority.Highest,
        Name = "MainTick",
        IsBackground = false
      };

      if (!LoadPlugins()) {
        return false;
      }

      _thread.Start();

      if (!_startupDone.WaitOne(StartupTimeoutMs)) {
        // Это не зарегистрированная ошибка, а запуск, который ещё не завершил формирование ответа.
        // Поток остаётся работать: сервер ещё может запуститься, а остановка отсюда конкурировала бы
        // с Init плагина, выполняющегося в этот момент.
        Log.Error("Server startup has not completed within {0} s; see the log for the plugin it is waiting on", StartupTimeoutMs / 1000);
        return false;
      }
      return _startupOk;
    }

    /// <summary>Отменяет действия Start и допускает вызов, даже если Start не дошёл до конца.</summary>
    /// <remarks>После освобождения дескрипторов ниже необходимы обе проверки. Start завершается раньше,
    /// если мьютекс уже занят или LoadPlugins возвращает ошибку, оставляя _thread незапущенным либо _tick равным null;
    /// при этом Main всё равно вызывает Stop перед выходом.
    /// <para>Защёлка нужна не для аккуратности, и проверки null ниже её не заменяют: два потока могут пройти каждую
    /// из них. У Stop есть второй, легко упускаемый вызывающий: Start регистрирует CurrentDomain_UnhandledException,
    /// этот обработчик вызывает Stop, а отмена регистрации находится после Join длительностью до ShutdownTimeoutMs.
    /// Именно при остановке рабочий поток плагина может аварийно завершиться из-за закрытой под ним базы данных.
    /// В результате Main уже останавливает сервер, а аварийный поток начинает вторую остановку. Без защиты второй
    /// вызов освобождает _tick и контейнер из-под потока движка, который первый вызов ещё ожидает через Join;
    /// собственный _tick.WaitOne() в PrThread не защищён от этого.</para></remarks>
    internal void Stop() {
      if (Interlocked.CompareExchange(ref _stopping, 1, 0) != 0) {
        return;
      }
      _terminate = true;
      AutoResetEvent tickEv = _tick;
      if (tickEv != null) {
        tickEv.Set();
      }
      if (_thread != null && _thread.IsAlive && !_thread.Join(ShutdownTimeoutMs)) {
        // Сообщение записывается до прерывания, поскольку Abort может попасть в любую инструкцию потока.
        // StopPlugins выполняется в этом потоке, поэтому прервана может быть операция закрытия базы данных плагином.
        // Abort сохранён только потому, что IsBackground = false: именно этот поток удерживает процесс,
        // поэтому отказ от его прерывания привёл бы к зависанию выхода.
        Log.Error("Engine thread did not stop within {0} s; aborting it", ShutdownTimeoutMs / 1000);
        _thread.Abort();
      }
      _performanceSR?.Dispose();
      _performanceSR = null;
      _tickTimer?.Dispose();
      _tickTimer = null;
      AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
      AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
      // Освобождение выполняется в try, поскольку владение мьютексом принадлежит ПОТОКУ: Start получает его
      // через WaitOne в вызывающем потоке, а ReleaseMutex из другого потока выбрасывает ApplicationException.
      // Stop не всегда выполняется в том же потоке: обработчик необработанного исключения работает в аварийном.
      // Ранее исключение отсюда попадало в общий catch обработчика, и на самом важном для журнала пути всё ниже
      // пропускалось: _tick не освобождался, контейнер плагинов не уничтожался, а Log.Finish(), который должен
      // записать сведения об аварии, не вызывался. Освобождение объекта в любом случае закрывает дескриптор;
      // ReleaseMutex является корректным дополнением, но процесс всё равно завершается.
      if (_singleInstance != null) {
        try {
          _singleInstance.ReleaseMutex();
        }
        catch (ApplicationException) {
        }
        _singleInstance.Dispose();
        _singleInstance = null;
      }
      // После завершения потока: PrThread ожидает этот объект на каждом проходе, а освобождение дескриптора,
      // на котором заблокирован поток, является именно той ошибкой, которую этот порядок предотвращает.
      _tick?.Dispose();
      _tick = null;
      // Последним и после выполнения StopPlugins в потоке движка: освобождение контейнера уничтожает
      // созданные им части, а перед этим для каждого плагина должен быть вызван собственный Stop().
      CompositionContainer container = _container;
      _container = null;
      if (container != null) {
        try {
          container.Dispose();
        }
        catch (Exception ex) {
          Log.Warning("Plugin container dispose - {0}", ex.Message);
        }
      }
      Log.Finish();
    }
    private void PrThread() {
      DateTime now = DateTime.Now, today = now.Date, performanceDT = now.AddSeconds(10), gcTick = now.AddSeconds(5);
      Tuple<bool, DateTime, double> perf_cpu = new Tuple<bool, DateTime, double>(true, now, 0);

      // Этот поток явно владеет скриптовым движком. NiL.JS хранит стек активных контекстов в поле
      // [ThreadStatic], а Function при КОМПИЛЯЦИИ захватывает Context.CurrentContext
      // (BaseLibrary/Function.cs), используя собственный DefaultGlobalContext NiL.JS при отсутствии контекста.
      // Такой переход выполняется без сообщения, но выбирает неверный глобальный контекст: setTimeout, console,
      // File и Arch определены только в нашем. Скрипт, скомпилированный в другом потоке, просто увидит их как undefined.
      // Скрипты компилируются ниже из Repo.Init -> Import(server.xst), поэтому владение нужно установить заранее.
      // Ранее результат зависел от того, какой поток первым обращался к JsExtLib.
      JsExtLib.ActivateEngineOnThisThread();

      if (!IsLinux) {
        int cpuCnt = System.Environment.ProcessorCount;
        if (cpuCnt > 1) {
          var mask = (UIntPtr)AffinityMask(cpuCnt, IntPtr.Size * 8);
          if (CSWindowsServiceRecoveryProperty.Win32.SetThreadAffinityMask(CSWindowsServiceRecoveryProperty.Win32.GetCurrentThread(), mask) == UIntPtr.Zero) {
            Log.Warning("SetThreadAffinityMask(0x{0:X}) failed - {1}", (ulong)mask, Marshal.GetLastWin32Error());
          }
        }
      }
      if (!InitPlugins() || !StartPlugins()) {
        StopPlugins();
        Log.Error("Fatal plugin startup failure, stopping server");
        // Ошибка сообщается, но решение о завершении здесь не принимается. Ранее использовался Environment.Exit(1):
        // рабочий поток определял судьбу процесса, из-за чего ветвь "start FAILED" консольного хоста была недостижима,
        // служба завершалась без уведомления SCM, а путь нельзя было покрыть тестом, поскольку runner завершался вместе с ним.
        _startupOk = false;
        _startupDone.Set();
        return;
      }

      // После StartPlugins, поскольку Topic.root появляется только после Repo.Init, и до запуска таймера тика,
      // чтобы первый проход через 10 секунд уже считывал установленное значение.
      _performanceT = Repository.Topic.root.Get("/$YS/Performance", true);
      _performanceSR = JsExtLib.EnsureCfg(Repository.Topic.root.Get("/$YS", true), "Performance",
        Repository.Topic.Attribute.DB | Repository.Topic.Attribute.Required, v => _performance = v, false);

      // Гранулярность таймера Windows равна 15,625 мс, а System.Threading.Timer планирует следующий вызов относительно callback,
      // а не по фиксированной сетке. Запрос 15 мс не дотягивает 0,625 мс до следующего интервала, поэтому любая задержка диспетчеризации
      // добавляет ЦЕЛЫЙ интервал, и период становится 31,25 мс.
      // Таймер не запускается после начала остановки. Stop() освобождает его только после Join этого потока,
      // однако пропускает Join, если поток ещё не запущен, и отказывается от ожидания при тайм-ауте и неудачном Abort.
      // Эта строка находится в конце запуска, ожидание которого Start() мог прекратить две минуты назад, поэтому оба
      // случая достижимы. Иначе остаётся бесхозный таймер, вызывающий Tick для уже освобождённого _tick.
      // Повторная проверка выполняется после присваивания: одна проверка до него всё ещё проигрывает Stop,
      // попавшему между операциями, и тогда очистку должен выполнить этот поток.
      if (!_terminate) {
        _tickTimer = new Timer(Tick, null, 100, 5);
        if (_terminate) {
          Timer orphan = _tickTimer;
          _tickTimer = null;
          orphan?.Dispose();
        }
      }
      // Теперь выполнено всё обещанное вызывающему Start(): плагины инициализированы и запущены,
      // а таймер тика активирован. Только здесь Start() прекращает ожидание.
      _startupOk = true;
      _startupDone.Set();
      int i;
      do {
        now = DateTime.Now;
        if (performanceDT < now) {
          performanceDT = now.AddSeconds(317);
          if (_performance) {
            Repository.Topic perf = _performanceT;
            perf.Get("GC").SetState(Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2));  // МБ
            using (var proc = System.Diagnostics.Process.GetCurrentProcess()) {
              perf.Get("Memory").SetState(Math.Round(proc.PrivateMemorySize64 / 1048576.0, 2));  // МБ
              var cpu = proc.TotalProcessorTime.TotalSeconds;
              if (perf_cpu.Item1) {
                perf.Get("CPU").SetState(Math.Round((cpu - perf_cpu.Item3) * 100 / (now - perf_cpu.Item2).TotalSeconds, 2));  // с
              }
              perf_cpu = new Tuple<bool, DateTime, double>(true, now, cpu);
              perf.Get("Physical").SetState(Math.Round(proc.WorkingSet64 / 1048576.0, 2));  // МБ
            }
            // Назначение цикла: один проход должен укладываться в один период.
            // Публикуются среднее и максимальное время с предыдущей публикации, а также самый длительный callback
            // скрипта — единственная часть прохода, которую пишет пользователь.
            perf.Get("Tick").SetState(Math.Round(_tickCount == 0 ? 0 : _tickTicks * 1000.0 / (_tickCount * (double)System.Diagnostics.Stopwatch.Frequency), 3));  // мс, среднее
            perf.Get("TickMax").SetState(Math.Round(_tickMax * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3));  // мс
            perf.Get("Period").SetState(Math.Round(_periodCount == 0 ? 0 : _periodTicks * 1000.0 / (_periodCount * (double)System.Diagnostics.Stopwatch.Frequency), 3));  // мс, среднее
            perf.Get("PeriodMax").SetState(Math.Round(_periodMax * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 3));  // мс
            perf.Get("Script").SetState(Math.Round(X13.JsExtLib.TakeMaxCallbackMs(), 3));  // мс
            perf.Get("Updated").SetState(X13.JsExtLib.Context.ProxyValue(now));
          } else {
            perf_cpu = new Tuple<bool, DateTime, double>(false, now, 0);
          }
          _tickTicks = 0;
          _tickMax = 0;
          _tickCount = 0;
          _periodTicks = 0;
          _periodMax = 0;
          _periodCount = 0;
        }
        if (_isLinux && gcTick < now) {
          gcTick = now.AddSeconds(887);
          GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
        }
        _tick.WaitOne();
        long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastPass != 0) {
          long period = passStart - _lastPass;
          _periodTicks += period;
          _periodCount++;
          if (period > _periodMax) {
            _periodMax = period;
          }
        }
        _lastPass = passStart;

        try {
          JsExtLib.Tick();
        }
        catch (Exception ex) {
          _faults.Report(true, "JsExtLib.Tick", null, ex);
        }
        for (i = 0; i < _modules.Length; i++) {
          long started = System.Diagnostics.Stopwatch.GetTimestamp();
          try {
            _modules[i].Tick();
          }
          catch (Exception ex) {
            // Плагин после исключения продолжает получать Tick. Его остановка превратила бы один неудачный проход
            // в навсегда отключённую подсистему.
            _faults.Report(false, _modules[i].GetType().FullName + ".Tick",
              ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms", ex);
          }
        }
        _faults.Flush(now);
        long passTicks = System.Diagnostics.Stopwatch.GetTimestamp() - passStart;
        _tickTicks += passTicks;
        _tickCount++;
        if (passTicks > _tickMax) {
          _tickMax = passTicks;
        }
        if (today != now.Date) {
          today = now.Date;
          Log.Info("{0} v.{1}", today.ToLongDateString(), _version);
        }
      } while (!_terminate);
      // null, если запуск таймера выше был пропущен. StopPlugins слишком важен, чтобы потерять его из-за
      // NullReferenceException, возникавшего ранее: он выполняется только в этом потоке.
      Timer tickTimer = _tickTimer;
      if (tickTimer != null) {
        try {
          tickTimer.Change(-1, -1);
        }
        catch (ObjectDisposedException) {
          // Stop() успел первым, что и является требуемым результатом этой строки.
        }
      }
      StopPlugins();
    }
    /// <summary>Маска привязки, выбирающая последний логический процессор.</summary>
    /// <remarks>Сдвиг должен выполняться в 64-битной арифметике: C# ограничивает величину сдвига int пятью битами,
    /// поэтому прежнее выражение `1 &lt;&lt; (cpuCnt - 1)` незаметно давало 1 при 33 процессорах и int.MinValue
    /// при 64. Индекс бита ограничивается разрядностью указателя, поскольку более широкую маску нельзя передать
    /// через UIntPtr, а в 32-битном процессе это привело бы к исключению.</remarks>
    internal static ulong AffinityMask(int cpuCnt, int pointerBits) {
      int bit = cpuCnt - 1;
      if (bit < 0) {
        bit = 0;
      }
      if (bit > pointerBits - 1) {
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
      if (args.Name == null || args.Name.Contains(".resources")) {
        return null;
      }
      string simple;
      try {
        simple = new AssemblyName(args.Name).Name;
      }
      catch (Exception) {
        simple = null;                        // имя, которое не разбирается, искать негде
      }
      if (!string.IsNullOrEmpty(simple)) {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        foreach (string ext in new[] { ".dll", ".exe" }) {
          string file = Path.Combine(dir, simple + ext);
          if (!File.Exists(file)) {
            continue;
          }
          try {
            return Assembly.LoadFrom(file);
          }
          catch (Exception ex) {
            // Файл есть, но не подошёл - чаще всего другая версия или разрядность. Это стоит
            // отличать от "файла нет вовсе", поэтому сообщение своё.
            Log.Error("AssemblyResolve({0}) from {1} - {2}", args.Name, file, ex.Message);
            return null;
          }
        }
      }
      Log.Error("AssemblyResolve failed: {0}", args.Name);
      return null;
    }

    #region Plugins
#pragma warning disable 649
    [ImportMany(typeof(IPlugModul), RequiredCreationPolicy = CreationPolicy.Shared)]
    private IEnumerable<Lazy<IPlugModul, IPlugModulData>> _impModules;
#pragma warning restore 649
    private IPlugModul[] _modules;
    private CompositionContainer _container;

    /// <summary>Этап, достигнутый сервером для конкретного плагина, начиная с решения о его запуске.</summary>
    /// <remarks>IPlugModul.enabled является только решением при запуске: значение один раз читается здесь,
    /// а последующее изменение топика не влияет ни на что до следующего запуска. Поэтому оно является первой
    /// ступенью этой последовательности, а не отдельным флагом, который пришлось бы синхронизировать.
    /// <para>Состояния с окончанием "-ing" устраняют дефект: ранее плагин регистрировался только после возврата
    /// из Start(). Если он выбрасывал исключение на середине, уже заняв порт, запустив поток или открыв базу,
    /// именно для него StopPlugins никогда не вызывался. Остановка применяется ко всем состояниям выше Enabled,
    /// то есть ко всему, для чего уже был вызван какой-либо метод.</para></remarks>
    private enum PlugState {
      Disabled = 0,   // найден MEF, но /$YS/<name> запрещает запуск
      Enabled,        // плагин включён, методы ещё не вызывались
      Initializing,   // начат Init(); ресурсы уже могут быть захвачены
      Initialized,    // Init() завершён
      Starting,       // начат Start()
      Started,        // Start() завершён
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
    /// <summary>Все обнаруженные плагины в порядке приоритета с достигнутым состоянием каждого.</summary>
    /// <remarks>Отключённые плагины сохраняются в списке, а не пропускаются: список содержит полный состав,
    /// и каждому потребителю достаточно проверить состояние без дополнительной проверки наличия.</remarks>
    private readonly List<Plug> _plugins = new List<Plug>();

    private bool LoadPlugins() {
      string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

      var catalog = new AggregateCatalog();
      catalog.Catalogs.Add(new AssemblyCatalog(Assembly.GetExecutingAssembly()));
      catalog.Catalogs.Add(new DirectoryCatalog(path));
      // Ссылка сохраняется, а не отбрасывается. Контейнер владеет каталогами и созданными общими частями,
      // то есть самими плагинами. Контейнер уничтожается в Stop после StopPlugins:
      // MEF освобождает части, реализующие IDisposable, а до этого для плагина должен быть вызван Stop().
      _container = new CompositionContainer(catalog);
      try {
        _container.ComposeParts(this);
      }
      catch (CompositionException ex) {
        Log.Error("Load plugins - {0}", ex.ToString());
        return false;
      }
      // Имя разрешает совпадение приоритетов
      _impModules = _impModules
        .OrderBy(z => z.Metadata.priority)
        .ThenBy(z => z.Metadata.name ?? string.Empty, StringComparer.Ordinal)
        .ToArray();
      return true;
    }
    /// <summary>Формирует список плагинов и инициализирует его за один проход, поскольку два прохода невозможны.</summary>
    /// <remarks>Предварительное чтение enabled у всех плагинов с последующей инициализацией выглядело бы аккуратнее,
    /// но не работает: enabled получает значение из дерева, а дерева нет до Repo.Init. Repo имеет приоритет 1
    /// и является первым элементом именно этого цикла. Поэтому enabled каждого плагина читается один раз,
    /// когда наступает его очередь и все предыдущие плагины уже инициализированы.</remarks>
    private bool InitPlugins() {
      foreach (var i in _impModules) {
        var p = new Plug(i.Value, i.Metadata.name ?? i.Value.GetType().FullName);
        _plugins.Add(p);
        if (!i.Value.enabled) {
          p.State = PlugState.Disabled;
          Log.Debug("plugin {0} disabled", p.Name);
          continue;
        }
        p.State = PlugState.Enabled;
        try {
          p.State = PlugState.Initializing;   // устанавливается до вызова, а не после
          p.Modul.Init();
          p.State = PlugState.Initialized;
          Log.Debug("plugin {0} Initialized", p.Name);
        }
        catch (Exception ex) {
          Log.Error("Init plugin {0} failure - {1}", p.Name, ex.ToString());
          return false;
        }
      }
      return true;
    }
    /// <summary>Запускает плагины, прошедшие Init, в том же порядке и без повторной проверки.</summary>
    private bool StartPlugins() {
      for (int i = 0; i < _plugins.Count; i++) {
        Plug p = _plugins[i];
        if (p.State != PlugState.Initialized) {
          continue;   // отключён; после успешного InitPlugins других состояний здесь быть не может
        }
        try {
          p.State = PlugState.Starting;
          p.Modul.Start();
          p.State = PlugState.Started;
          Log.Debug("plugin {0} Started", p.Name);
        }
        catch (Exception ex) {
          Log.Error("Start plugin {0} failure - {1}", p.Name, ex.ToString());
          return false;
        }
      }
      _modules = _plugins.Where(z => z.State == PlugState.Started).Select(z => z.Modul).ToArray();
      return true;
    }
    /// <summary>В обратном порядке отменяет каждое достигнутое состояние независимо от его завершённости.</summary>
    /// <remarks>Stop() является единственным способом завершения IPlugModul; отдельного Deinit нет. Поэтому здесь
    /// останавливается и плагин, который был инициализирован, но не запущен. Stop() может получить частично созданный
    /// объект, поэтому каждый вызов перехватывается отдельно, а достигнутое состояние указывается в сообщении.
    /// Ошибка остановки одного плагина в журнале лучше, чем порт, поток или файл базы данных, который продолжает
    /// удерживать другой плагин.</remarks>
    private void StopPlugins() {
      for (int i = _plugins.Count - 1; i >= 0; i--) {
        Plug p = _plugins[i];
        if (p.State <= PlugState.Enabled) {
          continue;   // для плагина не вызывался ни один метод
        }
        try {
          p.Modul.Stop();
        }
        catch (Exception ex) {
          Log.Error("Stop plugin {0} failure, reached {1} - {2}", p.Name, p.State, ex.ToString());
        }
        p.State = PlugState.Enabled;
      }
      _plugins.Clear();
    }
    #endregion Plugins
  }
}
