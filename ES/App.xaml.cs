///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace X13 {
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application {
    private static SortedDictionary<string, BitmapSource> _icons;

    internal static MainWindow mainWindow { get; set; }
    internal static Data.DWorkspace Workspace { get; set; }

    internal static System.Windows.Media.Imaging.BitmapSource GetIcon(string icData) {
      BitmapSource rez;
      if(string.IsNullOrEmpty(icData)) {
        icData = string.Empty;
      }
      if(_icons.TryGetValue(icData, out rez)) {
        return rez;
      }
      lock(_icons) {
        if(!_icons.TryGetValue(icData, out rez)) {
          if(icData.StartsWith("data:image/png;base64,")) {
            var bitmapData = Convert.FromBase64String(icData.Substring(22));
            var streamBitmap = new System.IO.MemoryStream(bitmapData);
            var decoder = new PngBitmapDecoder(streamBitmap, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            rez = decoder.Frames[0];
            _icons[icData] = rez;
          } else if(icData.StartsWith("component/Images/")) {
            var url = new Uri("pack://application:,,,/ES;" + icData, UriKind.Absolute);
            var decoder = new PngBitmapDecoder(url, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            rez = decoder.Frames[0];
            _icons[icData] = rez;
          }
        }
      }
      return rez;
    }

    public App() {
      AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
      AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
      _msgs = new System.Collections.Concurrent.ConcurrentQueue<INotMsg>();
      _msgProcessFunc = new Action(ProcessMessage);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
      try {
        Log.Error("unhandled Exception {0}", e.ExceptionObject.ToString());
      }
      catch {
      }
    }
    private System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args) {
      if(args.Name != null && !args.Name.Contains(".resources") && !args.Name.StartsWith("Xceed.Wpf.AvalonDock.XmlSerializers")) {
        Log.Warning("AssemblyResolve failed: {0}", args.Name);
      }
      return null;
    }

    private void Application_Startup(object sender, StartupEventArgs e) {
      string cfgPath;
      if(e.Args.Length > 0) {
        cfgPath = e.Args[0];
      } else {
        cfgPath = @"../data/ES.cfg";
      }

      _icons = new SortedDictionary<string, BitmapSource>();

      LoadIcon(string.Empty, "ty_topic.png");
      LoadIcon("Attribute", "attr.png");
      LoadIcon("Boolean", "ty_bool.png");
      LoadIcon("ByteArray", "ty_byteArray.png");
      LoadIcon("children", "children.png");
      LoadIcon("Date", "ty_dt.png");
      LoadIcon("Double", "ty_double.png");
      LoadIcon("Editor", "ic_editor.png");
      LoadIcon("EsConnection", "ty_es.png");
      LoadIcon("Hexadecimal", "ed_hex.png");
      LoadIcon("Integer", "ty_int.png");
      LoadIcon("JS", "ty_js.png");
      LoadIcon("Null", "ty_null.png");
      LoadIcon("Object", "ty_obj.png");
      LoadIcon("String", "ty_str.png");
      LoadIcon("Time", "ed_time.png");
      LoadIcon("Version", "ty_version.png");

      mainWindow = new MainWindow(cfgPath);
      _msgProcessBusy = 1;
      mainWindow.Show();
    }
    private void LoadIcon(string name, string path) {
      var decoder = new PngBitmapDecoder(new Uri("pack://application:,,,/ES;component/Images/" + path, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
      _icons[name] = decoder.Frames[0];
    }

    #region Background worker
    private static System.Collections.Concurrent.ConcurrentQueue<INotMsg> _msgs;
    private static int _msgProcessBusy;
    private static Action _msgProcessFunc;

    internal static void PostMsg(INotMsg msg) {
      _msgs.Enqueue(msg);
      if(_msgProcessBusy == 1) {
        mainWindow.Dispatcher.BeginInvoke(_msgProcessFunc, System.Windows.Threading.DispatcherPriority.DataBind);
      }
    }
    private static void ProcessMessage() {
      INotMsg msg;
      if(System.Threading.Interlocked.CompareExchange(ref _msgProcessBusy, 2, 1) != 1) {
        return;
      }
      while(_msgs.Any()) {
        if(_msgs.TryDequeue(out msg)) {
          try {
            //Log.Debug("Tick: {0}", msg.ToString());
            msg.Process();
          }
          catch(Exception ex) {
            Log.Warning("App.ProcessMessage(0) - {1}", msg, ex.ToString());
          }
        }
      }
      _msgProcessBusy = 1;
    }
    #endregion Background worker

  }
  internal interface INotMsg {
    void Process();
    void Response(bool success, JSC.JSValue value);
    //TODO: ADD
    // bool IsRequest {get; }
    // JSL.Array Data { get; }
  }
}
