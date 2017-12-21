///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSF = NiL.JS.Core.Functions;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using X13.Data;

namespace X13.UI {
  internal partial class LogramView : Canvas, IDisposable {
    private DTopic _model;
    private System.Threading.Timer _loadTimer;
    private int _disposed;

    private double _zoom;
    private Point ScreenStartPoint;
    private Point startOffset;
    private TransformGroup _transformGroup;
    private TranslateTransform _translateTransform;
    private ScaleTransform _zoomTransform;

    private DrawingVisual _backgroundVisual;
    private List<Visual> _visuals;
    private SortedList<uint, loItem> _map;
    private DrawingVisual _mSelectVisual;
    private loItem _selected;
    private loElement[] _mSelected;
    private bool _multipleSelection;
    private bool move;

    public LogramView() {
      _disposed = 0;
      _zoom = 1.25;
      _visuals = new List<Visual>();
      _backgroundVisual = new DrawingVisual();
      _mSelectVisual = new DrawingVisual();
      _translateTransform = new TranslateTransform();
      _zoomTransform = new ScaleTransform() { ScaleX = _zoom, ScaleY = _zoom };
      _transformGroup = new TransformGroup();

      _transformGroup.Children.Add(_zoomTransform);
      _transformGroup.Children.Add(_translateTransform);
      RenderTransform = _transformGroup;
      AddVisualChild(_backgroundVisual);
      _map = new SortedList<uint, loItem>();
      this.ContextMenu = new ContextMenu();
      this.AllowDrop = true;
      this.Drop += LogramView_Drop;
    }

    public void Attach(DTopic model) {
      this._model = model;
      _map.Clear();

      _model.changed += ModelChanged;
      ModelChanged(DTopic.Art.type, _model);

      if(_model.children != null) {
        _loadTimer = new System.Threading.Timer(LoadComplet, null, 250, -1);
        foreach(var ch in _model.children) {
          ch.GetAsync(null).ContinueWith(MChildrenLoad, TaskScheduler.FromCurrentSynchronizationContext());
        }
      }
    }

    private void LoadComplet(object state) {
      _loadTimer = null;
      this.Dispatcher.BeginInvoke(new Action(LoadComplet2));
    }
    private void LoadComplet2() {
      foreach(var p in _visuals.OfType<loBinding>().ToArray()) {
        p.Render(3);  // Draw loBinding's
      }
    }
    private void MChildrenLoad(Task<DTopic> tt) {
      DTopic t;
      if(tt.IsFaulted || !tt.IsCompleted || (t = tt.Result) == null) {
        return;
      }
      t.changed += ChildChanged;
      ChildChanged(DTopic.Art.addChild, t);
      var lt = _loadTimer;
      if(lt != null) {
        lt.Change(100, -1);
      }
    }
    private void ModelChanged(DTopic.Art a, DTopic t) {
      if(t == _model) {
        if(a == DTopic.Art.type) {
          this.Width = JsLib.OfInt(JsLib.GetField(_model.Manifest, "Logram.width"), 32 * CELL_SIZE);
          this.Height = JsLib.OfInt(JsLib.GetField(_model.Manifest, "Logram.height"), 18 * CELL_SIZE);
        }
      } else if(t.parent == _model) {
        if(a == DTopic.Art.addChild) {
          t.GetAsync(null).ContinueWith(MChildrenLoad, TaskScheduler.FromCurrentSynchronizationContext());
        } else if(a == DTopic.Art.RemoveChild) {
          foreach(var it in _visuals.OfType<loElement>().Where(z => z.GetModel() == t).ToArray()) {
            it.Dispose();
          }
        }
      }
    }
    private void ChildChanged(DTopic.Art a, DTopic t) {
      if(t.parent == _model) {
        if(JsLib.OfString(JsLib.GetField(t.Manifest, "cctor.LoBlock"), null) != null) {
          if(a == DTopic.Art.addChild) {
            var b = _visuals.OfType<loBlock>().FirstOrDefault(z => z.GetModel() == t);
            if(b == null) {
              b = new loBlock(t, this);
            }
          }
        } else {
          if(a == DTopic.Art.addChild) {
            var p = _visuals.OfType<loVariable>().FirstOrDefault(z => z.GetModel() == t);
            if(p == null) {
              p = new loVariable(t, this);
            }
          }
        }
      }
    }

    private void AddVisual(Visual item) {
      _visuals.Add(item);
      base.AddVisualChild(item);
      base.AddLogicalChild(item);
    }
    private void DeleteVisual(Visual item) {
      _visuals.Remove(item);
      base.RemoveVisualChild(item);
      base.RemoveLogicalChild(item);
    }
    private void RenderBackground() {
      using(DrawingContext dc = _backgroundVisual.RenderOpen()) {
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(-8, -8, this.Width + 8, this.Height + 8));
        Pen pen = new Pen(Brushes.LightGray, 0.5d);
        pen.DashStyle = new DashStyle(new double[] { 3, CELL_SIZE * 2 - 3 }, 1.5);
        for(double x = 0; x < this.Width - CELL_SIZE; x += CELL_SIZE) {
          dc.DrawLine(pen, new Point(x, 0), new Point(x, this.Height - CELL_SIZE));
        }
        for(double y = 0; y < this.Height - CELL_SIZE; y += CELL_SIZE) {
          dc.DrawLine(pen, new Point(0, y), new Point(this.Width - CELL_SIZE, y));
        }
      }
    }
    protected override int VisualChildrenCount {
      get {
        return _visuals.Count + (_multipleSelection ? 2 : 1);   // _backgroundVisual, _mSelectVisual
      }
    }
    protected override Visual GetVisualChild(int index) {
      if(index == 0) {
        return _backgroundVisual;
      } else if(index == _visuals.Count + 1) {
        return _mSelectVisual;
      }
      return _visuals[index - 1];
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {
      base.OnRenderSizeChanged(sizeInfo);
      if(Width > 0 && Height > 0) {
        RenderBackground();
      }
    }

    private void MapRemove(loItem val) {
      lock(_map) {
        foreach(var i in _map.Where(z => z.Value == val).ToArray()) {
          _map.Remove(i.Key);
        }
      }
    }
    /// <summary></summary>
    /// <param name="dir">0 - X+, 1 - Y-, 2 - Y+, 3 - X-</param>
    /// <param name="x">X</param>
    /// <param name="y">Y</param>
    /// <param name="val">item</param>
    private void MapSet(int dir, int x, int y, loItem val) {
      uint idx = (uint)(((y & 0x7FFF) << 17) | ((x & 0x7FFF) << 2) | (dir & 0x03));
      lock(_map) {
        if(val == null) {
          _map.Remove(idx);
        } else {
          _map[idx] = val;
        }
      }
    }
    private loItem MapGet(int dir, int x, int y) {
      uint idx = (uint)(((y & 0x7FFF) << 17) | ((x & 0x7FFF) << 2) | (dir & 0x03));
      loItem ret;
      lock(_map) {
        _map.TryGetValue(idx, out ret);
      }
      return ret;
    }
    private loItem GetVisual(double x, double y) {
      List<loItem> objs = new List<loItem>();
      GeometryHitTestParameters parameters = new GeometryHitTestParameters(new RectangleGeometry(new Rect(x - CELL_SIZE / 4, y - CELL_SIZE / 4, CELL_SIZE / 2, CELL_SIZE / 2)));
      VisualTreeHelper.HitTest(this, null, new HitTestResultCallback((hr) => {
        var rez = (GeometryHitTestResult)hr;
        var vis = hr.VisualHit as loItem;
        if(vis != null) {
          objs.Add(vis);
        }
        return HitTestResultBehavior.Continue;
      }), parameters);
      loItem ret = null;
      if(objs.Count > 0) {
        ret = objs.FirstOrDefault(z => z is loPin);
        if(ret == null) {
          ret = objs.FirstOrDefault(z => z is loBinding);
          if(ret == null) {
            ret = objs.FirstOrDefault(z => z is loElement);
          }
        }
      }
      return ret;
    }

    public loItem selected {
      get { return _selected; }
      private set {
        if(_selected != null) {
          _selected.Select(false);
        }
        _selected = value;
        if(_selected != null) {
          _selected.Select(true);
        }
        this.Focus();
      }
    }
    public DTopic Model { get { return _model; } }
    public void ResetSelection(System.Windows.Input.MouseButtonEventArgs e) {
      var p = e.GetPosition(this);
      ScreenStartPoint = new Point(Math.Min(Math.Max(p.X, 0), this.Width), Math.Min(Math.Max(p.Y, 0), this.Height));

      if(_mSelected != null && _mSelected.Length > 0) {
        foreach(var el in _mSelected) {
          el.Select(false);
        }
        _mSelected = null;
      } else if(selected != null) {
        selected = null;
      }
    }

    protected override void OnKeyUp(KeyEventArgs e) {
      if(e.Key == Key.Delete) {
        if(_mSelected != null) {
          foreach(var el in _mSelected) {
            el.Select(false);
            DeleteLI(el);
          }
          _mSelected = null;
        } else if(selected != null) {
          DeleteLI(selected);
          selected = null;
        }
        //} else if(e.Key == Key.C && Keyboard.IsKeyDown(Key.LeftCtrl)) {
        //  mi_Copy(null, null);
        //} else if(e.Key == Key.V && Keyboard.IsKeyDown(Key.LeftCtrl)) {
        //  mi_Paste(null, null);
      }
      base.OnKeyUp(e);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) {
      if(Keyboard.IsKeyDown(Key.LeftCtrl)) {
        if(e.Delta < 0 ? _zoom > 0.4 : _zoom < 2.5) {
          var p = e.GetPosition(this);
          _zoom += e.Delta / 3000.0;
          _translateTransform.X = p.X * (_zoomTransform.ScaleX - _zoom) + _translateTransform.X;
          _translateTransform.Y = p.Y * (_zoomTransform.ScaleY - _zoom) + _translateTransform.Y;
          _zoomTransform.ScaleY = _zoom;
          _zoomTransform.ScaleX = _zoom;
        }
        e.Handled = true;
      } else {
        base.OnMouseWheel(e);
      }
    }
    protected override void OnMouseDown(MouseButtonEventArgs e) {
      if(e.LeftButton == MouseButtonState.Pressed && Keyboard.IsKeyDown(Key.LeftCtrl)) {
        ScreenStartPoint = e.GetPosition((IInputElement)this.Parent);
        startOffset = new Point(_translateTransform.X, _translateTransform.Y);
        CaptureMouse();
        Cursor = Cursors.ScrollAll;
        e.Handled = true;
      } else {
        ScreenStartPoint = e.GetPosition(this);
        if(_mSelected == null) {
          var sel = GetVisual(ScreenStartPoint.X, ScreenStartPoint.Y);
          if(e.ClickCount==2 && sel==selected) {
            DTopic t;
            if(sel!=null && (t = sel.GetModel())!=null) {
              App.Workspace.Open(t.fullPath);
            }
            e.Handled = true;
          } else {
            selected = sel;
            if(selected == null) {
              base.OnMouseDown(e);
            } else {
              e.Handled = true;
            }
          }
        } else {
          e.Handled = true;
        }
      }
    }
    protected override void OnMouseMove(MouseEventArgs e) {
      var cp = e.GetPosition(this);
      if(IsMouseCaptured && Keyboard.IsKeyDown(Key.LeftCtrl)) {
        var pnt = (IInputElement)this.Parent;
        Point p = e.GetPosition(pnt);
        double toX = startOffset.X + p.X - ScreenStartPoint.X;
        double toY = startOffset.Y + p.Y - ScreenStartPoint.Y;
        _translateTransform.X = toX;
        _translateTransform.Y = toY;
      } else if(e.LeftButton == MouseButtonState.Pressed && (move || (Math.Abs(cp.X - ScreenStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(cp.Y - ScreenStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance))) {
        move = true;
        if(selected != null) {
          loElement el;
          loBinding w;
          loPin pin;
          if((el = selected as loElement) != null) {
            el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), false);
          } else if((pin = selected as loPin) != null && (pin.IsFreeInput || !pin.IsInput)) {
            w = new loBinding(selected as loPin, this);
            w.Update(ScreenStartPoint);
            selected = w;
          } else if((w = selected as loBinding) != null && (w.Input == null || w.Output == null)) {
            w.Update(cp);
          }
        } else if(_mSelected != null) {
          foreach(var el in _mSelected) {
            el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), false);
          }
        } else {
          if(!_multipleSelection) {
            _multipleSelection = true;
            base.AddVisualChild(_mSelectVisual);
          }
          using(DrawingContext dc = _mSelectVisual.RenderOpen()) {
            dc.DrawRectangle(null, SelectionPen, new Rect(ScreenStartPoint, cp));
          }
        }
      } else {
        base.OnMouseMove(e);
      }
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) {
      if(e.ChangedButton == MouseButton.Right && e.RightButton == MouseButtonState.Released) {
        DTopic cur;
        if(selected != null && (cur = selected.GetModel()) != null) {
          var cm = (this as Canvas).ContextMenu;
          cm.ItemsSource = MenuItems(cur);
          cm.IsOpen = true;
        }
        /*
        //TopicView tv;
        if(_mSelected != null) {
          var cm = (this.Parent as Grid).ContextMenu;
          cm.Items.Clear();
          MenuItem mi = new MenuItem();
          mi.Header = "Copy";
          mi.Click += new RoutedEventHandler(mi_Copy);
          cm.Items.Add(mi);
          cm.IsOpen = true;
        } else if() {
          if(selected is uiPin) {
            MenuItem mi = new MenuItem();
            mi.Header = "Trace";
            mi.Click += new RoutedEventHandler(mi_Trace);
            cm.Items.Add(mi);
          }
          if(cm.Items.Count > 0) {
            cm.IsOpen = true;
          }
        } else if(Clipboard.ContainsText()) {
          var cm = (this.Parent as Grid).ContextMenu;
          cm.Items.Clear();
          MenuItem mi = new MenuItem();
          mi.Header = "Paste";
          mi.Click += new RoutedEventHandler(mi_Paste);
          cm.Items.Add(mi);
          cm.IsOpen = true;
        }
        */
        e.Handled = true;
        return;
      } else
        if(e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Released) {
          if(_mSelected != null && _mSelected.Length > 0) {
            var cp = e.GetPosition(this);
            double r = 0, d = 0;
            foreach(var el in _mSelected) {
              if(move) {
                el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), true);
                d = Math.Max(d, el.Offset.Y + el.ContentBounds.Bottom);
                r = Math.Max(r, el.Offset.X + el.ContentBounds.Right);
              }
              el.Select(false);
            }
            _mSelected = null;
            if(move) {
              if(d + CELL_SIZE > this.Height) {
                _model.SetField("Logram.height", (int)(d + CELL_SIZE));
              }
              if(r + CELL_SIZE > this.Width) {
                _model.SetField("Logram.width", (int)(r + CELL_SIZE));
              }
            }
          }
          if(IsMouseCaptured) {
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
          } else if(selected != null) {
            loElement el;
            loBinding w;
            var cp = e.GetPosition(this);
            if((el = selected as loElement) != null && move) {
              el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), true);
              if(selected.Offset.Y + selected.ContentBounds.Bottom + CELL_SIZE > this.Height) {
                _model.SetField("Logram.height", (int)(selected.Offset.Y + selected.ContentBounds.Bottom + CELL_SIZE));
              }
              if(selected.Offset.X + selected.ContentBounds.Right + CELL_SIZE > this.Width) {
                _model.SetField("Logram.width", (int)(selected.Offset.X + selected.ContentBounds.Right + CELL_SIZE));
              }
            } else if((w = selected as loBinding) != null && (w.Output == null || w.Input == null)) {
              loPin finish = GetVisual(cp.X, cp.Y) as loPin;
              if(finish != null && ((w.Output == null && finish.GetModel() != w.Input.GetModel() && finish.IsFreeInput) || (w.Input == null && finish.GetModel() != w.Output.GetModel() && !finish.IsInput))) {
                w.SetFinish(finish);
              } else {
                this.DeleteVisual(w);
              }
            }
          } else if(_multipleSelection) {
            var cp = e.GetPosition(this);
            _multipleSelection = false;
            base.RemoveVisualChild(_mSelectVisual);
            var objs = new List<loElement>();
            GeometryHitTestParameters parameters;
            {
              double l, t, w, h;
              if(cp.X - ScreenStartPoint.X < 0) {
                l = cp.X;
                w = ScreenStartPoint.X - cp.X;
              } else {
                l = ScreenStartPoint.X;
                w = cp.X - ScreenStartPoint.X;
              }
              if(cp.Y - ScreenStartPoint.Y < 0) {
                t = cp.Y;
                h = ScreenStartPoint.Y - cp.Y;
              } else {
                t = ScreenStartPoint.Y;
                h = cp.Y - ScreenStartPoint.Y;
              }
              parameters = new GeometryHitTestParameters(new RectangleGeometry(new Rect(l, t, w, h)));
            }

            VisualTreeHelper.HitTest(this, null, new HitTestResultCallback((hr) => {
              var rez = (GeometryHitTestResult)hr;
              var vis = hr.VisualHit as loElement;
              if(vis != null && rez.IntersectionDetail == IntersectionDetail.FullyInside) {
                objs.Add(vis);
              }
              return HitTestResultBehavior.Continue;
            }), parameters);
            if(objs.Count > 0) {
              if(objs.Count == 1) {
                selected = objs[0];
              } else {
                _mSelected = objs.ToArray();
                foreach(var el in _mSelected) {
                  el.Select(true);
                }
              }
            }
          } else {
            base.OnMouseUp(e);
          }
          move = false;
        } else {
          base.OnMouseUp(e);
        }
    }

    private void LogramView_Drop(object sender, DragEventArgs e) {
      var pos = e.GetPosition(this);
      int y = (int)(pos.Y / CELL_SIZE + 0.5);
      if(y < 0) {
        y = 0;
      }
      int x = (int)(pos.X / CELL_SIZE);
      if(x < 0) {
        x = 0;
      }
      DTopic t;
      if(e.Data.GetDataPresent(typeof(DTopic)) && (t = e.Data.GetData(typeof(DTopic)) as DTopic) != null) {
        if(JsLib.OfString(JsLib.GetField(t.Manifest, "type"), null) == "Ext/LBDescr") {
          if(t.State.ValueType == JSC.JSValueType.Object && t.State.Value != null) {
            string name;
            string prefix = JsLib.OfString(t.State["namePrefix"], "U");
            if(Model.children != null) {
              int i = 1;
              do {
                name = prefix + i.ToString("D02");
                i++;
              } while(Model.children.Any(z => z.name == name));
            } else {
              name = prefix + "01";
            }
            Model.CreateAsync(name, t.State["default"], JsLib.SetField(JsLib.SetField(t.State["manifest"], "Logram.top", y), "Logram.left", x));
          }
        } else if((e.AllowedEffects & DragDropEffects.Link) == DragDropEffects.Link) {
          string name = t.name;
          if(Model.children!=null && Model.children.Any(z => z.name == name)) {
            if(t.parent == null || (name = t.parent.name + "_" + t.name) == null || Model.children.Any(z => z.name == name)) {
              int i = 1;
              do {
                name = string.Format("{0}_{1}", t.name, i);
                i++;
              } while(Model.children.Any(z => z.name == name));
            }
          }
          var m = JSC.JSObject.CreateObject();
          var ml = JSC.JSObject.CreateObject();
          ml["top"] = y;
          ml["left"] = x;
          m["Logram"] = ml;
          var mc = JSC.JSObject.CreateObject();
          mc["LoBind"] = t.path;
          m["cctor"] = mc;
          m["attr"] = 0;
          Model.CreateAsync(name, t.State, m);
          if(string.IsNullOrEmpty(JsLib.OfString(JsLib.GetField(t.Manifest, "cctor.LoBind"), null))) {
            t.SetField("cctor.LoBind", Model.path+"/"+name);
          }
        }
      }
    }

    private void DeleteLI(loItem el) {
      loBinding b = el as loBinding;
      DTopic t;

      if(b != null && (t = b.Output.GetModel()) != null) {
        t.SetField("cctor.LoBind", null);
      } else if((t = el.GetModel()) != null) {
        t.Delete();
      }
    }

    #region ContextMenu
    public ObservableCollection<Control> MenuItems(DTopic t) {
      var l = new ObservableCollection<Control>();
      JSC.JSValue v1;
      MenuItem mi;

      mi = new MenuItem() { Header = "Open in new tab", Tag = t };
      mi.Click += miOpen_Click;
      l.Add(mi);
      l.Add(new Separator());
      
      if(t.Manifest != null && (v1 = t.Manifest["Children"]).ValueType == JSC.JSValueType.Object) {
        var ad = new Dictionary<string, JSC.JSValue>();
        Jso2Acts(v1, ad);
        FillContextMenu(t, l, ad);
      } else if(t.Manifest != null && (v1 = t.Manifest["Children"]).ValueType == JSC.JSValueType.String) {
        t.GetAsync(v1.Value as string).ContinueWith(tt=>FillContextMenuFromChildren(t, l, tt), TaskScheduler.FromCurrentSynchronizationContext());
      } else {
        t.Connection.CoreTypes.GetAsync(null).ContinueWith(tt => FillContextMenuFromChildren(t, l, tt), TaskScheduler.FromCurrentSynchronizationContext());
      }
      return l;
    }
    private void Jso2Acts(JSC.JSValue obj, Dictionary<string, JSC.JSValue> act) {
      foreach(var kv in obj.Where(z => z.Value != null && z.Value.ValueType == JSC.JSValueType.Object && z.Value["default"].Defined)) {
        if(!act.ContainsKey(kv.Key)) {
          act.Add(kv.Key, kv.Value);
        }
      }
      if(obj.__proto__ != null && !obj.__proto__.IsNull && obj.Defined && obj.__proto__.ValueType == JSC.JSValueType.Object) {
        Jso2Acts(obj.__proto__, act);
      }
    }
    private async void FillContextMenuFromChildren(DTopic owner, ObservableCollection<Control> l, Task<DTopic> tt) {
      var acts = new Dictionary<string, JSC.JSValue>();
      if(tt.IsCompleted && !tt.IsFaulted && tt.Result != null) {
        foreach(var t in tt.Result.children) {
          var z = await t.GetAsync(null);
          if(z.State.ValueType == JSC.JSValueType.Object && z.State.Value != null && z.State["default"].Defined) {
            acts.Add(z.name, z.State);
          }
        }
      }
      FillContextMenu(owner, l, acts);
    }
    private void FillContextMenu(DTopic owner, ObservableCollection<Control> l, Dictionary<string, JSC.JSValue> _acts) {
      JSC.JSValue v2;
      MenuItem mi;
      MenuItem ma = new MenuItem() { Header = "Add" };

      if(_acts != null) {
        List<RcUse> resource = new List<RcUse>();
        string rName;
        JSC.JSValue tmp1;
        KeyValuePair<string, JSC.JSValue> rca;
        string rcs;
        // fill used resources
        if(owner.children != null) {
          foreach(var ch in owner.children) {
            if((tmp1 = JsLib.GetField(ch.Manifest, "MQTT-SN.tag")).ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(rName = tmp1.Value as string)) {
              rName = ch.name;
            }
            rca = _acts.FirstOrDefault(z => z.Key == rName);
            if(rca.Value == null || (tmp1 = rca.Value["rc"]).ValueType != JSC.JSValueType.String || string.IsNullOrEmpty(rcs = tmp1.Value as string)) {
              continue;
            }
            foreach(string curRC in rcs.Split(',').Where(z => !string.IsNullOrWhiteSpace(z) && z.Length > 1)) {
              int pos;
              if(!int.TryParse(curRC.Substring(1), out pos)) {
                continue;
              }
              for(int i = pos - resource.Count; i >= 0; i--) {
                resource.Add(RcUse.None);
              }
              if(curRC[0] != (char)RcUse.None && (curRC[0] != (char)RcUse.Shared || resource[pos] != RcUse.None)) {
                resource[pos] = (RcUse)curRC[0];
              }
            }
          }
        }
        // Add menuitems
        foreach(var kv in _acts) {
          if((bool)kv.Value["willful"]) {
            continue;
          }
          bool busy = false;
          if((tmp1 = kv.Value["rc"]).ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(rcs = tmp1.Value as string)) { // check used resources
            foreach(string curRC in rcs.Split(',').Where(z => !string.IsNullOrWhiteSpace(z) && z.Length > 1)) {
              int pos;
              if(!int.TryParse(curRC.Substring(1), out pos)) {
                continue;
              }
              if(pos < resource.Count && ((curRC[0] == (char)RcUse.Exclusive && resource[pos] != RcUse.None) || (curRC[0] == (char)RcUse.Shared && resource[pos] != RcUse.None && resource[pos] != RcUse.Shared))) {
                busy = true;
                break;
              }
            }
          }
          if(busy) {
            continue;
          }
          mi = new MenuItem() { Header = kv.Key.Replace("_", "__"), Tag = JsLib.SetField(kv.Value, "mi_path", owner.name+"/"+kv.Key) };
          if((v2 = kv.Value["icon"]).ValueType == JSC.JSValueType.String) {
            mi.Icon = new Image() { Source = App.GetIcon(v2.Value as string), Height = 16, Width = 16 };
          } else {
            mi.Icon = new Image() { Source = App.GetIcon(kv.Key), Height = 16, Width = 16 };
          }
          if((v2 = kv.Value["hint"]).ValueType == JSC.JSValueType.String) {
            mi.ToolTip = v2.Value;
          }
          mi.Click += miAdd_Click;
          if((v2 = kv.Value["menu"]).ValueType == JSC.JSValueType.String && kv.Value.Value != null) {
            AddSubMenu(ma, v2.Value as string, mi);
          } else {
            ma.Items.Add(mi);
          }
        }
      }
      if(ma.HasItems) {
        if(ma.Items.Count < 5) {
          foreach(var sm in ma.Items.OfType<System.Windows.Controls.Control>()) {
            l.Add(sm);
          }
        } else {
          l.Add(ma);
        }
        l.Add(new Separator());
      }
      //if((v2 = owner.Manifest["Action"]).ValueType == JSC.JSValueType.Object) {
      //  FillActions(l, v2);
      //}
      //Uri uri;
      //if(System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Text)
      //  && Uri.TryCreate(System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Text), UriKind.Absolute, out uri)
      //  && _owner.Connection.server == uri.DnsSafeHost) {
      //  mi = new MenuItem() { Header = "Paste", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Paste.png"), Width = 16, Height = 16 } };
      //  mi.Click += miPaste_Click;
      //  l.Add(mi);
      //}
      //mi = new MenuItem() { Header = "Cut", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Cut.png"), Width = 16, Height = 16 } };
      //mi.IsEnabled = !IsGroupHeader && !IsRequired;
      //mi.Click += miCut_Click;
      //l.Add(mi);
      mi = new MenuItem() { Header = "Delete", Icon = new Image() { Source = App.GetIcon("component/Images/Edit_Delete.png"), Width = 16, Height = 16 }, Tag = owner };
      mi.Click += miDelete_Click;
      mi.IsEnabled = ( owner.Manifest==null || ( JsLib.OfInt(owner.Manifest["attr"], 0) & 1 )!=1 );
      l.Add(mi);
    }
    //private void FillActions(ObservableCollection<Control> l, JSC.JSValue lst){
    //  JSC.JSValue v2;
    //  MenuItem mi;
    //  MenuItem ma = new MenuItem() { Header = "Action" };

    //  foreach(var kv in lst) {
    //    mi = new MenuItem() { Header = kv.Value["text"].Value as string, Tag = kv.Value["name"].Value as string };

    //    if((v2 = kv.Value["icon"]).ValueType == JSC.JSValueType.String) {
    //      mi.Icon = new Image() { Source = App.GetIcon(v2.Value as string), Height = 16, Width = 16 };
    //    }
    //    if((v2 = kv.Value["hint"]).ValueType == JSC.JSValueType.String) {
    //      mi.ToolTip = v2.Value;
    //    }
    //    mi.Click += miAktion_Click;
    //    ma.Items.Add(mi);
    //  }
      
    //  if(ma.HasItems) {
    //    if(ma.Items.Count < 5) {
    //      foreach(var sm in ma.Items.OfType<System.Windows.Controls.Control>()) {
    //        l.Add(sm);
    //      }
    //    } else {
    //      l.Add(ma);
    //    }
    //    l.Add(new Separator());
    //  }

    //}
    private void AddSubMenu(MenuItem ma, string prefix, MenuItem mi) {
      MenuItem mm = ma, mn;
      string[] lvls = prefix.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
      for(int j = 0; j < lvls.Length; j++) {
        mn = mm.Items.OfType<MenuItem>().FirstOrDefault(z => z.Header as string == lvls[j]);
        if(mn == null) {
          mn = new MenuItem();
          mn.Header = lvls[j];
          mm.Items.Add(mn);
        }
        mm = mn;
      }
      mm.Items.Add(mi);
    }
    
    private void miOpen_Click(object sender, System.Windows.RoutedEventArgs e) {
      DTopic t;
      var mi = sender as MenuItem;
      if(mi == null || (t = mi.Tag as DTopic)==null) {
        return;
      }

      App.Workspace.Open(t.fullPath);
    }
    
    private void miAdd_Click(object sender, System.Windows.RoutedEventArgs e) {
      var mi = sender as MenuItem;
      if(mi == null) {
        return;
      }
      var tag = mi.Tag as JSC.JSValue;
      if(tag != null) {
        _model.CreateAsync(tag["mi_path"].Value as string, tag["default"], tag["manifest"]);
      }
    }
    //private void miAktion_Click(object sender, System.Windows.RoutedEventArgs e) {
    //  var mi = sender as MenuItem;
    //  if(mi == null) {
    //    return;
    //  }
    //  var tag = mi.Tag as string;
    //  if(tag != null) {
    //    _owner.Call(tag, _owner.path);
    //  }
    //}
    //private void miCut_Click(object sender, System.Windows.RoutedEventArgs e) {
    //  System.Windows.Clipboard.SetText(_owner.fullPath, System.Windows.TextDataFormat.Text);
    //}
    //private void miPaste_Click(object sender, System.Windows.RoutedEventArgs e) {
    //  Uri uri;
    //  if(System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Text)
    //    && Uri.TryCreate(System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Text), UriKind.Absolute, out uri)
    //    && _owner.Connection.server==uri.DnsSafeHost) {
    //      System.Windows.Clipboard.Clear();
    //      App.Workspace.GetAsync(uri).ContinueWith(td => {
    //        if(td.IsCompleted && !td.IsFaulted && td.Result != null) {
    //          td.Result.Move(_owner, td.Result.name);
    //        }
    //      }, TaskScheduler.FromCurrentSynchronizationContext());
    //  }
    //}

    private void miDelete_Click(object sender, System.Windows.RoutedEventArgs e) {
      DTopic t;
      var mi = sender as MenuItem;
      if(mi == null || (t = mi.Tag as DTopic) == null) {
        return;
      }
      t.Delete();
    }

    private void IconFromTypeLoaded(Task<DTopic> td, object o) {
      var img = o as Image;
      if(img != null && td.IsCompleted && td.Result != null) {
        //img.Source = App.GetIcon(td.Result.GetField<string>("icon"));
      }
    }
    private enum RcUse : ushort {
      None = '0',
      Baned = 'B',
      Shared = 'S',
      Exclusive = 'X',
    }
    
    #endregion ContextMenu  
  
    #region IDisposable Member
    public void Dispose() {
      if(System.Threading.Interlocked.Exchange(ref this._disposed, 1)==0) {
        var lt = System.Threading.Interlocked.Exchange(ref _loadTimer, null);
        if(lt != null) {
          lt.Change(-1, -1);
        }

        _model.changed -= ModelChanged;
        DTopic t;
        foreach(var it in _visuals.OfType<loItem>().ToArray()) {
          if((t = it.GetModel()) != null) {
            t.changed -= ChildChanged;
          }
          it.Dispose();
        }
        _disposed = 2;
      }
    }
    #endregion IDisposable Member
  }
}
