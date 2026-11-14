///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using JSC = NiL.JS.Core;
using System;
using X13.DevicePLC;
using X13.Repository;

namespace X13.LogramPLC {
  /// <summary>Программа pa1 - диаграмма LPLC: сборка, раскладка результата по топикам, меню переменных
  /// устройства и место ошибки узла.</summary>
  /// <remarks>Протокол загрузки, общий с рукописной программой pa0, остаётся у DevicePLC: он создаёт этот
  /// объект для EspNode и зовёт его там, где pa1 отличается.</remarks>
  internal sealed class LP_Program {
    private readonly Topic _pa1;
    private readonly EP_Target _target;

    /// <summary>Пункты переменных в меню устройства обновляются сразу, см. EnsureVarTemplates.</summary>
    public LP_Program(Topic pa1, EP_Target target) {
      _pa1 = pa1;
      _target = target;
      EnsureVarTemplates();
    }

    /// <summary>pa1: блоки - дети pa1, переменные - M-топики устройства, см. LP_Tree.</summary>
    /// <remarks>Проблемы - в журнал с путём блока и строкой src: в IDE их так и видно. Сгенерированный JS -
    /// туда же при verbosePlc, отладочной записью: она не копится в истории журнала, а в IDE видна по кнопке Debug.
    /// Результат каждого блока уходит в его скрытый контейнер вместе с отпечатком программы.</remarks>
    /// <param name="fingerprint">отпечаток собранной программы; null - не собралась</param>
    /// <returns>null - описание устройства негодно или в диаграмме ошибки</returns>
    public LP_DiagramCompiler Compile(bool log, out string fingerprint) {
      fingerprint = null;
      EP_Device device;
      try {
        device = LoadDevice(_pa1, log);
      }
      catch(FormatException ex) {
        if(log) Log.Error("{0} - device type {1}.{2}", _pa1.path, _pa1.parent.GetField("type").AsString(null), ex.Message);
        LP_Status.RecordBuilt(_pa1, null);
        return null;
      }
      var r = LP_DiagramCompiler.Compile(LP_Tree.Read(_pa1), _target, device);
      if(log) {
        if(X13.Periphery.MQTT_SNPl.verbosePlc && r.Source != null) {   // null - ошибка в связях, до генерации не дошло
          Log.Debug("{0} - generated JS:\n{1}", _pa1.path, r.Source.TrimEnd('\n'));
        }
        foreach(var p in r.Problems) {
          Log.Error("{0} - {1}", Where(p), p.Message);
        }
        foreach(var p in r.Warnings) {
          Log.Warning("{0} - {1}", Where(p), p.Message);
        }
      }
      if(!r.Ok) {
        LP_Status.RecordBuilt(_pa1, null);
        return null;
      }
      fingerprint = LP_Tree.Fingerprint(r.Hex);
      LP_Tree.Attach(r, fingerprint, _pa1);
      LP_Status.RecordBuilt(_pa1, fingerprint);
      return r;
    }

    private string Where(LP_DiagramCompiler.Problem p) {
      var t = p.Element == null ? null : p.Element.Tag as Topic;
      return (t == null ? _pa1.path : t.path) + (p.Line > 0 ? ":" + p.Line : string.Empty);
    }
    /// <summary>Пины и режимы узла: поле PLC в состоянии типа устройства (/$YS/TYPES/MQTT-SN/E1S10).</summary>
    /// <remarks>Читается при каждой сборке: описание могли импортировать после подключения узла.</remarks>
    private static EP_Device LoadDevice(Topic program, bool log) {
      string type = program.parent == null ? null : program.parent.GetField("type").AsString(null);
      var types = Topic.root.Get("$YS/TYPES", false);  // null until PersistentStorage seeds it
      Topic tt;
      JSC.JSValue plc;
      if(type == null || types == null || !types.Exist(type, out tt) || !(plc = tt.GetState().Field("PLC")).Defined) {
        if(log) Log.Warning("{0} - no PLC description in device type {1}: named pin modes and Pin are unavailable", program.path, type ?? "<unknown>");
        return null;
      }
      return EP_Device.Parse(plc, program.parent.GetField(X13.Plc.PlcCapabilities.PinsField),
        program.parent.GetField(X13.Plc.PlcCapabilities.UartsField));
    }

    /// <summary>pa1: тег каждой переменной и наблюдателя - по меркеру его элемента.</summary>
    /// <remarks>Записью поля, а не ReReg: новая переменная с тегом-префиксом (Mf) ещё не зарегистрирована, и
    /// ReReg удалил бы её как незнакомую. Смену тега MsDevice сам превращает в перерегистрацию. Ничего не
    /// удаляется: M-топик, которого нет в диаграмме, - переменная неподдерживаемого типа, а не устаревшая.</remarks>
    public void Retag(LP_DiagramCompiler r) {
      foreach(var el in r.Elements) {
        var t = el.Tag as Topic;
        string m;
        if(t == null || LP_Tree.IsBlock(t) || !el.Merkers.TryGetValue("value", out m)) {
          continue;
        }
        if(t.GetField("MQTT-SN.tag").AsString(null) != m) {
          t.SetField("MQTT-SN.tag", m, _pa1);
        }
      }
    }
    /// <summary>Пункты меню устройства для новых переменных LPLC - по типам меркеров.</summary>
    /// <remarks>Тег - только префикс: адрес назначит компиляция, а до того MsDevice такой топик не регистрирует.</remarks>
    public static void AddVarTemplates(JSC.JSValue ch) {
      foreach(var kv in LP_Tree.VarTypes) {
        bool b = kv.Value == LP_Diagram.VarType.Boolean;
        var mqo = JSC.JSObject.CreateObject();
        mqo["tag"] = kv.Key;
        var mo = JSC.JSObject.CreateObject();
        mo["MQTT-SN"] = mqo;
        mo["attr"] = (int)Topic.Attribute.DB;   // уставка переживает перезапуск сервера: после REGACK шлюз шлёт её узлу
        var o = JSC.JSObject.CreateObject();
        o["manifest"] = mo;
        o["default"] = b ? (JSC.JSValue)false : 0;
        o["type"] = b ? "Boolean" : kv.Value == LP_Diagram.VarType.Float ? "Double" : "Integer";
        o["menu"] = "LPLC";
        o["willful"] = true;
        o["hint"] = "New LPLC variable " + kv.Value + " (" + kv.Key + ")";
        ch[kv.Value.ToString()] = o;
      }
    }
    /// <summary>Пункты новых переменных в меню устройства, см. LP_Schema.EnsureVarTemplates.</summary>
    private void EnsureVarTemplates() {
      LP_Schema.EnsureVarTemplates(_pa1.parent, _pa1);
    }

    /// <summary>Отпечаток программы узла - в LPLC.loaded, обычное сохраняемое поле.</summary>
    /// <remarks>По нему STOP_RESP после перезапуска сервера переводит PC на блоки без единого запроса к узлу.</remarks>
    public void MarkLoaded(string fingerprint) {
      _pa1.SetField(LP_Tree.FLD + ".loaded", fingerprint, _pa1);
    }
    /// <summary>pa1: блок и строка src, из которых построена команда по адресу pc; пусто - сказать нечего.</summary>
    /// <remarks>Результатов нет (сервер перезапущен, LPLC.run переписан) - pa1 компилируется локально, без
    /// загрузки, и отпечаток сверяется с LPLC.loaded: не совпал - диаграмму правили после загрузки, и узел
    /// выполняет другую программу.</remarks>
    public string BlockAt(uint pc) {
      string loaded = _pa1.GetField(LP_Tree.FLD + ".loaded").AsString(null);
      if(loaded == null) {
        return string.Empty;
      }
      int line;
      bool known;
      var t = LP_Tree.Locate(_pa1, pc, loaded, out line, out known);
      if(!known) {
        string fp;
        if(Compile(false, out fp) == null || fp != loaded) {
          return " - the diagram has changed since the upload, the block is unknown";
        }
        t = LP_Tree.Locate(_pa1, pc, loaded, out line, out known);
      }
      return t == null ? string.Empty : " in " + t.path + (line > 0 ? ":" + line : string.Empty);
    }
  }
}
