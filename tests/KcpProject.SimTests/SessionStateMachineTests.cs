using System;
using System.Collections.Generic;
using System.Net;
using KcpProject;
using KcpProject.Transport;

namespace KcpProject.SimTests;

/// <summary>
/// P4-2a/2b: 세션 상태 전이표를 가상 시간으로 검증한다.
/// 실소켓·실제 대기 없이 클라이언트 세션과 실제 <see cref="KcpServer"/> 를 수동 구동한다.
/// </summary>
public class SessionStateMachineTests
{
    private const uint StepMs = 10;

    private sealed class Harness : IDisposable
    {
        public readonly VirtualNetwork Network;
        public readonly KcpServer Server;
        public readonly KcpSession Client;
        public readonly IPEndPoint ServerEndPoint = new(IPAddress.Parse("10.0.0.1"), 27100);
        public readonly IPEndPoint ClientEndPoint = new(IPAddress.Parse("10.0.0.2"), 40000);
        public readonly List<KcpSession> ServerSessions = new();
        public readonly List<(KcpSession Session, KcpDisconnectReason Reason)> ServerDisconnects = new();
        public readonly List<KcpDisconnectReason> ClientDisconnects = new();

        private readonly VirtualTransport clientTransport;
        private readonly byte[] clientBuffer = new byte[2048];

        public uint Now;

        public Harness(int seed = 1, double loss = 0, int delayMs = 20, KcpSessionOptions? clientOptions = null)
        {
            Network = new VirtualNetwork(seed) { DelayMs = delayMs, LossRate = loss };

            var serverTransport = Network.CreateTransport(ServerEndPoint);
            clientTransport = Network.CreateTransport(ClientEndPoint);

            var serverOptions = new KcpServerOptions();
            serverOptions.Session.PingIntervalMs = 500;
            serverOptions.Session.SessionTimeoutMs = 3_000;

            Server = new KcpServer(ServerEndPoint.Port, serverOptions, () => Now);
            Server.SessionConnected += s => ServerSessions.Add(s);
            Server.SessionDisconnected += (s, r) => ServerDisconnects.Add((s, r));
            Server.Start(serverTransport, runLoop: false);

            var options = clientOptions ?? new KcpSessionOptions
            {
                PingIntervalMs = 500,
                SessionTimeoutMs = 3_000,
                HandshakeRetryIntervalMs = 300,
                HandshakeTimeoutMs = 2_000,
            };

            Client = new KcpSession(ServerEndPoint, options, SendFromClient, () => Now, isClient: true);
            Client.Disconnected += (_, reason) => ClientDisconnects.Add(reason);
        }

        private void SendFromClient(ReadOnlySpan<byte> data, IPEndPoint remote)
        {
            clientTransport.SendTo(data, remote);
        }

        public void Step()
        {
            Now += StepMs;
            Network.Advance(Now);

            // 클라이언트 수신
            while (clientTransport.TryReceive(clientBuffer, out var received, out _))
            {
                Client.OnFrame(clientBuffer.AsSpan(0, received), Now);
            }

            Client.Update(Now);
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

        public void Dispose()
        {
            Server.Dispose();
        }
    }

    /// <summary>Closed → Connecting → Connected: 쿠키 왕복을 거쳐 연결된다.</summary>
    [Fact]
    public void 정상_핸드셰이크로_연결된다()
    {
        using var h = new Harness();
        Assert.Equal(KcpSessionState.Closed, h.Client.State);

        h.Client.StartConnect(h.Now);
        Assert.Equal(KcpSessionState.Connecting, h.Client.State);

        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        Assert.Single(h.ServerSessions);
        Assert.Equal(KcpSessionState.Connected, h.ServerSessions[0].State);
        Assert.NotEqual(0u, h.Client.Conv);
        Assert.Equal(h.Client.Conv, h.ServerSessions[0].Conv);
    }

    /// <summary>서버는 SYN 만으로 세션을 만들지 않는다(무상태 쿠키, S-04).</summary>
    [Fact]
    public void 쿠키없는_SYN만으로는_세션이_생기지_않는다()
    {
        using var h = new Harness();

        // 쿠키 응답을 클라이언트에게 전달하지 않는다 → 클라이언트는 계속 cookie=0 으로 SYN 만 보낸다.
        h.Network.DropFilter = (_, to, data) =>
            Framing.PeekType(data) == FrameType.SynCookie;

        h.Client.StartConnect(h.Now);
        h.RunUntil(() => false, 2_000);

        Assert.Empty(h.ServerSessions);
        Assert.Equal(0, h.Server.SessionCount);
    }

    /// <summary>SYN 이 유실돼도 재시도로 연결된다.</summary>
    [Fact]
    public void SYN_유실을_재시도로_극복한다()
    {
        using var h = new Harness();

        var dropped = 0;
        h.Network.DropFilter = (_, _, data) =>
        {
            if (Framing.PeekType(data) == FrameType.Syn && dropped < 2)
            {
                dropped++;
                return true;
            }
            return false;
        };

        h.Client.StartConnect(h.Now);

        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 5_000));
        Assert.Equal(2, dropped);
    }

    /// <summary>SYN_ACK 이 유실돼도 SYN 재전송에 서버가 다시 답해 연결된다.</summary>
    [Fact]
    public void SYN_ACK_유실을_극복한다()
    {
        using var h = new Harness();

        var dropped = 0;
        h.Network.DropFilter = (_, _, data) =>
        {
            if (Framing.PeekType(data) == FrameType.SynAck && dropped < 1)
            {
                dropped++;
                return true;
            }
            return false;
        };

        h.Client.StartConnect(h.Now);

        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 5_000));
        Assert.Equal(1, dropped);
        Assert.Single(h.ServerSessions);   // 세션이 중복 생성되지 않아야 한다.
    }

    /// <summary>Connecting → Closed: 재시도를 다 쓰면 실패한다.</summary>
    [Fact]
    public void 응답이_없으면_핸드셰이크가_실패한다()
    {
        using var h = new Harness();
        h.Network.DropFilter = (_, _, _) => true;   // 전면 블랙홀

        h.Client.StartConnect(h.Now);

        Assert.True(h.RunUntil(() => h.Client.State != KcpSessionState.Connecting, 10_000));
        Assert.Equal(KcpSessionState.Closed, h.Client.State);
        Assert.Contains(KcpDisconnectReason.HandshakeFailed, h.ClientDisconnects);
    }

    /// <summary>Connected 상태에서 신뢰 채널 데이터가 양방향으로 오간다.</summary>
    [Fact]
    public void 신뢰채널_양방향_송수신()
    {
        using var h = new Harness();
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        var serverSession = h.ServerSessions[0];
        var fromClient = new List<byte[]>();
        var fromServer = new List<byte[]>();
        serverSession.ReliableReceived += (_, data) => fromClient.Add(data.ToArray());
        h.Client.ReliableReceived += (_, data) => fromServer.Add(data.ToArray());

        Assert.Equal(KcpSendResult.Ok, h.Client.Send(new byte[] { 1, 2, 3 }));
        Assert.True(h.RunUntil(() => fromClient.Count == 1, 3_000));
        Assert.Equal(new byte[] { 1, 2, 3 }, fromClient[0]);

        Assert.Equal(KcpSendResult.Ok, serverSession.Send(new byte[] { 4, 5, 6, 7 }));
        Assert.True(h.RunUntil(() => fromServer.Count == 1, 3_000));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, fromServer[0]);
    }

    /// <summary>비신뢰 채널(RAW)도 오간다.</summary>
    [Fact]
    public void 비신뢰채널_송수신()
    {
        using var h = new Harness();
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        var serverSession = h.ServerSessions[0];
        var received = new List<byte[]>();
        serverSession.UnreliableReceived += (_, data) => received.Add(data.ToArray());

        Assert.Equal(KcpSendResult.Ok, h.Client.SendUnreliable(new byte[] { 9, 9, 9 }));
        Assert.True(h.RunUntil(() => received.Count == 1, 2_000));
        Assert.Equal(new byte[] { 9, 9, 9 }, received[0]);
    }

    /// <summary>폴링 모델: 이벤트를 구독하지 않으면 큐에 쌓여 Receive 로 꺼낼 수 있다.</summary>
    [Fact]
    public void 폴링모델로도_받을_수_있다()
    {
        using var h = new Harness();
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        var serverSession = h.ServerSessions[0];
        Assert.Equal(KcpSendResult.Ok, serverSession.Send(new byte[] { 7, 7 }));
        Assert.Equal(KcpSendResult.Ok, serverSession.SendUnreliable(new byte[] { 8, 8, 8 }));

        Assert.True(h.RunUntil(() => h.Client.PeekSize() > 0, 3_000));

        var buffer = new byte[64];
        Assert.Equal(2, h.Client.Receive(buffer));
        Assert.Equal(new byte[] { 7, 7 }, buffer[..2]);

        // 비신뢰 메시지도 큐에서 꺼낼 수 있어야 한다.
        var unreliableLength = -1;
        Assert.True(h.RunUntil(() =>
        {
            unreliableLength = h.Client.ReceiveUnreliable(buffer);
            return unreliableLength > 0;
        }, 3_000));
        Assert.Equal(3, unreliableLength);
        Assert.Equal(new byte[] { 8, 8, 8 }, buffer[..3]);
    }

    /// <summary>PING/PONG 으로 핑이 측정된다.</summary>
    [Fact]
    public void PING_PONG으로_핑을_측정한다()
    {
        using var h = new Harness(delayMs: 30);
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        Assert.True(h.RunUntil(() => h.Client.PingMs >= 0, 3_000));

        // 편도 30ms → 왕복 60ms 언저리.
        Assert.InRange(h.Client.PingMs, 40, 120);
    }

    /// <summary>Connected → Dead: 상대가 사라지면 SessionTimeout 으로 끊긴다.</summary>
    [Fact]
    public void 무응답이면_타임아웃으로_끊긴다()
    {
        using var h = new Harness();
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        // 서버가 사라진다.
        h.Network.DropFilter = (from, _, _) => from == AddressKey.FromEndPoint(h.ServerEndPoint);

        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Dead, 10_000));
        Assert.Contains(KcpDisconnectReason.Timeout, h.ClientDisconnects);
    }

    /// <summary>Connected → Closing → Closed: FIN 을 보내면 상대가 즉시 정리한다.</summary>
    [Fact]
    public void FIN으로_정상_종료한다()
    {
        using var h = new Harness();
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        var serverSession = h.ServerSessions[0];

        h.Client.Disconnect(reason: 7);
        Assert.Equal(KcpSessionState.Closing, h.Client.State);

        Assert.True(h.RunUntil(() => serverSession.State == KcpSessionState.Closed, 3_000));
        Assert.Equal(7, serverSession.CloseReason);
        Assert.Contains(h.ServerDisconnects, x => x.Reason == KcpDisconnectReason.RemoteClosed);

        // linger 가 끝나면 클라이언트도 Closed 가 된다.
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Closed, 5_000));
        Assert.Contains(KcpDisconnectReason.LocalClosed, h.ClientDisconnects);
    }

    /// <summary>FIN 이 유실되면 상대는 타임아웃으로 정리한다.</summary>
    [Fact]
    public void FIN이_유실되면_상대는_타임아웃으로_정리한다()
    {
        using var h = new Harness();
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        var serverSession = h.ServerSessions[0];
        h.Network.DropFilter = (_, _, data) => Framing.PeekType(data) == FrameType.Fin;

        h.Client.Disconnect();

        Assert.True(h.RunUntil(
            () => serverSession.State == KcpSessionState.Dead || serverSession.State == KcpSessionState.Closed,
            15_000));
        Assert.Contains(h.ServerDisconnects, x => x.Reason == KcpDisconnectReason.Timeout);
    }

    /// <summary>연결 전에는 Send 가 거부된다.</summary>
    [Fact]
    public void 연결전_송신은_거부된다()
    {
        using var h = new Harness();

        Assert.Equal(KcpSendResult.NotConnected, h.Client.Send(new byte[] { 1 }));
        Assert.Equal(KcpSendResult.NotConnected, h.Client.SendUnreliable(new byte[] { 1 }));

        h.Client.StartConnect(h.Now);
        Assert.Equal(KcpSendResult.NotConnected, h.Client.Send(new byte[] { 1 }));
    }

    /// <summary>P4-4c: 백로그 상한을 넘으면 Send 가 백프레셔를 반환한다.</summary>
    [Fact]
    public void 백로그_상한을_넘으면_백프레셔를_반환한다()
    {
        var options = new KcpSessionOptions
        {
            PingIntervalMs = 500,
            SessionTimeoutMs = 30_000,
            HandshakeRetryIntervalMs = 300,
            HandshakeTimeoutMs = 2_000,
            MaxSendBacklog = 8,
        };

        using var h = new Harness(clientOptions: options);
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 3_000));

        // 네트워크를 막아 ACK 가 오지 않게 하면 백로그가 쌓인다.
        h.Network.DropFilter = (from, _, _) => from == AddressKey.FromEndPoint(h.ClientEndPoint);

        var payload = new byte[1000];
        var sawBackpressure = false;
        for (var i = 0; i < 200 && !sawBackpressure; i++)
        {
            if (h.Client.Send(payload) == KcpSendResult.Backpressure)
                sawBackpressure = true;
        }

        Assert.True(sawBackpressure, "백로그 상한을 넘겼는데도 백프레셔가 나오지 않았다.");
    }

    /// <summary>손실 20% 환경에서도 핸드셰이크와 데이터 전달이 완료된다.</summary>
    [Fact]
    public void 손실환경에서도_연결과_전달이_완료된다()
    {
        using var h = new Harness(seed: 7, loss: 0.20, delayMs: 30);
        h.Client.StartConnect(h.Now);
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Connected, 10_000));

        var serverSession = h.ServerSessions[0];
        var received = new List<byte[]>();
        serverSession.ReliableReceived += (_, data) => received.Add(data.ToArray());

        var payload = TestData.DeterministicBytes(4096, seed: 3);
        Assert.Equal(KcpSendResult.Ok, h.Client.Send(payload));

        Assert.True(h.RunUntil(() => received.Count == 1, 30_000));
        Assert.Equal(payload, received[0]);
    }
}
