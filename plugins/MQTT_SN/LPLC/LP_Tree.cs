///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using X13.Periphery;
using X13.Repository;

namespace X13.LogramPLC {
  /// <summary>Диаграмма LPLC в дереве: блоки - дети pa1, переменные - M-топики устройства.</summary>
  /// <remarks>
  /// <para>Описание блока - в манифесте, поле LPLC: pos (позиция), args (аргументы конструктора), in (входы
  /// Tick). State переменной - её значение, синхронное с меркером узла, поэтому и описание переменной не
  /// может жить в State; у блоков State занят только телом Func.</para>
  /// <para>Вход в in: строка "Блок.выход" - выход блока из pa1, имя без точки - переменная устройства,
  /// число или true/false - константа. Переменная - любой ребёнок устройства с тегом M… поддерживаемого
  /// типа; её in.x делает её приёмником. Наблюдатель (имя "Блок_выход") отделяет ядро.</para>
  /// <para>Результат компиляции блока лежит в неперечислимом свойстве видимого пустого объекта LPLC.run.
  /// Его не видят ни база, ни IDE, ни xst, а запись в LPLC.pos или LPLC.in.* его не задевает: JsLib.SetField
  /// заново собирает только объекты на пути записи, а run им сосед и переходит по ссылке. Пропадает он
  /// при загрузке из базы и при записи самого LPLC/LPLC.run - и восстанавливается компиляцией.</para></remarks>
  internal static class LP_Tree {
    internal const string FLD = "LPLC";
    /// <summary>Префикс типов блоков: LPLC/Func, LPLC/Pin, остальные - блоки-выражения (LPLC/ADD).</summary>
    internal const string PREFIX = "LPLC/";
    internal const string FUNC = "LPLC/Func";
    internal const string PIN = "LPLC/Pin";
    internal const string TWI = "LPLC/TwiMaster";
    /// <summary>Папка типов блоков LPLC - меню Add у pa1.</summary>
    internal const string TYPES = "/$YS/TYPES/LPLC";
    /// <summary>Типы блоков LPLC в ресурсах сборки, см. ImportTypes.</summary>
    internal const string TYPES_RESOURCE = "X13.LogramPLC.LPLC.xst";
    /// <summary>Имя скрытого свойства в LPLC.run.</summary>
    private const string HIDDEN = "$plc";
    private static readonly System.Text.RegularExpressions.Regex UnboundMerker = new System.Text.RegularExpressions.Regex(@"^M[A-Za-z]$");

    /// <summary>Префикс тега меркера - тип переменной; только то, что умеет компилятор.</summary>
    internal static readonly IReadOnlyDictionary<string, LP_Diagram.VarType> VarTypes = new Dictionary<string, LP_Diagram.VarType>(StringComparer.Ordinal) {
      { "Mz", LP_Diagram.VarType.Boolean },
      { "Mb", LP_Diagram.VarType.Int8 },
      { "MB", LP_Diagram.VarType.UInt8 },
      { "Mw", LP_Diagram.VarType.Int16 },
      { "MW", LP_Diagram.VarType.UInt16 },
      { "Md", LP_Diagram.VarType.Int32 },
      { "Mf", LP_Diagram.VarType.Float },
    };

    /// <summary>Результат компиляции блока и программа, к которой он относится.</summary>
    internal sealed class Compiled {
      public LP_DiagramCompiler.Element Element;
      /// <summary>Отпечаток программы (Fingerprint): результат верен для узла, только если на нём она.</summary>
      public string Program;
    }

    /// <summary>Типы блоков LPLC - свой xst в ресурсах сборки, как base.xst у PersistentStorage.</summary>
    /// <remarks>Xst.Import пропускает версию не новее установленной, поэтому правка типа доезжает подъёмом
    /// ver.</remarks>
    internal static void ImportTypes() {
      using(var rs = typeof(LP_Tree).Assembly.GetManifestResourceStream(TYPES_RESOURCE)) {
        using(var reader = new System.IO.StreamReader(rs)) {
          Xst.Import(reader, null);
        }
      }
    }

    /// <summary>Тег-префикс без адреса ("Mf"): переменная LPLC, которой компиляция ещё не дала меркер.</summary>
    /// <remarks>Такой топик не регистрируется: узел отверг бы REGISTER без номера, и шлюз в ответ удалил бы
    /// топик. Полный тег приходит записью поля, см. MsDevice.PublishTopic.</remarks>
    internal static bool IsUnboundMerker(string tag) {
      return tag != null && UnboundMerker.IsMatch(tag);
    }

    /// <summary>Тип переменной по тегу; null - тег не переменной LPLC.</summary>
    internal static LP_Diagram.VarType? VarTypeOf(string tag) {
      LP_Diagram.VarType vt;
      return tag != null && tag.Length >= 2 && VarTypes.TryGetValue(tag.Substring(0, 2), out vt) ? vt : (LP_Diagram.VarType?)null;
    }

    /// <summary>Диаграмма pa1: блоки - его дети LPLC/*, переменные - M-топики устройства; Tag - топик.</summary>
    /// <remarks>Порядок - порядок детей, то есть по имени: от него зависят ничьи в порядке Tick и раскладка
    /// меркеров, поэтому одно дерево всегда даёт одну программу.
    /// <para>Блок-выражение берёт из своего типа (/$YS/TYPES/LPLC/ADD, поле LPLC) входы in и выражение expr
    /// или операцию op, а Block - полный класс src с constructor и Tick. Тип без реализации -
    /// тоже блок, чтобы компилятор сказал о нём на самом блоке, а не пропустил молча.</para></remarks>
    internal static LP_Diagram Read(Topic pa1) {
      var d = new LP_Diagram();
      foreach(var t in pa1.children) {
        var lplc = t.GetField(FLD);
        string type = t.GetField("type").AsString(null);
        switch(type) {
        case FUNC:
          d.AddFunc(t.name, t.GetState().AsString(string.Empty), t);
          break;
        case X13.Plc.PlcScript.SerialPort:
          var serialType = Topic.root.Get(TYPES + "/SerialPort", false);
          d.AddSerialPort(t.name, t.GetState().AsString(string.Empty), new X13.Plc.ConstructorInstance {
            Name = t.name, Schema = X13.Plc.ConstructorSchema.Read(serialType?.GetState()),
            Values = lplc.Field("args")
          }, t);
          break;
        case PIN:
          var a = lplc.Field("args");
          d.AddPin(t.name, Literal(a.Field("pin")), Literal(a.Field("mode")), Literal(a.Field("option")), t);
          break;
        default:
          if(!IsBlock(type)) {
            continue;
          }
          var tt = Topic.root.Get("/$YS/TYPES/" + type, false);
          var def = tt == null ? JSC.JSValue.Undefined : tt.GetState().Field(FLD);
          string src = def.AsString("src", null);
          string op = def.AsString("op", null);
          if(src != null) {
            var parameters = Names(def.Field("args"));
            d.AddBlock(t.name, src, Names(def.Field("in")), t, parameters,
              parameters.Select(p => p == null ? null : Literal(lplc.Field("args").Field(p))).ToArray());
            var schema = X13.Plc.ConstructorSchema.Read(tt.GetState());
            if(schema.HasMetadata || schema.Errors.Count > 0) d.Blocks.Last().Configuration = new X13.Plc.ConstructorInstance {
              Name = t.name, Schema = schema, Values = lplc.Field("args")
            };
          } else if(op != null) {
            d.AddOp(t.name, op, Names(def.Field("in")), t);
          } else {
            d.AddExpr(t.name, def.AsString("expr", null), Names(def.Field("in")), t);
          }
          break;
        }
        var inputs = lplc.Field("in");
        if(inputs.IsObject()) {
          foreach(var kv in inputs) {
            Link(d, kv.Value, t.name + "." + kv.Key);
          }
        }
      }
      if(pa1.parent != null) {
        foreach(var t in pa1.parent.children) {
          var vt = VarTypeOf(t.GetField("MQTT-SN.tag").AsString(null));
          if(vt == null) {
            continue;
          }
          d.AddVar(t.name, vt.Value, t);
          var x = t.GetField(FLD).Field("in").Field("x");
          if(x.Defined && !x.IsNull) {
            Link(d, x, t.name);
          }
        }
      }
      return d;
    }
    private static void Link(LP_Diagram d, JSC.JSValue from, string to) {
      string s = from.AsString(null);
      if(s != null) {
        if(s.Length > 0) {   // пустая строка - вход не подключён: так удобнее править руками
          d.Connect(s, to);
        }
      } else {
        string c = Literal(from);
        if(c != null) {
          d.SetConst(to, c);
        }
      }
    }
    /// <summary>Строки массива; не строка - null, его отвергнет компилятор.</summary>
    private static string[] Names(JSC.JSValue a) {
      var r = new List<string>();
      if(a.IsObject()) {
        foreach(var kv in a) {
          r.Add(kv.Value.AsString(null));
        }
      }
      return r.ToArray();
    }
    /// <summary>Значение манифеста текстом для программы: число, true/false или строка; иначе null.</summary>
    private static string Literal(JSC.JSValue v) {
      if(v == null) {
        return null;
      }
      switch(v.ValueType) {
      case JSC.JSValueType.Integer:
        return ((int)v).ToString(CultureInfo.InvariantCulture);
      case JSC.JSValueType.Double:
        return ((double)v).ToString("R", CultureInfo.InvariantCulture);
      case JSC.JSValueType.Boolean:
        return (bool)v ? "true" : "false";
      case JSC.JSValueType.String:
        return v.AsString(null);
      default:
        return null;
      }
    }

    /// <summary>Отпечаток программы: адреса и CRC её кусков - тем же CRC, которым DevicePLC сверяет узел.</summary>
    internal static string Fingerprint(SortedList<uint, byte[]> hex) {
      return hex == null ? null : string.Join(",", hex.Select(kv => kv.Key.ToString("X4") + ":" + Crc16.UpdateCrc(0xFFFF, kv.Value).ToString("X4")));
    }

    /// <summary>Каждому блоку - его результат в скрытое свойство LPLC.run.</summary>
    /// <remarks>Нет контейнера - он создаётся одной записью вместе со скрытым свойством: репозиторий хранит
    /// значение поля по ссылке, а в базу оно уйдёт пустым объектом.</remarks>
    internal static void Attach(LP_DiagramCompiler r, string program, Topic prim = null) {
      foreach(var el in r.Elements) {
        var t = el.Tag as Topic;
        if(t == null || !IsBlock(t)) {
          continue;
        }
        var info = new Compiled() { Element = el, Program = program };
        var run = t.GetField(FLD + ".run");
        if(run.IsObject()) {
          Hide(run, info);
        } else {
          JSC.JSValue c = JSC.JSObject.CreateObject();
          Hide(c, info);
          t.SetField(FLD + ".run", c, prim);
        }
      }
    }
    /// <summary>Результат последней компиляции блока; null - его нет (загрузка из базы, перезапись LPLC.run).</summary>
    internal static Compiled Info(Topic block) {
      var run = block.GetField(FLD + ".run");
      return run.IsObject() ? run.GetProperty(HIDDEN).Value as Compiled : null;
    }
    internal static bool IsBlock(Topic t) {
      return IsBlock(t.GetField("type").AsString(null));
    }
    private static bool IsBlock(string type) {
      return type != null && type.StartsWith(PREFIX, StringComparison.Ordinal);
    }

    /// <summary>Блок программы program, из которого построена команда по адресу pc, и строка его src.</summary>
    /// <param name="known">false - ни у одного блока нет результата этой программы: его надо восстановить</param>
    internal static Topic Locate(Topic pa1, uint pc, string program, out int line, out bool known) {
      known = false;
      line = 0;
      foreach(var t in pa1.children.Where(IsBlock)) {
        var info = Info(t);
        if(info == null || info.Program != program) {
          continue;
        }
        known = true;
        foreach(var r in info.Element.Code) {
          if(r.From <= pc && pc < r.To) {
            line = r.Line;
            return t;
          }
        }
      }
      return null;
    }

    /// <summary>Скрытое свойство: неперечислимое, поэтому его не видят ни JSON.stringify, ни обход
    /// PersistentStorage, ни копирование JsLib.SetField.</summary>
    private static void Hide(JSC.JSValue run, Compiled info) {
      var desc = JSC.JSObject.CreateObject();
      desc["value"] = JsExtLib.Context.ProxyValue(info);
      desc["enumerable"] = false;
      desc["writable"] = true;
      desc["configurable"] = true;
      var a = new JSC.Arguments();
      a.Add(run);
      a.Add(HIDDEN);
      a.Add(desc);
      JSC.JSObject.defineProperty(a);
    }
  }
}
