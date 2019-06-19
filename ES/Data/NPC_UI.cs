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
      var pc = PropertyChanged;
      if(pc != null) {
        pc(this, new PropertyChangedEventArgs(propertyName));
      }
    }
    protected void SetVal<T>(ref T v, T val, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "") where T : IEquatable<T> {
      if(v==null?val!=null:!v.Equals(val)) {
        v=val;
        var pc = PropertyChanged;
        if(pc != null) {
          pc(this, new PropertyChangedEventArgs(propertyName));
        }
      }
    }
    #endregion INotifyPropertyChanged Members
  }
}
