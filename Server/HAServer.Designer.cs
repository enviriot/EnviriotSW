///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
namespace X13 {
  partial class HAServer {
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing) {
      if(disposing && (components != null)) {
        components.Dispose();
      }
      base.Dispose(disposing);
    }
    private void InitializeComponent() {
      this.ServiceName = SERVICE_NAME;
    }
  }
}
