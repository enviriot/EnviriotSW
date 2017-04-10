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
    public static void InstallService(string name) {
      string[] args_i=new string[] { name, "/LogFile=..\\log\\install.log" };
      ManagedInstallerClass.InstallHelper(args_i);
      Log.Info("The service installed");

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
      ServiceRecoveryProperty.ChangeRecoveryProperty("enviriot", FailureActions, 60 * 60 * 24, "", false, "");
      Log.Info("The service recovery property is modified successfully");
      ServiceController svc =  new ServiceController("enviriot");
      svc.Start();
      svc.WaitForStatus(ServiceControllerStatus.Running, new TimeSpan(0, 0, 3));
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
      if(Programm.IsLinux) {
        System.Threading.Thread.Sleep(5000);   // for mono-service 
      }

    }

    private Programm _instance;
    public HAServer(string cfgPath) {
      InitializeComponent();
      _instance=new Programm(cfgPath);
    }

    protected override void OnStart(string[] args) {
      _instance.Start();
    }

    protected override void OnStop() {
      _instance.Stop();
    }
  }
}
