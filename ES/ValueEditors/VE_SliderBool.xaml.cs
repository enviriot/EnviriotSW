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
  /// Interaction logic for veSliderBool.xaml
  /// </summary>
  public partial class VE_SliderBool : UserControl, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue type) {
      return new VE_SliderBool(owner, type);
    }

    private readonly InBase _owner;
    public VE_SliderBool(InBase owner, JSC.JSValue type) { //-V3117
      _owner = owner;
      InitializeComponent();
      ValueChanged(_owner.value);

      tbBoolean.Checked += CbBool_Checked;
      tbBoolean.Unchecked += CbBool_Unchecked;
    }

    public void ValueChanged(NiL.JS.Core.JSValue value) {
      this.tbBoolean.IsChecked = value.ValueType == JSC.JSValueType.Boolean && (bool)value;
    }

    public void TypeChanged(NiL.JS.Core.JSValue type) {
      tbBoolean.IsEnabled = !_owner.IsReadonly;
    }

    private void CbBool_Checked(object sender, RoutedEventArgs e) {
      _owner.value = new JSL.Boolean(true);
    }

    private void CbBool_Unchecked(object sender, RoutedEventArgs e) {
      _owner.value = new JSL.Boolean(false);
    }

    private void UserControl_GotFocus(object sender, RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
  }
}
