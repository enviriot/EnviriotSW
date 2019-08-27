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
  /// Interaction logic for veDevicePLC.xaml
  /// </summary>
  public partial class veDevicePLC : UserControl, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      return new veDevicePLC(owner, manifest);
    }

    private InBase _owner;
    private X13.Data.DTopic _stateT, _srcT;
    private GridViewColumn _c2;
    private InspectorForm _inspForm;

    public veDevicePLC(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      _stateT = _owner.CompactView?((InTopic)_owner).Owner:((InValue)_owner).Data;
      InitializeComponent();
      _stateT.GetAsync("src").ContinueWith(SrcLoaded);
      ValueChanged(_owner.value);
      TypeChanged(manifest);
      if(_owner.CompactView) {
        buStart.Visibility = System.Windows.Visibility.Collapsed;
        buStop.Visibility = System.Windows.Visibility.Collapsed;
        buCompile.Visibility = System.Windows.Visibility.Collapsed;
        buExecute.Visibility = System.Windows.Visibility.Collapsed;
        grJsEditor.Visibility = System.Windows.Visibility.Collapsed;
      } else {
        this.textEditor.ShowLineNumbers = true;
        this.textEditor.Options.EnableHyperlinks = false;
        this.textEditor.Options.EnableEmailHyperlinks = false;
        this.textEditor.Options.EnableTextDragDrop = false;
      }
    }

    private void SrcLoaded(Task<Data.DTopic> tt) {
      if(tt.IsCompleted && !tt.IsFaulted && tt.Result!=null) {
        _srcT = tt.Result;
        _srcT.changed+=_srcT_changed;
        Dispatcher.BeginInvoke(new Action<Data.DTopic.Art, Data.DTopic>(_srcT_changed), Data.DTopic.Art.value, _srcT);
      }
    }

    private void _srcT_changed(Data.DTopic.Art a, Data.DTopic t) {
      if(a==Data.DTopic.Art.value) {
        textEditor.Text = _srcT.State.Value as string;
      }
    }

    #region IValueEditor Members
    public void ValueChanged(JSC.JSValue value) {
      int st = value.IsNumber?((int)value):0;
      if(st==1) {
        imState.Source = App.GetIcon("log_ok");
        tbState.Text = "run";
      } else if(st==2) {
        imState.Source = App.GetIcon("log_err");
        tbState.Text = "stop";
      } else {
        imState.Source = App.GetIcon("log_deb");
        tbState.Text = "unknown";
      }
    }

    public void TypeChanged(JSC.JSValue type) {
    }
    #endregion IValueEditor Members

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
      while(obj!=null) {
        obj = VisualTreeHelper.GetParent(obj);
        if((r = obj as T) != null) {
          return r;
        }
      }
      return null;
    }

    private void UserControl_GotFocus(object sender, RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }

    private void textEditor_LayoutUpdated(object sender, EventArgs e) {
      double mh = _owner.CompactView?90:(_inspForm==null?180:(_inspForm.ActualHeight - 90));
      textEditor.MaxHeight = textEditor.ExtentHeight > mh?mh:double.PositiveInfinity;
    }
    private void textEditor_LostFocus(object sender, RoutedEventArgs e) {
      if(_srcT!=null && textEditor.IsModified) {
        _srcT.SetValue(textEditor.Text);
      }
    }
    private void buCompile_Click(object sender, RoutedEventArgs e) {
      _stateT.Call("MQTT_SN.PLC.Build", _stateT.path);
    }
    private void buExecute_Click(object sender, RoutedEventArgs e) {
      _stateT.Call("MQTT_SN.PLC.Run", _stateT.path);
    }
    private void buStart_Click(object sender, RoutedEventArgs e) {
      _stateT.Call("MQTT_SN.PLC.Start", _stateT.path);
    }
    private void buStop_Click(object sender, RoutedEventArgs e) {
      _stateT.Call("MQTT_SN.PLC.Stop", _stateT.path);
    }
  }
}
