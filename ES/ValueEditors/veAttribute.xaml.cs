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

namespace X13.UI {
  /// <summary>
  /// Interaction logic for veAttribute.xaml
  /// </summary>
  public partial class veAttribute : UserControl, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veAttribute(owner, manifest);
    }

    private InBase _owner;

    public veAttribute(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      InitializeComponent();
      ValueChanged(_owner.value);
      TypeChanged(manifest);
      // subscribe on events after initialize
      tbConfig.Checked += tbChanged;
      tbConfig.Unchecked += tbChanged;
      tbSaved.Checked += tbChanged;
      tbSaved.Unchecked += tbChanged;
      tbReadonly.Checked += tbChanged;
      tbReadonly.Unchecked += tbChanged;
      tbRequired.Checked += tbChanged;
      tbRequired.Unchecked += tbChanged;
      tbInternal.Checked += tbChanged;
      tbInternal.Unchecked += tbChanged;
    }

    public void ValueChanged(NiL.JS.Core.JSValue value) {
      if(value == null || !value.IsNumber) {
        tbConfig.IsChecked = false;
        tbSaved.IsChecked = false;
        tbReadonly.IsChecked = false;
        tbRequired.IsChecked = false;
        tbInternal.IsChecked = false;
      } else {
        int a = (int)value;
        tbConfig.IsChecked = ( a & 8 ) != 0;
        tbSaved.IsChecked = ( a & 12 ) == 4;
        tbReadonly.IsChecked = ( a & 2 ) != 0;
        tbRequired.IsChecked = ( a & 1 ) != 0;
        tbInternal.IsChecked = (a & 64) != 0;
      }
    }
    public void TypeChanged(NiL.JS.Core.JSValue manifest) {
      tbSaved.Visibility = _owner.levelPadding > 9 ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
      tbConfig.Visibility = _owner.levelPadding > 9 ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
      tbInternal.Visibility = _owner.levelPadding > 9 ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
      tbSaved.IsEnabled = !_owner.IsReadonly;
      tbConfig.IsEnabled = !_owner.IsReadonly;
      tbReadonly.IsEnabled = !_owner.IsReadonly;
      tbRequired.IsEnabled = !_owner.IsReadonly;
      tbInternal.IsEnabled = !_owner.IsReadonly;
    }
    private void tbChanged(object sender, RoutedEventArgs e) {
      if(!_owner.IsReadonly) {
        int ov = _owner.value.IsNumber ? (int)_owner.value : -1;
        int nv = ( tbConfig.IsChecked == true ? 8 : 0 ) + ( ( tbConfig.IsChecked != true && tbSaved.IsChecked == true ) ? 4 : 0 ) + ( tbRequired.IsChecked == true ? 1 : 0 ) + ( tbReadonly.IsChecked == true ? 2 : 0 ) + (tbInternal.IsChecked == true ? 64 : 0);
        if(nv != ov) {
          _owner.value = new JSL.Number(nv);
        }
      }
    }

    private void UserControl_GotFocus_1(object sender, RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
  }
}
