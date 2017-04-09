using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace X13 {
  /// <summary>
  /// Interaction logic for LogView.xaml
  /// </summary>
  public partial class LogView : UserControl {
    private ObservableCollection<LogEntry> LogCollection;

    public LogView() {
      LogCollection = new ObservableCollection<LogEntry>();
      Log.Write += Log_Write;

      InitializeComponent();

      lbLog.ItemsSource = LogCollection;
    }

    private void Log_Write(LogLevel ll, DateTime dt, string msg) {
      if(ll != LogLevel.Debug) {
        var obj = new LogEntry(dt, ll, msg);
        Dispatcher.BeginInvoke(new Action<LogEntry>((le) => {
          LogCollection.Add(le);
          lbLog.ScrollIntoView(le);
        }), System.Windows.Threading.DispatcherPriority.Input, new object[] { obj });
      }
    }
    private class LogEntry {
      public LogEntry(DateTime time, LogLevel logLevel, string text) {
        this.dt = time;
        this.ll = logLevel;
        this.message = text;
      }
      public DateTime dt { get; private set; }
      public LogLevel ll { get; private set; }
      public string message { get; private set; }
    }

  }
  internal class GridColumnSpringConverter : IMultiValueConverter {
    public object Convert(object[] values, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) {
      return values.Cast<double>().Aggregate((x, y) => x -= y) - 26;
    }
    public object[] ConvertBack(object value, System.Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) {
      throw new System.NotImplementedException();
    }
  }
}
