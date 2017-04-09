///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;

namespace X13.UI {
  internal class veByteArray : TextBox, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veByteArray(owner, manifest);
    }

    private InBase _owner;
    private string _oldValue;

    private veByteArray(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      base.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      base.BorderBrush = Brushes.Black;
      base.GotFocus += ve_GotFocus;
      base.LostFocus += ve_LostFocus;
      base.KeyUp += ve_KeyUp;
      ValueChanged(_owner.value);
      TypeChanged(manifest);
    }
    private void Publish() {
      if(!_owner.IsReadonly && _oldValue != base.Text) {
        string[] v = (base.Text).Split(new char[] { ',', ':', '-', ' ' });
        List<byte> rez = new List<byte>();
        byte tmp;
        for(int i = 0; i < v.Length; i++) {
          if(byte.TryParse(v[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out tmp)) {
            rez.Add(tmp);
          } else {
            base.Text = _oldValue;
            return;
          }
        }
        _owner.value = new ByteArray(rez.ToArray());
      }
    }
    private void ve_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) {
      if(e.Key == System.Windows.Input.Key.Enter) {
        e.Handled = true;
        Publish();
      } else if(e.Key == System.Windows.Input.Key.Escape) {
        e.Handled = true;
        base.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Previous));
        base.Text = _oldValue;
      }
    }
    private void ve_GotFocus(object sender, System.Windows.RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
    private void ve_LostFocus(object sender, System.Windows.RoutedEventArgs e) {
      Publish();
    }

    #region IValueEditor Members
    public void ValueChanged(JSC.JSValue value) {
      ByteArray ba;
      if((ba = value as ByteArray) != null || (ba = value.Value as ByteArray) != null) {
        _oldValue = BitConverter.ToString(ba.GetBytes());
      } else {
        _oldValue = value.ToString();
      }
      base.Text = _oldValue;
    }
    public void TypeChanged(JSC.JSValue type) {
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
    #endregion IValueEditor Members
  }
}
