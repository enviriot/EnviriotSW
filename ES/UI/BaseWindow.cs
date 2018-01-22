///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace X13.UI {
  public abstract class BaseWindow : UserControl, INotifyPropertyChanged {
    #region INotifyPropertyChanged Members
    public event PropertyChangedEventHandler PropertyChanged;
    protected void PropertyChangedReise([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "") {
      var pc = PropertyChanged;
      if(pc != null) {
        pc(this, new PropertyChangedEventArgs(propertyName));
      }
    }
    #endregion INotifyPropertyChanged Members
     
    private string _titel;
    public string Title {
      get {
        return _titel;
      }
      set {
        if(value != _titel) {
          _titel = value;
          PropertyChangedReise();
        }
      }
    }

    private string _contentId;
    public string ContentId {
      get {
        return _contentId;
      }
      set {
        if(value != _contentId) {
          _contentId = value;
          PropertyChangedReise();
        }
      }
    }

    private string _toolTip;
    public string ToolTipL {
      get {
        return _toolTip;
      }
      set {
        if(value != _toolTip) {
          _toolTip = value;
          PropertyChangedReise();
        }
      }
    }

    private System.Windows.Visibility _isVisible;
    public virtual System.Windows.Visibility IsVisibleL {
      get {
        return _isVisible;
      }
      set {
        if(value != _isVisible) {
          _isVisible = value;
          PropertyChangedReise();
        }
      }
    }
    /*
<Setter Property="IsSelected" Value="{Binding Model.IsSelected, Mode=TwoWay}"/>
<Setter Property="IsActive" Value="{Binding Model.IsActive, Mode=TwoWay}"/>
    */
  }
}
