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
using X13.Data;

namespace X13.UI {
  class veEnum : ComboBox, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veEnum(owner, manifest);
    }

    private InBase _owner;
    private DTopic _enumT;
    private JSC.JSValue _oldValue;

    public veEnum(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      base.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
      base.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Right;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      base.IsEditable = false;
      base.BorderBrush = Brushes.Black;
      base.GotFocus += ve_GotFocus;
      base.SelectionChanged += ve_SelectionChanged;
      TypeChanged(manifest);
    }

    public void ValueChanged(JSC.JSValue value) {
      if(_enumT != null && _enumT.value.ValueType == JSC.JSValueType.Object) {
        base.SelectedItem = _enumT.value[value.ToString()];
        _oldValue = value;
      }
    }

    public void TypeChanged(JSC.JSValue manifest) {
      if(_enumT == null || _enumT.name != manifest["enum"].Value as string) {
        _owner.Root.GetAsync("/$YS/TYPES/Enum/" + manifest["enum"].Value as string).ContinueWith(EnumRcv, TaskScheduler.FromCurrentSynchronizationContext());
      }
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

    private void EnumRcv(Task<DTopic> td) {
      if(td.IsCompleted && !td.IsFaulted) {
        if(_enumT != null) {
          _enumT.changed -= _enumT_changed;
        }
        _enumT = td.Result;
        if(_enumT != null) {
          _enumT.changed += _enumT_changed;
        }
        _enumT_changed(DTopic.Art.value, _enumT);
      }
    }

    private void _enumT_changed(DTopic.Art a, DTopic t) {
      this.Items.Clear();
      if(_enumT != null && _enumT.value.ValueType == JSC.JSValueType.Object) {
        foreach(var kv in _enumT.value) {
          this.Items.Add(kv.Value);
        }
        ValueChanged(_owner.value);
      }
    }

    private void Publish() {
      //if(!_owner.IsReadonly && _oldValue != base.Text) {
      //  _owner.value = new JSL.String(base.Text);
      //}
    }

    private void ve_GotFocus(object sender, System.Windows.RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
    private void ve_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if(!base.IsDropDownOpen) {
        Publish();
      }
    }

  }
}
