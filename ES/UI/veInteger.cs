///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace X13.UI {
  internal class veInteger: Xceed.Wpf.Toolkit.LongUpDown, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue type) {
      return new veInteger(owner, type);
    }

    private InBase _owner;
    private long _oldValue;

    public veInteger(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      base.TabIndex = 5;
      base.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      base.BorderBrush = Brushes.Black;
      base.GotFocus += ve_GotFocus;
      base.LostFocus += ve_LostFocus;
      base.KeyUp += ve_KeyUp;
      ValueChanged(_owner.value);
      TypeChanged(manifest);
    }
    public new void ValueChanged(JSC.JSValue value) {
      try {
        _oldValue = (long)value;
        base.Value = _oldValue;
      }
      catch(Exception) {
        base.Value = null;
      }
    }
    public void TypeChanged(JSC.JSValue manifest) {
      if(_owner.IsReadonly) {
        base.IsReadOnly = true;
        base.Background = null;
        base.BorderThickness = new System.Windows.Thickness(0, 0, 0, 0);
      } else {
        base.IsReadOnly = false;
        base.Background = Brushes.White;
        base.BorderThickness = new System.Windows.Thickness(1, 0, 1, 0);
      }
    }

    protected override void OnDecrement() {
      base.OnDecrement();
      Publish();
    }
    protected override void OnIncrement() {
      base.OnIncrement();
      Publish();
    }
    private void Publish() {
      if(!_owner.IsReadonly) {
        if(base.Value.HasValue) {
          if(_oldValue != base.Value.Value) {
            _owner.value = new JSL.Number(base.Value.Value);
          }
        } else {
          _owner.value = JSC.JSValue.Null;
        }
      }
    }
    private void ve_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) {
      if(e.Key == System.Windows.Input.Key.Enter) {
        e.Handled = true;
        Publish();
      } else if(e.Key == System.Windows.Input.Key.Escape) {
        base.Value = _oldValue;
        base.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Previous));
      }
    }
    private void ve_GotFocus(object sender, System.Windows.RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
    private void ve_LostFocus(object sender, System.Windows.RoutedEventArgs e) {
      Publish();
    }
  }
}
