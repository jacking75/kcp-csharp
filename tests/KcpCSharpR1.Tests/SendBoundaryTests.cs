using System.Net;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.Tests;

public class SendBoundaryTests
{
    [Fact]
    public void MessageModeRejectsFragmentCountThatCannotFitWireHeader()
    {
        var kcp = new Kcp(1, (_, _) => { });
        kcp.WndSize(512, 512);
        Assert.Equal(-2, kcp.Send(new byte[kcp.Mss * 256 + 1]));
        Assert.Equal(0, kcp.WaitSnd);
        Assert.Equal(0, kcp.Send(new byte[kcp.Mss * 256]));
        Assert.Equal(256, kcp.WaitSnd);
        kcp.Release();
    }

    [Fact]
    public void RejectedStreamSendDoesNotAppendPartialData()
    {
        var packets = new List<byte[]>();
        var kcp = new Kcp(1, (data, size) => packets.Add(data.AsSpan(0, size).ToArray()), () => 0);
        kcp.SetStreamMode(true);
        kcp.NoDelay(1, 10, 2, 1);
        Assert.Equal(0, kcp.Send(new byte[] { 42 }));
        Assert.Equal(-2, kcp.Send(new byte[kcp.Mss * 33]));
        kcp.Update();
        var receiver = new Kcp(1, (_, _) => { });
        foreach (var packet in packets) Assert.Equal(0, receiver.Input(packet, true, false));
        Assert.Equal(1, receiver.PeekSize());
        var result = new byte[1];
        Assert.Equal(1, receiver.Recv(result));
        Assert.Equal(42, result[0]);
        kcp.Release();
        receiver.Release();
    }

    [Fact]
    public void MtuReductionCannotUnderflowReservedPayloadSize()
    {
        var kcp = new Kcp(1, (_, _) => { });
        Assert.True(kcp.ReserveBytes(100));
        var oldMss = kcp.Mss;
        Assert.Equal(-1, kcp.SetMtu(100));
        Assert.Equal(oldMss, kcp.Mss);
    }

    [Fact]
    public void MtuReductionCannotInvalidateQueuedSegments()
    {
        var kcp = new Kcp(1, (_, _) => { });
        Assert.Equal(0, kcp.Send(new byte[500]));
        Assert.Equal(-1, kcp.SetMtu(100));
        Assert.False(kcp.ReserveBytes(1000));
        kcp.Release();
    }

    [Fact]
    public void MssGrowthCannotInvalidateQueuedStreamCapacity()
    {
        var kcp = new Kcp(1, (_, _) => { });
        kcp.SetMtu(100);
        kcp.ReserveBytes(10);
        kcp.SetStreamMode(true);
        Assert.Equal(0, kcp.Send(new byte[1]));
        Assert.Equal(-1, kcp.SetMtu(1400));
        Assert.False(kcp.ReserveBytes(0));
        Assert.Equal(0, kcp.Send(new byte[100]));
        Assert.Equal(2, kcp.WaitSnd);
        kcp.Release();
    }

    [Fact]
    public void FragmentedMessageCannotExceedSessionBacklog()
    {
        var options = new KcpSessionOptions { RawMode = true, RawModeConv = 1, Mtu = 100, MaxSendBacklog = 2 };
        var session = CreateSession(options);
        Assert.Equal(KcpSendResult.Ok, session.Send(new byte[1]));
        Assert.Equal(KcpSendResult.Backpressure, session.Send(new byte[77]));
        Assert.Equal(KcpSendResult.Ok, session.Send(new byte[1]));
        session.ReleaseResources();
    }

    [Fact]
    public void OutboxRejectsMessagesThatCanNeverFitBacklog()
    {
        var session = CreateSession(new KcpSessionOptions
        {
            RawMode = true, RawModeConv = 1, Mtu = 100, MaxSendBacklog = 2, SendOutboxCapacity = 2,
        });
        Assert.False(session.TrySendFromAnyThread(new byte[153]));
        Assert.Equal(0, session.OutboxAccepted);
        Assert.Equal(1, session.OutboxRejected);
        session.ReleaseResources();
    }

    [Fact]
    public void SessionUsesSnapshotOfOptions()
    {
        var options = new KcpSessionOptions { RawMode = true, RawModeConv = 1, MaxSendBacklog = 1 };
        var session = CreateSession(options);
        options.MaxSendBacklog = 100;
        Assert.Equal(KcpSendResult.Ok, session.Send(new byte[1]));
        Assert.Equal(KcpSendResult.Backpressure, session.Send(new byte[1]));
        session.ReleaseResources();
    }

    [Fact]
    public void OutboxRetainsFragmentedHeadUntilWholeMessageFits()
    {
        uint now = 0;
        var packets = new Queue<byte[]>();
        var options = new KcpSessionOptions
        {
            RawMode = true, RawModeConv = 1, Mtu = 100, MaxSendBacklog = 2, SendOutboxCapacity = 2,
        };
        var session = new KcpSession(new IPEndPoint(IPAddress.Loopback, 27180), options,
            (data, _) => packets.Enqueue(data.ToArray()), () => now);
        var receiver = new Kcp(1, (data, length) => session.OnFrame(data.AsSpan(0, length), now), () => now);
        receiver.NoDelay(1, 10, 2, 1);
        receiver.WndSize(128, 128);
        Assert.Equal(KcpSendResult.Ok, session.Send(new byte[] { 42 }));
        Assert.True(session.TrySendFromAnyThread(new byte[77]));
        session.Update(now);
        Assert.Equal(1, session.Kcp!.WaitSnd);
        while (packets.TryDequeue(out var packet)) receiver.Input(packet, true, true);
        Assert.Equal(0, session.Kcp!.WaitSnd);
        var received = new byte[100];
        Assert.Equal(1, receiver.Recv(received));
        now += 10;
        session.Update(now);
        while (packets.TryDequeue(out var packet)) receiver.Input(packet, true, true);
        Assert.Equal(77, receiver.Recv(received));
        Assert.Equal(0, session.OutboxRejected);
        session.ReleaseResources();
        receiver.Release();
    }

    [Fact]
    public void ClonedOptionsDoNotShareMutableKeyOrFecSettings()
    {
        var options = new KcpSessionOptions
        {
            EncryptionKey = new byte[32],
            Fec = new KcpCSharpR1.Transport.Fec.FecOptions(),
        };
        var clone = options.Clone();
        options.EncryptionKey[0] = 42;
        options.Fec.DataShards = 7;
        Assert.Equal(0, clone.EncryptionKey![0]);
        Assert.Equal(10, clone.Fec!.DataShards);
    }

    [Theory]
    [InlineData("HandshakeTimeoutMs", 0)]
    [InlineData("HandshakeRetryIntervalMs", -1)]
    [InlineData("FinLingerMs", -1)]
    [InlineData("IntervalMs", 0)]
    [InlineData("IntervalMs", 5001)]
    [InlineData("SendWindow", 0)]
    [InlineData("ReceiveWindow", 1)]
    [InlineData("ReceiveWindow", 65536)]
    [InlineData("NoDelay", 2)]
    [InlineData("NoCongestionControl", -1)]
    [InlineData("FastResend", -1)]
    public void InvalidOptionsFailBeforeCreatingSession(string name, int value)
    {
        var options = new KcpSessionOptions();
        typeof(KcpSessionOptions).GetProperty(name)!.SetValue(options, value);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateSession(options));
        Assert.Equal(name, exception.ParamName);
    }

    [Fact]
    public void EncryptionCannotSilentlyExceedConfiguredMtu()
    {
        var options = new KcpSessionOptions { Mtu = 64, EncryptionKey = new byte[32] };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSession(options));
    }

    [Fact]
    public void RawModeCannotSilentlyDisableEncryptionOrFec()
    {
        Assert.Throws<ArgumentException>(() => CreateSession(new KcpSessionOptions
        {
            RawMode = true, EncryptionKey = new byte[32],
        }));
        Assert.Throws<ArgumentException>(() => CreateSession(new KcpSessionOptions
        {
            RawMode = true, Fec = new KcpCSharpR1.Transport.Fec.FecOptions(),
        }));
    }

    [Fact]
    public void ReceiveCallbackCanCloseSessionDuringUpdate()
    {
        var session = CreateSession(new KcpSessionOptions { RawMode = true, RawModeConv = 1 });
        var sender = new Kcp(1, (data, length) => session.OnFrame(data.AsSpan(0, length), 0), () => 0);
        sender.NoDelay(1, 10, 2, 1);
        sender.Send(new byte[] { 42 });
        sender.Update();
        session.ReliableReceived += (current, _) => current.Disconnect();
        session.Update(0);
        Assert.Equal(KcpSessionState.Closed, session.State);
        sender.Release();
        session.ReleaseResources();
    }

    [Fact]
    public void ReleasedSessionIsClosedAndCannotReconnect()
    {
        var packets = 0;
        var session = new KcpSession(new IPEndPoint(IPAddress.Loopback, 27180),
            new KcpSessionOptions { SendOutboxCapacity = 1 }, (_, _) => packets++, () => 0);
        session.StartConnect(0);
        Assert.Equal(1, packets);
        session.ReleaseResources();
        Assert.Equal(KcpSessionState.Closed, session.State);
        session.StartConnect(1);
        session.Update(1000);
        Assert.Equal(1, packets);
        Assert.False(session.TrySendFromAnyThread(new byte[1]));
        Assert.Equal(KcpSendResult.NotConnected, session.Send(new byte[1]));
    }

    private static KcpSession CreateSession(KcpSessionOptions options) =>
        new(new IPEndPoint(IPAddress.Loopback, 27180), options, (_, _) => { }, () => 0);
}
