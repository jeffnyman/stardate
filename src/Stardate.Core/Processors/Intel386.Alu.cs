using System.Numerics;

namespace Stardate.Core.Processors;

// Arithmetic, logic, shifts and the flags they set, for operands of 1, 2 or 4 bytes.
public sealed partial class Intel386
{
    internal static class Flag
    {
        public const uint Carry = 1 << 0;
        public const uint Parity = 1 << 2;
        public const uint Adjust = 1 << 4;
        public const uint Zero = 1 << 6;
        public const uint Sign = 1 << 7;
        public const uint Trap = 1 << 8;
        public const uint Interrupt = 1 << 9;
        public const uint Direction = 1 << 10;
        public const uint Overflow = 1 << 11;

        /// <summary>The six flags arithmetic sets.</summary>
        public const uint Arithmetic = Carry | Parity | Adjust | Zero | Sign | Overflow;
    }

    private static uint Mask(int size) => size == 4 ? 0xFFFF_FFFF : (1u << (8 * size)) - 1;

    private static uint SignBit(int size) => 1u << (8 * size - 1);

    private static int SignExtend(uint value, int size) => size switch
    {
        1 => (sbyte)value,
        2 => (short)value,
        _ => (int)value,
    };

    private bool Is(uint flag) => (_flags & flag) != 0;

    private void Set(uint flag, bool on) => _flags = on ? _flags | flag : _flags & ~flag;

    private uint CarryIn => _flags & Flag.Carry;

    /// <summary>
    /// Sets the six arithmetic flags. Zero, sign and parity come from the result; parity looks at
    /// its low byte only, set when that byte has an even number of 1 bits.
    /// </summary>
    private void SetArithmetic(uint result, int size, bool carry, bool overflow, bool adjust)
    {
        uint flags = _flags & ~Flag.Arithmetic;
        if (carry)
        {
            flags |= Flag.Carry;
        }

        if (overflow)
        {
            flags |= Flag.Overflow;
        }

        if (adjust)
        {
            flags |= Flag.Adjust;
        }

        _flags = flags | ZeroSignParity(result, size);
    }

    private static uint ZeroSignParity(uint result, int size)
    {
        uint flags = 0;
        if ((result & Mask(size)) == 0)
        {
            flags |= Flag.Zero;
        }

        if ((result & SignBit(size)) != 0)
        {
            flags |= Flag.Sign;
        }

        if ((BitOperations.PopCount(result & 0xFF) & 1) == 0)
        {
            flags |= Flag.Parity;
        }

        return flags;
    }

    private void SetZeroSignParity(uint result, int size) =>
        _flags = (_flags & ~(Flag.Zero | Flag.Sign | Flag.Parity)) | ZeroSignParity(result, size);

    // ---- add, subtract, logic ----------------------------------------------------------------

    private uint Add(uint a, uint b, uint carry, int size)
    {
        uint mask = Mask(size);
        ulong sum = (ulong)a + b + carry;
        uint result = (uint)sum & mask;
        SetArithmetic(result, size,
            carry: sum > mask,
            overflow: ((a ^ result) & (b ^ result) & SignBit(size)) != 0,
            adjust: ((a ^ b ^ result) & 0x10) != 0);
        return result;
    }

    private uint Subtract(uint a, uint b, uint borrow, int size)
    {
        uint result = (a - b - borrow) & Mask(size);
        SetArithmetic(result, size,
            carry: (ulong)b + borrow > a,
            overflow: ((a ^ b) & (a ^ result) & SignBit(size)) != 0,
            adjust: ((a ^ b ^ result) & 0x10) != 0);
        return result;
    }

    /// <summary>AND, OR, XOR and TEST: carry and overflow clear, the rest from the result.</summary>
    private uint Logic(uint result, int size)
    {
        SetArithmetic(result, size, carry: false, overflow: false, adjust: false);
        return result;
    }

    /// <summary>
    /// The eight operations of opcodes 00h to 3Fh and of group 1 (80h to 83h), in their encoding's
    /// order: ADD, OR, ADC, SBB, AND, SUB, XOR, CMP. CMP's result is only for its flags.
    /// </summary>
    private uint Alu(int operation, uint a, uint b, int size) => operation switch
    {
        0 => Add(a, b, 0, size),
        1 => Logic(a | b, size),
        2 => Add(a, b, CarryIn, size),
        3 => Subtract(a, b, CarryIn, size),
        4 => Logic(a & b, size),
        5 => Subtract(a, b, 0, size),
        6 => Logic(a ^ b, size),
        _ => Subtract(a, b, 0, size),
    };

    /// <summary>INC and DEC change every arithmetic flag but carry.</summary>
    private uint Increment(uint value, int size)
    {
        uint carry = _flags & Flag.Carry;
        uint result = Add(value, 1, 0, size);
        _flags = (_flags & ~Flag.Carry) | carry;
        return result;
    }

    private uint Decrement(uint value, int size)
    {
        uint carry = _flags & Flag.Carry;
        uint result = Subtract(value, 1, 0, size);
        _flags = (_flags & ~Flag.Carry) | carry;
        return result;
    }

    // ---- conditions --------------------------------------------------------------------------

    /// <summary>
    /// The sixteen conditions of Jcc, SETcc and their kin, in their encoding's order: O, NO, B,
    /// AE, E, NE, BE, A, S, NS, P, NP, L, GE, LE, G.
    /// </summary>
    private bool Condition(int code)
    {
        bool result = (code >> 1) switch
        {
            0 => Is(Flag.Overflow),
            1 => Is(Flag.Carry),
            2 => Is(Flag.Zero),
            3 => Is(Flag.Carry) || Is(Flag.Zero),
            4 => Is(Flag.Sign),
            5 => Is(Flag.Parity),
            6 => Is(Flag.Sign) != Is(Flag.Overflow),
            _ => Is(Flag.Zero) || Is(Flag.Sign) != Is(Flag.Overflow),
        };

        // each odd condition is the opposite of the even one before it
        return (code & 1) == 0 ? result : !result;
    }

    // ---- shifts and rotates ------------------------------------------------------------------

    /// <summary>
    /// Group 2 (C0h, C1h, D0h to D3h): ROL, ROR, RCL, RCR, SHL, SHR, SAL (the same as SHL), SAR.
    /// The 386 masks the count to five bits. A count of zero changes nothing, flags included.
    /// </summary>
    private uint Shift(int operation, uint value, int count, int size)
    {
        count &= 0x1F;
        if (count == 0)
        {
            return value;
        }

        int bits = 8 * size;
        uint mask = Mask(size), sign = SignBit(size);
        uint result;
        bool carry;

        switch (operation)
        {
            case 0: // ROL
            {
                int n = count % bits;
                result = n == 0 ? value : ((value << n) | (value >> (bits - n))) & mask;
                carry = (result & 1) != 0;
                Set(Flag.Carry, carry);
                Set(Flag.Overflow, ((result & sign) != 0) != carry);
                return result;
            }

            case 1: // ROR
            {
                int n = count % bits;
                result = n == 0 ? value : ((value >> n) | (value << (bits - n))) & mask;
                Set(Flag.Carry, (result & sign) != 0);
                Set(Flag.Overflow, (((result << 1) ^ result) & sign) != 0);
                return result;
            }

            case 2: // RCL: the carry flag is a ninth (17th, 33rd) bit of the rotation
            {
                int n = count % (bits + 1);
                result = value;
                carry = Is(Flag.Carry);
                for (int i = 0; i < n; i++)
                {
                    bool outBit = (result & sign) != 0;
                    result = ((result << 1) | (carry ? 1u : 0)) & mask;
                    carry = outBit;
                }

                Set(Flag.Carry, carry);
                Set(Flag.Overflow, ((result & sign) != 0) != carry);
                return result;
            }

            case 3: // RCR
            {
                int n = count % (bits + 1);
                result = value;
                carry = Is(Flag.Carry);
                for (int i = 0; i < n; i++)
                {
                    bool outBit = (result & 1) != 0;
                    result = (result >> 1) | (carry ? sign : 0);
                    carry = outBit;
                }

                // Intel defines overflow only for a count of 1, as the top bit before the rotation
                // XOR carry. What the 386 computes, for every count, is the XOR of the result's
                // top two bits, which comes to the same thing for a count of 1
                Set(Flag.Carry, carry);
                Set(Flag.Overflow, (((result << 1) ^ result) & sign) != 0);
                return result;
            }

            case 4: // SHL
            case 6: // SAL
                carry = ShiftedOut(count, bits) is int fromBit && ((value >> fromBit) & 1) != 0;
                result = (uint)((ulong)value << count) & mask;
                SetArithmetic(result, size, carry, overflow: ((result & sign) != 0) != carry, adjust: false);
                return result;

            case 5: // SHR
                carry = ShiftedOut(count, bits) is int && ((value >> (Math.Min(count, bits) - 1)) & 1) != 0;
                result = (uint)((ulong)value >> count);

                // overflow is the top bit of the value for a count of 1, and clear for any other
                SetArithmetic(result, size, carry, overflow: count == 1 && (value & sign) != 0, adjust: false);
                return result;

            default: // SAR
            {
                long signed = SignExtend(value, size);
                int n = Math.Min(count, bits);
                carry = ((signed >> (n - 1)) & 1) != 0;
                result = (uint)(signed >> n) & mask;
                SetArithmetic(result, size, carry, overflow: false, adjust: false);
                return result;
            }
        }
    }

    /// <summary>
    /// For SHL, the bit of the value that a shift by the count leaves in carry, or null when none
    /// does and carry is cleared. A count up to the operand's width shifts out a bit of the value.
    /// Past that the bits shifted out are zeros, except for byte shifts by 16 and 24, which the
    /// 386 treats as shifts by 8 as far as carry is concerned (measured on the chip; Intel only
    /// calls carry undefined there).
    /// </summary>
    private static int? ShiftedOut(int count, int bits) =>
        count <= bits ? bits - count
        : bits == 8 && count is 16 or 24 ? 0
        : null;

    /// <summary>
    /// SHLD and SHRD: shift the destination, filling the vacated bits from a second register.
    /// </summary>
    /// <remarks>
    /// A 16-bit shift by more than 16 is undefined in Intel's manuals. The 386 runs it as a 32-bit
    /// shift with the source repeated: SHLD shifts destination:source:source left, SHRD shifts
    /// source:source:destination right. Overflow follows SHL's rule for SHLD (the result's top bit
    /// XOR carry) and the result's top two bits for SHRD, at every count.
    /// </remarks>
    private uint DoubleShift(bool left, uint destination, uint source, int count, int size)
    {
        count &= 0x1F;
        if (count == 0)
        {
            return destination;
        }

        int bits = 8 * size;
        uint mask = Mask(size), sign = SignBit(size);
        uint result;
        bool carry;

        // the bits the shift runs over, the destination's end first
        ulong pattern = size == 2
            ? left
                ? ((ulong)destination << 32) | ((ulong)source << 16) | source
                : ((ulong)source << 32) | ((ulong)source << 16) | destination
            : left
                ? ((ulong)destination << 32) | source
                : ((ulong)source << 32) | destination;
        int width = size == 2 ? 48 : 64;

        bool overflow;
        if (left)
        {
            result = (uint)((pattern << count) >> (width - bits)) & mask;
            carry = ((pattern >> (width - count)) & 1) != 0;
            overflow = ((result & sign) != 0) != carry;
        }
        else
        {
            result = (uint)(pattern >> count) & mask;
            carry = ((pattern >> (count - 1)) & 1) != 0;
            overflow = (((result << 1) ^ result) & sign) != 0;
        }

        SetArithmetic(result, size, carry, overflow, adjust: false);
        return result;
    }

    // ---- multiply and divide -----------------------------------------------------------------

    /// <summary>MUL: AL, AX or EAX times the operand into AX, DX:AX or EDX:EAX.</summary>
    private void Multiply(uint value, int size)
    {
        ulong product = (ulong)GetReg(EAX, size) * value;
        StoreDouble(product, size);
        bool high = (product >> (8 * size)) != 0;
        SetMultiplyFlags(high, (uint)product, size);
    }

    /// <summary>IMUL with one operand: the signed form of MUL.</summary>
    private void SignedMultiply(uint value, int size)
    {
        long product = (long)SignExtend(GetReg(EAX, size), size) * SignExtend(value, size);
        StoreDouble((ulong)product, size);
        bool overflow = product != SignExtend((uint)product & Mask(size), size);
        SetMultiplyFlags(overflow, (uint)product, size);
    }

    /// <summary>IMUL with two or three operands: a signed product truncated to the operand size.</summary>
    private uint SignedMultiplyTruncated(uint a, uint b, int size)
    {
        long product = (long)SignExtend(a, size) * SignExtend(b, size);
        uint result = (uint)product & Mask(size);
        SetMultiplyFlags(product != SignExtend(result, size), result, size);
        return result;
    }

    /// <summary>Carry and overflow tell whether the product overflowed; the others are undefined.</summary>
    private void SetMultiplyFlags(bool overflow, uint low, int size)
    {
        SetArithmetic(low, size, carry: overflow, overflow: overflow, adjust: false);
    }

    private void StoreDouble(ulong value, int size)
    {
        if (size == 1)
        {
            SetReg(EAX, 2, (uint)value);
        }
        else
        {
            SetReg(EAX, size, (uint)value);
            SetReg(EDX, size, (uint)(value >> (8 * size)));
        }
    }

    private ulong LoadDouble(int size) => size == 1
        ? GetReg(EAX, 2)
        : ((ulong)GetReg(EDX, size) << (8 * size)) | GetReg(EAX, size);

    /// <summary>
    /// DIV: AX, DX:AX or EDX:EAX divided by the operand, quotient into AL, AX or EAX, remainder
    /// into AH, DX or EDX. Dividing by zero, or a quotient too big for its register, is a divide
    /// error.
    /// </summary>
    private void Divide(uint divisor, int size)
    {
        if (divisor == 0)
        {
            throw Fault(Vector.DivideError);
        }

        ulong dividend = LoadDouble(size);
        ulong quotient = dividend / divisor;
        if (quotient > Mask(size))
        {
            throw Fault(Vector.DivideError);
        }

        StoreQuotient((uint)quotient, (uint)(dividend % divisor), size);
    }

    /// <summary>IDIV: the signed form, the quotient rounded toward zero.</summary>
    private void SignedDivide(uint divisor, int size)
    {
        long d = SignExtend(divisor, size);
        if (d == 0)
        {
            throw Fault(Vector.DivideError);
        }

        int bits = 8 * size;
        Int128 dividend = size == 4 ? (long)LoadDouble(size) : SignExtend((uint)LoadDouble(size), size * 2);
        Int128 quotient = dividend / d;
        Int128 remainder = dividend % d;
        long min = -(1L << (bits - 1)), max = (1L << (bits - 1)) - 1;
        if (quotient < min || quotient > max)
        {
            throw Fault(Vector.DivideError);
        }

        StoreQuotient((uint)(long)quotient, (uint)(long)remainder, size);
    }

    private void StoreQuotient(uint quotient, uint remainder, int size)
    {
        if (size == 1)
        {
            SetReg(EAX, 2, (quotient & 0xFF) | ((remainder & 0xFF) << 8));
        }
        else
        {
            SetReg(EAX, size, quotient);
            SetReg(EDX, size, remainder);
        }
    }

    // ---- decimal adjustment ------------------------------------------------------------------

    /// <summary>DAA: adjusts AL after adding two packed BCD bytes.</summary>
    private void DecimalAdjustAdd()
    {
        uint al = GetReg(EAX, 1), oldAl = al;
        bool oldCarry = Is(Flag.Carry), carry = false, adjust = false;
        if ((al & 0x0F) > 9 || Is(Flag.Adjust))
        {
            carry = oldCarry || al + 6 > 0xFF;
            al = (al + 6) & 0xFF;
            adjust = true;
        }

        if (oldAl > 0x99 || oldCarry)
        {
            al = (al + 0x60) & 0xFF;
            carry = true;
        }

        SetReg(EAX, 1, al);
        SetArithmetic(al, 1, carry, overflow: false, adjust);
    }

    /// <summary>DAS: adjusts AL after subtracting two packed BCD bytes.</summary>
    private void DecimalAdjustSubtract()
    {
        uint al = GetReg(EAX, 1), oldAl = al;
        bool oldCarry = Is(Flag.Carry), carry = false, adjust = false;
        if ((al & 0x0F) > 9 || Is(Flag.Adjust))
        {
            carry = oldCarry || al < 6;
            al = (al - 6) & 0xFF;
            adjust = true;
        }

        if (oldAl > 0x99 || oldCarry)
        {
            al = (al - 0x60) & 0xFF;
            carry = true;
        }

        SetReg(EAX, 1, al);
        SetArithmetic(al, 1, carry, overflow: false, adjust);
    }

    /// <summary>
    /// AAA and AAS: adjust AL, and carry into AH, after adding or subtracting unpacked BCD. The
    /// 386 adds (or subtracts) 106h to all of AX, so when AL's adjustment itself carries, AH moves
    /// by two. Older manuals describe adding 6 to AL and 1 to AH, which the chip doesn't do.
    /// </summary>
    private void AsciiAdjust(bool subtract)
    {
        uint ax = GetReg(EAX, 2);
        bool adjust = (ax & 0x0F) > 9 || Is(Flag.Adjust);
        if (adjust)
        {
            ax = subtract ? ax - 0x106 : ax + 0x106;
        }

        SetReg(EAX, 2, ax & 0xFF0F);
        Set(Flag.Carry, adjust);
        Set(Flag.Adjust, adjust);
    }

    /// <summary>AAM: splits AL into two unpacked BCD digits, AH = AL / base and AL = AL mod base.</summary>
    private void AsciiAdjustMultiply(uint numberBase)
    {
        if (numberBase == 0)
        {
            throw Fault(Vector.DivideError);
        }

        uint al = GetReg(EAX, 1);
        uint result = al % numberBase;
        SetReg(EAX, 2, result | ((al / numberBase) << 8));
        SetArithmetic(result, 1, carry: false, overflow: false, adjust: false);
    }

    /// <summary>AAD: joins two unpacked BCD digits, AL = AH * base + AL and AH = 0.</summary>
    private void AsciiAdjustDivide(uint numberBase)
    {
        uint ax = GetReg(EAX, 2);
        uint result = ((ax & 0xFF) + ((ax >> 8) * numberBase)) & 0xFF;
        SetReg(EAX, 2, result);
        SetArithmetic(result, 1, carry: false, overflow: false, adjust: false);
    }
}
