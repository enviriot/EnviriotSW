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
using System.Globalization;
using System.Windows.Media.Imaging;

namespace X13.UI {
  internal partial class LogramView : Canvas {
    #region Settings
    private const int CELL_SIZE = 16;
    private static readonly Typeface LFont = new Typeface("Times New Roman");

    private static readonly Brush brElementBody = Brushes.SteelBlue;
    private static readonly Brush brItemSelected = Brushes.Tomato;
    private static readonly Brush brByteArray = new LinearGradientBrush(new GradientStopCollection(
              new GradientStop[] {  new GradientStop(Colors.LightGreen, 0.22), 
                                  new GradientStop(Colors.Black, 0.23), 
                                  new GradientStop(Colors.Black, 0.36), 
                                  new GradientStop(Colors.LightGreen, 0.37), 
                                  new GradientStop(Colors.LightGreen, 0.62), 
                                  new GradientStop(Colors.Black, 0.63), 
                                  new GradientStop(Colors.Black, 0.76), 
                                  new GradientStop(Colors.LightGreen, 0.77) }));
    private static readonly Brush brValueFalse = new SolidColorBrush(Color.FromRgb(192, 200, 192));

    private static readonly Pen SelectionPen = new Pen(brItemSelected, 1);

    #endregion Settings


    internal abstract class loItem : DrawingVisual, IDisposable {

      protected loItem(LogramView lv) {
        this.lv = lv;
      }
      internal readonly LogramView lv;
      protected bool _selected;
      public abstract DTopic GetModel();
      public virtual void Select(bool select) {
        if(_selected != select) {
          _selected = select;
          Render(0);
        }
      }
      /// <summary>feel DrawingVisual</summary>
      /// <param name="chLevel">0 - locale, 1 - local & child, 2 - drag, 3- set position</param>
      public abstract void Render(int chLevel);
      public abstract void Dispose();

    }

    internal class loPin : loItem {
      private loElement _owner;
      private Vector _ownerOffset;
      private List<loBinding> _connections;
      private DTopic model;
      /// <summary>0 - output, 1 - input free, 2 - input local, 3 - input extern(busy)</summary>
      private int _mode;
      private DTopic _source;
      private loBinding _srcBinding;

      public loPin(loElement owner, DTopic model, bool isInput)
        : base(owner.lv) {
        this._owner = owner;
        this.model = model;
        this._mode = isInput ? 1 : 0;
        _connections = new List<loBinding>();
        this.brush = Brushes.LightGray;
      }

      public Brush brush { get; private set; }
      public bool IsInput { get { return _mode != 0; } }
      public bool IsFreeInput { get { return _mode == 1 || _mode == 3; } }

      public void SetLocation(Vector center, int chLevel) {
        _ownerOffset = center;
        Render(chLevel);

      }
      public void AddBinding(loBinding w) {
        if(w == null) {
          return;
        }
        if(_mode == 0) {
          _connections.Add(w);
        } else {
          if(_srcBinding != null) {
            _srcBinding.Dispose();
          }
          _srcBinding = w;
          _mode = 2;
        }
        Render(3);
      }
      public void RemoveBinding(loBinding w) {
        _connections.Remove(w);
      }
      public override void Render(int chLevel) {
        if(model == null || model.State == null || model.Manifest == null) {
          return;
        }
        if(_mode != 0 && chLevel == 3) {
          var src_s = JsLib.OfString(JsLib.GetField(model.Manifest, "cctor.LoBind"), null);
          if(src_s == null) {
            _mode = 1;
          } else if(_source == null || _source.path != src_s || ( _mode == 2 && _srcBinding == null && lv._loadTimer==null)) {
            model.GetAsync(src_s).ContinueWith(SourceLoaded, TaskScheduler.FromCurrentSynchronizationContext());
            return;
          }
          if(_mode != 2 && _srcBinding != null) {
            _source = null;
            _srcBinding.Dispose();
            _srcBinding = null;
          }
        }
        this.Offset = _owner.Offset + _ownerOffset;
        if(chLevel == 3) {
          lv.MapRemove(this);
          lv.MapSet(_mode == 0 ? 3 : 0, (int)( Offset.X / CELL_SIZE + 0.5 ), (int)( Offset.Y / CELL_SIZE + 0.5 ), this);
        }

        var tc = model.State.ValueType;
        switch(tc) {
        case JSC.JSValueType.Object:
          if(model.State is ByteArray || model.State.Value is ByteArray) {
            this.brush = brByteArray;
          } else {
            this.brush = Brushes.MediumOrchid;
          }
          break;
        case JSC.JSValueType.String:
          this.brush = Brushes.Gold;
          break;
        case JSC.JSValueType.Double:
        case JSC.JSValueType.Integer: {
            double val = (double)model.State;
            this.brush = val > 0 ? ( val == 1 ? Brushes.LawnGreen : Brushes.LightSeaGreen ) : ( val == 0 ? brValueFalse : Brushes.DodgerBlue );
          }
          break;
        case JSC.JSValueType.Boolean:
          this.brush = (bool)model.State.Value ? Brushes.LawnGreen : brValueFalse;
          break;
        default:
          this.brush = Brushes.MediumOrchid;
          break;
        }
        using(DrawingContext dc = this.RenderOpen()) {
          if(_mode == 3) {
            dc.DrawRectangle(_selected ? brItemSelected : this.brush, null, new Rect(-4, -4, 8, 8));
          } else {
            dc.DrawEllipse(_selected ? brItemSelected : this.brush, null, new Point(0, 0), 3, 3);
          }
        }
        if(_mode != 0 && _srcBinding != null && chLevel > 1) {
          _srcBinding.Render(chLevel);
        }
        if(_mode == 0 && chLevel > 0) {
          foreach(loBinding w in _connections.ToArray()) {
            w.Render(chLevel);
          }
        }
      }

      private void SourceLoaded(Task<DTopic> tt) {
        if(tt.IsFaulted || !tt.IsCompleted || tt.Result == null) {
          _mode = 1;
          return;
        }
        _source = tt.Result;
        DTopic lo = model.parent;
        if(_owner is loBlock && lo != null) {
          lo = lo.parent;
        }
        if(tt.Result.parent == lo || ( tt.Result.parent != null && tt.Result.parent.parent == lo )) {
          _mode = 2;
          var src = lv._visuals.OfType<loPin>().FirstOrDefault(z => z.model == _source && !z.IsInput);
          if(src != null) {
            if(_srcBinding != null) {
              _srcBinding.Dispose();
            }
            _srcBinding = new loBinding(src, this, lv);
            src.AddBinding(_srcBinding);
            this.Render(3);
          }
        } else {
          _mode = 3;
          this.Render(3);
        }
      }
      public override DTopic GetModel() {
        return model;
      }
      public override void Dispose() {
        var srcB = System.Threading.Interlocked.Exchange(ref _srcBinding, null);
        if(srcB != null) {
          srcB.Dispose();
        }
        _source = null;
        lv.MapRemove(this);
        lv.DeleteVisual(this);
      }

      public override string ToString() {
        return model.path + ( _mode < 2 ? ( _mode == 0 ? "Out" : "InF" ) : ( _mode == 2 ? "InI" : "InE" ) );
      }
    }

    internal class loBinding : loItem {
      private Point _cur;
      private List<Point> _track = new List<Point>();
      private bool _mapped;

      public loPin Input { get; private set; }
      public loPin Output { get; private set; }

      public loBinding(loPin input, loPin output, LogramView lv)
        : base(lv) {
        this.Input = input;
        this.Output = output;
        lv.AddVisual(this);
      }
      public loBinding(loPin start, LogramView lv)
        : base(lv) {
        if(start.IsInput) {
          this.Input = null;
          this.Output = start;
        } else {
          this.Input = start;
          this.Output = null;
        }
        Render(3);
        lv.AddVisual(this);
      }

      public void Update(Point p) {
        _cur = p;
        Render(2);
      }
      public void SetFinish(loPin finish) {
        if(Input == null && !finish.IsInput) {
          Input = finish;
        } else if(Output == null && finish.IsFreeInput) {
          Output = finish;
        }
        Output.GetModel().SetField("cctor.LoBind", Input.GetModel().path);
        this.Dispose();
      }
      public override void Render(int chLevel) {
        if(lv._loadTimer != null) {
          return;
        }
        if(chLevel > 1 && _track.Any()
           && ( chLevel == ( _mapped ? 2 : 3 )
             || _track[0].X != ( Input!=null?Input.Offset.X:_cur.X ) || _track[0].Y != ( Input!=null?Input.Offset.Y:_cur.Y ) 
             || _track[_track.Count - 1].X != ( Output!=null?Output.Offset.X:_cur.X ) || _track[_track.Count - 1].Y != ( Output!=null?Output.Offset.Y:_cur.Y ) )) {
          if(_mapped) {
            lv.MapRemove(this);
            _mapped = false;
          }
          _track.Clear();
        }

        if(chLevel == 3 && Input != null && Output != null && !_mapped) {
          FindPath(_track);
        }

        if(_track.Count == 0 || chLevel == 2) {
          if(Input != null) {
            _track.Add(new Point(Input.Offset.X, Input.Offset.Y));
          } else {
            _track.Add(_cur);
          }
          if(Output != null) {
            _track.Add(new Point(Output.Offset.X, Output.Offset.Y));
          } else {
            _track.Add(_cur);
          }
        }

        using(DrawingContext dc = this.RenderOpen()) {
          Pen pn = ( _selected || Input == null ) ? SelectionPen : new Pen(Input.brush, 2.0);
          for(int i = 0; i < _track.Count - 1; i++) {
            if(_track[i].X == _track[i + 1].X && _track[i].Y == _track[i + 1].Y) {
              dc.DrawEllipse(Input.brush, null, _track[i], 3, 3);
            } else {
              dc.DrawLine(pn, _track[i], _track[i + 1]);
            }
          }
        }
      }

      private static int[,] direction = new int[4, 3] { { 1, 0, 3 }, { 0, -1, 2 }, { 0, 1, 1 }, { -1, 0, 0 } };
      private void FindPath(List<Point> track) {
        PriorityQueue<PathFinderNode> mOpen = new PriorityQueue<PathFinderNode>(new ComparePFNode());
        List<PathFinderNode> mClose = new List<PathFinderNode>();
        int mSearchLimit = 3000;

        PathFinderNode parentNode;
        bool found = false;
        int startX = (int)( this.Input.Offset.X / CELL_SIZE + 0.5 );
        int startY = (int)( this.Input.Offset.Y / CELL_SIZE + 0.5 );
        int finishX = (int)( this.Output.Offset.X / CELL_SIZE + 0.5 );
        int finishY = (int)( this.Output.Offset.Y / CELL_SIZE + 0.5 );
        mOpen.Clear();
        mClose.Clear();


        parentNode.G = 0;
        parentNode.H = 1;
        parentNode.F = parentNode.G + parentNode.H;
        parentNode.X = startX;
        parentNode.Y = startY;
        parentNode.PX = startX - 1;
        parentNode.PY = startY;

        mOpen.Push(parentNode);
        while(mOpen.Count > 0) {
          parentNode = mOpen.Pop();

          if(parentNode.X == finishX && parentNode.Y == finishY) {
            mClose.Add(parentNode);
            found = true;
            break;
          }

          if(mClose.Count > mSearchLimit) {
            return;
          }

          //Lets calculate each successors
          for(int i = 0; i < 4; i++) {
            PathFinderNode newNode;
            newNode.PX = parentNode.X;
            newNode.PY = parentNode.Y;
            newNode.X = parentNode.X + direction[i, 0];
            newNode.Y = parentNode.Y + direction[i, 1];
            int newG = this.GetWeigt(newNode.X, newNode.Y, i);
            if(newG > 100 || newG == 0) {
              continue;
            }
            newG = Math.Max(newG, this.GetWeigt(newNode.PX, newNode.PY, direction[i, 2]));
            if(newG > 100 || newG == 0) {
              continue;
            }
            newG += parentNode.G;

            // Дополнительная стоимиость поворотов
            if(Math.Abs(newNode.Y - parentNode.PY) == 1 || Math.Abs(newNode.X - parentNode.PX) == 1) {
              if(this.GetWeigt(parentNode.X, parentNode.Y, i) > 100) {
                continue;
              }
              newG += 8;
            }

            int foundInOpenIndex = -1;
            for(int j = 0; j < mOpen.Count; j++) {
              if(mOpen[j].X == newNode.X && mOpen[j].Y == newNode.Y) {
                foundInOpenIndex = j;
                break;
              }
            }
            if(foundInOpenIndex != -1 && mOpen[foundInOpenIndex].G <= newG)
              continue;

            int foundInCloseIndex = -1;
            for(int j = 0; j < mClose.Count; j++) {
              if(mClose[j].X == newNode.X && mClose[j].Y == newNode.Y) {
                foundInCloseIndex = j;
                break;
              }
            }
            if(foundInCloseIndex != -1 && mClose[foundInCloseIndex].G <= newG)
              continue;

            newNode.G = newG;

            newNode.H = 2 + Math.Sign(Math.Abs(newNode.X - finishX) + Math.Abs(newNode.Y - finishY) - Math.Abs(newNode.PX - finishX) - Math.Abs(newNode.PY - finishY));
            newNode.F = newNode.G + newNode.H;

            mOpen.Push(newNode);
          }
          mClose.Add(parentNode);
        }

        if(found) {
          loItem pIt = null, cIt;
          track.Clear();
          PathFinderNode fNode = mClose[mClose.Count - 1];
          track.Add(new Point(finishX * CELL_SIZE, finishY * CELL_SIZE));
          for(int i = mClose.Count - 1; i >= 0; i--) {
            if(fNode.PX == mClose[i].X && fNode.PY == mClose[i].Y || i == mClose.Count - 1) {
              fNode = mClose[i];
              int dir = CalcDir(fNode.PX, fNode.X, fNode.PY, fNode.Y);
              int ndir = direction[dir, 2];
              if(( lv.MapGet(ndir, fNode.PX, fNode.PY) ) == null) {
                lv.MapSet(ndir, fNode.PX, fNode.PY, this);
              }
              if(( cIt = lv.MapGet(dir, fNode.X, fNode.Y) ) == null) {
                lv.MapSet(dir, fNode.X, fNode.Y, this);
              } else {
                if(cIt == this || cIt == this.Input || cIt == this.Output) {
                  cIt = null;
                }
              }
              if(i > 0 && i < mClose.Count - 1 && cIt != pIt) {
                track.Insert(0, new Point(fNode.X * CELL_SIZE, fNode.Y * CELL_SIZE));
                track.Insert(0, new Point(fNode.X * CELL_SIZE, fNode.Y * CELL_SIZE));
                //Log.Info("{0}: {1}; {2}, {3} - {4}; {5}, {6}", this.ToString(), pIt, fNode.X, fNode.Y, cIt, fNode.PX, fNode.PY);
              } else if(track[0].X != fNode.PX * CELL_SIZE && track[0].Y != fNode.PY * CELL_SIZE) {
                track.Insert(0, new Point(fNode.X * CELL_SIZE, fNode.Y * CELL_SIZE));
              }
              //Log.Info("{0}: {1}; {2}, {3} - {4}; {5}, {6}", this.ToString(), pIt, fNode.X, fNode.Y, cIt, fNode.PX, fNode.PY);
              pIt = cIt;
            }
          }
          if(track[0].X != startX * CELL_SIZE || track[0].Y != startY * CELL_SIZE) {
            track.Insert(0, new Point(startX * CELL_SIZE, startY * CELL_SIZE));
          }
          _mapped = true;
        }
        // Visu
        //using(DrawingContext dc = this.RenderOpen()) {
        //  for(int i = 0; i < mClose.Count; i++) {
        //    FormattedText txt = new FormattedText(mClose[i].G.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LFont, CELL_SIZE * 0.3, Brushes.Violet);
        //    dc.DrawText(txt, new Point(mClose[i].X * CELL_SIZE, mClose[i].Y * CELL_SIZE + CELL_SIZE / 2));
        //    dc.DrawLine(new Pen(Brushes.RosyBrown, 1), new Point(mClose[i].X * CELL_SIZE, CELL_SIZE + mClose[i].Y * CELL_SIZE), new Point(mClose[i].PX * CELL_SIZE, CELL_SIZE + mClose[i].PY * CELL_SIZE));
        //  }
        //  for(int i = 0; i < _track.Count - 1; i++) {
        //    dc.DrawLine(SelectionPen, _track[i], _track[i + 1]);
        //  }
        //}
      }
      private int GetWeigt(int X, int Y, int dir) {
        if(X < 0 || Y < 0 || X * CELL_SIZE >= lv.Width - CELL_SIZE || Y * CELL_SIZE >= lv.Height - CELL_SIZE) {
          return 256;
        }
        int g;
        var it = lv.MapGet(dir, X, Y);
        if(it == null) {
          g = 6;
        } else if(it is loPin) {
          g = ( it == this.Input || it == this.Output ) ? 1 : 101;
        } else if(it is loBinding) {
          var w = it as loBinding;
          g = ( w.Input == this.Input || w.Input == this.Output || w.Output == this.Input || w.Output == this.Output ) ? 1 : 101;
        } else if(it is loElement) {
          g = 101;
        } else {
          g = 9;
        }
        //if(it != null) {
        //  Log.Debug("[{0}:{1} - {2}] g={3}, it={4}", X, Y, dir, g, it);
        //}
        return g;
      }

      private int CalcDir(int PX, int X, int PY, int Y) {  // 0 - X+, 1 - Y-, 2 - Y+, 3 - X-
        return ( PY == Y ) ? ( PX > X ? 3 : 0 ) : ( Y > PY ? 2 : 1 );
      }
      private class ComparePFNode : IComparer<PathFinderNode> {
        public int Compare(PathFinderNode x, PathFinderNode y) {
          if(x.F > y.F)
            return 1;
          else if(x.F < y.F)
            return -1;
          return 0;
        }
      }
      private struct PathFinderNode {
        public int F;
        public int G;
        public int H;  // f = gone + heuristic
        public int X;
        public int Y;
        public int PX; // Parent
        public int PY;
      }

      public override DTopic GetModel() {
        return null;
      }
      public override string ToString() {
        return ( Input != null ? Input.GetModel().path : "nc" ) + " => " + ( Output != null ? Output.GetModel().path : "nc" );
      }

      public override void Dispose() {
        Input.RemoveBinding(this);
        lv.MapRemove(this);
        lv.DeleteVisual(this);
      }
    }

    internal abstract class loElement : loItem {
      protected loElement(LogramView lv)
        : base(lv) {
      }
      public Vector OriginalLocation { get; protected set; }
      public abstract void SetLocation(Vector loc, bool save);
    }

    internal class loVariable : loElement {
      public loPin Input { get; private set; }
      public loPin Output { get; private set; }

      public readonly DTopic model;

      public loVariable(DTopic model, LogramView lv)
        : base(lv) {
        this.model = model;
        this.Output = new loPin(this, model, false);
        this.Input = new loPin(this, model, true);
        Render(3);
        lv.AddVisual(this);
        lv.AddVisual(Input);
        lv.AddVisual(Output);
        model.changed += ModelChanged;
      }

      public override void SetLocation(Vector loc, bool save) {
        int topCell = (int)( loc.Y / CELL_SIZE + 0.5 );
        if(topCell < 0) {
          topCell = 0;
        }
        int leftCell = (int)( loc.X / CELL_SIZE );
        if(leftCell < 0) {
          leftCell = 0;
        }
        if(save) {
          var lo = JsLib.GetField(model.Manifest, "Logram");
          int xo, yo;
          xo = JsLib.OfInt(JsLib.GetField(lo, "left"), 0);
          yo = JsLib.OfInt(JsLib.GetField(lo, "top"), 0);

          if(xo == leftCell && yo == topCell) {    // refresh wires
            this.Dispatcher.BeginInvoke(new Action<int>(this.Render), System.Windows.Threading.DispatcherPriority.DataBind, 3);
          } else {
            lo = JsLib.SetField(lo, "top", topCell);
            lo = JsLib.SetField(lo, "left", leftCell);
            model.SetField("Logram", lo);
          }
        } else {
          this.Offset = new Vector(leftCell * CELL_SIZE, ( topCell - 0.5 ) * CELL_SIZE);
          ;
          Output.Render(2);
          Input.Render(2);
        }
      }
      public override void Render(int chLevel) {
        int x, y;
        y = JsLib.OfInt(JsLib.GetField(model.Manifest, "Logram.top"), 0);
        x = JsLib.OfInt(JsLib.GetField(model.Manifest, "Logram.left"), 0);
        double width = 0;
        base.OriginalLocation = new Vector(x * CELL_SIZE, ( y - 0.5 ) * CELL_SIZE);
        this.Offset = OriginalLocation;

        using(DrawingContext dc = this.RenderOpen()) {
          FormattedText ft = new FormattedText(model.name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LFont, CELL_SIZE * 0.7, Brushes.White);
          width = Math.Round(( ft.WidthIncludingTrailingWhitespace + CELL_SIZE * 1.5 ) / CELL_SIZE, 0) * CELL_SIZE;
          dc.DrawRoundedRectangle(_selected ? brItemSelected : brElementBody, null, new Rect(0, 1, width - 1, CELL_SIZE - 3), CELL_SIZE / 4, CELL_SIZE / 4);
          ft.MaxTextHeight = CELL_SIZE - 3;
          ft.MaxTextWidth = width - CELL_SIZE / 2 - 5;
          dc.DrawText(ft, new Point(5, 1));
        }
        if(chLevel == 3) {
          lv.MapRemove(this);
          for(int w = (int)( width / CELL_SIZE + 0.5 ); w >= 0; w--) {
            lv.MapSet(0, x + w, y, this);
            lv.MapSet(1, x + w, y, this);
            lv.MapSet(2, x + w, y, this);
            lv.MapSet(3, x + w, y, this);
          }
        }

        if(chLevel > 1) {
          Output.SetLocation(new Vector(width, CELL_SIZE / 2), chLevel);
          Input.SetLocation(new Vector(0, CELL_SIZE / 2), chLevel);
        }
      }

      private void ModelChanged(DTopic.Art a, DTopic t) {
        if(t == model) {
          switch(a) {
          case DTopic.Art.addChild:
            this.Render(2);
            break;
          case DTopic.Art.RemoveChild:
            this.Dispose();
            break;
          case DTopic.Art.value:
            Input.Render(1);
            Output.Render(1);
            break;
          case DTopic.Art.type:
            this.Render(3);
            break;
          }
        }
      }

      public override DTopic GetModel() {
        return model;
      }
      public override void Dispose() {
        Input.Dispose();
        Output.Dispose();
        lv.DeleteVisual(this);
        lv.MapRemove(this);
        model.changed -= ModelChanged;
      }
      public override string ToString() {
        return model.path;
      }
    }

    internal class loBlock : loElement {
      private const int MAX_PINS = 10;
      private readonly DTopic model;
      private List<loPin> _pins;

      public loBlock(DTopic model, LogramView owner)
        : base(owner) {
        this.model = model;
        _pins = new List<loPin>();
        this.model.changed += model_changed;
        lv.AddVisual(this);
        this.model.GetAsync(null).ContinueWith(ModelLoaded, TaskScheduler.FromCurrentSynchronizationContext());
      }

      private void ModelLoaded(Task<DTopic> tt) {
        if(tt.IsFaulted || !tt.IsCompleted || tt.Result == null) {
          return;
        }
        if(model.children != null) {
          foreach(var c in model.children) {
            c.GetAsync(null).ContinueWith(PinLoaded, TaskScheduler.FromCurrentSynchronizationContext());
          }
        }
      }
      private void PinLoaded(Task<DTopic> tt) {
        DTopic t;
        if(tt.IsFaulted || !tt.IsCompleted || ( t = tt.Result ) == null) {
          return;
        }
        loPin p;

        var chs = model.Manifest["Children"];
        if(chs.ValueType != JSC.JSValueType.Object || chs.Value == null) {
          return;
        }
        var pd = chs[t.name];
        int ddr;
        if(pd.ValueType != JSC.JSValueType.Object || pd.Value == null || (ddr = JsLib.OfInt(pd, "ddr", 0))==0) {
          return;
        }
        p = new loPin(this, t, ddr<0);
        _pins.Add(p);
        lv.AddVisual(p);
        t.changed += pin_changed;

        var lt = lv._loadTimer;
        if(lt != null) {
          lt.Change(100, -1);
        }
        this.Render(3);
      }
      private void model_changed(DTopic.Art a, DTopic t) {
        if(t == model) {
          if(a == DTopic.Art.type || a == DTopic.Art.addChild) {
            Render(3);
          }
        } else if(t.parent == model) {
          loPin p;
          p = _pins.FirstOrDefault(z => z.GetModel() == t);
          if(p == null) {
            if(a == DTopic.Art.addChild) {
              t.GetAsync(null).ContinueWith(PinLoaded, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return;
          }
          if(a == DTopic.Art.RemoveChild) {
            lv.DeleteVisual(p);
            _pins.Remove(p);
            t.changed -= pin_changed;
          }
          this.Render(3);
        }
      }
      private void pin_changed(DTopic.Art a, DTopic t) {
        loPin p;
        p = _pins.FirstOrDefault(z => z.GetModel() == t);
        if(p == null) {
          return;
        }
        p.Render(a == DTopic.Art.value ? 1 : 3);
      }

      #region loElement Members
      public override void Render(int chLevel) {
        int x, y;
        y = JsLib.OfInt(JsLib.GetField(model.Manifest, "Logram.top"), 0);
        x = JsLib.OfInt(JsLib.GetField(model.Manifest, "Logram.left"), 0);
        base.OriginalLocation = new Vector(x * CELL_SIZE, ( y - 0.5 ) * CELL_SIZE);
        this.Offset = OriginalLocation;


        FormattedText head = new FormattedText(model.name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LogramView.LFont, CELL_SIZE * 0.7, Brushes.Black);
        FormattedText[] textIp = new FormattedText[MAX_PINS];
        loPin[] pinIp = new loPin[MAX_PINS];
        int cntIp = 0;
        FormattedText[] textOp = new FormattedText[MAX_PINS];
        loPin[] pinOp = new loPin[MAX_PINS];
        int cntOp = 0;
        int pos = 0;
        double wi = 0;
        double wo = 0;

        var chs = model.Manifest["Children"];
        if(chs.ValueType != JSC.JSValueType.Object || chs.Value == null) {
          return;
        }

        foreach(var p in _pins) {
          var pd = chs[p.GetModel().name];
          int ddr;
          if(pd.ValueType != JSC.JSValueType.Object || pd.Value == null || ( ddr = JsLib.OfInt(pd, "ddr", 0) )==0) {
            continue;
          }
          var ft = new FormattedText(p.GetModel().name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LogramView.LFont, CELL_SIZE * 0.7, Brushes.White);
          double cw = 4 + ft.WidthIncludingTrailingWhitespace;
          if(ddr<0) {  // Input
            pos = -ddr-1;
            if(cntIp < pos + 1) {
              cntIp = pos + 1;
            }
            pinIp[pos] = p;
            textIp[pos] = ft;
            if(pos == 0) {
              cw += 9;
            }
            wi = Math.Max(wi, cw);
          } else {  // Output
            pos = ddr - 1;
            if(cntOp < pos + 1) {
              cntOp = pos + 1;
            }
            pinOp[pos] = p;
            textOp[pos] = ft;
            if(pos == 0) {
              cw += 9;
            }
            wo = Math.Max(wo, cw);
          }
        }
        wi = Math.Round(( 2 * wi ) / CELL_SIZE, 0) * CELL_SIZE / 2;
        wo = Math.Round(( 2 * wo ) / CELL_SIZE, 0) * CELL_SIZE / 2;
        double width = Math.Round(Math.Max(head.WidthIncludingTrailingWhitespace * 2 - CELL_SIZE / 2, wi + wo + CELL_SIZE) / CELL_SIZE + 0.5, 0) * CELL_SIZE;
        double height = Math.Max(cntIp * CELL_SIZE, cntOp * CELL_SIZE);
        if(height == 0) {
          return;
        }
        if(chLevel == 3) {
          lv.MapRemove(this);
          int cw = (int)width / CELL_SIZE;
          int ch = 1 + (int)height / CELL_SIZE;
          for(int i = cw; i >= 0; i--) {
            for(int j = ch - 1; j >= 0; j--) {
              lv.MapSet(0, x + i, y + j, this);
              lv.MapSet(1, x + i, y + j, this);
              lv.MapSet(2, x + i, y + j, this);
              lv.MapSet(3, x + i, y + j, this);
            }
          }
        }
        base.VisualBitmapScalingMode = BitmapScalingMode.HighQuality;
        using(DrawingContext dc = this.RenderOpen()) {
          dc.DrawRectangle(Brushes.White, null, new Rect(-2, 2, width + 4, height + CELL_SIZE - 2));
          dc.DrawRectangle(_selected ? brItemSelected : brElementBody, null, new Rect(0, CELL_SIZE, width, height));
          dc.DrawText(head, new Point(( width - head.WidthIncludingTrailingWhitespace ) / 2, 1));
          dc.DrawImage(App.GetIcon(JsLib.OfString(model.Manifest["icon"], null)), new Rect(wi, CELL_SIZE, CELL_SIZE, CELL_SIZE));
          int i;
          for(i = 0; i < cntIp; i++) {
            if(textIp[i] != null && pinIp[i] != null) {
              dc.DrawText(textIp[i], new Point(7, ( i + 1 ) * CELL_SIZE + 2));
            }
          }
          int inW = (int)width / CELL_SIZE;
          for(i = 0; i < cntOp; i++) {
            if(textOp[i] != null && pinOp[i] != null) {
              dc.DrawText(textOp[i], new Point(width - 7 - textOp[i].WidthIncludingTrailingWhitespace, ( i + 1 ) * CELL_SIZE + 2));
            }
          }
        }
        if(chLevel > 0) {
          int i;
          for(i = 0; i < cntIp; i++) {
            if(pinIp[i] != null) {
              pinIp[i].SetLocation(new Vector(0, i * CELL_SIZE + CELL_SIZE * 1.5), chLevel);
            }
          }
          for(i = 0; i < cntOp; i++) {
            if(pinOp[i] != null) {
              pinOp[i].SetLocation(new Vector(width, i * CELL_SIZE + CELL_SIZE * 1.5), chLevel);
            }
          }
        }
      }
      public override void SetLocation(Vector loc, bool save) {
        int topCell = (int)( loc.Y / CELL_SIZE + 0.5 );
        if(topCell < 0) {
          topCell = 0;
        }
        int leftCell = (int)( loc.X / CELL_SIZE );
        if(leftCell < 0) {
          leftCell = 0;
        }

        if(save) {
          var lo = JsLib.GetField(model.Manifest, "Logram");
          int xo, yo;
          xo = JsLib.OfInt(JsLib.GetField(lo, "left"), 0);
          yo = JsLib.OfInt(JsLib.GetField(lo, "top"), 0);

          if(xo == leftCell && yo == topCell) {    // refresh wires
            this.Dispatcher.BeginInvoke(new Action<int>(this.Render), System.Windows.Threading.DispatcherPriority.DataBind, 3);
          } else {
            lo = JsLib.SetField(lo, "top", topCell);
            lo = JsLib.SetField(lo, "left", leftCell);
            model.SetField("Logram", lo);
          }
        } else {
          this.Offset = new Vector(leftCell * CELL_SIZE, ( topCell - 0.5 ) * CELL_SIZE);
          foreach(var p in _pins) {
            p.Render(2);
          }
        }
      }
      public override DTopic GetModel() {
        return model;
      }
      public override void Dispose() {
        model.changed -= model_changed;
        lv.DeleteVisual(this);
        lv.MapRemove(this);
        foreach(var p in _pins) {
          p.GetModel().changed -= pin_changed;
          p.Dispose();
        }

      }
      #endregion loElement Members

      public override string ToString() {
        return model.path;
      }
    }
  }
}