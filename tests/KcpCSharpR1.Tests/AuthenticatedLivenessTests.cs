using System.Net;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.Tests;

public class AuthenticatedLivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TamperedAndReplayedDataCannotExtendSessionLifetime(bool reliable)
    {
        var session = CreateEncryptedSession();
        using var cipher = new AesGcmPacketCipher(new byte[32], 1, false);
        var frame = Frame(cipher, reliable);
        session.OnFrame(frame, 100);
        Assert.Equal(100u, session.LastReceiveAt);
        var tampered = (byte[])frame.Clone();
        tampered[^1] ^= 1;
        session.OnFrame(tampered, 900);
        session.OnFrame(frame, 950);
        Assert.Equal(100u, session.LastReceiveAt);
        session.Update(1100);
        Assert.Equal(KcpSessionState.Dead, session.State);
        session.ReleaseResources();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshAuthenticatedDataExtendsSessionLifetime(bool reliable)
    {
        var session = CreateEncryptedSession();
        using var cipher = new AesGcmPacketCipher(new byte[32], 1, false);
        session.OnFrame(Frame(cipher, reliable), 100);
        session.OnFrame(Frame(cipher, reliable), 900);
        Assert.Equal(900u, session.LastReceiveAt);
        session.Update(1000);
        Assert.Equal(KcpSessionState.Connected, session.State);
        session.ReleaseResources();
    }

    [Fact]
    public void AuthenticatedMalformedKcpCannotExtendSessionLifetime()
    {
        var session = CreateEncryptedSession();
        using var cipher = new AesGcmPacketCipher(new byte[32], 1, false);
        var malformed = new byte[1 + AesGcmPacketCipher.OverheadBytes + Kcp.IKCP_OVERHEAD];
        malformed[0] = (byte)FrameType.Kcp;
        cipher.Encrypt(new byte[Kcp.IKCP_OVERHEAD], malformed.AsSpan(1));
        session.OnFrame(malformed, 900);
        Assert.Equal(0u, session.LastReceiveAt);
        session.Update(1000);
        Assert.Equal(KcpSessionState.Dead, session.State);
        session.ReleaseResources();
    }

    [Fact]
    public void InvalidRawModePacketsCannotExtendSessionLifetime()
    {
        var session = new KcpSession(new IPEndPoint(IPAddress.Loopback, 27182),
            new KcpSessionOptions { RawMode = true, RawModeConv = 1, SessionTimeoutMs = 1000 }, (_, _) => { }, () => 0);
        session.OnFrame(new byte[] { 1 }, 900);
        Assert.Equal(0u, session.LastReceiveAt);
        session.Update(1000);
        Assert.Equal(KcpSessionState.Dead, session.State);
        session.ReleaseResources();
    }

    private static KcpSession CreateEncryptedSession()
    {
        var session = new KcpSession(new IPEndPoint(IPAddress.Loopback, 27182),
            new KcpSessionOptions { EncryptionKey = new byte[32], SessionTimeoutMs = 1000 }, (_, _) => { }, () => 0, false);
        session.AcceptAsServer(1, 0);
        return session;
    }

    private static byte[] Frame(AesGcmPacketCipher cipher, bool reliable)
    {
        byte[] plaintext;
        if (reliable)
        {
            plaintext = new byte[Kcp.IKCP_OVERHEAD + 1];
            KcpCodec.WriteHeader(plaintext, 1, Kcp.IKCP_CMD_PUSH, 0, 128, 0, 0, 0, 1);
            plaintext[^1] = 42;
        }
        else plaintext = new byte[] { 42 };
        var encrypted = new byte[plaintext.Length + cipher.Overhead];
        var length = cipher.Encrypt(plaintext, encrypted);
        var frame = new byte[(reliable ? Framing.KcpHeaderSize : Framing.RawHeaderSize) + length];
        if (reliable)
        {
            frame[0] = (byte)FrameType.Kcp;
            encrypted.AsSpan(0, length).CopyTo(frame.AsSpan(1));
        }
        else Framing.WriteRaw(frame, 1, encrypted.AsSpan(0, length));
        return frame;
    }
}
