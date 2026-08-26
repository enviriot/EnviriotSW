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
    /// <summary>The name the service is registered under in the SCM. Single source of truth.</summary>
    internal const string SERVICE_NAME = "Enviriot";

    public static void InstallService(string name) {
      string[] args_i=new string[] { name, "/LogFile=..\\log\\install.log" };
      ManagedInstallerClass.InstallHelper(args_i);
      Log.Info("The Enviriot service installed");

      List<SC_ACTION> FailureActions = new List<SC_ACTION>();

      // First Failure Actions and Delay (msec).
      FailureActions.Add(new SC_ACTION() {
        Type = (int)SC_ACTION_TYPE.RestartService,
        Delay = 1000 * 15
      });

      // Second Failure Actions and Delay (msec).
      FailureActions.Add(new SC_ACTION() {
        Type = (int)SC_ACTION_TYPE.RestartService,
        Delay = 1000 * 60 * 2
      });

      // Subsequent Failures Actions and Delay (msec).
      FailureActions.Add(new SC_ACTION() {
        Type = (int)SC_ACTION_TYPE.None,
        Delay = 1000 * 60 * 3
      });

      // Configure service recovery property.
      ServiceRecoveryProperty.ChangeRecoveryProperty(SERVICE_NAME, FailureActions, 60 * 60 * 24, "", false, "");
      Log.Info("The service recovery property is modified successfully");
      using(ServiceController svc = new ServiceController(SERVICE_NAME)) {
        svc.Start();
        try {
          svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
          Log.Info("The {0} service is running", SERVICE_NAME);
        }
        catch(System.ServiceProcess.TimeoutException) {
          // Startup imports the config and PersistentStorage copies the whole database to a
          // backup first, so a slow start is not necessarily a failed one.
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
        System.Threading.Thread.Sleep(5000);   // for mono-service 
      }

    }

    private Program _instance;
    public HAServer(string cfgPath) {
      InitializeComponent();
      _instance=new Program(cfgPath);
    }

    protected override void OnStart(string[] args) {
      _instance.Start();
    }

    protected override void OnStop() {
      _instance.Stop();
    }
  }
}
