///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Collections.Generic;

namespace X13.LogramPLC {
  /// <summary>Диаграмма DeviceLogram: блоки, переменные и связи - вход LP_DiagramCompiler.</summary>
  /// <remarks>Конец связи - строка "Блок.пин" или имя переменной: так её проще сопоставить с Logram.bind,
  /// когда диаграмма будет читаться из дерева. Порядок блоков разрешает ничьи в порядке Tick, поэтому
  /// одна диаграмма всегда даёт один байткод.</remarks>
  internal sealed class LP_Diagram {
    internal enum BlockKind {
      Func,
      SerialPort,
      /// <summary>Класс с конструктором и Tick: выходы - поля экземпляра, не возвращаемое значение.</summary>
      Block,
      Pin,
      /// <summary>Блок без состояния, выход Q: выражение по входам (SUB: "A - B") или операция между
      /// подключёнными входами (ADD: "+").</summary>
      Expr,
    }
    /// <summary>Тип переменной - тип меркера компилятора; имя совпадает с конструктором (new Float()).</summary>
    internal enum VarType {
      Boolean,
      Int8,
      UInt8,
      Int16,
      UInt16,
      Int32,
      Float,
    }

    internal sealed class Block {
      public string Name;
      /// <summary>Объект, к которому относится блок, - топик блока; ядро его не читает, только передаёт в
      /// результат. Так ядро не зависит от дерева, а тесты обходятся без него.</summary>
      public object Tag;
      public BlockKind Kind;
      /// <summary>Func: тело функции с входами A..H, return - Q. Block: полный класс с constructor и Tick.</summary>
      public string Src;
      /// <summary>Pin: аргументы new Pin - число или имя из описания устройства; Option может быть null.</summary>
      public string Pin, Mode, Option;
      /// <summary>Expr: выражение JS по входам Inputs; его значение - выход Q.</summary>
      public string Expr;
      /// <summary>Expr: операция между подключёнными входами вместо выражения (ADD: "+").</summary>
      public string Op;
      public string[] Inputs;
      public string[] Parameters;
      public string[] Arguments;
      public X13.Plc.ConstructorInstance Configuration;
    }
    /// <summary>Переменная лограммы: меркер, через который программа обменивается с Enviriot.</summary>
    internal sealed class Variable {
      public string Name;
      /// <summary>Топик переменной, см. Block.Tag.</summary>
      public object Tag;
      public VarType Type;
    }
    internal sealed class Link {
      /// <summary>Выход блока ("F1.Q", "F1.M", "P1.value") или переменная; null - константа Const.</summary>
      public string From;
      /// <summary>Константа на входе: число или true/false.</summary>
      public string Const;
      /// <summary>Вход блока ("F1.B", "P2.x") или переменная.</summary>
      public string To;
    }

    public readonly List<Block> Blocks = new List<Block>();
    public readonly List<Variable> Variables = new List<Variable>();
    public readonly List<Link> Links = new List<Link>();

    public LP_Diagram AddFunc(string name, string src, object tag = null) {
      Blocks.Add(new Block() { Name = name, Tag = tag, Kind = BlockKind.Func, Src = src });
      return this;
    }
    public LP_Diagram AddSerialPort(string name, string src, X13.Plc.ConstructorInstance configuration, object tag = null) {
      Blocks.Add(new Block { Name = name, Src = src, Configuration = configuration, Tag = tag, Kind = BlockKind.SerialPort });
      return this;
    }
    public LP_Diagram AddBlock(string name, string src, string[] inputs, object tag = null, string[] parameters = null, string[] arguments = null) {
      Blocks.Add(new Block() { Name = name, Tag = tag, Kind = BlockKind.Block, Src = src, Inputs = inputs, Parameters = parameters, Arguments = arguments });
      return this;
    }
    public LP_Diagram AddPin(string name, string pin, string mode, string option = null, object tag = null) {
      Blocks.Add(new Block() { Name = name, Tag = tag, Kind = BlockKind.Pin, Pin = pin, Mode = mode, Option = option });
      return this;
    }
    /// <param name="expr">выражение по входам; null - у типа блока нет выражения, это проблема блока</param>
    public LP_Diagram AddExpr(string name, string expr, string[] inputs, object tag = null) {
      Blocks.Add(new Block() { Name = name, Tag = tag, Kind = BlockKind.Expr, Expr = expr, Inputs = inputs });
      return this;
    }
    /// <summary>Блок-операция: op между подключёнными входами, неподключённые в программу не попадают.</summary>
    public LP_Diagram AddOp(string name, string op, string[] inputs, object tag = null) {
      Blocks.Add(new Block() { Name = name, Tag = tag, Kind = BlockKind.Expr, Op = op, Inputs = inputs });
      return this;
    }
    public LP_Diagram AddVar(string name, VarType type, object tag = null) {
      Variables.Add(new Variable() { Name = name, Tag = tag, Type = type });
      return this;
    }
    public LP_Diagram Connect(string from, string to) {
      Links.Add(new Link() { From = from, To = to });
      return this;
    }
    /// <summary>Константа на входе блока: literal - число или true/false.</summary>
    public LP_Diagram SetConst(string to, string literal) {
      Links.Add(new Link() { Const = literal, To = to });
      return this;
    }
  }
}
