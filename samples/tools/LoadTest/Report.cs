using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LoadTest;

/// <summary>
/// 부하 테스트 결과 리포트.
/// </summary>
/// <remarks>
/// 로드맵 P7-6 의 항목을 그대로 담는다: 처리량(Mbps), 메시지/초, 재전송률,
/// RTT p50/p95/p99, GC 수집 횟수(Gen0/1/2), 프로세스 메모리.
/// </remarks>
internal sealed class Report
{
    /// <summary>측정에 걸린 실제 시간(초).</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>연결에 성공한 세션 수.</summary>
    public int Sessions { get; init; }

    /// <summary>보낸 애플리케이션 메시지 수.</summary>
    public long MessagesSent { get; init; }

    /// <summary>에코로 돌아온 메시지 수.</summary>
    public long MessagesEchoed { get; init; }

    /// <summary>내용이 어긋난 메시지 수(0 이어야 정상).</summary>
    public long Mismatched { get; init; }

    /// <summary>KCP 가 내보낸 총 바이트(헤더 포함).</summary>
    public ulong BytesSent { get; init; }

    /// <summary>KCP 가 받은 총 바이트(헤더 포함).</summary>
    public ulong BytesReceived { get; init; }

    /// <summary>보낸 세그먼트 총수.</summary>
    public ulong SegmentsSent { get; init; }

    /// <summary>재전송한 세그먼트 총수.</summary>
    public ulong RetransmitSegments { get; init; }

    /// <summary>GC 수집 횟수 증가분.</summary>
    public int Gen0 { get; init; }

    /// <summary>GC 수집 횟수 증가분.</summary>
    public int Gen1 { get; init; }

    /// <summary>GC 수집 횟수 증가분.</summary>
    public int Gen2 { get; init; }

    /// <summary>측정 종료 시점의 관리 힙 크기(바이트).</summary>
    public long ManagedHeapBytes { get; init; }

    /// <summary>측정 종료 시점의 프로세스 작업 집합(바이트).</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>RTT 표본(ms). 정렬된 상태로 들어온다.</summary>
    public IReadOnlyList<double> RttSamples { get; init; } = Array.Empty<double>();

    /// <summary>초당 메시지 수.</summary>
    public double MessagesPerSecond => ElapsedSeconds <= 0 ? 0 : MessagesSent / ElapsedSeconds;

    /// <summary>송신 처리량(Mbps).</summary>
    public double SendMbps => ElapsedSeconds <= 0 ? 0 : BytesSent * 8.0 / ElapsedSeconds / 1_000_000.0;

    /// <summary>수신 처리량(Mbps).</summary>
    public double ReceiveMbps => ElapsedSeconds <= 0 ? 0 : BytesReceived * 8.0 / ElapsedSeconds / 1_000_000.0;

    /// <summary>재전송률(%).</summary>
    public double RetransmitRatePercent => SegmentsSent == 0 ? 0 : RetransmitSegments * 100.0 / SegmentsSent;

    /// <summary>에코 손실률(%). 신뢰 채널이면 0 이어야 한다.</summary>
    public double EchoLossPercent => MessagesSent == 0 ? 0 : (MessagesSent - MessagesEchoed) * 100.0 / MessagesSent;

    /// <summary>백분위 RTT(ms).</summary>
    /// <param name="percentile">0.0~1.0.</param>
    /// <returns>밀리초. 표본이 없으면 0.</returns>
    public double Rtt(double percentile)
    {
        if (RttSamples.Count == 0) return 0;

        var rank = (int)Math.Ceiling(percentile * RttSamples.Count);
        if (rank < 1) rank = 1;
        if (rank > RttSamples.Count) rank = RttSamples.Count;
        return RttSamples[rank - 1];
    }

    /// <summary>사람이 읽는 리포트를 출력한다.</summary>
    /// <param name="options">실행 옵션(설정을 함께 찍는다).</param>
    public void Print(Options options)
    {
        var channel = options.Unreliable ? "비신뢰(RAW)" : "신뢰(KCP)";

        Console.WriteLine();
        Console.WriteLine("┌───────────────── LoadTest 결과 ─────────────────");
        Console.WriteLine($"│ 설정      세션 {options.Sessions}, {options.Rate}msg/s, {options.Size}B, {channel}, 프리셋 {options.Preset}");
        Console.WriteLine($"│ 측정 시간 {ElapsedSeconds:F1}초 (연결 성공 {Sessions}/{options.Sessions})");
        Console.WriteLine("├─────────────────────────────────────────────────");
        Console.WriteLine($"│ 메시지    보냄 {MessagesSent:N0} / 에코 {MessagesEchoed:N0} ({MessagesPerSecond:N0} msg/s)");
        Console.WriteLine($"│ 무결성    불일치 {Mismatched:N0}건, 에코 손실 {EchoLossPercent:F2}%");
        Console.WriteLine($"│ 처리량    송신 {SendMbps:F2} Mbps / 수신 {ReceiveMbps:F2} Mbps");
        Console.WriteLine($"│ 재전송    {RetransmitSegments:N0} / {SegmentsSent:N0} 세그먼트 ({RetransmitRatePercent:F2}%)");
        Console.WriteLine($"│ RTT       p50 {Rtt(0.50):F1}ms  p95 {Rtt(0.95):F1}ms  p99 {Rtt(0.99):F1}ms  (표본 {RttSamples.Count:N0})");
        Console.WriteLine($"│ GC        Gen0 {Gen0} / Gen1 {Gen1} / Gen2 {Gen2}");
        Console.WriteLine($"│ 메모리    관리 힙 {ManagedHeapBytes / 1024.0 / 1024.0:F1} MB, 작업 집합 {WorkingSetBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine("└─────────────────────────────────────────────────");

        if (RetransmitRatePercent > 5)
            Console.WriteLine("! 재전송률이 5%를 넘습니다. 회선 품질이나 서버 처리 지연을 의심하세요(책 17장).");
        if (Gen2 > 0)
            Console.WriteLine($"! Gen2 수집이 {Gen2}회 있었습니다. 게임 서버에서는 프레임 스파이크로 나타납니다(책 14장).");
        if (Mismatched > 0)
            Console.WriteLine("! 내용 불일치가 있습니다. 심각한 버그이므로 반드시 원인을 찾으세요.");
    }

    /// <summary>결과를 CSV 로 저장한다(파일이 있으면 헤더 없이 덧붙인다).</summary>
    /// <param name="path">파일 경로.</param>
    /// <param name="options">실행 옵션.</param>
    public void WriteCsv(string path, Options options)
    {
        var exists = File.Exists(path);
        var builder = new StringBuilder();

        if (!exists)
        {
            builder.AppendLine(
                "timestamp,sessions,rate,size,channel,preset,seconds,msg_sent,msg_echoed,mismatched," +
                "send_mbps,recv_mbps,retransmit_pct,rtt_p50,rtt_p95,rtt_p99,gen0,gen1,gen2,heap_mb,ws_mb");
        }

        var culture = CultureInfo.InvariantCulture;
        builder.Append(DateTime.Now.ToString("s", culture)).Append(',')
               .Append(options.Sessions).Append(',')
               .Append(options.Rate).Append(',')
               .Append(options.Size).Append(',')
               .Append(options.Unreliable ? "unreliable" : "reliable").Append(',')
               .Append(options.Preset).Append(',')
               .Append(ElapsedSeconds.ToString("F2", culture)).Append(',')
               .Append(MessagesSent).Append(',')
               .Append(MessagesEchoed).Append(',')
               .Append(Mismatched).Append(',')
               .Append(SendMbps.ToString("F3", culture)).Append(',')
               .Append(ReceiveMbps.ToString("F3", culture)).Append(',')
               .Append(RetransmitRatePercent.ToString("F3", culture)).Append(',')
               .Append(Rtt(0.50).ToString("F2", culture)).Append(',')
               .Append(Rtt(0.95).ToString("F2", culture)).Append(',')
               .Append(Rtt(0.99).ToString("F2", culture)).Append(',')
               .Append(Gen0).Append(',')
               .Append(Gen1).Append(',')
               .Append(Gen2).Append(',')
               .Append((ManagedHeapBytes / 1024.0 / 1024.0).ToString("F1", culture)).Append(',')
               .Append((WorkingSetBytes / 1024.0 / 1024.0).ToString("F1", culture));
        builder.AppendLine();

        File.AppendAllText(path, builder.ToString());
        Console.WriteLine($"CSV 저장: {Path.GetFullPath(path)}");
    }

    /// <summary>측정 구간의 GC·메모리 스냅숏을 뜬다.</summary>
    /// <returns>(gen0, gen1, gen2, 관리 힙, 작업 집합).</returns>
    public static (int Gen0, int Gen1, int Gen2, long Heap, long WorkingSet) SampleRuntime()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalMemory(false), process.WorkingSet64);
    }
}
