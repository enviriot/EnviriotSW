///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using X13.Data;
using System.Collections.ObjectModel;
using System.Windows;

namespace X13.UI {
  public partial class LogramForm : UserControl, IBaseForm {
    private ObservableCollection<LBDesc> _blocks;

    internal LogramForm(DTopic data) {
      _blocks = new ObservableCollection<LBDesc>();
      InitializeComponent();
      uiLogram.Attach(data, new Action(() => data.Connection.root.GetAsync("/$YS/TYPES/LoBlock").ContinueWith(this.LoBlockLoad, TaskScheduler.FromCurrentSynchronizationContext())));
      icBlocks.ItemsSource = _blocks;
    }

    private void LoBlockLoad(Task<DTopic> tt) {
      DTopic t;
      if(tt.IsFaulted || !tt.IsCompleted || (t=tt.Result)==null) {
        return;        
      }
      t.changed += LBDescrChanged;
      if(t.children != null) {
        foreach(var ch in t.children) {
          ch.GetAsync(null).ContinueWith(this.LoBlockLoad, TaskScheduler.FromCurrentSynchronizationContext());
        }
      }
      LBDescrChanged(DTopic.Art.addChild, t);
    }

    private void LBDescrChanged(DTopic.Art a, DTopic t) {
      if(t.Manifest == null || t.Manifest.ValueType != JSC.JSValueType.Object || t.Manifest.Value == null || (t.Manifest["type"].Value as string) != "Ext/LBDescr") {
        return;
      }
      LBDesc bl;
      lock(_blocks) {
        bl = _blocks.FirstOrDefault(z => z.owner == t);
      }
      if(a == DTopic.Art.RemoveChild) {
        if(bl != null) {
          lock(_blocks) {
            _blocks.Remove(bl);
          }
        }
      } else {
        if(bl == null) {
          bl = new LBDesc(t);
          lock(_blocks) {
            int i = 0;
            while(_blocks.Count > i && _blocks[i].owner.path.CompareTo(t.path)<=0) {
              i++;
            }
            _blocks.Insert(i, bl);
          }
        } else {
          bl.Update();
        }
      }

    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e) {
      uiLogram.ResetSelection(e);
      base.OnMouseLeftButtonDown(e);
    }

    private Image _selectedImage;
    private Point _MouseDownPoint;
    private bool _readyToDrag;
    private void WrapPanel_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e) {
      LBDesc tag;
      Point pos = e.GetPosition(this);
      if(_readyToDrag && _selectedImage != null && (System.Math.Abs(_MouseDownPoint.X - pos.X) > SystemParameters.MinimumHorizontalDragDistance || System.Math.Abs(_MouseDownPoint.Y - pos.Y) > SystemParameters.MinimumVerticalDragDistance) && (tag = _selectedImage.Tag as LBDesc) != null) {
        _readyToDrag = false;
        DragDrop.DoDragDrop(_selectedImage, tag.owner, DragDropEffects.Copy);
      }
    }
    private void WrapPanel_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
      if((_selectedImage = e.OriginalSource as Image) != null) {
        _MouseDownPoint = e.GetPosition(this);
        _readyToDrag = true;
      }
    }
    private void WrapPanel_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) {
      LBDesc tag;
      if(_selectedImage!=null && ( tag=_selectedImage.Tag as LBDesc )!=null) {
        _readyToDrag=false;
        if(uiLogram.Model!=null) {
          string name;
          string prefix = JsLib.OfString(tag.owner.State["namePrefix"], "U");
          if(uiLogram.Model.children != null) {
            int i = 1;
            do {
              name = prefix+i.ToString("D02");
              i++;
            } while(uiLogram.Model.children.Any(z => z.name == name));
          } else {
            name = prefix + "01";
          }
          uiLogram.Model.CreateAsync(name, tag.owner.State["default"], tag.owner.State["manifest"]);
        }
        _selectedImage=null;
      }

    }

    #region IDisposable Member
    public void Dispose() {
      foreach(var b in _blocks.ToArray()) {
        b.owner.changed -= LBDescrChanged;
      }
      _blocks.Clear();
      uiLogram.Dispose();
    }
    #endregion IDisposable Member


    internal class LBDesc {
      public readonly DTopic owner;

      public LBDesc(DTopic owner) {
        this.owner = owner;
        Update();
      }
      public void Update() {
        if(owner.State != null && owner.State.Value!=null && owner.State.ValueType==JSC.JSValueType.Object) {
          string tmp_s;

          var iv = owner.State["icon"];
          if(iv.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(tmp_s = iv.Value as string)) {
            Icon = App.GetIcon(tmp_s);
          }
          iv = owner.State["hint"];
          if(iv.ValueType == JSC.JSValueType.String && !string.IsNullOrEmpty(tmp_s = iv.Value as string)) {
            Info = tmp_s;
          }
        }
      }
      public BitmapSource Icon { get; private set; }
      public string Info { get; private set; }
    }
  }
}
