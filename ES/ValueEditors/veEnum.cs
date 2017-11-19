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
      if(!base.Items.IsEmpty) {
        if(value.ValueType == JSC.JSValueType.Double) {
          value = new JSL.Number((int)value);
        }
        _oldValue = value;
        var it = base.Items.SourceCollection.OfType<TextBlock>().FirstOrDefault(z =>  value.Equals(z.Tag as JSC.JSValue) );
        if(it == null) {
          base.SelectedIndex = 0;
        } else {
          base.SelectedItem = it;
        }
        if((it = base.SelectedItem as TextBlock) != null) {
          base.Background = it.Background;
        }
      }
    }

    public void TypeChanged(JSC.JSValue manifest) {
      if(_enumT == null || _enumT.name != manifest["enum"].Value as string) {
        _owner.Root.GetAsync("/$YS/TYPES/Enum/" + manifest["enum"].Value as string).ContinueWith(EnumRcv, TaskScheduler.FromCurrentSynchronizationContext());
      }
      if(_owner.IsReadonly) {
        //base.IsReadOnly = true;
        base.IsEnabled = false;
        base.BorderThickness = new System.Windows.Thickness(0, 0, 0, 0);
      } else {
        //base.IsReadOnly = false;
        base.IsEnabled = true;
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

      string text;
      Brush bg_b;
      Brush fg_b;


      if(_enumT != null && _enumT.State.ValueType == JSC.JSValueType.Object) {
        bool isArr = (bool)JSL.Array.isArray(new JSC.Arguments { _enumT.State });
        foreach(var kv in _enumT.State) {
          text = string.Empty;
          bg_b = null;
          fg_b = Brushes.Black;

          if(kv.Value.ValueType == JSC.JSValueType.String) {
            text = kv.Value.Value as string;
          } else if(kv.Value.ValueType == JSC.JSValueType.Object) {
            text = kv.Value["text"].Value as string;
            var bg = kv.Value["BG"];
            if(bg.ValueType == JSC.JSValueType.String) {
              try {
                var c = (Color)ColorConverter.ConvertFromString(bg.Value as string);
                bg_b = new SolidColorBrush(c);
                if(Math.Max(c.B, Math.Max(c.G, c.R)) < 64) {
                  fg_b = Brushes.White;
                }
              }
              catch(Exception) {
              }
            }
          }
          this.Items.Add(new TextBlock { Tag = isArr ? ((JSC.JSValue)new JSL.Number(int.Parse(kv.Key))) : ((JSC.JSValue)new JSL.String(kv.Key)), Text = text, Background = bg_b, Foreground = fg_b });
        }
        ValueChanged(_owner.value);
      }
    }

    private void Publish() {
      if(_owner.IsReadonly || _enumT==null || _enumT.State==null || _enumT.State.ValueType!=JSC.JSValueType.Object) {
        return;
      }
      var v = base.SelectedItem as TextBlock;
      JSC.JSValue k;
      if(v==null || (k = v.Tag as JSC.JSValue)==null) {
        ValueChanged(_owner.value);  // restore value
      } else if(_oldValue!=k) {
        _owner.value = k;
      }
    }

    private void ve_GotFocus(object sender, System.Windows.RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
    private void ve_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      Publish();
    }

  }
}
