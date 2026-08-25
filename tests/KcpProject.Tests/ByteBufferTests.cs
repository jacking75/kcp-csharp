using System;
using KcpProject;

namespace KcpProject.Tests;

/// <summary>P2-2: ByteBuffer 회귀 테스트(B-06, B-07, B-09, B-14).</summary>
public class ByteBufferTests
{
    /// <summary>
    /// B-06 회귀: <c>ByteBuffer(byte[])</c> 가 writeIndex 를 length+1 로 잡아
    /// 쓰레기 1바이트가 유효 데이터로 붙던 문제.
    /// </summary>
    [Fact]
    public void Allocate_바이트배열은_길이만큼만_읽힌다()
    {
        var src = new byte[] { 1, 2, 3, 4 };
        var buf = ByteBuffer.Allocate(src);

        Assert.Equal(src.Length, buf.ReadableBytes);
        Assert.Equal(src, buf.ToArray());
    }

    /// <summary>B-06 회귀: Clone 경유에서도 길이가 늘어나지 않아야 한다.</summary>
    [Fact]
    public void Clone은_읽을수있는_바이트수를_유지한다()
    {
        var buf = ByteBuffer.Allocate(16);
        buf.WriteBytes(new byte[] { 9, 8, 7 });

        var clone = (ByteBuffer)buf.Clone();

        Assert.Equal(buf.ReadableBytes, clone.ReadableBytes);
        Assert.Equal(buf.ToArray(), clone.ToArray());
    }

    /// <summary>
    /// B-07 회귀: 풀에서 꺼낸 버퍼가 요청 용량보다 작으면 즉시 확장해서 돌려줘야 한다.
    /// </summary>
    [Fact]
    public void 풀에서_꺼낸_버퍼는_요청_용량_이상이다()
    {
        // 작은 버퍼를 풀에 넣어 둔다.
        var small = ByteBuffer.Allocate(8, fromPool: true);
        small.Dispose();

        var big = ByteBuffer.Allocate(4096, fromPool: true);
        try
        {
            Assert.True(big.Capacity >= 4096, $"용량 {big.Capacity} 는 요청 4096 보다 작다.");

            // 확장된 버퍼에 실제로 기록할 수 있어야 한다.
            var payload = new byte[4096];
            payload[4095] = 0xAB;
            big.WriteBytes(payload);
            Assert.Equal(4096, big.ReadableBytes);
        }
        finally
        {
            big.Dispose();
        }
    }

    /// <summary>
    /// B-09 회귀: 한도를 넘는 문자열은 길이 접두사를 오염시키는 대신 예외로 막는다.
    /// </summary>
    [Fact]
    public void WriteUTF8String_한도초과는_예외()
    {
        var buf = ByteBuffer.Allocate(1024);
        var tooLong = new string('a', 300); // UTF-8 300바이트 > byte.MaxValue

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buf.WriteUTF8String(tooLong, ByteBuffer.DataType.BYTE));
    }

    /// <summary>B-09 회귀: 길이 접두사는 항상 실제로 기록한 바이트 수여야 한다.</summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("한글 문자열 테스트")]
    [InlineData("")]
    public void WriteUTF8String_길이접두사가_실제_바이트수와_일치한다(string text)
    {
        var buf = ByteBuffer.Allocate(1024);
        buf.WriteUTF8String(text, ByteBuffer.DataType.SHORT);

        var expectedBytes = System.Text.Encoding.UTF8.GetByteCount(text);
        Assert.Equal(sizeof(short) + expectedBytes, buf.ReadableBytes);
        Assert.Equal(text, buf.ReadUTF8String(ByteBuffer.DataType.SHORT));
        Assert.Equal(0, buf.ReadableBytes);
    }

    /// <summary>EnsureWritableBytes 는 성장 경로와 트림 경로 모두에서 데이터를 보존해야 한다.</summary>
    [Fact]
    public void EnsureWritableBytes_성장경로에서_데이터가_보존된다()
    {
        var buf = ByteBuffer.Allocate(16);
        buf.WriteBytes(new byte[] { 1, 2, 3, 4 });

        buf.EnsureWritableBytes(4096);

        Assert.True(buf.WritableBytes >= 4096);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf.ToArray());
    }

    [Fact]
    public void EnsureWritableBytes_트림경로에서_읽지않은_데이터가_보존된다()
    {
        var buf = ByteBuffer.Allocate(32);
        buf.WriteBytes(new byte[16]);
        buf.WriteBytes(new byte[] { 7, 7, 7, 7 });
        buf.ReadBytes(16); // 앞 16바이트 소비 → ReaderIndex = 16

        buf.EnsureWritableBytes(12); // ReaderIndex(16) >= 12 → 트림 경로

        Assert.Equal(new byte[] { 7, 7, 7, 7 }, buf.ToArray());
    }

    /// <summary>B-14 회귀: IDisposable 을 구현해 using 으로 쓸 수 있어야 한다.</summary>
    [Fact]
    public void ByteBuffer는_IDisposable이다()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ByteBuffer)));

        using (var buf = ByteBuffer.Allocate(32, fromPool: true))
        {
            buf.WriteBytes(new byte[] { 1 });
        }
    }
}
