using System;
using System.Collections.Generic;
using X13.DevicePLC;

namespace X13.Tests {
  // Ограниченная тестовая VM: исполняет сгенерированный байткод, неизвестная команда — ошибка теста.
  // Стек, кадры и адресация соответствуют контракту EspNode; это не замена проверки на устройстве.
  internal sealed class DevicePlcTestVm {
    private readonly byte[] _code = new byte[65536];
    private readonly int[] _ram = new int[4096];
    private readonly int _programLength;
    private int _pc, _sp, _frame;
    internal Action<DevicePlcTestVm, byte> Api;
    internal int MaxStackDepth { get; private set; }

    internal DevicePlcTestVm(SortedList<uint, byte[]> hex) {
      foreach(var chunk in hex) {
        Array.Copy(chunk.Value, 0, _code, chunk.Key, chunk.Value.Length);
        _programLength = Math.Max(_programLength, checked((int)chunk.Key + chunk.Value.Length));
      }
    }
    internal byte[] ProgramData(int offset, int length) {
      if(offset < 0 || length < 0 || offset > _programLength || length > _programLength - offset)
        throw new InvalidOperationException("VM program data out of range");
      var bytes = new byte[length];
      Array.Copy(_code, offset, bytes, 0, length);
      return bytes;
    }
    internal void Push(int value) {
      if(_sp <= 0) throw new InvalidOperationException("VM stack overflow");
      _ram[--_sp] = value;
      MaxStackDepth = Math.Max(MaxStackDepth, _ram.Length - _sp);
    }
    internal int Pop() {
      if(_sp >= _frame) throw new InvalidOperationException("VM stack underflow");
      return _ram[_sp++];
    }
    private int Read(int bytes) {
      int value = 0;
      for(int i = 0; i < bytes; i++) value |= _code[_pc++] << (8 * i);
      return value;
    }
    private static float Float(int bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
    private static int Bits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
    private void Binary(Func<int, int, int> op) { int b = Pop(), a = Pop(); Push(op(a, b)); }
    private void FloatBinary(Func<float, float, float> op) { Binary((a, b) => Bits(op(Float(a), Float(b)))); }
    private void Compare(Func<float, float, bool> op) { Binary((a, b) => op(Float(a), Float(b)) ? 1 : 0); }
    private int Memory(int address, int kind) {
      if(kind == 0) return (int)((uint)_ram[address / 32] >> (address % 32)) & 1;
      if(kind == 3) return _ram[address];
      int size = kind == 1 || kind == 4 ? 1 : 2;
      int byteAddress = address * size;
      int value = (int)((uint)_ram[byteAddress / 4] >> ((byteAddress % 4) * 8));
      return kind == 1 ? (sbyte)value : kind == 2 ? (short)value : kind == 4 ? value & 255 : value & 65535;
    }
    private void Store(int address, int kind, int value) {
      if(kind == 3) { _ram[address] = value; return; }
      int size = kind == 0 ? 1 : kind == 1 ? 8 : 16;
      int bit = address * size, shift = bit % 32;
      int mask = ((1 << size) - 1) << shift;
      _ram[bit / 32] = (_ram[bit / 32] & ~mask) | (((kind == 0 ? (value == 0 ? 0 : 1) : value) << shift) & mask);
    }
    internal double Value(string tag) {
      int address = int.Parse(tag.Substring(2));
      switch(tag.Substring(0, 2)) {
      case "Mf": return Float(_ram[address]);
      case "Md": return _ram[address];
      case "Mz": return Memory(address, 0);
      default: throw new NotSupportedException("VM tag " + tag);
      }
    }
    internal void Cycle(bool initialize = false) {
      _pc = initialize ? 0 : 4;
      _sp = _frame = _ram.Length;
      for(int budget = 0; budget < 65535; budget++) {
        byte op = _code[_pc++];
        if(op >= 0x40 && op <= 0x7F) {
          int slot = op & 15;
          int address = (op & 0x10) == 0 ? _frame + 2 + slot : _frame - 1 - slot;
          if(op < 0x60) Push(_ram[address]); else _ram[address] = Pop();
          continue;
        }
        if(op >= 0x80 && op <= 0xBF && op != 0x87) {
          bool store = op >= 0xA0;
          int kind = op & 7, mode = (op & 0x18) >> 3;
          if(kind > (store ? 3 : 5)) throw new NotSupportedException("VM memory opcode " + op);
          int address = mode == 3 ? Read(2) : Pop();
          if(mode == 1 || mode == 2) {
            int factor = kind == 0 ? 32 : kind == 1 || kind == 4 ? 4 : kind == 2 || kind == 5 ? 2 : 1;
            address = address * factor + Read(mode);
          }
          if(store) Store(address, kind, Pop()); else Push(Memory(address, kind));
          continue;
        }
        switch((EP_InstCode)op) {
        case EP_InstCode.NOP: break;
        case EP_InstCode.DUP: { int a = Pop(); Push(a); Push(a); break; }
        case EP_InstCode.DROP: Pop(); break;
        case EP_InstCode.NIP: { int a = Pop(); Pop(); Push(a); break; }
        case EP_InstCode.SWAP: { int b = Pop(), a = Pop(); Push(b); Push(a); break; }
        case EP_InstCode.NOT: Push(~Pop()); break;
        case EP_InstCode.AND: Binary((a, b) => a & b); break;
        case EP_InstCode.OR: Binary((a, b) => a | b); break;
        case EP_InstCode.XOR: Binary((a, b) => a ^ b); break;
        case EP_InstCode.LSL: { int n = Read(1), a = Pop(); Push(n < 32 ? a << n : 0); break; }
        case EP_InstCode.LSR: { int n = Read(1), a = Pop(); Push(n < 32 ? (int)((uint)a >> n) : 0); break; }
        case EP_InstCode.ASR: { int n = Read(1), a = Pop(); Push(a >> Math.Min(n, 31)); break; }
        case EP_InstCode.ADD: Binary((a, b) => unchecked(a + b)); break;
        case EP_InstCode.SUB: Binary((a, b) => unchecked(a - b)); break;
        case EP_InstCode.MUL: Binary((a, b) => unchecked(a * b)); break;
        case EP_InstCode.DIV: Binary((a, b) => a / b); break;
        case EP_InstCode.MOD: Binary((a, b) => a % b); break;
        case EP_InstCode.INC: Push(unchecked(Pop() + 1)); break;
        case EP_InstCode.DEC: Push(unchecked(Pop() - 1)); break;
        case EP_InstCode.NEG: Push(unchecked(-Pop())); break;
        case EP_InstCode.FADD: FloatBinary((a, b) => a + b); break;
        case EP_InstCode.FSUB: FloatBinary((a, b) => a - b); break;
        case EP_InstCode.FMUL: FloatBinary((a, b) => a * b); break;
        case EP_InstCode.FDIV: FloatBinary((a, b) => a / b); break;
        case EP_InstCode.FMIN: FloatBinary(Math.Min); break;
        case EP_InstCode.FMAX: FloatBinary(Math.Max); break;
        case EP_InstCode.FNEG: Push(Bits(-Float(Pop()))); break;
        case EP_InstCode.FABS: Push(Bits(Math.Abs(Float(Pop())))); break;
        case EP_InstCode.CEQ: Binary((a, b) => a == b ? 1 : 0); break;
        case EP_InstCode.CNE: Binary((a, b) => a != b ? 1 : 0); break;
        case EP_InstCode.CGT: Binary((a, b) => a > b ? 1 : 0); break;
        case EP_InstCode.CGE: Binary((a, b) => a >= b ? 1 : 0); break;
        case EP_InstCode.CLT: Binary((a, b) => a < b ? 1 : 0); break;
        case EP_InstCode.CLE: Binary((a, b) => a <= b ? 1 : 0); break;
        case EP_InstCode.CZE: Push(Pop() == 0 ? 1 : 0); break;
        case EP_InstCode.FCEQ: Compare((a, b) => a == b); break;
        case EP_InstCode.FCNE: Compare((a, b) => a != b); break;
        case EP_InstCode.FCGT: Compare((a, b) => a > b); break;
        case EP_InstCode.FCGE: Compare((a, b) => a >= b); break;
        case EP_InstCode.FCLT: Compare((a, b) => a < b); break;
        case EP_InstCode.FCLE: Compare((a, b) => a <= b); break;
        case EP_InstCode.FCZE: Push(Float(Pop()) == 0 ? 1 : 0); break;
        case EP_InstCode.FISFINITE: { float a = Float(Pop()); Push(float.IsNaN(a) || float.IsInfinity(a) ? 0 : 1); break; }
        case EP_InstCode.FISNAN: Push(float.IsNaN(Float(Pop())) ? 1 : 0); break;
        case EP_InstCode.S32TOF: Push(Bits(Pop())); break;
        case EP_InstCode.U32TOF: Push(Bits((uint)Pop())); break;
        case EP_InstCode.FTOS32:
        case EP_InstCode.FTOU32: {
          double a = Float(Pop());
          Push(double.IsNaN(a) || double.IsInfinity(a) ? 0 : unchecked((int)(long)(Math.Truncate(a) % 4294967296.0)));
          break;
        }
        case EP_InstCode.LDI_0: Push(0); break;
        case EP_InstCode.LDI_1: Push(1); break;
        case EP_InstCode.LDI_M1: Push(-1); break;
        case EP_InstCode.LDI_MIN: Push(int.MinValue); break;
        case EP_InstCode.LDI_S1: Push((sbyte)Read(1)); break;
        case EP_InstCode.LDI_U1: Push(Read(1)); break;
        case EP_InstCode.LDI_S2: Push((short)Read(2)); break;
        case EP_InstCode.LDI_U2: Push(Read(2)); break;
        case EP_InstCode.LDI_S4: Push(Read(4)); break;
        case EP_InstCode.CHECK_IDX: {
          int length = Read(2), index = Pop();
          if((uint)index >= (uint)length) throw new InvalidOperationException("VM index out of range (0x90)");
          Push(index); break;
        }
        case EP_InstCode.LPM_U1: {
          int address = Read(2), index = Pop();
          Push(_code[checked(address + index)]); break;
        }
        case EP_InstCode.JMP: _pc = Read(2); break;
        case EP_InstCode.JZ: { int at = Read(2); if(Pop() == 0) _pc = at; break; }
        case EP_InstCode.JNZ: { int at = Read(2); if(Pop() != 0) _pc = at; break; }
        case EP_InstCode.CALL: { int at = Read(2); Push(_pc); Push(_frame); _frame = _sp; _pc = at; break; }
        case EP_InstCode.RET:
          if(_frame == _ram.Length) return;
          _sp = _frame; _frame = _ram[_sp++]; _pc = _ram[_sp++]; break;
        case EP_InstCode.API:
          if(Api == null) throw new NotSupportedException("No test API handler");
          Api(this, (byte)Read(1)); break;
        default: throw new NotSupportedException("VM opcode 0x" + op.ToString("X2"));
        }
      }
      throw new InvalidOperationException("VM instruction budget exceeded");
    }
  }
}
