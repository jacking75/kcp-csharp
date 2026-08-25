using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KcpCSharpR1;
using KcpCSharpR1.Transport;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// 성능 개선 제안서(docs/performance-improvements.md)의 신규 API 검증.
/// G-1(송신 주소 캐시) · G-2(Span 이벤트) · G-3(상주 복호화 버퍼) · C-1(배열 수신) · M-1(아웃박스) · M-2(이벤트 큐).
/// </summary>
public class PerfImprovementTests
{
    private const uint StepMs = 10;

    /// <summary>가상 망 위의 서버 1 + 클라이언트 세션 1.</summary>
    private sealed class Harness : IDisposable
    {
        public readonly VirtualNetwork Network;
        public readonly KcpServer Server;
        public readonly KcpSession Client;
        public readonly List<KcpSession> ServerSessions = new();
        public readonly IPEndPoint ServerEndPoint = new(IPAddress.Parse("10.0.0.1"), 27130);
        public readonly IPEndPoint ClientEndPoint = new(IPAddress.Parse("10.0.0.2"), 41100);

        private readonly VirtualTransport clientTransport;
        private readonly byte[] clientBuffer = new byte[8192];

        public uint Now;

        public Harness(Func<KcpSessionOptions> optionsFactory, Action<KcpServer>? configureServer = null)
        {
            Network = new VirtualNetwork(1) { DelayMs = 10 };

            var serverTransport = Network.CreateTransport(ServerEndPoint);
            clientTransport = Network.CreateTransport(ClientEndPoint);

            var serverOptions = new KcpServerOptions { Session = optionsFactory(), MetricsIntervalMs = 500 };
            Server = new KcpServer(ServerEndPoint.Port, serverOptions, () => Now);
            Server.SessionConnected += s => ServerSessions.Add(s);
            configureServer?.Invoke(Server);
            Server.Start(serverTransport, runLoop: false);

            Client = new KcpSession(ServerEndPoint, optionsFactory(),
                (data, remote) => clientTransport.SendTo(data, remote), () => Now, isClient: true);
        }

        public void Step()
        {
            Now += StepMs;
            Network.Advance(Now);

            while (clientTransport.TryReceive(clientBuffer, out var received, out _))
                Client.OnFrame(clientBuffer.AsSpan(0, received), Now);

            Client.Update(Now);
            Server.Pump();
        }

        public bool RunUntil(Func<bool> condition, uint maxMs = 5_000)
        {
            var deadline = Now + maxMs;
            while (Kcp.Itimediff(deadline, Now) > 0)
            {
                if (condition()) return true;
                Step();
            }
            return condition();
        }

        public bool Connect()
        {
            Client.StartConnect(Now);
            return RunUntil(() => Client.State == KcpSessionState.Connected && ServerSessions.Count == 1);
        }

        public void Dispose() => Server.Dispose();
    }

    private static KcpSessionOptions BaseOptions() => new()
    {
        PingIntervalMs = 1_000,
        SessionTimeoutMs = 30_000,
        HandshakeRetryIntervalMs = 300,
        HandshakeTimeoutMs = 3_000,
    };

    private static byte[] Pattern(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(seed + i);
        return data;
    }

    // ---------------- G-2: Span 이벤트 ----------------

    [Fact]
    public void Span_이벤트로_신뢰_메시지를_받는다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.Client.ReliableReceivedSpan += (_, data) => received.Add(data.ToArray());

        var payload = Pattern(300, 5);
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(payload));

        Assert.True(h.RunUntil(() => received.Count == 1));
        Assert.Equal(payload, received[0]);
    }

    [Fact]
    public void Span_이벤트와_Memory_이벤트를_함께_구독하면_둘_다_발화한다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var fromSpan = new List<byte[]>();
        var fromMemory = new List<byte[]>();
        h.Client.ReliableReceivedSpan += (_, data) => fromSpan.Add(data.ToArray());
        h.Client.ReliableReceived += (_, data) => fromMemory.Add(data.ToArray());

        var payload = Pattern(64, 9);
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(payload));

        Assert.True(h.RunUntil(() => fromSpan.Count == 1 && fromMemory.Count == 1));
        Assert.Equal(payload, fromSpan[0]);
        Assert.Equal(payload, fromMemory[0]);
    }

    [Fact]
    public void 다조각_메시지도_Span_이벤트로_온전히_온다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.Client.ReliableReceivedSpan += (_, data) => received.Add(data.ToArray());

        // MTU 한 장을 훌쩍 넘겨 여러 조각으로 쪼개지게 한다(조립 경로 폴백).
        var payload = Pattern(9_000, 17);
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(payload));

        Assert.True(h.RunUntil(() => received.Count == 1, 10_000));
        Assert.Equal(payload, received[0]);
    }

    [Fact]
    public void 단일_조각과_다조각을_섞어_보내도_순서가_유지된다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.Client.ReliableReceivedSpan += (_, data) => received.Add(data.ToArray());

        var expected = new List<byte[]>
        {
            Pattern(100, 1),
            Pattern(5_000, 2),
            Pattern(200, 3),
            Pattern(4_000, 4),
        };

        foreach (var message in expected)
            Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(message));

        Assert.True(h.RunUntil(() => received.Count == expected.Count, 10_000));
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], received[i]);
    }

    [Fact]
    public void 비신뢰_채널도_Span_이벤트로_온다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.Client.UnreliableReceivedSpan += (_, data) => received.Add(data.ToArray());

        var payload = Pattern(256, 33);
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].SendUnreliable(payload));

        Assert.True(h.RunUntil(() => received.Count == 1));
        Assert.Equal(payload, received[0]);
    }

    [Fact]
    public void Span_구독자가_없으면_폴링_API가_그대로_동작한다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var payload = Pattern(128, 44);
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(payload));
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].SendUnreliable(payload));

        Assert.True(h.RunUntil(() => h.Client.PeekSize() == payload.Length));

        var buffer = new byte[1024];
        Assert.Equal(payload.Length, h.Client.Receive(buffer));
        Assert.Equal(payload, buffer[..payload.Length]);

        Assert.True(h.RunUntil(() => h.Client.ReceiveUnreliable(buffer) == payload.Length));
    }

    // ---------------- G-3: 상주 복호화 버퍼 ----------------

    [Fact]
    public void 암호화_세션도_Span_이벤트로_왕복한다()
    {
        var key = new byte[AesGcmPacketCipher.KeySize];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i * 3 + 1);

        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.EncryptionKey = (byte[])key.Clone();
            return options;
        });

        Assert.True(h.Connect());
        Assert.True(h.Client.IsEncrypted);

        // 비신뢰 프레임은 KCP 를 거치지 않아 신뢰 메시지를 앞지를 수 있다. 채널별로 따로 모은다.
        var reliableFromServer = new List<byte[]>();
        var reliableFromClient = new List<byte[]>();
        var unreliableFromServer = new List<byte[]>();
        h.Client.ReliableReceivedSpan += (_, data) => reliableFromServer.Add(data.ToArray());
        h.ServerSessions[0].ReliableReceivedSpan += (_, data) => reliableFromClient.Add(data.ToArray());
        h.Client.UnreliableReceivedSpan += (_, data) => unreliableFromServer.Add(data.ToArray());

        // 상주 버퍼를 반복해서 쓰므로 여러 번 주고받아 잔재 오염이 없는지 본다.
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(Pattern(100 + i, (byte)i)));
            Assert.Equal(KcpSendResult.Ok, h.Client.Send(Pattern(50 + i, (byte)(i + 100))));
        }
        Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].SendUnreliable(Pattern(77, 200)));

        Assert.True(h.RunUntil(
            () => reliableFromServer.Count == 20 && reliableFromClient.Count == 20 && unreliableFromServer.Count == 1,
            10_000));

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(Pattern(100 + i, (byte)i), reliableFromServer[i]);
            Assert.Equal(Pattern(50 + i, (byte)(i + 100)), reliableFromClient[i]);
        }
        Assert.Equal(Pattern(77, 200), unreliableFromServer[0]);
    }

    // ---------------- G-4: FEC 디코더 버퍼 재활용 ----------------

    [Fact]
    public void FEC_디코더는_그룹을_재활용해도_정확히_복원한다()
    {
        // G-4 이후 디코더는 그룹 샤드 버퍼를 미리 잡아 두고 재사용한다.
        // 이전 그룹의 잔재가 남거나 패딩을 지우지 않으면 여기서 내용이 깨진다.
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.Fec = new FecOptions { DataShards = 4, ParityShards = 2 };
            return options;
        });

        Assert.True(h.Connect());

        // 결정적 손실: 클라이언트가 보내는 KCP 프레임 5개마다 1개(20%)를 떨어뜨린다.
        var counter = 0;
        var clientKey = AddressKey.FromEndPoint(h.ClientEndPoint);
        h.Network.DropFilter = (from, _, data) =>
        {
            if (from != clientKey || Framing.PeekType(data) != FrameType.Kcp)
                return false;
            counter++;
            return counter % 5 == 0;
        };

        var received = new List<byte[]>();
        h.ServerSessions[0].ReliableReceivedSpan += (_, data) => received.Add(data.ToArray());

        // 그룹(4+2)을 수십 바퀴 돌려 MaxGroups(16) 재활용을 여러 번 강제한다.
        const int messages = 200;
        var expected = new List<byte[]>(messages);
        for (var i = 0; i < messages; i++)
        {
            // 길이를 다양하게 섞어 패딩 구간이 매번 달라지게 한다.
            var message = Pattern(200 + (i * 7 % 900), (byte)i);
            expected.Add(message);
            Assert.Equal(KcpSendResult.Ok, h.Client.Send(message));
        }

        Assert.True(h.RunUntil(() => received.Count == messages, 180_000),
            $"{received.Count}/{messages} 만 도착했습니다.");

        for (var i = 0; i < messages; i++)
            Assert.Equal(expected[i], received[i]);

        Assert.True(h.ServerSessions[0].FecRecoveredPackets > 0,
            "FEC 가 하나도 복원하지 못했다면 이 테스트는 재활용 경로를 검증하지 못한 것이다.");
    }

    // ---------------- M-1: 송신 아웃박스 ----------------

    [Fact]
    public void 아웃박스를_켜지_않으면_예외를_던진다()
    {
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        Assert.Throws<InvalidOperationException>(() => h.Client.TrySendFromAnyThread(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task 다른_스레드에서_넣은_메시지가_Update에서_전송된다()
    {
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.SendOutboxCapacity = 64;
            return options;
        });

        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.ServerSessions[0].ReliableReceivedSpan += (_, data) => received.Add(data.ToArray());

        // 소유 스레드(테스트 스레드)가 아닌 곳에서 넣는다.
        var expected = new List<byte[]>();
        await Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                var message = Pattern(80 + i, (byte)(i * 7));
                lock (expected) expected.Add(message);
                Assert.True(h.Client.TrySendFromAnyThread(message));
            }
        });

        Assert.Equal(10, h.Client.OutboxAccepted);
        Assert.Equal(0, h.Client.OutboxRejected);

        Assert.True(h.RunUntil(() => received.Count == 10, 10_000));
        for (var i = 0; i < 10; i++)
            Assert.Equal(expected[i], received[i]);
    }

    [Fact]
    public void 아웃박스가_가득_차면_false를_돌려준다()
    {
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.SendOutboxCapacity = 4;
            return options;
        });

        Assert.True(h.Connect());

        // Update 를 돌리지 않으면 드레인되지 않으므로 용량만큼만 들어간다.
        for (var i = 0; i < 4; i++)
            Assert.True(h.Client.TrySendFromAnyThread(new byte[] { (byte)i }));

        Assert.False(h.Client.TrySendFromAnyThread(new byte[] { 99 }));
        Assert.Equal(1, h.Client.OutboxRejected);

        // 드레인되면 다시 받아들인다.
        h.Step();
        Assert.True(h.Client.TrySendFromAnyThread(new byte[] { 99 }));
    }

    [Fact]
    public void 빈_데이터는_아웃박스가_거부한다()
    {
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.SendOutboxCapacity = 8;
            return options;
        });

        Assert.True(h.Connect());
        Assert.False(h.Client.TrySendFromAnyThread(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, h.Client.OutboxAccepted);
    }

    [Fact]
    public void 세션이_끝나면_아웃박스_내용이_비워지고_수명_종료_시_닫힌다()
    {
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.SendOutboxCapacity = 2;
            return options;
        });

        Assert.True(h.Connect());

        // 드레인되지 않도록 Update 없이 용량까지 채운다.
        Assert.True(h.Client.TrySendFromAnyThread(new byte[] { 1, 2, 3 }));
        Assert.True(h.Client.TrySendFromAnyThread(new byte[] { 4, 5, 6 }));
        Assert.False(h.Client.TrySendFromAnyThread(new byte[] { 7 }));

        h.Client.Disconnect();
        Assert.True(h.RunUntil(() => h.Client.State == KcpSessionState.Closed, 10_000));

        // 종료 시 내용은 비워지되(대여 버퍼 회수), 재연결을 위해 큐 자체는 살아 있다.
        Assert.True(h.Client.TrySendFromAnyThread(new byte[] { 8, 9 }));

        // 수명이 끝나면 영구히 닫힌다.
        h.Client.ReleaseResources();
        Assert.False(h.Client.TrySendFromAnyThread(new byte[] { 10, 11 }));
    }

    [Fact]
    public void 핸들러가_세션을_다시_구동해도_메시지가_중복되거나_사라지지_않는다()
    {
        // G-2 이후 조립 버퍼가 세션 상주라, 콜백 안에서 Update 를 다시 부르면
        // 안쪽 드레인이 바깥쪽 버퍼를 덮어쓸 수 있다. 재진입 가드가 이를 막는지 본다.
        using var h = new Harness(BaseOptions);
        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.Client.ReliableReceivedSpan += (session, data) =>
        {
            received.Add(data.ToArray());
            session.Update(h.Now);   // 재진입!
        };

        var expected = new List<byte[]>();
        for (var i = 0; i < 30; i++)
        {
            var message = Pattern(150 + i, (byte)(i * 3));
            expected.Add(message);
            Assert.Equal(KcpSendResult.Ok, h.ServerSessions[0].Send(message));
        }

        Assert.True(h.RunUntil(() => received.Count == expected.Count, 20_000),
            $"{received.Count}/{expected.Count} 만 도착했습니다.");

        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], received[i]);
    }

    [Fact]
    public void IPv6_소켓에서도_주소_키_송신이_동작한다()
    {
        if (!Socket.OSSupportsIPv6)
            return;   // IPv6 가 없는 환경에서는 건너뛴다.

        using var receiver = new UdpSocketTransport(AddressFamily.InterNetworkV6);
        receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 27134));

        using var sender = new UdpSocketTransport(AddressFamily.InterNetworkV6);
        sender.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));

        var target = AddressKey.FromEndPoint(new IPEndPoint(IPAddress.IPv6Loopback, 27134));
        var payload = Pattern(48, 61);

        Assert.Equal(payload.Length, sender.SendTo(payload, target));

        var buffer = new byte[256];
        Assert.True(SpinUntilReceived(receiver, buffer, out var received, out var from));
        Assert.Equal(payload, buffer[..received]);
        Assert.Equal(sender.LocalEndPoint!.Port, from.Port);
    }

    // ---------------- M-2: 이벤트 큐 ----------------

    [Fact]
    public void 이벤트_큐로_연결과_메시지와_종료가_들어온다()
    {
        KcpEventQueue? queue = null;
        using var h = new Harness(BaseOptions, server => queue = server.EnableEventQueue(64));

        Assert.NotNull(queue);
        Assert.True(h.Connect());

        Assert.Equal(KcpSendResult.Ok, h.Client.Send(Pattern(120, 21)));
        Assert.Equal(KcpSendResult.Ok, h.Client.SendUnreliable(Pattern(60, 22)));

        Assert.True(h.RunUntil(() => queue!.Count >= 3));

        var seen = new List<(KcpEventType Type, byte[] Data, KcpDisconnectReason Reason)>();
        while (queue!.TryRead(out var ev))
        {
            seen.Add((ev.Type, ev.Data.ToArray(), ev.Reason));
            ev.ReturnPayload();
            Assert.Equal(0, ev.Data.Length);   // 반환 후에는 비어 있다.
            ev.ReturnPayload();                // 이중 호출도 안전하다.
        }

        // 연결이 가장 먼저다. 두 채널 사이의 순서는 보장되지 않으므로(비신뢰는 KCP 를 거치지 않는다)
        // 종류별로 확인한다.
        Assert.Equal(KcpEventType.Connected, seen[0].Type);
        Assert.Equal(Pattern(120, 21), Assert.Single(seen.FindAll(e => e.Type == KcpEventType.Reliable)).Data);
        Assert.Equal(Pattern(60, 22), Assert.Single(seen.FindAll(e => e.Type == KcpEventType.Unreliable)).Data);

        h.Client.Disconnect();
        Assert.True(h.RunUntil(() => queue.Count >= 1, 10_000));

        Assert.True(queue.TryRead(out var closed));
        Assert.Equal(KcpEventType.Disconnected, closed.Type);
        Assert.Equal(KcpDisconnectReason.RemoteClosed, closed.Reason);
        closed.ReturnPayload();

        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void 이벤트_큐가_가득_차면_버리고_계수한다()
    {
        KcpEventQueue? queue = null;
        using var h = new Harness(BaseOptions, server => queue = server.EnableEventQueue(2));

        Assert.True(h.Connect());   // Connected 이벤트 1개가 이미 들어 있다.

        for (var i = 0; i < 20; i++)
            Assert.Equal(KcpSendResult.Ok, h.Client.Send(Pattern(32, (byte)i)));

        Assert.True(h.RunUntil(() => queue!.Dropped > 0, 10_000));

        // 버려도 큐 자체는 계속 쓸 수 있어야 한다.
        while (queue!.TryRead(out var ev)) ev.ReturnPayload();
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void 이벤트_큐는_서버_시작_후에는_켤_수_없다()
    {
        using var h = new Harness(BaseOptions);
        Assert.Throws<InvalidOperationException>(() => h.Server.EnableEventQueue());
    }

    // ---------------- G-1 / C-1: 실소켓 경로 ----------------

    [Fact]
    public void 주소_키_송신과_배열_수신이_실소켓에서_동작한다()
    {
        // G-1: SendTo(ReadOnlySpan, AddressKey) 가 IPEndPoint 없이도 정확한 상대에게 간다.
        // C-1: TryReceive(byte[], ...) 오버로드가 같은 결과를 준다.
        using var receiver = new UdpSocketTransport(AddressFamily.InterNetwork);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 27131));

        using var sender = new UdpSocketTransport(AddressFamily.InterNetwork);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var target = AddressKey.FromEndPoint(new IPEndPoint(IPAddress.Loopback, 27131));
        var payload = Pattern(64, 123);

        Assert.Equal(payload.Length, sender.SendTo(payload, target));

        var buffer = new byte[256];
        Assert.True(SpinUntilReceived(receiver, buffer, out var received, out var from));
        Assert.Equal(payload.Length, received);
        Assert.Equal(payload, buffer[..received]);
        Assert.Equal(sender.LocalEndPoint!.Port, from.Port);

        // 반대 방향: IPEndPoint 오버로드(같은 인스턴스를 재사용하는 세션 경로)
        var back = Pattern(32, 200);
        Assert.Equal(back.Length, receiver.SendTo(back, from.ToEndPoint()));

        Assert.True(SpinUntilReceived(sender, buffer, out received, out _));
        Assert.Equal(back, buffer[..received]);
    }

    [Fact]
    public void 같은_상대에게_반복_송신해도_주소_캐시가_정확하다()
    {
        // G-1 의 ConditionalWeakTable 캐시가 첫 직렬화를 재사용하면서도 목적지를 바꾸지 않는지 본다.
        using var a = new UdpSocketTransport(AddressFamily.InterNetwork);
        a.Bind(new IPEndPoint(IPAddress.Loopback, 27132));

        using var b = new UdpSocketTransport(AddressFamily.InterNetwork);
        b.Bind(new IPEndPoint(IPAddress.Loopback, 27133));

        using var sender = new UdpSocketTransport(AddressFamily.InterNetwork);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // 서로 다른 IPEndPoint 인스턴스 2개를 번갈아 쓴다(세션 2개를 흉내 낸다).
        var toA = new IPEndPoint(IPAddress.Loopback, 27132);
        var toB = new IPEndPoint(IPAddress.Loopback, 27133);

        var buffer = new byte[256];
        for (var i = 0; i < 5; i++)
        {
            var forA = Pattern(16, (byte)i);
            var forB = Pattern(16, (byte)(i + 50));

            Assert.Equal(forA.Length, sender.SendTo(forA, toA));
            Assert.Equal(forB.Length, sender.SendTo(forB, toB));

            Assert.True(SpinUntilReceived(a, buffer, out var n, out _));
            Assert.Equal(forA, buffer[..n]);

            Assert.True(SpinUntilReceived(b, buffer, out n, out _));
            Assert.Equal(forB, buffer[..n]);
        }
    }

    private static bool SpinUntilReceived(IUdpTransport udp, byte[] buffer, out int received, out AddressKey from)
    {
        for (var i = 0; i < 200; i++)
        {
            if (udp.TryReceive(buffer, out received, out from))
                return true;
            Thread.Sleep(5);
        }

        received = 0;
        from = default;
        return false;
    }
}
