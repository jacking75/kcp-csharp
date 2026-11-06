using System.Collections.Concurrent;
using System.Net;
using KcpCSharpR1.Transport;
using KcpSamples.Common;

namespace KcpCSharpR1.SimTests;

public sealed class PollingGameClientTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 27090);

    [Fact]
    public void 연결중_도착한_이벤트도_Poll_스레드에서만_발화한다()
    {
        using var udp = new ScriptedTransport(reply: true);
        using var game = new PollingKcpClient();
        var calls = new List<string>();
        var thread = Environment.CurrentManagedThreadId;
        game.Connected += _ => { Assert.Equal(thread, Environment.CurrentManagedThreadId); calls.Add("connected"); };
        game.UnreliableReceived += (_, data) =>
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            Assert.Equal(new byte[] { 7, 8 }, data.ToArray());
            calls.Add("message");
        };

        game.Connect(udp, Remote);
        Assert.Empty(calls);
        Assert.Null(game.Session);
        game.Poll();
        Assert.Collection(calls,
            value => Assert.Equal("connected", value),
            value => Assert.Equal("message", value));
        game.Poll();
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task 연결대기중_Poll은_수신에_접근하지_않고_종료는_대기를_합류한다()
    {
        using var udp = new ScriptedTransport(reply: false);
        using var game = new PollingKcpClient();
        game.Connect(udp, Remote);
        Assert.True(udp.WaitEntered.Wait(TimeSpan.FromSeconds(5)));
        var receives = udp.ReceiveCalls;
        game.Poll();
        Assert.Equal(receives, udp.ReceiveCalls);

        var stop = Task.Run(game.Dispose);
        try
        {
            Assert.False(udp.Disposed);
        }
        finally
        {
            udp.AllowWait.Set();
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(udp.Disposed);
        Assert.False(udp.DisposedDuringWait);
    }

    [Fact]
    public void 연결실패는_Poll에서_한번만_보고한다()
    {
        using var udp = new ScriptedTransport(reply: false) { FailSend = true };
        using var game = new PollingKcpClient();
        var errors = new List<Exception>();
        game.Error += errors.Add;
        game.Connect(udp, Remote);
        Assert.Empty(errors);
        game.Poll();
        game.Poll();
        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.Null(game.Session);
        Assert.True(udp.Disposed);
    }

    [Fact]
    public void 이벤트가_누락되면_게임진행을_중단하고_실패를_보고한다()
    {
        using var udp = new ScriptedTransport(reply: true);
        using var game = new PollingKcpClient(eventCapacity: 1);
        var connected = false;
        var errors = new List<Exception>();
        game.Connected += _ => connected = true;
        game.Error += errors.Add;
        game.Connect(udp, Remote);
        game.Poll();
        Assert.False(connected);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        Assert.Null(game.Session);
        Assert.True(udp.Disposed);
    }

    private sealed class ScriptedTransport(bool reply) : IUdpTransport
    {
        private readonly ConcurrentQueue<byte[]> incoming = new();
        private int waiting;
        public readonly ManualResetEventSlim WaitEntered = new();
        public readonly ManualResetEventSlim AllowWait = new();
        public int ReceiveCalls;
        public bool FailSend;
        public bool Disposed;
        public bool DisposedDuringWait;
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 27091);
        public void Bind(IPEndPoint localEndPoint) { }

        public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
        {
            if (FailSend) throw new IOException("simulated send failure");
            if (reply && Framing.PeekType(data) == FrameType.Syn)
            {
                var ack = new byte[Framing.SynAckSize];
                Framing.WriteSynAck(ack, 42, 1);
                incoming.Enqueue(ack);
                var raw = new byte[7];
                Framing.WriteRaw(raw, 42, new byte[] { 7, 8 });
                incoming.Enqueue(raw);
            }
            return data.Length;
        }

        public bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)
        {
            Interlocked.Increment(ref ReceiveCalls);
            remote = AddressKey.FromEndPoint(Remote);
            received = 0;
            if (!incoming.TryDequeue(out var data)) return false;
            data.CopyTo(buffer);
            received = data.Length;
            return true;
        }

        public bool WaitForData(int microseconds)
        {
            Interlocked.Exchange(ref waiting, 1);
            WaitEntered.Set();
            AllowWait.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref waiting, 0);
            return false;
        }

        public void Dispose()
        {
            DisposedDuringWait |= Volatile.Read(ref waiting) != 0;
            Disposed = true;
            AllowWait.Set();
        }
    }
}
