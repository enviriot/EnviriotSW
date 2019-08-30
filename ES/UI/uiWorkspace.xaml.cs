///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
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
  /// Interaction logic for uiWorkspace.xaml
  /// </summary>
  public partial class uiWorkspace : BaseWindow {
    private InspectorForm _content;
    public uiWorkspace() {
      ContentId = "file://local/?view=wks";
      Title = "Workspace";
      InitializeComponent();
      _content = new InspectorForm(null);
      ccWorkspace.Content = _content;
      foreach(var cl in App.Workspace.Clients) {
        _content.CollectionChange(new InTopic(cl.root, null, _content.CollectionChange), true);
      }
      App.Workspace.Clients.CollectionChanged+=Clients_CollectionChanged;
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

    private void Clients_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
      switch(e.Action) {
      case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
        foreach(var cl in e.NewItems.OfType<Data.Client>()) {
          _content.CollectionChange(new InTopic(cl.root, null, _content.CollectionChange), true);
        }
        break;
      case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
        foreach(var cl in e.OldItems.OfType<Data.Client>()) {
          _content.RemoveItem(cl.root);
        }
        break;
      }
    }
    private void buAddConnection_Click(object sender, RoutedEventArgs e) {

      string url = Microsoft.VisualBasic.Interaction.InputBox("input server address", "Add connection");
      if(string.IsNullOrEmpty(url)){
        return;
      }
      Uri uri;
      string server, user, pass;
      int port;
      if(Uri.TryCreate(url, UriKind.Absolute, out uri)) {
        server = uri.DnsSafeHost;
        port = uri.IsDefaultPort?EsBroker.EsSocket.portDefault:uri.Port;
        if(string.IsNullOrWhiteSpace(uri.UserInfo)){
        user = null;
        pass = null;
        } else {
          var up = uri.UserInfo.Split(':');
          if(up.Length==2) {
            user = up[0];
            pass = up[1];
          } else {
            user = uri.UserInfo;
            pass = null;
          }
        }
      } else {
        server = url;
        port = EsBroker.EsSocket.portDefault;
        user = null;
        pass = null;
      }
      Data.Client cl = new Data.Client(server, port, user, pass);
      App.Workspace.Clients.Add(cl);
      App.Workspace.Open(cl.root.fullPath);
    }
  }
}
