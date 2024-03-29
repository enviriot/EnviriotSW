﻿using NiL.JS;
using NiL.JS.Core;
using NiL.JS.Expressions;
using NiL.JS.Statements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.DevicePLC {
  internal class EP_Compiler {
    private static bool Verbose {
      get {
        return true;
      }
    }

    internal Stack<Instruction> _sp;
    internal List<Scope> _programm;
    internal Stack<Scope> _scope;
    public Scope global, cur, initBlock, dataBlock;

    public SortedList<string, string> varList;
    public List<string> ioList;
    public event CompilerMessageCallback CMsg;
    public uint StackBottom { get; private set; }
    public SortedList<uint, byte[]> Hex;

    public EP_Compiler() {
      Hex = new SortedList<uint, byte[]>();
    }

    public bool Parse(string code) {
      bool success = false;
      _scope = new Stack<Scope>();
      _programm = new List<Scope>();
      _sp = new Stack<Instruction>();
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

        var module = new Module(code, CompilerMessageCallback, Options.SuppressConstantPropogation | Options.SuppressUselessExpressionsElimination);

        if(module.Script.Root != null) {
          var p1 = new EP_VP1(this);
          module.Script.Root.Visit(p1);

          _sp.Clear();
          var p2 = new EP_VP2(this);
          module.Script.Root.Visit(p2);
        }

        varList = new SortedList<string, string>();
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
            case EP_Type.UINT8:
              vName = "MB";
              mLen = 8;
              break;
            case EP_Type.UINT16:
              vName = "MW";
              mLen = 16;
              break;
            case EP_Type.REFERENCE:
              vName = null;
              mLen = (uint)m.pOut * 32;
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
            c.Link();  // update size for LDI_*
            c.addr = addr;
            if(c._blob && c._param != null) {
              c._param.Addr = c.addr;
            }
            addr += (uint)c._code.Length;
          }
        }

        List<byte> bytes = new List<byte>();
        foreach(var p in _programm) {
          foreach(var c in p.code) {
            c.Link();
            if(c._code.Length > 0) {
              bytes.AddRange(c._code);
            }
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
          Log.Error("{0}", syntaxError.message);
        } else {
          Log.Error("Compile - {0}: {1}", ex.GetType().Name, ex.Message);
        }
      }
      catch(Exception ex) {
        Log.Error("Compile - {0}: {1}", ex.GetType().Name, ex.Message);
      }
      _scope = null;
      _programm = null;
      _sp = null;

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
          addr = (uint)( (uint)( ( (byte)v.Name[0] ) << 24 ) | (uint)( ( (byte)v.Name[1] ) << 16 ) | (v_addr & 0xFFFF) );
          type = (nt.Item2 & Periphery.MsDevice.DType.Output)!=0?EP_Type.OUTPUT:EP_Type.INPUT;
        } else if(type == EP_Type.NONE) {
          if(v.Initializer != null && v.Initializer is FunctionDefinition) {
            type = EP_Type.FUNCTION;
          } else if(v.LexicalScope) {
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

    private Merker LoadNativeFunctions(VariableDescriptor v) {
      Merker m;
      switch(v.Name) {
      case "TwiControl":
        m = new Merker() { type = EP_Type.API, Addr = 1, vd = v, pIn = 1 };
        break;
      case "TwiStatus":
        m = new Merker() { type = EP_Type.API, Addr = 2, vd = v, pOut = 1 };
        break;
      case "TwiPutByte":
        m = new Merker() { type = EP_Type.API, Addr = 3, vd = v, pIn = 1 };
        break;
      case "TwiGetByte":
        m = new Merker() { type = EP_Type.API, Addr = 4, vd = v, pOut = 1 };
        break;
      case "NodeStatus":
        m = new Merker() { type = EP_Type.API, Addr = 5, vd = v, pOut = 1 };
        break;
      case "getMilliseconds":
        m = new Merker() { type = EP_Type.API, Addr = 6, vd = v, pOut = 1 };
        break;
      case "getSeconds":
        m = new Merker() { type = EP_Type.API, Addr = 7, vd = v, pOut = 1 };
        break;
      case "Random":
        m = new Merker() { type = EP_Type.API, Addr = 8, vd = v, pOut = 1 };
        break;
      case "NowSeconds":    // total seconds since 0:00:00
        m = new Merker() { type = EP_Type.API, Addr = 9, vd = v, pOut = 1 };
        break;
      case "Today":         //  (year[0..99]<<24) | (month[1..12]<<16) | (day[1..31]<<8) | (dayOfWeek[1-Monday..7-Sunday]) 
        m = new Merker() { type = EP_Type.API, Addr = 10, vd = v, pOut = 1 };
        break;
      case "UartInit":  // void UartInit(port, speed)
        m = new Merker() { type = EP_Type.API, Addr = 20, vd = v, pIn = 2 };
        break;
      case "UartBytesToRead":  // int UartBytesToRead(port)
        m = new Merker() { type = EP_Type.API, Addr = 21, vd = v, pOut = 1, pIn = 1 };
        break;
      case "UartGetByte":  // int UartGetByte(port)
        m = new Merker() { type = EP_Type.API, Addr = 22, vd = v, pOut = 1, pIn = 1 };
        break;
      case "UartPutByte":  // bool UartPutByte(port, data)
        m = new Merker() { type = EP_Type.API, Addr = 23, vd = v, pOut = 1, pIn = 2 };
        break;
      default:
        return null;
      }
      global.memory.Add(m);
      return m;
    }
    private void CompilerMessageCallback(MessageLevel level, CodeCoordinates coords, string message) {
      var msg = string.Format("[{0}, {1}] {2}", coords.Line, coords.Column, message);
      if(message == "Type of an expression is ambiguous") {
        return;
      }
      switch(level) {
      case MessageLevel.Error:
      case MessageLevel.CriticalWarning:
        Log.Error("{0}", msg);
        break;
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
      var cm = CMsg;
      if(cm != null) {
        cm(level, coords, message);
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
      public int pIn;
      public int pOut;
      public string pName;
      public string fName;
      public override string ToString() {
        return fName;
      }
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
        // remove unreachable code
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
            // enclosed jump  {jmp l0 ... :l0 jmp l1}
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
            // remove all DROP & NIP before RET
            if(i0._code.Length == 1 && i0._code[0] == (byte)EP_InstCode.RET) {
              for(j = i - 1; j >= 0; j--) {
                i1 = code[j];
                if(i1._code.Length == 1 && ( i1._code[0] == (byte)EP_InstCode.DROP || i1._code[0] == (byte)EP_InstCode.NIP )) {
                  code.RemoveAt(j);
                  i--;
                  cnt++;
                } else if(i1._code.Length != 0) {   // !label
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

      public bool canOptimized;

      public Instruction(EP_InstCode cmd, Merker param = null, CodeNode cn = null) {
        _param = param;
        _cn = cn;
        _blob = false;
        Prepare(cmd);
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
          _code[1] = (byte)( (int)( (Constant)_cn ).Value & 0x1F );
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
          if(_param != null && ( _param.type == EP_Type.REFERENCE || _param.type == EP_Type.FUNCTION )) {
            tmp_d = (int)_param.Addr;
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
          _code[1] = (byte)( _param.Addr >> 24 );     // Place
          _code[2] = (byte)( _param.Addr >> 16 );     // Type
          _code[3] = (byte)_param.Addr;             // Base low
          _code[4] = (byte)( _param.Addr >> 8 );      // Base high
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
          _code[1] = (byte)_param.pOut; //-V3125
          _code[2] = (byte)( _param.pOut >> 8 );
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
