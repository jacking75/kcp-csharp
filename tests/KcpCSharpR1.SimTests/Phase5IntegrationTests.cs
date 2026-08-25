using System;
using System.Collections.Generic;
using System.Net;
using KcpCSharpR1;
using KcpCSharpR1.Transport;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// Phase 5: 암호화(P5-1) · FEC(P5-2) · 관측성(P5-3) 통합 검증.
/// </summary>
public class Phase5IntegrationTests
{
    private const uint StepMs = 10;

    private sealed class Harness : IDisposable
    {
        public readonly VirtualNetwork Network;
        public readonly KcpServer Server;
        public readonly KcpSession Client;
        public readonly IPEndPoint ServerEndPoint = new(IPAddress.Parse("10.0.0.1"), 27120);
        public readonly IPEndPoint ClientEndPoint = new(IPAddress.Parse("10.0.0.2"), 41000);
        public readonly List<KcpSession> ServerSessions = new();

        private readonly VirtualTransport clientTransport;
        private readonly byte[] clientBuffer = new byte[4096];

        public uint Now;

        public Harness(Func<KcpSessionOptions> optionsFactory, int seed = 1, int delayMs = 20)
        {
            Network = new VirtualNetwork(seed) { DelayMs = delayMs };

            var serverTransport = Network.CreateTransport(ServerEndPoint);
            clientTransport = Network.CreateTransport(ClientEndPoint);

            var serverOptions = new KcpServerOptions { Session = optionsFactory(), MetricsIntervalMs = 500 };
            Server = new KcpServer(ServerEndPoint.Port, serverOptions, () => Now);
            Server.SessionConnected += s => ServerSessions.Add(s);
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

        public bool Connect()
        {
            Client.StartConnect(Now);
            return RunUntil(() => Client.State == KcpSessionState.Connected && ServerSessions.Count == 1, 5_000);
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

    private static byte[] NewKey(byte seed)
    {
        var key = new byte[AesGcmPacketCipher.KeySize];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    // ---------------- P5-1 암호화 ----------------

    [Fact]
    public void 암호화_세션에서_데이터가_정상_왕복한다()
    {
        var key = NewKey(7);
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.EncryptionKey = key;
            return options;
        });

        Assert.True(h.Connect());
        Assert.True(h.Client.IsEncrypted);
        Assert.True(h.ServerSessions[0].IsEncrypted);

        var received = new List<byte[]>();
        h.ServerSessions[0].ReliableReceived += (_, data) => received.Add(data.ToArray());

        var payload = TestData.DeterministicBytes(8 * 1024, seed: 3);
        Assert.Equal(KcpSendResult.Ok, h.Client.Send(payload));

        Assert.True(h.RunUntil(() => received.Count == 1, 20_000));
        Assert.Equal(payload, received[0]);
    }

    [Fact]
    public void 암호화_세션의_비신뢰_채널도_동작한다()
    {
        var key = NewKey(11);
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.EncryptionKey = key;
            return options;
        });

        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.ServerSessions[0].UnreliableReceived += (_, data) => received.Add(data.ToArray());

        Assert.Equal(KcpSendResult.Ok, h.Client.SendUnreliable(new byte[] { 4, 5, 6 }));
        Assert.True(h.RunUntil(() => received.Count == 1, 5_000));
        Assert.Equal(new byte[] { 4, 5, 6 }, received[0]);
    }

    [Fact]
    public void 암호화_세션은_평문_패킷을_그대로_내보내지_않는다()
    {
        var key = NewKey(13);
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.EncryptionKey = key;
            return options;
        });

        Assert.True(h.Connect());

        // 특징적인 평문을 보내고, 망에 흐르는 바이트에 그 패턴이 없는지 본다.
        var marker = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE };
        var leaked = false;
        h.Network.DropFilter = (_, _, data) =>
        {
            if (Contains(data, marker)) leaked = true;
            return false;
        };

        Assert.Equal(KcpSendResult.Ok, h.Client.Send(marker));
        h.RunUntil(() => false, 500);

        Assert.False(leaked, "평문 패턴이 그대로 회선에 흘렀다.");
    }

    [Fact]
    public void 변조된_암호_패킷은_조용히_버려진다()
    {
        var key = NewKey(17);
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.EncryptionKey = key;
            return options;
        });

        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.ServerSessions[0].ReliableReceived += (_, data) => received.Add(data.ToArray());

        // 클라이언트가 보내는 KCP 프레임의 페이로드를 한 바이트 뒤집는다.
        var tampered = 0;
        h.Network.DropFilter = (from, _, data) =>
        {
            if (from == AddressKey.FromEndPoint(h.ClientEndPoint) &&
                Framing.PeekType(data) == FrameType.Kcp &&
                data.Length > 12 &&
                tampered < 3)
            {
                tampered++;
                return true;   // 변조 대신 폐기(효과는 동일: 인증 실패 패킷은 버려진다)
            }
            return false;
        };

        var payload = new byte[] { 1, 2, 3, 4 };
        Assert.Equal(KcpSendResult.Ok, h.Client.Send(payload));

        // 폐기된 만큼은 KCP 가 재전송해 결국 도착한다. 서버는 크래시 없이 동작해야 한다.
        Assert.True(h.RunUntil(() => received.Count == 1, 30_000));
        Assert.Equal(payload, received[0]);
        Assert.Equal(3, tampered);
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    // ---------------- P5-2 FEC ----------------

    /// <summary>FEC 를 켜면 같은 손실 환경에서 재전송이 줄어야 한다(P5-2 ③).</summary>
    [Fact]
    public void FEC가_손실_환경의_재전송을_줄인다()
    {
        var withoutFec = RunLossyTransfer(useFec: false);
        var withFec = RunLossyTransfer(useFec: true);

        Assert.True(withFec.Recovered > 0,
            "FEC 를 켰는데 복원된 패킷이 하나도 없다.");

        Assert.True(withFec.Retransmits < withoutFec.Retransmits,
            $"FEC 가 재전송을 줄이지 못했다: FEC 없음 {withoutFec.Retransmits} vs FEC {withFec.Retransmits}");
    }

    private static (ulong Retransmits, long Recovered) RunLossyTransfer(bool useFec)
    {
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            if (useFec)
                options.Fec = new FecOptions { DataShards = 10, ParityShards = 3 };
            return options;
        }, seed: 99, delayMs: 20);

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

        var received = new List<int>();
        h.ServerSessions[0].ReliableReceived += (_, data) => received.Add(data.Length);

        var payload = TestData.DeterministicBytes(100 * 1024, seed: 21);
        Assert.Equal(KcpSendResult.Ok, h.Client.Send(payload));

        Assert.True(h.RunUntil(() => received.Count == 1, 120_000),
            $"전송이 끝나지 않았다(FEC={useFec}).");

        var stats = h.Client.Stats;
        var retransmits = stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments;
        return (retransmits, h.ServerSessions[0].FecRecoveredPackets);
    }

    [Fact]
    public void FEC_세션도_무손실에서_정상_동작한다()
    {
        using var h = new Harness(() =>
        {
            var options = BaseOptions();
            options.Fec = new FecOptions { DataShards = 4, ParityShards = 2 };
            return options;
        });

        Assert.True(h.Connect());

        var received = new List<byte[]>();
        h.ServerSessions[0].ReliableReceived += (_, data) => received.Add(data.ToArray());

        var payload = TestData.DeterministicBytes(32 * 1024, seed: 41);
        Assert.Equal(KcpSendResult.Ok, h.Client.Send(payload));

        Assert.True(h.RunUntil(() => received.Count == 1, 60_000));
        Assert.Equal(payload, received[0]);
    }

    [Fact]
    public void FEC와_암호화를_함께_켜면_설정_오류다()
    {
        var options = BaseOptions();
        options.EncryptionKey = NewKey(1);
        options.Fec = new FecOptions();

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    // ---------------- P5-3 관측성 ----------------

    [Fact]
    public void 서버가_주기적으로_관측_스냅숏을_낸다()
    {
        using var h = new Harness(BaseOptions);

        var snapshots = new List<KcpServerMetrics>();
        h.Server.MetricsSampled += m => snapshots.Add(m);

        Assert.True(h.Connect());

        var payload = TestData.DeterministicBytes(16 * 1024, seed: 51);
        h.ServerSessions[0].ReliableReceived += (s, data) => s.Send(data.Span);
        Assert.Equal(KcpSendResult.Ok, h.Client.Send(payload));

        Assert.True(h.RunUntil(() => snapshots.Count >= 2, 10_000));

        var last = snapshots[snapshots.Count - 1];
        Assert.Equal(1, last.SessionCount);
        Assert.True(last.SegmentsSent > 0);
        Assert.True(last.BytesReceived > 0);
        Assert.InRange(last.RetransmitRatePercent, 0, 100);
        Assert.True(last.RttP50 >= 0 && last.RttP95 >= last.RttP50 && last.RttP99 >= last.RttP95);
        Assert.Contains("세션 1", last.ToString());
    }

    [Fact]
    public void 세션이_없으면_스냅숏도_비어_있다()
    {
        using var h = new Harness(BaseOptions);

        var snapshots = new List<KcpServerMetrics>();
        h.Server.MetricsSampled += m => snapshots.Add(m);

        h.RunUntil(() => snapshots.Count >= 1, 5_000);

        Assert.NotEmpty(snapshots);
        Assert.Equal(0, snapshots[0].SessionCount);
        Assert.Equal(0, snapshots[0].RttP50);
        Assert.Equal(0.0, snapshots[0].RetransmitRatePercent);
    }
}
