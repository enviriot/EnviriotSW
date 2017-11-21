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

namespace X13.UI {
  internal partial class LogramView : Canvas {

    internal abstract class uiItem : DrawingVisual {

      protected uiItem(LogramView lv) {
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
      public override string ToString() {
        return GetModel() != null ? GetModel().name : "??";
      }
    }

    internal class uiPin : uiItem {
      private static Brush _BABrush;
      private static Brush _DblBrush;
      static uiPin() {
        _DblBrush = new SolidColorBrush(Color.FromRgb(0, 40, 100));
        _BABrush = new LinearGradientBrush(new GradientStopCollection(
              new GradientStop[] {  new GradientStop(Colors.LightGreen, 0.22), 
                                  new GradientStop(Colors.Black, 0.23), 
                                  new GradientStop(Colors.Black, 0.36), 
                                  new GradientStop(Colors.LightGreen, 0.37), 
                                  new GradientStop(Colors.LightGreen, 0.62), 
                                  new GradientStop(Colors.Black, 0.63), 
                                  new GradientStop(Colors.Black, 0.76), 
                                  new GradientStop(Colors.LightGreen, 0.77) }));
      }

      private SchemaElement _owner;
      private Vector _ownerOffset;
      private List<loBinding> _connections;
      private DTopic model;
      /// <summary>0 - output, 1 - input free, 2 - input local, 3 - input extern(busy)</summary>
      private int _mode;
      private DTopic _source;
      private loBinding _srcBinding;

      public uiPin(SchemaElement owner, DTopic model, bool isInput)
        : base(owner.lv) {
          this._owner = owner;
        this.model = model;
        this._mode = isInput?1:0;
        _connections = new List<loBinding>();
        this.brush = Brushes.LightGray;
      }

      public Brush brush { get; private set; }
      public bool IsInput { get { return _mode != 0; } }

      public void SetLocation(Vector center, int chLevel) {
        _ownerOffset = center;
        Render(chLevel);
      }
      public void AddBinding(loBinding w) {
        _connections.Add(w);
        Render(3);
      }
      public void RemoveBinding(loBinding w) {
        _connections.Remove(w);
      }
      public override void Render(int chLevel) {
        if(model == null) {
          return;
        }
        if(_mode != 0 && chLevel == 3) {
          var src_s = JsLib.OfString(JsLib.GetField(model.Manifest, "cctor.LoBind"), null);
          if(src_s == null) {
            _mode = 1;
          } else if(_source==null || _source.path!=src_s) {
            model.GetAsync(src_s).ContinueWith(SourceLoaded);
            return;
          }
          //TODO: check input wire
        }
        this.Offset = _owner.Offset + _ownerOffset;
        if((_mode == 0 && _connections.Any()) || _mode == 2) {
          var tc = model.State.ValueType;
          switch(tc) {
          case JSC.JSValueType.Object:
            //if(model.valueType == typeof(ByteArray)) {
            //  this.brush = _BABrush;
            //} else {
            this.brush = Brushes.Magenta;
            //}
            break;
          case JSC.JSValueType.Date:
            this.brush = Brushes.LightSeaGreen;
            break;
          case JSC.JSValueType.String:
            this.brush = Brushes.Khaki;
            break;
          case JSC.JSValueType.Double:
            this.brush = _DblBrush;
            break;
          case JSC.JSValueType.Integer:
            this.brush = Brushes.Green;
            break;
          case JSC.JSValueType.Boolean:
            this.brush = (bool)model.State.Value ? Brushes.Lime : Brushes.DarkGray;
            break;
          default:
            this.brush = Brushes.LightGray;
            break;
          }
        } else {
          this.brush = null;
        }
        using(DrawingContext dc = this.RenderOpen()) {
          dc.DrawEllipse(this.brush, _selected ? SelectionPen : null, new Point(0, 0), 3, 3);
        }
        if(_mode != 0 && _srcBinding != null && chLevel > 1) {
          _srcBinding.Render(chLevel);
        }
        if(_mode==0 && chLevel > 0) {
          foreach(loBinding w in _connections.ToArray()) {
            w.Render(chLevel);
          }
        }
      }

      private void SourceLoaded(Task<DTopic> tt) {
        if(tt.IsFaulted || !tt.IsCompleted || tt.Result==null) {
          _mode = 1;
          return;
        }
        _source = tt.Result;
        if(tt.Result.parent == model.parent || (tt.Result.parent!=null && tt.Result.parent.parent==model.parent)){
          _mode = 2;
          var src = lv._visuals.OfType<uiPin>().FirstOrDefault(z => z.model == _source && !z.IsInput);
          if(src != null) {
            _srcBinding = new loBinding(src, this, lv);
            src.AddBinding(_srcBinding);
          }
        } else {
          _mode = 3;
        }
        this.Render(3);
      }
      public override DTopic GetModel() {
        return model;
      }
    }

    internal class loBinding : uiItem {
      private Point _cur;
      private List<Point> _track = new List<Point>();

      public uiPin Input { get; private set; }
      public uiPin Output { get; private set; }

      public loBinding(uiPin input, uiPin output, LogramView lv) : base(lv) {
        this.Input = input;
        this.Output = output;
        lv.AddVisual(this);
      }
      public loBinding(uiPin start, LogramView lv)
        : base(lv) {
        if(start.IsInput) {
          this.Input = start;
          this.Output = null;
        } else {
          this.Input = null;
          this.Output = start;
        }
        Render(3);
        lv.AddVisual(this);
      }

      public void Update(Point p) {
        _cur = p;
        Render(2);
      }
      public void SetFinish(uiPin finish) {
        if(Input == null) {
          Input = finish;
        } else if(Output==null) {
          Output = finish;
        }
        Render(3);
      }
      public override void Render(int chLevel) {
        if(chLevel > 1 && _track.Count > 0) {
          lv.MapRemove(this);
        }
        if(chLevel > 2 && Input != null && Output!=null) {
          FindPath(_track);
        }

        if(_track.Count == 0 || chLevel == 2) {
          _track.Clear();
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
          Pen pn = _selected ? SelectionPen : new Pen(Input.brush, 2.0);
          for(int i = 0; i < _track.Count - 1; i++) {
            if(_track[i].X == _track[i + 1].X && _track[i].Y == _track[i + 1].Y) {
              dc.DrawEllipse(Input.brush, null, _track[i], 3, 3);
            } else {
              dc.DrawLine(pn, _track[i], _track[i + 1]);
            }
          }
        }
      }

      private static int[,] direction = new int[4, 2] { { -1, 0 }, { 0, -1 }, { 1, 0 }, { 0, 1 } };
      private void FindPath(List<Point> track) {
        PriorityQueue<PathFinderNode> mOpen = new PriorityQueue<PathFinderNode>(new ComparePFNode());
        List<PathFinderNode> mClose = new List<PathFinderNode>();
        double mVert = 0;
        int mSearchLimit = 3000;

        PathFinderNode parentNode;
        bool found = false;
        int startX = (int)Math.Round(this.Input.Offset.X / CELL_SIZE - 1, 0);
        int startY = (int)Math.Round(this.Input.Offset.Y / CELL_SIZE - 1, 0);
        int finishX = (int)Math.Round(this.Output.Offset.X / CELL_SIZE - 1, 0);
        int finishY = (int)Math.Round(this.Output.Offset.Y / CELL_SIZE - 1, 0);
        mOpen.Clear();
        mClose.Clear();


        parentNode.G = 0;
        parentNode.H = 1;
        parentNode.F = parentNode.G + parentNode.H;
        parentNode.X = startX;
        parentNode.Y = startY;
        parentNode.PX = startX + Math.Sign(this.Input.Offset.X - CELL_SIZE - startX * CELL_SIZE);
        parentNode.PY = startY + Math.Sign(this.Output.Offset.Y - CELL_SIZE - startY * CELL_SIZE);

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

          mVert = (parentNode.Y - parentNode.PY);

          //Lets calculate each successors
          for(int i = 0; i < 4; i++) {
            PathFinderNode newNode;
            newNode.PX = parentNode.X;
            newNode.PY = parentNode.Y;
            newNode.X = parentNode.X + direction[i, 0];
            newNode.Y = parentNode.Y + direction[i, 1];
            int newG = this.GetWeigt(newNode.X, newNode.Y, direction[i, 0] == 0);
            if(newG > 100 || newG == 0)
              continue;
            newG += parentNode.G;

            // Дополнительная стоимиость поворотов
            if(((newNode.Y - parentNode.Y) != 0) != (mVert != 0)) {
              if(this.GetWeigt(parentNode.X, parentNode.Y, direction[i, 0] == 0) > 100) {
                continue;
              }
              newG += 4; // 20;
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
          uiItem pIt = null, cIt;
          track.Clear();
          PathFinderNode fNode = mClose[mClose.Count - 1];
          track.Add(new Point(CELL_SIZE + finishX * CELL_SIZE, CELL_SIZE + finishY * CELL_SIZE));
          for(int i = mClose.Count - 1; i >= 0; i--) {
            if(fNode.PX == mClose[i].X && fNode.PY == mClose[i].Y || i == mClose.Count - 1) {
              fNode = mClose[i];
              bool vert = (fNode.PY - fNode.Y) != 0;
              if((lv.MapGet(vert, fNode.PX, fNode.PY)) == null) {
                lv.MapSet(vert, fNode.PX, fNode.PY, this);
              }
              if((cIt = lv.MapGet(vert, fNode.X, fNode.Y)) == null) {
                lv.MapSet(vert, fNode.X, fNode.Y, this);
              } else {
                if(cIt == this || cIt == this.Input || cIt == this.Output) {
                  cIt = null;
                }
              }
              if(i > 0 && i < mClose.Count - 1 && cIt != pIt) {
                track.Insert(0, new Point(CELL_SIZE + fNode.X * CELL_SIZE, CELL_SIZE + fNode.Y * CELL_SIZE));
                track.Insert(0, new Point(CELL_SIZE + fNode.X * CELL_SIZE, CELL_SIZE + fNode.Y * CELL_SIZE));
                //Log.Info("{0}: {1}; {2}, {3} - {4}; {5}, {6}", this.ToString(), pIt, fNode.X, fNode.Y, cIt, fNode.PX, fNode.PY);
              } else if(track[0].X != CELL_SIZE + fNode.PX * CELL_SIZE && track[0].Y != CELL_SIZE + fNode.PY * CELL_SIZE) {
                track.Insert(0, new Point(CELL_SIZE + fNode.X * CELL_SIZE, CELL_SIZE + fNode.Y * CELL_SIZE));
              }
              pIt = cIt;
            }
          }
          if(track[0].X != startX * CELL_SIZE + CELL_SIZE || track[0].Y != startY * CELL_SIZE + CELL_SIZE) {
            track.Insert(0, new Point(CELL_SIZE + startX * CELL_SIZE, CELL_SIZE + startY * CELL_SIZE));
          }
        }
        // Visu
        //using(DrawingContext dc=this.RenderOpen()) {
        //  for(int i=0; i<mClose.Count; i++) {
        //    FormattedText txt=new FormattedText(mClose[i].F.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LogramView.FtFont, gs*0.3, Brushes.Violet);
        //    dc.DrawText(txt, new Point(gs/2+mClose[i].X*gs, gs/2+mClose[i].Y*gs));
        //    dc.DrawLine(new Pen(Brushes.RosyBrown, 1), new Point(gs+mClose[i].X*gs, gs+mClose[i].Y*gs), new Point(gs+mClose[i].PX*gs, gs+mClose[i].PY*gs));
        //  }
        //  Pen pn=new Pen(A.brush, 2.0);
        //  for(int i=0; i<_track.Count-1; i++) {
        //    dc.DrawLine(_selected?Schema.SelectionPen:pn, _track[i], _track[i+1]);
        //  }
        //}
      }
      private int GetWeigt(int X, int Y, bool vert) {
        if(X < 0 || Y < 0 || X * CELL_SIZE >= lv.Width - CELL_SIZE || Y * CELL_SIZE >= lv.Height - CELL_SIZE) {
          return 256;
        }
        var it = lv.MapGet(vert, X, Y);
        if(it is SchemaElement) {
          vert = !vert;
          it = lv.MapGet(vert, X, Y);
        }
        if(it == null) {
          return 3;
        } else if(it is uiPin) {
          if(it == this.Input || it == this.Output) {
            return 1;
          }
          return 101;
        } else if(it is loBinding) {
          var w = it as loBinding;
          if(w.Input == this.Input || w.Input == this.Output || w.Output == this.Input || w.Output == this.Output) {
            return 1;
          } else if((w = lv.MapGet(!vert, X, Y) as loBinding) != null && (w.Input == this.Input || w.Input == this.Output || w.Output == this.Input || w.Output == this.Output)) {
            return 1;
          }
          return 101;
        } else if(it is SchemaElement) {
          return 101;
        }
        return 5;
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
    }


    internal abstract class SchemaElement : uiItem {
      protected SchemaElement(LogramView lv)
        : base(lv) {
      }
      public Vector OriginalLocation { get; protected set; }
      public abstract void SetLocation(Vector loc, bool save);
    }

    internal class uiAlias : SchemaElement {
      public uiPin Input { get; private set; }
      public uiPin Output { get; private set; }
      private int _oldX = -1;
      private int _oldY = -1;
      private int _oldH = 0;

      public readonly DTopic model;

      public uiAlias(DTopic model, LogramView lv) : base(lv) {
        this.model = model;
        this.Output = new uiPin(this, model, false);
        this.Input = new uiPin(this, model, true);
        Render(3);
        lv.AddVisual(this);
        lv.AddVisual(Input);
        lv.AddVisual(Output);
        model.changed += ModelChanged;
      }

      public override void SetLocation(Vector loc, bool save) {
        if(save) {
          int topCell = (int)(loc.Y - CELL_SIZE / 2);
          if(topCell < 0) {
            topCell = 0;
          }
          int leftCell = (int)(loc.X);
          if(leftCell < 0) {
            leftCell = 0;
          }
          model.SetField("Logram.top", topCell);
          model.SetField("Logram.left", leftCell);
        } else {
          if(_oldX >= 0 && _oldY >= 0) {
            for(int inH = _oldH; inH >= 0; inH--) {
              lv.MapSet(true, _oldX, _oldY + inH, null);
              lv.MapSet(false, _oldX, _oldY + inH, null);
            }
          }
          _oldX = -1;
          _oldY = -1;
          this.Offset = loc;
          Output.Render(2);
          Input.Render(2);
        }
      }
      public override void Render(int chLevel) {
        int x, y;
        y = JsLib.OfInt(JsLib.GetField(model.Manifest, "Logram.top"), 0);
        x = JsLib.OfInt(JsLib.GetField(model.Manifest, "Logram.left"), 0);
        double width = 0;
        base.OriginalLocation = new Vector((1.0 + x / CELL_SIZE) * CELL_SIZE, (0.5 + y / CELL_SIZE) * CELL_SIZE);
        this.Offset = OriginalLocation;

        using(DrawingContext dc = this.RenderOpen()) {
          FormattedText ft = new FormattedText(model.name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LFont, CELL_SIZE * 0.7, Brushes.White);
          width = Math.Round((ft.WidthIncludingTrailingWhitespace + CELL_SIZE * 1.5) / CELL_SIZE, 0) * CELL_SIZE;
          dc.DrawRoundedRectangle(_selected ? Brushes.DarkOrange : Brushes.DarkSlateGray, null, new Rect(0, 1, width - 1, CELL_SIZE - 3), CELL_SIZE / 4, CELL_SIZE / 4);
          ft.MaxTextHeight = CELL_SIZE - 3;
          ft.MaxTextWidth = width - CELL_SIZE / 2 - 5;
          dc.DrawText(ft, new Point(5, 1));
        }
        if(chLevel == 3) {
          _oldY = x;
          _oldX = y;
          _oldH = (int)width / CELL_SIZE;
          for(int inH = _oldH; inH >= 0; inH--) {
            lv.MapSet(true, _oldX, _oldY + inH, this);
            lv.MapSet(false, _oldX, _oldY + inH, this);
          }
          lv.MapSet(false, _oldX, _oldY, Input);
          lv.MapSet(false, _oldX, _oldY + _oldH, Output);
        }

        if(chLevel > 1) {
          Output.SetLocation(new Vector(width, CELL_SIZE / 2), chLevel);
          Input.SetLocation(new Vector(0, CELL_SIZE / 2), chLevel);
        }
      }

      private void ModelChanged(DTopic.Art a, DTopic t) {
        switch(a) {
        case DTopic.Art.addChild:
          this.Render(2);
          break;
        case DTopic.Art.RemoveChild:
          this.Remove();
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
      private void Remove() {
        lv.DeleteVisual(Input);
        lv.DeleteVisual(Output);
        lv.DeleteVisual(this);
      }

      public override DTopic GetModel() {
        return model;
      }
    }

  }
}