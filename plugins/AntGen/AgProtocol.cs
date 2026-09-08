///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace X13.AntGen {
  /// <summary>Wire format of the 4O3A Antenna Genius, firmware 3.x.
  ///
  /// NOT the protocol described at github.com/4o3a/genius-api-docs — that wiki
  /// documents firmware 4.x and its "C1|port get 1" text commands get no answer
  /// at all from a 3.1.5 device. Everything here was read off the wire from an
  /// AG 8x2+ V2 running 3.1.5 (f160ae0, Jul 9 2021) talking to the vendor's own
  /// software; see the notes in AntGenPl for what each table means.
  ///
  ///     !LLLL!CCCCCC!payload
  ///      ^    ^      ^ fields separated by ';'
  ///      |    24-bit code, high byte 00=request 08=response 10=announce
  ///      LLLL = 7 + payload length, hex. The 7 is the code (6 chars) plus the
  ///      '!' after it, so a whole frame is 6 + LLLL bytes.
  ///
  /// Frames arrive back to back with nothing between them — "…000b00!!0007!…" —
  /// so the length is the only thing that separates them. Scanning for '!'
  /// would desynchronise on the first payload that contains one.
  ///
  /// Pure and dependency-free on purpose: it is verified by replaying captured
  /// TCP streams through it, which needs no Enviriot and no device.</summary>
  internal static class AgProto {
    /* Codes, without the flags byte. */
    public const int CODE_SW_VERSION = 0x0a80;   /* 4;3.1.5;f160ae0;Jul  9 2021;10:38:49 */
    public const int CODE_HW_INFO    = 0x0b00;   /* 4;4;1.6;1709156;1                    */
    public const int CODE_NAME       = 0x0c00;   /* 1;0;AntennaGenius                    */
    public const int CODE_PORT_CNT   = 0xdd00;   /* 0;2 - port count, read once          */
    public const int CODE_BAND       = 0x2900;   /* id;name;freq_start;freq_stop         */
    public const int CODE_ANTENNA    = 0xce00;   /* id;name;portmask;bandmask            */
    public const int CODE_OUTPUT     = 0xd300;   /* id;out;name;…                        */
    public const int CODE_RADIO      = 0xd800;   /* id;ip;port;n  — the FLEX to follow   */
    public const int CODE_STATUS     = 0xc880;   /* polled; 13 fields, see AgStatus      */
    public const int CODE_SET_ANT    = 0xcf80;   /* port;antenna — see SetAntenna() below */
    /// <summary>The device's answer to a rejected SetAntenna — observed on
    /// hardware (not in either capture: the vendor UI evidently never lets an
    /// operator ask for an antenna outside the current band's mask, so this
    /// never came up before a plain field write did). Confirmed once:
    /// requesting antenna 1 (a 10m monobander) while the port sat on 20m drew
    /// "08cf81" instead of "08cf80", same payload shape. Treated as a normal,
    /// distinct outcome of the exchange, not a framing error — see
    /// AgClient.EnforceAntenna(). Only this one value has been observed; a
    /// third variant would still (correctly) be treated as a protocol error.</summary>
    public const int CODE_SET_ANT_NAK = 0xcf81;
    public const int CODE_ANNOUNCE   = 0x0280;   /* UDP broadcast from the device        */

    public const int FLAG_RESPONSE = 0x08;
    public const int FLAG_ANNOUNCE = 0x10;

    /// <summary>Codes this program is allowed to transmit.
    ///
    /// A whitelist rather than "we simply never call the setter": step one of
    /// the integration is read-only, and the difference between code that
    /// cannot send a command and code that merely does not is the difference
    /// between a guarantee and an intention. CODE_SET_ANT is deliberately
    /// absent — adding it is a conscious act, not an oversight.</summary>
    private static readonly int[] Sendable = {
      CODE_SW_VERSION, CODE_HW_INFO, CODE_NAME, CODE_PORT_CNT, CODE_BAND,
      CODE_ANTENNA, CODE_OUTPUT, CODE_RADIO, CODE_STATUS
    };

    public static bool MaySend(int code) {
      for(int i = 0; i < Sendable.Length; i++) {
        if(Sendable[i] == code) {
          return true;
        }
      }
      return false;
    }

    /// <summary>What the vendor's own client asks for, once, on connect - in
    /// its exact order, copied from the capture rather than invented.
    ///
    /// The counts are not derived from anything the device reports: 16 bands,
    /// 8 antennas, 7 outputs, 2 radios is simply what the vendor asked an
    /// 8x2+ V2 for. Extrapolating instead ("it has 8 outputs, ask for 8")
    /// risks a request the firmware answers with something we have never seen,
    /// on a device wired to a transmitter. Replaying a known-good sequence
    /// costs nothing and can be checked byte for byte against the capture.</summary>
    public static List<KeyValuePair<int, string>> Discovery() {
      var l = new List<KeyValuePair<int, string>>();
      l.Add(new KeyValuePair<int, string>(CODE_HW_INFO, ""));
      l.Add(new KeyValuePair<int, string>(CODE_SW_VERSION, ""));
      for(int i = 0; i <= 15; i++) {
        l.Add(new KeyValuePair<int, string>(CODE_BAND, i.ToString(CultureInfo.InvariantCulture)));
      }
      l.Add(new KeyValuePair<int, string>(CODE_PORT_CNT, ""));
      l.Add(new KeyValuePair<int, string>(CODE_NAME, ""));
      for(int i = 1; i <= 8; i++) {
        l.Add(new KeyValuePair<int, string>(CODE_ANTENNA, i.ToString(CultureInfo.InvariantCulture)));
      }
      for(int i = 1; i <= 7; i++) {
        l.Add(new KeyValuePair<int, string>(CODE_OUTPUT, i.ToString(CultureInfo.InvariantCulture)));
      }
      for(int i = 1; i <= 2; i++) {
        l.Add(new KeyValuePair<int, string>(CODE_RADIO, i.ToString(CultureInfo.InvariantCulture)));
      }
      return l;
    }

    /// <summary>Build a request. Throws for a code outside the whitelist:
    /// a caller that reaches for a write has made a mistake worth stopping on,
    /// not a condition worth returning.</summary>
    public static byte[] Request(int code, string payload) {
      if(!MaySend(code)) {
        throw new InvalidOperationException(
          string.Format("AntGen: code {0:x4} is not sendable in read-only mode", code));
      }
      if(payload == null) {
        payload = string.Empty;
      }
      var s = string.Format(CultureInfo.InvariantCulture, "!{0:x4}!{1:x6}!{2}",
                            7 + payload.Length, code, payload);
      return Encoding.ASCII.GetBytes(s);
    }

    /// <summary>The one write this protocol makes: select an antenna on a
    /// port. Deliberately NOT reachable through Request()/the Sendable
    /// whitelist — that whitelist exists so ordinary code cannot send a
    /// command by accident; this is the one explicit, named door through
    /// which a write happens at all, and its presence in a call stack is
    /// itself the signal that write mode is switched on for this station.
    ///
    /// Confirmed against the capture: the vendor's own "1;7" request drew a
    /// response of the same op with the same payload echoed back
    /// (!000a!00cf80!1;7 -> !000a!08cf80!1;7).</summary>
    public static byte[] SetAntenna(int port, int antennaId) {
      if(port != 1 && port != 2) {
        throw new ArgumentOutOfRangeException("port", port, "AG has ports 1 and 2 only");
      }
      if(antennaId < 1 || antennaId > 8) {
        throw new ArgumentOutOfRangeException("antennaId", antennaId, "AG has antennas 1-8 only");
      }
      string payload = port.ToString(CultureInfo.InvariantCulture) + ";"
                      + antennaId.ToString(CultureInfo.InvariantCulture);
      var s = string.Format(CultureInfo.InvariantCulture, "!{0:x4}!{1:x6}!{2}",
                            7 + payload.Length, CODE_SET_ANT, payload);
      return Encoding.ASCII.GetBytes(s);
    }
  }

  internal struct AgFrame {
    public int Code;          /* 24-bit, flags included */
    public string Payload;

    public int Op { get { return Code & 0xFFFF; } }
    public bool IsResponse { get { return ((Code >> 16) & AgProto.FLAG_RESPONSE) != 0; } }
    public bool IsAnnounce { get { return ((Code >> 16) & AgProto.FLAG_ANNOUNCE) != 0; } }

    public string[] Fields { get { return Payload.Length == 0 ? new string[0] : Payload.Split(';'); } }

    public override string ToString() {
      return string.Format("{0:x6}:{1}", Code, Payload);
    }
  }

  /// <summary>Incremental frame reader: bytes in, whole frames out.</summary>
  internal class AgFrameReader {
    /* Long enough for any table row seen (the longest was 94 bytes) with room
     * to spare; a "frame" past this is a desynchronised stream, not data. */
    private const int MAX_FRAME = 1024;
    private readonly StringBuilder _buf = new StringBuilder();

    /// <summary>Junk that was not a frame header and not a preamble.</summary>
    public int Resyncs { get; private set; }
    /// <summary>Zero bytes skipped ahead of frames. Every response carries
    /// exactly six of them (the first of a session, eight); requests carry
    /// none. What the field holds is unknown - it was zero in every one of the
    /// 1304 responses captured - so it is skipped rather than interpreted.</summary>
    public int PreambleBytes { get; private set; }
    /// <summary>Hex of the first preamble seen carrying anything but zeros, or
    /// null. This is the interesting one: the day it stops being null is the
    /// day those six bytes turn out to mean something, and silently discarding
    /// them would hide it.</summary>
    public string OddPreamble { get; private set; }

    public void Reset() {
      _buf.Length = 0;
    }

    public List<AgFrame> Feed(byte[] data, int count) {
      var outp = new List<AgFrame>();
      for(int i = 0; i < count; i++) {
        _buf.Append((char)data[i]);
      }
      while(true) {
        string s = _buf.ToString();
        /* Skip the response preamble: a run of zero bytes before the header. */
        int z = 0;
        while(z < s.Length && s[z] == '\0') {
          z++;
        }
        if(z > 0) {
          PreambleBytes += z;
          _buf.Remove(0, z);
          continue;
        }
        int start = s.IndexOf('!');
        if(start < 0) {
          /* Nothing usable at all. Keep only a tail: a '!' cannot be further
           * than one frame away, so anything older is noise. */
          if(s.Length > MAX_FRAME) {
            _buf.Length = 0;
            Resyncs++;
          }
          return outp;
        }
        if(start > 0) {
          /* Non-zero junk before the marker. If it sits where a preamble
           * belongs, record it once — that is the field finally saying
           * something. Either way it is dropped and counted. */
          if(OddPreamble == null && start <= 8) {
            var sb = new StringBuilder();
            for(int k = 0; k < start; k++) {
              sb.AppendFormat("{0:x2}", (int)s[k]);
            }
            OddPreamble = sb.ToString();
          }
          _buf.Remove(0, start);
          Resyncs++;
          continue;
        }
        if(s.Length < 13) {              /* '!' + 4 + '!' + 6 + '!' */
          return outp;
        }
        int len, code;
        if(s[5] != '!' || s[12] != '!'
           || !int.TryParse(s.Substring(1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out len)
           || !int.TryParse(s.Substring(6, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
           || len < 7 || len > MAX_FRAME) {
          _buf.Remove(0, 1);             /* not a header here; try the next '!' */
          Resyncs++;
          continue;
        }
        int total = 6 + len;             /* '!'+4+'!' then len covers code+'!'+payload */
        if(s.Length < total) {
          return outp;                   /* frame split across TCP segments */
        }
        outp.Add(new AgFrame { Code = code, Payload = s.Substring(13, len - 7) });
        _buf.Remove(0, total);
      }
    }
  }

  /// <summary>One radio port's slice of the status message.</summary>
  internal struct AgPort {
    public int Band;          /* index into the band table; 0 = none        */
    public int Antenna;       /* index into the antenna table; 0 = none     */
    public bool Tx;           /* transmitting                               */
    public string Raw;        /* all six fields, unparsed                   */

    public bool Active { get { return Band != 0 || Antenna != 0; } }
  }

  /// <summary>The polled status: 13 fields, six per port and one trailing.
  ///
  /// Field meanings were established by driving the vendor software and
  /// watching what moved:
  ///   [1] band     — followed the radio through 10m, 15m and back to idle
  ///   [3] antenna  — followed an explicit antenna selection
  ///   [5] tx       — the only field that changed on key-down
  /// Fields 0, 2, 4 and the trailing one never left 0 in either capture, so
  /// they are published raw rather than guessed at. Field 4 in particular is
  /// NOT a prerequisite for transmitting: a key-down was captured with the
  /// antenna set and field 4 at zero.
  ///
  /// Switching the radio's antenna output moved the whole six-field block from
  /// the first half to the second, which is why the active port is derived
  /// rather than configured.</summary>
  internal struct AgStatus {
    public AgPort A;
    public AgPort B;
    public string Tail;

    public int ActivePort {
      get { return A.Active ? 1 : (B.Active ? 2 : 0); }
    }

    public static bool TryParse(string payload, out AgStatus st) {
      st = new AgStatus();
      if(payload == null) {
        return false;
      }
      var f = payload.Split(';');
      if(f.Length < 13) {
        return false;
      }
      st.A = Port(f, 0);
      st.B = Port(f, 6);
      st.Tail = f[12];
      return true;
    }

    private static AgPort Port(string[] f, int o) {
      var p = new AgPort();
      p.Band = Num(f[o + 1]);
      p.Antenna = Num(f[o + 3]);
      p.Tx = Num(f[o + 5]) != 0;
      p.Raw = string.Join(";", new[] { f[o], f[o + 1], f[o + 2], f[o + 3], f[o + 4], f[o + 5] });
      return p;
    }

    private static int Num(string s) {
      int v;
      return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }
  }

  /// <summary>A row of the device's band table: id;name;freq_start;freq_stop,
  /// frequencies in Hz. Ids run 0..15 with 0 = "None"; the id is the bit
  /// position used by an antenna's band mask.</summary>
  internal struct AgBand {
    public int Id;
    public string Name;
    public long From, To;

    public static bool TryParse(string payload, out AgBand b) {
      b = new AgBand();
      var f = payload.Split(';');
      if(f.Length < 4) {
        return false;
      }
      int id; long lo, hi;
      if(!int.TryParse(f[0], out id)) {
        return false;
      }
      long.TryParse(f[2], out lo);
      long.TryParse(f[3], out hi);
      b.Id = id; b.Name = f[1]; b.From = lo; b.To = hi;
      return true;
    }
  }

  /// <summary>A row of the antenna table: id;name;portmask;bandmask, both masks
  /// hexadecimal. The band mask has bit N set for band id N, so the station's
  /// log-periodic reads 03e0 = bands 5,6,7,8,9 = 20/17/15/12/10m. The id is
  /// also the physical port number on the back of the unit.</summary>
  internal struct AgAntenna {
    public int Id;
    public string Name;
    public int PortMask;
    public int BandMask;

    public bool CoversBand(int bandId) {
      return bandId > 0 && (BandMask & (1 << bandId)) != 0;
    }

    public static bool TryParse(string payload, out AgAntenna a) {
      a = new AgAntenna();
      var f = payload.Split(';');
      if(f.Length < 4) {
        return false;
      }
      int id, pm, bm;
      if(!int.TryParse(f[0], out id)) {
        return false;
      }
      int.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pm);
      int.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bm);
      a.Id = id; a.Name = f[1]; a.PortMask = pm; a.BandMask = bm;
      return true;
    }
  }
}
