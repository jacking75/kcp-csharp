using System.Net;
using KcpCSharpR1.Transport;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.Tests;

public class CipherFactoryTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 27181);

    [Fact]
    public void FactoryRunsOnlyAfterConversationIsKnownAndOncePerConnection()
    {
        var created = new List<TrackingCipher>();
        var options = Options((conv, serverDirection) =>
        {
            var cipher = new TrackingCipher(conv, serverDirection);
            created.Add(cipher);
            return cipher;
        });
        var session = new KcpSession(Remote, options, (_, _) => { }, () => 0);
        Assert.Empty(created);
        session.StartConnect(0);
        Assert.Empty(created);
        var ack = new byte[32];
        var length = Framing.WriteSynAck(ack, 71, 0);
        session.OnFrame(ack.AsSpan(0, length), 0);
        session.OnFrame(ack.AsSpan(0, length), 0);
        var cipher = Assert.Single(created);
        Assert.Equal(71u, cipher.Conversation);
        Assert.False(cipher.ServerDirection);
        Assert.True(session.IsEncrypted);
        session.ReleaseResources();
        session.ReleaseResources();
        Assert.Equal(1, cipher.DisposeCount);
    }

    [Fact]
    public void ServerSessionsHaveIndependentCipherStateAndOwnership()
    {
        var created = new List<TrackingCipher>();
        var options = Options((conv, direction) =>
        {
            var cipher = new TrackingCipher(conv, direction);
            created.Add(cipher);
            return cipher;
        });
        var firstPackets = new List<byte[]>();
        var secondPackets = new List<byte[]>();
        var first = new KcpSession(Remote, options, (data, _) => firstPackets.Add(data.ToArray()), () => 0, false);
        var second = new KcpSession(Remote, options, (data, _) => secondPackets.Add(data.ToArray()), () => 0, false);
        first.AcceptAsServer(11, 0);
        second.AcceptAsServer(12, 0);
        Assert.Equal(2, created.Count);
        Assert.All(created, cipher => Assert.True(cipher.ServerDirection));
        Assert.NotSame(created[0], created[1]);
        var message = new byte[] { 1, 2, 3 };
        Assert.Equal(KcpSendResult.Ok, first.SendUnreliable(message));
        Assert.Equal(KcpSendResult.Ok, second.SendUnreliable(message));
        first.ReleaseResources();
        Assert.Equal(1, created[0].DisposeCount);
        Assert.Equal(0, created[1].DisposeCount);
        Assert.Equal(KcpSendResult.Ok, second.SendUnreliable(message));
        using var receiver = new AesGcmPacketCipher(new byte[32], 12, false);
        var plaintext = new byte[16];
        foreach (var packet in secondPackets)
        {
            Assert.True(Framing.TryReadRaw(packet, out var conv, out var encrypted));
            Assert.Equal(12u, conv);
            Assert.True(receiver.TryDecrypt(encrypted, plaintext, out var length));
            Assert.Equal(message, plaintext.AsSpan(0, length).ToArray());
        }
        second.ReleaseResources();
        Assert.Equal(1, created[1].DisposeCount);
    }

    [Fact]
    public void ReconnectingCreatesFreshCipherInsteadOfReusingDisposedInstance()
    {
        var created = new List<TrackingCipher>();
        var session = new KcpSession(Remote, Options((conv, direction) =>
        {
            var cipher = new TrackingCipher(conv, direction);
            created.Add(cipher);
            return cipher;
        }), (_, _) => { }, () => 0);
        var frame = new byte[32];
        session.StartConnect(0);
        var length = Framing.WriteSynAck(frame, 1, 0);
        session.OnFrame(frame.AsSpan(0, length), 0);
        length = Framing.WriteFin(frame, 1, 0);
        session.OnFrame(frame.AsSpan(0, length), 1);
        Assert.Equal(KcpSessionState.Closed, session.State);
        Assert.Equal(1, created[0].DisposeCount);
        session.StartConnect(2);
        length = Framing.WriteSynAck(frame, 2, 0);
        session.OnFrame(frame.AsSpan(0, length), 2);
        Assert.Equal(2, created.Count);
        Assert.Equal(2u, created[1].Conversation);
        Assert.Equal(0, created[1].DisposeCount);
        Assert.Equal(KcpSendResult.Ok, session.SendUnreliable(new byte[] { 42 }));
        session.ReleaseResources();
        Assert.All(created, cipher => Assert.Equal(1, cipher.DisposeCount));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(25)]
    public void OverheadMismatchDisposesCipherAndLeavesSessionClosed(int overhead)
    {
        var cipher = new TrackingCipher(1, true, overhead);
        var session = new KcpSession(Remote, Options((_, _) => cipher), (_, _) => { }, () => 0, false);
        Assert.Throws<InvalidOperationException>(() => session.AcceptAsServer(1, 0));
        Assert.Equal(KcpSessionState.Closed, session.State);
        Assert.Null(session.Kcp);
        Assert.False(session.IsEncrypted);
        Assert.Equal(1, cipher.DisposeCount);
        session.ReleaseResources();
        Assert.Equal(1, cipher.DisposeCount);
    }

    [Fact]
    public void NullFactoryResultCannotSilentlyDisableEncryption()
    {
        var session = new KcpSession(Remote, Options((_, _) => null!), (_, _) => { }, () => 0, false);
        Assert.Throws<InvalidOperationException>(() => session.AcceptAsServer(1, 0));
        Assert.Equal(KcpSessionState.Closed, session.State);
        Assert.Null(session.Kcp);
        session.ReleaseResources();
    }

    [Fact]
    public void ThrowingFactoryClosesClientHandshakeAndCanRetryWithFreshCipher()
    {
        var fail = true;
        var options = Options((conv, direction) => fail ? throw new InvalidOperationException("factory failure") : new TrackingCipher(conv, direction));
        var session = new KcpSession(Remote, options, (_, _) => { }, () => 0);
        session.StartConnect(0);
        var ack = new byte[32];
        var length = Framing.WriteSynAck(ack, 91, 0);
        Assert.Throws<InvalidOperationException>(() => session.OnFrame(ack.AsSpan(0, length), 0));
        Assert.Equal(KcpSessionState.Closed, session.State);
        Assert.Null(session.Kcp);
        fail = false;
        session.StartConnect(1);
        session.OnFrame(ack.AsSpan(0, length), 1);
        Assert.Equal(KcpSessionState.Connected, session.State);
        Assert.True(session.IsEncrypted);
        session.ReleaseResources();
    }

    [Fact]
    public void FactoryConfigurationIsValidatedBeforeInvocation()
    {
        var calls = 0;
        var options = Options((_, _) => { calls++; return null!; });
        options.EncryptionKey = new byte[32];
        Assert.Throws<ArgumentException>(options.Validate);
        options.EncryptionKey = null;
        options.Cipher = new TrackingCipher(1, true);
        Assert.Throws<ArgumentException>(options.Validate);
        ((IDisposable)options.Cipher).Dispose();
        options.Cipher = null;
        options.RawMode = true;
        Assert.Throws<ArgumentException>(options.Validate);
        options.RawMode = false;
        options.Fec = new FecOptions();
        Assert.Throws<ArgumentException>(options.Validate);
        options.Fec = null;
        options.Mtu = 64;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        options.Mtu = 1400;
        options.CipherFactoryOverhead = -1;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FactoryOverheadParticipatesInReliableAndUnreliableMtuLimits()
    {
        var packets = new List<byte[]>();
        var options = Options((conv, direction) => new TrackingCipher(conv, direction));
        options.Mtu = 100;
        var session = new KcpSession(Remote, options, (data, _) => packets.Add(data.ToArray()), () => 0, false);
        session.AcceptAsServer(1, 0);
        Assert.Equal(51u, session.Kcp!.Mss);
        Assert.Equal(KcpSendResult.Ok, session.Send(new byte[102]));
        session.Update(0);
        Assert.Equal(KcpSendResult.Ok, session.SendUnreliable(new byte[71]));
        Assert.Equal(KcpSendResult.TooLarge, session.SendUnreliable(new byte[72]));
        Assert.NotEmpty(packets);
        Assert.All(packets, packet => Assert.True(packet.Length <= options.Mtu));
        session.ReleaseResources();
    }

    private static KcpSessionOptions Options(PacketCipherFactory factory) => new()
    {
        CipherFactory = factory,
        CipherFactoryOverhead = AesGcmPacketCipher.OverheadBytes,
    };

    private sealed class TrackingCipher : IPacketCipher, IDisposable
    {
        private readonly AesGcmPacketCipher cipher;
        public uint Conversation { get; }
        public bool ServerDirection { get; }
        public int DisposeCount { get; private set; }
        public int Overhead { get; }

        public TrackingCipher(uint conv, bool direction, int overhead = AesGcmPacketCipher.OverheadBytes)
        {
            Conversation = conv;
            ServerDirection = direction;
            Overhead = overhead;
            cipher = new AesGcmPacketCipher(new byte[32], conv, direction);
        }

        public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination) => cipher.Encrypt(plaintext, destination);
        public bool TryDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> destination, out int plaintextLength) =>
            cipher.TryDecrypt(ciphertext, destination, out plaintextLength);
        public void Dispose() { DisposeCount++; cipher.Dispose(); }
    }
}
