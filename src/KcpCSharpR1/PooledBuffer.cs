using System;
using System.Buffers;

namespace KcpCSharpR1;

/// <summary>
/// <see cref="ArrayPool{T}"/> 기반의 성장 가능한 읽기/쓰기 버퍼.
/// </summary>
/// <remarks>
/// P3-3 에서 <c>ByteBuffer</c> 를 코어에서 걷어내면서, 수신 조립 버퍼가 필요한 자리(레거시
/// <see cref="UDPSession"/>, 전송 계층)를 위해 최소 기능만 남긴 대체물이다.
/// 스레드 안전하지 않다(소유 스레드 규약).
/// </remarks>
internal sealed class PooledBuffer : IDisposable
{
    private byte[] buffer;

    /// <summary>읽기 위치.</summary>
    public int ReaderIndex;

    /// <summary>쓰기 위치.</summary>
    public int WriterIndex;

    /// <summary>버퍼를 만든다.</summary>
    /// <param name="capacity">초기 용량.</param>
    public PooledBuffer(int capacity)
    {
        buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, capacity));
    }

    /// <summary>내부 배열(대여 배열이므로 길이가 요청보다 클 수 있다).</summary>
    public byte[] Raw => buffer;

    /// <summary>현재 용량.</summary>
    public int Capacity => buffer.Length;

    /// <summary>읽을 수 있는 바이트 수.</summary>
    public int ReadableBytes => WriterIndex - ReaderIndex;

    /// <summary>쓸 수 있는 바이트 수.</summary>
    public int WritableBytes => buffer.Length - WriterIndex;

    /// <summary>읽을 수 있는 구간에 대한 뷰.</summary>
    public ReadOnlySpan<byte> Readable => buffer.AsSpan(ReaderIndex, ReadableBytes);

    /// <summary>최소 <paramref name="minBytes"/> 만큼 쓸 수 있도록 보장한다(트림 → 확장 순).</summary>
    /// <param name="minBytes">필요한 쓰기 공간.</param>
    public void EnsureWritable(int minBytes)
    {
        if (WritableBytes >= minBytes)
            return;

        // 앞쪽에 이미 읽은 공간이 충분하면 당겨서 재사용한다.
        if (ReaderIndex >= minBytes)
        {
            Trim();
            if (WritableBytes >= minBytes)
                return;
        }

        var required = ReadableBytes + minBytes;
        var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(required, buffer.Length * 2));
        Buffer.BlockCopy(buffer, ReaderIndex, newBuffer, 0, ReadableBytes);
        WriterIndex = ReadableBytes;
        ReaderIndex = 0;
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = newBuffer;
    }

    /// <summary>이미 읽은 앞부분을 버리고 남은 데이터를 앞으로 당긴다.</summary>
    public void Trim()
    {
        if (ReaderIndex == 0) return;
        var readable = ReadableBytes;
        if (readable > 0)
            Buffer.BlockCopy(buffer, ReaderIndex, buffer, 0, readable);
        ReaderIndex = 0;
        WriterIndex = readable;
    }

    /// <summary>데이터를 뒤에 기록한다.</summary>
    /// <param name="source">원본.</param>
    public void Write(ReadOnlySpan<byte> source)
    {
        EnsureWritable(source.Length);
        source.CopyTo(buffer.AsSpan(WriterIndex));
        WriterIndex += source.Length;
    }

    /// <summary>읽기/쓰기 위치를 되돌린다(내용은 버린다).</summary>
    public void Clear()
    {
        ReaderIndex = 0;
        WriterIndex = 0;
    }

    /// <summary>빌린 배열을 풀에 돌려준다. 여러 번 불러도 안전하다.</summary>
    public void Dispose()
    {
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = Array.Empty<byte>();
        }
        ReaderIndex = 0;
        WriterIndex = 0;
    }
}
