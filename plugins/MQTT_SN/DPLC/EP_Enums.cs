using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.DevicePLC {
  internal enum EP_InstCode : byte {
    NOP = 0x00,

    DUP = 0x02,
    DROP,
    NIP,
    SWAP,
    OVER,
    ROT,

    NOT = 0x08,
    AND,
    OR,
    XOR,
    LSL,
    LSR,
    ASR,

    ADD = 0x10,
    SUB,
    MUL,
    DIV,
    MOD,
    INC,
    DEC,
    NEG,

    // Float-команды (float - 32 бита IEEE-754 в той же ячейке стека, что и int).
    // Исходников прошивки здесь нет, поэтому допущения компилятора: все команды однобайтовые,
    // порядок операндов как у целых (a b FSUB -> a-b, a b FCGT -> a>b), сравнения и FIS*/FCZE
    // кладут 0/1, FROUND/FTRUNC дают float.
    FADD = 0x18,
    FSUB,
    FMUL,
    FDIV,
    FNEG,
    FABS,
    FMIN,
    FMAX,

    CEQ = 0x20,
    CNE,
    CGT,
    CGE,
    CLT,
    CLE,
    CZE,

    FCEQ = 0x28,
    FCNE,
    FCGT,
    FCGE,
    FCLT,
    FCLE,
    FCZE,
    FISFINITE,

    S32TOF = 0x30,
    U32TOF,
    FTOS32,
    FTOU32,
    FROUND,
    FTRUNC,
    FISNAN,
    FISINF,

    LDI_0 = 0x38,
    LDI_S1,
    LDI_S2,
    LDI_S4,
    LDI_U1,
    LDI_U2,
    LDI_1,
    LDI_M1,


    LD_P0 = 0x40,
    LD_P1,
    LD_P2,
    LD_P3,
    LD_P4,
    LD_P5,
    LD_P6,
    LD_P7,
    LD_P8,
    LD_P9,
    LD_PA,
    LD_PB,
    LD_PC,
    LD_PD,
    LD_PE,
    LD_PF,

    LD_L0 = 0x50,
    LD_L1,
    LD_L2,
    LD_L3,
    LD_L4,
    LD_L5,
    LD_L6,
    LD_L7,
    LD_L8,
    LD_L9,
    LD_LA,
    LD_LB,
    LD_LC,
    LD_LD,
    LD_LE,
    LD_LF,

    ST_P0 = 0x60,
    ST_P1,
    ST_P2,
    ST_P3,
    ST_P4,
    ST_P5,
    ST_P6,
    ST_P7,
    ST_P8,
    ST_P9,
    ST_PA,
    ST_PB,
    ST_PC,
    ST_PD,
    ST_PE,
    ST_PF,

    ST_L0 = 0x70,
    ST_L1,
    ST_L2,
    ST_L3,
    ST_L4,
    ST_L5,
    ST_L6,
    ST_L7,
    ST_L8,
    ST_L9,
    ST_LA,
    ST_LB,
    ST_LC,
    ST_LD,
    ST_LE,
    ST_LF,

    LDM_B1_S = 0x80,
    LDM_S1_S,
    LDM_S2_S,
    LDM_S4_S,
    LDM_U1_S,
    LDM_U2_S,
    LDI_MIN = 0x87,

    LDM_B1_CS8 = 0x88,
    LDM_S1_CS8,
    LDM_S2_CS8,
    LDM_S4_CS8,
    LDM_U1_CS8,
    LDM_U2_CS8,

    LDM_B1_CS16 = 0x90,
    LDM_S1_CS16,
    LDM_S2_CS16,
    LDM_S4_CS16,
    LDM_U1_CS16,
    LDM_U2_CS16,

    LDM_B1_C16 = 0x98,
    LDM_S1_C16,
    LDM_S2_C16,
    LDM_S4_C16,
    LDM_U1_C16,
    LDM_U2_C16,

    STM_B1_S = 0xA0,
    STM_S1_S,
    STM_S2_S,
    STM_S4_S,

    STM_B1_CS8 = 0xA8,
    STM_S1_CS8,
    STM_S2_CS8,
    STM_S4_CS8,

    STM_B1_CS16 = 0xB0,
    STM_S1_CS16,
    STM_S2_CS16,
    STM_S4_CS16,

    STM_B1_C16 = 0xB8,
    STM_S1_C16,
    STM_S2_C16,
    STM_S4_C16,

    IN = 0xC0,
    OUT = 0xC1,

    API=0xC6,

    LPM_S1 = 0xC9,
    LPM_S2,
    LPM_S4,
    LPM_U1,
    LPM_U2,

    SJMP = 0xF0,
    JZ = 0xF1,
    JNZ = 0xF2,
    JMP = 0xF3,
    SCALL = 0xF4,
    CALL = 0xF7,

    LABEL = 0xF8,

    CHECK_IDX = 0xFD,
    TEST_EQ = 0xFE,
    RET = 0xFF,
  }
  internal enum EP_Type {
    NONE,
    BOOL,
    UINT8,
    SINT8,
    UINT16,
    SINT16,
    SINT32,
    FLOAT,
    U8_CARR,
    I8_CARR,
    U16_CARR,
    I16_CARR,
    I32_CARR,
    PropB1,
    PropU1,
    PropS1,
    PropU2,
    PropS2,
    PropS4,
    PropF4,
    REFERENCE,
    INPUT,
    OUTPUT,
    PARAMETER,
    LOCAL,
    API,
    FUNCTION,
    CLASS,
    CONST,      // константа времени компиляции, значение - в Addr; в память не попадает
  }
  /// <summary>Тип значения на стеке VM: выбирает целую или float-команду и нужное приведение.</summary>
  internal enum EP_ValType {
    Int,
    UInt,    // только результат UInt32(x) и >>>: отличается от Int лишь приведением к float
    Float,
  }
  /// <summary>Узел, для которого компилируется программа.</summary>
  /// <remarks>Байткод и протокол загрузки у них общие, а набор API-функций разный: на EspNode пины и порты
  /// настраивает сама программа (ConfigPin), есть вторая шина TWI и нет UartInit.</remarks>
  internal enum EP_Target {
    X13,        // объект управления pa0
    EspNode,    // pa1
  }
}
