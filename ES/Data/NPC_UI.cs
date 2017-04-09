///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace X13.Data {
  public class NPC_UI : INotifyPropertyChanged {
    #region INotifyPropertyChanged Members
    public event PropertyChangedEventHandler PropertyChanged;
    protected void PropertyChangedReise([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "") {
      if(PropertyChanged != null) {
        PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
      }
    }
    #endregion INotifyPropertyChanged Members
  }
}
