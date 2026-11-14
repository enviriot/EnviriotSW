///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;

namespace X13.LogramPLC {
  /// <summary>То, что редактор диаграммы pa1 знает о блоках от компилятора, - в манифестах топиков.</summary>
  /// <remarks>
  /// <para>Возможные пины блока - каталог mi: у типа в LPLC.xst (mi.LPLC.mi.in/out), а выходы Func, которые
  /// пишет его src, - в собственном манифесте блока. Показанные пины - LPLC.in и LPLC.out самого блока.
  /// Так WebUI читает каталог через SchemaOverlay, как и Inspector, и не разбирает src сам.</para>
  /// <para>Пункты новых переменных в меню устройства пишутся отсюда же: без них переменную не создать
  /// до первой загрузки программы.</para></remarks>
  internal static class LP_Schema {
    /// <summary>Имя топика программы EspNode, см. DevicePLC.TargetOf.</summary>
    internal const string PA1 = "pa1";
    /// <summary>Тип топика программы LPLC (pa1), см. MsDevice.GetTopicInfo и LPLC.xst.</summary>
    /// <remarks>Свой, а не общий с pa0 Ext/DevicePLC: по одному типу pa1 узнают и MQTT_SN, и WebUI, без имени.</remarks>
    internal const string PLC_TYPE = "Ext/LogramPLC";
    /// <summary>Выходы Func по его src в манифесте блока; Q - в каталоге типа, здесь его нет.</summary>
    internal const string OUT_MI = "mi.LPLC.mi.out.mi";

    /// <summary>Топик программы LPLC: по типу, как его узнаёт и WebUI.</summary>
    internal static bool IsPa1(Topic t) {
      return t != null && t.GetField("type").AsString(null) == PLC_TYPE;
    }

    /// <summary>Пункты новых переменных в меню устройства - если их там нет или они устарели.</summary>
    /// <remarks>Нужны до первого Run: иначе переменную, которую ещё не на что подключить, нечем создать.
    /// Меню лежит в манифесте устройства и сохраняется в базе, поэтому пункт от прежней версии шлюза
    /// заменяется.</remarks>
    internal static void EnsureVarTemplates(Topic dev, Topic prim) {
      var cur = dev == null ? null : dev.GetField("Children");
      if(dev == null || (cur.Defined && !cur.IsNull && !cur.IsObject())) {
        return;   // строка-путь: меню устройства задано иначе, его не трогаем
      }
      var t = JSC.JSObject.CreateObject();
      LP_Program.AddVarTemplates(t);
      foreach(var kv in t) {
        if(!cur.IsObject() || JsLib.Stringify(cur.GetProperty(kv.Key)) != JsLib.Stringify(kv.Value)) {
          dev.SetField("Children." + kv.Key, kv.Value, prim);
        }
      }
    }

    /// <summary>Выходы Func, которые пишет его src, - в каталог mi его манифеста.</summary>
    /// <remarks>Пишется, только если каталог изменился. src с ошибкой каталог не трогает: пока его правят,
    /// пины на диаграмме не мигают.</remarks>
    /// <returns>true - каталог записан</returns>
    internal static bool SyncFunc(Topic block, Topic prim) {
      if(block == null || !X13.Plc.PlcScript.UsesState(block.GetField("type").AsString(null))) {
        return false;
      }
      string readOnlyField = block.GetField("type").AsString(null) == X13.Plc.PlcScript.SerialPort ? "Port" : null;
      var f = LP_DiagramBinder.ParseFunc(block.GetState().AsString(string.Empty), readOnlyField);
      if(f.Error != null) {
        return false;
      }
      JSC.JSValue mi = null;
      foreach(var o in f.Outputs.Where(z => z != "Q" && z != readOnlyField)) {
        if(mi == null) {
          mi = JSC.JSObject.CreateObject();
        }
        var d = JSC.JSObject.CreateObject();
        d["default"] = true;
        mi[o] = d;
      }
      var cur = block.GetField(OUT_MI);
      string have = cur.IsObject() ? JsLib.Stringify(cur) : null;
      string want = mi == null ? null : JsLib.Stringify(mi);
      if(have == want) {
        return false;
      }
      block.SetField(OUT_MI, mi ?? JSC.JSValue.Null, prim);
      return true;
    }

    /// <summary>Меню переменных устройства и каталоги выходов всех Func программы.</summary>
    internal static void Sync(Topic pa1) {
      EnsureVarTemplates(pa1.parent, pa1);
      foreach(var t in pa1.children) {
        SyncFunc(t, pa1);
      }
    }

    /// <summary>Следит за каждым pa1 дерева и держит его схему в порядке, см. Sync.</summary>
    /// <remarks>Свой, а не LP_Program: тот появляется, только когда узел зарегистрировал pa1, а диаграмму
    /// правят и без узла. Подписка на детей pa1 - на их State и тип: свои записи mi.* сюда не приходят.</remarks>
    internal sealed class Watcher : IDisposable {
      private readonly object _sync = new object();
      private readonly Dictionary<Topic, SubRec> _programs = new Dictionary<Topic, SubRec>();
      private SubRec _root;

      public Watcher(Topic root) {
        _root = root.Subscribe(SubRec.SubMask.Field | SubRec.SubMask.All, "type", RootEvent);
      }

      private void RootEvent(TopicEvent e, SubRec sr) {
        var t = e.Source;
        switch(e.Kind) {
        case EventKind.Snapshot:
        case EventKind.Created:
        case EventKind.FieldChanged:
        case EventKind.Moved:
          break;
        case EventKind.Removed:
          Forget(t);
          return;
        default:
          return;
        }
        if(!IsPa1(t) || t.disposed) {
          Forget(t);
          return;
        }
        lock(_sync) {
          if(_root == null || _programs.ContainsKey(t)) {
            return;
          }
          _programs[t] = t.Subscribe(SubRec.SubMask.Children | SubRec.SubMask.Value | SubRec.SubMask.Field, "type", BlockEvent);
        }
        EnsureVarTemplates(t.parent, t);
      }

      private void BlockEvent(TopicEvent e, SubRec sr) {
        switch(e.Kind) {
        case EventKind.Snapshot:
        case EventKind.Created:
        case EventKind.StateChanged:
        case EventKind.FieldChanged:
        case EventKind.Moved:
          if(e.Source.parent == sr.setTopic && !e.Source.disposed) {
            SyncFunc(e.Source, sr.setTopic);
          }
          break;
        }
      }

      private void Forget(Topic t) {
        SubRec s;
        lock(_sync) {
          if(!_programs.TryGetValue(t, out s)) {
            return;
          }
          _programs.Remove(t);
        }
        s.Dispose();
      }

      public void Dispose() {
        SubRec[] subs;
        lock(_sync) {
          subs = _programs.Values.ToArray();
          _programs.Clear();
          if(_root != null) {
            subs = subs.Concat(new[] { _root }).ToArray();
            _root = null;
          }
        }
        foreach(var s in subs) {
          s.Dispose();
        }
      }
    }
  }
}
