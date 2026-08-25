using System;

namespace LoadTest;

/// <summary>
/// 고정 크기 지연 히스토그램.
/// </summary>
/// <remarks>
/// <para>
/// 표본을 리스트에 그대로 쌓으면 측정 자체가 GC 를 유발한다. 실제로 1,000세션·60초 실행에서
/// RTT 표본이 96만 개까지 늘어 <b>측정 도구 때문에</b> Gen2 수집이 일어났다.
/// (측정 대상이 아니라 측정기가 만든 노이즈다.)
/// </para>
/// <para>
/// 그래서 1ms 폭 버킷 2,048개(0~2,047ms)로 고정한다. 세션당 8KB, 1,000세션이면 8MB 로 항상 일정하고
/// 실행 중 추가 할당이 없다. 백분위는 누적합으로 구하며 정확도는 1ms 다.
/// </para>
/// </remarks>
internal sealed class LatencyHistogram
{
    private const int BucketCount = 2048;

    private readonly int[] buckets = new int[BucketCount];
    private long overflow;

    /// <summary>기록한 표본 수.</summary>
    public long Count { get; private set; }

    /// <summary>가장 큰 표본(ms).</summary>
    public double Max { get; private set; }

    /// <summary>표본을 하나 기록한다.</summary>
    /// <param name="milliseconds">지연(ms).</param>
    public void Record(double milliseconds)
    {
        Count++;
        if (milliseconds > Max) Max = milliseconds;

        var index = (int)milliseconds;
        if (index < 0) index = 0;

        if (index >= BucketCount) overflow++;
        else buckets[index]++;
    }

    /// <summary>다른 히스토그램을 흡수한다(세션별 결과를 합칠 때).</summary>
    /// <param name="other">합칠 대상.</param>
    public void Merge(LatencyHistogram other)
    {
        for (var i = 0; i < BucketCount; i++)
            buckets[i] += other.buckets[i];

        overflow += other.overflow;
        Count += other.Count;
        if (other.Max > Max) Max = other.Max;
    }

    /// <summary>백분위 값을 구한다.</summary>
    /// <param name="percentile">0.0~1.0.</param>
    /// <returns>밀리초. 표본이 없으면 0.</returns>
    public double Percentile(double percentile)
    {
        if (Count == 0) return 0;

        var target = (long)Math.Ceiling(percentile * Count);
        if (target < 1) target = 1;

        long cumulative = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            cumulative += buckets[i];
            if (cumulative >= target) return i;
        }

        // 버킷 범위를 넘은 표본이 있는 구간
        return Max;
    }
}
