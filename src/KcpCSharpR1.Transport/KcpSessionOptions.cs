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
    /// 송신 백로그 상한(세그먼트 수). 새 메시지의 조각까지 포함해 이 값을 넘으면
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
    /// 그 값을 여기에 넣는다. nonce 는 conv·송신 방향·카운터로 구성되므로 같은 키로 같은 conv 를
    /// 다시 사용하면 안 된다. 서버 재시작이나 conv 순환 전에는 새 키가 필요하며 서버 인스턴스마다
    /// 서로 다른 키를 사용해야 한다. 세션마다 별도 키를 쓰는 편이 더 낫다.
    /// </remarks>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>
    /// 단일 연결에 직접 주입하는 패킷 암호화기. 연결 종료 시 IDisposable 구현을 해제한다.
    /// 서버 또는 재연결에는 세션마다 새 인스턴스를 만드는 <see cref="CipherFactory"/> 를 사용한다.
    /// <see cref="EncryptionKey"/> 및 <see cref="CipherFactory"/> 와 함께 지정할 수 없다.
    /// </summary>
    public IPacketCipher? Cipher { get; set; }

    /// <summary>대화 번호와 송신 방향으로 세션 전용 암호화기를 만드는 팩토리.</summary>
    /// <remarks>
    /// netstandard2.1 서버에서도 연결마다 독립된 nonce·리플레이 상태와 수명을 갖게 한다.
    /// 반환 객체의 Overhead 는 <see cref="CipherFactoryOverhead"/> 와 정확히 같아야 한다.
    /// <see cref="Cipher"/> 및 <see cref="EncryptionKey"/> 와 함께 지정할 수 없다.
    /// </remarks>
    public PacketCipherFactory? CipherFactory { get; set; }

    /// <summary>팩토리가 반환하는 암호화기의 Overhead 값. 연결 전에 MTU·버퍼 크기를 검증하는 데 쓴다.</summary>
    public int CipherFactoryOverhead { get; set; }

    /// <summary>FEC 설정(P5-2). null 이면 FEC 를 쓰지 않는다.</summary>
    public FecOptions? Fec { get; set; }

    /// <summary>암호화·FEC 를 고려한 패킷 오버헤드(바이트).</summary>
    internal int CipherOverhead => CipherFactory != null ? CipherFactoryOverhead :
        Cipher?.Overhead ?? (EncryptionKey != null ? AesGcmPacketCipher.OverheadBytes : 0);

    /// <summary>설정과 키·FEC 값을 복사한다. 사용자 제공 Cipher 인스턴스는 공유하므로 세션별로 주입해야 한다.</summary>
    /// <returns>복사본.</returns>
    public KcpSessionOptions Clone()
    {
        var copy = (KcpSessionOptions)MemberwiseClone();
        copy.EncryptionKey = EncryptionKey == null ? null : (byte[])EncryptionKey.Clone();
        copy.Fec = Fec == null ? null : new FecOptions { DataShards = Fec.DataShards, ParityShards = Fec.ParityShards };
        return copy;
    }

    /// <summary>값이 유효한지 검사한다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">범위를 벗어난 값이 있는 경우.</exception>
    /// <exception cref="ArgumentException">서로 함께 사용할 수 없는 설정을 지정한 경우.</exception>
    public void Validate()
    {
        if (Mtu < 64 || Mtu > 65507)
            throw new ArgumentOutOfRangeException(nameof(Mtu), Mtu, "MTU 는 64~65507 이어야 합니다.");
        if (HandshakeTimeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeoutMs));
        if (HandshakeRetryIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(HandshakeRetryIntervalMs));
        if (FinLingerMs < 0)
            throw new ArgumentOutOfRangeException(nameof(FinLingerMs));
        if (IntervalMs < 10 || IntervalMs > 5000)
            throw new ArgumentOutOfRangeException(nameof(IntervalMs), "10~5000 이어야 합니다.");
        if (SendWindow <= 0 || SendWindow > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(SendWindow));
        if (ReceiveWindow < 2 || ReceiveWindow > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ReceiveWindow));
        if (NoDelay < 0 || NoDelay > 1)
            throw new ArgumentOutOfRangeException(nameof(NoDelay));
        if (NoCongestionControl < 0 || NoCongestionControl > 1)
            throw new ArgumentOutOfRangeException(nameof(NoCongestionControl));
        if (FastResend < 0)
            throw new ArgumentOutOfRangeException(nameof(FastResend));
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
        var cipherSources = (Cipher != null ? 1 : 0) + (CipherFactory != null ? 1 : 0) + (EncryptionKey != null ? 1 : 0);
        if (cipherSources > 1)
            throw new ArgumentException("Cipher, CipherFactory, EncryptionKey 중 하나만 지정해야 합니다.");
        if (CipherFactoryOverhead < 0)
            throw new ArgumentOutOfRangeException(nameof(CipherFactoryOverhead));
        if (CipherFactory == null && CipherFactoryOverhead != 0)
            throw new ArgumentException("CipherFactoryOverhead 는 CipherFactory 와 함께 지정해야 합니다.", nameof(CipherFactoryOverhead));
        if (EncryptionKey != null && EncryptionKey.Length != AesGcmPacketCipher.KeySize)
            throw new ArgumentOutOfRangeException(nameof(EncryptionKey), EncryptionKey.Length,
                $"AES-256-GCM 키는 {AesGcmPacketCipher.KeySize}바이트여야 합니다.");
        if (Fec != null && cipherSources != 0)
            throw new ArgumentException("이 버전에서는 FEC 와 암호화를 함께 켤 수 없습니다(로드맵 P5-2 참조).", nameof(Fec));
        if (RawMode && (Fec != null || cipherSources != 0))
            throw new ArgumentException("RawMode 는 암호화 또는 FEC 를 지원하지 않습니다.", nameof(RawMode));
        if (CipherOverhead < 0 || CipherOverhead > Mtu - 50)
            throw new ArgumentOutOfRangeException(nameof(Mtu), "암호화 오버헤드를 제외한 MTU 는 50 이상이어야 합니다.");
        Fec?.Validate();
    }
}

/// <summary>서버 전용 설정.</summary>
public sealed class KcpServerOptions
{
    /// <summary>서버 루프·소켓·세션 설정을 검사한다.</summary>
    /// <exception cref="ArgumentException">세션 설정이 없거나 지원하지 않는 조합인 경우.</exception>
    /// <exception cref="ArgumentOutOfRangeException">양수여야 하는 상한·대기 시간·버퍼 크기가 유효하지 않은 경우.</exception>
    public void Validate()
    {
        if (Session == null) throw new ArgumentException("세션 설정이 필요합니다.", nameof(Session));
        Session.Validate();
        if (MaxSessions <= 0) throw new ArgumentOutOfRangeException(nameof(MaxSessions));
        if (PollMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(PollMicroseconds));
        if (MaxReceiveBatch <= 0) throw new ArgumentOutOfRangeException(nameof(MaxReceiveBatch));
        if (SocketReceiveBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(SocketReceiveBufferSize));
        if (SocketSendBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(SocketSendBufferSize));
    }

    /// <summary>서버 및 세션 설정을 복사한다.</summary>
    /// <returns>복사본.</returns>
    /// <exception cref="ArgumentException">세션 설정이 null 인 경우.</exception>
    public KcpServerOptions Clone()
    {
        if (Session == null) throw new ArgumentException("세션 설정이 필요합니다.", nameof(Session));
        var copy = (KcpServerOptions)MemberwiseClone();
        copy.Session = Session.Clone();
        return copy;
    }

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
