using System;
using System.Collections.Generic;
using KcpCSharpR1;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// P2-4 프로토콜 시나리오 테스트 14종. 모두 가상 시간이므로 실제 대기가 없다.
/// </summary>
public class ScenarioTests
{
    private static IEnumerable<byte[]> Chunks(byte[] data, int size)
    {
        for (var i = 0; i < data.Length; i += size)
        {
            var n = Math.Min(size, data.Length - i);
            var chunk = new byte[n];
            Buffer.BlockCopy(data, i, chunk, 0, n);
            yield return chunk;
        }
    }

    /// <summary>#1 무손실 1만 메시지: 바이트 단위 일치, 순서 보존.</summary>
    [Fact]
    public void S01_무손실_1만메시지_전량_순서대로_도달()
    {
        var pair = KcpPair.Create(seed: 1, loss: 0, delayMs: 20);
        pair.AckNoDelay = true;

        const int count = 10_000;
        var expected = new List<byte>(count * 8);
        var pending = new Queue<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var msg = BitConverter.GetBytes(i);
            pending.Enqueue(msg);
            expected.AddRange(msg);
        }

        // 송신 윈도가 허용하는 만큼만 밀어 넣으면서 진행한다.
        pair.A.WndSize(256, 256);
        pair.B.WndSize(256, 256);

        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 128)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            // 마지막 바이트 도착만으로 끝내면 ACK 왕복 전에 멈춘다. 전량 확인(WaitSnd==0)까지 진행한다.
            return pending.Count == 0 && pair.ReceivedB.Length == expected.Count && pair.A.WaitSnd == 0;
        }, maxMs: 600_000);

        Assert.True(ok, $"도달 {pair.ReceivedB.Length} / 기대 {expected.Count}");
        Assert.Equal(expected.ToArray(), pair.ReceivedB.ToArray());
    }

    /// <summary>#2 손실 10/20/40%: 전량 도달하고 재전송 통계가 증가한다.</summary>
    [Theory]
    [InlineData(0.10)]
    [InlineData(0.20)]
    [InlineData(0.40)]
    public void S02_손실환경에서_전량_도달하고_재전송이_발생한다(double loss)
    {
        var pair = KcpPair.Create(seed: 42, loss: loss, delayMs: 50);
        pair.AckNoDelay = true;
        pair.A.WndSize(128, 128);
        pair.B.WndSize(128, 128);

        var payload = TestData.DeterministicBytes(64 * 1024, seed: 7);
        var pending = new Queue<byte[]>(Chunks(payload, 1024));

        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 64)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
        }, maxMs: 600_000);

        Assert.True(ok, $"도달 {pair.ReceivedB.Length} / 기대 {payload.Length} (손실률 {loss})");
        Assert.Equal(payload, pair.ReceivedB.ToArray());

        var stats = pair.A.GetStats();
        var retrans = stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments;
        Assert.True(retrans > 0, "손실 환경인데 재전송이 한 번도 없었다.");
    }

    /// <summary>#3 순서 뒤섞임 30%: 애플리케이션 수신 순서는 보존된다.</summary>
    [Fact]
    public void S03_순서뒤섞임_30퍼센트에서_수신순서가_보존된다()
    {
        var pair = KcpPair.Create(seed: 3, delayMs: 40, reorder: 0.30);
        pair.AckNoDelay = true;

        var payload = TestData.DeterministicBytes(32 * 1024, seed: 11);
        var pending = new Queue<byte[]>(Chunks(payload, 512));

        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 32)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
        }, maxMs: 300_000);

        Assert.True(ok);
        Assert.Equal(payload, pair.ReceivedB.ToArray());
    }

    /// <summary>#4 중복 20%: 애플리케이션은 1회만 받고 중복 통계가 증가한다.</summary>
    [Fact]
    public void S04_중복_20퍼센트에서_앱수신은_1회다()
    {
        var pair = KcpPair.Create(seed: 4, delayMs: 30, duplicate: 0.20);
        pair.AckNoDelay = true;

        var payload = TestData.DeterministicBytes(16 * 1024, seed: 13);
        var pending = new Queue<byte[]>(Chunks(payload, 512));

        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 32)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
        }, maxMs: 300_000);

        Assert.True(ok);
        Assert.Equal(payload, pair.ReceivedB.ToArray());
        Assert.True(pair.B.GetStats().RepeatSegments > 0, "중복 링크인데 중복 수신 통계가 0이다.");
    }

    /// <summary>#5 지연 200ms + 지터 100ms: RTO 가 상한 안에서 안정적으로 수렴한다.</summary>
    [Fact]
    public void S05_큰지연과_지터에서_RTO가_발산하지_않는다()
    {
        var pair = KcpPair.Create(seed: 5, delayMs: 200, jitterMs: 100);
        pair.AckNoDelay = true;

        var payload = TestData.DeterministicBytes(8 * 1024, seed: 17);
        var pending = new Queue<byte[]>(Chunks(payload, 512));

        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 32)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
        }, maxMs: 300_000);

        Assert.True(ok);
        Assert.Equal(payload, pair.ReceivedB.ToArray());

        // RTT 400ms 왕복이므로 srtt 는 그 부근, rto 는 상한(60초) 훨씬 아래여야 한다.
        Assert.InRange(pair.A.Srtt, 200, 900);
        Assert.InRange((int)pair.A.Rto, Kcp.IKCP_RTO_NDL, 5000);
    }

    /// <summary>#6 수신측이 Recv 를 멈추면 송신이 멈추고, 재개하면 다시 흐른다(윈도 프로브).</summary>
    [Fact]
    public void S06_수신윈도_0에서_송신정지_후_재개된다()
    {
        var pair = KcpPair.Create(seed: 6, delayMs: 20);
        pair.AckNoDelay = true;
        pair.DrainB = false;                 // B 는 애플리케이션 수신을 하지 않는다.
        pair.A.WndSize(64, 64);
        pair.B.WndSize(32, 32);

        var payload = TestData.DeterministicBytes(64 * 1024, seed: 19);
        var pending = new Queue<byte[]>(Chunks(payload, 512));

        // B 의 수신 큐가 가득 차 A 의 송신이 막힐 때까지 진행한다.
        pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 64)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pair.A.RmtWnd == 0;
        }, maxMs: 60_000);

        Assert.Equal(0u, pair.A.RmtWnd);

        var stalledLength = pair.ReceivedB.Length;
        pair.AdvanceTo(pair.Now + 2_000);
        Assert.Equal(stalledLength, pair.ReceivedB.Length);   // 정말로 멈춰 있다.

        // 수신 재개 → 윈도가 열리고 전량 도달해야 한다.
        pair.DrainB = true;
        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 64)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
        }, maxMs: 600_000);

        Assert.True(ok, $"재개 후 도달 {pair.ReceivedB.Length} / 기대 {payload.Length}");
        Assert.Equal(payload, pair.ReceivedB.ToArray());
    }

    /// <summary>#7 최대 메시지 크기 경계. rcv_wnd 이상 조각이면 Send 가 -2 를 반환한다(B-04 회귀).</summary>
    [Fact]
    public void S07_최대메시지_경계와_조각수_한도()
    {
        var pair = KcpPair.Create(seed: 7, delayMs: 20);
        pair.AckNoDelay = true;

        var mss = (int)pair.A.Mss;
        var rcvWnd = (int)pair.A.RcvWnd;

        // (rcv_wnd - 1) × mss 는 보낼 수 있어야 한다.
        var maxMessage = TestData.DeterministicBytes((rcvWnd - 1) * mss, seed: 23);
        Assert.Equal(0, pair.A.Send(maxMessage));

        var ok = pair.RunUntil(() => pair.ReceivedB.Length == maxMessage.Length, maxMs: 120_000);
        Assert.True(ok, $"도달 {pair.ReceivedB.Length} / 기대 {maxMessage.Length}");
        Assert.Equal(maxMessage, pair.ReceivedB.ToArray());

        // 조각 수가 수신 윈도 이상이면 영구 교착 대신 -2 를 반환해야 한다.
        var tooBig = new byte[rcvWnd * mss];
        Assert.Equal(-2, pair.A.Send(tooBig));

        // 길이 0 은 -1.
        Assert.Equal(-1, pair.A.Send(Array.Empty<byte>()));
    }

    /// <summary>#8 스트림 모드: 경계 없는 바이트열의 무결성.</summary>
    [Fact]
    public void S08_스트림모드_바이트열_무결성()
    {
        var pair = KcpPair.Create(seed: 8, delayMs: 30, loss: 0.05,
            configure: k => k.SetStreamMode(true));
        pair.AckNoDelay = true;
        pair.A.WndSize(128, 128);
        pair.B.WndSize(128, 128);

        var payload = TestData.DeterministicBytes(48 * 1024, seed: 29);
        var pending = new Queue<byte[]>(Chunks(payload, 300)); // mss 와 어긋난 크기로 쪼갠다.

        var ok = pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 64)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
        }, maxMs: 600_000);

        Assert.True(ok, $"도달 {pair.ReceivedB.Length} / 기대 {payload.Length}");
        Assert.Equal(payload, pair.ReceivedB.ToArray());
    }

    /// <summary>#9 상대 무응답: 재전송이 dead_link 를 넘으면 IsDeadLink 로 감지된다(B-03 회귀).</summary>
    [Fact]
    public void S09_상대_무응답이면_데드링크로_전이한다()
    {
        var pair = KcpPair.Create(seed: 9, delayMs: 20);

        Assert.False(pair.A.IsDeadLink);

        pair.AtoB.Blackhole = true;   // B 에게 아무것도 도달하지 않는다.
        Assert.Equal(0, pair.A.Send(new byte[] { 1, 2, 3 }));

        var ok = pair.RunUntil(() => pair.A.IsDeadLink, maxMs: 600_000);

        Assert.True(ok, "무응답 상대인데 데드링크로 전이하지 않았다.");
        Assert.Equal(Kcp.IKCP_STATE_DEAD_LINK, pair.A.State);
    }

    /// <summary>#10 0바이트 PUSH 를 받아도 수신 큐가 쌓이지 않는다(커밋 217ba37 회귀).</summary>
    [Fact]
    public void S10_0바이트_PUSH가_수신큐에_적체되지_않는다()
    {
        uint now = 0;
        var kcp = new Kcp(0x11223344, (_, _) => { }, () => now);

        var pkt = new byte[Kcp.IKCP_OVERHEAD];
        var offset = 0;
        offset += Kcp.ikcp_encode32u(pkt, offset, 0x11223344);
        offset += Kcp.ikcp_encode8u(pkt, offset, Kcp.IKCP_CMD_PUSH);
        offset += Kcp.ikcp_encode8u(pkt, offset, 0);      // frg
        offset += Kcp.ikcp_encode16u(pkt, offset, 32);    // wnd
        offset += Kcp.ikcp_encode32u(pkt, offset, 0);     // ts
        offset += Kcp.ikcp_encode32u(pkt, offset, 0);     // sn
        offset += Kcp.ikcp_encode32u(pkt, offset, 0);     // una
        Kcp.ikcp_encode32u(pkt, offset, 0);               // len = 0

        Assert.Equal(0, kcp.Input(pkt, 0, pkt.Length, true, false));

        // 0바이트 메시지가 큐에 들어와 있다 → 한 번 꺼내면 비어야 한다.
        Assert.Equal(0, kcp.PeekSize());
        Assert.Equal(0, kcp.Recv(new byte[16]));
        Assert.Equal(-1, kcp.PeekSize());
        Assert.Equal(-1, kcp.Recv(new byte[16]));
    }

    /// <summary>#11 시계가 랩어라운드(0xFFFF_FFFF → 0)를 넘어가도 정상 동작한다(B-01/B-02 회귀).</summary>
    [Fact]
    public void S11_시계_랩어라운드_구간에서_정상동작한다()
    {
        // 랩어라운드까지 256ms 남은 지점에서 시작한다.
        var pair = KcpPair.Create(seed: 11, loss: 0.20, delayMs: 40, startTime: 0xFFFF_FF00);
        pair.AckNoDelay = true;

        var first = TestData.DeterministicBytes(16 * 1024, seed: 31);
        Assert.True(SendAll(pair, first, chunk: 512, maxMs: 600_000),
            $"랩어라운드 전 도달 {pair.ReceivedB.Length} / 기대 {first.Length}");

        // 경계를 확실히 넘긴다(유휴 상태로 2초 진행).
        pair.AdvanceTo(pair.Now + 2_000);
        Assert.True(pair.Now < 0xFFFF_FF00, $"랩어라운드가 일어나지 않았다(now={pair.Now}).");

        // 경계를 넘긴 뒤에도 송수신·재전송이 정상이어야 한다.
        var second = TestData.DeterministicBytes(16 * 1024, seed: 37);
        Assert.True(SendAll(pair, second, chunk: 512, maxMs: 600_000),
            $"랩어라운드 후 도달 {pair.ReceivedB.Length} / 기대 {first.Length + second.Length}");

        var expected = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, expected, 0, first.Length);
        Buffer.BlockCopy(second, 0, expected, first.Length, second.Length);
        Assert.Equal(expected, pair.ReceivedB.ToArray());
        Assert.False(pair.A.IsDeadLink);
    }

    /// <summary>payload 전량을 보내고 상대가 전부 받아 확인할 때까지 진행한다.</summary>
    private static bool SendAll(KcpPair pair, byte[] payload, int chunk, uint maxMs)
    {
        var target = pair.ReceivedB.Length + payload.Length;
        var pending = new Queue<byte[]>(Chunks(payload, chunk));

        return pair.RunUntil(() =>
        {
            while (pending.Count > 0 && pair.A.WaitSnd < 32)
            {
                if (pair.A.Send(pending.Peek()) == 0) pending.Dequeue();
                else break;
            }
            return pending.Count == 0 && pair.ReceivedB.Length == target && pair.A.WaitSnd == 0;
        }, maxMs);
    }

    /// <summary>#12 conv 불일치 패킷은 -1 을 반환하고 상태를 오염시키지 않는다.</summary>
    [Fact]
    public void S12_conv_불일치_패킷은_거부된다()
    {
        uint now = 0;
        var kcp = new Kcp(0x11223344, (_, _) => { }, () => now);

        var pkt = new byte[Kcp.IKCP_OVERHEAD + 4];
        var offset = 0;
        offset += Kcp.ikcp_encode32u(pkt, offset, 0xDEADBEEF);   // 다른 conv
        offset += Kcp.ikcp_encode8u(pkt, offset, Kcp.IKCP_CMD_PUSH);
        offset += Kcp.ikcp_encode8u(pkt, offset, 0);
        offset += Kcp.ikcp_encode16u(pkt, offset, 32);
        offset += Kcp.ikcp_encode32u(pkt, offset, 0);
        offset += Kcp.ikcp_encode32u(pkt, offset, 0);
        offset += Kcp.ikcp_encode32u(pkt, offset, 0);
        Kcp.ikcp_encode32u(pkt, offset, 4);

        Assert.Equal(-1, kcp.Input(pkt, 0, pkt.Length, true, false));
        Assert.Equal(-1, kcp.PeekSize());
        Assert.Equal(0, kcp.WaitSnd);
        Assert.False(kcp.IsDeadLink);
    }

    /// <summary>#13 잘린 헤더·거짓 length·알 수 없는 cmd 를 넣어도 예외 없이 오류 코드만 반환한다.</summary>
    [Fact]
    public void S13_변조패킷에_예외없이_오류코드를_반환한다()
    {
        uint now = 0;
        var kcp = new Kcp(0x11223344, (_, _) => { }, () => now);

        // 헤더보다 짧은 패킷
        Assert.Equal(-1, kcp.Input(new byte[10], 0, 10, true, false));

        // 거짓 length (실제 페이로드 없음)
        var liar = new byte[Kcp.IKCP_OVERHEAD];
        var offset = 0;
        offset += Kcp.ikcp_encode32u(liar, offset, 0x11223344);
        offset += Kcp.ikcp_encode8u(liar, offset, Kcp.IKCP_CMD_PUSH);
        offset += Kcp.ikcp_encode8u(liar, offset, 0);
        offset += Kcp.ikcp_encode16u(liar, offset, 32);
        offset += Kcp.ikcp_encode32u(liar, offset, 0);
        offset += Kcp.ikcp_encode32u(liar, offset, 0);
        offset += Kcp.ikcp_encode32u(liar, offset, 0);
        Kcp.ikcp_encode32u(liar, offset, 9999);
        Assert.Equal(-2, kcp.Input(liar, 0, liar.Length, true, false));

        // 알 수 없는 cmd
        var badCmd = (byte[])liar.Clone();
        Kcp.ikcp_encode8u(badCmd, 4, 200);
        Kcp.ikcp_encode32u(badCmd, 20, 0);
        Assert.Equal(-3, kcp.Input(badCmd, 0, badCmd.Length, true, false));

        // 무작위 쓰레기(conv 만 맞춘다)
        var rng = new Random(1234);
        for (var i = 0; i < 200; i++)
        {
            var junk = new byte[rng.Next(Kcp.IKCP_OVERHEAD, 200)];
            rng.NextBytes(junk);
            Kcp.ikcp_encode32u(junk, 0, 0x11223344);
            var ret = kcp.Input(junk, 0, junk.Length, true, false);
            Assert.True(ret == 0 || ret == -1 || ret == -2 || ret == -3, $"예상 밖 반환값 {ret}");
        }

        Assert.False(kcp.IsDeadLink);
    }

    /// <summary>#14 ackNoDelay 를 켜면 ACK 가 interval 을 기다리지 않고 즉시 나간다.</summary>
    [Fact]
    public void S14_ackNoDelay가_ACK_지연을_줄인다()
    {
        static uint MeasureAckDelay(bool ackNoDelay)
        {
            var pair = KcpPair.Create(seed: 14, delayMs: 10, conv: 0x2222);
            pair.AckNoDelay = ackNoDelay;
            // interval 을 크게 잡아 "Update 를 기다렸는지" 여부가 드러나게 한다.
            pair.A.NoDelay(0, 100, 0, 1);
            pair.B.NoDelay(0, 100, 0, 1);

            pair.A.Send(new byte[] { 1, 2, 3, 4 });

            var sentAt = pair.Now;
            pair.RunUntil(() => pair.A.WaitSnd == 0, maxMs: 5_000);
            return pair.Now - sentAt;
        }

        var withNoDelay = MeasureAckDelay(true);
        var withoutNoDelay = MeasureAckDelay(false);

        Assert.True(withNoDelay < withoutNoDelay,
            $"ackNoDelay 켠 쪽이 더 빨라야 한다. on={withNoDelay}ms off={withoutNoDelay}ms");
        Assert.True(withNoDelay < Kcp.IKCP_INTERVAL,
            $"ackNoDelay 인데 ACK 왕복이 interval({Kcp.IKCP_INTERVAL}ms) 이상 걸렸다: {withNoDelay}ms");
    }
}
