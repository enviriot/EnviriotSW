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
  /// Interaction logic for veJS.xaml
  /// </summary>
  public partial class veJS : UserControl, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veJS(owner, manifest);
    }

    private InBase _owner;
    private GridViewColumn _c2;
    private InspectorForm _inspForm;

    private veJS(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      InitializeComponent();
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      base.BorderBrush = Brushes.Black;
      ValueChanged(_owner.value);
      TypeChanged(manifest);
      this.textEditor.ShowLineNumbers = true;
      this.textEditor.Options.EnableHyperlinks = false;
      this.textEditor.Options.EnableEmailHyperlinks = false;
      this.textEditor.Options.EnableTextDragDrop = false;
    }

    public void ValueChanged(JSC.JSValue value) {
      if(value != null && value.ValueType == JSC.JSValueType.String) {
        textEditor.Text = value.Value as string;
      }
    }

    public void TypeChanged(JSC.JSValue type) {
      if(_owner.IsReadonly) {
        textEditor.IsReadOnly = true;
        base.Background = null;
        base.BorderThickness = new System.Windows.Thickness(0, 0, 0, 0);
      } else {
        textEditor.IsReadOnly = false;
        base.Background = Brushes.White;
        base.BorderThickness = new System.Windows.Thickness(1, 0, 1, 0);
      }
    }
    private void UserControl_GotFocus_1(object sender, RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }

    private void UserControl_LostFocus(object sender, RoutedEventArgs e) {
      Publish();
    }

    private void Publish() {
      if(!_owner.IsReadonly && textEditor.IsModified) {
        _owner.value = textEditor.Text;
      }
    }

    private void textEditor_LayoutUpdated(object sender, EventArgs e) {
      double mh = _owner.CompactView?90:(_inspForm==null?180:(_inspForm.ActualHeight - 90));
      textEditor.MaxHeight = textEditor.ExtentHeight > mh?mh:double.PositiveInfinity;
    }
    private void UserControl_Loaded(object sender, RoutedEventArgs e) {
      try {
        var v = FindParent<GridViewRowPresenter>(this);
        _c2 = v.Columns[1];
        _inspForm = FindParent<InspectorForm>(v);

        grJsEditor.Width = _c2.ActualWidth;
        v.LayoutUpdated+=RowPresenter_LayoutUpdated;
      }
      catch(Exception) {
      }
    }
    private void RowPresenter_LayoutUpdated(object sender, EventArgs e) {
      grJsEditor.Width = _c2.ActualWidth;
    }
    private T FindParent<T>(DependencyObject obj) where T : DependencyObject {
      T r;
      while(obj!=null){
        obj = VisualTreeHelper.GetParent(obj);
        if((r = obj as T) != null) {
          return r;
        }
      }
      return null;
    }
  }
}
