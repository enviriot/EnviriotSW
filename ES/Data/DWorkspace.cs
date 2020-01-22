///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JST = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using X13.UI;
using System.Windows.Controls;

namespace X13.Data {
  internal class DWorkspace : NPC_UI {
    private string _cfgPath;
    private BaseWindow _activeDocument;
    private ObservableCollection<Client> _clients;

    public XmlDocument config;
    public ObservableCollection<Client> Clients { get { return _clients; } }

    public DWorkspace(string cfgPath) {
      this._cfgPath = cfgPath;
      _clients = new ObservableCollection<Client>();
      Files = new ObservableCollection<BaseWindow>();
      Tools = new ObservableCollection<BaseWindow>();
      _activeDocument = null;

      try {
        if(!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(_cfgPath))) {
          System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_cfgPath));
        } else if(System.IO.File.Exists(_cfgPath)) {
          config = new XmlDocument();
          config.Load(_cfgPath);
          var sign = config.DocumentElement.Attributes["Signature"];
          if(config.FirstChild.Name != "Config" || sign == null || sign.Value != "X13.ES v.0.4") {
            config = null;
            Log.Warning("Load config({0}) - unknown format", _cfgPath);
          } else {
            XmlNode cList = config.SelectSingleNode("/Config/Connections");
            if(cList != null) {
              int i;
              XmlNode xc;
              string server, userName, password, alias;
              int port;
              var xcl = cList.SelectNodes("Server");
              for(i = 0; i < xcl.Count; i++) {
                xc = xcl[i];
                var tmp = xc.Attributes["URL"];
                if(tmp == null || string.IsNullOrEmpty(server = tmp.Value)) {
                  continue;
                }
                tmp = xc.Attributes["Port"];
                if(tmp == null || !int.TryParse(tmp.Value, out port) || port == 0) {
                  port = EsBroker.EsSocket.portDefault;
                }
                tmp = xc.Attributes["User"];
                userName = tmp != null ? tmp.Value : null;
                tmp = xc.Attributes["Password"];
                password = tmp != null ? tmp.Value : null;
                tmp = xc.Attributes["Alias"];
                alias = tmp != null?tmp.Value:null;
                var cl = new Client(server, port, userName, password, alias);
                _clients.Add(cl);
              }
            }
          }
        }
      }
      catch(Exception ex) {
        Log.Error("Load config - {0}", ex.Message);
        config = null;
      }
    }
    public BaseWindow Open(string path, string view = null) {
      string id;
      if(string.IsNullOrEmpty(path)) {
        id = null;
        path = null;
        view = null;
      } else {
        if(view != null) {
          id = path + "?view=" + view;
        } else {
          id = path;
        }
      }
      if(view == "wks") {
        var ui = Tools.FirstOrDefault(z => z != null && z.ContentId == id);
        if(ui == null) {
          ui = new uiWorkspace();
          Tools.Add(ui);
        }
        return ui;
      } else if(view == "log") {
        var ui = Tools.FirstOrDefault(z => z != null && z.ContentId == id);
        if(ui == null) {
          ui = new uiLog();
          Tools.Add(ui);
        }
        return ui;
      } else if(view == "catatlog") {
        var catalog = Files.OfType<UI.UiCatalog>().FirstOrDefault(z => z != null && z.ContentId == id);
        if(catalog == null) {
          catalog = new UI.UiCatalog(path);
          Files.Add(catalog);
        }
        ActiveDocument = catalog;
        return catalog;
      } else {
        var doc = Files.OfType<UI.UIDocument>().FirstOrDefault(z => z != null && ((z.data != null && z.data.fullPath == path) || ContentIdEqual(z.ContentId, path, view)));
        if(doc==null) {
          doc = new UI.UIDocument(path, view);
          Files.Add(doc);
        }
        ActiveDocument = doc;
        return doc;
      }
    }
    private bool ContentIdEqual(string id, string path, string view) {
      Uri u;
      if(!Uri.TryCreate(id, UriKind.Absolute, out u)) {
        return false;
      }
      string id_view = u.Query;
      if(id_view != null && id_view.StartsWith("?view=")) {
        id_view = id_view.Substring(6);
      } else {
        id_view = null;
      }
      if(u.GetLeftPart(UriPartial.Path) != path) {
        return false;
      }
      return (id_view??"IN")==(view??"IN");
    }
    public Task<DTopic> GetAsync(Uri url) {
      var up = Uri.UnescapeDataString(url.UserInfo).Split(':');
      string uName = (up.Length > 0 && !string.IsNullOrWhiteSpace(up[0])) ? up[0] : null;
      Client cl = _clients.FirstOrDefault(z => z.server == url.DnsSafeHost && z.userName == uName && z.port == (url.IsDefaultPort ? EsBroker.EsSocket.portDefault : url.Port));
      if(cl == null) {
        lock(_clients) {
          cl = _clients.FirstOrDefault(z => z.server == url.DnsSafeHost && z.userName == uName && z.port == (url.IsDefaultPort ? EsBroker.EsSocket.portDefault : url.Port));
          if(cl == null) {
            cl = new Client(url.DnsSafeHost, url.IsDefaultPort ? EsBroker.EsSocket.portDefault : url.Port, uName, up.Length == 2 ? up[1] : null);
            _clients.Add(cl);
          }
        }
      }
      return cl.root.GetAsync(url.LocalPath);
    }

    public void Close(BaseWindow w) {
      UIDocument doc;
      UiCatalog catatlog;
      if((doc = w as UIDocument) != null) {
        Files.Remove(doc);
        doc.Dispose();
      } else if((catatlog = w as UiCatalog) != null) {
        Files.Remove(catatlog);
        catatlog.Dispose();
      } else {
        Tools.Remove(w);
      }
    }

    public void Close(XmlDocument cfg) {
      XmlNode xc;
      XmlAttribute tmp;

      var clx = cfg.CreateElement("Connections");
      foreach(var cl in _clients) {
        xc = cfg.CreateElement("Server");
        tmp = cfg.CreateAttribute("URL");
        tmp.Value = cl.server;
        xc.Attributes.Append(tmp);
        if(cl.port != EsBroker.EsSocket.portDefault) {
          tmp = cfg.CreateAttribute("Port");
          tmp.Value = cl.port.ToString();
          xc.Attributes.Append(tmp);
        }
        if(cl.userName != null) {
          tmp = cfg.CreateAttribute("User");
          tmp.Value = cl.userName;
          xc.Attributes.Append(tmp);
        }
        if(cl.password != null) {
          tmp = cfg.CreateAttribute("Password");
          tmp.Value = cl.password;
          xc.Attributes.Append(tmp);
        }
        if(cl.alias != null) {
          tmp = cfg.CreateAttribute("Alias");
          tmp.Value = cl.alias;
          xc.Attributes.Append(tmp);
        }
        clx.AppendChild(xc);
        cl.Close();
      }
      cfg.DocumentElement.AppendChild(clx);

      cfg.Save(_cfgPath);
    }

    public BaseWindow ActiveDocument {
      get { return _activeDocument; }
      set {
        if(_activeDocument != value) {
          _activeDocument = value;
          base.PropertyChangedReise("ActiveDocument");
        }
      }
    }
    public ObservableCollection<BaseWindow> Files { get; private set; }
    public ObservableCollection<BaseWindow> Tools { get; private set; }

    public T ReadConfig<T>(string path, T defaultValue = default(T)) {
      if(string.IsNullOrWhiteSpace(path)) {
        throw new ArgumentNullException("path");
      }
      string attr;
      int idx = path.LastIndexOf('.');
      if(idx<0) {
        attr = path;
        path = "/Config/Common";
      } else {
        attr = path.Substring(idx+1);
        path = path.Substring(0, idx);
      }
      XmlNode xTmp;
      if(App.Workspace.config != null && (xTmp = App.Workspace.config.SelectSingleNode(path)) != null) {
        var xAttr = xTmp.Attributes[attr];
        if(xAttr != null && !string.IsNullOrWhiteSpace(xAttr.Value)) {
          return (T)Convert.ChangeType(xAttr.Value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
      }
      return defaultValue;
    }

  }
}
