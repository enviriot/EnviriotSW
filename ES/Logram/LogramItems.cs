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

      private Vector _ownerOffset;
      //private List<uiWire> _connections;
      //private uiTracer _tracer;
      public SchemaElement owner { get; private set; }
      /// <summary>0 - bidirectional, 1 - input, 2 - output</summary>
      private DTopic model;

      public uiPin(SchemaElement owner, DTopic model) {
        this.owner = owner;
        this.model = model;
        //_connections = new List<uiWire>();
        this.brush = Brushes.LightGray;
      }

      public Brush brush { get; private set; }

      public void SetLocation(Vector center, int chLevel) {
        _ownerOffset = center;
        Render(chLevel);
      }
      //public void AddConnection(uiWire w) {
      //  _connections.Add(w);
      //  model.saved = false;
      //}
      //public void RemoveConnection(uiWire w) {
      //  _connections.Remove(w);
      //  if(_connections.Count == 0) {
      //    model.saved = true;
      //  }
      //}
      //public void SetTracer(uiTracer tr) {
      //  if(_tracer != null) {
      //    _tracer.GetModel().Remove();
      //  }
      //  _tracer = tr;
      //}
      public override void Render(int chLevel) {
        if(model == null) {
          return;
        }
        this.Offset = owner.Offset + _ownerOffset;
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
          this.brush = (bool)model.State.Value?Brushes.Lime:Brushes.DarkGray;
          break;
        default:
          this.brush = Brushes.LightGray;
          break;
        }
        using(DrawingContext dc = this.RenderOpen()) {
          dc.DrawEllipse(this.brush, _selected ? SelectionPen : null, new Point(0, 0), 3, 3);
        }
        //if(chLevel > 0) {
        //  foreach(uiWire w in _connections.ToArray()) {
        //    w.Render(chLevel);
        //  }
        //}
        //if(_tracer != null) {
        //  _tracer.Render(chLevel);
        //}
      }
      public override DTopic GetModel() {
        return model;
      }
    }

    internal abstract class SchemaElement : uiItem {
      public Vector OriginalLocation { get; protected set; }
      public abstract void SetLocation(Vector loc, bool save);
    }

    internal class uiAlias : SchemaElement {
      private LogramView _owner;
      public uiPin _pin { get; private set; }
      private int _oldX = -1;
      private int _oldY = -1;
      private int _oldH = 0;

      public readonly DTopic model;

      public uiAlias(DTopic model, LogramView owner) {
        this.model = model;
        _owner = owner;
        this._pin = new uiPin(this, model);
        Render(3);
        _owner.AddVisual(this);
        _owner.AddVisual(_pin);
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
            //for(int inH = _oldH; inH >= 0; inH--) {
            //  _owner.MapSet(true, _oldX, _oldY + inH, null);
            //  _owner.MapSet(false, _oldX, _oldY + inH, null);
            //}
          }
          _oldX = -1;
          _oldY = -1;
          this.Offset = loc;
          _pin.Render(2);
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
          dc.DrawRoundedRectangle(_selected ? Brushes.DarkOrange: Brushes.DarkSlateGray, null, new Rect(1, 1, width - 3, CELL_SIZE-3), CELL_SIZE / 4, CELL_SIZE / 4);
          ft.MaxTextHeight = CELL_SIZE - 3;
          ft.MaxTextWidth = width - CELL_SIZE / 2 - 5;
          dc.DrawText(ft, new Point(5, 1));
        }
        if(chLevel == 3) {
          _oldY = x;
          _oldX = y;
          _oldH = (int)width / CELL_SIZE;
          //for(int inH = _oldH; inH >= 0; inH--) {
          //  _owner.MapSet(true, _oldX, _oldY + inH, this);
          //  _owner.MapSet(false, _oldX, _oldY + inH, this);
          //}
          //_owner.MapSet(true, _oldX, _oldY + _oldH, _pin);
        }

        if(chLevel > 1) {
          _pin.SetLocation(new Vector(width - 3, CELL_SIZE / 2), chLevel);
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
          this.Render(2);
          break;
        case DTopic.Art.type:
          this.Render(3);
          break;
        }
      }
      private void Remove() {
        _owner.DeleteVisual(_pin);
        _owner.DeleteVisual(this);
      }

      public override DTopic GetModel() {
        return model;
      }
    }

  }
}