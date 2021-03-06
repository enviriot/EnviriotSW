﻿///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Periphery {
  public static class Crc16 {
    private const ushort polynomial = 0xA001;
    private static ushort[] table = new ushort[256];

    public static ushort UpdateCrc(ushort crc_in, byte[] buf) {
      for(int j = 0; j < buf.Length; ++j) {
        byte data = buf[j];
        for(int i = 0; i < 8; i++) {
          if((((crc_in & 0x8000) >> 8) ^ (data & 0x80))!=0)
            crc_in = (ushort)((crc_in << 1) ^ 0x8005);
          else
            crc_in = (ushort)(crc_in << 1);
          data <<= 1;
        }
      }
      return crc_in;
    }


    public static ushort ComputeChecksum(byte[] bytes) {
      ushort crc = 0xFFFF;
      for(int i = 0; i < bytes.Length; ++i) {
        byte index = (byte)(crc ^ bytes[i]);
        crc = (ushort)((crc >> 8) ^ table[index]);
      }
      return crc;
    }
    public static ushort UpdateChecksum(ushort crc, byte b) {
      byte index = (byte)(crc ^ b);
      crc = (ushort)((crc >> 8) ^ table[index]);
      return crc;
    }

    static Crc16() {
      ushort value;
      ushort temp;
      for(ushort i = 0; i < table.Length; ++i) {
        value = 0;
        temp = i;
        for(byte j = 0; j < 8; ++j) {
          if(((value ^ temp) & 0x0001) != 0) {
            value = (ushort)((value >> 1) ^ polynomial);
          } else {
            value >>= 1;
          }
          temp >>= 1;
        }
        table[i] = value;
      }
    }
  }
}
