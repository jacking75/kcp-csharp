using System.Diagnostics;

namespace KcpCSharpR1;

/// <summary>
/// <see cref="Stopwatch"/> 기반의 단조(monotonic) 밀리초 시계.
/// </summary>
/// <remarks>
/// <para>
/// KCP 는 32비트 밀리초 타임스탬프로 재전송 시각과 RTT 를 계산하므로, 시계가 뒤로 흐르면
/// RTO 폭주·재전송 폭풍·연결 정지가 발생한다(진단 B-01). <c>DateTime.Now</c> 는 NTP 동기화,
/// 수동 시각 변경, 서머타임 전환에서 뒤로 흐를 수 있어 사용하지 않는다.
/// </para>
/// <para>
/// 반환값은 프로세스 기동 시점을 0 으로 하는 상대 시각이며, 약 49.7 일마다 랩어라운드한다.
/// 랩어라운드 자체는 정상이며, 시각 비교는 반드시 <c>(int)(a - b)</c> 형태의 부호 있는 차이로 해야 한다.
/// </para>
/// </remarks>
public static class KcpClock
{
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>현재 단조 밀리초 시각을 반환한다.</summary>
    public static uint NowMs => (uint)(long)((Stopwatch.GetTimestamp() - StartTimestamp) * TicksToMs);
}
