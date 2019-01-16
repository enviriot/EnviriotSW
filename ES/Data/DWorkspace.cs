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
    private UIDocument _activeDocument;
    private ObservableCollection<Client> _clients;

    public XmlDocument config;
    public ObservableCollection<Client> Clients { get { return _clients; } }

    public DWorkspace(string cfgPath) {
      this._cfgPath = cfgPath;
      _clients = new ObservableCollection<Client>();
      Files = new ObservableCollection<UIDocument>();
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
              string server, userName, password;
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
                var cl = new Client(server, port, userName, password);
                tmp = xc.Attributes["Alias"];
                if(tmp != null) {
                  cl.alias = tmp.Value;
                }
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
      if(view == "log") {
        var ui = Tools.FirstOrDefault(z => z != null && z.ContentId == id);
        if(ui == null) {
          ui = new uiLog();
          Tools.Add(ui);
        }
        return ui;
      } else {
        var doc = Files.FirstOrDefault(z => z != null && ((z.data != null && z.data.fullPath == path) || z.ContentId == id));
        if(doc==null){
          doc = new UI.UIDocument(path, view);
          Files.Add(doc);
        }
        ActiveDocument = doc;
        return doc;
      }
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
      UIDocument doc= w as UIDocument;
      if(doc != null) {
        Files.Remove(doc);
        doc.Dispose();
      } else {
        Tools.Remove(w);
      }
    }

    public void Close() {
      var clx = config.CreateElement("Connections");
      XmlNode xc;
      foreach(var cl in _clients) {
        xc = config.CreateElement("Server");
        var tmp = config.CreateAttribute("URL");
        tmp.Value = cl.server;
        xc.Attributes.Append(tmp);
        if(cl.port != EsBroker.EsSocket.portDefault) {
          tmp = config.CreateAttribute("Port");
          tmp.Value = cl.port.ToString();
          xc.Attributes.Append(tmp);
        }
        if(cl.userName != null) {
          tmp = config.CreateAttribute("User");
          tmp.Value = cl.userName;
          xc.Attributes.Append(tmp);
        }
        if(cl.password != null) {
          tmp = config.CreateAttribute("Password");
          tmp.Value = cl.password;
          xc.Attributes.Append(tmp);
        }
        if(cl.alias != null) {
          tmp = config.CreateAttribute("Alias");
          tmp.Value = cl.alias;
          xc.Attributes.Append(tmp);
        }
        clx.AppendChild(xc);
        cl.Close();
      }
      config.DocumentElement.AppendChild(clx);
      config.Save(_cfgPath);
    }


    public UIDocument ActiveDocument {
      get { return _activeDocument; }
      set {
        if(_activeDocument != value) {
          _activeDocument = value;
          base.PropertyChangedReise("ActiveDocument");
        }
      }
    }
    public ObservableCollection<UIDocument> Files { get; private set; }
    public ObservableCollection<BaseWindow> Tools { get; private set; }
  }
}
