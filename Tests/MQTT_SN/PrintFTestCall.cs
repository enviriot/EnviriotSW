using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using X13.DevicePLC;

namespace X13.Tests {
  internal sealed class PrintFTestCall {
    internal int Stream;
    internal string Format;
    internal int[] Values;

    internal static PrintFTestCall Read(DevicePlcTestVm vm) {
      int stream = vm.Pop(), offset = vm.Pop(), length = vm.Pop(), count = vm.Pop();
      if(count < 0 || count > 256) throw new InvalidOperationException("PrintF argument count");
      var result = new PrintFTestCall {
        Stream = stream, Format = new string(vm.ProgramData(offset, length).Select(b => (char)b).ToArray()), Values = new int[count]
      };
      for(int i = 0; i < count; i++) result.Values[i] = vm.Pop();
      return result;
    }

    internal byte[] Render() {
      if(EP_PrintF.Arguments(Format).Length != Values.Length) throw new InvalidOperationException("PrintF format/argument mismatch");
      int index = 0;
      string rendered = Regex.Replace(Format, @"%([-0]?)([1-9]?)(?:\.([0-8]))?([cdxXf%])", match => {
        char kind = match.Groups[4].Value[0];
        if(kind == '%') return "%";
        int value = Values[index++];
        if(kind == 'c') return ((char)(value & 255)).ToString();
        int width = match.Groups[2].Success && match.Groups[2].Length > 0 ? int.Parse(match.Groups[2].Value) : 0;
        int precision = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 6;
        bool finite = true;
        string text;
        if(kind == 'd') text = value.ToString(CultureInfo.InvariantCulture);
        else if(kind == 'x' || kind == 'X') text = ((uint)value).ToString(kind.ToString(), CultureInfo.InvariantCulture);
        else {
          float f = BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
          finite = !float.IsNaN(f) && !float.IsInfinity(f);
          text = float.IsNaN(f) ? "nan" : float.IsPositiveInfinity(f) ? "inf" : float.IsNegativeInfinity(f) ? "-inf"
            : ((double)f).ToString("F" + precision, CultureInfo.InvariantCulture);
          if(f == 0 && value < 0 && !text.StartsWith("-", StringComparison.Ordinal)) text = "-" + text;
        }
        if(match.Groups[1].Value == "-") return text.PadRight(width);
        if(match.Groups[1].Value == "0" && finite)
          return text.StartsWith("-", StringComparison.Ordinal)
            ? "-" + text.Substring(1).PadLeft(Math.Max(0, width - 1), '0') : text.PadLeft(width, '0');
        return text.PadLeft(width);
      });
      if(rendered.Length > 256) throw new InvalidOperationException("PrintF output too long");
      return rendered.Select(c => (byte)c).ToArray();
    }
  }
}
