using System;
using System.Collections.Generic;

namespace KcpCSharpR1.Transport;

/// <summary>
/// 서버 상태의 주기 스냅숏(P5-3 관측성).
/// </summary>
/// <remarks>
/// 포맷을 강요하지 않는다. Prometheus·CloudWatch·자체 대시보드 등 어디로 보낼지는 사용자가 정한다.
/// <see cref="KcpServer.MetricsSampled"/> 이벤트로 받아 원하는 곳에 흘려 보내면 된다.
/// </remarks>
public readonly struct KcpServerMetrics
{
    /// <summary>스냅숏 시각(ms, 서버 시계 기준).</summary>
    public uint TimestampMs { get; init; }

    /// <summary>현재 세션 수.</summary>
    public int SessionCount { get; init; }

    /// <summary>전 세션이 지금까지 내보낸 세그먼트 총수.</summary>
    public ulong SegmentsSent { get; init; }

    /// <summary>전 세션의 재전송 세그먼트 총수(RTO + 빠른 재전송 + 조기 재전송).</summary>
    public ulong RetransmitSegments { get; init; }

    /// <summary>재전송률(%). <c>재전송 / 보낸 세그먼트</c>.</summary>
    public double RetransmitRatePercent =>
        SegmentsSent == 0 ? 0 : RetransmitSegments * 100.0 / SegmentsSent;

    /// <summary>RTT 중앙값(ms).</summary>
    public int RttP50 { get; init; }

    /// <summary>RTT 95 백분위(ms).</summary>
    public int RttP95 { get; init; }

    /// <summary>RTT 99 백분위(ms).</summary>
    public int RttP99 { get; init; }

    /// <summary>전 세션이 보낸 총 바이트.</summary>
    public ulong BytesSent { get; init; }

    /// <summary>전 세션이 받은 총 바이트.</summary>
    public ulong BytesReceived { get; init; }

    /// <summary>FEC 로 복원한 패킷 총수(FEC 를 쓰지 않으면 0).</summary>
    public long FecRecoveredPackets { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"세션 {SessionCount}, 재전송률 {RetransmitRatePercent:F2}%, RTT p50/p95/p99 = {RttP50}/{RttP95}/{RttP99}ms";
}

/// <summary>세션 목록에서 <see cref="KcpServerMetrics"/> 를 계산한다.</summary>
internal static class MetricsCollector
{
    /// <summary>스냅숏을 만든다.</summary>
    /// <param name="sessions">대상 세션들.</param>
    /// <param name="now">현재 시각(ms).</param>
    /// <param name="rttScratch">RTT 정렬에 재사용할 리스트(할당을 줄이기 위해 호출자가 준다).</param>
    /// <returns>스냅숏.</returns>
    public static KcpServerMetrics Collect(IReadOnlyCollection<KcpSession> sessions, uint now, List<int> rttScratch)
    {
        rttScratch.Clear();

        ulong segmentsSent = 0;
        ulong retransmit = 0;
        ulong bytesSent = 0;
        ulong bytesReceived = 0;
        long fecRecovered = 0;

        foreach (var session in sessions)
        {
            var stats = session.Stats;
            segmentsSent += stats.SegmentsSent;
            retransmit += stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments;
            bytesSent += stats.BytesSent;
            bytesReceived += stats.BytesReceived;
            fecRecovered += session.FecRecoveredPackets;

            if (stats.Srtt > 0)
                rttScratch.Add(stats.Srtt);
        }

        rttScratch.Sort();

        return new KcpServerMetrics
        {
            TimestampMs = now,
            SessionCount = sessions.Count,
            SegmentsSent = segmentsSent,
            RetransmitSegments = retransmit,
            BytesSent = bytesSent,
            BytesReceived = bytesReceived,
            FecRecoveredPackets = fecRecovered,
            RttP50 = Percentile(rttScratch, 0.50),
            RttP95 = Percentile(rttScratch, 0.95),
            RttP99 = Percentile(rttScratch, 0.99),
        };
    }

    /// <summary>정렬된 표본에서 백분위 값을 고른다(nearest-rank).</summary>
    private static int Percentile(List<int> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;

        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        if (rank < 1) rank = 1;
        if (rank > sorted.Count) rank = sorted.Count;
        return sorted[rank - 1];
    }
}
