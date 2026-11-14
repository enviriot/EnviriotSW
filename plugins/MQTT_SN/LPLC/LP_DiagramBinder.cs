///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using NiL.JS;
using NiL.JS.Core;
using NiL.JS.Expressions;
using NiL.JS.Statements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using X13.DevicePLC;
using static X13.LogramPLC.LP_DiagramCompiler;
using static X13.LogramPLC.LP_DiagramGenerator;

namespace X13.LogramPLC {
  /// <summary>Первая стадия LP_DiagramCompiler - что с чем связано: разбор и проверка блоков, переменных и
  /// связей, имена в программе, порядок Tick.</summary>
  /// <remarks>Все выходы блоков - поля их экземпляров: у Func это Q (значение return) и любое поле
  /// this.X, которое пишет src, у Pin - value. Поэтому связь читает поле источника, временных значений
  /// нет, а обратная связь сама получает значение прошлого цикла. Типы входов и Q выводит EP_Compiler по
  /// подключённым источникам, генератору их знать не нужно.
  /// <para>Пользовательские имена в JS не попадают: класс _cN, экземпляр _bN, пин _pN, переменная _vN,
  /// выражение _x.eN. Иначе блок OUT столкнулся бы с константой режима, а Op2 - с тегом X13.</para>
  /// <para>Проблемы уходят в фасад через add в порядке диаграммы: блоки, переменные, связи.</para></remarks>
  internal sealed class LP_DiagramBinder {
    internal static readonly string[] FuncInputs = { "A", "B", "C", "D", "E", "F", "G", "H" };
    private const string FuncWrap = "function _(A,B,C,D,E,F,G,H){\n";
    /// <summary>Общий объект выходов блоков-выражений: _x.e1, _x.e2...</summary>
    internal const string Holder = "_x", HolderClass = "_cx";
    private const Options ParseOptions = Options.SuppressConstantPropogation | Options.SuppressUselessExpressionsElimination;
    /// <summary>Аргумент Pin: число или имя константы - он вставляется в текст программы.</summary>
    private static readonly Regex PinArg = new Regex(@"^([A-Za-z_][A-Za-z0-9_]*|-?\d+)$");
    /// <summary>Константа на входе: число или true/false - тоже вставляется в текст программы.</summary>
    private static readonly Regex ConstArg = new Regex(@"^(-?\d+(\.\d+)?([eE][-+]?\d+)?|true|false)$");
    /// <summary>Имя входа блока-выражения.</summary>
    private static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");
    // NiL хранит позиции методов, но не имени класса. По тексту ищется только заголовок;
    // контракт класса и ссылки на его имя проверяются по дереву разбора.
    private const string Trivia = @"(?:\s|/\*[\s\S]*?\*/|//[^\r\n]*(?:\r?\n|$))*";
    private static readonly Regex ClassHeader = new Regex(@"\A" + Trivia + @"class\b" + Trivia + @"(?<name>[A-Za-z_$][A-Za-z0-9_$]*)" + Trivia + @"\{");

    internal sealed class Node {
      public LP_Diagram.Block Def;
      public Element El;
      public string Id;
      public string[] Inputs = new string[0];
      public readonly HashSet<string> Outputs = new HashSet<string>(StringComparer.Ordinal);
      /// <summary>Источник каждого входа - выражение; null - вход не подключён.</summary>
      public string[] Args;
      public string[] ConstructorArgs = new string[0];
      public readonly HashSet<string> ConstructorDependencies = new HashSet<string>(StringComparer.Ordinal);
      /// <summary>Func: src с return this.Q; Block: полный класс с внутренним именем.</summary>
      public string Code;
      /// <summary>Func: какие входы src трогает (читает, пишет, замыкает); null - src не разобран.</summary>
      public bool[] Used;
      /// <summary>Func: сколько параметров у Tick - входы до последнего, который трогает src.</summary>
      public int Arity;
      /// <summary>Expr: места ссылок на входы в выражении - позиция, длина, номер входа.</summary>
      public readonly List<Tuple<int, int, int>> Refs = new List<Tuple<int, int, int>>();
      /// <summary>Выражение, которым связь читает выход: поле экземпляра или поле общего объекта _x.</summary>
      public string Out(string pin) {
        return Def.Kind == LP_Diagram.BlockKind.Expr ? Holder + "." + Id : Id + "." + pin;
      }
      public EP_Device.PinIo Io;
      public readonly List<Node> Producers = new List<Node>();
      /// <summary>Присваивания переменным после Tick.</summary>
      public readonly List<string> Sinks = new List<string>();
    }

    /// <summary>Внутреннее имя (_b1, _p1, _v1) - элемент.</summary>
    private readonly Dictionary<string, Element> _ids = new Dictionary<string, Element>(StringComparer.Ordinal);
    /// <summary>Наблюдатели: имя - элемент, блок и выход, который он показывает.</summary>
    private readonly Dictionary<string, Tuple<Element, Node, string>> _observed = new Dictionary<string, Tuple<Element, Node, string>>(StringComparer.Ordinal);

    /// <summary>Блоки - в порядке диаграммы.</summary>
    public List<Node> Nodes { get; private set; }
    /// <summary>Порядок Tick; пуст, если в диаграмме ошибки.</summary>
    public List<Node> Order { get; private set; }
    public List<Node> ConstructorOrder { get; private set; }
    /// <summary>Переменные со своим меркером (не наблюдатели) - в порядке диаграммы.</summary>
    public List<LP_Diagram.Variable> VarDefs { get; private set; }
    /// <summary>Имя переменной - _vN.</summary>
    public Dictionary<string, string> VarIds { get; private set; }
    public Dictionary<string, Element> Ids { get { return _ids; } }
    public Dictionary<string, Tuple<Element, Node, string>> Observed { get { return _observed; } }

    private readonly List<Element> _elements;
    private readonly Action<Element, int, string, bool> _add;
    private int _errors;

    private LP_DiagramBinder(List<Element> elements, Action<Element, int, string, bool> add) {
      _elements = elements;
      _add = add;
      Order = new List<Node>();
    }

    /// <param name="elements">сюда - элемент на каждый блок, затем на каждую переменную</param>
    /// <param name="add">проблема: элемент, строка src, сообщение, предупреждение ли</param>
    public static LP_DiagramBinder Bind(LP_Diagram d, EP_Device device, List<Element> elements, Action<Element, int, string, bool> add) {
      var b = new LP_DiagramBinder(elements, add);
      b.Run(d, device);
      return b;
    }

    private void Add(Element el, int line, string message, bool warning = false) {
      if(!warning) {
        _errors++;
      }
      _add(el, line, message, warning);
    }

    private void Run(LP_Diagram d, EP_Device device) {
      var nodes = new List<Node>();
      var blocks = new Dictionary<string, Node>(StringComparer.Ordinal);
      var vars = new Dictionary<string, string>(StringComparer.Ordinal);   // имя - _vN
      var varDefs = new List<LP_Diagram.Variable>();
      var configurations = d.Blocks.Where(b => b.Configuration != null).Select(b => b.Configuration).ToArray();
      var resolver = new X13.Plc.ConstructorResolver(device == null ? null : device.Capabilities, configurations);
      int nf = 0, np = 0, nv = 0, ne = 0;
      foreach(var b in d.Blocks) {
        var el = new Element() { Name = b.Name, Tag = b.Tag };
        _elements.Add(el);
        if(string.IsNullOrEmpty(b.Name) || blocks.ContainsKey(b.Name)) {
          Add(el, 0, "block name is empty or not unique");
          continue;
        }
        var n = new Node() { Def = b, El = el };
        if(b.Kind == LP_Diagram.BlockKind.Func || b.Kind == LP_Diagram.BlockKind.SerialPort) {
          n.Id = "_b" + (++nf);
          n.Inputs = FuncInputs;
          n.Outputs.Add("Q");
          AnalyzeFunc(n);
          if(b.Kind == LP_Diagram.BlockKind.SerialPort) {
            if(b.Configuration == null) {
              Add(n.El, 0, "SerialPort requires constructor configuration");
            } else {
              var result = resolver.Resolve(b.Name);
              foreach(var error in result.Errors) Add(n.El, 0, error.Key + ": " + error.Value);
              if(result.Ok) n.ConstructorArgs = SerialParameters.Select(p =>
                result.Values.TryGetValue(p, out var value) ? X13.Plc.ConstructorSchema.Literal(value) : null).ToArray();
              if(result.Ok && n.ConstructorArgs.Any(a => a == null)) Add(n.El, 0, "incomplete SerialPort configuration");
            }
            n.Outputs.Add("Port");
          }
        } else if(b.Kind == LP_Diagram.BlockKind.Block) {
          n.Id = "_b" + (++nf);
          n.Inputs = b.Inputs ?? new string[0];
          if(b.Configuration != null) {
            var result = resolver.Resolve(b.Name);
            foreach(var error in result.Errors) Add(n.El, 0, error.Key + ": " + error.Value);
            foreach(var dependency in result.Dependencies) n.ConstructorDependencies.Add(dependency);
            if(result.Ok) AnalyzeBlock(n, device, parameters: b.Configuration.Schema.Parameters,
              arguments: b.Configuration.Schema.Parameters.Select(p => X13.Plc.ConstructorSchema.Literal(result.Values[p])).ToArray());
          } else {
            AnalyzeBlock(n, device);
          }
        } else if(b.Kind == LP_Diagram.BlockKind.Expr) {
          n.Id = "e" + (++ne);
          n.Outputs.Add("Q");
          AnalyzeExpr(n);
        } else {
          n.Id = "_p" + (++np);
          AnalyzePin(n, device);
        }
        n.Args = new string[n.Inputs.Length];
        nodes.Add(n);
        blocks.Add(b.Name, n);
        _ids.Add(n.Id, el);
      }
      foreach(var v in d.Variables) {
        var el = new Element() { Name = v.Name, Tag = v.Tag };
        _elements.Add(el);
        if(string.IsNullOrEmpty(v.Name) || blocks.ContainsKey(v.Name) || vars.ContainsKey(v.Name) || _observed.ContainsKey(v.Name)) {
          Add(el, 0, "variable name is empty or not unique");
          continue;
        }
        // Наблюдатель: имя "Блок_выход" существующего выхода - показывает поле блока, а не заводит свой
        // меркер. Так он работал и для рукописных программ: тег следует за меркером поля по имени.
        var obs = nodes.SelectMany(n => n.Outputs.Select(o => Tuple.Create(n, o))).FirstOrDefault(z => v.Name == z.Item1.Def.Name + "_" + z.Item2);
        if(obs != null) {
          _observed.Add(v.Name, Tuple.Create(el, obs.Item1, obs.Item2));
          continue;
        }
        string id = "_v" + (++nv);
        vars.Add(v.Name, id);
        varDefs.Add(v);
        _ids.Add(id, el);
      }
      var fed = new HashSet<string>(StringComparer.Ordinal);   // переменные-приёмники
      foreach(var l in d.Links) {
        Connect(l, blocks, vars, fed);
      }
      Nodes = nodes;
      VarDefs = varDefs;
      VarIds = vars;
      if(_errors > 0) {
        return;
      }

      ConstructorOrder = new List<Node>();
      var initialized = new HashSet<string>(StringComparer.Ordinal);
      while(ConstructorOrder.Count < nodes.Count) {
        var next = nodes.FirstOrDefault(n => !initialized.Contains(n.Def.Name) && n.ConstructorDependencies.All(initialized.Contains));
        if(next == null) {
          foreach(var n in nodes.Where(n => !initialized.Contains(n.Def.Name))) Add(n.El, 0, "constructor resource dependency cycle or missing provider");
          return;
        }
        ConstructorOrder.Add(next);
        initialized.Add(next.Def.Name);
      }

      // Порядок Tick: источники раньше потребителей, ничьи - по порядку диаграммы. В кольце первым идёт
      // первый по порядку блок: его входы от ещё не посчитанных блоков читают поле, то есть прошлый цикл.
      var order = new List<Node>();
      var placed = new HashSet<Node>();
      while(order.Count < nodes.Count) {
        var next = nodes.FirstOrDefault(z => !placed.Contains(z) && z.Producers.All(p => p == z || placed.Contains(p)))
          ?? nodes.First(z => !placed.Contains(z));
        placed.Add(next);
        order.Add(next);
      }
      Order = order;
    }

    /// <summary>Выходы Func по src, входы, которые он трогает, и src, где return без значения возвращает this.Q.</summary>
    /// <remarks>Места берутся из дерева разбора NiL, а не поиском по тексту: return, this.X и имена входов
    /// бывают и в строках, и в комментариях. В конец добавляется return this.Q: «нет значения» значит «Q не
    /// меняется», как undefined у серверного Func.
    /// <para>Вход трогает src, если у параметра есть ссылка, кроме самого объявления: чтение, запись или
    /// замыкание. let с тем же именем во вложенном блоке - своя переменная, параметр он не трогает. Каждый
    /// параметр Tick стоит вызову LDI_0 или загрузки перед CALL и NIP после, поэтому лишние не объявляются.</para></remarks>
    private void AnalyzeFunc(Node n) {
      var f = ParseFunc(n.Def.Src, n.Def.Kind == LP_Diagram.BlockKind.SerialPort ? "Port" : null);
      n.Used = f.Used;
      n.Arity = f.Arity;
      foreach(var o in f.Outputs) {
        n.Outputs.Add(o);
      }
      if(f.Error != null) {
        Add(n.El, f.ErrorLine, f.Error);
        return;
      }
      n.Code = f.Code;
    }

    /// <summary>Block - класс, а не тело Func: конструктор и Tick остаются без переписывания return.</summary>
    private void AnalyzeBlock(Node n, EP_Device device, string source = null, string[] parameters = null, string[] arguments = null) {
      if(n.Inputs.Any(z => z == null || !Identifier.IsMatch(z)) || n.Inputs.Distinct(StringComparer.Ordinal).Count() != n.Inputs.Length) {
        Add(n.El, 0, "block inputs must be distinct names: " + string.Join(", ", n.Inputs));
        return;
      }
      parameters = parameters ?? n.Def.Parameters ?? new string[0];
      arguments = arguments ?? n.Def.Arguments ?? new string[0];
      if(parameters.Any(p => p == null || !Identifier.IsMatch(p)) || parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Length) {
        Add(n.El, 0, "constructor parameters must be distinct names");
        return;
      }
      if(parameters.Length != arguments.Length) {
        Add(n.El, 0, "constructor argument count must match parameters");
        return;
      }
      foreach(var argument in arguments) {
        int value;
        double number;
        bool literal = argument != null && ConstArg.IsMatch(argument) && (argument == "true" || argument == "false"
          || (double.TryParse(argument, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)
            && !double.IsInfinity(number) && !double.IsNaN(number)));
        if(!literal && (argument == null || !Identifier.IsMatch(argument) || device == null || !device.TryConst(argument, out value))) {
          Add(n.El, 0, "constructor argument must be a finite literal or device constant: " + (argument ?? "<unset>"));
          return;
        }
      }
      n.ConstructorArgs = arguments;
      string src = source ?? n.Def.Src ?? string.Empty;
      try {
        var mod = new Module(src, (level, coords, message) => { }, ParseOptions);
        var body = mod.Script.Root.Body;
        var cls = body.Length == 1 ? body[0] as ClassDefinition : null;
        var header = ClassHeader.Match(src);
        if(cls == null || !header.Success || header.Groups["name"].Value != cls.Name) {
          Add(n.El, 0, "Block src must be a single named class");
          return;
        }
        // NiL's synthesized constructor has a nonzero length but no position inside this class.
        if(cls.Constructor == null || cls.Constructor.Position < header.Length || cls.Constructor.Length == 0) {
          Add(n.El, 0, "Block requires an explicit constructor");
          return;
        }
        if(!cls.Constructor.Parameters.Select(p => p.Name).SequenceEqual(parameters)) {
          Add(n.El, 0, "constructor parameters must match block arguments: " + string.Join(", ", parameters));
          return;
        }
        var tick = cls.Members.Where(m => !m.Static && m.Name is Constant && ((Constant)m.Name).Value.AsString(null) == "Tick")
          .Select(m => m.Value as FunctionDefinition).FirstOrDefault();
        if(tick == null) {
          Add(n.El, 0, "Block requires an instance Tick method");
          return;
        }
        if(!tick.Parameters.Select(p => p.Name).SequenceEqual(n.Inputs)) {
          Add(n.El, 0, "Tick parameters must match block inputs: " + string.Join(", ", n.Inputs));
          return;
        }
        foreach(var method in new[] { cls.Constructor }.Concat(cls.Members.Where(m => !m.Static).Select(m => m.Value as FunctionDefinition)).Where(m => m != null)) {
          Walk(method.Body, z => {
            var set = z as SetProperty;
            var field = set == null ? null : set.FieldName as Constant;
            if(set != null && set.Source is This && field != null && field.Value.ValueType == JSValueType.String) {
              n.Outputs.Add(field.Value.ToString());
            }
          });
        }
        var name = header.Groups["name"];
        var positions = new SortedSet<int> { name.Index };
        foreach(var reference in cls.Reference.Descriptor.References) {
          if(reference.Length == name.Length && reference.Position >= 0 && reference.EndPosition <= src.Length
            && src.Substring(reference.Position, reference.Length) == cls.Name) positions.Add(reference.Position);
        }
        var code = new StringBuilder(src);
        string internalName = "_c" + n.Id.Substring(2);
        foreach(int position in positions.Reverse()) code.Remove(position, name.Length).Insert(position, internalName);
        n.Code = code.ToString();
      }
      catch(JSException ex) {
        var m = AtEnd.Match(ex.Message);
        Add(n.El, m.Success ? int.Parse(m.Groups[1].Value) : 0, ex.Message);
      }
      catch(ArgumentException ex) {
        Add(n.El, 0, "invalid Block src: " + ex.Message);
      }
    }


    /// <summary>Разбор src Func - то, что о нём знает компилятор.</summary>
    internal sealed class FuncInfo {
      /// <summary>Q, затем каждое поле this.X, которое пишет src, - в порядке появления, без повторов.</summary>
      public readonly List<string> Outputs = new List<string>();
      /// <summary>Какие входы src трогает; null - src не разобран.</summary>
      public bool[] Used;
      public int Arity;
      /// <summary>src, где return без значения возвращает this.Q; null - src не разобран.</summary>
      public string Code;
      /// <summary>Ошибка разбора; null - разобран.</summary>
      public string Error;
      public int ErrorLine;
    }

    /// <summary>Выходы Func, входы, которые он трогает, и код с return this.Q - одним разбором src.</summary>
    /// <remarks>Нужен не только компилятору: выходы блока для редактора LP_Schema берёт отсюда же, чтобы
    /// IDE не разбирала src сама и не расходилась с компилятором.</remarks>
    internal static FuncInfo ParseFunc(string src, string readOnlyField = null) {
      src = src ?? string.Empty;
      var f = new FuncInfo();
      f.Outputs.Add("Q");
      CodeBlock body;
      try {
        var mod = new Module(FuncWrap + src + "\n}", (level, coords, message) => { }, ParseOptions);
        var fv = mod.Script.Root.Variables == null ? null : mod.Script.Root.Variables.FirstOrDefault(z => z.Name == "_");
        var fd = fv == null ? null : fv.Initializer as FunctionDefinition;
        body = fd == null ? null : fd.Body;
        if(fd != null) {
          f.Used = fd.Parameters.Select(p => p.References.Any(r => !(r is ParameterReference))).ToArray();
          f.Arity = Array.FindLastIndex(f.Used, z => z) + 1;
        }
      }
      catch(JSException ex) {
        var m = AtEnd.Match(ex.Message);
        f.ErrorLine = m.Success ? Math.Max(0, int.Parse(m.Groups[1].Value) - 1) : 0;
        f.Error = ex.Message;
        return f;
      }
      if(body == null) {
        f.Error = "src is not a function body";
        return f;
      }
      if(readOnlyField != null) {
        Walk(body, z => {
          if(f.Error == null && WrittenField(z) == readOnlyField) {
            f.Error = readOnlyField + " is read-only";
            f.ErrorLine = Math.Max(1, CodeCoordinates.FromTextPosition(FuncWrap + src + "\n}", z.Position, 0).Line - 1);
          }
        }, includeFunctions: true);
        if(f.Error != null) return f;
      }
      var inserts = new SortedSet<int>();
      Walk(body, z => {
        Return r;
        string output = WrittenField(z);
        if((r = z as Return) != null && r.Value == null) {
          inserts.Add(r.Position + "return".Length - FuncWrap.Length);
        } else if(output != null) {
          if(!f.Outputs.Contains(output)) {
            f.Outputs.Add(output);
          }
        }
      });
      var code = new StringBuilder(src);
      foreach(int pos in inserts.Reverse()) {
        code.Insert(pos, " this.Q");
      }
      f.Code = code.Append("\nreturn this.Q;").ToString();
      return f;
    }
    internal static string WrittenField(CodeNode node) {
      if(node is SetProperty set && set.Source is This && set.FieldName is Constant field)
        return field.Value.AsString(null);
      Property property = null;
      if(node is Assignment assignment && assignment.Children[0] is AssignmentOperatorCache cache)
        property = cache.Source as Property;
      else if(node is Increment || node is Decrement) property = node.Children[0] as Property;
      return property?.Source is This && property.FieldName is Constant key ? key.Value.AsString(null) : null;
    }
    /// <summary>По умолчанию не заходит во вложенные функции: их return не принадлежит Tick.</summary>
    private static void Walk(CodeNode n, Action<CodeNode> f, bool includeFunctions = false) {
      if(n == null || !includeFunctions && n is FunctionDefinition) {
        return;
      }
      f(n);
      var ch = n.Children;
      if(ch != null) {
        foreach(var c in ch) {
          Walk(c, f, includeFunctions);
        }
      }
      if(n is Conditional conditional) foreach(var branch in conditional.Branches) Walk(branch, f, includeFunctions);
    }

    /// <summary>Вход и выход Pin по направлению режима: out - вход x и Tick(x), иначе Tick() без входов.</summary>
    /// <remarks>Пин и параметр проверит EP_Compiler, его ошибка придёт на строку объявления, то есть на
    /// этот блок. Счётчик здесь только читается: сброс - будущий блок Counter.</remarks>
    private void AnalyzePin(Node n, EP_Device device) {
      var b = n.Def;
      n.Outputs.Add("value");
      foreach(var a in new[] { b.Pin, b.Mode, b.Option ?? "0" }) {
        if(a == null || !PinArg.IsMatch(a)) {
          Add(n.El, 0, "Pin argument must be a number or a name: " + (a ?? "null"));
          return;
        }
      }
      if(device == null) {
        Add(n.El, 0, "Pin needs the PLC description of the device type");
        return;
      }
      int mv;
      EP_Device.Mode mode;
      if((!int.TryParse(b.Mode, out mv) && !device.TryConst(b.Mode, out mv)) || (mode = device.ModeOf(mv)) == null) {
        Add(n.El, 0, "unknown mode " + b.Mode);
        return;
      }
      n.Io = mode.Io;
      if(n.Io == EP_Device.PinIo.Out) {
        n.Inputs = new[] { "x" };
      }
    }

    /// <summary>Входы блока-выражения и места, где выражение их читает.</summary>
    /// <remarks>Выражение разбирается как return функции с входами блока, и места ссылок на входы берутся из
    /// дерева разбора: на них при генерации встаёт источник в скобках, а имя входа в строке или комментарии
    /// остаётся как есть. Выражение встаёт в цикл программы, а не в свой метод, поэтому оно должно быть одним
    /// выражением, без this и без записи во вход.</remarks>
    private void AnalyzeExpr(Node n) {
      var b = n.Def;
      var inputs = b.Inputs ?? new string[0];
      if(inputs.Any(z => z == null || !Identifier.IsMatch(z)) || inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Length) {
        Add(n.El, 0, "block inputs must be distinct names: " + string.Join(", ", inputs));
        return;
      }
      n.Inputs = inputs;   // до проверки выражения: иначе каждая связь на блок дала бы ещё и "no input"
      if(b.Op != null) {   // подключённый вход операции всегда в деле, предупреждать не о чем
        if(!OpIdentity.ContainsKey(b.Op)) {
          Add(n.El, 0, "unsupported operation " + b.Op + ", allowed: " + string.Join(" ", OpIdentity.Keys));
        }
        return;
      }
      if(string.IsNullOrWhiteSpace(b.Expr)) {
        Add(n.El, 0, "the block type has no expression");
        return;
      }
      string head = "function _(" + string.Join(",", inputs) + "){\nreturn (";
      FunctionDefinition fd;
      try {
        var mod = new Module(head + b.Expr + "\n);\n}", (level, coords, message) => { }, ParseOptions);
        var fv = mod.Script.Root.Variables == null ? null : mod.Script.Root.Variables.FirstOrDefault(z => z.Name == "_");
        fd = fv == null ? null : fv.Initializer as FunctionDefinition;
      }
      catch(JSException ex) {
        Add(n.El, 0, "expression " + b.Expr + ": " + ex.Message);
        return;
      }
      var ret = fd == null || fd.Body.Body.Length != 1 ? null : fd.Body.Body[0] as Return;
      if(ret == null || ret.Value == null) {
        Add(n.El, 0, "not a single expression: " + b.Expr);
        return;
      }
      bool self = false;
      Walk(ret, z => self |= z is This);
      if(self) {
        Add(n.El, 0, "an expression has no this: " + b.Expr);
        return;
      }
      n.Used = new bool[inputs.Length];
      for(int i = 0; i < inputs.Length; i++) {
        var p = fd.Parameters[i];
        if(p.Assignments != null && p.Assignments.Count > 0) {
          Add(n.El, 0, "the expression writes input " + p.Name + ": " + b.Expr);
          return;
        }
        foreach(var r in p.References.Where(z => !(z is ParameterReference))) {
          n.Refs.Add(Tuple.Create(r.Position - head.Length, r.Length, i));
          n.Used[i] = true;
        }
      }
    }

    private void Connect(LP_Diagram.Link l, Dictionary<string, Node> blocks, Dictionary<string, string> vars, HashSet<string> fed) {
      string what = (l.From ?? l.Const ?? "null") + " -> " + (l.To ?? "null");
      string expr, pin;
      Node src = null;
      Tuple<Element, Node, string> obs;
      if(l.From == null) {
        if(l.Const == null || !ConstArg.IsMatch(l.Const)) {
          Add(null, 0, what + ": a constant is a number or true/false");
          return;
        }
        expr = l.Const;
      } else if(!Split(l.From, out var srcName, out pin)) {
        if(_observed.TryGetValue(srcName, out obs)) {   // наблюдатель - тот же выход блока
          src = obs.Item2;
          expr = src.Out(obs.Item3);
        } else if(!vars.TryGetValue(srcName, out expr)) {
          Add(null, 0, what + ": no variable " + srcName);
          return;
        }
      } else {
        if(!blocks.TryGetValue(srcName, out src)) {
          Add(null, 0, what + ": no block " + srcName);
          return;
        }
        if(!src.Outputs.Contains(pin)) {
          Add(src.El, 0, what + ": no output " + pin);
          return;
        }
        expr = src.Out(pin);
      }
      if(!Split(l.To, out var dstName, out pin)) {
        string vid;
        if(_observed.TryGetValue(dstName, out obs)) {
          Add(obs.Item1, 0, what + ": " + dstName + " shows " + obs.Item2.Def.Name + "." + obs.Item3 + " and is not fed");
        } else if(!vars.TryGetValue(dstName, out vid)) {
          Add(null, 0, what + ": no variable " + dstName);
        } else if(src == null) {
          Add(_ids[vid], 0, what + ": a variable is fed by a block output only");
        } else if(!fed.Add(dstName)) {
          Add(_ids[vid], 0, what + ": the variable is fed twice");
        } else {
          src.Sinks.Add(vid + " = " + expr + ";");
        }
        return;
      }
      Node dst;
      if(!blocks.TryGetValue(dstName, out dst)) {
        Add(null, 0, what + ": no block " + dstName);
        return;
      }
      int idx = Array.IndexOf(dst.Inputs, pin);
      if(idx < 0) {
        Add(dst.El, 0, what + ": no input " + pin);
      } else if(dst.Args[idx] != null) {
        Add(dst.El, 0, what + ": input " + pin + " is connected twice");
      } else {
        if(dst.Used != null && !dst.Used[idx]) {   // связь не ошибка, но в программу она не попадёт
          Add(dst.El, 0, what + ": " + (dst.Def.Kind == LP_Diagram.BlockKind.Expr ? "the expression" : "src") + " does not use input " + pin, true);
        }
        dst.Args[idx] = expr;
        if(src != null && !dst.Producers.Contains(src)) {
          dst.Producers.Add(src);
        }
      }
    }
    /// <summary>"Блок.пин" - true; имя переменной - false.</summary>
    private static bool Split(string end, out string name, out string pin) {
      end = end ?? string.Empty;
      int dot = end.IndexOf('.');
      name = dot < 0 ? end : end.Substring(0, dot);
      pin = dot < 0 ? null : end.Substring(dot + 1);
      return dot >= 0;
    }
  }
}
