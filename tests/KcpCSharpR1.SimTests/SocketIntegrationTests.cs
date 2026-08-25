using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpCSharpR1;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// P4-5: 실소켓 루프백 통합 테스트. 로드맵 12.4절 배정에 따라 UDP 27100~27199 대역만 쓴다.
/// </summary>
[Collection("SocketIntegration")]
public class SocketIntegrationTests
{
    /// <summary>테스트마다 다른 포트를 쓰도록 27100 부터 순번을 돌린다.</summary>
    private static int nextPort = 27100;

    private static int NextPort()
    {
        var port = Interlocked.Increment(ref nextPort);
        if (port > 27199)
        {
            Interlocked.Exchange(ref nextPort, 27100);
            port = 27100;
        }
        return port;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(5).ConfigureAwait(false);
        }
        return condition();
    }

    [Fact]
    public async Task 연결_송수신_정상종료가_실소켓에서_동작한다()
    {
        var port = NextPort();

        using var server = new KcpServer(port);
        var connected = new ConcurrentQueue<KcpSession>();
        var serverReceived = new ConcurrentQueue<byte[]>();
        var serverDisconnects = new ConcurrentQueue<KcpDisconnectReason>();

        server.SessionConnected += s => connected.Enqueue(s);
        server.ReliableReceived += (s, data) =>
        {
            serverReceived.Enqueue(data.ToArray());
            s.Send(data.Span);            // 에코
        };
        server.SessionDisconnected += (_, reason) => serverDisconnects.Enqueue(reason);
        server.Start();

        using var client = new KcpClient();
        var clientReceived = new ConcurrentQueue<byte[]>();
        client.ReliableReceived += (_, data) => clientReceived.Enqueue(data.ToArray());

        var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        Assert.Equal(KcpSessionState.Connected, session.State);
        Assert.True(await WaitUntilAsync(() => connected.Count == 1));

        var payload = TestData.DeterministicBytes(1500, seed: 5);
        Assert.Equal(KcpSendResult.Ok, session.Send(payload));

        var pumping = true;
        var pump = Task.Run(async () =>
        {
            while (Volatile.Read(ref pumping))
            {
                client.Update();
                await Task.Delay(5);
            }
        });

        Assert.True(await WaitUntilAsync(() => clientReceived.Count == 1, 10_000));
        Assert.True(serverReceived.TryDequeue(out var echoed));
        Assert.Equal(payload, echoed);
        Assert.True(clientReceived.TryDequeue(out var roundtrip));
        Assert.Equal(payload, roundtrip);

        // 정상 종료: FIN 을 보내면 서버가 세션을 정리한다.
        client.Disconnect(reason: 1);
        Assert.True(await WaitUntilAsync(() => server.SessionCount == 0, 10_000));
        Assert.True(serverDisconnects.TryDequeue(out var reason));
        Assert.Equal(KcpDisconnectReason.RemoteClosed, reason);

        Volatile.Write(ref pumping, false);
        await pump;
        server.Stop();
    }

    [Fact]
    public async Task 비신뢰채널도_실소켓에서_동작한다()
    {
        var port = NextPort();

        using var server = new KcpServer(port);
        var unreliable = new ConcurrentQueue<byte[]>();
        server.UnreliableReceived += (_, data) => unreliable.Enqueue(data.ToArray());
        server.Start();

        using var client = new KcpClient();
        var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));

        // UDP 라 유실될 수 있으므로 여러 번 보낸다(비신뢰 채널의 정상 동작).
        for (var i = 0; i < 20; i++)
        {
            session.SendUnreliable(new byte[] { 1, 2, 3, 4 });
            client.Update();
            await Task.Delay(5);
        }

        Assert.True(await WaitUntilAsync(() => !unreliable.IsEmpty, 5_000));
        Assert.True(unreliable.TryDequeue(out var received));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, received);

        server.Stop();
    }

    [Fact]
    public async Task 클라이언트가_사라지면_서버가_타임아웃으로_정리한다()
    {
        var port = NextPort();

        var options = new KcpServerOptions();
        options.Session.SessionTimeoutMs = 1_500;
        options.Session.PingIntervalMs = 300;

        using var server = new KcpServer(port, options);
        var disconnects = new ConcurrentQueue<KcpDisconnectReason>();
        server.SessionDisconnected += (_, reason) => disconnects.Enqueue(reason);
        server.Start();

        var client = new KcpClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(await WaitUntilAsync(() => server.SessionCount == 1));

        // 인사도 없이 소켓을 닫는다(크래시·전원 차단 재현).
        client.Dispose();

        Assert.True(await WaitUntilAsync(() => server.SessionCount == 0, 10_000));
        Assert.True(disconnects.TryDequeue(out var reason));
        Assert.Equal(KcpDisconnectReason.Timeout, reason);

        server.Stop();
    }

    [Fact]
    public async Task 다중_클라이언트가_각자_conv를_받는다()
    {
        var port = NextPort();

        using var server = new KcpServer(port);
        var sessions = new ConcurrentQueue<KcpSession>();
        server.SessionConnected += s => sessions.Enqueue(s);
        server.ReliableReceived += (s, data) => s.Send(data.Span);
        server.Start();

        const int clientCount = 8;
        var clients = new KcpClient[clientCount];
        var received = new ConcurrentQueue<byte[]>[clientCount];

        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                var queue = new ConcurrentQueue<byte[]>();
                received[i] = queue;
                clients[i] = new KcpClient();
                clients[i].ReliableReceived += (_, data) => queue.Enqueue(data.ToArray());
                await clients[i].ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            }

            Assert.True(await WaitUntilAsync(() => sessions.Count == clientCount, 10_000));

            // conv 는 세션마다 달라야 한다.
            var convs = new System.Collections.Generic.HashSet<uint>();
            foreach (var s in sessions)
                Assert.True(convs.Add(s.Conv), $"conv 가 중복됐다: {s.Conv}");

            for (var i = 0; i < clientCount; i++)
                clients[i].Session!.Send(new byte[] { (byte)i });

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 10_000)
            {
                for (var i = 0; i < clientCount; i++) clients[i].Update();

                var done = true;
                for (var i = 0; i < clientCount; i++)
                    if (received[i].IsEmpty) { done = false; break; }
                if (done) break;

                await Task.Delay(5);
            }

            for (var i = 0; i < clientCount; i++)
            {
                Assert.True(received[i].TryDequeue(out var echo), $"클라이언트 {i} 가 에코를 못 받았다.");
                Assert.Equal(new byte[] { (byte)i }, echo);
            }
        }
        finally
        {
            foreach (var c in clients)
                c?.Dispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task 응답없는_주소로_연결하면_타임아웃_예외가_난다()
    {
        var options = new KcpSessionOptions
        {
            HandshakeTimeoutMs = 500,
            HandshakeRetryIntervalMs = 100,
            HandshakeMaxRetries = 2,
        };

        using var client = new KcpClient(options);

        // 아무도 듣고 있지 않은 포트
        var deadPort = NextPort();
        await Assert.ThrowsAsync<TimeoutException>(
            () => client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, deadPort)));
    }

    [Fact]
    public async Task 큰_메시지도_조각화되어_왕복한다()
    {
        var port = NextPort();

        using var server = new KcpServer(port);
        server.ReliableReceived += (s, data) => s.Send(data.Span);
        server.Start();

        using var client = new KcpClient();
        var received = new ConcurrentQueue<byte[]>();
        client.ReliableReceived += (_, data) => received.Enqueue(data.ToArray());

        var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));

        var payload = TestData.DeterministicBytes(60 * 1024, seed: 9);
        Assert.Equal(KcpSendResult.Ok, session.Send(payload));

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000 && received.IsEmpty)
        {
            client.Update();
            await Task.Delay(2);
        }

        Assert.True(received.TryDequeue(out var echoed), "60KB 메시지가 왕복하지 못했다.");
        Assert.Equal(payload, echoed);

        server.Stop();
    }
}
