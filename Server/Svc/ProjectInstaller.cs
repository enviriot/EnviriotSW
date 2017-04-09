///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration.Install;
using System.Linq;

namespace X13 {
  [RunInstaller(true)]
  public partial class ProjectInstaller : System.Configuration.Install.Installer {
    public ProjectInstaller() {
      InitializeComponent();
    }
  }
}
