using System;
using System.Collections.Generic;
using KcpProject;

namespace KcpProject.Tests;

/// <summary>공개 API 표면(설정·통계·로깅·수명) 동작 검증.</summary>
public class KcpApiTests
{
    /// <summary>손실 없이 즉시 배달하는 두 Kcp + 가상 시계.</summary>
    private sealed class Loopback
    {
        public readonly Kcp A;
        public readonly Kcp B = null!;
        public uint Now;
        public readonly List<string> LogA = new();

        public Loopback(uint conv = 0x1234)
        {
            A = new Kcp(conv, (buf, len) => B!.Input(buf, 0, len, true, true), () => Now);
            B = new Kcp(conv, (buf, len) => A!.Input(buf, 0, len, true, true), () => Now);
            A.NoDelay(1, 10, 2, 1);
            B.NoDelay(1, 10, 2, 1);
        }

        public void Tick(uint stepMs = 10)
        {
            Now += stepMs;
            A.Update();
            B.Update();
        }

        public byte[] DrainB()
        {
            var buffer = new byte[64 * 1024];
            var n = B.Recv(buffer);
            return n <= 0 ? Array.Empty<byte>() : buffer[..n];
        }
    }

    [Fact]
    public void 생성자는_output이_null이면_예외()
    {
        Assert.Throws<ArgumentNullException>(() => new Kcp(1, null!));
    }

    [Fact]
    public void 기본값이_원본_kcp와_같다()
    {
        var kcp = new Kcp(7, (_, _) => { }, () => 0);

        Assert.Equal(7u, kcp.Conv);
        Assert.Equal((uint)Kcp.IKCP_WND_SND, kcp.SndWnd);
        Assert.Equal((uint)Kcp.IKCP_WND_RCV, kcp.RcvWnd);
        Assert.Equal((uint)Kcp.IKCP_WND_RCV, kcp.RmtWnd);
        Assert.Equal((uint)(Kcp.IKCP_MTU_DEF - Kcp.IKCP_OVERHEAD), kcp.Mss);
        Assert.Equal((uint)Kcp.IKCP_RTO_DEF, kcp.Rto);
        Assert.Equal((uint)Kcp.IKCP_INTERVAL, kcp.Interval);
        Assert.Equal(0, kcp.Srtt);
        Assert.Equal(0, kcp.RttVar);
        Assert.Equal(0u, kcp.State);
        Assert.False(kcp.IsDeadLink);
        Assert.Equal(0, kcp.WaitSnd);
    }

    [Fact]
    public void SetMtu는_mss를_다시_계산한다()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);

        Assert.Equal(0, kcp.SetMtu(600));
        Assert.Equal(600u - Kcp.IKCP_OVERHEAD, kcp.Mss);

        // 너무 작은 MTU 는 거부한다.
        Assert.Equal(-1, kcp.SetMtu(10));
        Assert.Equal(-1, kcp.SetMtu(-5));
        Assert.Equal(600u - Kcp.IKCP_OVERHEAD, kcp.Mss);
    }

    [Fact]
    public void ReserveBytes는_예약분만큼_mss를_줄인다()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);

        Assert.True(kcp.ReserveBytes(24));
        Assert.Equal((uint)(Kcp.IKCP_MTU_DEF - Kcp.IKCP_OVERHEAD - 24), kcp.Mss);

        Assert.False(kcp.ReserveBytes(-1));
        Assert.False(kcp.ReserveBytes(Kcp.IKCP_MTU_DEF));
    }

    [Fact]
    public void NoDelay는_interval을_10에서_5000으로_제한한다()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);

        kcp.NoDelay(1, 1, 2, 1);
        Assert.Equal(10u, kcp.Interval);

        kcp.NoDelay(1, 99999, 2, 1);
        Assert.Equal(5000u, kcp.Interval);

        // 음수는 "변경 없음"이다.
        kcp.NoDelay(-1, -1, -1, -1);
        Assert.Equal(5000u, kcp.Interval);
    }

    [Fact]
    public void WndSize는_0이하_값을_무시한다()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);

        kcp.WndSize(128, 256);
        Assert.Equal(128u, kcp.SndWnd);
        Assert.Equal(256u, kcp.RcvWnd);

        kcp.WndSize(0, -1);
        Assert.Equal(128u, kcp.SndWnd);
        Assert.Equal(256u, kcp.RcvWnd);
    }

    [Fact]
    public void Recv는_버퍼가_작으면_마이너스2를_반환한다()
    {
        var link = new Loopback();
        Assert.Equal(0, link.A.Send(new byte[100]));

        for (var i = 0; i < 10 && link.B.PeekSize() < 0; i++)
            link.Tick();

        Assert.Equal(100, link.B.PeekSize());
        Assert.Equal(-2, link.B.Recv(new byte[10]));
        Assert.Equal(100, link.B.Recv(new byte[100]));
        Assert.Equal(-1, link.B.Recv(new byte[100]));
    }

    [Fact]
    public void byte배열_오버로드와_Span_오버로드가_같은_결과를_낸다()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var link1 = new Loopback();
        Assert.Equal(0, link1.A.Send(payload, 2, 4));
        for (var i = 0; i < 10 && link1.B.PeekSize() < 0; i++) link1.Tick();
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, link1.DrainB());

        var link2 = new Loopback();
        Assert.Equal(0, link2.A.Send(payload.AsSpan(2, 4)));
        for (var i = 0; i < 10 && link2.B.PeekSize() < 0; i++) link2.Tick();
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, link2.DrainB());
    }

    [Fact]
    public void 스트림모드는_메시지_경계를_없앤다()
    {
        var link = new Loopback();
        link.A.SetStreamMode(true);
        link.B.SetStreamMode(true);

        Assert.Equal(0, link.A.Send(new byte[] { 1, 2, 3 }));
        Assert.Equal(0, link.A.Send(new byte[] { 4, 5 }));

        for (var i = 0; i < 20 && link.B.PeekSize() < 0; i++) link.Tick();

        // 두 번 보낸 것이 한 덩어리로 이어져 온다.
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, link.DrainB());
    }

    [Fact]
    public void 통계는_송수신을_누적한다()
    {
        var link = new Loopback();
        for (var i = 0; i < 5; i++)
            Assert.Equal(0, link.A.Send(new byte[200]));

        for (var i = 0; i < 40; i++) { link.Tick(); link.DrainB(); }

        var a = link.A.GetStats();
        var b = link.B.GetStats();

        Assert.True(a.BytesSent > 0);
        Assert.True(a.SegmentsSent >= 5);
        Assert.True(b.BytesReceived > 0);
        Assert.True(b.SegmentsReceived >= 5);
        Assert.Equal(link.A.WaitSnd, a.WaitSend);
        Assert.Equal((int)link.A.Rto, a.CurrentRto);
        Assert.Equal(link.A.Srtt, a.Srtt);
    }

    [Fact]
    public void 로거를_붙이면_로그가_남고_떼면_멈춘다()
    {
        var logs = new List<string>();
        var link = new Loopback();
        link.A.SetLogger(logs.Add);
        link.A.SetLogMask(Kcp.IKCP_LOG_OUTPUT | Kcp.IKCP_LOG_INPUT | Kcp.IKCP_LOG_SEND |
                          Kcp.IKCP_LOG_RECV | Kcp.IKCP_LOG_IN_DATA | Kcp.IKCP_LOG_IN_ACK |
                          Kcp.IKCP_LOG_IN_PROBE | Kcp.IKCP_LOG_IN_WINS | Kcp.IKCP_LOG_OUT_DATA |
                          Kcp.IKCP_LOG_OUT_ACK | Kcp.IKCP_LOG_OUT_PROBE | Kcp.IKCP_LOG_OUT_WINS);
        link.B.SetLogger(logs.Add);
        link.B.SetLogMask(Kcp.IKCP_LOG_IN_DATA | Kcp.IKCP_LOG_OUT_ACK | Kcp.IKCP_LOG_OUTPUT);

        Assert.Equal(0, link.A.Send(new byte[300]));
        for (var i = 0; i < 30; i++)
        {
            link.Tick();
            link.A.Recv(new byte[1024]);
            link.DrainB();
        }

        Assert.NotEmpty(logs);

        var before = logs.Count;
        link.A.SetLogger(null);
        link.B.SetLogger(null);
        link.A.Send(new byte[100]);
        for (var i = 0; i < 20; i++) { link.Tick(); link.DrainB(); }

        Assert.Equal(before, logs.Count);
    }

    /// <summary>상대 윈도가 0 이면 일정 시간 후 윈도 프로브(WASK)를 보내야 한다.</summary>
    [Fact]
    public void 상대윈도가_0이면_윈도_프로브를_보낸다()
    {
        var sentCommands = new List<byte>();
        uint now = 0;
        var kcp = new Kcp(0x2020, (buf, len) =>
        {
            for (var offset = 0; offset + Kcp.IKCP_OVERHEAD <= len;)
            {
                sentCommands.Add(buf[offset + 4]);
                uint segLen = 0;
                Kcp.ikcp_decode32u(buf, offset + 20, ref segLen);
                offset += Kcp.IKCP_OVERHEAD + (int)segLen;
            }
        }, () => now);
        kcp.NoDelay(1, 10, 2, 1);

        // 상대가 "내 윈도는 0" 이라고 알려 온다.
        var wins = new byte[Kcp.IKCP_OVERHEAD];
        var o = 0;
        o += Kcp.ikcp_encode32u(wins, o, 0x2020);
        o += Kcp.ikcp_encode8u(wins, o, Kcp.IKCP_CMD_WINS);
        o += Kcp.ikcp_encode8u(wins, o, 0);
        o += Kcp.ikcp_encode16u(wins, o, 0);      // wnd = 0
        o += Kcp.ikcp_encode32u(wins, o, 0);
        o += Kcp.ikcp_encode32u(wins, o, 0);
        o += Kcp.ikcp_encode32u(wins, o, 0);
        Kcp.ikcp_encode32u(wins, o, 0);
        Assert.Equal(0, kcp.Input(wins, 0, wins.Length, true, false));
        Assert.Equal(0u, kcp.RmtWnd);

        kcp.Send(new byte[64]);

        // IKCP_PROBE_INIT(7초)를 넘겨 진행시킨다.
        for (var i = 0; i < 900; i++)
        {
            now += 10;
            kcp.Update();
        }

        Assert.Contains((byte)Kcp.IKCP_CMD_WASK, sentCommands);
    }

    /// <summary>WASK 를 받으면 WINS 로 답해야 한다.</summary>
    [Fact]
    public void 윈도_질의를_받으면_윈도를_알려준다()
    {
        var sentCommands = new List<byte>();
        uint now = 0;
        var kcp = new Kcp(0x3030, (buf, len) =>
        {
            for (var offset = 0; offset + Kcp.IKCP_OVERHEAD <= len;)
            {
                sentCommands.Add(buf[offset + 4]);
                uint segLen = 0;
                Kcp.ikcp_decode32u(buf, offset + 20, ref segLen);
                offset += Kcp.IKCP_OVERHEAD + (int)segLen;
            }
        }, () => now);

        var wask = new byte[Kcp.IKCP_OVERHEAD];
        var o = 0;
        o += Kcp.ikcp_encode32u(wask, o, 0x3030);
        o += Kcp.ikcp_encode8u(wask, o, Kcp.IKCP_CMD_WASK);
        o += Kcp.ikcp_encode8u(wask, o, 0);
        o += Kcp.ikcp_encode16u(wask, o, 32);
        o += Kcp.ikcp_encode32u(wask, o, 0);
        o += Kcp.ikcp_encode32u(wask, o, 0);
        o += Kcp.ikcp_encode32u(wask, o, 0);
        Kcp.ikcp_encode32u(wask, o, 0);

        Assert.Equal(0, kcp.Input(wask, 0, wask.Length, true, false));
        now += 10;
        kcp.Update();

        Assert.Contains((byte)Kcp.IKCP_CMD_WINS, sentCommands);
    }

    [Fact]
    public void Flush_ackOnly는_interval을_반환한다()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);
        kcp.NoDelay(1, 20, 2, 1);

        Assert.Equal(20u, kcp.Flush(true));
    }

    [Fact]
    public void Check는_Update전에는_현재시각을_반환한다()
    {
        uint now = 12345;
        var kcp = new Kcp(1, (_, _) => { }, () => now);

        Assert.Equal(now, kcp.Check());

        kcp.Update();
        var next = kcp.Check();
        Assert.True(Kcp._itimediff(next, now) >= 0);
    }

    [Fact]
    public void Release후에도_다시_사용할_수_있다()
    {
        var link = new Loopback();
        Assert.Equal(0, link.A.Send(new byte[500]));
        link.Tick();

        link.A.Release();
        link.B.Release();

        Assert.Equal(0, link.A.WaitSnd);
        Assert.Equal(0, link.A.Send(new byte[10]));

        link.A.Release();
    }

    [Fact]
    public void KcpClock은_단조_증가한다()
    {
        var a = KcpClock.NowMs;
        for (var i = 0; i < 1000; i++)
        {
            var b = KcpClock.NowMs;
            Assert.True(Kcp._itimediff(b, a) >= 0, $"시계가 뒤로 갔다: {a} -> {b}");
            a = b;
        }
    }

    [Fact]
    public void 구이름_KCP_별칭도_동작한다()
    {
#pragma warning disable CS0618 // 하위 호환 확인이 목적이다.
        var legacy = new KCP(99, (_, _) => { });
#pragma warning restore CS0618

        Assert.IsAssignableFrom<Kcp>(legacy);
        Assert.Equal(99u, legacy.Conv);
        Assert.Equal(0, legacy.Send(new byte[8]));
    }

    [Fact]
    public void Update를_오래_건너뛰어도_복구한다()
    {
        uint now = 0;
        var outputs = 0;
        var kcp = new Kcp(1, (_, _) => outputs++, () => now);
        kcp.NoDelay(1, 10, 2, 1);

        kcp.Update();
        kcp.Send(new byte[64]);

        // 10초를 훌쩍 넘겨 버린 상황(ts_flush 리셋 경로)
        now += 60_000;
        kcp.Update();

        Assert.True(outputs > 0);
    }
}
