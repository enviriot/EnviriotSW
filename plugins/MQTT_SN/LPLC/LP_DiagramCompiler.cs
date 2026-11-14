///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using NiL.JS;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using X13.DevicePLC;
using static X13.LogramPLC.LP_DiagramBinder;

namespace X13.LogramPLC {
  /// <summary>Ядро DeviceLogram: диаграмма - программа на JS - байткод через EP_Compiler.</summary>
  /// <remarks>Стадии: LP_DiagramBinder - что с чем связано, LP_DiagramGenerator - текст программы и карта
  /// его строк, здесь - EP_Compiler и раскладка результата по элементам.
  /// <para>Объект одноразовый: одна диаграмма, один Compile. Результат раздаётся по элементам (Element) -
  /// проблемы, адреса команд и меркеры каждого блока и каждой переменной; слой дерева кладёт их в
  /// скрытый контейнер топика элемента. EP_Compiler с деревом разбора после Compile не держится.</para></remarks>
  internal sealed class LP_DiagramCompiler {
    /// <summary>Ошибка или предупреждение и место в диаграмме.</summary>
    internal sealed class Problem {
      /// <summary>Блок или переменная; null - диаграмма целиком.</summary>
      public Element Element;
      /// <summary>Строка src блока Func; 0 - не строка src.</summary>
      public int Line;
      public string Message;
      public bool IsWarning;
      public override string ToString() {
        return (Element == null ? "diagram" : Element.Name) + (Line > 0 ? ":" + Line : string.Empty) + " - " + Message;
      }
    }
    /// <summary>Команды по адресам [From, To), построенные из строки Line src блока (0 - не из src).</summary>
    internal struct CodeRange {
      public uint From;
      public uint To;
      public int Line;
    }
    /// <summary>Результат по блоку или переменной диаграммы.</summary>
    internal sealed class Element {
      public string Name;
      /// <summary>Tag блока или переменной диаграммы - её топик.</summary>
      public object Tag;
      /// <summary>Ошибки и предупреждения элемента.</summary>
      public readonly List<Problem> Problems = new List<Problem>();
      /// <summary>Адреса команд блока - для PC из STOP_RESP. У переменной пусто: она объявляется без
      /// начального значения и кода не даёт.</summary>
      public readonly List<CodeRange> Code = new List<CodeRange>();
      /// <summary>Меркер каждого выхода блока ("Q", "M", "value"); у переменной - "value", у наблюдателя
      /// ("Блок_выход") - меркер того выхода, который он показывает.</summary>
      public readonly SortedList<string, string> Merkers = new SortedList<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Место от Where() и CompilerMessageCallback: "[строка, столбец] ".</summary>
    private static readonly Regex AtStart = new Regex(@"^\[(\d+), \d+\] ");
    /// <summary>Место синтаксической ошибки NiL: "(строка:столбец)" в конце.</summary>
    internal static readonly Regex AtEnd = new Regex(@"\((\d+):\d+\)\s*$");

    /// <summary>Текст программы и карта его строк; null - до генерации не дошло.</summary>
    private LP_DiagramGenerator _program;

    public bool Ok { get; private set; }
    /// <summary>Программа для загрузки; null - до неё не дошло.</summary>
    public SortedList<uint, byte[]> Hex { get; private set; }
    public uint StackBottom { get; private set; }
    /// <summary>Текст программы, сгенерированный по диаграмме, - для отладки.</summary>
    public string Source { get; private set; }
    /// <summary>Блоки, затем переменные - в порядке диаграммы.</summary>
    public List<Element> Elements { get; private set; }
    /// <summary>Все ошибки, в том числе ошибки элементов.</summary>
    public List<Problem> Problems { get; private set; }
    /// <summary>Предупреждения NiL на строках src (недостижимый код, присваивание необъявленной и т. п.)
    /// и связи на входы, которых src не трогает.</summary>
    public List<Problem> Warnings { get; private set; }
    /// <summary>Меркеры под именами диаграммы: "F1.Q", "F1.M", "P1.value", "V1".</summary>
    public SortedList<string, string> Variables { get; private set; }

    private LP_DiagramCompiler() {
      Elements = new List<Element>();
      Problems = new List<Problem>();
      Warnings = new List<Problem>();
      Variables = new SortedList<string, string>(StringComparer.Ordinal);
    }

    public static LP_DiagramCompiler Compile(LP_Diagram d, EP_Target target, EP_Device device) {
      var r = new LP_DiagramCompiler();
      r.Run(d, target, device);
      return r;
    }

    /// <summary>Блок, из которого построена команда по адресу pc (PC из STOP_RESP), и строка его src.</summary>
    public Element ElementAt(uint pc, out int line) {
      foreach(var e in Elements) {
        foreach(var r in e.Code) {
          if(r.From <= pc && pc < r.To) {
            line = r.Line;
            return e;
          }
        }
      }
      line = 0;
      return null;
    }

    private void Run(LP_Diagram d, EP_Target target, EP_Device device) {
      var b = LP_DiagramBinder.Bind(d, device, Elements, Add);
      if(Problems.Count > 0) {
        return;
      }
      _program = LP_DiagramGenerator.Generate(b);
      Source = _program.Source;
      var c = new EP_Compiler(target, device);
      c.CMsg += (level, coords, message) => {
        var at = Place(coords.Line);
        if(level == MessageLevel.Error) {
          Add(at.Item1, at.Item2, message);
        } else if(( level == MessageLevel.Warning || level == MessageLevel.CriticalWarning ) && at.Item2 > 0) {
          // Только строки src: return this.Q после return пользователя - "Unreachable code detected"
          // на строке, которую добавил генератор.
          Add(at.Item1, at.Item2, message, true);
        }
      };
      if(!c.Parse(Source)) {
        AddFromMessage(c.Error ?? "compilation failed");
        return;
      }
      Hex = c.Hex;
      StackBottom = c.StackBottom;
      foreach(var cl in c.CodeLines) {
        var at = Place(cl.Line);
        if(at.Item1 == null) {
          continue;
        }
        var code = at.Item1.Code;
        int last = code.Count - 1;
        if(last >= 0 && code[last].To == cl.Addr && code[last].Line == at.Item2) {
          code[last] = new CodeRange() { From = code[last].From, To = cl.End, Line = at.Item2 };
        } else {
          code.Add(new CodeRange() { From = cl.Addr, To = cl.End, Line = at.Item2 });
        }
      }
      foreach(var kv in c.varList) {   // "_b1.M" - элемент F1, выход M; "_x.e1" - выход Q выражения
        int dot = kv.Key.IndexOf('.');
        string id = dot < 0 ? kv.Key : kv.Key.Substring(0, dot);
        string pin = dot < 0 ? "value" : kv.Key.Substring(dot + 1);
        if(id == Holder) {
          id = pin;
          pin = "Q";
        }
        Element el;
        if(b.Ids.TryGetValue(id, out el)) {
          el.Merkers[pin] = kv.Value;
          Variables[dot < 0 ? el.Name : el.Name + "." + pin] = kv.Value;
        }
      }
      foreach(var kv in b.Observed) {   // наблюдатель - меркер того выхода, который он показывает
        string m;
        if(kv.Value.Item2.El.Merkers.TryGetValue(kv.Value.Item3, out m)) {
          kv.Value.Item1.Merkers["value"] = m;
          Variables[kv.Key] = m;
        }
      }
      Ok = Problems.Count == 0;
    }
    /// <summary>Строка программы - элемент и строка его src, см. LP_DiagramGenerator.Place.</summary>
    private Tuple<Element, int> Place(int line) {
      return _program.Place(line);
    }
    /// <summary>Ошибка EP_Compiler: место - префикс "[строка, столбец] " или "(строка:столбец)" в конце.</summary>
    private void AddFromMessage(string message) {
      var m = AtStart.Match(message);
      if(m.Success) {
        var at = Place(int.Parse(m.Groups[1].Value));
        Add(at.Item1, at.Item2, message.Substring(m.Length));
        return;
      }
      m = AtEnd.Match(message);
      if(m.Success) {
        var at = Place(int.Parse(m.Groups[1].Value));
        Add(at.Item1, at.Item2, message);
      } else {
        Add(null, 0, message);
      }
    }
    private void Add(Element el, int line, string message, bool warning = false) {
      var p = new Problem() { Element = el, Line = line, Message = message, IsWarning = warning };
      if(el != null) {
        el.Problems.Add(p);
      }
      (warning ? Warnings : Problems).Add(p);
    }
  }
}
