using System.Net;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.SimTests;

public sealed class EventQueueOverflowTests
{
    [Fact]
    public async Task 서버_암호화기_생성_실패는_등록을_되돌리고_같은_주소의_재시도를_허용한다()
    {
        var key = new byte[AesGcmPacketCipher.KeySize];
        var attempts = 0;
        using var server = new KcpServer(27184, new KcpServerOptions
        {
            Session = new KcpSessionOptions
            {
                CipherFactoryOverhead = AesGcmPacketCipher.OverheadBytes,
                CipherFactory = (conv, direction) =>
                {
                    if (++attempts == 1) throw new InvalidOperationException("factory failed");
                    return new AesGcmPacketCipher(key, conv, direction);
                },
            },
        });
        var serverTransport = new MemoryTransport(new IPEndPoint(IPAddress.Loopback, 27184));
        server.Start(serverTransport, runLoop: false);
        using var client = new KcpClient(new KcpSessionOptions { EncryptionKey = key });
        var first = new MemoryTransport(new IPEndPoint(IPAddress.Loopback, 27185))
        {
            Peer = serverTransport,
            OnSend = server.Pump,
        };
        serverTransport.Peers[AddressKey.FromEndPoint(first.LocalEndPoint)] = first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(first, serverTransport.LocalEndPoint));
        Assert.Equal(0, server.SessionCount);
        Assert.Empty(server.Sessions);

        var retry = new MemoryTransport(first.LocalEndPoint) { Peer = serverTransport, OnSend = server.Pump };
        serverTransport.Peers[AddressKey.FromEndPoint(retry.LocalEndPoint)] = retry;
        var session = await client.ConnectAsync(retry, serverTransport.LocalEndPoint);
        Assert.True(session.IsEncrypted);
        Assert.Equal(1, server.SessionCount);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 신뢰_이벤트_누락은_해당_연결을_종료하고_누계를_남긴다(bool overflowClient)
    {
        using var h = new Harness(overflowClient, KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
        await h.ConnectAsync();
        var sending = overflowClient ? h.ServerSession! : h.Client.Session!;
        Assert.Equal(KcpSendResult.Ok, sending.Send(new byte[] { 42 }));
        h.Pump();
        Assert.Equal(1, h.Events.DroppedReliable);
        Assert.True(h.Events.HasCriticalOverflow);
        Assert.NotEqual(KcpSessionState.Connected,
            (overflowClient ? h.Client.Session! : h.ServerSession!).State);
        h.Pump();
        Assert.True(h.Events.DroppedDisconnected > 0);
        Assert.Equal(1, h.Events.Count);
        h.Server.Stop();
        h.Client.Dispose();
        Assert.Equal(0, h.Events.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 비신뢰_이벤트_누락은_연결을_유지한다(bool overflowClient)
    {
        using var h = new Harness(overflowClient, KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
        await h.ConnectAsync();
        var sending = overflowClient ? h.ServerSession! : h.Client.Session!;
        Assert.Equal(KcpSendResult.Ok, sending.SendUnreliable(new byte[] { 7 }));
        h.Pump();
        Assert.Equal(1, h.Events.DroppedUnreliable);
        Assert.Equal(0, h.Events.DroppedReliable);
        Assert.False(h.Events.HasCriticalOverflow);
        Assert.Equal(KcpSessionState.Connected, h.Client.Session!.State);
        Assert.Equal(KcpSessionState.Connected, h.ServerSession!.State);
        Assert.Equal(1, h.Events.Count);
    }

    [Fact]
    public async Task 기본_정책은_기존처럼_신뢰_이벤트를_버리고_연결을_유지한다()
    {
        using var h = new Harness(false, KcpEventQueueOverflowPolicy.DropNewest);
        await h.ConnectAsync();
        Assert.Equal(KcpSendResult.Ok, h.Client.Session!.Send(new byte[] { 1 }));
        h.Pump();
        Assert.Equal(1, h.Events.DroppedReliable);
        Assert.Equal(1, h.Events.Dropped);
        Assert.True(h.Events.HasCriticalOverflow);
        Assert.Equal(KcpSessionState.Connected, h.ServerSession!.State);
    }

    [Fact]
    public async Task 종료_이벤트_누락은_재귀_종료를_만들지_않는다()
    {
        using var client = new KcpClient(new KcpSessionOptions { RawMode = true });
        var events = client.EnableEventQueue(1, KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
        var transport = new MemoryTransport(new IPEndPoint(IPAddress.Loopback, 27185));
        var session = await client.ConnectAsync(transport, new IPEndPoint(IPAddress.Loopback, 27184));
        var disconnected = 0;
        client.Disconnected += (_, _) => disconnected++;
        Assert.True(events.Publish(KcpEventType.Connected, session, default, default));
        Assert.False(events.Publish(KcpEventType.Reliable, session, new byte[] { 2 }, default));
        Assert.Equal(1, disconnected);
        Assert.Equal(1, events.DroppedReliable);
        Assert.Equal(1, events.DroppedDisconnected);
        Assert.Equal(2, events.Dropped);
        Assert.Equal(KcpSessionState.Closed, session.State);
    }

    [Fact]
    public async Task 연결_이벤트가_누락되면_연결을_계속하지_않는다()
    {
        using var h = new Harness(false, KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
        await h.ConnectAsync();
        // 첫 연결 이벤트가 큐를 채웠다. 새 클라이언트는 같은 실제 서버 수신 경로로 연결한다.
        using var second = new KcpClient();
        var peer = new MemoryTransport(new IPEndPoint(IPAddress.Loopback, 27186)) { Peer = h.ServerTransport };
        peer.OnSend = h.Server.Pump;
        h.ServerTransport.Peers[AddressKey.FromEndPoint(peer.LocalEndPoint)] = peer;
        await Assert.ThrowsAsync<TimeoutException>(() => second.ConnectAsync(peer, h.ServerTransport.LocalEndPoint));
        Assert.Equal(1, h.Events.DroppedConnected);
        Assert.True(h.Events.HasCriticalOverflow);
        Assert.True(h.Client.IsConnected);
    }

    private sealed class Harness : IDisposable
    {
        public readonly KcpServer Server;
        public readonly KcpClient Client;
        public readonly MemoryTransport ServerTransport = new(new IPEndPoint(IPAddress.Loopback, 27184));
        public readonly KcpEventQueue Events;
        public KcpSession? ServerSession;
        private readonly MemoryTransport clientTransport = new(new IPEndPoint(IPAddress.Loopback, 27185));
        private uint now;

        public Harness(bool overflowClient, KcpEventQueueOverflowPolicy policy)
        {
            Client = new KcpClient(new KcpSessionOptions { FinLingerMs = 0 }, () => now);
            Server = new KcpServer(27184, new KcpServerOptions
            {
                Session = new KcpSessionOptions { FinLingerMs = 0 },
            }, () => now);
            Server.SessionConnected += session => ServerSession = session;
            ServerTransport.Peers[AddressKey.FromEndPoint(clientTransport.LocalEndPoint)] = clientTransport;
            clientTransport.Peer = ServerTransport;
            clientTransport.OnSend = Server.Pump;
            Events = overflowClient ? Client.EnableEventQueue(1, policy) : Server.EnableEventQueue(1, policy);
            Server.Start(ServerTransport, runLoop: false);
        }

        public Task<KcpSession> ConnectAsync() => Client.ConnectAsync(clientTransport, ServerTransport.LocalEndPoint);

        public void Pump()
        {
            now += 10;
            Server.Pump();
            Client.Update();
            Server.Pump();
            Client.Update();
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    private sealed class MemoryTransport(IPEndPoint local) : IUdpTransport
    {
        private readonly Queue<(byte[] Data, AddressKey Remote)> incoming = new();
        public readonly Dictionary<AddressKey, MemoryTransport> Peers = new();
        public MemoryTransport? Peer;
        public Action? OnSend;
        public IPEndPoint LocalEndPoint => local;
        public void Bind(IPEndPoint localEndPoint) { }
        public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
        {
            var target = Peer;
            if (target == null) Peers.TryGetValue(AddressKey.FromEndPoint(remote), out target);
            target?.incoming.Enqueue((data.ToArray(), AddressKey.FromEndPoint(local)));
            OnSend?.Invoke();
            return data.Length;
        }
        public bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)
        {
            received = 0;
            remote = default;
            if (!incoming.TryDequeue(out var packet)) return false;
            packet.Data.CopyTo(buffer);
            received = packet.Data.Length;
            remote = packet.Remote;
            return true;
        }
        public bool WaitForData(int microseconds) => incoming.Count != 0;
        public void Dispose() => incoming.Clear();
    }
}
