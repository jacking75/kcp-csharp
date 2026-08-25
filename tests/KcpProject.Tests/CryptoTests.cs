using System;
using KcpProject.Transport;

namespace KcpProject.Tests;

/// <summary>P5-1: AES-256-GCM 패킷 암호화(왕복, 변조 탐지, 리플레이 방지).</summary>
public class CryptoTests
{
    private static byte[] NewKey(byte seed)
    {
        var key = new byte[AesGcmPacketCipher.KeySize];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static (AesGcmPacketCipher Sender, AesGcmPacketCipher Receiver) NewPair(uint conv = 42)
    {
        var key = NewKey(1);
        // 클라이언트가 보내고 서버가 받는 방향.
        var sender = new AesGcmPacketCipher(key, conv, serverDirection: false);
        var receiver = new AesGcmPacketCipher(key, conv, serverDirection: true);
        return (sender, receiver);
    }

    [Fact]
    public void 암호화_복호화_왕복()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var buffer = new byte[plaintext.Length + AesGcmPacketCipher.OverheadBytes];

            var written = sender.Encrypt(plaintext, buffer);
            Assert.Equal(buffer.Length, written);

            // 암호문은 평문과 달라야 한다.
            Assert.NotEqual(plaintext, buffer.AsSpan(8, plaintext.Length).ToArray());

            var output = new byte[plaintext.Length];
            Assert.True(receiver.TryDecrypt(buffer.AsSpan(0, written), output, out var length));
            Assert.Equal(plaintext.Length, length);
            Assert.Equal(plaintext, output);
        }
    }

    [Fact]
    public void 빈_페이로드도_처리한다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var buffer = new byte[AesGcmPacketCipher.OverheadBytes];
            var written = sender.Encrypt(ReadOnlySpan<byte>.Empty, buffer);
            Assert.Equal(AesGcmPacketCipher.OverheadBytes, written);

            Assert.True(receiver.TryDecrypt(buffer.AsSpan(0, written), Array.Empty<byte>(), out var length));
            Assert.Equal(0, length);
        }
    }

    [Fact]
    public void 변조된_패킷은_거부한다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var plaintext = new byte[] { 9, 9, 9, 9 };
            var buffer = new byte[plaintext.Length + AesGcmPacketCipher.OverheadBytes];
            var written = sender.Encrypt(plaintext, buffer);

            buffer[10] ^= 0xFF;   // 암호문 한 바이트 뒤집기

            var output = new byte[plaintext.Length];
            Assert.False(receiver.TryDecrypt(buffer.AsSpan(0, written), output, out _));
        }
    }

    [Fact]
    public void 태그가_변조되면_거부한다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var plaintext = new byte[] { 5, 5 };
            var buffer = new byte[plaintext.Length + AesGcmPacketCipher.OverheadBytes];
            var written = sender.Encrypt(plaintext, buffer);

            buffer[written - 1] ^= 0x01;   // 태그 마지막 바이트

            Assert.False(receiver.TryDecrypt(buffer.AsSpan(0, written), new byte[plaintext.Length], out _));
        }
    }

    [Fact]
    public void 다른_키로는_복호화하지_못한다()
    {
        using var sender = new AesGcmPacketCipher(NewKey(1), 42, serverDirection: false);
        using var wrongReceiver = new AesGcmPacketCipher(NewKey(200), 42, serverDirection: true);

        var plaintext = new byte[] { 1, 2, 3 };
        var buffer = new byte[plaintext.Length + AesGcmPacketCipher.OverheadBytes];
        var written = sender.Encrypt(plaintext, buffer);

        Assert.False(wrongReceiver.TryDecrypt(buffer.AsSpan(0, written), new byte[plaintext.Length], out _));
    }

    [Fact]
    public void 다른_conv의_패킷은_복호화하지_못한다()
    {
        var key = NewKey(1);
        using var sender = new AesGcmPacketCipher(key, 42, serverDirection: false);
        using var receiver = new AesGcmPacketCipher(key, 43, serverDirection: true);

        var plaintext = new byte[] { 1, 2, 3 };
        var buffer = new byte[plaintext.Length + AesGcmPacketCipher.OverheadBytes];
        var written = sender.Encrypt(plaintext, buffer);

        // nonce 에 conv 가 섞이므로 다른 세션의 패킷은 인증에 실패한다.
        Assert.False(receiver.TryDecrypt(buffer.AsSpan(0, written), new byte[plaintext.Length], out _));
    }

    [Fact]
    public void 같은_패킷을_두번_주면_리플레이로_거부한다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var plaintext = new byte[] { 7, 7, 7 };
            var buffer = new byte[plaintext.Length + AesGcmPacketCipher.OverheadBytes];
            var written = sender.Encrypt(plaintext, buffer);

            var output = new byte[plaintext.Length];
            Assert.True(receiver.TryDecrypt(buffer.AsSpan(0, written), output, out _));
            Assert.False(receiver.TryDecrypt(buffer.AsSpan(0, written), output, out _));
        }
    }

    [Fact]
    public void 윈도_안에서_순서가_뒤바뀐_패킷은_받아준다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var packets = new byte[8][];
            for (var i = 0; i < packets.Length; i++)
            {
                var plaintext = new byte[] { (byte)i };
                var buffer = new byte[1 + AesGcmPacketCipher.OverheadBytes];
                sender.Encrypt(plaintext, buffer);
                packets[i] = buffer;
            }

            var output = new byte[1];

            // 뒤에 것부터 넣어도(순서 뒤섞임) 전부 받아야 한다.
            for (var i = packets.Length - 1; i >= 0; i--)
            {
                Assert.True(receiver.TryDecrypt(packets[i], output, out _), $"패킷 {i} 를 거부했다.");
                Assert.Equal((byte)i, output[0]);
            }

            // 다시 넣으면 전부 리플레이로 거부.
            for (var i = 0; i < packets.Length; i++)
                Assert.False(receiver.TryDecrypt(packets[i], output, out _));
        }
    }

    [Fact]
    public void 윈도보다_오래된_패킷은_거부한다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            var first = new byte[1 + AesGcmPacketCipher.OverheadBytes];
            sender.Encrypt(new byte[] { 1 }, first);

            // 64개 넘게 진행시킨 뒤에 첫 패킷을 넣으면 윈도 밖이다.
            var output = new byte[1];
            for (var i = 0; i < 100; i++)
            {
                var buffer = new byte[1 + AesGcmPacketCipher.OverheadBytes];
                sender.Encrypt(new byte[] { (byte)i }, buffer);
                Assert.True(receiver.TryDecrypt(buffer, output, out _));
            }

            Assert.False(receiver.TryDecrypt(first, output, out _));
        }
    }

    [Fact]
    public void 방향이_다르면_카운터_공간이_겹치지_않는다()
    {
        var key = NewKey(1);
        using var clientSide = new AesGcmPacketCipher(key, 42, serverDirection: false);
        using var serverSide = new AesGcmPacketCipher(key, 42, serverDirection: true);

        // 서버 방향은 카운터 최상위 비트가 1 이라 클라 방향과 절대 겹치지 않는다.
        Assert.Equal(0UL, clientSide.NextCounter);
        Assert.Equal(1UL << 63, serverSide.NextCounter);
    }

    [Fact]
    public void 잘못된_키_길이는_거부한다()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmPacketCipher(new byte[16], 1, false));
        Assert.Throws<ArgumentNullException>(() => new AesGcmPacketCipher(null!, 1, false));
    }

    [Fact]
    public void 짧은_패킷은_복호화하지_않는다()
    {
        var (sender, receiver) = NewPair();
        using (sender)
        using (receiver)
        {
            Assert.False(receiver.TryDecrypt(new byte[4], new byte[16], out _));
            Assert.False(receiver.TryDecrypt(Array.Empty<byte>(), new byte[16], out _));
        }
    }
}
