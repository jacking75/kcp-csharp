using System;
using System.Diagnostics;
using KcpCSharpR1;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// P2-6 장시간·스트레스 테스트. 가상 시간이므로 실제 대기는 없지만 반복 횟수가 많아
/// <c>Category=Soak</c> 로 분리한다. 일반 CI 는 <c>--filter Category!=Soak</c> 로 제외하고,
/// 야간 스케줄 잡에서만 실행한다.
/// </summary>
[Trait("Category", "Soak")]
public class SoakTests
{
    /// <summary>FNV-1a 64비트 롤링 해시. 수백 MB 를 메모리에 쌓지 않고 무결성을 비교한다.</summary>
    private struct Fnv1a
    {
        private ulong hash;

        public static Fnv1a Create() => new Fnv1a { hash = 14695981039346656037UL };

        public void Append(byte[] data, int offset, int count)
        {
            var h = hash;
            for (var i = 0; i < count; i++)
            {
                h ^= data[offset + i];
                h *= 1099511628211UL;
            }
            hash = h;
        }

        public ulong Value => hash;
    }

    /// <summary>
    /// 가상 24시간, 손실 5% 링크에서 연속 송수신. 무결성·통계 일관성·풀 상한을 검증한다.
    /// </summary>
    [Fact]
    public void 가상24시간_손실5퍼센트에서_무결성과_풀상한을_지킨다()
    {
        const uint stepMs = 10;
        const uint hours = 24;
        const uint totalMs = hours * 60 * 60 * 1000;
        const int sendIntervalMs = 200;
        const int messageSize = 512;

        uint now = 0;
        var atob = new SimulatedLink(99) { DelayMs = 30, LossRate = 0.05 };
        var btoa = new SimulatedLink(100) { DelayMs = 30, LossRate = 0.05 };

        var sender = new Kcp(0x5A5A5A5A, (buf, len) => atob.Send(buf, len), () => now);
        var receiver = new Kcp(0x5A5A5A5A, (buf, len) => btoa.Send(buf, len), () => now);
        sender.NoDelay(1, 10, 2, 1);
        receiver.NoDelay(1, 10, 2, 1);
        sender.WndSize(128, 128);
        receiver.WndSize(128, 128);

        atob.Deliver = (buf, len) => receiver.Input(buf, 0, len, true, true);
        btoa.Deliver = (buf, len) => sender.Input(buf, 0, len, true, true);

        var sentHash = Fnv1a.Create();
        var recvHash = Fnv1a.Create();
        long sentBytes = 0, recvBytes = 0;
        var message = new byte[messageSize];
        var recvBuffer = new byte[64 * 1024];
        var rng = new Random(12345);
        uint nextSendAt = 0;
        var maxPoolCount = 0;

        var sw = Stopwatch.StartNew();

        while (now < totalMs)
        {
            now += stepMs;

            atob.Advance(now);
            btoa.Advance(now);

            if (Kcp._itimediff(now, nextSendAt) >= 0 && sender.WaitSnd < 64)
            {
                rng.NextBytes(message);
                if (sender.Send(message) == 0)
                {
                    sentHash.Append(message, 0, message.Length);
                    sentBytes += message.Length;
                }
                nextSendAt = now + sendIntervalMs;
            }

            sender.Update();
            receiver.Update();

            while (true)
            {
                var size = receiver.PeekSize();
                if (size < 0) break;
                var n = receiver.Recv(recvBuffer, 0, recvBuffer.Length);
                if (n < 0) break;
                recvHash.Append(recvBuffer, 0, n);
                recvBytes += n;
            }

            if (now % 60_000 == 0)
            {
                var pool = sender.SegmentPoolCount + receiver.SegmentPoolCount;
                if (pool > maxPoolCount) maxPoolCount = pool;
            }
        }

        sw.Stop();

        // 마지막 잔여분이 빠져나갈 시간을 준다.
        var drainDeadline = now + 60_000;
        while (Kcp._itimediff(drainDeadline, now) > 0 && sender.WaitSnd > 0)
        {
            now += stepMs;
            atob.Advance(now);
            btoa.Advance(now);
            sender.Update();
            receiver.Update();
            while (true)
            {
                var size = receiver.PeekSize();
                if (size < 0) break;
                var n = receiver.Recv(recvBuffer, 0, recvBuffer.Length);
                if (n < 0) break;
                recvHash.Append(recvBuffer, 0, n);
                recvBytes += n;
            }
        }

        Assert.Equal(sentBytes, recvBytes);
        Assert.Equal(sentHash.Value, recvHash.Value);
        Assert.Equal(0, sender.WaitSnd);
        Assert.False(sender.IsDeadLink);

        // 풀은 상한을 넘지 않아야 한다(진단 B-08).
        Assert.True(maxPoolCount <= Kcp.MaxSegmentPoolSize * 2,
            $"세그먼트 풀이 상한을 넘었다: {maxPoolCount} > {Kcp.MaxSegmentPoolSize * 2}");

        // 통계 일관성: 재전송이 있었고, 송수신 세그먼트 수가 0 이 아니다.
        var s = sender.GetStats();
        var r = receiver.GetStats();
        Assert.True(s.SegmentsSent > 0 && r.SegmentsReceived > 0);
        Assert.True(s.RetransmitSegments + s.FastRetransmitSegments + s.EarlyRetransmitSegments > 0,
            "손실 5% 인데 재전송이 없었다.");
        Assert.True(s.BytesSent > 0 && r.BytesReceived > 0);

        // P3-5: Release 후 재사용해도 예외가 없어야 한다.
        sender.Release();
        receiver.Release();
        Assert.Equal(0, sender.Send(message));
        sender.Release();
    }
}
