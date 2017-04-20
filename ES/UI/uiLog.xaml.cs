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
  /// Interaction logic for uiLog.xaml
  /// </summary>
  public partial class uiLog : BaseWindow {
    private Data.Client _client;

    internal uiLog(Data.Client cl) {
      this._client = cl;
      ContentId = _client.ToString() + "/?view=log";
      Title = _client.alias;
      InitializeComponent();
    }
    public override Visibility IsVisibleL {
      get {
        return base.IsVisibleL;
      }
      set {
        base.IsVisibleL = value;
        if(value!=System.Windows.Visibility.Visible) {
          App.Workspace.Close(this);
        }
      }
    }
  }
}
