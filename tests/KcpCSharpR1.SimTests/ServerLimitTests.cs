using System;
using System.Collections.Generic;
using System.Net;
using KcpCSharpR1;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.SimTests;

/// <summary>P4-4c: 서버 한도(세션 수 상한)와 다중 세션 스케줄링.</summary>
public class ServerLimitTests
{
    private const uint StepMs = 10;

    private sealed class MultiClientHarness : IDisposable
    {
        public readonly VirtualNetwork Network;
        public readonly KcpServer Server;
        public readonly List<KcpSession> Clients = new();
        public readonly List<KcpSession> Accepted = new();
        public readonly IPEndPoint ServerEndPoint = new(IPAddress.Parse("10.0.0.1"), 27110);

        private readonly List<VirtualTransport> clientTransports = new();
        private readonly byte[] buffer = new byte[2048];

        public uint Now;

        public MultiClientHarness(int maxSessions)
        {
            Network = new VirtualNetwork(5) { DelayMs = 10 };

            var serverTransport = Network.CreateTransport(ServerEndPoint);
            var serverOptions = new KcpServerOptions { MaxSessions = maxSessions };
            serverOptions.Session.PingIntervalMs = 500;
            serverOptions.Session.SessionTimeoutMs = 5_000;

            Server = new KcpServer(ServerEndPoint.Port, serverOptions, () => Now);
            Server.SessionConnected += s => Accepted.Add(s);
            Server.Start(serverTransport, runLoop: false);
        }

        public KcpSession AddClient(int index)
        {
            var local = new IPEndPoint(IPAddress.Parse("10.0.1." + (index + 1)), 40000 + index);
            var transport = Network.CreateTransport(local);
            clientTransports.Add(transport);

            var options = new KcpSessionOptions
            {
                PingIntervalMs = 500,
                SessionTimeoutMs = 5_000,
                HandshakeRetryIntervalMs = 200,
                HandshakeTimeoutMs = 1_500,
            };

            var session = new KcpSession(ServerEndPoint, options,
                (data, remote) => transport.SendTo(data, remote), () => Now, isClient: true);
            Clients.Add(session);
            return session;
        }

        public void Step()
        {
            Now += StepMs;
            Network.Advance(Now);

            for (var i = 0; i < Clients.Count; i++)
            {
                while (clientTransports[i].TryReceive(buffer, out var received, out _))
                    Clients[i].OnFrame(buffer.AsSpan(0, received), Now);
                Clients[i].Update(Now);
            }

            Server.Pump();
        }

        public bool RunUntil(Func<bool> condition, uint maxMs)
        {
            var deadline = Now + maxMs;
            while (Kcp.Itimediff(deadline, Now) > 0)
            {
                if (condition()) return true;
                Step();
            }
            return condition();
        }

        public void Dispose() => Server.Dispose();
    }

    /// <summary>MaxSessions 를 넘는 SYN 은 무응답으로 버려진다.</summary>
    [Fact]
    public void 세션_상한을_넘으면_새_연결을_받지_않는다()
    {
        using var h = new MultiClientHarness(maxSessions: 2);

        for (var i = 0; i < 4; i++)
            h.AddClient(i).StartConnect(h.Now);

        h.RunUntil(() => false, 4_000);

        Assert.Equal(2, h.Server.SessionCount);
        Assert.Equal(2, h.Accepted.Count);

        var connected = 0;
        var failed = 0;
        foreach (var client in h.Clients)
        {
            if (client.State == KcpSessionState.Connected) connected++;
            if (client.State == KcpSessionState.Closed) failed++;
        }

        Assert.Equal(2, connected);
        Assert.Equal(2, failed);   // 상한 초과분은 핸드셰이크 실패로 끝난다.
    }

    /// <summary>여러 세션이 동시에 붙어도 스케줄러가 전부 구동한다.</summary>
    [Fact]
    public void 다수_세션이_동시에_송수신한다()
    {
        const int clientCount = 32;
        using var h = new MultiClientHarness(maxSessions: 100);

        for (var i = 0; i < clientCount; i++)
            h.AddClient(i).StartConnect(h.Now);

        // 서버 테이블만 보면 SYN_ACK 이 아직 날아가는 중일 수 있다. 클라이언트 상태까지 확인한다.
        Assert.True(h.RunUntil(() =>
        {
            if (h.Server.SessionCount != clientCount) return false;
            foreach (var client in h.Clients)
                if (client.State != KcpSessionState.Connected) return false;
            return true;
        }, 10_000));

        // 서버는 받은 것을 그대로 돌려준다.
        foreach (var session in h.Accepted)
            session.ReliableReceived += (s, data) => s.Send(data.Span);

        var echoes = new int[clientCount];
        for (var i = 0; i < clientCount; i++)
        {
            var index = i;
            h.Clients[i].ReliableReceived += (_, _) => echoes[index]++;
            Assert.Equal(KcpSendResult.Ok, h.Clients[i].Send(new byte[] { (byte)i }));
        }

        Assert.True(h.RunUntil(() =>
        {
            foreach (var count in echoes)
                if (count == 0) return false;
            return true;
        }, 15_000));

        foreach (var count in echoes)
            Assert.True(count >= 1);

        // conv 는 전부 달라야 한다.
        var convs = new HashSet<uint>();
        foreach (var session in h.Accepted)
            Assert.True(convs.Add(session.Conv), $"conv 중복: {session.Conv}");
    }

    /// <summary>세션이 끊기면 서버 테이블과 스케줄러에서 사라진다.</summary>
    [Fact]
    public void 끊긴_세션은_테이블에서_제거된다()
    {
        using var h = new MultiClientHarness(maxSessions: 10);

        var client = h.AddClient(0);
        client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Server.SessionCount == 1, 5_000));

        client.Disconnect();

        Assert.True(h.RunUntil(() => h.Server.SessionCount == 0, 10_000));
        Assert.Empty(h.Server.Sessions);
    }
}
