///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;
using NiL.JS.Extensions;
using System.Collections.Concurrent;

namespace X13.Logram {
  /// <summary>Плагин Logram: создаёт объекты схемы и планирует передачу значений между ними.</summary>
  /// <remarks>Однократный обход в Start дополняет глобальную подписку на события Repository.
  /// Подготовка связей и вычисления разделены на две фазы; задания, чья позиция уже пройдена,
  /// переносятся на следующий тик.</remarks>
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 5)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Logram")]
  class LogramPl : IPlugModul {
    private const string OWNER_PATH = "/$YS/Logram";

    /// <summary>Поля, по которым Logram распознаёт блоки и привязки в дереве топиков.</summary>
    internal const string FLD_BLOCK = "Logram.block";
    internal const string FLD_BIND = "Logram.bind";
#if DEBUG
    private const bool VerboseDefault = true;
#else
    private const bool VerboseDefault = false;
#endif
    private Topic _owner;
    private IDisposable _allSub;
    private bool _verbose;
    private SubRec _verboseSR;
    private Dictionary<Topic, ILoItem> _items;
    private ConcurrentQueue<ILoItem> _TaskIn;
    private List<ILoItem> _TaskPr;
    private int _curIdx;

    public LogramPl() {
      _items = new Dictionary<Topic, ILoItem>();
      _TaskIn = new ConcurrentQueue<ILoItem>();
      _TaskPr = new List<ILoItem>();
      _curIdx = 0;
    }

    public bool verbose { get { return _verbose; } }

    #region IPlugModul Members
    public void Init() {
    }
    public void Start() {
      _verboseSR = JsExtLib.EnsureCfg(Owner, "verbose",
        Topic.Attribute.Required | Topic.Attribute.DB, v => _verbose = v, VerboseDefault);
      _allSub = Topic.Subscribe(SubFunc);
      // Включённый PersistentStorage восстанавливает дерево в своём Start, до Start Logram.
      // Подписка не повторяет события для существующих топиков, поэтому нужен начальный обход.
      // Родители обрабатываются раньше детей; конструктор блока собирает уже существующие пины.
      foreach(Topic t in Topic.root.all) {
        Claim(t);
      }
    }

    /// <summary>Создаёт объект схемы по признаку в манифесте топика или описании его типа.</summary>
    /// <remarks>Переменная регистрируется до разрешения привязки, чтобы кольцо источников
    /// находило уже созданный объект, а не порождало его повторно.</remarks>
    private bool Claim(Topic t) {
      if(_items.TryGetValue(t, out _)) return true;
      if(Names(t, FLD_BLOCK)) {
        _items.Add(t, new LoBlock(this, t));
        return true;
      }
      if(Names(t, FLD_BIND)) {
        var v = new LoVariable(this, t);
        _items.Add(t, v);
        v.ManifestChanged();
        return true;
      }
      return false;
    }


    /// <summary>Проверяет, затрагивает ли записанный путь интересующее поле.</summary>
    /// <remarks>Учитываются изменения вложенного поля и замена родительского объекта.
    /// Граница сегмента задаётся точкой: запись typeface не является изменением type.</remarks>
    private static bool Affects(string written, string field) {
      return written != null
        && (string.Equals(written, field, StringComparison.Ordinal)
          || written.StartsWith(field + ".", StringComparison.Ordinal)
          || field.StartsWith(written + ".", StringComparison.Ordinal));
    }

    /// <summary>Ищет определённое поле в манифесте экземпляра или состоянии его типа.</summary>
    /// <remarks>Проверяется наличие значения, а не его логическая истинность.
    /// В описаниях типов из base.xst эти поля находятся в State, не в Manifest.</remarks>
    private static bool Names(Topic t, string field) {
      if(t.GetField(field).Defined) {
        return true;
      }
      var jType = t.GetField("type");
      if(!jType.Is<string>() || jType.Value == null) {
        return false;
      }
      Topic types = Topic.root.Get("$YS/TYPES", false), tt;   // Чтение не создаёт отсутствующий каталог.
      if(types == null || !types.Exist(jType.Value as string, out tt)) {
        return false;
      }
      return tt.GetState().Field(field).Defined;
    }
    public void Tick() {
      ILoItem it;
      _curIdx = -1;
      while(_TaskIn.TryDequeue(out it)) {
        try {
          it.Tick1();
          if(it.Disposed) {
            _items.Remove(it.Owner);
          } else {
            EnqueuePr(it);
          }
        }
        catch(Exception ex) {
          Log.Warning("{0}.Tick1() - {1}", it.ToString(), ex.Message);
        }
      }
      _curIdx = 0;
      while(_curIdx< _TaskPr.Count) {
        it = _TaskPr[_curIdx++];
        if (it != null) {
          try {
            it.Tick2();
          }
          catch (Exception ex) {
            Log.Warning("{0}.Tick2() - {1}", it.ToString(), ex.Message);
          }
        }
      }
      _TaskPr.Clear();
      _curIdx = -1;
    }
    /// <summary>Снимает подписки и очищает очереди и реестр объектов схемы.</summary>
    /// <remarks>Подписка Repository снимается первой, чтобы новые события не пополняли
    /// очищаемую очередь. Завершение каждого отдельного блока здесь не вызывается.</remarks>
    public void Stop() {
      IDisposable allSub = _allSub;
      _allSub = null;
      if(allSub != null) {
        allSub.Dispose();
      }
      // Подпиской, возвращённой EnsureCfg, владеет вызывающий код.
      if(_verboseSR != null) {
        _verboseSR.Dispose();
        _verboseSR = null;
      }
      ILoItem drop;
      while(_TaskIn.TryDequeue(out drop)) {
      }
      _TaskPr.Clear();
      _items.Clear();
    }
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }

    public bool enabled {
      get {
        // Проверяем тип без подстановки значения: явно заданный false не требует инициализации.
        if(!Owner.GetState().Is<bool>()) {
          Owner.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
          Owner.SetState(true);
          return true;
        }
        return (bool)Owner.GetState();
      }
    }
    #endregion IPlugModul Members

    internal LoVariable GetVariable(Topic t) {
      LoVariable v;
      ILoItem it;
      if(_items.TryGetValue(t, out it) && ( v = it as LoVariable )!=null) {
        return v;
      }
      v=new LoVariable(this, t);
      _items[t] = v;
      v.ManifestChanged();
      return v;
    }
    internal void EnqueuePr(ILoItem it) {
      int idx = _TaskPr.BinarySearch(it);
      if(idx<0) {
        idx = ~idx;
        if(_curIdx <= idx) {
          _TaskPr.Insert(idx, it);
        } else {
          _TaskIn.Enqueue(it);
        }
      } else if(_curIdx >= idx) {
        _TaskIn.Enqueue(it);
      }
    }
    internal void EnqueueIn(ILoItem it) {
      _TaskIn.Enqueue(it);
    }

    private void SubFunc(TopicEvent p) {
      ILoItem it;
      if(!_items.TryGetValue(p.Source, out it)) {
        // Тип и признаки могут появиться как при создании топика, так и при последующей правке.
        // После Claim новый топик всё ещё нужно подключить как пин родительского блока.
        if(p.Kind == EventKind.Created || p.Kind == EventKind.FieldChanged) {
          Claim(p.Source);
        }
        if(p.Kind==EventKind.Created) {
          ILoItem parent;
          LoBlock lb;
          if(p.Source.parent!=null && _items.TryGetValue(p.Source.parent, out parent) && ( lb = parent as LoBlock )!=null) {
            lb.GetPin(p.Source);
          }
        }
        return;
      }
      if(p.Kind==EventKind.StateChanged) {
        it.SetValue(p.Source.GetState(), p.Author);
      } else if(p.Kind==EventKind.Removed) {
        _TaskIn.Enqueue(it);
      } else if(p.Kind==EventKind.FieldChanged) {
        // Координаты и трассировка не требуют перепривязки. У выходного пина источник назначен
        // блоком, а собственного Logram.bind может не быть: лишний ManifestChanged сбросил бы Source.
        var lb = it as LoBlock;
        if(lb != null) {
          if(Affects(p.FieldPath, "type") || Affects(p.FieldPath, FLD_BLOCK)) {
            lb.ManifestChanged();
          }
        } else {
          var lv = it as LoVariable;
          if(lv != null && Affects(p.FieldPath, FLD_BIND)) {
            lv.ManifestChanged();
          }
        }
      }
    }
  }
}
