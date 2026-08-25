using System;
using KcpCSharpR1;

namespace KcpCSharpR1.Tests;

/// <summary>P3-3: ArrayPool 기반 수신 조립 버퍼(ByteBuffer 대체)의 경계 동작.</summary>
public class PooledBufferTests
{
    [Fact]
    public void 기록한_만큼만_읽힌다()
    {
        using var buf = new PooledBuffer(16);
        buf.Write(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(4, buf.ReadableBytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf.Readable.ToArray());
    }

    [Fact]
    public void EnsureWritable_성장경로에서_데이터가_보존된다()
    {
        using var buf = new PooledBuffer(16);
        buf.Write(new byte[] { 1, 2, 3, 4 });

        buf.EnsureWritable(64 * 1024);

        Assert.True(buf.WritableBytes >= 64 * 1024);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf.Readable.ToArray());
    }

    [Fact]
    public void EnsureWritable_트림경로에서_읽지않은_데이터가_보존된다()
    {
        using var buf = new PooledBuffer(32);
        var capacity = buf.Capacity;

        buf.Write(new byte[capacity - 4]);
        buf.Write(new byte[] { 7, 7, 7, 7 });
        buf.ReaderIndex += capacity - 4;              // 앞부분 소비 → 읽지 않은 4바이트만 남는다

        // 남은 쓰기 공간(0)보다 크지만 이미 읽은 공간(capacity-4)보다는 작은 요구 → 트림 경로
        buf.EnsureWritable(capacity - 8);

        Assert.Equal(capacity, buf.Capacity);        // 재할당 없이 트림으로 처리
        Assert.Equal(0, buf.ReaderIndex);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, buf.Readable.ToArray());
    }

    [Fact]
    public void 대여배열은_요청보다_클수있으므로_인덱스로_길이를_관리한다()
    {
        using var buf = new PooledBuffer(100);
        Assert.True(buf.Capacity >= 100);
        Assert.Equal(0, buf.ReadableBytes);

        buf.Write(new byte[] { 9 });
        Assert.Equal(1, buf.ReadableBytes);
    }

    [Fact]
    public void Dispose는_여러번_불러도_안전하다()
    {
        var buf = new PooledBuffer(64);
        buf.Write(new byte[] { 1, 2 });

        buf.Dispose();
        buf.Dispose();

        Assert.Equal(0, buf.ReadableBytes);
    }

    [Fact]
    public void Clear는_인덱스를_되돌린다()
    {
        using var buf = new PooledBuffer(64);
        buf.Write(new byte[] { 1, 2, 3 });
        buf.Clear();

        Assert.Equal(0, buf.ReadableBytes);
        Assert.Equal(0, buf.ReaderIndex);
        Assert.Equal(0, buf.WriterIndex);
    }
}
