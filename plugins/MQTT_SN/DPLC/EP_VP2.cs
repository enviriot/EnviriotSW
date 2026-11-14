using System;
using NiL.JS.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NiL.JS.Core;
using NiL.JS.Expressions;
using NiL.JS.Statements;


namespace X13.DevicePLC {
  internal class EP_VP2 : Visitor<EP_VP2> {
    private readonly EP_Compiler _compiler;
    private readonly HashSet<EP_Compiler.Merker> _activeLocals = new HashSet<EP_Compiler.Merker>();

    public EP_VP2(EP_Compiler compiler) {
      _compiler = compiler;
    }
    protected override EP_VP2 Visit(CodeNode node) {
      throw new NotSupportedException("Visit(<" + node.GetType().Name + ">" + node.ToString() + ")");
    }
    protected override EP_VP2 Visit(Addition node) {
      AddCommon(node, node.Children[0] as Expression, node.Children[1] as Expression);
      return this;
    }
    protected override EP_VP2 Visit(BitwiseConjunction node) {
      Arg2Op(node, EP_InstCode.AND);
      return this;
    }
    protected override EP_VP2 Visit(ArrayDefinition node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Assignment node) {
      EP_Compiler.Instruction d2 = new EP_Compiler.Instruction(EP_InstCode.DUP) { canOptimized = true };
      _compiler._sp.Push(d2);
      EmitAs(node.Children[1] as Expression, _compiler.TypeOf(node.Children[0] as Expression));
      _compiler.cur.AddInst(d2);
      Store(node, node.Children[0] as Expression);
      return this;
    }
    protected override EP_VP2 Visit(Call node) {
      Property p;
      Variable f;
      EP_Compiler.Merker m;
      EP_Compiler.Instruction d;

      if((p = node.Children[0] as Property) != null) {
        EP_Compiler.Merker mp;
        if((f = p.Source as Variable) != null && (mp = _compiler.GetMerker(f.Descriptor)) != null && mp.pin != null) {
          CallPin(node, p, mp);
          return this;
        }
        string mathFn = _compiler.MathFunction(p);
        if(mathFn != null) {
          CallMath(node, mathFn);
          return this;
        }
        EP_Compiler.Merker mf;
        if((mf = _compiler.FindMethod(p)) != null) {
          CallFunction(node, mf, p.Source as Expression);
          return this;
        }
      }
      f = node.Children[0] as Variable;
      if(f != null && _compiler.GetMerker(f.Descriptor) == null && CallBuiltin(node, f.Name)) {
        return this;
      }
      if(f != null) {
        if(node.CallMode == CallMode.Regular) {
          m = _compiler.GetMerker(f.Descriptor);
          if(m == null || (m.type != EP_Type.FUNCTION && m.type != EP_Type.API)) {
            throw new ArgumentException("Unknown function: " + f.Descriptor.Name);
          }
          if(m.type == EP_Type.API) {
            if(m.Addr == EP_Compiler.API_PRINT_F) {
              CallPrintF(node, m);
              return this;
            }
            if(f.Name == "SendByte" && node.Arguments.Length != m.ParameterTypes.Length)
              throw new NotSupportedException(_compiler.Where(node) + f.Name + ": expected " + m.ParameterTypes.Length + " arguments");
            if(_compiler.Device != null && m.Addr == EP_Compiler.API_CONFIG_PIN) {
              CheckConfigPin(node);
            }
            int i;
            for(i = m.ParameterTypes.Length - 1; i >= 0; i--) {
              if(i < node.Arguments.Length) {
                EmitAs(node.Arguments[i], m.ParameterTypes[i]);
                _compiler._sp.Pop();
              } else {
                _compiler.cur.AddInst(EP_InstCode.LDI_0);
              }
            }
            _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.API, m, node), 0, m.ResultType.HasValue ? 1 : 0);
            return this;
          } else {  // m.type == EP_Type.FUNCTION
            CallFunction(node, m, null);
            return this;
          }
        } else {
          throw new NotSupportedException("Call(" + node.Children[0].ToString() + ") Mode: " + node.CallMode.ToString());
        }
      }

      for(int i = node.Arguments.Length - 1; i >= 0; i--) {
        node.Arguments[i].Visit(this);
      }
      _compiler.cur.AddInst(EP_InstCode.LDI_0, 0, 1);
      node.Children[0].Visit(this);
      _compiler.cur.AddInst(EP_InstCode.SCALL, 1);

      for(int i = node.Arguments.Length - 1; i >= 0; i--) {
        _compiler.cur.AddInst(EP_InstCode.NIP);
        d = _compiler._sp.Pop();
        _compiler._sp.Pop();
        _compiler._sp.Push(d);
      }
      return this;
    }
    private void CallPrintF(Call node, EP_Compiler.Merker api) {
      if(node.Arguments.Length < 2 || !(node.Arguments[1] is Constant literal) || literal.Value == null || literal.Value.ValueType != JSValueType.String)
        throw new NotSupportedException(_compiler.Where(node) + "PrintF requires stream and a constant format string");
      string format = literal.Value.AsString(null);
      EP_ValType[] types;
      try { types = EP_PrintF.Arguments(format); }
      catch(FormatException ex) { throw new NotSupportedException(_compiler.Where(literal) + ex.Message); }
      if(node.Arguments.Length != types.Length + 2)
        throw new NotSupportedException(_compiler.Where(node) + "PrintF argument count does not match the format");
      var data = new EP_Compiler.Merker { type = EP_Type.U8_CARR, ElementCount = format.Length, init = literal };
      _compiler.dataBlock.AddInst(new EP_Compiler.Instruction(format.Select(c => (byte)c).ToArray()) { _param = data, _cn = literal });
      for(int i = types.Length - 1; i >= 0; i--) {
        var arg = node.Arguments[i + 2];
        if(arg is Constant constant && (constant.Value == null || !constant.Value.IsNumber && constant.Value.ValueType != JSValueType.Boolean))
          throw new NotSupportedException(_compiler.Where(arg) + "PrintF value arguments must be numeric");
        Constant number = arg as Constant;
        bool negative = arg is Negation;
        if(negative) number = arg.Children[0] as Constant;
        double value = number != null && number.Value.IsNumber ? (double)number.Value * (negative ? -1 : 1) : double.NaN;
        if(types[i] != EP_ValType.Float && !double.IsNaN(value) && value == Math.Truncate(value)) {
          if(value < int.MinValue || value > uint.MaxValue)
            throw new NotSupportedException(_compiler.Where(arg) + "PrintF integer literal must fit 32 bits");
          LoadConstant(arg, unchecked((int)(long)value));
        } else {
          EmitAs(arg, types[i]);
        }
        _compiler._sp.Pop();
      }
      LoadConstant(node, types.Length); _compiler._sp.Pop();
      LoadConstant(literal, data.ElementCount); _compiler._sp.Pop();
      _compiler.cur.AddInst(EP_Compiler.Instruction.LoadProgramAddress(data, literal), 0, 1); _compiler._sp.Pop();
      EmitAs(node.Arguments[0], api.ParameterTypes[0]); _compiler._sp.Pop();
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.API, api, node), 0, api.ResultType.HasValue ? 1 : 0);
    }

    protected override EP_VP2 Visit(ClassDefinition node) {
      var mc = _compiler.GetMerker(node.Reference.Descriptor);
      if(mc == null || mc.type != EP_Type.CLASS) {
        throw new ApplicationException("Unknown merker in pass 2: " + node.Reference.Descriptor.Name);
      }
      mc.scope = _compiler.ScopePush(mc);
      var ctor = _compiler.GetMerker(node.Constructor.Reference.Descriptor);
      if(ctor == null || ctor.type != EP_Type.FUNCTION) {
        throw new ApplicationException("Unknown merker in pass 2: " + node.Constructor.Reference.Descriptor.Name);
      }
      DefineFunction(node.Constructor, ctor);
      foreach(var fv in node.Members.Where(z => z.Value is FunctionDefinition && z.Name is Constant)) {
        var fm = mc.scope.GetProperty((fv.Name as Constant).Value.ToString(), EP_Type.FUNCTION);
        if(fm == null || fm.type != EP_Type.FUNCTION) {
          throw new ApplicationException("Unknown merker in pass 2: " + mc.fName + "." + (fv.Name as Constant).Value.ToString());
        }
        DefineFunction(fv.Value as FunctionDefinition, fm);
      }
      _compiler.ScopePop();
      return this;
    }
    protected override EP_VP2 Visit(Constant node) {
      if(EP_Compiler.IsFloatConst(node)) {
        _compiler.cur.AddInst(EP_Compiler.Instruction.LoadFloat(node.Value.As<double>(), node), 0, 1);
        return this;
      }
      int v = node.Value == null ? 0 : (int)node.Value;
      LoadConstant(node, v);
      return this;
    }
    protected override EP_VP2 Visit(Decrement node) {
      Expression a;
      EP_Compiler.Instruction d2;
      bool isFloat = _compiler.TypeOf(node.Children[0] as Expression) == EP_ValType.Float;
      node.Children[0].Visit(this);
      _compiler._sp.Pop();
      if(node.Type == DecrimentType.Predecriment) {
        d2 = new EP_Compiler.Instruction(EP_InstCode.DUP) { canOptimized = true };
        _compiler._sp.Push(d2);
        StepOne(EP_InstCode.DEC, isFloat);
        _compiler.cur.AddInst(d2);
      } else {
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.DUP) { canOptimized = true }, 0, 1);
        StepOne(EP_InstCode.DEC, isFloat);
      }

      if((a = node.Children[0] as Expression) != null) {
        Store(node, a);
      } else {
        throw new NotImplementedException();
      }
      return this;
    }
    protected override EP_VP2 Visit(Delete node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(DeleteProperty node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Division node) {
      Arg2Op(node, EP_InstCode.DIV, EP_InstCode.FDIV);   // int / int остаётся целочисленным
      return this;
    }
    protected override EP_VP2 Visit(Equal node) {
      Arg2Op(node, EP_InstCode.CEQ, EP_InstCode.FCEQ);
      return this;
    }
    protected override EP_VP2 Visit(Expression node) {
      var v = node as AssignmentOperatorCache;
      if(v != null) {
        return v.Source.Visit(this);
      }
      var t = node as This;
      if(t != null) {
        _compiler.cur.AddInst(EP_InstCode.LD_P0, 0, 1);
        return this;
      }
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(FunctionDefinition node) {
      var fm = _compiler.GetMerker(node.Reference.Descriptor);
      if(fm == null || fm.type != EP_Type.FUNCTION) {
        throw new ApplicationException("Unknown merker in pass 2: " + node.Reference.Descriptor.Name);
      }

      DefineFunction(node, fm);
      return this;
    }
    protected override EP_VP2 Visit(Property node) {
      Variable f;
      EP_Compiler.Merker m;

      if((f = node.Source as Variable) != null && (m = _compiler.GetMerker(f.Descriptor)) != null) {
        switch(m.type) {
        case EP_Type.REFERENCE:  //TODO: LDM_xx_C16  (m.addr+offset)
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LDI_S4, m), 0, 1);
          break;
        case EP_Type.U8_CARR:
          EmitAs(node.FieldName as Expression, EP_ValType.Int);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.CHECK_IDX, m), 0, 0);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LPM_U1, m, node), 1, 1);
          return this;
        case EP_Type.I8_CARR:
          EmitAs(node.FieldName as Expression, EP_ValType.Int);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.CHECK_IDX, m), 0, 0);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LPM_S1, m, node), 1, 1);
          return this;
        case EP_Type.U16_CARR:
          EmitAs(node.FieldName as Expression, EP_ValType.Int);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.CHECK_IDX, m), 0, 0);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LPM_U2, m, node), 1, 1);
          return this;
        case EP_Type.I16_CARR:
          EmitAs(node.FieldName as Expression, EP_ValType.Int);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.CHECK_IDX, m), 0, 0);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LPM_S2, m, node), 1, 1);
          return this;
        case EP_Type.I32_CARR:
          EmitAs(node.FieldName as Expression, EP_ValType.Int);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.CHECK_IDX, m), 0, 0);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LPM_S4, m, node), 1, 1);
          return this;
        default:
          throw new NotSupportedException("P2: " + node.ToString());
        }
      } else if(node.Source is This) {
        _compiler.cur.AddInst(EP_InstCode.LD_P0, 0, 1);
      } else {
        throw new NotSupportedException("P2: " + node.ToString());
      }
      m = _compiler.FindProperty(_compiler.PropertyScope(node.Source as Expression), node.FieldName as Expression);
      if(m != null) {
        EP_InstCode cmd;
        switch(m.type) {
        case EP_Type.PropB1:
          cmd = m.Addr < 256 ? EP_InstCode.LDM_B1_CS8 : EP_InstCode.LDM_B1_CS16;
          break;
        case EP_Type.PropS1:
          cmd = m.Addr < 256 ? EP_InstCode.LDM_S1_CS8 : EP_InstCode.LDM_S1_CS16;
          break;
        case EP_Type.PropU1:
          cmd = m.Addr < 256 ? EP_InstCode.LDM_U1_CS8 : EP_InstCode.LDM_U1_CS16;
          break;
        case EP_Type.PropS2:
          cmd = m.Addr < 256 ? EP_InstCode.LDM_S2_CS8 : EP_InstCode.LDM_S2_CS16;
          break;
        case EP_Type.PropU2:
          cmd = m.Addr < 256 ? EP_InstCode.LDM_U2_CS8 : EP_InstCode.LDM_U2_CS16;
          break;
        case EP_Type.PropS4:
        case EP_Type.PropF4:    // float - те же 4 байта
          cmd = m.Addr < 256 ? EP_InstCode.LDM_S4_CS8 : EP_InstCode.LDM_S4_CS16;
          break;
        default:
          throw new ApplicationException("Merker " + m.ToString() + " is not property");
        }
        _compiler.cur.AddInst(new EP_Compiler.Instruction(cmd, m), 1, 1);
        return this;
      }
      throw new NotSupportedException("Field name in " + node.ToString());
    }
    protected override EP_VP2 Visit(Variable node) {
      EP_Compiler.Merker m = _compiler.GetMerker(node.Descriptor);
      if(m == null) {
        throw new ArgumentException(_compiler.Where(node) + "undefined variable " + node.Name);
      }
      EP_Compiler.Instruction d;
      switch(m.type) {
      case EP_Type.BOOL:
        d = new EP_Compiler.Instruction(EP_InstCode.LDM_B1_C16, m, node);
        break;
      case EP_Type.UINT8:
        d = new EP_Compiler.Instruction(EP_InstCode.LDM_U1_C16, m, node);
        break;
      case EP_Type.SINT8:
        d = new EP_Compiler.Instruction(EP_InstCode.LDM_S1_C16, m, node);
        break;
      case EP_Type.UINT16:
        d = new EP_Compiler.Instruction(EP_InstCode.LDM_U2_C16, m, node);
        break;
      case EP_Type.SINT16:
        d = new EP_Compiler.Instruction(EP_InstCode.LDM_S2_C16, m, node);
        break;
      case EP_Type.SINT32:
      case EP_Type.FLOAT:     // float - те же 4 байта
        d = new EP_Compiler.Instruction(EP_InstCode.LDM_S4_C16, m, node);
        break;
      case EP_Type.PARAMETER:
        d = new EP_Compiler.Instruction((EP_InstCode)(EP_InstCode.LD_P0 + (byte)m.Addr));
        break;
      case EP_Type.INPUT:
      case EP_Type.OUTPUT:
        d = new EP_Compiler.Instruction(EP_InstCode.IN, m, node);
        break;
      case EP_Type.LOCAL:
        d = new EP_Compiler.Instruction((EP_InstCode)(EP_InstCode.LD_L0 + (byte)m.Addr));
        break;
      case EP_Type.FUNCTION:
        d = new EP_Compiler.Instruction(EP_InstCode.LDI_U2, m, node);
        break;
      case EP_Type.CONST:
        d = EP_Compiler.Instruction.LoadInt((int)m.Addr, node);
        break;
      default:
        throw new NotImplementedException(node.ToString());
      }
      _compiler.cur.AddInst(d, 0, 1);
      return this;
    }
    protected override EP_VP2 Visit(VariableReference node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(In node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Increment node) {
      Expression a;
      EP_Compiler.Instruction d2;
      bool isFloat = _compiler.TypeOf(node.Children[0] as Expression) == EP_ValType.Float;
      node.Children[0].Visit(this);
      _compiler._sp.Pop();
      if(node.Type == IncrimentType.Preincriment) {
        d2 = new EP_Compiler.Instruction(EP_InstCode.DUP) { canOptimized = true };
        _compiler._sp.Push(d2);
        StepOne(EP_InstCode.INC, isFloat);
        _compiler.cur.AddInst(d2);
      } else {
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.DUP) { canOptimized = true }, 0, 1);
        StepOne(EP_InstCode.INC, isFloat);
      }

      if((a = node.Children[0] as Expression) != null) {
        Store(node, a);
      } else {
        throw new NotImplementedException();
      }
      return this;
    }
    protected override EP_VP2 Visit(InstanceOf node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(NiL.JS.Expressions.ObjectDefinition node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Less node) {
      Arg2Op(node, EP_InstCode.CLT, EP_InstCode.FCLT);
      return this;
    }
    protected override EP_VP2 Visit(LessOrEqual node) {
      Arg2Op(node, EP_InstCode.CLE, EP_InstCode.FCLE);
      return this;
    }
    protected override EP_VP2 Visit(LogicalConjunction node) {
      EP_Compiler.Instruction j1, j2;
      EmitCond(node.Children[0]);
      _compiler.cur.AddInst(EP_InstCode.DUP, 0, 1);
      _compiler.cur.AddInst(j1 = new EP_Compiler.Instruction(EP_InstCode.JZ), 1);
      _compiler.cur.AddInst(EP_InstCode.DROP, 1, 0);
      EmitCond(node.Children[1]);
      _compiler.cur.AddInst(j2 = new EP_Compiler.Instruction(EP_InstCode.LABEL));
      j1._ref = j2;
      return this;
    }
    protected override EP_VP2 Visit(LogicalNegation node) {
      node.Children[0].Visit(this);
      bool isFloat = _compiler.TypeOf(node.Children[0] as Expression) == EP_ValType.Float;
      _compiler.cur.AddInst(isFloat ? EP_InstCode.FCZE : EP_InstCode.CZE, 1, 1);
      return this;

    }
    protected override EP_VP2 Visit(LogicalDisjunction node) {
      EP_Compiler.Instruction j1, j2;
      EmitCond(node.Children[0]);
      _compiler.cur.AddInst(EP_InstCode.DUP, 0, 1);
      _compiler.cur.AddInst(j1 = new EP_Compiler.Instruction(EP_InstCode.JNZ), 1, 0);
      _compiler.cur.AddInst(EP_InstCode.DROP, 1, 0);
      EmitCond(node.Children[1]);
      _compiler.cur.AddInst(j2 = new EP_Compiler.Instruction(EP_InstCode.LABEL));
      j1._ref = j2;
      return this;
    }
    protected override EP_VP2 Visit(Modulo node) {
      // FMOD в VM нет, а тихое усечение до int дало бы не тот остаток
      if(_compiler.TypeOf(node.Children[0] as Expression) == EP_ValType.Float || _compiler.TypeOf(node.Children[1] as Expression) == EP_ValType.Float) {
        throw new NotSupportedException(_compiler.Where(node) + "operator % is not supported for float: " + node.ToString());
      }
      Arg2Op(node, EP_InstCode.MOD);
      return this;
    }
    protected override EP_VP2 Visit(More node) {
      Arg2Op(node, EP_InstCode.CGT, EP_InstCode.FCGT);
      return this;
    }
    protected override EP_VP2 Visit(MoreOrEqual node) {
      Arg2Op(node, EP_InstCode.CGE, EP_InstCode.FCGE);
      return this;
    }
    protected override EP_VP2 Visit(Multiplication node) {
      Arg2Op(node, EP_InstCode.MUL, EP_InstCode.FMUL);
      return this;
    }
    protected override EP_VP2 Visit(Negation node) {
      node.Children[0].Visit(this);
      bool isFloat = _compiler.TypeOf(node.Children[0] as Expression) == EP_ValType.Float;
      _compiler.cur.AddInst(isFloat ? EP_InstCode.FNEG : EP_InstCode.NEG, 1, 1);
      return this;
    }
    protected override EP_VP2 Visit(New node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Comma node) {
      var children = node.Children.ToArray();
      for(int i = 0; i < children.Length - 1; i++) SafeCodeBlock(children[i]);
      children[children.Length - 1].Visit(this);
      return this;
    }
    protected override EP_VP2 Visit(BitwiseNegation node) {
      EmitAs(node.Children[0] as Expression, EP_ValType.Int);
      _compiler.cur.AddInst(EP_InstCode.NOT, 1, 1);
      return this;
    }
    protected override EP_VP2 Visit(NotEqual node) {
      Arg2Op(node, EP_InstCode.CNE, EP_InstCode.FCNE);
      return this;
    }
    protected override EP_VP2 Visit(NumberAddition node) {
      AddCommon(node, node.Children[0] as Expression, node.Children[1] as Expression);
      return this;
    }
    protected override EP_VP2 Visit(NumberLess node) {
      Arg2Op(node, EP_InstCode.CLT, EP_InstCode.FCLT);
      return this;
    }
    protected override EP_VP2 Visit(NumberLessOrEqual node) {
      Arg2Op(node, EP_InstCode.CLE, EP_InstCode.FCLE);
      return this;
    }
    protected override EP_VP2 Visit(NumberMore node) {
      Arg2Op(node, EP_InstCode.CGT, EP_InstCode.FCGT);
      return this;
    }
    protected override EP_VP2 Visit(NumberMoreOrEqual node) {
      Arg2Op(node, EP_InstCode.CGE, EP_InstCode.FCGE);
      return this;
    }
    protected override EP_VP2 Visit(BitwiseDisjunction node) {
      Arg2Op(node, EP_InstCode.OR);
      return this;
    }
    protected override EP_VP2 Visit(RegExpExpression node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(SetProperty node) {
      FunctionDefinition fd;
      Variable f;
      EP_Compiler.Merker m;
      Call ca;

      if((fd = node.Value as FunctionDefinition) == null) {
        EP_Compiler.Instruction d2 = new EP_Compiler.Instruction(EP_InstCode.DUP) { canOptimized = true };
        _compiler._sp.Push(d2);
        var pt = EP_Compiler.IsFloat(_compiler.FindProperty(_compiler.PropertyScope(node.Source as Expression), node.FieldName as Expression)) ? EP_ValType.Float : EP_ValType.Int;
        if((ca = node.Value as Call) != null && (f = ca.Children[0] as Variable) != null && TypeNames.Any(z => z == f.Name)) {
          if(ca.Arguments.Length > 0) {
            EmitAs(ca.Arguments[0], pt);
          } else {
            _compiler._sp.Pop();
            return this;
          }
        } else {
          EmitAs(node.Value, pt);
        }
        _compiler.cur.AddInst(d2);

        StoreProperty(node, node.Source, node.FieldName);
      } else {
        Constant c;
        if(node.Source is This && (c = node.FieldName as Constant) != null && c.Value != null && c.Value.ValueType == JSValueType.String) {
          m = _compiler.cur.GetProperty(c.Value.ToString());
          DefineFunction(fd, m);
        }
      }
      return this;
    }
    protected override EP_VP2 Visit(SignedShiftLeft node) {
      EmitShift(node, EP_InstCode.LSL, EP_ValType.Int);
      return this;
    }
    protected override EP_VP2 Visit(SignedShiftRight node) {
      EmitShift(node, EP_InstCode.ASR, EP_ValType.Int);
      return this;
    }
    protected override EP_VP2 Visit(StrictEqual node) {
      Arg2Op(node, EP_InstCode.CEQ, EP_InstCode.FCEQ);
      return this;
    }
    protected override EP_VP2 Visit(StrictNotEqual node) {
      Arg2Op(node, EP_InstCode.CNE, EP_InstCode.FCNE);
      return this;
    }
    protected override EP_VP2 Visit(StringConcatenation node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Substract node) {
      Arg2Op(node, EP_InstCode.SUB, EP_InstCode.FSUB);
      return this;
    }
    protected override EP_VP2 Visit(Conditional node) {
      EP_Compiler.Instruction j1, j2, j3;
      var rt = _compiler.TypeOf(node);   // обе ветви приводятся к общему типу
      EmitCond(node.Children[0]);
      j1 = new EP_Compiler.Instruction(EP_InstCode.JZ, null, node.Children[0]);
      _compiler.cur.AddInst(j1);
      _compiler._sp.Pop();
      EmitAs(node.Branches[0], rt);
      if(node.Branches.Count > 1) {
        j2 = new EP_Compiler.Instruction(EP_InstCode.JMP);
        _compiler.cur.AddInst(j2);
        j3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        j1._ref = j3;
        _compiler.cur.AddInst(j3);
        EmitAs(node.Branches[1], rt);
        _compiler._sp.Pop();
        j3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        j2._ref = j3;
        _compiler.cur.AddInst(j3);
      } else {
        j3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        j1._ref = j3;
        _compiler.cur.AddInst(j3);
      }
      return this;
    }
    protected override EP_VP2 Visit(ConvertToBoolean node) {
      return this;
    }
    /// <summary>x | 0, x >> 0, x << 0 - NiL сворачивает их в этот узел.</summary>
    /// <remarks>Раньше операнд не вычислялся вовсе: b = a | 0 записывал в b то, что лежало на стеке.
    /// Целое уже 32-битное, так что для него это пустая операция, для float - FTOS32.</remarks>
    protected override EP_VP2 Visit(ConvertToInteger node) {
      EmitAs(node.Children[0] as Expression, EP_ValType.Int);
      return this;
    }
    protected override EP_VP2 Visit(ConvertToNumber node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(ConvertToString node) {
      return Visit(node as Expression);
    }
    /// <summary>x >>> 0: биты целого те же, меняется только тип (UInt), float - FTOU32.</summary>
    protected override EP_VP2 Visit(ConvertToUnsignedInteger node) {
      EmitAs(node.Children[0] as Expression, EP_ValType.UInt);
      return this;
    }
    protected override EP_VP2 Visit(TypeOf node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(UnsignedShiftRight node) {
      EmitShift(node, EP_InstCode.LSR, EP_ValType.UInt);
      return this;
    }
    protected override EP_VP2 Visit(BitwiseExclusiveDisjunction node) {
      Arg2Op(node, EP_InstCode.XOR);
      return this;
    }
    protected override EP_VP2 Visit(Yield node) {
      return Visit(node as Expression);
    }
    protected override EP_VP2 Visit(Break node) {
      Loop cl;
      if(node.Label != null) {
        var l = node.Label.ToString();
        cl = _compiler.cur.loops.FirstOrDefault(z => z.labels.Any(y => y == l));
        if(cl == null) {
          cl = _compiler.cur.loops.Peek();
        }
      } else {
        cl = _compiler.cur.loops.Peek();
      }
      int tmp = _compiler._sp.Count;
      while(tmp > cl.sp1) {
        tmp--;
        _compiler.cur.AddInst(EP_InstCode.DROP);
      }
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = cl.L3 });
      return this;
    }
    protected override EP_VP2 Visit(CodeBlock node) {
      EP_Compiler.Merker m;
      int sp2 = _compiler._sp.Count;
      var locals = new List<EP_Compiler.Merker>();
      if(node.Variables!=null) {
        foreach(var v in node.Variables) {
          if(v.Initializer is ClassDefinition) {
            continue;
          }
          m = _compiler.GetMerker(v);
          if(m == null) {
            throw new ApplicationException("Unknown Merker in Pass2: " + v.Name);
          }
          if(m.type == EP_Type.LOCAL) {
            // Резервируем ячейку, но RHS вычисляем в точке объявления, после предыдущих операторов.
            // VP1 нумерует по обходу дерева, а не по реальному размещению вложенных блоков в стеке.
            m.Addr = (uint)_compiler.cur.memory.Count(z => _activeLocals.Contains(z));
            _activeLocals.Add(m);
            locals.Add(m);
            _compiler.cur.AddInst(EP_InstCode.LDI_0, 0, 1);
          } else if(m.vd.Initializer != null) {
            m.vd.Initializer.Visit(this);
          }
        }
      }

      int sp = _compiler._sp.Count;
      for(var i = 0; i < node.Body.Length; i++) {
        SafeCodeBlock(node.Body[i], sp);
      }
      while(_compiler._sp.Count > sp2) {
        _compiler.cur.AddInst(EP_InstCode.DROP, 1, 0);
      }
      foreach(var local in locals) {
        _activeLocals.Remove(local);
        _compiler.cur.memory.Remove(local);
      }

      return this;
    }
    protected override EP_VP2 Visit(Continue node) {
      Loop cl;
      if(node.Label != null) {
        var l = node.Label.ToString();
        cl = _compiler.cur.loops.FirstOrDefault(z => z.labels.Any(y => y == l));
        if(cl == null) {
          cl = _compiler.cur.loops.Peek();
        }
      } else {
        cl = _compiler.cur.loops.Peek();
      }
      int tmp = _compiler._sp.Count;
      while(tmp > cl.sp2) {
        tmp--;
        _compiler.cur.AddInst(EP_InstCode.DROP);
      }
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = cl.L2 });
      return this;
    }
    protected override EP_VP2 Visit(Debugger node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(DoWhile node) {
      var cl = new Loop(_compiler._sp.Count, node.Labels);
      _compiler.cur.loops.Push(cl);

      _compiler.cur.AddInst(cl.L1);

      cl.sp2 = _compiler._sp.Count;
      SafeCodeBlock(node.Body);

      _compiler.cur.AddInst(cl.L2);
      EmitCond(node.Condition);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JNZ, null, node.Condition) { _ref = cl.L1 });
      _compiler.cur.AddInst(cl.L3);
      _compiler._sp.Pop();
      _compiler.cur.loops.Pop();

      return this;
    }
    protected override EP_VP2 Visit(Empty node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(ForIn node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(ForOf node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(For node) {
      var cl = new Loop(_compiler._sp.Count, node.Labels.ToArray());
      _compiler.cur.loops.Push(cl);

      if(node.Initializer != null) {
        node.Initializer.Visit(this);
      }
      _compiler.cur.AddInst(cl.L1);
      EmitCond(node.Condition);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JZ, null, node.Condition) { _ref = cl.L3 });
      _compiler._sp.Pop();
      cl.sp2 = _compiler._sp.Count;
      SafeCodeBlock(node.Body);

      _compiler.cur.AddInst(cl.L2);
      if(node.Post != null) {
        node.Post.Visit(this);
      }
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = cl.L1 });
      _compiler.cur.AddInst(cl.L3);

      _compiler.cur.loops.Pop();
      return this;
    }
    protected override EP_VP2 Visit(IfElse node) {
      EP_Compiler.Instruction j1, j2, j3;
      EmitCond(node.Condition);

      j1 = new EP_Compiler.Instruction(EP_InstCode.JZ, null, node.Condition);
      _compiler.cur.AddInst(j1, 1);
      SafeCodeBlock(node.Then);
      if(node.Else != null) {
        j2 = new EP_Compiler.Instruction(EP_InstCode.JMP);
        _compiler.cur.AddInst(j2);
        j3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        j1._ref = j3;
        _compiler.cur.AddInst(j3);
        SafeCodeBlock(node.Else);
        j3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        j2._ref = j3;
        _compiler.cur.AddInst(j3);
      } else {
        j3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        j1._ref = j3;
        _compiler.cur.AddInst(j3);
      }
      return this;
    }
    protected override EP_VP2 Visit(InfinityLoop node) {
      var cl = new Loop(_compiler._sp.Count, node.Labels);
      _compiler.cur.loops.Push(cl);

      _compiler.cur.AddInst(cl.L1);
      _compiler.cur.AddInst(cl.L2);

      cl.sp2 = _compiler._sp.Count;
      SafeCodeBlock(node.Body);

      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = cl.L1 });
      _compiler.cur.AddInst(cl.L3);
      _compiler.cur.loops.Pop();
      return this;
    }
    protected override EP_VP2 Visit(LabeledStatement node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(Return node) {
      if(node.Value != null) {
        // тип возврата выведен EP_Compiler.InferTypes: в float-функции и return 0 - float
        var fm = _compiler.cur.fm;
        EmitAs(node.Value, fm != null && fm.isFloat ? EP_ValType.Float : EP_ValType.Int);
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.ST_P0, null, node), 1);
      }
      _compiler.cur.AddInst(EP_InstCode.RET);
      return this;
    }
    protected override EP_VP2 Visit(Switch node) {
      int i, j;
      var labels = new EP_Compiler.Instruction[node.Cases.Length];
      var cvs = node.Cases.Where(z => z.Statement != null).OrderBy(z => z.Index).Union(node.Cases.Where(z => z.Statement == null)).ToArray();
      node.Image.Visit(this);
      for(j = 0; j < cvs.Length; j++) {
        labels[j] = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        if(cvs[j].Statement != null) {
          if(j < cvs.Length - 2) {
            _compiler.cur.AddInst(EP_InstCode.DUP);
          }
          cvs[j].Statement.Visit(this);
          _compiler.cur.AddInst(EP_InstCode.CEQ);
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JNZ, null, cvs[j].Statement) { _ref = labels[j] }, 1);
        } else {
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = labels[j] });
        }
      }
      _compiler._sp.Pop();

      var cl = new Loop(_compiler._sp.Count, null);
      _compiler.cur.loops.Push(cl);

      j = 0;
      for(i = 0; i < node.Body.Length; i++) {
        if(j < cvs.Length && cvs[j].Index == i) {
          _compiler.cur.AddInst(labels[j]);
          j++;
        }
        SafeCodeBlock(node.Body[i]);
      }
      while(j < labels.Length) {
        _compiler.cur.AddInst(labels[j++]);
      }
      _compiler.cur.AddInst(cl.L3);
      _compiler.cur.loops.Pop();
      return this;
    }
    protected override EP_VP2 Visit(Throw node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(TryCatch node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP2 Visit(VariableDefinition node) {
      int i;
      Assignment a1;
      EP_Compiler.Scope tmp;
      EP_Compiler.Merker m, mf;
      List<EP_Compiler.Merker> tmp2;
      Variable v;
      for(i = 0; i < node.Initializers.Length; i++) {
        if(node.Initializers[i] is Variable) {
          continue;
        }
        if((a1 = node.Initializers[i] as Assignment) != null && (v = a1.Children[0] as Variable) != null) {
          Call ca;
          Variable f;
          m = _compiler.GetMerker(v.Descriptor);
          if(m == null) {
            throw new ApplicationException("Unknown merker in pass 2: " + v.Descriptor.Name);
          }
          if(m.initialized) {
            continue;
          }
          if(m.type == EP_Type.LOCAL) {
            SafeCodeBlock(node.Initializers[i]);
            continue;
          }
          if((ca = a1.Children[1] as Call) != null && ca.CallMode == CallMode.Construct && (f = ca.Children[0] as Variable) != null) {
            if(m.pin != null) {   // конструктор блока Pin: настройка пина в init
              tmp = _compiler.cur;
              _compiler.cur = _compiler.initBlock;
              LoadConstant(ca, m.pin.Option);
              LoadConstant(ca, m.pin.Mode.Value);
              LoadConstant(ca, m.pin.Gpio);
              var configure = EP_Compiler.Merker.Api(EP_Compiler.API_CONFIG_PIN, null, EP_ValType.Int, EP_ValType.Int, EP_ValType.Int);
              _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.API, configure, ca), configure.ParameterTypes.Length, configure.ResultType.HasValue ? 1 : 0);
              _compiler.cur = tmp;
              continue;
            }
            if(TypeNames.Any(z => z == f.Name)) {
              mf = null;
            } else if((new string[] { "Uint8Array", "Uint16Array", "Int8Array", "Int16Array", "Int32Array"}).Any(z => z == f.Name)) {
              continue;
            } else {
              mf = _compiler.GetMerker(f.Descriptor);
              if(mf == null || (mf.type != EP_Type.FUNCTION && mf.type != EP_Type.CLASS)) {
                throw new ApplicationException("Unknown merker in pass 2: " + f.Descriptor.Name);
              }
              m.scope = mf.scope;
              if(mf.type == EP_Type.CLASS) {
                var mc = mf.scope.GetProperty("constructor");
                if(mc == null || mc.type != EP_Type.FUNCTION) {
                  throw new ApplicationException("Unknown merker in pass 2: " + f.Descriptor.Name + ".constructor");
                }
                mf = mc;
              }
              m.InstanceWords = (int)(m.scope.memBlocks.Last().start + 31) / 32;
            }

            tmp = _compiler.cur;
            _compiler.cur = _compiler.initBlock;
            tmp2 = _compiler.cur.memory;
            _compiler.cur.memory = tmp.memory;
            if(mf != null) {
              CallFunction(ca, mf, v);    // Call in INIT section
              _compiler.cur.AddInst(EP_InstCode.DROP, 1, 0);
            } else {
              if(ca.Arguments.Length > 0) {
                EmitAs(ca.Arguments[0], _compiler.TypeOf(v));
                Store(a1, v);
              }
            }
            _compiler.cur.memory = tmp2;
            _compiler.cur = tmp;

            continue;
          } else {
            if(m.type != EP_Type.LOCAL && m.type != EP_Type.NONE) {
              tmp = _compiler.cur;
              _compiler.cur = _compiler.initBlock;
              tmp2 = _compiler.cur.memory;
              _compiler.cur.memory = tmp.memory;
              SafeCodeBlock(node.Initializers[i]);
              _compiler.cur.memory = tmp2;
              _compiler.cur = tmp;
              continue;
            }
          }
        }
        SafeCodeBlock(node.Initializers[i]);
      }
      return this;
    }
    protected override EP_VP2 Visit(While node) {
      var cl = new Loop(_compiler._sp.Count, node.Labels);
      _compiler.cur.loops.Push(cl);

      _compiler.cur.AddInst(cl.L1);
      _compiler.cur.AddInst(cl.L2);
      EmitCond(node.Condition);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JZ, null, node.Condition) { _ref = cl.L3 });
      _compiler._sp.Pop();

      cl.sp2 = _compiler._sp.Count;
      SafeCodeBlock(node.Body, cl.sp2);

      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = cl.L1 });
      _compiler.cur.AddInst(cl.L3);
      _compiler.cur.loops.Pop();
      return this;
    }
    protected override EP_VP2 Visit(With node) {
      return Visit(node as CodeNode);
    }

    /// <summary>Метод экземпляра Pin: Tick() читает пин, Tick(x) пишет; оба обновляют выход value.</summary>
    /// <remarks>Номер пина зашит в команды IN/OUT, поэтому код подставляется на месте вызова, а не вызывается.
    /// Tick() оставляет значение на стеке, Tick(x) - ничего.</remarks>
    private void CallPin(Call node, Property p, EP_Compiler.Merker m) {
      var c = p.FieldName as Constant;
      if(c == null || c.Value == null || c.Value.ToString() != "Tick" || node.Arguments.Length > 1) {
        throw new NotSupportedException(_compiler.Where(node) + node.ToString() + ": Pin has only Tick() and Tick(value)");
      }
      if(node.Arguments.Length > 0 && m.pin.Mode.Io == EP_Device.PinIo.In) {
        throw new NotSupportedException(_compiler.Where(node) + node.ToString() + ": " + m.pin.Mode.Name + " is an input");
      }
      var io = new EP_Compiler.Merker() { type = EP_Type.OUTPUT, Addr = (uint)m.pin.Gpio };   // операнд IN/OUT: place и type - 0
      if(node.Arguments.Length == 0) {
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.IN, io, node), 0, 1);
      } else {
        EmitAs(node.Arguments[0], EP_ValType.Int);
      }
      _compiler.cur.AddInst(EP_InstCode.DUP, 0, 1);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LDI_S4, m), 0, 1);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.STM_S4_CS8, m.scope.GetProperty("value"), node), 2, 0);
      if(node.Arguments.Length > 0) {
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.OUT, io, node), 1, 0);
      }
    }

    /// <summary>ConfigPin с константными пином и режимом - по описанию узла.</summary>
    /// <remarks>Параметр не проверяется: это низкоуровневый вызов с правилами прошивки, где 0 - «по
    /// умолчанию», и ConfigPin(4, PWM, 0) законен при min частоты 1. По описанию целиком живёт только Pin.</remarks>
    private void CheckConfigPin(Call node) {
      if(node.Arguments.Length < 2) {
        return;
      }
      int? pin = _compiler.ConstValue(node.Arguments[0]);
      int? mv = _compiler.ConstValue(node.Arguments[1]);
      if(pin == null || mv == null) {
        return;
      }
      var mode = _compiler.Device.ModeOf(mv.Value);
      if(mode == null) {
        throw new NotSupportedException(_compiler.Where(node) + node.ToString() + ": mode " + mv.Value + " is not in the device description");
      }
      if(!_compiler.Device.Allows(pin.Value, mode)) {
        throw new NotSupportedException(_compiler.Where(node) + node.ToString() + ": " + mode.Name + " is not available on pin " + pin.Value);
      }
    }

    /// <remarks>Код команды по v прежде выбирался здесь, а значение Prepare брал из node - и для свёрнутой
    /// суммы двух констант, где node - сложение, загрузил бы 0. Теперь значение несёт сама команда.</remarks>
    private void LoadConstant(CodeNode node, int v) {
      _compiler.cur.AddInst(EP_Compiler.Instruction.LoadInt(v, node), 0, 1);
    }
    private void Store(CodeNode node, Expression e) {
      Variable a;
      AssignmentOperatorCache a2;
      Property p;
      if((a = e as Variable) != null || ((a2 = e as AssignmentOperatorCache) != null && (a = a2.Source as Variable) != null)) {
        var m = _compiler.GetMerker(a.Descriptor);
        if(m == null) {
          throw new ApplicationException(_compiler.Where(node) + "Unknown variable: " + a.Descriptor.Name);
        }
        switch(m.type) {
        case EP_Type.BOOL:
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.STM_B1_C16, m, node), 1, 0);
          break;
        case EP_Type.UINT8:
        case EP_Type.SINT8:
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.STM_S1_C16, m, node), 1, 0);
          break;
        case EP_Type.UINT16:
        case EP_Type.SINT16:
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.STM_S2_C16, m, node), 1, 0);
          break;
        case EP_Type.SINT32:
        case EP_Type.FLOAT:
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.STM_S4_C16, m, node), 1, 0);
          break;
        case EP_Type.PARAMETER:
          _compiler.cur.AddInst(new EP_Compiler.Instruction((EP_InstCode)(EP_InstCode.ST_P0 + (byte)m.Addr), null, node), 1, 0);
          break;
        case EP_Type.LOCAL:
          _compiler.cur.AddInst(new EP_Compiler.Instruction((EP_InstCode)(EP_InstCode.ST_L0 + (byte)m.Addr), null, node), 1, 0);
          break;
        case EP_Type.OUTPUT:
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.OUT, m, node), 1, 0);
          break;
        default:
          throw new NotSupportedException(node.ToString());
        }
      } else if((p = e as Property) != null || ((a2 = e as AssignmentOperatorCache) != null && (p = a2.Source as Property) != null)) {
        StoreProperty(node, p.Source as Expression, p.FieldName as Expression);
      } else {
        throw new NotImplementedException(node.ToString());
      }
    }
    private void StoreProperty(CodeNode node, Expression src, Expression name) {
      Variable f;
      EP_Compiler.Merker m;

      if((f = src as Variable) != null && (m = _compiler.GetMerker(f.Descriptor)) != null && m.type == EP_Type.REFERENCE) {
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LDI_S4, m), 0, 1);
        //TODO: STM_xx_C16  (m.addr*size+offset)
      } else if(src is This) {
        _compiler.cur.AddInst(EP_InstCode.LD_P0, 0, 1);
      } else {
        throw new NotSupportedException(src.ToString() + " as object");
      }
      m = _compiler.FindProperty(_compiler.PropertyScope(src), name);
      if(m != null) {
        EP_InstCode cmd;
        switch(m.type) {
        case EP_Type.PropB1:
          cmd = m.Addr < 256 ? EP_InstCode.STM_B1_CS8 : EP_InstCode.STM_B1_CS16;
          break;
        case EP_Type.PropS1:
        case EP_Type.PropU1:
          cmd = m.Addr < 256 ? EP_InstCode.STM_S1_CS8 : EP_InstCode.STM_S1_CS16;
          break;
        case EP_Type.PropS2:
        case EP_Type.PropU2:
          cmd = m.Addr < 256 ? EP_InstCode.STM_S2_CS8 : EP_InstCode.STM_S2_CS16;
          break;
        case EP_Type.PropS4:
        case EP_Type.PropF4:
          cmd = m.Addr < 256 ? EP_InstCode.STM_S4_CS8 : EP_InstCode.STM_S4_CS16;
          break;
        default:
          throw new ApplicationException("Merker " + m.ToString() + " is not property");
        }
        _compiler.cur.AddInst(new EP_Compiler.Instruction(cmd, m, node), 2, 0);
        return;
      }
      throw new NotSupportedException("Field name in " + node.ToString());
    }
    /// <summary>Value of an integer constant operand, or a compile error naming where it is.</summary>
    /// <remarks>These operands are folded into instruction arguments, so a fractional constant
    /// cannot be represented. It already failed - unboxing JSValue.Value straight to int threw
    /// InvalidCastException, which Compiler logged without saying where. The refusal is the same,
    /// only now it names the line and the reason.</remarks>
    private int ConstInt(Constant c, CodeNode node) {
      if(!c.Value.Is<int>()) {
        throw new NotSupportedException(_compiler.Where(node) + "operand must be an integer constant, got " + c.Value.ToString());
      }
      return (int)c.Value;
    }

    private void AddCommon(CodeNode node, Expression a, Expression b) {
      if(_compiler.TypeOf(a) == EP_ValType.Float || _compiler.TypeOf(b) == EP_ValType.Float) {
        EmitAs(a, EP_ValType.Float);
        EmitAs(b, EP_ValType.Float);
        _compiler.cur.AddInst(EP_InstCode.FADD, 2, 1);
        return;
      }
      var c1 = a as Constant;
      var c2 = b as Constant;
      if(c1 != null && c2 != null) {
        LoadConstant(node, ConstInt(c1, node) + ConstInt(c2, node));
      } else if(c1 != null && ConstInt(c1, node) == 1) {
        b.Visit(this);
        _compiler.cur.AddInst(EP_InstCode.INC, 1, 1);
      } else if(c1 != null && ConstInt(c1, node) == -1) {
        b.Visit(this);
        _compiler.cur.AddInst(EP_InstCode.DEC, 1, 1);
      } else if(c2 != null && ConstInt(c2, node) == 1) {
        a.Visit(this);
        _compiler.cur.AddInst(EP_InstCode.INC, 1, 1);
      } else if(c2 != null && ConstInt(c2, node) == -1) {
        a.Visit(this);
        _compiler.cur.AddInst(EP_InstCode.DEC, 1, 1);
      } else {
        a.Visit(this);
        b.Visit(this);
        _compiler.cur.AddInst(EP_InstCode.ADD, 2, 1);
      }
    }
    private void CallFunction(Call node, EP_Compiler.Merker m, Expression This) {
      EP_Compiler.Instruction d;
      EP_Compiler.Merker mt;
      Variable v;

      if(m.scope != null) {
        var al = m.scope.memory.Where(z => z.type == EP_Type.PARAMETER).OrderBy(z => z.Addr).ToArray();
        for(int i = al.Length - 1; i >= 0; i--) {
          var pt = al[i].isFloat ? EP_ValType.Float : EP_ValType.Int;
          if(i < node.Arguments.Length) {
            EmitAs(node.Arguments[i], pt);
          } else if(al[i].init != null) {  //TODO: check function(a, b=7)
            EmitAs(al[i].init, pt);
          } else {
            _compiler.cur.AddInst(EP_InstCode.LDI_0, 0, 1);
          }
        }
        if(This == null) {
          _compiler.cur.AddInst(EP_InstCode.LDI_MIN, 0, 1);
        } else if((v = This as Variable) != null && (mt = _compiler.GetMerker(v.Descriptor)).type == EP_Type.REFERENCE) {
          _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.LDI_S4, mt), 0, 1);
        } else if(This is This) {
          _compiler.cur.AddInst(EP_InstCode.LD_P0, 0, 1);
        } else {
          throw new NotSupportedException(This.ToString() + " as this");
        }
        _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.CALL, m, node));
        for(int i = al.Length - 1; i >= 0; i--) {
          _compiler.cur.AddInst(EP_InstCode.NIP);
          d = _compiler._sp.Pop();
          _compiler._sp.Pop();
          _compiler._sp.Push(d);
        }
      } else {
        throw new ApplicationException("undefined function: " + m.vd.Name);
      }
    }
    /// <param name="fc">float-вариант команды; null - команда только целая, и float-операнды
    /// приводятся к int (как ToInt32 в JS для & | ^)</param>
    private void Arg2Op(Expression node, EP_InstCode c, EP_InstCode? fc = null) {
      var a = node.Children[0] as Expression;
      var b = node.Children[1] as Expression;
      if(fc != null && (_compiler.TypeOf(a) == EP_ValType.Float || _compiler.TypeOf(b) == EP_ValType.Float)) {
        EmitAs(a, EP_ValType.Float);
        EmitAs(b, EP_ValType.Float);
        _compiler.cur.AddInst(fc.Value, 2, 1);
      } else {
        EmitAs(a, EP_ValType.Int);
        EmitAs(b, EP_ValType.Int);
        _compiler.cur.AddInst(c, 2, 1);
      }
    }
    /// <summary>Имена конструкторов типа меркера: new Int32(5), this.k = new Float(0).</summary>
    private static readonly string[] TypeNames = { "Boolean", "Int8", "UInt8", "Int16", "UInt16", "Int32", "Float" };

    /// <summary>Значение выражения, приведённое к типу want.</summary>
    /// <remarks>Константа сворачивается при компиляции: целая в float-контексте сразу грузится
    /// как float, без S32TOF в рантайме. Int и UInt в стеке неразличимы, между ними приведения нет.</remarks>
    private void EmitAs(Expression e, EP_ValType want) {
      var have = _compiler.TypeOf(e);
      Constant c;
      if(want == EP_ValType.Float && have != EP_ValType.Float && (c = e as Constant) != null) {
        _compiler.cur.AddInst(EP_Compiler.Instruction.LoadFloat(c.Value == null ? 0 : c.Value.As<double>(), c), 0, 1);
        return;
      }
      e.Visit(this);
      if(want == EP_ValType.Float) {
        if(have == EP_ValType.Int) {
          _compiler.cur.AddInst(EP_InstCode.S32TOF, 1, 1);
        } else if(have == EP_ValType.UInt) {
          _compiler.cur.AddInst(EP_InstCode.U32TOF, 1, 1);
        }
      } else if(have == EP_ValType.Float) {
        _compiler.cur.AddInst(want == EP_ValType.UInt ? EP_InstCode.FTOU32 : EP_InstCode.FTOS32, 1, 1);
      }
    }
    /// <summary>Условие перехода: JZ/JNZ проверяют биты, а у -0.0f они не нулевые, поэтому
    /// float сначала превращается в 0/1.</summary>
    private void EmitCond(CodeNode n) {
      n.Visit(this);
      if(_compiler.TypeOf(n as Expression) == EP_ValType.Float) {
        _compiler.cur.AddInst(EP_InstCode.FCZE, 1, 1);
        _compiler.cur.AddInst(EP_InstCode.CZE, 1, 1);
      }
    }
    /// <summary>a &lt;&lt; b, a &gt;&gt; b, a &gt;&gt;&gt; b: число бит берётся по модулю 32, как в JS.</summary>
    /// <remarks>Команды VM сдвигают только на число из своего байта, поэтому сдвиг на значение
    /// выражения - цикл из сдвигов на 1 бит; работает на прошивках без новой команды.
    /// Порядок вычисления как в JS: сначала a, потом b.</remarks>
    /// <param name="vt">тип левого операнда: UInt у &gt;&gt;&gt;, иначе Int</param>
    private void EmitShift(Expression node, EP_InstCode ic, EP_ValType vt) {
      var a = node.Children[0] as Expression;
      var b = node.Children[1] as Expression;
      Constant c;
      int? n = (c = b as Constant) != null ? (int)c.Value : _compiler.ConstValue(b);
      EmitAs(a, vt);
      if(n != null) {
        _compiler.cur.AddInst(EP_Compiler.Instruction.Shift(ic, n.Value, b), 1, 1);
        return;
      }
      EmitAs(b, EP_ValType.Int);
      _compiler.cur.AddInst(EP_Compiler.Instruction.LoadInt(31, null), 0, 1);
      _compiler.cur.AddInst(EP_InstCode.AND, 2, 1);
      // a n -- a<<n; проверка счётчика внизу: за итерацию на один переход меньше
      var body = new EP_Compiler.Instruction(EP_InstCode.LABEL);
      var test = new EP_Compiler.Instruction(EP_InstCode.LABEL);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JMP) { _ref = test });
      _compiler.cur.AddInst(body);
      _compiler.cur.AddInst(EP_InstCode.SWAP);
      _compiler.cur.AddInst(EP_Compiler.Instruction.Shift(ic, 1, node));
      _compiler.cur.AddInst(EP_InstCode.SWAP);
      _compiler.cur.AddInst(EP_InstCode.DEC);
      _compiler.cur.AddInst(test);
      _compiler.cur.AddInst(EP_InstCode.DUP);
      _compiler.cur.AddInst(new EP_Compiler.Instruction(EP_InstCode.JNZ) { _ref = body });
      _compiler.cur.AddInst(EP_InstCode.DROP, 2, 1);
    }
    /// <summary>INC/DEC для ++/--; у float - сложение с 1.0f.</summary>
    /// <remarks>Учёт стека как у INC(0, 1): загруженное значение вызывающий уже снял с _sp.</remarks>
    private void StepOne(EP_InstCode ic, bool isFloat) {
      if(isFloat) {
        _compiler.cur.AddInst(EP_Compiler.Instruction.LoadFloat(1, null), 0, 0);
        _compiler.cur.AddInst(ic == EP_InstCode.INC ? EP_InstCode.FADD : EP_InstCode.FSUB, 0, 1);
      } else {
        _compiler.cur.AddInst(ic, 0, 1);
      }
    }
    /// <summary>Float(x), Int32(x), UInt32(x), isNaN(x), isFinite(x); false - не встроенная.</summary>
    private bool CallBuiltin(Call node, string name) {
      EP_ValType t;
      EP_InstCode? op = null;
      switch(name) {
      case "Float":
        t = EP_ValType.Float;
        break;
      case "Int32":
        t = EP_ValType.Int;
        break;
      case "UInt32":
        t = EP_ValType.UInt;
        break;
      case "isNaN":
        t = EP_ValType.Float;
        op = EP_InstCode.FISNAN;
        break;
      case "isFinite":
        t = EP_ValType.Float;
        op = EP_InstCode.FISFINITE;
        break;
      default:
        return false;
      }
      if(node.Arguments.Length == 0) {
        if(op != null) {
          throw new NotSupportedException(_compiler.Where(node) + name + "() needs an argument");
        }
        _compiler.cur.AddInst(EP_InstCode.LDI_0, 0, 1);   // Float() - 0.0f, те же нулевые биты
        return true;
      }
      EmitAs(node.Arguments[0], t);
      if(op != null) {
        _compiler.cur.AddInst(op.Value, 1, 1);
      }
      return true;
    }
    /// <summary>Math.abs/round/trunc(x), Math.min/max(a, b, ...) - всегда float.</summary>
    private void CallMath(Call node, string name) {
      int argc = node.Arguments.Length;
      if(argc == 0 || (argc > 1 && name != "min" && name != "max")) {
        throw new NotSupportedException(_compiler.Where(node) + "wrong number of arguments: " + node.ToString());
      }
      EmitAs(node.Arguments[0], EP_ValType.Float);
      switch(name) {
      case "abs":
        _compiler.cur.AddInst(EP_InstCode.FABS, 1, 1);
        break;
      case "round":
        _compiler.cur.AddInst(EP_InstCode.FROUND, 1, 1);
        break;
      case "trunc":
        _compiler.cur.AddInst(EP_InstCode.FTRUNC, 1, 1);
        break;
      default:   // min, max - цепочкой
        for(int i = 1; i < argc; i++) {
          EmitAs(node.Arguments[i], EP_ValType.Float);
          _compiler.cur.AddInst(name == "min" ? EP_InstCode.FMIN : EP_InstCode.FMAX, 2, 1);
        }
        break;
      }
    }
    private void SafeCodeBlock(CodeNode node, int sp = -1) {
      if(node is CodeBlock) {
        node.Visit(this);
      } else {
        if(sp < 0) {
          sp = _compiler._sp.Count;
        }
        // Команды без своего узла (арифметика, загрузка поля) получат строку этого оператора, см.
        // EP_Compiler.CodeLines; вложенный оператор переопределит её на время своего разбора.
        var outer = _compiler.Statement;
        _compiler.Statement = node;
        try {
          node.Visit(this);
          while(_compiler._sp.Count > sp) {
            var d = _compiler._sp.Pop();
            if(d == null || !d.canOptimized || !_compiler.cur.code.Remove(d)) {
              _compiler.cur.AddInst(EP_InstCode.DROP);
            }
          }
        }
        finally {
          _compiler.Statement = outer;
        }
      }
    }
    private void DefineFunction(FunctionDefinition node, EP_Compiler.Merker fm) {
      fm.scope = _compiler.ScopePush(fm);
      for(int i = 0; i < node.Parameters.Count; i++) {
        if(i > 15) {
          throw new IndexOutOfRangeException(node.Reference.Descriptor.Name + "(.., " + node.Parameters[i].Name + " ..)" + " too many parameters");
        }
        var m = _compiler.GetMerker(node.Parameters[i]);
        if(m == null || m.type != EP_Type.PARAMETER || m.Addr != (uint)i + 1) {
          throw new ApplicationException("Unknown merker in pass 2: " + node.Reference.Descriptor.Name);
        }
      }
      node.Body.Visit(this);
      if(_compiler.cur.code.Count == 0 || _compiler.cur.code[_compiler.cur.code.Count - 1]._code.Length != 1 || _compiler.cur.code[_compiler.cur.code.Count - 1]._code[0] != (byte)EP_InstCode.RET) {
        _compiler.cur.AddInst(EP_InstCode.RET);
      }
      _compiler.ScopePop();
    }

    internal class Loop {
      public Loop(int sp1, ICollection<string> labels) {
        this.sp1 = sp1;
        this.labels = labels;
        L1 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        L2 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
        L3 = new EP_Compiler.Instruction(EP_InstCode.LABEL);
      }
      public EP_Compiler.Instruction L1, L2, L3;
      public int sp1, sp2;
      public ICollection<string> labels;
    }
  }
}
