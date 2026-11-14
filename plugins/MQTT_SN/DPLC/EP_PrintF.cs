using System;
using System.Collections.Generic;
using System.Linq;

namespace X13.DevicePLC {
  internal static class EP_PrintF {
    internal static EP_ValType[] Arguments(string format) {
      if(format.Length > ushort.MaxValue || format.Any(c => c > 255))
        throw new FormatException("PrintF format must be a byte string within the VM address space");
      var types = new List<EP_ValType>();
      for(int i = 0; i < format.Length;) {
        if(format[i++] != '%') continue;
        if(i < format.Length && format[i] == '%') { i++; continue; }
        bool flag = i < format.Length && (format[i] == '0' || format[i] == '-');
        if(flag) i++;
        bool width = i < format.Length && format[i] >= '1' && format[i] <= '9';
        if(width) i++;
        if(flag && !width) throw new FormatException("PrintF padding requires width 1..9");
        bool precision = i < format.Length && format[i] == '.';
        if(precision) {
          i++;
          if(i >= format.Length || format[i] < '0' || format[i] > '8')
            throw new FormatException("PrintF precision must be 0..8");
          i++;
        }
        if(i >= format.Length || "cdxXf".IndexOf(format[i]) < 0)
          throw new FormatException("PrintF supports %c, %d, %x, %X, %f and %%");
        char kind = format[i++];
        if(kind == 'c' && (width || precision) || kind != 'f' && precision)
          throw new FormatException("PrintF width/precision is not supported for this conversion");
        types.Add(kind == 'f' ? EP_ValType.Float : (kind == 'x' || kind == 'X') ? EP_ValType.UInt : EP_ValType.Int);
        if(types.Count > 256) throw new FormatException("PrintF supports at most 256 value arguments");
      }
      return types.ToArray();
    }
  }
}
