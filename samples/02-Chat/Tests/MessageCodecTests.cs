using System;
using Chat.Shared;

namespace Chat.Tests;

/// <summary>
/// P7-2 ①: 메시지 코덱 왕복 테스트.
/// </summary>
/// <remarks>
/// 프로토콜을 손으로 만들 때 가장 먼저 써야 하는 테스트다. 코덱이 틀리면 그 위의 모든 것이 틀린다.
/// </remarks>
public class MessageCodecTests
{
    private readonly byte[] buffer = new byte[ChatLimits.MaxMessageBytes];

    [Fact]
    public void SetNick_왕복()
    {
        var writer = new MessageWriter(buffer, MessageType.SetNick);
        writer.WriteString("헝배");
        var packet = writer.Finish();

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.Equal(MessageType.SetNick, reader.Type);
        Assert.True(reader.TryReadString(out var nick));
        Assert.Equal("헝배", nick);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Whisper_문자열_두개_왕복()
    {
        var writer = new MessageWriter(buffer, MessageType.Whisper);
        writer.WriteString("target");
        writer.WriteString("비밀 이야기");
        var packet = writer.Finish();

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.Equal(MessageType.Whisper, reader.Type);
        Assert.True(reader.TryReadString(out var target));
        Assert.True(reader.TryReadString(out var text));
        Assert.Equal("target", target);
        Assert.Equal("비밀 이야기", text);
    }

    [Fact]
    public void Welcome_가변길이_목록_왕복()
    {
        var names = new[] { "a", "bb", "한글닉" };

        var writer = new MessageWriter(buffer, MessageType.Welcome);
        writer.WriteString(ChatLimits.Motd);
        writer.WriteUInt16((ushort)names.Length);
        foreach (var name in names) writer.WriteString(name);
        var packet = writer.Finish();

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.True(reader.TryReadString(out var motd));
        Assert.Equal(ChatLimits.Motd, motd);
        Assert.True(reader.TryReadUInt16(out var count));
        Assert.Equal(names.Length, count);

        for (var i = 0; i < count; i++)
        {
            Assert.True(reader.TryReadString(out var name));
            Assert.Equal(names[i], name);
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Error_바이트와_문자열_왕복()
    {
        var writer = new MessageWriter(buffer, MessageType.Error);
        writer.WriteByte((byte)ChatError.NickTaken);
        writer.WriteString("이미 사용 중");
        var packet = writer.Finish();

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.True(reader.TryReadByte(out var code));
        Assert.Equal(ChatError.NickTaken, (ChatError)code);
        Assert.True(reader.TryReadString(out var message));
        Assert.Equal("이미 사용 중", message);
    }

    [Fact]
    public void 길이_접두사가_실제_길이와_일치한다()
    {
        var writer = new MessageWriter(buffer, MessageType.Say);
        writer.WriteString("hello");
        var packet = writer.Finish();

        // [u16 len][u8 type][u16 strlen][utf8 5바이트]
        var declared = buffer[0] | (buffer[1] << 8);
        Assert.Equal(packet.Length - 2, declared);
        Assert.Equal(2 + 1 + 2 + 5, packet.Length);
    }

    [Fact]
    public void 빈_문자열도_처리한다()
    {
        var writer = new MessageWriter(buffer, MessageType.Say);
        writer.WriteString(string.Empty);
        var packet = writer.Finish();

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.True(reader.TryReadString(out var text));
        Assert.Equal(string.Empty, text);
    }

    // ---- 여기서부터는 "네트워크는 거짓말을 한다" 를 전제로 한 방어 테스트 ----

    [Fact]
    public void 너무_짧은_패킷은_거부한다()
    {
        Assert.False(MessageReader.TryParse(Array.Empty<byte>(), out _));
        Assert.False(MessageReader.TryParse(new byte[] { 1 }, out _));
        Assert.False(MessageReader.TryParse(new byte[] { 1, 0 }, out _));
    }

    [Fact]
    public void 길이가_거짓이면_거부한다()
    {
        var writer = new MessageWriter(buffer, MessageType.Say);
        writer.WriteString("hi");
        var packet = writer.Finish().ToArray();

        // 실제보다 큰 길이를 적어 넣는다.
        packet[0] = 0xFF;
        packet[1] = 0x00;

        Assert.False(MessageReader.TryParse(packet, out _));
    }

    [Fact]
    public void 문자열_길이가_남은_바이트보다_크면_거부한다()
    {
        // len = 3 (타입 1 + strlen 필드 2). strlen 은 100 이라고 하지만 뒤에 아무것도 없다.
        var packet = new byte[] { 3, 0, (byte)MessageType.Say, 100, 0 };

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.False(reader.TryReadString(out _));
        Assert.False(reader.IsValid);
    }

    [Fact]
    public void 한도를_넘는_문자열은_거부한다()
    {
        var writer = new MessageWriter(buffer, MessageType.Say);
        writer.WriteString(new string('x', 200));
        var packet = writer.Finish();

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.False(reader.TryReadString(out _, maxBytes: 100));
    }

    [Fact]
    public void 실패한_뒤의_읽기는_계속_실패한다()
    {
        // len = 2 (타입 1 + 1바이트). 문자열 길이 필드(2바이트)를 채우기엔 모자라다.
        var packet = new byte[] { 2, 0, (byte)MessageType.Say, 0 };

        Assert.True(MessageReader.TryParse(packet, out var reader));
        Assert.False(reader.TryReadString(out _));   // 길이 필드가 모자란다
        Assert.False(reader.TryReadByte(out _));     // 한 번 깨지면 계속 false
        Assert.False(reader.IsValid);
    }

    [Fact]
    public void 무작위_쓰레기에_예외가_나지_않는다()
    {
        var rng = new Random(20260825);
        var junk = new byte[64];

        for (var i = 0; i < 1000; i++)
        {
            rng.NextBytes(junk);
            var length = rng.Next(0, junk.Length);

            if (MessageReader.TryParse(junk.AsSpan(0, length), out var reader))
            {
                // 해석에 성공했더라도 뒤의 읽기는 실패할 수 있을 뿐, 예외는 없어야 한다.
                reader.TryReadByte(out _);
                reader.TryReadUInt16(out _);
                reader.TryReadString(out _);
            }
        }
    }

    [Fact]
    public void 너무_긴_문자열을_쓰려_하면_예외()
    {
        // ref struct 는 람다로 넘길 수 없으므로 직접 잡는다.
        var caught = false;
        try
        {
            var big = new byte[128 * 1024];
            var writer = new MessageWriter(big, MessageType.Say);
            writer.WriteString(new string('가', 30_000));   // UTF-8 로 90,000바이트 > 65,535
        }
        catch (ArgumentException)
        {
            caught = true;
        }

        Assert.True(caught, "65535바이트를 넘는 문자열인데 예외가 나지 않았다.");
    }
}
