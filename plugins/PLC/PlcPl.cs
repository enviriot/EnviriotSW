///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13.PLC {
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 5)]
  [System.ComponentModel.Composition.ExportMetadata("name", "PLC")]
  class PlcPl : IPlugModul {
    private Topic _owner;
    private Topic _verbose;
    private SubRec _subMs;
    private Dictionary<Topic, IPlcItem> _items;

    public PlcPl() {
      _items = new Dictionary<Topic, IPlcItem>();
    }

    public bool verbose {
      get {
        return _verbose != null && (bool)_verbose.GetState();
      }
    }

    #region IPlugModul Members
    public void Init() {

    }

    public void Start() {
      _owner = Topic.root.Get("/$YS/PLC");
      _verbose = _owner.Get("verbose");
      if(_verbose.GetState().ValueType != JSC.JSValueType.Boolean) {
        _verbose.SetAttribute(Topic.Attribute.Required | Topic.Attribute.DB);
#if DEBUG
        _verbose.SetState(true);
#else
        _verbose.SetState(false);
#endif
      }
      _subMs = Topic.root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "PLC.tag", SubFunc);
    }

    public void Tick() {

    }

    public void Stop() {

    }

    public bool enabled {
      get {
        var en = Topic.root.Get("/$YS/PLC", true);
        if(en.GetState().ValueType != JSC.JSValueType.Boolean) {
          en.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          en.SetState(true);
          return true;
        }
        return (bool)en.GetState();
      }
      set {
        Topic.root.Get("/$YS/PLC", true).SetState(value);
      }
    }
    #endregion IPlugModul Members

    private void SubFunc(Perform p, SubRec sb) {
      int tag;
      {
        var tag_v = p.src.GetField("PLC.tag");
        if(!tag_v.IsNumber) {
          tag = 0;
        } else {
          tag =(int)tag_v;
        }
      }
      if(p.art == Perform.Art.subscribe || p.art == Perform.Art.create) {
        switch(tag) {
        case 1:
          _items[p.src] = new Binding(this, p.src);
          break;
        }
      }
    }
  }
}
