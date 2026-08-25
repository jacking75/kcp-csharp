using System;

namespace KcpProject.Transport.Fec;

/// <summary>
/// GF(2^8) 유한체 연산. 리드-솔로몬 부호의 바탕이다.
/// </summary>
/// <remarks>
/// 기약 다항식은 <c>x^8 + x^4 + x^3 + x^2 + 1</c>(0x11D), 생성원은 2 다.
/// klauspost/reedsolomon 및 kcp-go 와 같은 설정이라 산출 바이트가 호환된다.
/// </remarks>
internal static class GaloisField
{
    private const int Polynomial = 0x11D;

    private static readonly byte[] ExpTable = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    static GaloisField()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;

            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= Polynomial;
        }

        // 로그 덧셈 결과가 255 를 넘어도 그대로 인덱싱할 수 있게 두 배로 펼쳐 둔다.
        for (var i = 255; i < 512; i++)
            ExpTable[i] = ExpTable[i - 255];
    }

    /// <summary>GF(2^8) 곱셈.</summary>
    /// <param name="a">피연산자.</param>
    /// <param name="b">피연산자.</param>
    /// <returns>곱.</returns>
    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return ExpTable[LogTable[a] + LogTable[b]];
    }

    /// <summary>GF(2^8) 나눗셈.</summary>
    /// <param name="a">피제수.</param>
    /// <param name="b">제수(0 이면 예외).</param>
    /// <returns>몫.</returns>
    public static byte Divide(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("GF(2^8) 에서 0 으로 나눌 수 없습니다.");
        if (a == 0) return 0;
        return ExpTable[LogTable[a] - LogTable[b] + 255];
    }

    /// <summary>거듭제곱 <c>a^n</c>.</summary>
    /// <param name="a">밑.</param>
    /// <param name="n">지수.</param>
    /// <returns>결과.</returns>
    public static byte Exp(byte a, int n)
    {
        if (n == 0) return 1;
        if (a == 0) return 0;

        var logResult = LogTable[a] * n % 255;
        return ExpTable[logResult];
    }

    /// <summary><c>destination ^= source * multiplier</c> (샤드 단위 연산).</summary>
    /// <param name="multiplier">계수.</param>
    /// <param name="source">원본 샤드.</param>
    /// <param name="destination">누적 대상.</param>
    public static void MultiplyAddInto(byte multiplier, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (multiplier == 0) return;

        var log = LogTable[multiplier];
        for (var i = 0; i < source.Length; i++)
        {
            var value = source[i];
            if (value != 0)
                destination[i] ^= ExpTable[log + LogTable[value]];
        }
    }

    /// <summary><c>destination = source * multiplier</c> (샤드 단위 연산).</summary>
    /// <param name="multiplier">계수.</param>
    /// <param name="source">원본 샤드.</param>
    /// <param name="destination">대상.</param>
    public static void MultiplyInto(byte multiplier, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (multiplier == 0)
        {
            destination.Slice(0, source.Length).Clear();
            return;
        }

        var log = LogTable[multiplier];
        for (var i = 0; i < source.Length; i++)
        {
            var value = source[i];
            destination[i] = value == 0 ? (byte)0 : ExpTable[log + LogTable[value]];
        }
    }
}
