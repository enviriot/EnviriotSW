///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace X13.UI{
  /// <summary>
  /// Interaction logic for veMsStatus.xaml
  /// </summary>
  public partial class veMsStatus : UserControl, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veMsStatus(owner, manifest);
    }

    private InBase _owner;

    public veMsStatus(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      InitializeComponent();
      ValueChanged(_owner.value);
      TypeChanged(manifest);
    }
    #region IValueEditor Members
    public void ValueChanged(JSC.JSValue value) {
      int st = -1;
      string via = null;
      if(value == null || value.IsNull) {
        st = -1;
      } else if(value.IsNumber) {
        st = (int)value;
      } else if(value.ValueType == JSC.JSValueType.Object) {
        var tj = value["st"];
        if(tj.IsNumber) {
          st = (int)tj;
        }
        tj = value["via"];
        if(tj.ValueType == JSC.JSValueType.String) {
          via = tj.Value as string;
        }
      }
      switch(st) {
      case 0:
        imStatus.Source = App.GetIcon("component/Images/log_err.png");
        tbStatus.Text = "offline";
        tbVia.Text = string.Empty;
        break;
      case 1:
        imStatus.Source = App.GetIcon("component/Images/log_ok.png");
        if(string.IsNullOrEmpty(via)) {
          tbStatus.Text = "online";
          tbVia.Text = string.Empty;
        } else {
          tbStatus.Text = "online via";
          tbVia.Text = via;
        }
        break;
      case 2:
        imStatus.Source = App.GetIcon("component/Images/log_info.png");
        if(string.IsNullOrEmpty(via)) {
          tbStatus.Text = "sleep";
          tbVia.Text = string.Empty;
        } else {
          tbStatus.Text = "sleep via";
          tbVia.Text = via;
        }
        break;
      default:
        imStatus.Source = App.GetIcon("component/Images/log_deb.png");
        tbStatus.Text = "unknown";
        tbVia.Text = string.Empty;
        break;
      }
    }

    public void TypeChanged(JSC.JSValue type) {
      // nothing
    }
    #endregion IValueEditor Members
  }
}
