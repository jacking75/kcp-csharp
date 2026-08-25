using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace KcpProject;

/// <summary>
/// KCP 와이어 포맷(리틀엔디언) 인코딩/디코딩. <see cref="BinaryPrimitives"/> 기반이라 임시 배열을 만들지 않는다.
/// </summary>
/// <remarks>
/// 공개 API 인 <c>Kcp.ikcp_encode*/decode*</c> 는 호환을 위해 남겨 두고, 내부는 전부 이 클래스를 쓴다(진단 PF-02).
/// </remarks>
internal static class KcpCodec
{
    /// <summary>부호 없는 8비트 정수를 기록한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteU8(Span<byte> p, byte v) => p[0] = v;

    /// <summary>부호 없는 8비트 정수를 읽는다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadU8(ReadOnlySpan<byte> p) => p[0];

    /// <summary>부호 없는 16비트 정수를 리틀엔디언으로 기록한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteU16(Span<byte> p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(p, v);

    /// <summary>부호 없는 16비트 정수를 리틀엔디언으로 읽는다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadU16(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p);

    /// <summary>부호 없는 32비트 정수를 리틀엔디언으로 기록한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteU32(Span<byte> p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(p, v);

    /// <summary>부호 없는 32비트 정수를 리틀엔디언으로 읽는다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadU32(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32LittleEndian(p);

    /// <summary>세그먼트 헤더 24바이트를 기록한다.</summary>
    /// <returns>기록한 바이트 수(항상 <see cref="Kcp.IKCP_OVERHEAD"/>).</returns>
    public static int WriteHeader(Span<byte> p, uint conv, byte cmd, byte frg, ushort wnd, uint ts, uint sn, uint una, uint len)
    {
        WriteU32(p, conv);
        WriteU8(p.Slice(4), cmd);
        WriteU8(p.Slice(5), frg);
        WriteU16(p.Slice(6), wnd);
        WriteU32(p.Slice(8), ts);
        WriteU32(p.Slice(12), sn);
        WriteU32(p.Slice(16), una);
        WriteU32(p.Slice(20), len);
        return Kcp.IKCP_OVERHEAD;
    }
}
