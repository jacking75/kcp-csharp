using System;
using System.Buffers;

namespace KcpProject;

/// <summary>
/// KCP 세그먼트(24바이트 헤더 + 페이로드).
/// </summary>
/// <remarks>
/// <para>
/// 페이로드는 <see cref="ArrayPool{T}.Shared"/> 에서 빌린 배열이다. 대여 배열은 요청보다 클 수 있으므로
/// <b>유효 길이는 반드시 <see cref="Len"/> 으로 관리</b>하며 <c>Data.Length</c> 를 길이로 쓰지 않는다.
/// </para>
/// <para>
/// <see cref="Reset"/> 은 멱등이다(<see cref="Array.Empty{T}"/> 센티널). 이중 반환은 풀을 오염시키므로 반드시 지킨다.
/// </para>
/// </remarks>
internal sealed class KcpSegment
{
    internal uint conv;
    internal uint cmd;
    internal uint frg;
    internal uint wnd;
    internal uint ts;
    internal uint sn;
    internal uint una;
    internal uint rto;
    internal uint xmit;
    internal uint resendts;
    internal uint fastack;
    internal uint acked;

    /// <summary>ArrayPool 에서 빌린 페이로드 버퍼. 비어 있으면 <see cref="Array.Empty{T}"/>.</summary>
    internal byte[] Data = Array.Empty<byte>();

    /// <summary>페이로드 유효 길이.</summary>
    internal int Len;

    /// <summary>유효 페이로드에 대한 읽기 전용 뷰.</summary>
    internal ReadOnlySpan<byte> Payload => Data.AsSpan(0, Len);

    /// <summary>남은 쓰기 가능 용량(스트림 모드에서 뒤에 이어 붙일 때 쓴다).</summary>
    internal int WritableBytes => Data.Length - Len;

    /// <summary>지정한 용량의 버퍼를 빌린다. 이미 빌린 버퍼가 있으면 먼저 반환한다.</summary>
    /// <param name="capacity">필요한 최소 용량.</param>
    internal void Rent(int capacity)
    {
        ReturnBuffer();
        if (capacity > 0)
        {
            // 민감 데이터가 없으므로 clearArray: false (기본값) 로 빌려 성능을 취한다.
            Data = ArrayPool<byte>.Shared.Rent(capacity);
        }
        Len = 0;
    }

    /// <summary>페이로드 뒤에 바이트를 이어 붙인다.</summary>
    /// <param name="source">원본.</param>
    internal void Append(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0) return;
        if (source.Length > WritableBytes)
            throw new InvalidOperationException($"세그먼트 용량 초과: 필요 {source.Length}, 남은 {WritableBytes}");

        source.CopyTo(Data.AsSpan(Len));
        Len += source.Length;
    }

    /// <summary>헤더 24바이트를 기록한다.</summary>
    /// <param name="destination">대상(24바이트 이상).</param>
    /// <returns>기록한 바이트 수.</returns>
    internal int Encode(Span<byte> destination)
        => KcpCodec.WriteHeader(destination, conv, (byte)cmd, (byte)frg, (ushort)wnd, ts, sn, una, (uint)Len);

    /// <summary>필드를 초기화하고 빌린 버퍼를 풀에 반환한다. 여러 번 불러도 안전하다.</summary>
    internal void Reset()
    {
        conv = 0;
        cmd = 0;
        frg = 0;
        wnd = 0;
        ts = 0;
        sn = 0;
        una = 0;
        rto = 0;
        xmit = 0;
        resendts = 0;
        fastack = 0;
        acked = 0;

        ReturnBuffer();
    }

    private void ReturnBuffer()
    {
        if (Data.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(Data);
            Data = Array.Empty<byte>();   // 이중 반환 방지 센티널
        }
        Len = 0;
    }
}
