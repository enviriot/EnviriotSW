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
using System.Xml;
using X13.Data;
using X13.UI;
using Xceed.Wpf.AvalonDock.Layout;

namespace X13 {
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window {
    public MainWindow(string cfgPath) {
#if !DEBUG
      System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Critical;
#endif
      App.Workspace = new DWorkspace(cfgPath);
      XmlNode window;
      if(App.Workspace.config != null && (window = App.Workspace.config.SelectSingleNode("/Config/Window")) != null) {
        WindowState st;
        double tmp;
        if(window.Attributes["Top"] != null && double.TryParse(window.Attributes["Top"].Value, out tmp)) {
          this.Top = tmp;
        }
        if(window.Attributes["Left"] != null && double.TryParse(window.Attributes["Left"].Value, out tmp)) {
          this.Left = tmp;
        }
        if(window.Attributes["Width"] != null && double.TryParse(window.Attributes["Width"].Value, out tmp)) {
          this.Width = tmp;
        }
        if(window.Attributes["Height"] != null && double.TryParse(window.Attributes["Height"].Value, out tmp)) {
          this.Height = tmp;
        }
        if(window.Attributes["State"] != null && Enum.TryParse(window.Attributes["State"].Value, out st)) {
          this.WindowState = st;
        }
      }
      InitializeComponent();
      dmMain.DataContext = App.Workspace;
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) {
      try {
        XmlNode xlay;
        string lo;
        if(App.Workspace.config != null && (xlay = App.Workspace.config.SelectSingleNode("/Config/LayoutRoot")) != null) {
          lo = xlay.OuterXml;
        } else {
          lo = "<LayoutRoot><RootPanel Orientation=\"Vertical\"><LayoutPanel Orientation=\"Horizontal\"><LayoutDocumentPane /><LayoutAnchorablePaneGroup Orientation=\"Horizontal\" DockWidth=\"450\" DockHeight=\"584\"><LayoutAnchorablePane DockHeight=\"584\" ><LayoutAnchorable Title=\"Workspace\" IsSelected=\"True\" ContentId=\"file://local/?view=wks\" CanClose=\"False\" /></LayoutAnchorablePane></LayoutAnchorablePaneGroup></LayoutPanel><LayoutAnchorablePaneGroup Orientation=\"Horizontal\" DockHeight=\"225\"><LayoutAnchorablePane DockHeight=\"225\" ><LayoutAnchorable Title=\"Output\" IsSelected=\"True\" ContentId=\"file://local/?view=log\" CanClose=\"False\" /></LayoutAnchorablePane></LayoutAnchorablePaneGroup></RootPanel><TopSide /><RightSide /><LeftSide /><BottomSide /><FloatingWindows /><Hidden /></LayoutRoot>";
        }
        var layoutSerializer = new Xceed.Wpf.AvalonDock.Layout.Serialization.XmlLayoutSerializer(this.dmMain);
        layoutSerializer.LayoutSerializationCallback += LSF;
        layoutSerializer.Deserialize(new System.IO.StringReader(lo));
      }
      catch(Exception ex) {
        Log.Error("Load layout - {0}", ex.Message);
      }
      if(App.Workspace.Clients.Count == 0) {
        var cl = new Client("localhost", EsSocket.portDefault, null, null);
        App.Workspace.Clients.Add(cl);
      }
    }
    private void Window_Closed(object sender, EventArgs e) {
      var layoutSerializer = new Xceed.Wpf.AvalonDock.Layout.Serialization.XmlLayoutSerializer(this.dmMain);
      try {
        var lDoc = new XmlDocument();
        using(var ix = lDoc.CreateNavigator().AppendChild()) {
          layoutSerializer.Serialize(ix);
        }

        var cfg = new XmlDocument();
        var root = cfg.CreateElement("Config");
        var sign = cfg.CreateAttribute("Signature");
        sign.Value = "X13.ES v.0.4";
        root.Attributes.Append(sign);
        cfg.AppendChild(root);
        var window = cfg.CreateElement("Window");
        {
          var tmp = cfg.CreateAttribute("State");
          tmp.Value = this.WindowState.ToString();
          window.Attributes.Append(tmp);

          tmp = cfg.CreateAttribute("Left");
          tmp.Value = this.Left.ToString();
          window.Attributes.Append(tmp);

          tmp = cfg.CreateAttribute("Top");
          tmp.Value = this.Top.ToString();
          window.Attributes.Append(tmp);

          tmp = cfg.CreateAttribute("Width");
          tmp.Value = this.Width.ToString();
          window.Attributes.Append(tmp);

          tmp = cfg.CreateAttribute("Height");
          tmp.Value = this.Height.ToString();
          window.Attributes.Append(tmp);
        }
        root.AppendChild(window);
        root.AppendChild(cfg.ImportNode(lDoc.FirstChild, true));
        App.Workspace.Close(cfg);
      }
      catch(Exception ex) {
        Log.Error("Save config - {0}", ex.Message);
      }
      Log.Finish();
    }

    private void LSF(object sender, Xceed.Wpf.AvalonDock.Layout.Serialization.LayoutSerializationCallbackEventArgs arg) {
      if(!string.IsNullOrWhiteSpace(arg.Model.ContentId)) {
        Uri u;
        if(!Uri.TryCreate(arg.Model.ContentId, UriKind.Absolute, out u)) {
          Log.Warning("Restore Layout({0}) - Bad ContentID", arg.Model.ContentId);
          arg.Cancel = true;
          return;
        }
        string view = u.Query;
        if(view != null && view.StartsWith("?view=")) {
          view = view.Substring(6);
        } else {
          view = null;
        }
        arg.Content = App.Workspace.Open(u.GetLeftPart(UriPartial.Path), view);
      }
    }

    private void buConfig_Click(object sender, RoutedEventArgs e) {
      if(buConfig.ContextMenu != null) {
        buConfig.ContextMenu.IsOpen = !buConfig.ContextMenu.IsOpen;
      }
    }
    private void CloseButtonClick(object sender, RoutedEventArgs e) {
      SystemCommands.CloseWindow(this);
    }
    private void MinButtonClick(object sender, RoutedEventArgs e) {
      SystemCommands.MinimizeWindow(this);
    }
    private void MaxButtonClick(object sender, RoutedEventArgs e) {
      if(this.WindowState == WindowState.Maximized) {
        SystemCommands.RestoreWindow(this);
      } else {
        SystemCommands.MaximizeWindow(this);
      }
    }

    private void miCatatlog_Click(object sender, RoutedEventArgs e) {
      Client cl;
      var doc =App.Workspace.ActiveDocument as UI.UIDocument;
      if(doc == null || doc.data == null || (cl = doc.data.Connection) == null || cl.Status!=ClientState.Ready) {
        return;
      }
      App.Workspace.Open(cl.ToString(), "catatlog");
    }
    private void miOpenLog(object sender, RoutedEventArgs e) {
      App.Workspace.Open("file://local/", "log");
    }
    private void miWorkSpace_Click(object sender, RoutedEventArgs e) {
      App.Workspace.Open("file://local/", "wks");
    }
    private void miImport_Click(object sender, RoutedEventArgs e) {
      Client cl;
      var doc =App.Workspace.ActiveDocument as UI.UIDocument;
      if(doc == null || doc.data == null || (cl = doc.data.Connection) == null || cl.Status!=ClientState.Ready) {
        return;
      }

      Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
      dlg.Title = "Import";
      dlg.DefaultExt = ".xst"; // Default file extension
      dlg.Filter = "Exported storage (.xst)|*.xst"; // Filter files by extension
      dlg.CheckFileExists = true;

      if(dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.FileName) || !System.IO.File.Exists(dlg.FileName)) {
        return;
      }
      try {
        var txt = System.IO.File.ReadAllText(dlg.FileName, Encoding.UTF8);
        var body = Encoding.UTF8.GetBytes(txt);
        var payload = Convert.ToBase64String(body);
        cl.SendCmd(16, dlg.FileName, payload);
      }
      catch(Exception ex) {
        Log.Warning("Import({0}) - {1}", dlg.FileName, ex.Message);
      }
    }
    private void miExport_Click(object sender, RoutedEventArgs e) {
      DTopic t;
      var doc =App.Workspace.ActiveDocument as UI.UIDocument;
      if(doc == null || (t = doc.data) == null) {
        return;
      }
      var dlg = new Microsoft.Win32.SaveFileDialog();
      dlg.Title = "Export "+t.fullPath+" to";
      dlg.FileName = t.parent == null ? "root" : t.name;
      dlg.DefaultExt = ".xst"; // Default file extension
      dlg.Filter = "Exported storage (.xst)|*.xst"; // Filter files by extension

      if(dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.FileName)) {
        return;
      }
      t.Export(dlg.FileName);
    }

    private void dmMain_DocumentClosed(object sender, Xceed.Wpf.AvalonDock.DocumentClosedEventArgs e) {
      var doc = e.Document.Content as BaseWindow;
      if(doc != null) {
        App.Workspace.Close(doc);
      }
    }
  }
}
