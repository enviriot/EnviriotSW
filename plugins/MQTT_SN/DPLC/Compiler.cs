using NiL.JS;
using NiL.JS.Core;
using NiL.JS.Expressions;
using NiL.JS.Extensions;
using NiL.JS.Statements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.DevicePLC {
  internal class EP_Compiler {
    private static bool Verbose {
      get {
        return X13.Periphery.MQTT_SNPl.verbosePlc;
      }
    }

    internal Stack<Instruction> _sp;
    internal List<Scope> _programm;
    internal Stack<Scope> _scope;
    public Scope global, cur, initBlock, dataBlock;

    public SortedList<string, string> varList;
    public List<string> ioList;
    /// <summary>Получатель диагностик отвечает за контекст и журнал; без него компилятор пишет сам.</summary>
    public event CompilerMessageCallback CMsg;
    public uint StackBottom { get; private set; }
    public SortedList<uint, byte[]> Hex;

    private string _sourceText;
    /// <summary>Объявления без явного типа: меркер, инициализатор и область, где он вычисляется.</summary>
    private List<Tuple<Merker, Expression, Scope>> _untyped;
    /// <summary>Вызовы и области, откуда они сделаны: аргументы задают тип параметров.</summary>
    private List<Tuple<Call, Scope>> _calls;

    /// <summary>Текст ошибки, на которой остановился последний Parse; null - ошибки не было.</summary>
    /// <remarks>Прежде он только уходил в Log. Начинается с "[строка, столбец] ", если место известно
    /// (Where), у синтаксической ошибки NiL место - в конце: "(строка:столбец)".</remarks>
    public string Error { get; private set; }
    /// <summary>Команда программы: адреса [Addr, End) и строка текста, из которой она построена.</summary>
    internal sealed class CodeLine {
      public uint Addr;
      public uint End;
      /// <summary>0 - строка неизвестна.</summary>
      public int Line;
    }
    /// <summary>Все команды последнего успешного Parse по порядку адресов.</summary>
    /// <remarks>Строка - по узлу команды, а у команды без узла (арифметика, загрузка поля, NIP после
    /// вызова) - по оператору, внутри которого она выдана (Statement). Вне оператора (RET в конце
    /// функции) - строка предыдущей команды той же области, а в начале области - первой известной: так
    /// функция целиком относится к своему тексту и не захватывает строку соседней через промежуток
    /// выравнивания.</remarks>
    public List<CodeLine> CodeLines { get; private set; }
    /// <summary>Оператор, который сейчас компилирует VP2; его позиция достаётся командам без своего узла.</summary>
    internal CodeNode Statement;

    /// <summary>Строка программы, из которой построена команда по адресу pc (PC из STOP_RESP); 0 - неизвестно.</summary>
    public int SourceLineAt(uint pc) {
      var cl = CodeLines == null ? null : CodeLines.FirstOrDefault(z => z.Addr <= pc && pc < z.End);
      return cl == null ? 0 : cl.Line;
    }

    /// <summary>Место узла в исходнике в формате CompilerMessageCallback.</summary>
    /// <remarks>CodeNode.Position — смещение в символах; для получения строки и столбца
    /// нужен исходный текст, поэтому Parse сохраняет его.</remarks>
    internal string Where(NiL.JS.Core.CodeNode node) {
      if(_sourceText == null || node == null) {
        return string.Empty;
      }
      // Формат как у CompilerMessageCallback ниже, а не CodeCoordinates.ToString():
      // тот выводит (line:column*length), и два формата координат в одном журнале излишни.
      var c = CodeCoordinates.FromTextPosition(_sourceText, node.Position, node.Length);
      return string.Format("[{0}, {1}] ", c.Line, c.Column);
    }

    /// <summary>Узел, для которого компилируется программа: от него зависит набор API-функций.</summary>
    public EP_Target Target { get; private set; }
    /// <summary>Пины и режимы узла из описания его типа; null - описания нет.</summary>
    public EP_Device Device { get; private set; }
    /// <summary>Поля экземпляра Pin, общие для всех экземпляров: одно value.</summary>
    internal Scope PinScope { get; private set; }

    /// <summary>void ConfigPin(pin, mode, option); им же конструктор Pin настраивает пин.</summary>
    internal const uint API_CONFIG_PIN = 31;
    internal const uint API_PRINT_F = 26;

    public EP_Compiler(EP_Target target = EP_Target.X13, EP_Device device = null) {
      Target = target;
      Device = device;
      Hex = new SortedList<uint, byte[]>();
    }

    public bool Parse(string code) {
      _sourceText = code;   // сохраняется для указания строки ошибки компиляции, см. Where()
      Error = null;
      CodeLines = new List<CodeLine>();
      bool success = false;
      _scope = new Stack<Scope>();
      _programm = new List<Scope>();
      _sp = new Stack<Instruction>();
      _untyped = new List<Tuple<Merker, Expression, Scope>>();
      _calls = new List<Tuple<Call, Scope>>();
      uint addr;
      string vName;
      Instruction ri;

      try {
        global = ScopePush(null);
        initBlock = new Scope(this, null, null);
        _programm.Add(initBlock);

        initBlock.AddInst(ri = new Instruction(EP_InstCode.LABEL));
        global.AddInst(EP_InstCode.NOP);
        global.AddInst(new Instruction(EP_InstCode.JMP) { _ref = ri });
        global.AddInst(EP_InstCode.LABEL);

        dataBlock = new Scope(this, null, null);
        _programm.Add(dataBlock);

        // Не в _programm: кода у Pin нет, а поле размечено заранее - слово 0 экземпляра
        PinScope = new Scope(this, new Merker() { type = EP_Type.CLASS, pName = "Pin", fName = "Pin" }, null);
        PinScope.memory.Add(new Merker() { type = EP_Type.PropS4, pName = "value", fName = "Pin.value", Addr = 0 });

        var module = new Module(code, CompilerMessageCallback, Options.SuppressConstantPropogation | Options.SuppressUselessExpressionsElimination);

        if(module.Script.Root != null) {
          var p1 = new EP_VP1(this);
          module.Script.Root.Visit(p1);

          // Поля - только когда программа прочитана целиком: поле появляется и в методе, и снаружи
          // (a.z = 5). VP1 размечал их сразу после конструктора, и такое поле оставалось с адресом 0,
          // на первом поле объекта. Разметка не повторяема, поэтому - один раз и здесь.
          // И не позже VP2: он выбирает LDM/STM_*_CS8 или _CS16 по смещению поля (Addr < 256), а размер
          // экземпляра InstanceWords берёт из memBlocks области. После VP2 поле со смещением от 256 осталось бы с
          // короткой командой, и Prepare обрезал бы смещение до байта.
          foreach(var sc in _programm.Where(z => z != global)) {
            sc.AllocatFields();
          }

          InferTypes();

          _sp.Clear();
          var p2 = new EP_VP2(this);
          module.Script.Root.Visit(p2);
        }

        varList = new SortedList<string, string>(StringComparer.Ordinal);
        ioList = new List<string>();
        uint mLen;

        addr = 0;
        var HexN = new SortedList<uint, byte[]>();

        foreach(var p in _programm) {
          foreach(var m in p.memory.OrderBy(z => z.type)) {
            switch(m.type) {
            case EP_Type.BOOL:
              vName = "Mz";
              mLen = 1;
              break;
            case EP_Type.SINT8:
              vName = "Mb";
              mLen = 8;
              break;
            case EP_Type.SINT16:
              vName = "Mw";
              mLen = 16;
              break;
            case EP_Type.SINT32:
              vName = "Md";
              mLen = 32;
              break;
            case EP_Type.FLOAT:
              vName = "Mf";
              mLen = 32;
              break;
            case EP_Type.UINT8:
              vName = "MB";
              mLen = 8;
              break;
            case EP_Type.UINT16:
              vName = "MW";
              mLen = 16;
              break;
            case EP_Type.REFERENCE:
              if(m.InstanceWords == 0) {
                // Экземпляр класса без полей: памяти ему не нужно, а this у него никто не разыменует.
                // Прежде AllocateMemory(0) и деление на mLen роняли компиляцию DivideByZeroException.
                m.Addr = 0;
                continue;
              }
              vName = null;
              mLen = (uint)m.InstanceWords * 32;
              break;
            case EP_Type.INPUT:
            case EP_Type.OUTPUT:
              ioList.Add(m.vd.Name);
              continue;
            default:
              continue;
            }
            if(m.Addr == uint.MaxValue) {
              m.Addr = global.AllocateMemory(uint.MaxValue, mLen) / ( mLen >= 32 ? 32 : mLen ); //-V3064
            }
            if(p == global) {
              if(vName != null) {
                varList[m.vd.Name] = vName + m.Addr.ToString();
                if(Verbose) {
                  Log.Debug("{0}<{1}> = {2}; {3}", m.vd.Name, m.type, vName + m.Addr.ToString(), mLen==1?( ( m.Addr/8 ).ToString("X2")+"."+( m.Addr%8 ).ToString() ):( m.Addr*mLen/8 ).ToString("X2"));
                }
              } else {    // REFERENCE
                foreach(var m1 in m.scope.memory) {
                  switch(m1.type) {
                  case EP_Type.PropB1:
                    vName = "Mz";
                    mLen = 1;
                    break;
                  case EP_Type.PropS1:
                    vName = "Mb";
                    mLen = 8;
                    break;
                  case EP_Type.PropS2:
                    vName = "Mw";
                    mLen = 16;
                    break;
                  case EP_Type.PropS4:
                    vName = "Md";
                    mLen = 32;
                    break;
                  case EP_Type.PropF4:
                    vName = "Mf";
                    mLen = 32;
                    break;
                  case EP_Type.PropU1:
                    vName = "MB";
                    mLen = 8;
                    break;
                  case EP_Type.PropU2:
                    vName = "MW";
                    mLen = 16;
                    break;
                  default:
                    continue;
                  }
                  varList[m.vd.Name + "." + m1.pName] = vName + ( m.Addr * 32 / mLen + m1.Addr ).ToString();
                  if(Verbose) {
                    Log.Debug("{0}<{1}> = {2}; {3}:{4}", m.vd.Name + "." + m1.pName, m1.type, vName + ( m.Addr * 32 / mLen + m1.Addr ).ToString()
                      , ( m.Addr*4 ).ToString("X2")
                      , mLen==1?( ( m1.Addr/8 ).ToString("X2")+"."+( m1.Addr%8 ).ToString() ):( m1.Addr*mLen/8 ).ToString("X2"));
                  }

                }
              }
            }
          }
        }
        foreach(var p in _programm) {
          p.Optimize();
          addr += ( 32 - ( addr % 32 ) ) % 32;
          if(p.fm != null) {
            p.fm.Addr = addr;
          }
          foreach(var c in p.code) {
            c.Link();  // обновить размер LDI_*
            c.addr = addr;
            if(c._blob && c._param != null) {
              c._param.Addr = c.addr;
            }
            addr += (uint)c._code.Length;
          }
        }

        if(addr > 65536) throw new NotSupportedException("Program exceeds the 64 KiB address space");
        List<byte> bytes = new List<byte>();
        var lineOf = new Dictionary<int, int>();   // позиция - строка: у многих команд одна позиция
        foreach(var p in _programm) {
          int first = CodeLines.Count;
          int line = 0;
          foreach(var c in p.code) {
            c.Link();
            if(c._code.Length > 0) {
              bytes.AddRange(c._code);
              int pos = c._cn != null ? c._cn.Position : c._at;
              if(pos >= 0 && !lineOf.TryGetValue(pos, out line)) {
                line = CodeCoordinates.FromTextPosition(_sourceText, pos, 0).Line;
                lineOf.Add(pos, line);
              }
              CodeLines.Add(new CodeLine() { Addr = c.addr, End = c.addr + (uint)c._code.Length, Line = line });
            }
          }
          // команды до первого узла области - его строка
          var known = CodeLines.Skip(first).FirstOrDefault(z => z.Line > 0);
          for(int i = first; known != null && i < CodeLines.Count && CodeLines[i].Line == 0; i++) {
            CodeLines[i].Line = known.Line;
          }
          if(bytes.Count > 0) {
            HexN[p.code.First().addr] = bytes.ToArray();
            if(Verbose) {
              Log.Debug("{0}", p.ToString());
            }
          }
          bytes.Clear();
        }
        Hex = HexN;
        StackBottom = (uint)( ( global.memBlocks.Last().start + 7 ) / 8 );
        Log.Info("Used ROM: {0} bytes, RAM: {1} bytes", Hex.Select(z => z.Key + z.Value.Length).Max(), StackBottom);
        success = true;
      }
      catch(JSException ex) {
        var syntaxError = ex.Error.Value as NiL.JS.BaseLibrary.SyntaxError;
        if(syntaxError != null) {
          Error = syntaxError.message.ToString();
        } else {
          Error = ex.Message;
        }
      }
      catch(Exception ex) {
        Error = ex.Message;
      }
      if(!success && CMsg == null) {
        Log.Error("Compile - {0}", Error);
      }
      _scope = null;
      _programm = null;
      _sp = null;
      _untyped = null;
      _calls = null;

      return success;
    }

    internal Scope ScopePush(Merker fm) {
      var tmp = cur;
      cur = _programm.FirstOrDefault(z => z.fm == fm);
      if(cur == null) {
        cur = new Scope(this, fm, tmp);
        _programm.Add(cur);
      }
      _scope.Push(cur);
      return cur;
    }
    internal void ScopePop() {
      _scope.Pop();
      cur = _scope.Peek();
    }
    internal Merker DefineMerker(VariableDescriptor v, EP_Type type = EP_Type.NONE) {
      Merker m = null;
      uint addr;

      m = cur.memory.FirstOrDefault(z => z.vd == v);
      if(m == null) {
        m = global.memory.FirstOrDefault(z => z.vd == v);
      }
      if(m == null) {
        addr = uint.MaxValue;
        var nt = Periphery.MsDevice.NTTable.FirstOrDefault(z => v.Name.StartsWith(z.Item1));
        
        if(nt!=null && v.Name.Length > 2 && UInt32.TryParse(v.Name.Substring(2), out uint v_addr)) {
          // На EspNode IN/OUT адресуют GPIO, и буквы тега ничего не значат: Mz1 молча стал бы IN на GPIO 1
          if(Target == EP_Target.EspNode) {
            throw new NotSupportedException(v.Name + ": X13 object tags are not available on EspNode, use new Pin(pin, mode)");
          }
          addr = (uint)( (uint)( ( (byte)v.Name[0] ) << 24 ) | (uint)( ( (byte)v.Name[1] ) << 16 ) | (v_addr & 0xFFFF) );
          type = (nt.Item2 & Periphery.MsDevice.DType.Output)!=0?EP_Type.OUTPUT:EP_Type.INPUT;
        } else if(type == EP_Type.NONE) {
          if(v.Initializer != null && v.Initializer is FunctionDefinition) {
            type = EP_Type.FUNCTION;
          } else if(v.IsLexicalScoped) {
            type = EP_Type.LOCAL;
            addr = (uint)cur.memory.Where(z => z.type == EP_Type.LOCAL).Count();
            if(addr > 15) {
              throw new ArgumentOutOfRangeException("Too many local variables: " + v.Name + cur.fm == null ? string.Empty : ( "in " + cur.fm.ToString() ));
            }
          } else {
            type = EP_Type.SINT32;
            addr = uint.MaxValue;
          }

        }
        m = new Merker() { type = type, vd = v, pName = v.Name, Addr = addr, init = v.Initializer };

        if(type == EP_Type.API || type == EP_Type.INPUT || type == EP_Type.OUTPUT) {
          global.memory.Add(m);
          m.fName = v.Name;
        } else {
          cur.memory.Add(m);
          m.fName = ( cur == global ? v.Name : cur.fm.fName + ( cur.fm.type == EP_Type.FUNCTION ? "+" : "." ) + v.Name );
        }

      } else if(m.type != type && m.type == EP_Type.NONE) {
        m.type = type;
      }
      return m;
    }
    internal Merker GetMerker(VariableDescriptor v) {
      Merker m = null;

      m = cur.memory.FirstOrDefault(z => z.vd == v);
      if(m == null) {
        m = global.memory.FirstOrDefault(z => z.vd == v);
      }
      if(m == null) {
        m = LoadNativeFunctions(v);
      }

      return m;
    }

    /// <summary>e - new Pin(...), а не конструктор своего класса Pin.</summary>
    internal bool IsPinCtor(Expression e) {
      Call ca;
      Variable f;
      return (ca = e as Call) != null && ca.CallMode == CallMode.Construct && (f = ca.Children[0] as Variable) != null
        && f.Name == "Pin" && GetMerker(f.Descriptor) == null;
    }

    /// <summary>Значение целой константы: литерала или константы описания узла; иначе null.</summary>
    internal int? ConstValue(Expression e) {
      Constant c;
      Variable v;
      Merker m;
      if(e is Negation) {
        int? operand = ConstValue(e.Children[0] as Expression);
        return operand.HasValue && operand.Value != int.MinValue ? (int?)-operand.Value : null;
      }
      if((c = e as Constant) != null) {
        if(c.Value == null || !c.Value.IsNumber) return null;
        double number = (double)c.Value;
        return !double.IsNaN(number) && number == Math.Floor(number) && number >= int.MinValue && number <= int.MaxValue ? (int?)(int)number : null;
      }
      if((v = e as Variable) != null && (m = GetMerker(v.Descriptor)) != null && m.type == EP_Type.CONST) {
        return (int)m.Addr;
      }
      return null;
    }

    /// <summary>Экземпляр блока Pin: var x = new Pin(pin, mode[, option]).</summary>
    /// <remarks>Номер пина зашит в команды IN/OUT, поэтому экземпляр - только глобальная var, а аргументы -
    /// константы. Конструктор (ConfigPin в init) и Tick генерирует VP2.</remarks>
    internal Merker DefinePin(VariableDescriptor v, Call ca) {
      string w = Where(ca);
      if(Target != EP_Target.EspNode) {
        throw new NotSupportedException(w + "Pin is not available on " + Target);
      }
      if(Device == null) {
        throw new NotSupportedException(w + "Pin needs the PLC description of the device type");
      }
      if(cur != global || v.IsLexicalScoped) {
        throw new NotSupportedException(w + v.Name + ": Pin must be declared with var at the top level");
      }
      if(ca.Arguments.Length < 2 || ca.Arguments.Length > 3) {
        throw new NotSupportedException(w + v.Name + ": Pin(pin, mode[, option]) expected");
      }
      int pin = PinArg(ca, 0, "pin");
      int mv = PinArg(ca, 1, "mode");
      var mode = Device.ModeOf(mv);
      if(mode == null) {
        throw new NotSupportedException(w + v.Name + ": mode " + mv + " is not in the device description");
      }
      if(mode.Io == EP_Device.PinIo.None) {
        throw new NotSupportedException(w + v.Name + ": " + mode.Name + " is not a pin mode, use ConfigPin");
      }
      if(!Device.Allows(pin, mode)) {
        throw new NotSupportedException(w + v.Name + ": " + mode.Name + " is not available on pin " + pin);
      }
      int opt = ca.Arguments.Length > 2 ? PinArg(ca, 2, "option") : Device.DefaultOption(mode);
      if(!Device.CheckOption(mode, opt)) {
        throw new NotSupportedException(w + v.Name + ": option " + opt + " of " + mode.Name
          + (mode.Opt == null ? " is not supported" : " is not in " + mode.Opt.ToString()));
      }
      var m = DefineMerker(v, EP_Type.REFERENCE);
      m.type = EP_Type.REFERENCE;
      m.scope = PinScope;
      m.InstanceWords = 1;   // одно слово: value
      m.pin = new PinCfg() { Gpio = pin, Mode = mode, Option = opt };
      return m;
    }
    private int PinArg(Call ca, int i, string what) {
      var r = ConstValue(ca.Arguments[i]);
      if(r == null) {
        throw new NotSupportedException(Where(ca.Arguments[i]) + "Pin: " + what + " must be an integer constant");
      }
      return r.Value;
    }

    /// <summary>Объявление без явного типа: тип выведет InferTypes по инициализатору init.</summary>
    /// <remarks>Только то, что может стать float: целая переменная, локальная, параметр, поле. Область -
    /// текущая: инициализатор ссылается на переменные той функции, где объявлен. У поля объявления нет,
    /// поэтому сюда идёт каждое его нетипизированное присваивание.</remarks>
    internal void DeferType(Merker m, Expression init) {
      if(m == null || init == null) {
        return;
      }
      if(m.type == EP_Type.SINT32 || m.type == EP_Type.LOCAL || m.type == EP_Type.PARAMETER || m.type == EP_Type.PropS4) {
        _untyped.Add(Tuple.Create(m, init, cur));
      }
    }
    /// <summary>Вызов: его аргументы задают тип параметров, см. InferTypes.</summary>
    /// <remarks>Цель разрешается позже, в InferTypes: функция или метод бывают объявлены ниже вызова.</remarks>
    internal void DeferCall(Call ca) {
      _calls.Add(Tuple.Create(ca, cur));
    }
    /// <summary>Функция, метод или конструктор программы, который вызывает ca; иначе null.</summary>
    private Merker Callee(Call ca) {
      Variable f;
      Property p;
      Merker m = null;
      if((f = ca.Children[0] as Variable) != null) {
        m = GetMerker(f.Descriptor);
        if(m != null && m.type == EP_Type.CLASS && ca.CallMode == CallMode.Construct) {
          m = m.scope == null ? null : m.scope.GetProperty("constructor");
        }
      } else if((p = ca.Children[0] as Property) != null && MathFunction(p) == null) {
        m = FindMethod(p);
      }
      return m != null && m.type == EP_Type.FUNCTION && m.scope != null ? m : null;
    }

    /// <summary>Вывод типов между VP1 и VP2: переменная и поле float, если float их значение, параметр -
    /// если float значение по умолчанию или аргумент хотя бы одного вызова, функция - если float хотя
    /// бы один её return.</summary>
    /// <remarks>VP2 должен знать типы раньше, чем скомпилирует использующий их код: тело функции
    /// бывает ниже вызова, переменная - ниже функции, которая её читает. Признак меняется только
    /// Int->Float, поэтому цикл конечен, а повторные круги разрешают ссылки вперёд, цепочки вида
    /// var b = a * 2 и взаимную рекурсию.
    /// <para>Параметр по аргументам - ради блоков DeviceLogram: тип входа Func известен по
    /// подключённому источнику и не объявляется. Без этого float-аргумент усекался до целого.</para></remarks>
    private void InferTypes() {
      foreach(var call in _calls) {
        var fm = InScope(call.Item2, () => Callee(call.Item1));
        if(fm == null) {
          continue;
        }
        foreach(var prm in fm.scope.memory.Where(z => z.type == EP_Type.PARAMETER && z.Addr >= 1 && z.Addr <= call.Item1.Arguments.Length)) {
          _untyped.Add(Tuple.Create(prm, call.Item1.Arguments[prm.Addr - 1], call.Item2));
        }
      }
      var fns = _programm.Select(z => z.fm).Where(z => z != null && z.type == EP_Type.FUNCTION && z.returns != null).ToArray();
      bool changed;
      do {
        changed = false;
        foreach(var u in _untyped) {
          var m = u.Item1;
          if(IsFloat(m) || m.explicitType || InScope(u.Item3, () => TypeOf(u.Item2)) != EP_ValType.Float) {
            continue;
          }
          if(m.type == EP_Type.SINT32) {
            m.type = EP_Type.FLOAT;   // Md -> Mf: размер тот же, память ещё не размечена
          } else if(m.type == EP_Type.PropS4) {
            m.type = EP_Type.PropF4;  // размер тот же, поэтому разметка полей до этого прохода остаётся верной
          } else {
            m.isFloat = true;         // LOCAL, PARAMETER
          }
          changed = true;
        }
        foreach(var fm in fns) {
          if(fm.isFloat || !InScope(fm.scope, () => fm.returns.Any(z => TypeOf(z) == EP_ValType.Float))) {
            continue;
          }
          fm.isFloat = true;
          changed = true;
        }
      } while(changed);
    }
    /// <summary>f() с cur = sc, как его видел бы VP2 внутри этой области.</summary>
    private T InScope<T>(Scope sc, Func<T> f) {
      var prev = cur;
      _scope.Push(sc);
      cur = sc;
      try {
        return f();
      }
      finally {
        _scope.Pop();
        cur = prev;
      }
    }

    /// <summary>Тип значения выражения на стеке VM.</summary>
    /// <remarks>Считается по AST до эмиссии: левый операнд к моменту разбора правого уже лежит
    /// под ним, и привести его задним числом можно только через SWAP. Всё, что здесь не
    /// распознано, - Int, как было до появления float.</remarks>
    internal EP_ValType TypeOf(Expression e) {
      if(e is Comma) return TypeOf(e.Children.Last() as Expression);
      Constant c;
      Variable v;
      Property p;
      SetProperty sp;
      AssignmentOperatorCache aoc;
      Conditional cd;
      Call ca;

      if(e == null) {
        return EP_ValType.Int;
      }
      if((c = e as Constant) != null) {
        return IsFloatConst(c) ? EP_ValType.Float : EP_ValType.Int;
      }
      if((aoc = e as AssignmentOperatorCache) != null) {
        return TypeOf(aoc.Source as Expression);
      }
      if((v = e as Variable) != null) {
        return IsFloat(GetMerker(v.Descriptor)) ? EP_ValType.Float : EP_ValType.Int;
      }
      if((p = e as Property) != null) {
        return IsFloat(FindProperty(PropertyScope(p.Source as Expression), p.FieldName as Expression)) ? EP_ValType.Float : EP_ValType.Int;
      }
      if((sp = e as SetProperty) != null) {
        return IsFloat(FindProperty(PropertyScope(sp.Source as Expression), sp.FieldName as Expression)) ? EP_ValType.Float : EP_ValType.Int;
      }
      if(e is Assignment) {
        return TypeOf(e.Children[0] as Expression);
      }
      if(e is Addition || e is NumberAddition || e is Substract || e is Multiplication || e is Division) {
        return TypeOf(e.Children[0] as Expression) == EP_ValType.Float || TypeOf(e.Children[1] as Expression) == EP_ValType.Float ? EP_ValType.Float : EP_ValType.Int;
      }
      if(e is Negation || e is Increment || e is Decrement || e is ConvertToNumber) {
        return TypeOf(e.Children[0] as Expression) == EP_ValType.Float ? EP_ValType.Float : EP_ValType.Int;
      }
      // NiL сворачивает x | 0, x >> 0 и x << 0 в ConvertToInteger, а x >>> 0 - в ConvertToUnsignedInteger
      if(e is UnsignedShiftRight || e is ConvertToUnsignedInteger) {
        return EP_ValType.UInt;
      }
      if(e is ConvertToInteger) {
        return EP_ValType.Int;
      }
      if((cd = e as Conditional) != null) {
        return cd.Branches.Any(z => TypeOf(z) == EP_ValType.Float) ? EP_ValType.Float : EP_ValType.Int;
      }
      if((ca = e as Call) != null) {
        return CallType(ca);
      }
      return EP_ValType.Int;
    }
    private EP_ValType CallType(Call ca) {
      Variable f;
      Property p;
      Merker m;
      if((f = ca.Children[0] as Variable) != null) {
        m = GetMerker(f.Descriptor);
        if(m != null) {
          if(m.type == EP_Type.API) return m.ResultType ?? EP_ValType.Int;
          return m.type == EP_Type.FUNCTION && m.isFloat ? EP_ValType.Float : EP_ValType.Int;
        }
        switch(f.Name) {
        case "Float":
          return EP_ValType.Float;
        case "UInt32":
          return EP_ValType.UInt;
        default:
          return EP_ValType.Int;   // Int32, isNaN, isFinite
        }
      }
      if((p = ca.Children[0] as Property) != null) {
        if(MathFunction(p) != null) {
          return EP_ValType.Float;
        }
        m = FindMethod(p);
        return m != null && m.type == EP_Type.FUNCTION && m.isFloat ? EP_ValType.Float : EP_ValType.Int;
      }
      return EP_ValType.Int;
    }
    internal static bool IsFloat(Merker m) {
      if(m == null) {
        return false;
      }
      switch(m.type) {
      case EP_Type.FLOAT:
      case EP_Type.PropF4:
        return true;
      case EP_Type.LOCAL:
      case EP_Type.PARAMETER:
        return m.isFloat;
      default:
        return false;
      }
    }
    internal static bool IsFloatConst(Constant c) {
      return c != null && c.Value != null && c.Value.ValueType == JSValueType.Double;
    }
    /// <summary>Math.abs/min/max/round/trunc - имя функции, иначе null.</summary>
    /// <remarks>Только если Math не объявлен в программе: своя переменная Math - обычный объект.</remarks>
    internal string MathFunction(Property p) {
      Variable v;
      Constant c;
      if((v = p.Source as Variable) == null || v.Name != "Math" || GetMerker(v.Descriptor) != null) {
        return null;
      }
      if((c = p.FieldName as Constant) == null || c.Value == null || c.Value.ValueType != JSValueType.String) {
        return null;
      }
      switch(c.Value.ToString()) {
      case "abs":
      case "min":
      case "max":
      case "round":
      case "trunc":
        return c.Value.ToString();
      default:
        return null;
      }
    }
    /// <summary>Область полей объекта obj (REFERENCE) или this (ближайший класс), иначе null.</summary>
    internal Scope PropertyScope(Expression src) {
      Variable f;
      Merker m;
      if((f = src as Variable) != null) {
        m = GetMerker(f.Descriptor);
        return m != null && m.type == EP_Type.REFERENCE ? m.scope : null;
      }
      if(src is This) {
        var sc = cur;
        while(sc.fm != null && sc.fm.type != EP_Type.CLASS) {
          sc = sc._parent;
          if(sc == null) {
            sc = cur;
            break;
          }
        }
        return sc;
      }
      return null;
    }
    /// <summary>Меркер поля name в области sc, с запасным поиском во внешней области.</summary>
    internal Merker FindProperty(Scope sc, Expression name) {
      Constant c;
      Merker m;
      if(sc == null || (c = name as Constant) == null || c.Value == null || c.Value.ValueType != JSValueType.String) {
        return null;
      }
      string pn = c.Value.ToString();
      // GetProperty разыменовывает fm, а у глобальной области его нет
      m = sc.fm == null ? null : sc.GetProperty(pn);
      if(m == null) {
        sc = _scope.Skip(1).FirstOrDefault();
        if(sc != null && sc.fm != null) {
          m = sc.GetProperty(pn);
        }
      }
      return m;
    }
    /// <summary>Меркер метода obj.name() / this.name(), иначе null.</summary>
    internal Merker FindMethod(Property p) {
      Variable f;
      Merker m;
      Constant c;
      Scope sc = null;
      if((f = p.Source as Variable) != null && (m = GetMerker(f.Descriptor)) != null) {
        sc = m.scope;
      } else if(p.Source is This) {
        sc = cur;
      }
      if(sc == null || sc.fm == null || (c = p.FieldName as Constant) == null || c.Value == null || c.Value.ValueType != JSValueType.String) {
        return null;
      }
      return sc.GetProperty(c.Value.ToString());
    }

    /// <summary>API-функция узла по имени, иначе null.</summary>
    /// <remarks>Функция другого узла - ошибка компиляции: сам узел остановил бы программу с 0x88 уже при
    /// работе, на первом вызове.</remarks>
    private Merker LoadNativeFunctions(VariableDescriptor v) {
      Merker m;
      EP_Target? only = null;   // есть только у этого узла
      switch(v.Name) {
      case "TwiControl":
        m = Merker.Api(1, null, EP_ValType.Int);
        break;
      case "TwiStatus":
        m = Merker.Api(2, EP_ValType.Int);
        break;
      case "TwiPutByte":
        m = Merker.Api(3, null, EP_ValType.Int);
        break;
      case "TwiGetByte":
        m = Merker.Api(4, EP_ValType.Int);
        break;
      case "NodeStatus":
        m = Merker.Api(5, EP_ValType.Int);
        break;
      case "getMilliseconds":
        m = Merker.Api(6, EP_ValType.Int);
        break;
      case "getSeconds":
        m = Merker.Api(7, EP_ValType.Int);
        break;
      case "Random":
        m = Merker.Api(8, EP_ValType.Int);
        break;
      case "NowSeconds":    // число секунд с 0:00:00
        m = Merker.Api(9, EP_ValType.Int);
        break;
      case "Today":         //  (year[0..99]<<24) | (month[1..12]<<16) | (day[1..31]<<8) | (dayOfWeek[1-понедельник..7-воскресенье])
        m = Merker.Api(10, EP_ValType.Int);
        break;
      case "UartInit":  // void UartInit(port, speed); на EspNode порт открывает ConfigPin
        m = Merker.Api(20, null, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.X13;
        break;
      case "UartBytesToRead":  // int UartBytesToRead(port)
        m = Merker.Api(21, EP_ValType.Int, EP_ValType.Int);
        break;
      case "UartGetByte":  // int UartGetByte(port)
        m = Merker.Api(22, EP_ValType.Int, EP_ValType.Int);
        break;
      case "UartPutByte":  // bool UartPutByte(port, data)
        m = Merker.Api(23, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int);
        break;
      case "SendByte":
        m = Merker.Api(23, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      case "PrintF":
        m = Merker.Api(API_PRINT_F, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      case "LOG_DEBUG":
      case "LOG_INFO":
      case "LOG_WARN":
      case "LOG_ERROR":
        m = new Merker() { type = EP_Type.CONST, Addr = v.Name == "LOG_DEBUG" ? 0xFFE0u : v.Name == "LOG_INFO" ? 0xFFE1u : v.Name == "LOG_WARN" ? 0xFFE2u : 0xFFE3u,
          vd = v, pName = v.Name, fName = v.Name };
        only = EP_Target.EspNode;
        break;
      case "ConfigPin":  // void ConfigPin(pin, mode, option)
        m = Merker.Api(API_CONFIG_PIN, null, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      // TwiControl..TwiGetByte для любой шины, номер шины - первый аргумент
      case "TwiBusControl":  // void TwiBusControl(bus, ctrl)
        m = Merker.Api(40, null, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      case "TwiBusStatus":  // int TwiBusStatus(bus)
        m = Merker.Api(41, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      case "TwiBusPutByte":  // void TwiBusPutByte(bus, data)
        m = Merker.Api(42, null, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      case "TwiBusGetByte":  // int TwiBusGetByte(bus)
        m = Merker.Api(43, EP_ValType.Int, EP_ValType.Int);
        only = EP_Target.EspNode;
        break;
      default:
        int c;
        if(Device == null || !Device.TryConst(v.Name, out c)) {
          return null;
        }
        m = new Merker() { type = EP_Type.CONST, Addr = (uint)c, vd = v, pName = v.Name, fName = v.Name };   // режим или пин из описания узла
        break;
      }
      if(only != null && only != Target) {
        throw new NotSupportedException(v.Name + " is not available on " + Target);
      }
      m.vd = v;
      global.memory.Add(m);
      return m;
    }
    private void CompilerMessageCallback(MessageLevel level, CodeCoordinates coords, string message) {
      if(message == "Type of an expression is ambiguous") {
        return;
      }
      var cm = CMsg;
      if(cm != null) {
        cm(level, coords, message);
        return;
      }
      var msg = string.Format("[{0}, {1}] {2}", coords.Line, coords.Column, message);
      switch(level) {
      case MessageLevel.Error:
        Log.Error("{0}", msg);
        break;
      case MessageLevel.CriticalWarning:
      case MessageLevel.Warning:
        Log.Warning("{0}", msg);
        break;
      case MessageLevel.Recomendation:
        Log.Info("{0}", msg);
        break;
      default:
        Log.Debug("{0}", msg);
        break;
      }
    }

    internal class DP_MemBlock : IComparable<DP_MemBlock> {
      public readonly uint start;
      public readonly uint end;

      public DP_MemBlock(uint start, uint end) {
        this.start = start;
        this.end = end;
      }
      public int CompareTo(DP_MemBlock other) {
        return other == null ? int.MaxValue : this.start.CompareTo(other.start);
      }
      public bool Check(uint length, int o) {
        return ( length + ( o - ( start % o ) ) % o ) <= end - start + 1;
      }
    }
    internal class Merker {
      public uint Addr;
      public EP_Type type;
      public VariableDescriptor vd;
      public Expression init;
      public Scope scope;
      public bool initialized;
      /// <summary>API: аргументы в порядке исходника; null ResultType означает отсутствие результата.</summary>
      public EP_ValType[] ParameterTypes = Array.Empty<EP_ValType>();
      public EP_ValType? ResultType;
      /// <summary>Длина константного массива для CHECK_IDX.</summary>
      public int ElementCount;
      /// <summary>Размер экземпляра REFERENCE в 32-битных словах.</summary>
      public int InstanceWords;
      public string pName;
      public string fName;
      /// <summary>Float там, где собственного float-типа нет: слот стека (LOCAL, PARAMETER)
      /// или возвращаемое значение (FUNCTION).</summary>
      public bool isFloat;
      /// <summary>Выражения return функции; собирает VP1 для InferTypes.</summary>
      public List<Expression> returns;
      /// <summary>Настройка экземпляра Pin; не null - этот REFERENCE экземпляр Pin.</summary>
      public PinCfg pin;
      /// <summary>Поле хоть раз задано явно (new Int32(...)): InferTypes его тип не трогает.</summary>
      public bool explicitType;
      public static Merker Api(uint number, EP_ValType? resultType, params EP_ValType[] parameterTypes) {
        return new Merker { type = EP_Type.API, Addr = number, ParameterTypes = parameterTypes, ResultType = resultType };
      }
      public override string ToString() {
        return fName;
      }
    }
    internal sealed class PinCfg {
      public int Gpio;
      public EP_Device.Mode Mode;
      public int Option;
    }
    internal class Scope {
      private EP_Compiler _compiler;
      public Scope _parent;
      public List<Instruction> code;
      public List<Merker> memory;
      public Stack<EP_VP2.Loop> loops;
      public Merker fm;
      public SortedSet<DP_MemBlock> memBlocks;


      public Scope(EP_Compiler c, Merker fm, Scope parent) {
        _compiler = c;
        _parent = parent;
        this.fm = fm;
        memBlocks = new SortedSet<DP_MemBlock>();
        memBlocks.Add(new DP_MemBlock(0, 16384));
        code = new List<Instruction>();
        memory = new List<Merker>();
        loops = new Stack<EP_VP2.Loop>();
      }
      public void AddInst(Instruction inst, int pop = 0, int push = 0) {
        int i;
        for(i = 0; i < pop; i++) {
          _compiler._sp.Pop();
        }
        if(inst._at < 0 && _compiler.Statement != null) {
          inst._at = _compiler.Statement.Position;
        }
        code.Add(inst);
        for(i = 0; i < push; i++) {
          _compiler._sp.Push(inst);
        }
      }
      public void AddInst(EP_InstCode ic, int pop = 0, int push = 0) {
        int i;
        Instruction inst;
        for(i = 0; i < pop; i++) {
          _compiler._sp.Pop();
        }
        code.Add(inst = new Instruction(ic));
        if(_compiler.Statement != null) {
          inst._at = _compiler.Statement.Position;
        }
        for(i = 0; i < push; i++) {
          _compiler._sp.Push(inst);
        }
      }
      public uint AllocateMemory(uint addr, uint length) {
        DP_MemBlock fb;
        uint start, end;

        if(addr == uint.MaxValue) {
          int o;
          if(length > 16) {
            o = 32;
          } else if(length > 8) {
            o = 16;
          } else if(length > 1) {
            o = 8;
          } else {
            o = 1;
          }

          fb = memBlocks.FirstOrDefault(z => z.Check(length, o));
          if(fb == null) {
            throw new ArgumentOutOfRangeException("Not enough memory");
          }
          memBlocks.Remove(fb);
          start = (uint)( fb.start + ( o - ( fb.start % o ) ) % o );
          end = start + length - 1;
          if(fb.start < start) {
            memBlocks.Add(new DP_MemBlock(fb.start, start - 1));
          }
          if(fb.end > end) {
            memBlocks.Add(new DP_MemBlock(end + 1, fb.end));
          }
        } else {
          start = addr;
          end = addr + length - 1;
          do {
            fb = memBlocks.FirstOrDefault(z => z.start <= end && z.end >= start);
            if(fb == null) {
              break;
            }
            memBlocks.Remove(fb);
            if(fb.start < start) {
              memBlocks.Add(new DP_MemBlock(fb.start, start - 1));
            }
            if(fb.end > end) {
              memBlocks.Add(new DP_MemBlock(end + 1, fb.end));
            }
          } while(fb != null);
        }
        //{
        //  StringBuilder sb = new StringBuilder();
        //  sb.AppendFormat("AllocateMemory({0:X4}{2}, {1:X2})\n", start, length, addr==uint.MaxValue?"*":"");
        //  foreach(var m in global.memBlocks) {
        //    sb.AppendFormat("  {0:X4}:{1:X4}\n", m.start, m.end);
        //  }
        //  Log.Info("{0}", sb.ToString());
        //}
        return start;
      }
      public override string ToString() {
        var sb = new StringBuilder();
        int ls = 0;
        if(fm != null) {
          sb.Append(fm.ToString());
        }
        sb.Append("\n");
        byte[] hex;
        int j;
        for(int i = 0; i < code.Count; i++) {
          var c = code[i];
          sb.Append(c.addr.ToString("X4"));
          sb.Append(" ");
          hex = c._code;
          for(j = 0; j < 8; j++) {
            if(j < hex.Length) {
              sb.Append(hex[j].ToString("X2"));
              sb.Append(" ");
            } else {
              sb.Append("   ");
            }
          }
          sb.Append("| ").Append(c.ToString());
          if(c._cn != null) {
            while(( sb.Length - ls ) < 50) {
              sb.Append(" ");
            }
            sb.Append("; ").Append(c._cn.ToString());
          }
          sb.Append("\r\n");
          ls = sb.Length;
          for(; j < hex.Length; j++) {
            if(( j & 7 ) == 0) {
              sb.Append(( c.addr + j ).ToString("X4"));
              sb.Append(" ");
            }
            sb.Append(hex[j].ToString("X2"));
            if(( j & 7 ) == 7 || j == hex.Length - 1) {
              sb.Append("\r\n");
            } else {
              sb.Append(" ");
            }
          }

        }
        return sb.ToString();
      }
      public Merker GetProperty(string name, EP_Type type = EP_Type.NONE) {
        Merker m;
        string fName = fm.fName + "." + name;
        m = memory.FirstOrDefault(z => z.pName == name);
        if(m == null && _parent != null) {
          m = _parent.memory.FirstOrDefault(z => z.pName == name);
        }
        if(type != EP_Type.NONE && m == null && !string.IsNullOrEmpty(name)) {
          m = new Merker() { fName = fName, pName = name, type = type };
          memory.Add(m);
        }
        return m;
      }
      public void AllocatFields() {
        uint mLen;
        foreach(var m in memory.OrderBy(z => z.type)) {
          switch(m.type) {
          case EP_Type.PropB1:
            mLen = 1;
            break;
          case EP_Type.PropU1:
          case EP_Type.PropS1:
            mLen = 8;
            break;
          case EP_Type.PropU2:
          case EP_Type.PropS2:
            mLen = 16;
            break;
          case EP_Type.PropS4:
          case EP_Type.PropF4:
            mLen = 32;
            break;
          default:
            continue;
          }
          m.Addr = AllocateMemory(uint.MaxValue, mLen) / ( mLen >= 32 ? 32 : mLen );
        }
      }
      public void Optimize() {
        int i, j, cnt;
        Instruction i0, i1;
        if(code.Count == 0) {
          return;
        }
        if(( i0 = code[code.Count - 1] )._code.Length != 1 || i0._code[0] != (byte)EP_InstCode.RET) {
          AddInst(EP_InstCode.RET);
        }
        if(this == _compiler.dataBlock) return;   // Байты литералов не являются командами RET/JMP/DUP.
        // удалить недостижимый код
        bool fl = false;
        for(i = 0; i < code.Count; ) {
          i0 = code[i];
          if(fl) {
            if(i0._code.Length == 0) {
              fl = false;
            } else {
              code.RemoveAt(i);
              continue;
            }
          } else if(i0._code.Length > 0 && ( i0._code[0] == (byte)EP_InstCode.RET || i0._code[0] == (byte)EP_InstCode.JMP )) {
            fl = true;
          }
          i++;
        }
        do {
          cnt = 0;
          i = 0;
          while(i < code.Count) {
            i0 = code[i];
            // цепочка переходов  {jmp l0 ... :l0 jmp l1}
            if(i0._code.Length == 3 && ( i0._code[0] == (byte)EP_InstCode.JMP || i0._code[0] == (byte)EP_InstCode.JZ || i0._code[0] == (byte)EP_InstCode.JNZ )) {
              for(j = 0; j < code.Count; j++) {
                if(code[j] == i0._ref) {
                  do {
                    j++;
                    i1 = code[j];
                  } while(i1._code.Length == 0);

                  if(i1._code.Length == 3 && i1._code[0] == (byte)EP_InstCode.JMP) {
                    i0._ref = i1._ref;
                    cnt++;
                  } else if(i1._code.Length == 1 && i1._code[0] == (byte)EP_InstCode.RET && i0._code[0] == (byte)EP_InstCode.JMP) {
                    code[i] = i0 = new Instruction(EP_InstCode.RET);
                  }
                  break;
                }
              }
            }
            // удалить все DROP и NIP перед RET
            if(i0._code.Length == 1 && i0._code[0] == (byte)EP_InstCode.RET) {
              for(j = i - 1; j >= 0; j--) {
                i1 = code[j];
                if(i1._code.Length == 1 && ( i1._code[0] == (byte)EP_InstCode.DROP || i1._code[0] == (byte)EP_InstCode.NIP )) {
                  code.RemoveAt(j);
                  i--;
                  cnt++;
                } else if(i1._code.Length != 0) {   // не метка
                  break;
                }
              }
            }
            i++;
          }
        } while(cnt > 0);
      }
    }
    internal class Instruction {
      internal uint addr;
      internal byte[] _code;
      internal Merker _param;
      internal CodeNode _cn;
      internal Instruction _ref;
      internal bool _blob;
      internal bool _programAddress;
      /// <summary>LDI_* грузит _fValue как float, а не константу _cn как int.</summary>
      internal bool _isFloat;
      internal double _fValue;
      /// <summary>Позиция оператора, внутри которого выдана команда; -1 - вне оператора. Для команд без _cn.</summary>
      internal int _at = -1;

      public bool canOptimized;

      public Instruction(EP_InstCode cmd, Merker param = null, CodeNode cn = null) {
        _param = param;
        _cn = cn;
        _blob = false;
        Prepare(cmd);
      }
      /// <summary>Загрузка float-константы: дробной или целой в float-контексте.</summary>
      /// <param name="cn">узел для листинга, может быть null</param>
      public static Instruction LoadFloat(double value, CodeNode cn) {
        var i = new Instruction(EP_InstCode.LABEL, null, cn) { _isFloat = true, _fValue = value };
        i.Prepare(EP_InstCode.LDI_S4);
        return i;
      }
      /// <summary>Загрузка целого; самую короткую LDI_* выберет Prepare.</summary>
      /// <remarks>Значение несёт меркер CONST, а не cn: узел может и не быть константой - тогда LDI
      /// загрузила бы 0.</remarks>
      /// <param name="cn">узел для листинга, может быть null</param>
      public static Instruction LoadInt(int value, CodeNode cn) {
        return new Instruction(EP_InstCode.LDI_S4, new Merker() { type = EP_Type.CONST, Addr = (uint)value }, cn);
      }
      /// <summary>LSL/LSR/ASR на число бит, известное при компиляции; берётся по модулю 32, как в JS.</summary>
      /// <remarks>Число несёт меркер CONST: cn может и не быть константой.</remarks>
      /// <param name="cn">узел для листинга, может быть null</param>
      public static Instruction Shift(EP_InstCode cmd, int count, CodeNode cn) {
        return new Instruction(cmd, new Merker() { type = EP_Type.CONST, Addr = (uint)count }, cn);
      }
      // Адрес ROM известен после раскладки; размер команды не должен меняться при Link.
      public static Instruction LoadProgramAddress(Merker data, CodeNode cn) {
        var instruction = new Instruction(EP_InstCode.LDI_U2, data, cn) { _programAddress = true };
        instruction.Prepare(EP_InstCode.LDI_U2);
        return instruction;
      }
      internal static int FloatBits(double v) {
        return BitConverter.ToInt32(BitConverter.GetBytes((float)v), 0);
      }
      public Instruction(byte[] arr) {
        _blob = true;
        _code = arr;
      }
      public bool Link() {
        if(( _param == null && _ref == null )) {
          return true;
        }
        if(_blob) {
          return true;
        }
        Prepare((EP_InstCode)_code[0]);
        return false;
      }
      private void Prepare(EP_InstCode cmd) {
        if(_programAddress) {
          if(_param.Addr > ushort.MaxValue) throw new NotSupportedException("PrintF format address exceeds the VM address space");
          _code = new[] { (byte)EP_InstCode.LDI_U2, (byte)_param.Addr, (byte)(_param.Addr >> 8) };
          return;
        }
        int tmp_d;
        uint tmp_D;
        switch(cmd) {
        case EP_InstCode.LABEL:
          if(_code == null || _code.Length != 0) {
            _code = new byte[0];
          }
          break;
        case EP_InstCode.NOP:
        case EP_InstCode.DUP:
        case EP_InstCode.DROP:
        case EP_InstCode.NIP:
        case EP_InstCode.SWAP:
        case EP_InstCode.OVER:
        case EP_InstCode.ROT:
        case EP_InstCode.NOT:
        case EP_InstCode.AND:
        case EP_InstCode.OR:
        case EP_InstCode.XOR:
        case EP_InstCode.ADD:
        case EP_InstCode.SUB:
        case EP_InstCode.MUL:
        case EP_InstCode.DIV:
        case EP_InstCode.MOD:
        case EP_InstCode.INC:
        case EP_InstCode.DEC:
        case EP_InstCode.NEG:
        case EP_InstCode.CEQ:
        case EP_InstCode.CNE:
        case EP_InstCode.CGT:
        case EP_InstCode.CGE:
        case EP_InstCode.CLT:
        case EP_InstCode.CLE:
        case EP_InstCode.CZE:
        case EP_InstCode.FADD:
        case EP_InstCode.FSUB:
        case EP_InstCode.FMUL:
        case EP_InstCode.FDIV:
        case EP_InstCode.FNEG:
        case EP_InstCode.FABS:
        case EP_InstCode.FMIN:
        case EP_InstCode.FMAX:
        case EP_InstCode.FCEQ:
        case EP_InstCode.FCNE:
        case EP_InstCode.FCGT:
        case EP_InstCode.FCGE:
        case EP_InstCode.FCLT:
        case EP_InstCode.FCLE:
        case EP_InstCode.FCZE:
        case EP_InstCode.FISFINITE:
        case EP_InstCode.S32TOF:
        case EP_InstCode.U32TOF:
        case EP_InstCode.FTOS32:
        case EP_InstCode.FTOU32:
        case EP_InstCode.FROUND:
        case EP_InstCode.FTRUNC:
        case EP_InstCode.FISNAN:
        case EP_InstCode.FISINF:
        case EP_InstCode.LD_P0:
        case EP_InstCode.LD_P1:
        case EP_InstCode.LD_P2:
        case EP_InstCode.LD_P3:
        case EP_InstCode.LD_P4:
        case EP_InstCode.LD_P5:
        case EP_InstCode.LD_P6:
        case EP_InstCode.LD_P7:
        case EP_InstCode.LD_P8:
        case EP_InstCode.LD_P9:
        case EP_InstCode.LD_PA:
        case EP_InstCode.LD_PB:
        case EP_InstCode.LD_PC:
        case EP_InstCode.LD_PD:
        case EP_InstCode.LD_PE:
        case EP_InstCode.LD_PF:
        case EP_InstCode.LD_L0:
        case EP_InstCode.LD_L1:
        case EP_InstCode.LD_L2:
        case EP_InstCode.LD_L3:
        case EP_InstCode.LD_L4:
        case EP_InstCode.LD_L5:
        case EP_InstCode.LD_L6:
        case EP_InstCode.LD_L7:
        case EP_InstCode.LD_L8:
        case EP_InstCode.LD_L9:
        case EP_InstCode.LD_LA:
        case EP_InstCode.LD_LB:
        case EP_InstCode.LD_LC:
        case EP_InstCode.LD_LD:
        case EP_InstCode.LD_LE:
        case EP_InstCode.LD_LF:
        case EP_InstCode.ST_P0:
        case EP_InstCode.ST_P1:
        case EP_InstCode.ST_P2:
        case EP_InstCode.ST_P3:
        case EP_InstCode.ST_P4:
        case EP_InstCode.ST_P5:
        case EP_InstCode.ST_P6:
        case EP_InstCode.ST_P7:
        case EP_InstCode.ST_P8:
        case EP_InstCode.ST_P9:
        case EP_InstCode.ST_PA:
        case EP_InstCode.ST_PB:
        case EP_InstCode.ST_PC:
        case EP_InstCode.ST_PD:
        case EP_InstCode.ST_PE:
        case EP_InstCode.ST_PF:
        case EP_InstCode.ST_L0:
        case EP_InstCode.ST_L1:
        case EP_InstCode.ST_L2:
        case EP_InstCode.ST_L3:
        case EP_InstCode.ST_L4:
        case EP_InstCode.ST_L5:
        case EP_InstCode.ST_L6:
        case EP_InstCode.ST_L7:
        case EP_InstCode.ST_L8:
        case EP_InstCode.ST_L9:
        case EP_InstCode.ST_LA:
        case EP_InstCode.ST_LB:
        case EP_InstCode.ST_LC:
        case EP_InstCode.ST_LD:
        case EP_InstCode.ST_LE:
        case EP_InstCode.ST_LF:
        case EP_InstCode.LDM_B1_S:
        case EP_InstCode.LDM_S1_S:
        case EP_InstCode.LDM_S2_S:
        case EP_InstCode.LDM_S4_S:
        case EP_InstCode.LDM_U1_S:
        case EP_InstCode.LDM_U2_S:
        case EP_InstCode.STM_B1_S:
        case EP_InstCode.STM_S1_S:
        case EP_InstCode.STM_S2_S:
        case EP_InstCode.STM_S4_S:
        case EP_InstCode.LDI_MIN:
        case EP_InstCode.SJMP:
        case EP_InstCode.SCALL:
        case EP_InstCode.RET:
          if(_code == null || _code.Length != 1) {
            _code = new byte[1];
          }
          _code[0] = (byte)cmd;
          break;
        case EP_InstCode.LSL:
        case EP_InstCode.LSR:
        case EP_InstCode.ASR:
          if(_code == null || _code.Length != 2) {
            _code = new byte[2];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)( _param.Addr & 0x1F );
          break;
        case EP_InstCode.LDM_B1_CS8:
        case EP_InstCode.STM_B1_CS8:

        case EP_InstCode.LDM_S1_CS8:
        case EP_InstCode.STM_S1_CS8:
        case EP_InstCode.LDM_U1_CS8:

        case EP_InstCode.LDM_S2_CS8:
        case EP_InstCode.STM_S2_CS8:
        case EP_InstCode.LDM_U2_CS8:

        case EP_InstCode.LDM_S4_CS8:
        case EP_InstCode.STM_S4_CS8:
          if(_code == null || _code.Length != 2) {
            _code = new byte[2];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)_param.Addr;
          break;
        case EP_InstCode.LDM_B1_C16:
        case EP_InstCode.LDM_B1_CS16:
        case EP_InstCode.STM_B1_C16:
        case EP_InstCode.STM_B1_CS16:

        case EP_InstCode.LDM_S1_C16:
        case EP_InstCode.LDM_S1_CS16:
        case EP_InstCode.STM_S1_C16:
        case EP_InstCode.STM_S1_CS16:

        case EP_InstCode.LDM_S2_C16:
        case EP_InstCode.LDM_S2_CS16:
        case EP_InstCode.STM_S2_C16:
        case EP_InstCode.STM_S2_CS16:

        case EP_InstCode.LDM_S4_C16:
        case EP_InstCode.LDM_S4_CS16:
        case EP_InstCode.STM_S4_C16:
        case EP_InstCode.STM_S4_CS16:

        case EP_InstCode.LDM_U1_C16:
        case EP_InstCode.LDM_U1_CS16:

        case EP_InstCode.LDM_U2_C16:
        case EP_InstCode.LDM_U2_CS16:

        case EP_InstCode.LPM_S1:
        case EP_InstCode.LPM_S2:
        case EP_InstCode.LPM_S4:
        case EP_InstCode.LPM_U1:
        case EP_InstCode.LPM_U2:

        case EP_InstCode.CALL:
          if(_code == null || _code.Length != 3) {
            _code = new byte[3];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)_param.Addr;
          _code[2] = (byte)( _param.Addr >> 8 );
          break;
        case EP_InstCode.API:
          if(_code == null || _code.Length != 2) {
            _code = new byte[2];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)_param.Addr;
          break;
        case EP_InstCode.LDI_0:
        case EP_InstCode.LDI_1:
        case EP_InstCode.LDI_M1:
        case EP_InstCode.LDI_S1:
        case EP_InstCode.LDI_U1:
        case EP_InstCode.LDI_S2:
        case EP_InstCode.LDI_U2:
        case EP_InstCode.LDI_S4:
          if(_param != null && ( _param.type == EP_Type.REFERENCE || _param.type == EP_Type.FUNCTION || _param.type == EP_Type.CONST )) {
            tmp_d = (int)_param.Addr;
          } else if(_isFloat) {
            // Битовый шаблон float; сжатие ниже работает и для него: 0.0f -> LDI_0 и т.д.
            tmp_d = FloatBits(_fValue);
          } else if(( _cn as Constant ) != null) {
            tmp_d = (int)( (Constant)_cn ).Value;
          } else {
            tmp_d = 0;
          }

          if(tmp_d == 0) {
            if(_code == null || _code.Length != 1) {
              _code = new byte[1];
            }
            _code[0] = (byte)EP_InstCode.LDI_0;
          } else if(tmp_d == 1) {
            if(_code == null || _code.Length != 1) {
              _code = new byte[1];
            }
            _code[0] = (byte)EP_InstCode.LDI_1;
          } else if(tmp_d == -1) {
            if(_code == null || _code.Length != 1) {
              _code = new byte[1];
            }
            _code[0] = (byte)EP_InstCode.LDI_M1;
          } else if(tmp_d > -128 && tmp_d < 256) {
            if(_code == null || _code.Length != 2) {
              _code = new byte[2];
            }
            _code[0] = (byte)( tmp_d < 0 ? EP_InstCode.LDI_S1 : EP_InstCode.LDI_U1 );
            _code[1] = (byte)tmp_d;
          } else if(tmp_d > -32768 && tmp_d < 65536) {
            if(_code == null || _code.Length != 3) {
              _code = new byte[3];
            }
            _code[0] = (byte)( tmp_d < 0 ? EP_InstCode.LDI_S2 : EP_InstCode.LDI_U2 );
            _code[1] = (byte)tmp_d;
            _code[2] = (byte)( tmp_d >> 8 );

          } else {
            if(_code == null || _code.Length != 5) {
              _code = new byte[5];
            }
            _code[0] = (byte)EP_InstCode.LDI_S4;
            _code[1] = (byte)tmp_d;
            _code[2] = (byte)( tmp_d >> 8 );
            _code[3] = (byte)( tmp_d >> 16 );
            _code[4] = (byte)( tmp_d >> 24 );
          }
          break;
        case EP_InstCode.OUT:
        case EP_InstCode.IN:
          if(_code == null || _code.Length != 5) {
            _code = new byte[5];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)( _param.Addr >> 24 );     // область
          _code[2] = (byte)( _param.Addr >> 16 );     // тип
          _code[3] = (byte)_param.Addr;             // младший байт базы
          _code[4] = (byte)( _param.Addr >> 8 );      // старший байт базы
          break;

        case EP_InstCode.JZ:
        case EP_InstCode.JNZ:
        case EP_InstCode.JMP:
          tmp_D = _ref == null ? uint.MaxValue : _ref.addr;
          if(_code == null || _code.Length != 3) {
            _code = new byte[3];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)tmp_D;
          _code[2] = (byte)( tmp_D >> 8 );
          break;
        case EP_InstCode.CHECK_IDX:
          if(_code == null || _code.Length != 3) {
            _code = new byte[3];
          }
          _code[0] = (byte)cmd;
          _code[1] = (byte)_param.ElementCount;
          _code[2] = (byte)( _param.ElementCount >> 8 );
          break;
        default:
          throw new NotImplementedException(this.ToString());
        }
      }
      public override string ToString() {
        if(_code.Length > 0) {
          StringBuilder sb = new StringBuilder();
          if(_blob) {
            switch(_param.type) {
            case EP_Type.U8_CARR:
            case EP_Type.I8_CARR:
              sb.Append("DB");
              break;
            case EP_Type.U16_CARR:
            case EP_Type.I16_CARR:
              sb.Append("DW");
              break;
            case EP_Type.I32_CARR:
              sb.Append("DD");
              break;
            }
            while(sb.Length < 12) {
              sb.Append(" ");
            }
            sb.Append(_param.init.ToString());
          } else {
            sb.Append(( (EP_InstCode)_code[0] ).ToString());

            if(_code.Length > 1) {
              while(sb.Length < 12) {
                sb.Append(" ");
              }
              sb.Append("0x");
              for(int i = _code.Length - 1; i > 0; i--) {
                sb.Append(_code[i].ToString("X2"));
              }
            }
          }
          return sb.ToString();
        } else {
          return string.Empty;
        }
      }
    }
  }
}
