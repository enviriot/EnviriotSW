///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using CSWindowsServiceRecoveryProperty;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration.Install;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;

namespace X13 {
  public partial class HAServer : ServiceBase {
    internal const string SERVICE_NAME = "Enviriot";

    public static void InstallService(string name) {
      string[] args_i=new string[] { name, "/LogFile=..\\log\\install.log" };
      ManagedInstallerClass.InstallHelper(args_i);
      Log.Info("The Enviriot service installed");

      List<SC_ACTION> FailureActions = new List<SC_ACTION>();

      // Действие и задержка после первого сбоя (мс).
      FailureActions.Add(new SC_ACTION() {
        Type = (int)SC_ACTION_TYPE.RestartService,
        Delay = 1000 * 15
      });

      // Действие и задержка после второго сбоя (мс).
      FailureActions.Add(new SC_ACTION() {
        Type = (int)SC_ACTION_TYPE.RestartService,
        Delay = 1000 * 60 * 2
      });

      // Действие и задержка после последующих сбоев (мс).
      FailureActions.Add(new SC_ACTION() {
        Type = (int)SC_ACTION_TYPE.None,
        Delay = 1000 * 60 * 3
      });

      // Настраиваем параметры восстановления службы.
      ServiceRecoveryProperty.ChangeRecoveryProperty(SERVICE_NAME, FailureActions, 60 * 60 * 24, "", false, "");
      Log.Info("The service recovery property is modified successfully");
      using(ServiceController svc = new ServiceController(SERVICE_NAME)) {
        svc.Start();
        try {
          svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
          Log.Info("The {0} service is running", SERVICE_NAME);
        }
        catch(System.ServiceProcess.TimeoutException) {
          // При запуске импортируется конфигурация, а PersistentStorage сначала копирует всю базу данных
          // в резервную копию, поэтому медленный запуск не обязательно означает ошибку.
          Log.Warning("The {0} service did not report Running within 30 s, see the log", SERVICE_NAME);
        }
      }
    }
    public static void UninstallService(string name) {
      string[] args_i=new string[] { "/u", name, "/LogFile=..\\log\\uninstall.log" };
      ManagedInstallerClass.InstallHelper(args_i);
    }
    public static void Run(string cfgPath) {
      ServiceBase[] ServicesToRun;
      ServicesToRun = new ServiceBase[] 
            { 
                new HAServer(cfgPath) 
            };
      ServiceBase.Run(ServicesToRun);
      if(Program.IsLinux) {
        System.Threading.Thread.Sleep(5000);   // для mono-service 
      }

    }

    private Program _instance;
    public HAServer(string cfgPath) {
      InitializeComponent();
      _instance=new Program(cfgPath);
    }

    protected override void OnStart(string[] args) {
      if (!Program.IsLinux) {
        RequestAdditionalTime(Program.StartupTimeoutMs + 15000);
      }
      if(!_instance.Start()) {
        Log.Error("The {0} service failed to start", SERVICE_NAME);
        ExitCode = 1;
        Stop();
      }
    }

    protected override void OnStop() {
      _instance.Stop();
    }
  }
}
