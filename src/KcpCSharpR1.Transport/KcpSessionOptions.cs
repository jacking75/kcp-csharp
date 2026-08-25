using System;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.Transport;

/// <summary>
/// 세션 수명과 KCP 파라미터 설정.
/// </summary>
/// <remarks>
/// 기본값은 로드맵 §7 P4-2a 의 표를 그대로 따른다. 장르별 권장값은 로드맵 12장 참조.
/// </remarks>
public sealed class KcpSessionOptions
{
    /// <summary>핸드셰이크 전체 제한 시간(ms). 기본 5초.</summary>
    public int HandshakeTimeoutMs { get; set; } = 5_000;

    /// <summary>SYN 재시도 간격(ms). 기본 1초.</summary>
    public int HandshakeRetryIntervalMs { get; set; } = 1_000;

    /// <summary>SYN 최대 재시도 횟수. 기본 3회.</summary>
    public int HandshakeMaxRetries { get; set; } = 3;

    /// <summary>유휴 시 PING 을 보내는 간격(ms). 기본 2초.</summary>
    public int PingIntervalMs { get; set; } = 2_000;

    /// <summary>마지막 수신 이후 이 시간이 지나면 Dead 로 본다(ms). 기본 10초.</summary>
    public int SessionTimeoutMs { get; set; } = 10_000;

    /// <summary>FIN 이후 잔여 재전송을 처리할 시간(ms). 기본 2초.</summary>
    public int FinLingerMs { get; set; } = 2_000;

    /// <summary>
    /// 송신 백로그 상한(세그먼트 수). <c>Kcp.WaitSnd</c> 가 이 값을 넘으면
    /// <see cref="KcpSession.Send(ReadOnlySpan{byte})"/> 가 <see cref="KcpSendResult.Backpressure"/> 를 반환한다.
    /// </summary>
    public int MaxSendBacklog { get; set; } = 1024;

    /// <summary>
    /// 게임 스레드용 송신 아웃박스의 용량(메시지 수). 0 이면 아웃박스를 만들지 않는다(진단 M-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0 보다 크면 <see cref="KcpSession.TrySendFromAnyThread(ReadOnlySpan{byte})"/> 를 쓸 수 있다.
    /// 어느 스레드에서든 넣을 수 있고, 소유 스레드가 <see cref="KcpSession.Update"/> 첫머리에서 꺼내
    /// KCP 에 넣는다.
    /// </para>
    /// <para>
    /// <b><see cref="MaxSendBacklog"/> 와 역할이 다르다.</b> 이 값은 "스레드 경계에서 대기하는 메시지 수"이고,
    /// <see cref="MaxSendBacklog"/> 는 "KCP 안에서 아직 ACK 를 못 받은 세그먼트 수"다.
    /// 아웃박스가 차면 <c>TrySendFromAnyThread</c> 가 false 를 돌려주므로(백프레셔),
    /// 게임 루프 한 틱에 넣을 메시지 수의 몇 배 정도로 잡는 것이 적당하다.
    /// </para>
    /// </remarks>
    public int SendOutboxCapacity { get; set; }

    /// <summary>KCP nodelay(0 끔 / 1 켬).</summary>
    public int NoDelay { get; set; } = 1;

    /// <summary>KCP 내부 갱신 간격(ms).</summary>
    public int IntervalMs { get; set; } = 10;

    /// <summary>KCP 빠른 재전송 임계값(중복 ACK 수). 0 이면 끔.</summary>
    public int FastResend { get; set; } = 2;

    /// <summary>KCP 혼잡 제어 끔(1) / 켬(0).</summary>
    public int NoCongestionControl { get; set; } = 1;

    /// <summary>송신 윈도(세그먼트 수).</summary>
    public int SendWindow { get; set; } = 128;

    /// <summary>수신 윈도(세그먼트 수).</summary>
    public int ReceiveWindow { get; set; } = 128;

    /// <summary>MTU(바이트). 프레이밍 1바이트를 포함한 UDP 페이로드 상한이다.</summary>
    public int Mtu { get; set; } = Kcp.IKCP_MTU_DEF;

    /// <summary>true 면 수신 즉시 ACK 를 내보낸다(지연↓ 대역폭↑).</summary>
    public bool AckNoDelay { get; set; } = true;

    /// <summary>true 면 KCP 스트림 모드(메시지 경계 없음).</summary>
    public bool StreamMode { get; set; }

    /// <summary>
    /// true 면 프레이밍 없이 순수 KCP 패킷만 주고받는다(kcp-go 등과 상호운용).
    /// 이 경우 핸드셰이크·PING·FIN 이 없으므로 conv 를 직접 지정해야 한다.
    /// </summary>
    public bool RawMode { get; set; }

    /// <summary>RawMode 에서 사용할 conv. RawMode 가 아니면 무시된다.</summary>
    public uint RawModeConv { get; set; }

    /// <summary>
    /// AES-256-GCM 세션 키(32바이트). 지정하면 KCP·RAW 프레임의 페이로드를 암호화한다(P5-1).
    /// </summary>
    /// <remarks>
    /// 라이브러리는 키 합의를 하지 않는다. 게임은 보통 TLS 웹 API 로그인에서 세션 키를 받아 오므로
    /// 그 값을 여기에 넣는다. 세션마다 conv 로 nonce 를 분리하므로 같은 키를 여러 세션이 공유해도 안전하지만,
    /// 세션별 키를 쓰는 편이 더 낫다.
    /// </remarks>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>
    /// 직접 구현한 패킷 암호화기(P5-1). 지정하면 <see cref="EncryptionKey"/> 보다 우선한다.
    /// netstandard2.1 처럼 <c>AesGcm</c> 을 쓸 수 없는 런타임에서 자체 구현을 주입하는 통로다.
    /// </summary>
    public IPacketCipher? Cipher { get; set; }

    /// <summary>FEC 설정(P5-2). null 이면 FEC 를 쓰지 않는다.</summary>
    public FecOptions? Fec { get; set; }

    /// <summary>암호화·FEC 를 고려한 패킷 오버헤드(바이트).</summary>
    internal int CipherOverhead => Cipher?.Overhead ?? (EncryptionKey != null ? AesGcmPacketCipher.OverheadBytes : 0);

    /// <summary>설정을 복사한다(서버가 세션마다 독립 사본을 갖도록).</summary>
    /// <returns>복사본.</returns>
    public KcpSessionOptions Clone() => (KcpSessionOptions)MemberwiseClone();

    /// <summary>값이 유효한지 검사한다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">범위를 벗어난 값이 있는 경우.</exception>
    public void Validate()
    {
        if (Mtu < 64 || Mtu > 65507)
            throw new ArgumentOutOfRangeException(nameof(Mtu), Mtu, "MTU 는 64~65507 이어야 합니다.");
        if (SessionTimeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(SessionTimeoutMs), SessionTimeoutMs, "0보다 커야 합니다.");
        if (PingIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(PingIntervalMs), PingIntervalMs, "0보다 커야 합니다.");
        if (MaxSendBacklog <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxSendBacklog), MaxSendBacklog, "0보다 커야 합니다.");
        if (HandshakeMaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(HandshakeMaxRetries), HandshakeMaxRetries, "0 이상이어야 합니다.");
        if (SendOutboxCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(SendOutboxCapacity), SendOutboxCapacity, "0 이상이어야 합니다.");
        if (EncryptionKey != null && EncryptionKey.Length != AesGcmPacketCipher.KeySize)
            throw new ArgumentOutOfRangeException(nameof(EncryptionKey), EncryptionKey.Length,
                $"AES-256-GCM 키는 {AesGcmPacketCipher.KeySize}바이트여야 합니다.");
        if (Fec != null && (EncryptionKey != null || Cipher != null))
            throw new ArgumentException("이 버전에서는 FEC 와 암호화를 함께 켤 수 없습니다(로드맵 P5-2 참조).", nameof(Fec));
        Fec?.Validate();
    }
}

/// <summary>서버 전용 설정.</summary>
public sealed class KcpServerOptions
{
    /// <summary>세션 공통 설정.</summary>
    public KcpSessionOptions Session { get; set; } = new KcpSessionOptions();

    /// <summary>동시 세션 상한. 초과하는 SYN 은 무응답으로 버린다.</summary>
    public int MaxSessions { get; set; } = 8192;

    /// <summary>수신 루프가 데이터를 기다리는 최대 시간(마이크로초). 스케줄러 해상도를 결정한다.</summary>
    public int PollMicroseconds { get; set; } = 5_000;

    /// <summary>한 번의 수신 배치에서 처리할 최대 패킷 수(타이머 굶주림 방지).</summary>
    public int MaxReceiveBatch { get; set; } = 1024;

    /// <summary>수신 버퍼(소켓 옵션) 크기.</summary>
    public int SocketReceiveBufferSize { get; set; } = 1 << 20;

    /// <summary>송신 버퍼(소켓 옵션) 크기.</summary>
    public int SocketSendBufferSize { get; set; } = 1 << 20;

    /// <summary>
    /// 관측 스냅숏(<see cref="KcpServer.MetricsSampled"/>) 주기(ms). 0 이하면 수집하지 않는다(P5-3).
    /// </summary>
    public int MetricsIntervalMs { get; set; } = 5_000;
}
