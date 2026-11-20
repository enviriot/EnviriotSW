using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NiL.JS.Core;
using NiL.JS.Expressions;
using NiL.JS.Statements;

namespace X13.DevicePLC {
  internal class EP_VP1 : Visitor<EP_VP1> {
    private readonly EP_Compiler _compiler;

    public EP_VP1(EP_Compiler compiler) {
      _compiler = compiler;
    }

    protected override EP_VP1 Visit(CodeNode node) {
      return this;
    }
    protected override EP_VP1 Visit(Addition node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(BitwiseConjunction node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(ArrayDefinition node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Assignment node) {
      var aoc = node.Children[0] as AssignmentOperatorCache;
      if(aoc != null) {   // this.X += v: тип поля - тип выражения this.X + v
        DeclareUpdatedField(node, aoc.Source as Property, node.Children[1] as Expression);
      }
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Call node) {
      for(int i = node.Arguments.Length - 1; i >= 0; i--) {
        node.Arguments[i].Visit(this);
      }
      node.Children[0].Visit(this);
      _compiler.DeferCall(node);
      return this;
    }
    protected override EP_VP1 Visit(ClassDefinition node) {
      var mc = _compiler.DefineMerker(node.Reference.Descriptor, EP_Type.CLASS);
      mc.scope = _compiler.ScopePush(mc);
      var ctor = _compiler.DefineMerker(node.Constructor.Reference.Descriptor, EP_Type.FUNCTION);
      DefineFunction(node.Constructor, ctor, mc.scope.memory);
      //foreach(var m in ctor.scope.memory.Where(z=>IsProperty(z.type))){
      //  int idx=m.fName.IndexOf(".constructor");
      //  m.fName=m.fName.Remove(idx, 12);
      //}
      //mc.scope.memory.AddRange(ctor.scope.memory.Where(z => IsProperty(z.type)));
      // поля размечает EP_Compiler.Parse после VP1: они появляются и в методах, и снаружи

      foreach(var fv in node.Members.Where(z => z.Value is FunctionDefinition && z.Name is Constant)) {
        var fm = mc.scope.GetProperty((fv.Name as Constant).Value.ToString(), EP_Type.FUNCTION);
        DefineFunction(fv.Value as FunctionDefinition, fm, mc.scope.memory);
      }
      _compiler.ScopePop();
      return this;
    }
    protected override EP_VP1 Visit(Constant node) {
      return this;
    }
    protected override EP_VP1 Visit(Decrement node) {
      DeclareUpdatedField(node, node.Children[0] as Property, null);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Delete node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(DeleteProperty node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Division node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Equal node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Expression node) {
      var v = node as AssignmentOperatorCache;
      if(v != null) {
        return v.Source.Visit(this);
      }
      if(node is This) {
        return this;
      }
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(FunctionDefinition node) {
      var fm = _compiler.DefineMerker(node.Reference.Descriptor, EP_Type.FUNCTION);
      DefineFunction(node, fm, null);
      return this;
    }
    protected override EP_VP1 Visit(Property node) {
      node.Source.Visit(this);
      //node.FieldName.Visit(this);
      //Constant c;
      //if(node.FieldName is This && (c = node.FieldName as Constant) != null && c.Value != null && c.Value.ValueType == JSValueType.String) {
      //  _compiler.cur.GetProperty(c.Value.ToString());
      //}
      return this;
    }
    protected override EP_VP1 Visit(Variable node) {
      return this;
    }
    protected override EP_VP1 Visit(VariableReference node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(In node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Increment node) {
      DeclareUpdatedField(node, node.Children[0] as Property, null);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(InstanceOf node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(NiL.JS.Expressions.ObjectDefinition node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Less node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(LessOrEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(LogicalConjunction node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(LogicalNegation node) {
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(LogicalDisjunction node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Modulo node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(More node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(MoreOrEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Multiplication node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Negation node) {
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(New node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Comma node) {
      foreach(var child in node.Children) child.Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(BitwiseNegation node) {
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(NotEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(NumberAddition node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(NumberLess node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(NumberLessOrEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(NumberMore node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(NumberMoreOrEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(BitwiseDisjunction node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(RegExpExpression node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(SetProperty node) {
      Constant c, c1;
      FunctionDefinition fd;
      Variable f;
      EP_Compiler.Merker m;
      EP_Compiler.Scope sc;
      Call ca;

      sc = FieldScope(node, node.Source);
      if((c = node.FieldName as Constant) == null || c.Value == null || c.Value.ValueType != JSValueType.String) {
        throw new NotSupportedException(node.Source.ToString() + "." + node.FieldName.ToString() + " as FieldName");
      }

      if((ca = node.Value as Call) != null && (f = ca.Children[0] as Variable) != null) {
        EP_Type t;
        switch(f.Name) {
        case "Boolean":
          t = EP_Type.PropB1;
          break;
        case "Int8":
          t = EP_Type.PropS1;
          break;
        case "UInt8":
          t = EP_Type.PropU1;
          break;
        case "Int16":
          t = EP_Type.PropS2;
          break;
        case "UInt16":
          t = EP_Type.PropU2;
          break;
        case "Int32":
          t = EP_Type.PropS4;
          break;
        case "Float":
          t = EP_Type.PropF4;
          break;
        default:
          t = EP_Type.NONE;
          break;
        }
        if(t == EP_Type.NONE && ca.CallMode == CallMode.Construct) {
          if(_compiler.IsPinCtor(ca)) {
            throw new NotSupportedException(_compiler.Where(node) + node.ToString() + ": Pin must be declared with var at the top level");
          }
          throw new NotSupportedException("enclosed constructor"+ node.ToString());
        } else if(t == EP_Type.NONE) {   // значение - вызов функции: тип поля выведет InferTypes
          node.Value.Visit(this);
          _compiler.DeferType(sc.GetProperty(c.Value.ToString(), EP_Type.PropS4), node.Value as Expression);
        } else {
          sc.GetProperty(c.Value.ToString(), t).explicitType = true;
        }
      } else if((c1 = node.Value as Constant) != null && c1.Value != null && c1.Value.ValueType == JSValueType.Boolean) {
        sc.GetProperty(c.Value.ToString(), EP_Type.PropB1);
      } else if(EP_Compiler.IsFloatConst(node.Value as Constant)) {
        sc.GetProperty(c.Value.ToString(), EP_Type.PropF4);
      } else if((fd = node.Value as FunctionDefinition) != null) {
        m=sc.GetProperty(c.Value.ToString(), EP_Type.FUNCTION);
        DefineFunction(fd, m, sc.memory);
      } else {
        node.Value.Visit(this);
        // тип выведет InferTypes: this.M = this.M * 0.75 + B * 0.25 иначе стало бы целым
        _compiler.DeferType(sc.GetProperty(c.Value.ToString(), EP_Type.PropS4), node.Value as Expression);
      }
      return this;
    }
    protected override EP_VP1 Visit(SignedShiftLeft node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(SignedShiftRight node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(StrictEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(StrictNotEqual node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(StringConcatenation node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Substract node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Conditional node) {
      node.Children[0].Visit(this);
      node.Branches[0].Visit(this);
      if(node.Branches.Count > 1) {
        node.Branches[1].Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(ConvertToBoolean node) {
      return this;
    }
    protected override EP_VP1 Visit(ConvertToInteger node) {
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(ConvertToNumber node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(ConvertToString node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(ConvertToUnsignedInteger node) {
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(TypeOf node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(UnsignedShiftRight node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(BitwiseExclusiveDisjunction node) {
      node.Children[1].Visit(this);
      node.Children[0].Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Yield node) {
      return Visit(node as Expression);
    }
    protected override EP_VP1 Visit(Break node) {
      return this;
    }
    protected override EP_VP1 Visit(CodeBlock node) {
      //EP_Compiler.Merker m;
      FunctionDefinition fd;
      if(node.Variables!=null) {
        foreach(var v in node.Variables) {
          if((fd = v.Initializer as FunctionDefinition) != null) {
            var fm = _compiler.DefineMerker(v, EP_Type.FUNCTION);
            DefineFunction(fd, fm, null);
          }
          //m = _compiler.DefineMerker(v);
          //if(m.vd.Initializer != null) {
          //  m.vd.Initializer.Visit(this);
          //}
        }
      }

      for(var i = 0; i < node.Body.Length; i++) {
        node.Body[i].Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(Continue node) {
      return this;
    }
    protected override EP_VP1 Visit(Debugger node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(DoWhile node) {
      node.Body.Visit(this);
      node.Condition.Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(Empty node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(ForIn node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(ForOf node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(For node) {
      if(node.Initializer != null) {
        node.Initializer.Visit(this);
      }
      if(node.Condition != null) {
        node.Condition.Visit(this);
      }
      node.Body.Visit(this);

      if(node.Post != null) {
        node.Post.Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(IfElse node) {
      node.Condition.Visit(this);
      node.Then.Visit(this);
      if(node.Else != null) {
        node.Else.Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(InfinityLoop node) {
      node.Body.Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(LabeledStatement node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(Return node) {
      if(node.Value != null) {
        var fm = _compiler.cur.fm;
        if(fm != null) {   // для EP_Compiler.InferTypes
          if(fm.returns == null) {
            fm.returns = new List<Expression>();
          }
          fm.returns.Add(node.Value);
        }
        node.Value.Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(Switch node) {
      int i, j;
      var cvs = node.Cases.Where(z => z.Statement != null).OrderBy(z => z.Index).Union(node.Cases.Where(z => z.Statement == null)).ToArray();
      node.Image.Visit(this);
      for(j = 0; j < cvs.Length; j++) {
        if(cvs[j].Statement != null) {
          cvs[j].Statement.Visit(this);
        }
      }
      for(i = 0; i < node.Body.Length; i++) {
        node.Body[i].Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(Throw node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(TryCatch node) {
      return Visit(node as CodeNode);
    }
    protected override EP_VP1 Visit(VariableDefinition node) {
      Assignment a1;
      EP_Compiler.Merker m;
      Variable v;

      for(int i = 0; i < node.Initializers.Length; i++) {
        if((v=node.Initializers[i] as Variable)!=null) {
          m = _compiler.DefineMerker(v.Descriptor);
          continue;
        }
        if((a1 = node.Initializers[i] as Assignment) != null && (v = a1.Children[0] as Variable) != null) {
          if(_compiler.IsPinCtor(a1.Children[1] as Expression)) {
            _compiler.DefinePin(v.Descriptor, (Call)a1.Children[1]);   // let и var в функции DefinePin отвергнет
            continue;
          }
          if(v.Descriptor.IsLexicalScoped) {
            m = _compiler.DefineMerker(v.Descriptor);  // локальная переменная
            _compiler.DeferType(m, a1.Children[1] as Expression);
            a1.Children[1].Visit(this);
            continue;
          } else {
            Call ca;
            Variable f;
            Constant c;
            if((ca = a1.Children[1] as Call) != null && (f = ca.Children[0] as Variable) != null) {
              EP_Type t;
              switch(f.Name) {
              case "Boolean":
                t = EP_Type.BOOL;
                break;
              case "Int8":
                t = EP_Type.SINT8;
                break;
              case "UInt8":
                t = EP_Type.UINT8;
                break;
              case "Int16":
                t = EP_Type.SINT16;
                break;
              case "UInt16":
                t = EP_Type.UINT16;
                break;
              case "Int32":
                t = EP_Type.SINT32;
                break;
              case "Float":
                t = EP_Type.FLOAT;
                break;
              case "Uint8Array":
              case "Uint16Array":
              case "Int8Array":
              case "Int16Array":
              case "Int32Array":
                if(ca.Arguments.Count() == 1 && ca.Arguments[0] is ArrayDefinition) {
                  List<byte> narr=new List<byte>();
                  int cnt = 0;
                  t = EP_Type.NONE;
                  switch(f.Name) {
                  case "Uint8Array":
                    t = EP_Type.U8_CARR;
                    foreach(var el in (ca.Arguments[0] as ArrayDefinition).Elements) {
                      narr.Add((byte)el.Evaluate(null));
                      cnt++;
                    }
                    break;
                  case "Int8Array":
                    t = EP_Type.I8_CARR;
                    foreach(var el in (ca.Arguments[0] as ArrayDefinition).Elements) {
                      narr.Add((byte)el.Evaluate(null));
                      cnt++;
                    }
                    break;
                  case "Uint16Array":
                    t = EP_Type.U16_CARR;
                    foreach(var el in (ca.Arguments[0] as ArrayDefinition).Elements) {
                      narr.AddRange(BitConverter.GetBytes((ushort)el.Evaluate(null)));
                      cnt++;
                    }
                    break;
                  case "Int16Array":
                    t = EP_Type.I16_CARR;
                    foreach(var el in (ca.Arguments[0] as ArrayDefinition).Elements) {
                      narr.AddRange(BitConverter.GetBytes((ushort)el.Evaluate(null)));
                      cnt++;
                    }
                    break;
                  case "Int32Array":
                    t = EP_Type.I32_CARR;
                    foreach(var el in (ca.Arguments[0] as ArrayDefinition).Elements) {
                      narr.AddRange(BitConverter.GetBytes((int)el.Evaluate(null)));
                      cnt++;
                    }
                    break;
                  }
                  EP_Compiler.Instruction i1=new EP_Compiler.Instruction(narr.ToArray());
                  _compiler.dataBlock.AddInst(i1, 0, 0);
                  m = _compiler.DefineMerker(v.Descriptor, t);
                  i1._param = m;
                  i1._cn = v;
                  m.init = ca.Arguments[0];
                  m.ElementCount = cnt;
                  continue;
                } else {
                  throw new NotSupportedException("P1: " + ca.ToString());
                }
              default:
                t = EP_Type.NONE;
                break;
              }
              if(t == EP_Type.NONE && ca.CallMode == CallMode.Construct) {
                m = _compiler.DefineMerker(v.Descriptor, EP_Type.REFERENCE);
                Variable cl = ca.LeftOperand as Variable;
                EP_Compiler.Merker m1;
                if(cl!=null && (m1 = _compiler.GetMerker(cl.Descriptor))!=null){
                  m.scope = m1.scope;
                }
                m.type = EP_Type.REFERENCE;
                foreach(var arg in ca.Arguments) {
                  arg.Visit(this);
                }
                _compiler.DeferCall(ca);   // аргументы задают тип параметров конструктора
                continue;
              } else {
                m = _compiler.DefineMerker(v.Descriptor, t);
                if(t == EP_Type.NONE) {   // var b = f(x): тип - тип результата вызова
                  _compiler.DeferType(m, ca);
                }
              }
            } else if((c = a1.Children[1] as Constant) != null && c.Value != null && c.Value.ValueType == JSValueType.Boolean) {
              m = _compiler.DefineMerker(v.Descriptor, EP_Type.BOOL);
            } else {
              // var b = 1.5, var b = Math.round(a * 10) / 10: Md, пока InferTypes не найдёт float
              m = _compiler.DefineMerker(v.Descriptor, EP_Type.SINT32);
              _compiler.DeferType(m, a1.Children[1] as Expression);
            }
          }
        }

        node.Initializers[i].Visit(this);
      }
      return this;
    }
    protected override EP_VP1 Visit(While node) {
      node.Condition.Visit(this);
      node.Body.Visit(this);
      return this;
    }
    protected override EP_VP1 Visit(With node) {
      return Visit(node as CodeNode);
    }

    private void DefineFunction(FunctionDefinition node, EP_Compiler.Merker fm, List<EP_Compiler.Merker> parentMemory) {
      fm.scope = _compiler.ScopePush(fm);
      //if(parentMemory != null) {
      //  fm.scope.memory.AddRange(parentMemory.Where(z => IsProperty(z.type)));
      //}

      for(int i = 0; i < node.Parameters.Count; i++) {
        if(i > 15) {
          throw new IndexOutOfRangeException(node.Reference.Descriptor.Name + "(.., " + node.Parameters[i].Name + " ..)" + " too many parameters");
        }
        var m = _compiler.DefineMerker(node.Parameters[i], EP_Type.PARAMETER);
        m.Addr = (uint)i + 1;
        _compiler.DeferType(m, node.Parameters[i].Initializer);   // function f(a = Float(0))
      }
      node.Body.Visit(this);
      _compiler.ScopePop();
    }
    private static bool IsProperty(EP_Type type) {
      return type == EP_Type.PropB1 || type == EP_Type.PropS1 || type == EP_Type.PropS2 || type == EP_Type.PropS4 || type == EP_Type.PropU1 || type == EP_Type.PropU2 || type == EP_Type.PropF4;
    }
    /// <summary>Область полей, куда пишет node: экземпляр obj (REFERENCE) или this ближайшего класса.</summary>
    private EP_Compiler.Scope FieldScope(CodeNode node, CodeNode source) {
      Variable f;
      EP_Compiler.Merker m;
      EP_Compiler.Scope sc;
      if((f = source as Variable) != null && (m = _compiler.GetMerker(f.Descriptor)) != null && m.type == EP_Type.REFERENCE) {
        if(m.pin != null) {   // иначе новое поле попало бы в PinScope, общую для всех экземпляров
          throw new NotSupportedException(_compiler.Where(node) + node.ToString() + ": Pin output is set by Tick only");
        }
        return m.scope;
      }
      if(source is This) {
        sc = _compiler.cur;
        while(sc.fm.type!=EP_Type.CLASS) {
          sc = sc._parent;
          if(sc==null) {
            sc = _compiler.cur;
            break;
          }
        }
        return sc;
      }
      throw new NotSupportedException(source.ToString() + " as object");
    }
    /// <summary>Поле, которое пишет this.X += v, this.X++ или --obj.X, объявляется, как при this.X = v.</summary>
    /// <remarks>NiL разбирает их не в SetProperty, а в Assignment или Increment над Property. Поле, которое
    /// только накапливается, прежде не размечалось, и VP2 падал "Field name in this.X". Уже объявленное поле
    /// свой тип сохраняет; value - выражение, по которому InferTypes сделает поле float.</remarks>
    private void DeclareUpdatedField(CodeNode node, Property p, Expression value) {
      Constant c;
      if(p == null) {
        return;
      }
      var sc = FieldScope(node, p.Source);
      if((c = p.FieldName as Constant) == null || c.Value == null || c.Value.ValueType != JSValueType.String) {
        throw new NotSupportedException(p.Source.ToString() + "." + p.FieldName.ToString() + " as FieldName");
      }
      _compiler.DeferType(sc.GetProperty(c.Value.ToString(), EP_Type.PropS4), value);
    }

  }
}
