///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace X13.Periphery {
  /// <summary>
  /// Read/write access to a Remote unit's own EEPROM configuration (antenna
  /// matrix + switching delays), and to the Mainboard-local console/Ext mask.
  /// Port of SW/Server/core/src/cfg_remote.c — same command names, same result
  /// strings, so the same web_loc/SetupRemote.html works against either backend.
  ///
  /// ---- Write model: set, then read back --------------------------------
  /// A write is not acknowledged in any usable way, so it is not waited on:
  /// the value is sent, the same code is read back, and the two are compared.
  /// Reasons, all read out of the firmware in this repo rather than assumed:
  ///
  ///   - There is no single ack. A successful Set is answered with
  ///     BUS_EV_EEPROM_READY for codes 20/21/22 and 64-95, but with *silence*
  ///     for PWR_CAL (48): extRxEvent returns BUS_EV_IDLE and bus.c only emits
  ///     an event when the result differs from IDLE. Waiting for an ack there
  ///     reported a timeout on a write that had actually succeeded.
  ///   - EEPROM_READY exists only for the old configurator's benefit — both
  ///     firmware ends say exactly that in comments. It is therefore ignored
  ///     here: it is still emitted, and it lands inside the verify read's wait
  ///     window, so treating it as the read's answer would report success
  ///     without ever comparing anything.
  ///   - Reading back immediately is valid: parser_set_dly / parser_set_ant_cfg
  ///     / adc_set_cal all update RAM first and commit EEPROM afterwards, and
  ///     the Get handlers read that same RAM.
  ///
  /// A rejected parameter arrives promptly as an 'F' frame, so a bad value
  /// costs one round trip rather than a timeout.
  ///
  /// ---- One attempt, no retries -----------------------------------------
  /// ConfigN4's 3 x 700ms retry loop was historical, not measured. A read gets
  /// one wait; a lost frame surfaces as "timeout" and the operator decides.
  ///
  /// ---- Threading --------------------------------------------------------
  /// Nothing here blocks. Requests are queued from whatever thread delivers
  /// the topic change and are driven forward from AntSwPl.Tick(), which the
  /// host calls about every 15ms (Programm.cs). That matters: Programm.Tick
  /// walks the plugins one after another, so blocking here for the better part
  /// of a second would stall every other plugin and all topic processing with
  /// it. Same reason the C version's blocking design could not simply be
  /// transliterated.
  /// </summary>
  internal class CfgRemote {
    private const int TIMEOUT_MS = 700;
    private const byte ADDR_MASTER = 32;
    // Address map, for reading the numbers that arrive in CFGGET/CFGSET:
    //   1-8    consoles     (bus 0)
    //   17-24  Remote units (bus 1; matches ConfigN4's historical 17+nAddr)
    //   32     the Mainboard itself
    // Callers send the composite address; see Addr() for what is accepted.

    private const ushort EX_RXTX_DELAY = 20;
    private const ushort EX_TXRX_DELAY = 21;
    private const ushort EX_ACCEL_TMR = 22;
    private const ushort EX_PWR_CAL = 48;
    private const ushort EX_GET_SVC = 97;
    private const ushort EX_GET_ID = 2;         // read-only, 2 elements, ASCII 'R','B'
    private const ushort EX_GET_SW = 3;         // read-only, 2 elements, major/minor
    // Read-only SDIO diagnostics, 8 elements = 4 x (hi,lo) — same order as the
    // antenna-matrix codes below. Firmware sent these lo-first before SW_ID
    // 2.4; the codes were unused, so bus_ext_event.c was changed rather than
    // keeping two conventions. Needs SW_ID 2.4 or later (reported by code 3).
    private const ushort EX_GET_SDSTAT = 104;
    private const ushort EX_GET_SDOUTC = 105;
    private const ushort EX_SDIO_DIAG = 40;     // Set half of 105: {channel 0-31, mode 0-3}
    // Console-unit settings (FW/Console/Source/BUS/bus_ext_event.c), 1 element
    // each, addressed at the console's own bus-0 address. These exist only on a
    // Console; a Remote has none of them and vice versa.
    private const ushort EX_PTT_MODE = 32;
    private const ushort EX_PTT_INV = 33;
    private const ushort EX_CURR_LOC = 34;
    private const ushort EX_CURR_EXT = 35;
    private const ushort EX_PTT_WD = 36;
    private const ushort EX_WK_MODE = 37;
    private const ushort EX_ANT_CFG_RX = 64;    // .. 79
    private const ushort EX_ANT_CFG_TX = 80;    // .. 95
    private const ushort EX_CONSOLE_EXT = 152;

    private const byte EV_OFFLINE = 0x29;       // BUS_EV_ERROR_OFFLINE

    /// <summary>Elements this code carries on the wire.</summary>
    private static int ExpectedLen(ushort code) {
      if(code == EX_RXTX_DELAY || code == EX_TXRX_DELAY) return 3;
      if(code == EX_ACCEL_TMR) return 1;
      if(code == EX_PWR_CAL) return 7;
      if(code == EX_GET_SVC) return 5;
      if(code == EX_GET_ID || code == EX_GET_SW) return 2;
      if(code == EX_GET_SDSTAT || code == EX_GET_SDOUTC) return 8;
      if(code == EX_SDIO_DIAG) return 2;        // {channel 0-31, mode 0-3}
      // Console-unit settings, all single values. Keyed by code alone, so a
      // Remote asked for one of these is let through and answers F_FAIL.
      if(code >= EX_PTT_MODE && code <= EX_WK_MODE) return 1;
      if(code == EX_CONSOLE_EXT) return 1;
      // 2, not 1: the wire splits each 16-bit matrix word into hi/lo as two
      // separate elements (bus_ext_event.c, both directions), not one raw u16.
      if(code >= EX_ANT_CFG_RX && code <= EX_ANT_CFG_RX + 15) return 2;
      if(code >= EX_ANT_CFG_TX && code <= EX_ANT_CFG_TX + 15) return 2;
      return 0;   // unrecognized
    }

    /// <summary>
    /// False for codes that cannot answer a Get of themselves, so there is
    /// nothing to verify against. Only EX_SDIO_DIAG (40): it is the Set half of
    /// EX_GET_SDOUTC (105) and has no read handler of its own, so the generic
    /// read-back would draw an empty response and report a mismatch on every
    /// write. The Diagnostics page polls 105, so the confirmation is on screen
    /// anyway. Mirrors verify_after_set() in SW/Server/core/src/cfg_remote.c.
    /// </summary>
    private static bool VerifyAfterSet(ushort code) {
      return code != EX_SDIO_DIAG;
    }

    private class Request {
      public string session;      // which WS session asked; routes the reply
      public string line;         // the original command line, echoed back
      public bool isSet;
      public byte addr;
      public ushort code;
      public ushort[] data;       // for a Set: what we wrote, to compare against
      public string error;        // set when the line could not even be parsed
    }

    private enum St { Idle, WaitRead }

    private readonly Queue<Request> _q = new Queue<Request>();
    private readonly object _qLock = new object();
    private readonly Transport _tr;
    private readonly Action<string, string> _reply;   // (session, "<line>\t<result>")

    private St _st = St.Idle;
    private Request _cur;
    private DateTime _to;

    public CfgRemote(Transport tr, Action<string, string> reply) {
      _tr = tr;
      _reply = reply;
    }

    /// <summary>Queues one command line. Returns immediately; never blocks.</summary>
    public void Submit(string session, string line) {
      var rq = Parse(session, line);
      lock(_qLock) {
        _q.Enqueue(rq);
      }
    }

    private static Request Parse(string session, string line) {
      var rq = new Request { session = session, line = line };
      var sa = (line ?? string.Empty).Split('\t');
      try {
        switch(sa[0]) {
        case "CFGGET":      // CFGGET\t<addr>\t<code>
          if(sa.Length != 3) { rq.error = "bad cmd"; break; }
          rq.addr = Addr(int.Parse(sa[1], CultureInfo.InvariantCulture));
          rq.code = ushort.Parse(sa[2], CultureInfo.InvariantCulture);
          break;
        case "CFGSET":      // CFGSET\t<addr>\t<code>\t<v0>[,<v1>...]
          if(sa.Length != 4) { rq.error = "bad cmd"; break; }
          rq.isSet = true;
          rq.addr = Addr(int.Parse(sa[1], CultureInfo.InvariantCulture));
          rq.code = ushort.Parse(sa[2], CultureInfo.InvariantCulture);
          rq.data = ParseCsv(sa[3]);
          break;
        default:
          rq.error = "unknown";
          break;
        }
      }
      catch(Exception) {
        rq.error = "bad args";
      }
      if(rq.error == null) {
        int need = ExpectedLen(rq.code);
        if(need == 0 || rq.addr == 0) {
          rq.error = "bad_args";
        } else if(rq.isSet && (rq.data == null || rq.data.Length != need)) {
          rq.error = "bad_args";
        }
        // Deliberately no read-only blocklist here (nor in the C version):
        // the node rejects a Set to a read-only code itself, and the verify
        // read then reports "mismatch". A second, hand-maintained list would
        // only be somewhere for the two to drift apart.
      }
      return rq;
    }

    /// <summary>
    /// Validates the composite bus address a CFGGET/CFGSET carries — the same
    /// rule as asw_cfg_addr_valid() in SW/Server/core/src/cfg_remote.c:
    /// the Mainboard's own address, or bus 0/1 with device 1-8. Returns 0 for
    /// anything else, which Parse turns into "bad_args".
    ///
    /// 0x21-0x28 are rejected on purpose: they address the same consoles, but
    /// as the Mainboard's event-injection path, which is meaningful for E
    /// events and never for config.
    /// </summary>
    private static byte Addr(int addr) {
      if(addr < 0 || addr > 0xFF) return 0;
      if(addr == ADDR_MASTER) return ADDR_MASTER;
      int bus = addr >> 4, dev = addr & 0x0F;
      if(bus <= 1 && dev >= 1 && dev <= 8) return (byte)addr;
      return 0;
    }

    private static ushort[] ParseCsv(string s) {
      var parts = s.Split(',');
      var v = new ushort[parts.Length];
      for(int i = 0; i < parts.Length; i++) {
        v[i] = ushort.Parse(parts[i], CultureInfo.InvariantCulture);
      }
      return v;
    }

    /// <summary>
    /// Call for every frame the transport produced, alongside whatever else
    /// consumes it. Cheap no-op unless a read is currently outstanding.
    /// </summary>
    public void OnCommand(Command cmd) {
      if(_st != St.WaitRead || _cur == null || cmd.addr != _cur.addr) {
        return;
      }
      if(cmd.code == CommandCode.ExEvent && cmd.param == _cur.code) {
        Complete(BuildOk(cmd.data));
      } else if(cmd.code == CommandCode.Event && (cmd.param & 0xFF) == EV_OFFLINE) {
        Complete("ERR\toffline");
      } else if(cmd.code == CommandCode.Fail) {
        Complete("ERR\tfail");
      }
      // Anything else — notably BUS_EV_EEPROM_READY, which the firmware still
      // emits after a successful Set and which therefore lands right here
      // during the verify read — is ignored on purpose. See the class remarks.
    }

    /// <summary>
    /// Drives the state machine. Call from AntSwPl.Tick().
    ///
    /// Deliberately NOT gated on the startup bulk poll having finished. That
    /// gate looks prudent and is actively harmful: the poll re-sends the same
    /// index forever when a node does not answer, so gating on it would leave
    /// the configurator permanently dead in exactly the situation where you
    /// want to open it. The two can share the bus safely — the poll talks to
    /// addr 32 with codes 128-136, config talks to addr 17-24 (or addr 32 with
    /// code 152), and both this matcher and AntSwPl.GetResponse discriminate
    /// on address plus code, so neither can swallow the other's answer.
    /// </summary>
    public void Tick() {
      if(_st == St.WaitRead) {
        if(DateTime.Now > _to) {
          Complete("ERR\ttimeout");
        }
        return;
      }
      Request rq = null;
      lock(_qLock) {
        if(_q.Count > 0) rq = _q.Dequeue();
      }
      if(rq == null) {
        return;
      }
      if(rq.error != null) {
        _reply(rq.session, rq.line + "\tERR\t" + rq.error);
        return;
      }
      _cur = rq;
      if(rq.isSet) {
        // Unacknowledged write, immediately followed by the read that confirms
        // it. Both are queued on the transport, which paces them out.
        _tr.Write(new Command(CommandCode.Set, rq.addr, rq.code, rq.data));
        if(!VerifyAfterSet(rq.code)) {
          Complete("OK");   // sent; see VerifyAfterSet
          return;
        }
      }
      _tr.Write(new Command(CommandCode.Get, rq.addr, rq.code));
      _to = DateTime.Now.AddMilliseconds(TIMEOUT_MS);
      _st = St.WaitRead;
    }

    /// <summary>Formats a successful read, or compares it for a write.</summary>
    private string BuildOk(ushort[] data) {
      if(_cur.isSet) {
        if(data == null || data.Length != _cur.data.Length) {
          return "ERR\tmismatch";
        }
        for(int i = 0; i < data.Length; i++) {
          if(data[i] != _cur.data[i]) return "ERR\tmismatch";
        }
        return "OK";
      }
      if(data == null || data.Length == 0) {
        return "OK";
      }
      var sb = new StringBuilder("OK\t");
      for(int i = 0; i < data.Length; i++) {
        if(i > 0) sb.Append(',');
        sb.Append(data[i].ToString(CultureInfo.InvariantCulture));
      }
      return sb.ToString();
    }

    private void Complete(string result) {
      var rq = _cur;
      _cur = null;
      _st = St.Idle;
      if(rq != null) {
        _reply(rq.session, rq.line + "\t" + result);
      }
    }
  }
}
