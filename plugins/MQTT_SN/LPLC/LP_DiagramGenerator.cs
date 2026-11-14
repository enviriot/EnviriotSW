///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.DevicePLC;
using static X13.LogramPLC.LP_DiagramBinder;
using static X13.LogramPLC.LP_DiagramCompiler;

namespace X13.LogramPLC {
  /// <summary>Вторая стадия LP_DiagramCompiler: программа на JS из связанной диаграммы и карта её строк -
  /// элемент и строка его src, по которой сообщения и адреса EP_Compiler возвращаются на блоки.</summary>
  /// <remarks>Блок-выражение (Expr: выражение по входам или операция между подключёнными, как ADD) своего Tick
  /// не имеет: выражение встаёт прямо в цикл, а его
  /// выход - поле eN общего объекта _x. Тип поля EP_Compiler выводит по имени поля, так что у каждого блока
  /// свой, а пустой конструктор у _x один на программу. Свой класс на блок стоил бы по 32 байта на пустой
  /// конструктор, а общий класс с полем Q - один тип Q на все блоки: целый ADD стал бы float.</remarks>
  internal sealed class LP_DiagramGenerator {
    internal static readonly string[] SerialParameters = { "Port", "TxPin", "TxMode", "RxPin", "RxMode", "Baud", "Format", "DePin", "DeMode" };
    /// <summary>Операции блока-операции и их нейтральное значение - Q, когда не подключён ни один вход.</summary>
    internal static readonly Dictionary<string, string> OpIdentity = new Dictionary<string, string>(StringComparer.Ordinal) {
      { "+", "0" }, { "*", "1" }, { "&", "-1" }, { "|", "0" }, { "^", "0" }, { "&&", "true" }, { "||", "false" },
      { "-", "0" },
    };
    /// <summary>Операции, у которых нейтральное значение только справа: x - 0 = x, но 0 - x - не x. Выбросить
    /// неподключённый первый вход значило бы сделать уменьшаемым первый подключённый, поэтому первый вход
    /// подставляется всегда, неподключённый - нейтральным значением. У SUB A - всегда уменьшаемое, как у
    /// серверного arSUB.</summary>
    private static readonly HashSet<string> HeadOps = new HashSet<string>(StringComparer.Ordinal) { "-" };
    /// <summary>Логические операции: их результат приводится к 1/0. && и || в EP_Compiler, как в JS, дают
    /// операнд (5 && 3 - 3), а логический элемент - истину или ложь.</summary>
    private static readonly HashSet<string> LogicOps = new HashSet<string>(StringComparer.Ordinal) { "&&", "||" };

    /// <summary>Строка программы - элемент и строка его src.</summary>
    private readonly List<Tuple<Element, int>> _lines = new List<Tuple<Element, int>>();
    /// <summary>Внутреннее имя (_v1) - элемент: строка объявления переменной относится к ней.</summary>
    private Dictionary<string, Element> _ids;

    /// <summary>Текст программы.</summary>
    public string Source { get; private set; }

    private LP_DiagramGenerator() {
    }

    public static LP_DiagramGenerator Generate(LP_DiagramBinder b) {
      var g = new LP_DiagramGenerator() { _ids = b.Ids };
      g.Source = g.Generate(b.Nodes, b.ConstructorOrder, b.Order, b.VarDefs, b.VarIds);
      return g;
    }

    private string Generate(List<Node> nodes, List<Node> constructors, List<Node> order, List<LP_Diagram.Variable> vars, Dictionary<string, string> ids) {
      var sb = new StringBuilder();
      foreach(var n in constructors.Where(z => z.Def.Kind == LP_Diagram.BlockKind.Func || z.Def.Kind == LP_Diagram.BlockKind.SerialPort || z.Def.Kind == LP_Diagram.BlockKind.Block)) {
        string cls = "_c" + n.Id.Substring(2);
        if(n.Def.Kind == LP_Diagram.BlockKind.Block) {
          var lines = n.Code.Split('\n');
          for(int i = 0; i < lines.Length; i++) Emit(sb, lines[i], n.El, i + 1);
        } else {
          Emit(sb, "class " + cls + " {", n.El, 0);
          if(n.Def.Kind == LP_Diagram.BlockKind.SerialPort) {
            Emit(sb, "  constructor(" + string.Join(", ", SerialParameters) + ") {", n.El, 0);
            Emit(sb, "    this.Port = Port;", n.El, 0);
            Emit(sb, "    ConfigPin(TxPin, TxMode, Baud);", n.El, 0);
            Emit(sb, "    ConfigPin(RxPin, RxMode, Format);", n.El, 0);
            Emit(sb, "    if(DePin >= 0) ConfigPin(DePin, DeMode, 0);", n.El, 0);
            Emit(sb, "  }", n.El, 0);
          }
          Emit(sb, "  Tick(" + string.Join(", ", FuncInputs.Take(n.Arity)) + ") {", n.El, 0);
          var lines = n.Code.Split('\n');
          int own = (n.Def.Src ?? string.Empty).Split('\n').Length;   // строки src без добавленного return this.Q
          for(int i = 0; i < lines.Length; i++) {
            Emit(sb, lines[i], n.El, i < own ? i + 1 : 0);
          }
          Emit(sb, "  }", n.El, 0);
          Emit(sb, "}", n.El, 0);
        }
        Emit(sb, "var " + n.Id + " = new " + cls + "(" + string.Join(", ", n.ConstructorArgs) + ");", n.El, 0);
      }
      if(nodes.Any(z => z.Def.Kind == LP_Diagram.BlockKind.Expr)) {
        Emit(sb, "class " + HolderClass + " {", null, 0);
        Emit(sb, "}", null, 0);
        Emit(sb, "var " + Holder + " = new " + HolderClass + "();", null, 0);
      }
      foreach(var n in nodes.Where(z => z.Def.Kind == LP_Diagram.BlockKind.Pin)) {
        var b = n.Def;
        Emit(sb, "var " + n.Id + " = new Pin(" + b.Pin + ", " + b.Mode + (b.Option != null ? ", " + b.Option : string.Empty) + ");", n.El, 0);
      }
      foreach(var v in vars) {
        // Без начального значения: VP2 тогда ничего не пишет в init, и START не затирает уставку,
        // которую шлюз уже записал в меркер.
        string id = ids[v.Name];
        Emit(sb, "var " + id + " = new " + v.Type + "();", _ids[id], 0);
      }
      foreach(var n in order) {
        if(n.Def.Kind == LP_Diagram.BlockKind.Func || n.Def.Kind == LP_Diagram.BlockKind.SerialPort) {
          // Все параметры явно: что в тексте, то и на стеке. Вход, которого src не трогает, но который стоит
          // перед нужным, получает 0 - LDI_0 короче загрузки меркера, а значение всё равно не читается.
          var args = Enumerable.Range(0, n.Arity).Select(i => n.Used[i] ? (n.Args[i] ?? "0") : "0");
          Emit(sb, n.Id + ".Q = " + n.Id + ".Tick(" + string.Join(", ", args) + ");", n.El, 0);
        } else if(n.Def.Kind == LP_Diagram.BlockKind.Block) {
          Emit(sb, n.Id + ".Tick(" + string.Join(", ", n.Args.Select(a => a ?? "0")) + ");", n.El, 0);
        } else if(n.Def.Kind == LP_Diagram.BlockKind.Expr) {
          // Комментарий // в выражении съел бы закрывающую скобку - тогда она уходит на свою строку
          string close = n.Def.Expr != null && n.Def.Expr.Contains("//") ? "\n);" : ");";
          foreach(var line in (n.Out("Q") + " = (" + (n.Def.Op != null ? Fold(n) : Substitute(n)) + close).Split('\n')) {
            Emit(sb, line, n.El, 0);
          }
        } else if(n.Io != EP_Device.PinIo.Out) {
          Emit(sb, n.Id + ".Tick();", n.El, 0);
        } else if(n.Args[0] != null) {   // выход без источника не пишется: пин остаётся в 0
          Emit(sb, n.Id + ".Tick(" + n.Args[0] + ");", n.El, 0);
        }
        foreach(var s in n.Sinks) {
          Emit(sb, s, n.El, 0);
        }
      }
      return sb.ToString();
    }
    /// <summary>Выражение, где на месте каждой ссылки на вход - его источник в скобках, 0 - вход не подключён.</summary>
    private static string Substitute(Node n) {
      var sb = new StringBuilder(n.Def.Expr);
      foreach(var r in n.Refs.OrderByDescending(z => z.Item1)) {
        sb.Remove(r.Item1, r.Item2).Insert(r.Item1, "(" + (n.Args[r.Item3] ?? "0") + ")");
      }
      return sb.ToString();
    }
    /// <summary>Операция между подключёнными входами по порядку входов; ни одного - нейтральное значение.</summary>
    /// <remarks>Неподключённый вход в программу не попадает: в шаблоне "A + B + … + H" каждый стоил бы
    /// LDI_0 и сложения. Исключение - первый вход HeadOps, см. там.</remarks>
    private static string Fold(Node n) {
      var args = new List<string>();
      for(int i = 0; i < n.Args.Length; i++) {
        if(n.Args[i] != null) {
          args.Add("(" + n.Args[i] + ")");
        } else if(i == 0 && HeadOps.Contains(n.Def.Op)) {
          args.Add("(" + OpIdentity[n.Def.Op] + ")");
        }
      }
      if(args.Count == 0) {
        return OpIdentity[n.Def.Op];
      }
      string s = string.Join(" " + n.Def.Op + " ", args);
      return LogicOps.Contains(n.Def.Op) ? "!!(" + s + ")" : s;
    }
    private void Emit(StringBuilder sb, string line, Element el, int srcLine) {
      sb.Append(line).Append('\n');
      _lines.Add(Tuple.Create(el, srcLine));
    }

    /// <summary>Строка программы - элемент и строка его src; вне программы - (null, 0).</summary>
    public Tuple<Element, int> Place(int line) {
      return line >= 1 && line <= _lines.Count ? _lines[line - 1] : Tuple.Create((Element)null, 0);
    }
  }
}
