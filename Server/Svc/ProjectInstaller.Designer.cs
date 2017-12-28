///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
namespace X13 {
  partial class ProjectInstaller {
    /// <summary>
    /// Erforderliche Designervariable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary> 
    /// Verwendete Ressourcen bereinigen.
    /// </summary>
    /// <param name="disposing">True, wenn verwaltete Ressourcen gelöscht werden sollen; andernfalls False.</param>
    protected override void Dispose(bool disposing) {
      if(disposing && (components != null)) {
        components.Dispose();
      }
      base.Dispose(disposing);
    }

    #region Vom Komponenten-Designer generierter Code

    /// <summary>
    /// Erforderliche Methode für die Designerunterstützung.
    /// Der Inhalt der Methode darf nicht mit dem Code-Editor geändert werden.
    /// </summary>
    private void InitializeComponent() {
      this.serviceProcessInstaller1 = new System.ServiceProcess.ServiceProcessInstaller();
      this.serviceInstaller1 = new System.ServiceProcess.ServiceInstaller();
      // 
      // serviceProcessInstaller1
      // 
      this.serviceProcessInstaller1.Account = System.ServiceProcess.ServiceAccount.NetworkService;
      this.serviceProcessInstaller1.Password = null;
      this.serviceProcessInstaller1.Username = null;
      // 
      // serviceInstaller1
      // 
      this.serviceInstaller1.Description = "Provides network communications and Logramm processing";
      this.serviceInstaller1.DisplayName = "Enviriot service";
      this.serviceInstaller1.ServiceName = "Enviriot";
      this.serviceInstaller1.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
      // 
      // ProjectInstaller
      // 
      this.Installers.AddRange(new System.Configuration.Install.Installer[] {
            this.serviceProcessInstaller1,
            this.serviceInstaller1});

    }

    #endregion

    private System.ServiceProcess.ServiceProcessInstaller serviceProcessInstaller1;
    private System.ServiceProcess.ServiceInstaller serviceInstaller1;
  }
}