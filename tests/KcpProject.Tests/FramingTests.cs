using System;
using KcpProject.Transport;

namespace KcpProject.Tests;

/// <summary>P4-1a: 프레이밍 코덱의 프레임별 왕복.</summary>
public class FramingTests
{
    [Fact]
    public void SYN_왕복()
    {
        var buffer = new byte[64];
        var len = Framing.WriteSyn(buffer, FrameType.Syn, 0xAABBCCDD, 0);

        Assert.Equal(Framing.SynSize, len);
        Assert.Equal(FrameType.Syn, Framing.PeekType(buffer));

        Assert.True(Framing.TryReadSyn(buffer.AsSpan(0, len), out var version, out var nonce, out var cookie));
        Assert.Equal(Framing.ProtocolVersion, version);
        Assert.Equal(0xAABBCCDDu, nonce);
        Assert.Equal(0u, cookie);
    }

    [Fact]
    public void SYN_COOKIE_왕복()
    {
        var buffer = new byte[64];
        var len = Framing.WriteSyn(buffer, FrameType.SynCookie, 1234, 0xFEEDFACE);

        Assert.Equal(FrameType.SynCookie, Framing.PeekType(buffer));
        Assert.True(Framing.TryReadSyn(buffer.AsSpan(0, len), out _, out var nonce, out var cookie));
        Assert.Equal(1234u, nonce);
        Assert.Equal(0xFEEDFACEu, cookie);
    }

    [Fact]
    public void SYN_ACK_왕복()
    {
        var buffer = new byte[64];
        var len = Framing.WriteSynAck(buffer, 42, 0x11112222);

        Assert.Equal(Framing.SynAckSize, len);
        Assert.Equal(FrameType.SynAck, Framing.PeekType(buffer));
        Assert.True(Framing.TryReadSynAck(buffer.AsSpan(0, len), out var conv, out var serverNonce));
        Assert.Equal(42u, conv);
        Assert.Equal(0x11112222u, serverNonce);
    }

    [Fact]
    public void KCP_프레임은_페이로드를_그대로_보존한다()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new byte[64];

        var offset = Framing.WriteKcpHeader(buffer);
        payload.CopyTo(buffer.AsSpan(offset));

        Assert.Equal(FrameType.Kcp, Framing.PeekType(buffer));
        Assert.True(Framing.TryReadKcp(buffer.AsSpan(0, offset + payload.Length), out var read));
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public void RAW_왕복()
    {
        var payload = new byte[] { 9, 8, 7 };
        var buffer = new byte[64];
        var len = Framing.WriteRaw(buffer, 0x1234, payload);

        Assert.Equal(Framing.RawHeaderSize + payload.Length, len);
        Assert.Equal(FrameType.Raw, Framing.PeekType(buffer));
        Assert.True(Framing.TryReadRaw(buffer.AsSpan(0, len), out var conv, out var read));
        Assert.Equal(0x1234u, conv);
        Assert.Equal(payload, read.ToArray());
    }

    [Theory]
    [InlineData(FrameType.Ping)]
    [InlineData(FrameType.Pong)]
    public void PING_PONG_왕복(FrameType type)
    {
        var buffer = new byte[64];
        var len = Framing.WritePing(buffer, type, 77, 123456);

        Assert.Equal(Framing.PingSize, len);
        Assert.Equal(type, Framing.PeekType(buffer));
        Assert.True(Framing.TryReadPing(buffer.AsSpan(0, len), out var conv, out var ts));
        Assert.Equal(77u, conv);
        Assert.Equal(123456u, ts);
    }

    [Fact]
    public void FIN_왕복()
    {
        var buffer = new byte[64];
        var len = Framing.WriteFin(buffer, 55, 3);

        Assert.Equal(Framing.FinSize, len);
        Assert.Equal(FrameType.Fin, Framing.PeekType(buffer));
        Assert.True(Framing.TryReadFin(buffer.AsSpan(0, len), out var conv, out var reason));
        Assert.Equal(55u, conv);
        Assert.Equal(3, reason);
    }

    [Fact]
    public void 잘린_프레임은_읽기에_실패한다()
    {
        var buffer = new byte[64];
        Framing.WriteSyn(buffer, FrameType.Syn, 1, 2);

        Assert.False(Framing.TryReadSyn(buffer.AsSpan(0, Framing.SynSize - 1), out _, out _, out _));
        Assert.False(Framing.TryReadSynAck(buffer.AsSpan(0, Framing.SynAckSize - 1), out _, out _));
        Assert.False(Framing.TryReadPing(buffer.AsSpan(0, Framing.PingSize - 1), out _, out _));
        Assert.False(Framing.TryReadFin(buffer.AsSpan(0, Framing.FinSize - 1), out _, out _));
        Assert.False(Framing.TryReadRaw(buffer.AsSpan(0, Framing.RawHeaderSize - 1), out _, out _));
        Assert.False(Framing.TryReadKcp(buffer.AsSpan(0, 1), out _));
    }

    [Fact]
    public void 알수없는_타입은_None이다()
    {
        Assert.Equal(FrameType.None, Framing.PeekType(Array.Empty<byte>()));
        Assert.Equal(FrameType.None, Framing.PeekType(new byte[] { 0x00 }));
        Assert.Equal(FrameType.None, Framing.PeekType(new byte[] { 0x09 }));
        Assert.Equal(FrameType.None, Framing.PeekType(new byte[] { 0xFF }));
    }

    /// <summary>프레임 타입 값은 문서·상호운용의 기준이므로 바뀌면 안 된다.</summary>
    [Fact]
    public void 프레임_타입_값이_명세와_일치한다()
    {
        Assert.Equal(0x01, (byte)FrameType.Syn);
        Assert.Equal(0x02, (byte)FrameType.SynCookie);
        Assert.Equal(0x03, (byte)FrameType.SynAck);
        Assert.Equal(0x04, (byte)FrameType.Kcp);
        Assert.Equal(0x05, (byte)FrameType.Raw);
        Assert.Equal(0x06, (byte)FrameType.Ping);
        Assert.Equal(0x07, (byte)FrameType.Pong);
        Assert.Equal(0x08, (byte)FrameType.Fin);
    }
}
