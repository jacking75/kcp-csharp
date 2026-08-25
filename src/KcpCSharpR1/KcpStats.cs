namespace KcpCSharpR1;

/// <summary>
/// <see cref="Kcp"/> 인스턴스의 누적 통계 스냅숏.
/// </summary>
/// <remarks>
/// <see cref="Kcp.GetStats"/> 호출 시점의 값을 복사한 값 타입이다. 재전송률(<see cref="RetransmitSegments"/> /
/// <see cref="SegmentsSent"/>)과 <see cref="Srtt"/> 는 게임의 네트워크 품질 표시·로그에 그대로 쓸 수 있다.
/// </remarks>
public struct KcpStats
{
    /// <summary>output 콜백으로 내보낸 총 바이트 수(KCP 헤더 포함).</summary>
    public ulong BytesSent;

    /// <summary><see cref="Kcp.Input(byte[], int, int, bool, bool)"/> 로 들어온 총 바이트 수.</summary>
    public ulong BytesReceived;

    /// <summary>내보낸 총 세그먼트 수(데이터·ACK·윈도 프로브 전부 포함).</summary>
    public ulong SegmentsSent;

    /// <summary>수신해 해석한 총 세그먼트 수.</summary>
    public ulong SegmentsReceived;

    /// <summary>RTO 만료로 재전송한 세그먼트 수.</summary>
    public ulong RetransmitSegments;

    /// <summary>빠른 재전송(fast retransmit)으로 재전송한 세그먼트 수.</summary>
    public ulong FastRetransmitSegments;

    /// <summary>조기 재전송(early retransmit)으로 재전송한 세그먼트 수.</summary>
    public ulong EarlyRetransmitSegments;

    /// <summary>이미 받은 적 있는 중복 데이터 세그먼트를 다시 받은 횟수.</summary>
    public ulong RepeatSegments;

    /// <summary>현재 RTO(ms).</summary>
    public int CurrentRto;

    /// <summary>평활 RTT(ms).</summary>
    public int Srtt;

    /// <summary>송신 버퍼에 올라가 있는(전송 중인) 세그먼트 수.</summary>
    public int SendWindowUsage;

    /// <summary>아직 상대에게 확인되지 않은 전체 대기 세그먼트 수(<c>snd_buf + snd_queue</c>).</summary>
    public int WaitSend;
}
