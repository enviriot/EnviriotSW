///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSL = NiL.JS.BaseLibrary;
using JSC = NiL.JS.Core;
using JSF = NiL.JS.Core.Functions;
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
using X13.Data;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace X13.UI {
  public partial class InspectorForm : UserControl, IBaseForm {
    private static readonly SortedList<string, Func<InBase, JSC.JSValue, IValueEditor>> _editors;
    static InspectorForm() {
      _editors = new SortedList<string, Func<InBase, JSC.JSValue, IValueEditor>> {
        ["Attribute"] = veAttribute.Create,
        ["Boolean"] = VE_SliderBool.Create,
        ["ByteArray"] = VE_ByteArray.Create,
        ["Date"] = veDateTimePicker.Create,
        ["DevicePLC"] = veDevicePLC.Create,
        ["Double"] = veDouble.Create,
        ["Editor"] = veEditor.Create,
        ["Enum"] = veEnum.Create,
        ["Integer"] = veInteger.Create,
        ["JS"] = veJS.Create,
        ["Hexadecimal"] = veHexadecimal.Create,
        ["MsStatus"] = veMsStatus.Create,
        ["String"] = veString.Create,
        ["Time"] = veTimePicker.Create,
        ["TopicReference"] = VE_TopicReference.Create,
        ["Version"] = VE_Version.Create,
      };
    }
    public static IValueEditor GetEditor(string editor, InBase owner, JSC.JSValue manifest) {
      IValueEditor rez;
      if(editor!=null && _editors.TryGetValue(editor, out Func<InBase, JSC.JSValue, IValueEditor> ct) && ct != null) {
        rez = ct(owner, manifest);
      } else {
        rez = new VE_Default(owner, manifest);
      }
      return rez;
    }
    public static IList<string> GetEditors() {
      return _editors.Keys;
    }

    private readonly DTopic _data;
    private readonly ObservableCollection<InBase> _valueVC;
    private bool _disableDrag;

    internal InspectorForm(DTopic data) {
      _valueVC = new ObservableCollection<InBase>();
      this._data = data;
      if(_data!=null) {
        CollectionChange(new InValue(_data, CollectionChange), true);
        CollectionChange(new InManifest(_data, CollectionChange), true);
        CollectionChange(new InTopic(_data, null, CollectionChange), true);
      }
      InitializeComponent();
      lvValue.ItemsSource = _valueVC;
    }
    internal void CollectionChange(InBase item, bool visible) {
      if(item == null) {
        throw new ArgumentNullException("item");
      }
      if(visible) {
        lock(_valueVC) {
          int min = 0, mid = -1, max = _valueVC.Count - 1, cr;

          while(min <= max) {
            mid = (min + max) / 2;
            cr = item.CompareTo(_valueVC[mid]);
            if(cr > 0) {
              min = mid + 1;
            } else if(cr < 0) {
              max = mid - 1;
              mid = max;
            } else {
              break;
            }
          }
          _valueVC.Insert(mid + 1, item);
        }
      } else {
        _valueVC.Remove(item);
      }
    }
    internal void RemoveItem(DTopic t) {
      InBase item = _valueVC.OfType<InTopic>().First(z => z.Owner==t);
      if(item!=null) {
        CollectionChange(item, false);
      }
    }

    private void ListViewItem_KeyUp(object sender, KeyEventArgs e) {
      var gr = e.OriginalSource as ListViewItem;
      if(gr != null) {
        var it = gr.DataContext as InBase;
        if(e.Key == Key.Apps || e.Key == Key.Space) {
          if(gr.ContextMenu != null && it != null) {
            gr.ContextMenu.ItemsSource = it.MenuItems(gr);
            gr.ContextMenu.IsOpen = true;
          }
          return;
        }
        if(e.Key == Key.Left || e.Key == Key.Right) {
          if(it != null) {
            if(e.Key == Key.Right && it.HasChildren && !it.IsExpanded) {
              it.IsExpanded = true;
              e.Handled = true;
            } else if(e.Key == Key.Left) {
              if(it.IsExpanded) {
                it.IsExpanded = false;
              } else {
                base.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Up));
              }
              e.Handled = true;
            }
          }
        }
      }
    }
    private void ListViewItem_ContextMenuOpening(object sender, ContextMenuEventArgs e) {
      FrameworkElement gr;
      InBase d;
      if((gr = sender as FrameworkElement) != null && (d = gr.DataContext as InBase) != null) {
        gr.ContextMenu.ItemsSource = d.MenuItems(gr);
        return;
      }
      e.Handled = true;
    }
    private void ListViewItem_ContextMenuClosing(object sender, ContextMenuEventArgs e) {
      var gr = sender as FrameworkElement;
      if(gr != null && gr.ContextMenu != null) {
        gr.ContextMenu.ItemsSource = null;
        gr.ContextMenu.Items.Clear();
      }
    }
    private void RecordMLBD(object sender, MouseButtonEventArgs e) {
      if(e.ClickCount==2) {
        FrameworkElement gr;
        InTopic it;
        if((gr = sender as FrameworkElement) != null && (it = gr.DataContext as InTopic) != null && it.Owner!=null && it.Owner!=_data) {
          _disableDrag = true;
          App.Workspace.Open(it.Owner.FullPath);
          e.Handled = true;
        }
      }
    }

    private void ListViewItem_MouseLeave(object sender, MouseEventArgs e) {
      ListViewItem gr;
      InTopic it;
      if(_disableDrag) {
        _disableDrag = false;
      } else if(e.LeftButton == MouseButtonState.Pressed && (gr = sender as ListViewItem) != null && (it = gr.DataContext as InTopic) != null) {
        DragDrop.DoDragDrop(gr, it.Owner, DragDropEffects.Link);
      }
    }

    private void TbItemName_Loaded(object sender, RoutedEventArgs e) {
      (sender as TextBox).SelectAll();
      (sender as TextBox).Focus();
    }
    private void TbItemName_PreviewKeyDown(object sender, KeyEventArgs e) {
      TextBox tb;
      InBase tv;
      if((tb = sender as TextBox) == null || (tv = tb.DataContext as InBase) == null) {
        return;
      }
      if(e.Key == Key.Escape) {
        tv.FinishNameEdit(null);
        e.Handled = true;
      } else if(e.Key == Key.Enter) {
        tv.FinishNameEdit(tb.Text);
        e.Handled = true;
      }
    }
    private void TbItemName_LostFocus(object sender, RoutedEventArgs e) {
      TextBox tb;
      InTopic tv;
      if((tb = sender as TextBox) == null || (tv = tb.DataContext as InTopic) == null) {
        return;
      }
      tv.FinishNameEdit(tb.Text);
      e.Handled = true;
    }

    #region IDisposable Member
    public void Dispose() {
      foreach(var vc in _valueVC.ToArray()) {
        vc.Dispose();
      }
      _valueVC.Clear();
    }
    #endregion IDisposable Member

  }
  internal class GridColumnSpringConverter : IMultiValueConverter {
    public object Convert(object[] values, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) {
      double v = values.OfType<double>().Aggregate((x, y) => x -= y) - 26;
      return v > 0 ? v : 100;
    }
    public object[] ConvertBack(object value, System.Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) {
      throw new System.NotImplementedException();
    }
  }
}
