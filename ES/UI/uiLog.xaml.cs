///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
  /// Interaction logic for uiLog.xaml
  /// </summary>
  public partial class uiLog : BaseWindow {
    private ObservableCollection<LogEntry> LogCollection;

    public uiLog() {
      LogCollection = new ObservableCollection<LogEntry>();
      Log.Write += Log_Write;
      ContentId = "file://local/?view=log";
      Title = "Output";

      InitializeComponent();

      var v = CollectionViewSource.GetDefaultView(LogCollection);
      (v as System.ComponentModel.ICollectionView).Filter = (o) => (o as LogEntry).ll != LogLevel.Debug;
      lbLog.ItemsSource = v;
    }
    public override Visibility IsVisibleL {
      get {
        return base.IsVisibleL;
      }
      set {
        base.IsVisibleL = value;
        if(value != System.Windows.Visibility.Visible) {
          App.Workspace.Close(this);
        }
      }
    }
    private void Log_Write(LogLevel ll, DateTime dt, string msg, bool local) {
      var obj = new LogEntry(dt, ll, msg, local);
      Dispatcher.BeginInvoke(new Action<LogEntry>((le) => {
        int i, len = LogCollection.Count - 1;
        for(i = len; i >= 0; i--) {
          if(LogCollection[i].dt < le.dt) {
            break;
          }
        }
        if(i == len) {
          LogCollection.Add(le);
          lbLog.ScrollIntoView(le);
        } else {
          LogCollection.Insert(i + 1, le);
        }
      }), System.Windows.Threading.DispatcherPriority.Input, new object[] { obj });
    }
    private class LogEntry {
      public LogEntry(DateTime time, LogLevel logLevel, string text, bool local = true) {
        this.dt = time;
        this.ll = logLevel;
        this.msg = text;
        this.local = local;
      }
      public DateTime dt { get; private set; }
      public LogLevel ll { get; private set; }
      public string msg { get; private set; }
      public bool local { get; private set; }
    }

    private void BaseWindow_MouseLeave(object sender, MouseEventArgs e) {
      lbLog.SelectedItem = null;
    }
    private void ToggleButton_Changed(object sender, RoutedEventArgs e) {
      if(tbShowDebug.IsChecked == true) {
        (lbLog.ItemsSource as System.ComponentModel.ICollectionView).Filter = (o) => true;
      } else {
        (lbLog.ItemsSource as System.ComponentModel.ICollectionView).Filter = (o) => (o as LogEntry).ll != LogLevel.Debug;
      }
    }
  }
}
