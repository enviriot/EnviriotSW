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
  internal partial class LogramView : Canvas {
    #region Settings
    public const int CELL_SIZE = 16;
    public static readonly Typeface LFont = new Typeface("Times New Roman");
    public static readonly Pen SelectionPen = new Pen(Brushes.Orange, 1);
    #endregion Settings

    private DTopic _model;

    private double _zoom;
    private Point ScreenStartPoint;
    private Point startOffset;
    private TransformGroup _transformGroup;
    private TranslateTransform _translateTransform;
    private ScaleTransform _zoomTransform;

    private SortedList<uint, uiItem> _map;

    private DrawingVisual _backgroundVisual;
    public List<Visual> _visuals;
    //private bool move;

    public LogramView() {
      _zoom = 1.25;
      _visuals = new List<Visual>();
      _backgroundVisual = new DrawingVisual();
      _translateTransform = new TranslateTransform();
      _zoomTransform = new ScaleTransform() { ScaleX = _zoom, ScaleY = _zoom };
      _transformGroup = new TransformGroup();

      _transformGroup.Children.Add(_zoomTransform);
      _transformGroup.Children.Add(_translateTransform);
      RenderTransform = _transformGroup;
      AddVisualChild(_backgroundVisual);
      _map = new SortedList<uint, uiItem>();

    }

    public void Attach(DTopic model) {
      this._model = model;
      _map.Clear();

      _model.changed += ModelChanged;
      ModelChanged(DTopic.Art.type, _model);

      if(_model.children != null) {
        foreach(var ch in _model.children) {
          ch.GetAsync(null).ContinueWith(MChildrenLoad, TaskScheduler.FromCurrentSynchronizationContext());
        }
      }
    }
    private void MChildrenLoad(Task<DTopic> tt) {
      DTopic t;
      if(tt.IsFaulted || !tt.IsCompleted || (t = tt.Result) == null) {
        return;
      }
      t.changed+=ChildChanged;
      ChildChanged(DTopic.Art.addChild, t);
    }
    private void ModelChanged(DTopic.Art a, DTopic t) {
      if(t == _model) {
        if(a == DTopic.Art.type) {
          this.Width = JsLib.OfInt(JsLib.GetField(_model.Manifest, "Logram.width"), 32 * CELL_SIZE);
          this.Height = JsLib.OfInt(JsLib.GetField(_model.Manifest, "Logram.height"), 18 * CELL_SIZE);
        }
      } else if(t.parent == _model) {
        if(JsLib.OfString(JsLib.GetField(t.Manifest, "cctor.LoBlock"), null) != null) {
          // LoBlock
        } else {
          if(a == DTopic.Art.addChild) {
            t.GetAsync(null).ContinueWith(MChildrenLoad, TaskScheduler.FromCurrentSynchronizationContext());
          }
        }
      }
    }
    private void ChildChanged(DTopic.Art a, DTopic t) {
      if(t.parent == _model) {
        if(JsLib.OfString(JsLib.GetField(t.Manifest, "cctor.LoBlock"), null) != null) {
          // LoBlock
        } else {
          if(a == DTopic.Art.addChild) {
            var p = new uiAlias(t, this);
          }
        }
      }
    }

    public void AddVisual(Visual item) {
      _visuals.Add(item);
      base.AddVisualChild(item);
      base.AddLogicalChild(item);
    }
    public void DeleteVisual(Visual item) {
      _visuals.Remove(item);
      base.RemoveVisualChild(item);
      base.RemoveLogicalChild(item);
    }
    private void RenderBackground() {
      using(DrawingContext dc = _backgroundVisual.RenderOpen()) {
        Pen pen = new Pen(Brushes.LightGray, 0.5d);
        pen.DashStyle = new DashStyle(new double[] { 3, CELL_SIZE * 2 - 3 }, 1.5);
        for(double x = CELL_SIZE; x < this.Width; x += CELL_SIZE) {
          dc.DrawLine(pen, new Point(x, 0), new Point(x, this.Height));
        }
        for(double y = CELL_SIZE; y < this.Height; y += CELL_SIZE) {
          dc.DrawLine(pen, new Point(0, y), new Point(this.Width, y));
        }
      }
    }
    protected override int VisualChildrenCount {
      get {
        return _visuals.Count + 1;   // _backgroundVisual, _mSelectVisual
      }
    }
    protected override Visual GetVisualChild(int index) {
      if(index == 0) {
        return _backgroundVisual;
        //} else if(index == _visuals.Count + 1) {
        //  return _mSelectVisual;
      }
      return _visuals[index - 1];
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {
      base.OnRenderSizeChanged(sizeInfo);
      if(Width > 0 && Height > 0) {
        RenderBackground();
      }
    }

    private void MapRemove(uiItem val) {
      lock(_map) {
        foreach(var i in _map.Where(z => z.Value == val).ToArray()) {
          _map.Remove(i.Key);
          //Log.Debug("MapRemove({0}, {1}, {2}) = {3}", (i.Key&1)!=0?"V":"H", (i.Key>>1) & 0xFFFF, (i.Key>>17) & 0x7FFF, i.Value);
        }
      }
    }
    private void MapSet(bool vert, int x, int y, uiItem val) {
      uint idx = (uint)(((y & 0x7FFF) << 17) | ((x & 0xFFFF) << 1) | (vert ? 1 : 0));
      lock(_map) {
        if(val == null) {
          _map.Remove(idx);
        } else {
          _map[idx] = val;
        }
      }
      //RenderBackground();
      //Log.Debug("MapSet({0}, {1}, {2}) = {3}", vert?"V":"H", x, y, val);
    }
    private uiItem MapGet(bool vert, int x, int y) {
      uint idx = (uint)(((y & 0x7FFF) << 17) | ((x & 0xFFFF) << 1) | (vert ? 1 : 0));
      uiItem ret;
      lock(_map) {
        _map.TryGetValue(idx, out ret);
      }
      return ret;
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
        //if(_mSelected == null) {
        //  selected = GetVisual(ScreenStartPoint.X, ScreenStartPoint.Y);
        //  if(selected == null) {
        //    base.OnMouseDown(e);
        //  }
        //}
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
        //} else if(e.LeftButton == MouseButtonState.Pressed && (move || (Math.Abs(cp.X - ScreenStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(cp.Y - ScreenStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance))) {
        //  move = true;
        //  if(selected != null) {
        //    SchemaElement el;
        //    loBinding w;
        //    uiPin pin;
        //    if((el = selected as SchemaElement) != null) {
        //      el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), false);
        //    } else if((pin = selected as uiPin) != null) {
        //      w = new loBinding(selected as uiPin, this);
        //      w.Update(ScreenStartPoint);
        //      selected = w;
        //    } else if((w = selected as loBinding) != null && w.B == null) {
        //      w.Update(cp);
        //    }
        //  } else if(_mSelected != null) {
        //    foreach(var el in _mSelected.Where(z => !(z is uiTracer))) {
        //      el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), false);
        //    }
        //  } else {
        //    if(!_multipleSelection) {
        //      _multipleSelection = true;
        //      base.AddVisualChild(_mSelectVisual);
        //    }
        //    using(DrawingContext dc = _mSelectVisual.RenderOpen()) {
        //      dc.DrawRectangle(null, Schema.SelectionPen, new Rect(ScreenStartPoint, cp));
        //    }
        //  }
      } else {
        base.OnMouseMove(e);
      }
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) {
      //if(e.ChangedButton == MouseButton.Right && e.RightButton == MouseButtonState.Released) {
      //  Topic cur;
      //  TopicView tv;
      //  if(_mSelected != null) {
      //    var cm = (this.Parent as Grid).ContextMenu;
      //    cm.Items.Clear();
      //    MenuItem mi = new MenuItem();
      //    mi.Header = "Copy";
      //    mi.Click += new RoutedEventHandler(mi_Copy);
      //    cm.Items.Add(mi);
      //    cm.IsOpen = true;
      //  } else if(selected != null && (cur = selected.GetModel()) != null && (tv = TopicView.root.Get(cur, true)) != null) {
      //    var cm = (this.Parent as Grid).ContextMenu;
      //    var actions = tv.GetActions();
      //    cm.Items.Clear();

      //    ItemCollection items;
      //    for(int i = 0; i < actions.Count; i++) {
      //      switch(actions[i].action) {
      //      case ItemAction.addToLogram:
      //      case ItemAction.createBoolMask:
      //      case ItemAction.createDoubleMask:
      //      case ItemAction.createLongMask:
      //      case ItemAction.createNodeMask:
      //      case ItemAction.createStringMask:
      //      case ItemAction.createByteArrMask:
      //      case ItemAction.open:
      //        continue;
      //      }
      //      items = cm.Items;
      //      string[] lvls = actions[i].menuItem.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
      //      for(int j = 0; j < lvls.Length; j++) {
      //        MenuItem mi = DataStorageView.FindMenuItem(items, lvls[j]);
      //        if(mi == null) {
      //          mi = new MenuItem();
      //          mi.Header = lvls[j];
      //          mi.DataContext = tv;
      //          items.Add(mi);
      //        }

      //        if(j == lvls.Length - 1) {
      //          mi.Tag = actions[i];
      //          mi.Click += new RoutedEventHandler(mi_Click);
      //          mi.ToolTip = actions[i].description;
      //        }
      //        items = mi.Items;
      //      }
      //    }
      //    if(cur.valueType == typeof(PiStatement)) {
      //      MenuItem mi = new MenuItem();
      //      mi.Header = "Copy";
      //      mi.Click += new RoutedEventHandler(mi_Copy);
      //      cm.Items.Add(mi);
      //    } else if(selected is uiPin) {
      //      MenuItem mi = new MenuItem();
      //      mi.Header = "Trace";
      //      mi.Click += new RoutedEventHandler(mi_Trace);
      //      cm.Items.Add(mi);
      //    }
      //    if(cm.Items.Count > 0) {
      //      cm.IsOpen = true;
      //    }
      //  } else if(Clipboard.ContainsText()) {
      //    var cm = (this.Parent as Grid).ContextMenu;
      //    cm.Items.Clear();
      //    MenuItem mi = new MenuItem();
      //    mi.Header = "Paste";
      //    mi.Click += new RoutedEventHandler(mi_Paste);
      //    cm.Items.Add(mi);
      //    cm.IsOpen = true;
      //  }
      //  return;
      //} else 
      if(e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Released) {
        //if(_mSelected != null && _mSelected.Length > 0) {
        //  var cp = e.GetPosition(this);
        //  double r = 0, d = 0;
        //  foreach(var el in _mSelected.Where(z => !(z is uiTracer))) {
        //    if(move) {
        //      el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), true);
        //      d = Math.Max(d, el.Offset.Y + el.ContentBounds.Bottom);
        //      r = Math.Max(r, el.Offset.X + el.ContentBounds.Right);
        //    }
        //    el.Select(false);
        //  }
        //  _mSelected = null;
        //  if(move) {
        //    if(d + CELL_SIZE > this.Height) {
        //      model.Get<long>("_height").value = 1 + (int)(d) / CELL_SIZE;
        //    }
        //    if(r + CELL_SIZE > this.Width) {
        //      model.Get<long>("_width").value = 1 + (int)(r) / CELL_SIZE;
        //    }
        //  }
        //}
        if(IsMouseCaptured) {
          Cursor = Cursors.Arrow;
          ReleaseMouseCapture();
          //} else if(selected != null) {
          //  SchemaElement el;
          //  loBinding w;
          //  var cp = e.GetPosition(this);
          //  if((el = selected as SchemaElement) != null && move) {
          //    el.SetLocation(new Vector(el.OriginalLocation.X + (cp.X - ScreenStartPoint.X), el.OriginalLocation.Y + (cp.Y - ScreenStartPoint.Y)), true);
          //    if(selected.Offset.Y + selected.ContentBounds.Bottom + CELL_SIZE > this.Height) {
          //      model.Get<long>("_height").value = 1 + (int)(selected.Offset.Y + selected.ContentBounds.Bottom) / CELL_SIZE;
          //    }
          //    if(selected.Offset.X + selected.ContentBounds.Right + CELL_SIZE > this.Width) {
          //      model.Get<long>("_width").value = 1 + (int)(selected.Offset.X + selected.ContentBounds.Right) / CELL_SIZE;
          //    }
          //  } else if((w = selected as loBinding) != null && w.GetModel() == null) {
          //    uiPin finish = GetVisual(cp.X, cp.Y) as uiPin;
          //    if(finish != null && finish != w.A) {
          //      w.SetFinish(finish);
          //    } else {
          //      this.DeleteVisual(w);
          //    }
          //  }
          //} else if(_multipleSelection) {
          //  var cp = e.GetPosition(this);
          //  _multipleSelection = false;
          //  base.RemoveVisualChild(_mSelectVisual);
          //  var objs = new List<SchemaElement>();
          //  GeometryHitTestParameters parameters;
          //  {
          //    double l, t, w, h;
          //    if(cp.X - ScreenStartPoint.X < 0) {
          //      l = cp.X;
          //      w = ScreenStartPoint.X - cp.X;
          //    } else {
          //      l = ScreenStartPoint.X;
          //      w = cp.X - ScreenStartPoint.X;
          //    }
          //    if(cp.Y - ScreenStartPoint.Y < 0) {
          //      t = cp.Y;
          //      h = ScreenStartPoint.Y - cp.Y;
          //    } else {
          //      t = ScreenStartPoint.Y;
          //      h = cp.Y - ScreenStartPoint.Y;
          //    }
          //    parameters = new GeometryHitTestParameters(new RectangleGeometry(new Rect(l, t, w, h)));
          //  }

          //  VisualTreeHelper.HitTest(this, null, new HitTestResultCallback((hr) => {
          //    var rez = (GeometryHitTestResult)hr;
          //    var vis = hr.VisualHit as SchemaElement;
          //    if(vis != null && rez.IntersectionDetail == IntersectionDetail.FullyInside) {
          //      objs.Add(vis);
          //    }
          //    return HitTestResultBehavior.Continue;
          //  }), parameters);
          //  if(objs.Count > 0) {
          //    if(objs.Count == 1) {
          //      selected = objs[0];
          //    } else {
          //      _mSelected = objs.ToArray();
          //      foreach(var el in _mSelected) {
          //        el.Select(true);
          //      }
          //    }
          //  }
        } else {
          base.OnMouseUp(e);
        }
        //move = false;
      } else {
        base.OnMouseUp(e);
      }
    }
  }

}
