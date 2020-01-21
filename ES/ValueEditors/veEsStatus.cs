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
  class veEsStatus : TextBlock, IValueEditor {
    public static IValueEditor Create(InBase owner, JSC.JSValue manifest) {
      var ow = owner as InTopic;
      if(ow!=null) {
        return new veEsStatus(owner, manifest);
      } else {
        return new veDefault(owner, manifest);
      }
    }

    private InBase _owner;
    private  Client _client;

    private veEsStatus(InBase owner, JSC.JSValue manifest) {
      _owner = owner;
      _client = (_owner as InTopic).Owner.Connection;
      base.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      base.GotFocus += ve_GotFocus;
      _client.PropertyChanged+=_client_PropertyChanged;
      ShowStatus();
    }

    private void _client_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
      Dispatcher.BeginInvoke(new Action(ShowStatus));
    }
    private void ShowStatus() {
      switch(_client.Status) {
      case ClientState.BadAuth:
        this.Text = "Authorization error";
        this.Background = Brushes.OrangeRed;
        break;
      case ClientState.Connecting:
        this.Text = "Connecting";
        this.Background = Brushes.LightGray;
        break;
      case ClientState.Disposed:
        this.Text = "Disposed";
        this.Background = Brushes.Black;
        this.Foreground = Brushes.LightGray;
        break;
      case ClientState.Offline:
        this.Text = "Offline";
        this.Background = Brushes.LightCoral;
        break;
      case ClientState.Idle:
        this.Text = "Not connected";
        this.Background = null;
        break;
      case ClientState.Ready:
        this.Text = "Online";
        this.Background = Brushes.LightGreen;
        break;
      }
    }

    public void ValueChanged(JSC.JSValue value) {
    }

    public void TypeChanged(JSC.JSValue manifest) {
    }

    private void ve_GotFocus(object sender, System.Windows.RoutedEventArgs e) {
      _owner.GotFocus(sender, e);
    }
  }
}
