///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Controls;

namespace X13.UI {
  internal class veEditor : ComboBox, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veEditor(owner, manifest);
    }
    private InBase _owner;

    private veEditor(InBase owner, JSC.JSValue manifest) {
      this._owner = owner;
      base.ItemsSource = InspectorForm.GetEditors();
      base.IsEditable = false;
      base.BorderBrush = Brushes.Black;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      base.MinWidth = 90;
      base.GotFocus += ve_GotFocus;
      ValueChanged(_owner.value);
      TypeChanged(manifest);
      base.SelectionChanged += ve_SelectionChanged;
    }

    private void ve_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if(!base.IsDropDownOpen) {
        Publish();
      }
    }
    private void ve_GotFocus(object sender, System.Windows.RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
    private void Publish() {
      string ov = _owner.value.Value as string;
      string nv = base.SelectedValue as string;
      if(ov != nv) {
        if(!_owner.IsReadonly) {
          _owner.value = nv;
        } else {
          base.SelectedValue = ov;
        }
      }
    }

    #region IValueEditor Members
    public void ValueChanged(JSC.JSValue value) {
      if(value != null && value.ValueType == JSC.JSValueType.String && value.Value != null) {
        string ov = base.SelectedValue as string;
        string nv = value.Value as string;
        if(ov != nv) {
          base.SelectedValue = nv;
        }
      }
    }
    public void TypeChanged(JSC.JSValue type) {
      if(_owner.IsReadonly) {
        base.IsReadOnly = true;
        base.Background = Brushes.White;
        base.BorderThickness = new System.Windows.Thickness(0, 0, 0, 0);
      } else {
        base.IsReadOnly = false;
        base.Background = Brushes.Azure;
        base.BorderThickness = new System.Windows.Thickness(1, 0, 1, 0);
      }
    }
    #endregion IValueEditor Members
  }
}
