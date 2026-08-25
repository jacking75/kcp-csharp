using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading.Channels;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.Transport;

/// <summary>세션 상태.</summary>
public enum KcpSessionState
{
    /// <summary>연결되지 않음(초기 상태이자 정상 종료 후 상태).</summary>
    Closed,

    /// <summary>핸드셰이크 진행 중.</summary>
    Connecting,

    /// <summary>연결됨. 송수신 가능.</summary>
    Connected,

    /// <summary>FIN 을 보내고 잔여 재전송을 처리하는 중.</summary>
    Closing,

    /// <summary>타임아웃 또는 데드링크로 끊김.</summary>
    Dead,
}

/// <summary><see cref="KcpSession.Send(ReadOnlySpan{byte})"/> 결과.</summary>
public enum KcpSendResult
{
    /// <summary>송신 큐에 적재 성공.</summary>
    Ok = 0,

    /// <summary>연결 상태가 아니다.</summary>
    NotConnected = -1,

    /// <summary>백로그 상한 초과. 잠시 후 다시 시도해야 한다(백프레셔).</summary>
    Backpressure = -2,

    /// <summary>한 번에 보낼 수 있는 최대 크기를 넘었다.</summary>
    TooLarge = -3,

    /// <summary>그 밖의 오류.</summary>
    Error = -4,
}

/// <summary>
/// 수신 메시지를 <b>복사 없이</b> 넘겨받는 핸들러(진단 G-2).
/// </summary>
/// <remarks>
/// <see cref="ReadOnlySpan{T}"/> 는 제네릭 <see cref="Action{T1, T2}"/> 의 타입 인자가 될 수 없어
/// 전용 델리게이트로 선언한다. <paramref name="data"/> 는 <b>콜백이 반환하면 무효</b>다.
/// 보관하려면 반드시 복사해야 한다.
/// </remarks>
/// <param name="session">메시지를 받은 세션.</param>
/// <param name="data">메시지 페이로드.</param>
public delegate void KcpMessageHandler(KcpSession session, ReadOnlySpan<byte> data);

/// <summary>세션 종료 사유.</summary>
public enum KcpDisconnectReason
{
    /// <summary>상대가 FIN 을 보냈다.</summary>
    RemoteClosed,

    /// <summary>내가 <see cref="KcpSession.Disconnect"/> 를 호출했다.</summary>
    LocalClosed,

    /// <summary>마지막 수신 후 <see cref="KcpSessionOptions.SessionTimeoutMs"/> 가 지났다.</summary>
    Timeout,

    /// <summary>KCP 가 데드링크를 감지했다(진단 B-03).</summary>
    DeadLink,

    /// <summary>핸드셰이크가 실패했다.</summary>
    HandshakeFailed,
}

/// <summary>
/// KCP 세션의 상태 머신(P4-2a).
/// </summary>
/// <remarks>
/// <para>
/// 소켓을 알지 못한다. 패킷 송신은 주입된 sender 델리게이트가, 시각은 주입된 시계가 담당하므로
/// 가상 시간 단위 테스트로 상태 전이표 전체를 검증할 수 있다.
/// </para>
/// <para>
/// <b>스레딩 규약</b>: 세션 1개는 소유 스레드 1개에서만 사용한다. 서버에서는 수신 루프 스레드가 소유자다.
/// 이벤트 콜백도 그 스레드에서 발화하므로, 게임 로직 스레드로는 큐를 통해 넘겨야 한다.
/// </para>
/// </remarks>
public sealed class KcpSession
{
    private const int MaxUnreliableQueue = 256;

    private readonly KcpSessionOptions options;
    private readonly PacketSender sender;
    private readonly Func<uint> clock;
    private readonly bool isClient;
    private readonly byte[] frameBuffer;
    private readonly Queue<PooledMessage> unreliableQueue = new Queue<PooledMessage>();

    // G-3: 암호화 세션의 상주 복호화 버퍼. 패킷마다의 Rent/Return 과 반환 누락 위험을 함께 없앤다.
    private byte[]? decryptBuffer;

    // G-2: 수신 이벤트용 상주 버퍼. 다조각 조립과 ReadOnlyMemory 계약을 맞출 때만 쓴다(필요 시 성장).
    private byte[] assemblyBuffer = Array.Empty<byte>();
    private byte[] unreliableScratch = Array.Empty<byte>();

    // G-2: RecvInPlace 콜백을 세션당 1개만 만들어 재사용한다(호출마다 델리게이트를 만들지 않기 위해).
    private readonly Kcp.InPlaceMessageHandler inPlaceCallback;
    private KcpMessageHandler? inPlaceTarget;

    // G-2: 드레인 재진입 가드.
    // 조립 버퍼가 세션 상주로 바뀌었으므로, 핸들러 안에서 Update/OnFrame 을 다시 부르면
    // 안쪽 드레인이 바깥쪽이 아직 넘겨 준 버퍼를 덮어쓸 수 있다. 안쪽은 그냥 돌아가고
    // 바깥 루프가 이어서 처리하게 한다(메시지 유실·순서 뒤바뀜 없음).
    private bool draining;

    // G-4: FEC 인/디코더에 넘기는 콜백도 세션당 1개만 만든다.
    // 람다로 두면 패킷마다 클로저 객체 + 델리게이트가 생긴다(FEC 를 켠 세션의 송수신 전 경로).
    private readonly FecOutput fecOutputCallback;
    private readonly FecOutput fecRecoveredCallback;

    // M-1: 게임 스레드용 송신 아웃박스(bounded MPSC). 옵션을 켠 세션에만 만든다.
    private readonly Channel<PooledMessage>? outbox;
    private long outboxAccepted;
    private long outboxRejected;

    private Kcp? kcp;
    private KcpSessionState state;
    private uint conv;

    // P5-1 암호화 / P5-2 FEC
    private IPacketCipher? cipher;
    private FecEncoder? fecEncoder;
    private FecDecoder? fecDecoder;
    private readonly int reservedBytes;

    // 핸드셰이크(클라이언트)
    private uint clientNonce;
    private uint cookie;
    private uint handshakeStartedAt;
    private uint nextHandshakeAt;
    private int handshakeAttempts;

    // 수명 타이머
    private uint lastReceiveAt;
    private uint lastPingSentAt;
    private uint pendingPingTs;
    private bool pingPending;
    private uint closingStartedAt;
    private byte closeReason;

    private struct PooledMessage
    {
        public byte[] Buffer;
        public int Length;
    }

    /// <summary>패킷을 하위 계층으로 내보내는 델리게이트.</summary>
    /// <param name="data">보낼 바이트.</param>
    /// <param name="remote">수신자.</param>
    public delegate void PacketSender(ReadOnlySpan<byte> data, IPEndPoint remote);

    /// <summary>세션을 만든다.</summary>
    /// <param name="remote">상대 주소.</param>
    /// <param name="options">설정.</param>
    /// <param name="sender">패킷 송신 델리게이트.</param>
    /// <param name="clock">밀리초 시계. 생략하면 <see cref="KcpClock.NowMs"/>.</param>
    /// <param name="isClient">true 면 클라이언트(핸드셰이크를 시작하는 쪽).</param>
    public KcpSession(IPEndPoint remote, KcpSessionOptions options, PacketSender sender, Func<uint>? clock = null, bool isClient = true)
    {
        RemoteEndPoint = remote ?? throw new ArgumentNullException(nameof(remote));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.clock = clock ?? (() => KcpClock.NowMs);
        this.isClient = isClient;
        inPlaceCallback = OnInPlaceMessage;
        fecOutputCallback = OnFecOutput;
        fecRecoveredCallback = OnFecRecovered;

        options.Validate();
        RemoteKey = AddressKey.FromEndPoint(remote);

        if (options.SendOutboxCapacity > 0)
        {
            // 여러 생산자(게임 스레드들) → 단일 소비자(소유 스레드). 가득 차면 TryWrite 가 false 를 돌려준다.
            outbox = Channel.CreateBounded<PooledMessage>(new BoundedChannelOptions(options.SendOutboxCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        var cipherOverhead = options.CipherOverhead;
        reservedBytes = options.RawMode
            ? 0
            : Framing.KcpHeaderSize + (options.Fec != null ? FecFrame.DataHeaderSize : 0);
        frameBuffer = new byte[options.Mtu + cipherOverhead + Framing.RawHeaderSize];

        if (options.RawMode)
        {
            // 핸드셰이크 없이 곧바로 연결된 것으로 취급한다(kcp-go 상호운용 모드).
            conv = options.RawModeConv;
            CreateKcp(conv);
            state = KcpSessionState.Connected;
            lastReceiveAt = this.clock();
        }
    }

    /// <summary>이 연결의 대화 번호. 서버가 할당한다.</summary>
    public uint Conv => conv;

    /// <summary>현재 상태.</summary>
    public KcpSessionState State => state;

    /// <summary>상대 주소.</summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>세션 테이블 조회용 주소 키.</summary>
    public AddressKey RemoteKey { get; }

    /// <summary>PING/PONG 왕복으로 잰 최근 핑(ms). 아직 측정 전이면 -1.</summary>
    public int PingMs { get; private set; } = -1;

    /// <summary>KCP 가 계산한 평활 RTT(ms).</summary>
    public int SrttMs => kcp?.Srtt ?? 0;

    /// <summary>KCP 통계 스냅숏.</summary>
    public KcpStats Stats => kcp?.GetStats() ?? default;

    /// <summary>내부 KCP 인스턴스(고급 조회용). 연결 전이면 null.</summary>
    public Kcp? Kcp => kcp;

    /// <summary>애플리케이션이 자유롭게 쓰는 태그(플레이어 객체 등).</summary>
    public object? UserState { get; set; }

    /// <summary>연결이 성립했을 때 발생한다.</summary>
    public event Action<KcpSession>? Connected;

    /// <summary>연결이 끊겼을 때 발생한다.</summary>
    public event Action<KcpSession, KcpDisconnectReason>? Disconnected;

    /// <summary>
    /// 신뢰 채널 메시지를 받았을 때 발생한다.
    /// 구독자가 있으면 폴링(<see cref="Receive(Span{byte})"/>) 대신 이 이벤트로 전달된다.
    /// </summary>
    /// <remarks>메모리는 콜백이 끝나면 무효다. 보관하려면 복사해야 한다.</remarks>
    public event Action<KcpSession, ReadOnlyMemory<byte>>? ReliableReceived;

    /// <summary>
    /// 비신뢰 채널 메시지를 받았을 때 발생한다.
    /// 구독자가 있으면 폴링(<see cref="ReceiveUnreliable(Span{byte})"/>) 대신 이 이벤트로 전달된다.
    /// </summary>
    /// <remarks>메모리는 콜백이 끝나면 무효다. 보관하려면 복사해야 한다.</remarks>
    public event Action<KcpSession, ReadOnlyMemory<byte>>? UnreliableReceived;

    /// <summary>
    /// 신뢰 채널 메시지를 <b>무복사</b>로 전달한다(진단 G-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReliableReceived"/> 구독자가 없으면 단일 조각 메시지를 KCP 세그먼트에서 그대로 넘긴다.
    /// 대여 버퍼도, 복사도, 할당도 없다. 다조각 메시지만 세션 상주 조립 버퍼를 거친다.
    /// </para>
    /// <para>
    /// <b>Span 은 콜백 밖으로 보관할 수 없다.</b> 필요하면 콜백 안에서 복사한다.
    /// (<see cref="ReliableReceived"/> 의 <see cref="ReadOnlyMemory{T}"/> 도 이미 같은 규약이다.)
    /// </para>
    /// <para>두 이벤트를 함께 구독하면 둘 다 발화한다(이 경우 무복사 경로는 쓰이지 않는다).</para>
    /// </remarks>
    public event KcpMessageHandler? ReliableReceivedSpan;

    /// <summary>
    /// 비신뢰 채널 메시지를 <b>무복사</b>로 전달한다(진단 G-2).
    /// </summary>
    /// <remarks>
    /// 페이로드는 이미 수신 버퍼 안에 살아 있으므로 복사 없이 그대로 넘긴다.
    /// Span 은 콜백 밖으로 보관할 수 없다.
    /// </remarks>
    public event KcpMessageHandler? UnreliableReceivedSpan;

    // ---------------- 핸드셰이크 ----------------

    /// <summary>클라이언트 핸드셰이크를 시작한다(SYN 송신).</summary>
    /// <param name="now">현재 시각(ms).</param>
    public void StartConnect(uint now)
    {
        if (options.RawMode || state != KcpSessionState.Closed)
            return;

        clientNonce = NewNonce();
        cookie = 0;
        handshakeStartedAt = now;
        handshakeAttempts = 0;
        state = KcpSessionState.Connecting;
        SendSyn(now);
    }

    private void SendSyn(uint now)
    {
        handshakeAttempts++;
        nextHandshakeAt = now + (uint)options.HandshakeRetryIntervalMs;
        var len = Framing.WriteSyn(frameBuffer, FrameType.Syn, clientNonce, cookie);
        sender(frameBuffer.AsSpan(0, len), RemoteEndPoint);
    }

    /// <summary>서버 쪽에서 이미 성립된 연결로 세션을 연다.</summary>
    /// <param name="assignedConv">서버가 할당한 conv.</param>
    /// <param name="now">현재 시각(ms).</param>
    internal void AcceptAsServer(uint assignedConv, uint now)
    {
        conv = assignedConv;
        CreateKcp(assignedConv);
        state = KcpSessionState.Connected;
        lastReceiveAt = now;
        lastPingSentAt = now;
        Connected?.Invoke(this);
    }

    private void CreateKcp(uint conversation)
    {
        // P5-1: 키만 주면 세션이 conv·방향에 맞는 암호화기를 직접 만든다(nonce 재사용 방지).
        cipher = options.Cipher;
        if (cipher == null && options.EncryptionKey != null)
        {
            cipher = new AesGcmPacketCipher(options.EncryptionKey, conversation, serverDirection: !isClient);
        }

        // G-3: 복호화 대상 크기는 MTU 로 상한이 정해져 있다. 세션당 한 번만 잡아 두고 재사용한다.
        if (cipher != null && decryptBuffer == null)
            decryptBuffer = new byte[frameBuffer.Length];

        // P5-2: FEC 는 프레이밍 안쪽, KCP 바깥쪽에 놓인다.
        if (options.Fec != null && !options.RawMode)
        {
            var maxShardSize = options.Mtu - Framing.KcpHeaderSize - FecFrame.HeaderSize;
            fecEncoder = new FecEncoder(options.Fec, maxShardSize, Framing.KcpHeaderSize);
            fecDecoder = new FecDecoder(options.Fec, maxShardSize);
        }

        var instance = new Kcp(conversation, OnKcpOutput, clock);
        instance.SetMtu(options.Mtu - (cipher?.Overhead ?? 0));
        if (reservedBytes > 0)
        {
            // 프레임 타입(+FEC 헤더) 만큼을 KCP 패킷 앞에 확보한다(복사 없이 프레이밍).
            instance.ReserveBytes(reservedBytes);
        }
        instance.NoDelay(options.NoDelay, options.IntervalMs, options.FastResend, options.NoCongestionControl);
        instance.WndSize(options.SendWindow, options.ReceiveWindow);
        instance.SetStreamMode(options.StreamMode);
        kcp = instance;
    }

    private void OnKcpOutput(byte[] buffer, int length)
    {
        if (options.RawMode)
        {
            sender(buffer.AsSpan(0, length), RemoteEndPoint);
            return;
        }

        buffer[0] = (byte)FrameType.Kcp;

        if (fecEncoder != null)
        {
            // [0x04][seqid][flag][size][KCP 패킷] — 데이터 패킷과 (그룹이 찼으면) 패리티가 함께 나간다.
            fecEncoder.Encode(buffer.AsSpan(0, length), fecOutputCallback);
            return;
        }

        if (cipher == null)
        {
            // 예약해 둔 첫 바이트에 프레임 타입만 채우면 그대로 보낼 수 있다.
            sender(buffer.AsSpan(0, length), RemoteEndPoint);
            return;
        }

        // 암호화 경로: [0x04][암호문(카운터+태그 포함)]
        frameBuffer[0] = (byte)FrameType.Kcp;
        var payload = buffer.AsSpan(Framing.KcpHeaderSize, length - Framing.KcpHeaderSize);
        var written = cipher.Encrypt(payload, frameBuffer.AsSpan(Framing.KcpHeaderSize));
        sender(frameBuffer.AsSpan(0, Framing.KcpHeaderSize + written), RemoteEndPoint);
    }

    // ---------------- 수신 ----------------

    /// <summary>하위 계층에서 받은 프레임 하나를 처리한다.</summary>
    /// <param name="frame">프레임 바이트.</param>
    /// <param name="now">현재 시각(ms).</param>
    public void OnFrame(ReadOnlySpan<byte> frame, uint now)
    {
        // 이미 끝난 세션은 어떤 프레임에도 반응하지 않는다.
        // (특히 PONG 을 돌려주면 상대가 "아직 살아 있다"고 오해해 영원히 타임아웃되지 않는다.)
        if (state == KcpSessionState.Dead || state == KcpSessionState.Closed)
            return;

        if (options.RawMode)
        {
            lastReceiveAt = now;
            kcp?.Input(frame, true, options.AckNoDelay);
            DrainReliable();
            return;
        }

        var type = Framing.PeekType(frame);
        switch (type)
        {
            case FrameType.SynCookie:
                OnSynCookie(frame, now);
                break;

            case FrameType.SynAck:
                OnSynAck(frame, now);
                break;

            case FrameType.Kcp:
                OnKcpFrame(frame, now);
                break;

            case FrameType.Raw:
                OnRawFrame(frame, now);
                break;

            case FrameType.Ping:
                OnPing(frame, now);
                break;

            case FrameType.Pong:
                OnPong(frame, now);
                break;

            case FrameType.Fin:
                OnFin(frame, now);
                break;

            default:
                // 알 수 없는 프레임은 조용히 버린다.
                break;
        }
    }

    private void OnSynCookie(ReadOnlySpan<byte> frame, uint now)
    {
        if (state != KcpSessionState.Connecting) return;
        if (!Framing.TryReadSyn(frame, out var version, out var nonce, out var receivedCookie)) return;
        if (version != Framing.ProtocolVersion || nonce != clientNonce) return;
        if (receivedCookie == 0) return;

        lastReceiveAt = now;
        cookie = receivedCookie;

        // 쿠키를 받았으니 즉시 SYN 을 다시 보낸다(재시도 카운트는 그대로 둔다).
        handshakeAttempts--;
        SendSyn(now);
    }

    private void OnSynAck(ReadOnlySpan<byte> frame, uint now)
    {
        if (state != KcpSessionState.Connecting) return;
        if (!Framing.TryReadSynAck(frame, out var assignedConv, out _)) return;
        if (assignedConv == 0) return;

        lastReceiveAt = now;
        conv = assignedConv;
        CreateKcp(assignedConv);
        state = KcpSessionState.Connected;
        lastPingSentAt = now;
        Connected?.Invoke(this);
    }

    private void OnKcpFrame(ReadOnlySpan<byte> frame, uint now)
    {
        if (kcp == null || state == KcpSessionState.Closed) return;
        if (!Framing.TryReadKcp(frame, out var payload)) return;

        lastReceiveAt = now;

        if (fecDecoder != null)
        {
            // 데이터 샤드면 payload 가 원본 KCP 패킷, 복원된 샤드는 콜백으로 온다.
            var isData = fecDecoder.Decode(payload, out var original, fecRecoveredCallback);

            if (isData)
                kcp.Input(original, true, options.AckNoDelay);

            DrainReliable();
            return;
        }

        if (cipher == null)
        {
            kcp.Input(payload, true, options.AckNoDelay);
        }
        else
        {
            // G-3: 상주 버퍼로 복호화한다(Rent/Return·try/finally 없음).
            var plain = decryptBuffer!;
            if (!cipher.TryDecrypt(payload, plain, out var plainLength))
                return;   // 변조·리플레이 패킷은 조용히 버린다.
            kcp.Input(plain.AsSpan(0, plainLength), true, options.AckNoDelay);
        }

        DrainReliable();
    }

    private void OnRawFrame(ReadOnlySpan<byte> frame, uint now)
    {
        if (state != KcpSessionState.Connected) return;
        if (!Framing.TryReadRaw(frame, out var frameConv, out var payload)) return;
        if (frameConv != conv) return;

        lastReceiveAt = now;

        if (cipher == null)
        {
            DeliverUnreliable(payload);
            return;
        }

        // G-3: 상주 버퍼로 복호화한다.
        var plain = decryptBuffer!;
        if (!cipher.TryDecrypt(payload, plain, out var plainLength))
            return;
        DeliverUnreliable(plain.AsSpan(0, plainLength));
    }

    private void OnPing(ReadOnlySpan<byte> frame, uint now)
    {
        if (!Framing.TryReadPing(frame, out var frameConv, out var timestamp)) return;
        if (frameConv != conv) return;

        lastReceiveAt = now;

        var len = Framing.WritePing(frameBuffer, FrameType.Pong, conv, timestamp);
        sender(frameBuffer.AsSpan(0, len), RemoteEndPoint);
    }

    private void OnPong(ReadOnlySpan<byte> frame, uint now)
    {
        if (!Framing.TryReadPing(frame, out var frameConv, out var timestamp)) return;
        if (frameConv != conv) return;

        lastReceiveAt = now;
        if (pingPending && timestamp == pendingPingTs)
        {
            PingMs = Kcp.Itimediff(now, timestamp);
            pingPending = false;
        }
    }

    private void OnFin(ReadOnlySpan<byte> frame, uint now)
    {
        if (!Framing.TryReadFin(frame, out var frameConv, out var reason)) return;
        if (frameConv != conv) return;

        lastReceiveAt = now;
        closeReason = reason;
        Close(KcpSessionState.Closed, KcpDisconnectReason.RemoteClosed);
    }

    private void DeliverUnreliable(ReadOnlySpan<byte> payload)
    {
        var spanHandler = UnreliableReceivedSpan;
        var handler = UnreliableReceived;

        if (spanHandler == null && handler == null)
        {
            // 폴링 모델: 큐에 쌓아 둔다. 상한을 넘으면 가장 오래된 것을 버린다(비신뢰 채널이므로 허용).
            // 여기는 정책상 대여 버퍼를 유지한다(꺼내 갈 때까지 살아 있어야 하므로).
            if (unreliableQueue.Count >= MaxUnreliableQueue)
            {
                var dropped = unreliableQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(dropped.Buffer);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
            payload.CopyTo(buffer);
            unreliableQueue.Enqueue(new PooledMessage { Buffer = buffer, Length = payload.Length });
            return;
        }

        // G-2: 페이로드는 이미 수신 버퍼 안에 살아 있다. 복사 없이 그대로 넘긴다.
        spanHandler?.Invoke(this, payload);

        if (handler != null)
        {
            // ReadOnlyMemory 계약에는 배열이 필요하다. 대여 대신 세션 상주 스크래치로 복사한다.
            var scratch = EnsureBuffer(ref unreliableScratch, payload.Length);
            payload.CopyTo(scratch);
            handler(this, new ReadOnlyMemory<byte>(scratch, 0, payload.Length));
        }
    }

    private void DrainReliable()
    {
        var spanHandler = ReliableReceivedSpan;
        var memoryHandler = ReliableReceived;
        if (spanHandler == null && memoryHandler == null)
            return;

        // 핸들러가 다시 이 세션을 구동한 경우다. 바깥 루프가 이어서 꺼내므로 여기서는 돌아간다.
        if (draining)
            return;

        draining = true;
        inPlaceTarget = spanHandler;
        try
        {
            while (true)
            {
                // 핸들러가 세션을 끊으면 kcp 가 사라진다. 매 회 다시 읽는다.
                var instance = kcp;
                if (instance == null) break;

                if (memoryHandler == null)
                {
                    // G-2 무복사 경로: 단일 조각이면 세그먼트 페이로드를 그대로 넘긴다.
                    var inPlace = instance.RecvInPlace(inPlaceCallback);
                    if (inPlace >= 0) continue;
                    if (inPlace != -3) break;   // -1: 더 읽을 메시지가 없다.
                    // -3: 다조각이므로 아래 조립 경로로 내려간다.
                }

                var size = instance.PeekSize();
                if (size < 0) break;

                var buffer = EnsureBuffer(ref assemblyBuffer, size);
                var n = instance.Recv(buffer.AsSpan(0, size));
                if (n < 0) break;

                spanHandler?.Invoke(this, buffer.AsSpan(0, n));
                memoryHandler?.Invoke(this, new ReadOnlyMemory<byte>(buffer, 0, n));
            }
        }
        finally
        {
            inPlaceTarget = null;
            draining = false;
        }
    }

    private void OnInPlaceMessage(ReadOnlySpan<byte> message)
    {
        inPlaceTarget?.Invoke(this, message);
    }

    /// <summary>FEC 인코더가 내보내는 패킷(데이터·패리티)을 그대로 하위 계층으로 보낸다.</summary>
    private void OnFecOutput(ReadOnlySpan<byte> packet)
    {
        sender(packet, RemoteEndPoint);
    }

    /// <summary>FEC 가 복원해 낸 KCP 패킷을 코어에 넣는다(재전송으로 온 것이 아니므로 regular: false).</summary>
    private void OnFecRecovered(ReadOnlySpan<byte> recovered)
    {
        kcp?.Input(recovered, false, options.AckNoDelay);
    }

    /// <summary>상주 버퍼가 <paramref name="size"/> 이상이 되도록 키운다(2의 거듭제곱 성장).</summary>
    private static byte[] EnsureBuffer(ref byte[] buffer, int size)
    {
        if (size < 1) size = 1;
        if (buffer.Length >= size) return buffer;

        var capacity = buffer.Length == 0 ? 1024 : buffer.Length;
        while (capacity < size && capacity <= (1 << 29))
            capacity <<= 1;
        if (capacity < size) capacity = size;

        buffer = new byte[capacity];
        return buffer;
    }

    // ---------------- 송신 ----------------

    /// <summary>신뢰·순서 보장 채널로 보낸다(KCP).</summary>
    /// <param name="data">보낼 데이터.</param>
    /// <returns>결과 코드.</returns>
    public KcpSendResult Send(ReadOnlySpan<byte> data)
    {
        if (state != KcpSessionState.Connected || kcp == null)
            return KcpSendResult.NotConnected;
        if (data.Length == 0)
            return KcpSendResult.Error;

        // 백프레셔: 큐가 이미 상한이면 받아 주지 않는다(P4-4c).
        if (kcp.WaitSnd >= options.MaxSendBacklog)
            return KcpSendResult.Backpressure;

        var ret = kcp.Send(data);
        return ret switch
        {
            0 => KcpSendResult.Ok,
            -2 => KcpSendResult.TooLarge,
            _ => KcpSendResult.Error,
        };
    }

    /// <summary>
    /// <b>어느 스레드에서든</b> 호출할 수 있는 신뢰 채널 송신(진단 M-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Send(ReadOnlySpan{byte})"/> 는 소유 스레드 전용이다(코어에 lock 이 없다).
    /// 게임 로직 스레드에서 보내야 할 때 이 메서드를 쓰면, 데이터가 바운디드 큐에 복사되어
    /// 소유 스레드의 다음 <see cref="Update"/> 첫머리에서 KCP 로 들어간다.
    /// </para>
    /// <para>
    /// 스레드 경계를 넘기 위한 복사 1회가 추가된다(호출자 버퍼의 수명과 분리해야 하므로 필수 비용).
    /// 이벤트 콜백 안(= 소유 스레드)에서는 <see cref="Send(ReadOnlySpan{byte})"/> 를 그대로 쓰면 무복사다.
    /// </para>
    /// <para>
    /// 쓰려면 <see cref="KcpSessionOptions.SendOutboxCapacity"/> 를 0 보다 크게 설정해야 한다.
    /// </para>
    /// <para>
    /// 큐는 <see cref="KcpSessionState.Connected"/> 일 때만 비워진다. 연결 전·종료 후에 계속 넣으면
    /// 용량까지 쌓인 뒤 false 가 돌아온다. 호출 전에 <see cref="State"/> 를 확인하는 것이 정석이다.
    /// </para>
    /// </remarks>
    /// <param name="data">보낼 데이터.</param>
    /// <returns>
    /// 큐에 넣었으면 true. 데이터가 비었거나, 큐가 가득 찼거나(백프레셔),
    /// 세션 수명이 이미 끝났으면 false.
    /// </returns>
    /// <exception cref="InvalidOperationException">아웃박스를 켜지 않은 세션인 경우.</exception>
    public bool TrySendFromAnyThread(ReadOnlySpan<byte> data)
    {
        var queue = outbox
            ?? throw new InvalidOperationException(
                "아웃박스를 쓰려면 KcpSessionOptions.SendOutboxCapacity 를 0 보다 크게 설정해야 합니다(M-1).");

        if (data.Length == 0)
            return false;

        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);

        if (queue.Writer.TryWrite(new PooledMessage { Buffer = buffer, Length = data.Length }))
        {
            System.Threading.Interlocked.Increment(ref outboxAccepted);
            return true;
        }

        // 넣지 못했으면 대여 버퍼를 즉시 돌려준다(누수 방지).
        ArrayPool<byte>.Shared.Return(buffer);
        System.Threading.Interlocked.Increment(ref outboxRejected);
        return false;
    }

    /// <summary>아웃박스가 받아들인 메시지 누계(진단용).</summary>
    public long OutboxAccepted => System.Threading.Interlocked.Read(ref outboxAccepted);

    /// <summary>
    /// 아웃박스가 살리지 못한 메시지 누계(진단용). 큐가 가득 찼거나 KCP 가 거부한 경우가 모두 포함된다.
    /// 늘어나면 용량이 부족하거나 메시지가 너무 큰 것이다.
    /// </summary>
    public long OutboxRejected => System.Threading.Interlocked.Read(ref outboxRejected);

    /// <summary>아웃박스에서 꺼내 KCP 송신 큐로 옮긴다. 소유 스레드에서만 부른다.</summary>
    private void DrainOutbox()
    {
        var queue = outbox;
        if (queue == null) return;

        var reader = queue.Reader;

        // 백프레셔를 매번 확인한다. KCP 백로그가 차면 남은 메시지는 아웃박스에 그대로 둔다
        // (다음 Update 에서 이어서 처리된다).
        while (state == KcpSessionState.Connected &&
               kcp != null &&
               kcp.WaitSnd < options.MaxSendBacklog &&
               reader.TryRead(out var msg))
        {
            // 조각 수가 수신 윈도를 넘는 메시지는 KCP 가 거부한다(-2). 조용히 사라지지 않게 계수한다.
            if (kcp.Send(msg.Buffer.AsSpan(0, msg.Length)) != 0)
                System.Threading.Interlocked.Increment(ref outboxRejected);

            ArrayPool<byte>.Shared.Return(msg.Buffer);
        }
    }

    /// <summary>아웃박스에 남은 대여 버퍼를 모두 돌려준다.</summary>
    /// <param name="complete">
    /// true 면 쓰기 자체를 영구히 닫는다(세션 수명 종료). false 면 내용만 비운다 —
    /// 끊긴 세션을 <see cref="StartConnect"/> 로 다시 연결하는 경우 아웃박스를 계속 쓸 수 있어야 하기 때문이다.
    /// </param>
    private void ReleaseOutbox(bool complete)
    {
        var queue = outbox;
        if (queue == null) return;

        // 먼저 쓰기를 닫아야 드레인 도중 새 메시지가 들어오는 경합이 사라진다.
        if (complete)
            queue.Writer.TryComplete();

        while (queue.Reader.TryRead(out var msg))
            ArrayPool<byte>.Shared.Return(msg.Buffer);
    }

    /// <summary>비신뢰 채널로 보낸다(재전송·순서 보장 없음).</summary>
    /// <param name="data">보낼 데이터.</param>
    /// <returns>결과 코드.</returns>
    public KcpSendResult SendUnreliable(ReadOnlySpan<byte> data)
    {
        if (state != KcpSessionState.Connected)
            return KcpSendResult.NotConnected;
        if (data.Length == 0)
            return KcpSendResult.Error;
        if (options.RawMode)
            return KcpSendResult.Error;   // RawMode 에는 비신뢰 프레임이 없다.

        var overhead = Framing.RawHeaderSize + (cipher?.Overhead ?? 0);
        if (data.Length + overhead > options.Mtu)
            return KcpSendResult.TooLarge;

        int length;
        if (cipher == null)
        {
            length = Framing.WriteRaw(frameBuffer, conv, data);
        }
        else
        {
            frameBuffer[0] = (byte)FrameType.Raw;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frameBuffer.AsSpan(1), conv);
            var written = cipher.Encrypt(data, frameBuffer.AsSpan(Framing.RawHeaderSize));
            length = Framing.RawHeaderSize + written;
        }

        sender(frameBuffer.AsSpan(0, length), RemoteEndPoint);
        return KcpSendResult.Ok;
    }

    // ---------------- 폴링 수신 ----------------

    /// <summary>신뢰 채널 메시지를 하나 꺼낸다(폴링 모델).</summary>
    /// <param name="buffer">대상 버퍼.</param>
    /// <returns>읽은 바이트 수. 없으면 -1, 버퍼가 작으면 -2.</returns>
    public int Receive(Span<byte> buffer)
    {
        return kcp?.Recv(buffer) ?? -1;
    }

    /// <summary>다음 신뢰 메시지의 크기. 없으면 -1.</summary>
    /// <returns>바이트 수.</returns>
    public int PeekSize() => kcp?.PeekSize() ?? -1;

    /// <summary>비신뢰 채널 메시지를 하나 꺼낸다(폴링 모델).</summary>
    /// <param name="buffer">대상 버퍼.</param>
    /// <returns>읽은 바이트 수. 없으면 -1, 버퍼가 작으면 -2.</returns>
    public int ReceiveUnreliable(Span<byte> buffer)
    {
        if (unreliableQueue.Count == 0) return -1;

        var head = unreliableQueue.Peek();
        if (head.Length > buffer.Length) return -2;

        unreliableQueue.Dequeue();
        head.Buffer.AsSpan(0, head.Length).CopyTo(buffer);
        ArrayPool<byte>.Shared.Return(head.Buffer);
        return head.Length;
    }

    // ---------------- 수명 ----------------

    /// <summary>정상 종료를 시작한다(FIN 송신 후 linger).</summary>
    /// <param name="reason">애플리케이션 정의 종료 사유.</param>
    public void Disconnect(byte reason = 0)
    {
        if (state == KcpSessionState.Closed || state == KcpSessionState.Dead)
            return;

        var now = clock();
        closeReason = reason;

        if (state == KcpSessionState.Connected && !options.RawMode)
        {
            var len = Framing.WriteFin(frameBuffer, conv, reason);
            sender(frameBuffer.AsSpan(0, len), RemoteEndPoint);

            state = KcpSessionState.Closing;
            closingStartedAt = now;
            return;
        }

        Close(KcpSessionState.Closed, KcpDisconnectReason.LocalClosed);
    }

    /// <summary>모든 타이머와 KCP 를 한 번 구동한다.</summary>
    /// <param name="now">현재 시각(ms).</param>
    /// <returns>다음에 <see cref="Update"/> 를 호출하면 좋은 시각(ms).</returns>
    public uint Update(uint now)
    {
        // M-1: 다른 스레드가 넣어 둔 메시지를 먼저 KCP 로 옮긴다(소유 스레드에서만 실행된다).
        DrainOutbox();

        switch (state)
        {
            case KcpSessionState.Connecting:
                return UpdateConnecting(now);

            case KcpSessionState.Connected:
                return UpdateConnected(now);

            case KcpSessionState.Closing:
                return UpdateClosing(now);

            default:
                return now + (uint)options.PingIntervalMs;
        }
    }

    private uint UpdateConnecting(uint now)
    {
        if (Kcp.Itimediff(now, handshakeStartedAt + (uint)options.HandshakeTimeoutMs) >= 0 ||
            handshakeAttempts > options.HandshakeMaxRetries)
        {
            Close(KcpSessionState.Closed, KcpDisconnectReason.HandshakeFailed);
            return now + (uint)options.PingIntervalMs;
        }

        if (Kcp.Itimediff(now, nextHandshakeAt) >= 0)
        {
            SendSyn(now);
        }

        return nextHandshakeAt;
    }

    private uint UpdateConnected(uint now)
    {
        if (kcp == null)
            return now + (uint)options.IntervalMs;

        // 데드링크·타임아웃 판정
        if (kcp.IsDeadLink)
        {
            Close(KcpSessionState.Dead, KcpDisconnectReason.DeadLink);
            return now + (uint)options.PingIntervalMs;
        }

        if (Kcp.Itimediff(now, lastReceiveAt + (uint)options.SessionTimeoutMs) >= 0)
        {
            Close(KcpSessionState.Dead, KcpDisconnectReason.Timeout);
            return now + (uint)options.PingIntervalMs;
        }

        // keepalive
        if (!options.RawMode && Kcp.Itimediff(now, lastPingSentAt + (uint)options.PingIntervalMs) >= 0)
        {
            lastPingSentAt = now;
            pendingPingTs = now;
            pingPending = true;
            var len = Framing.WritePing(frameBuffer, FrameType.Ping, conv, now);
            sender(frameBuffer.AsSpan(0, len), RemoteEndPoint);
        }

        kcp.Update();
        DrainReliable();

        var nextKcp = kcp.Check();
        var nextPing = lastPingSentAt + (uint)options.PingIntervalMs;
        return Kcp.Itimediff(nextKcp, nextPing) <= 0 ? nextKcp : nextPing;
    }

    private uint UpdateClosing(uint now)
    {
        kcp?.Update();

        if (Kcp.Itimediff(now, closingStartedAt + (uint)options.FinLingerMs) >= 0)
        {
            Close(KcpSessionState.Closed, KcpDisconnectReason.LocalClosed);
            return now + (uint)options.PingIntervalMs;
        }

        return now + (uint)options.IntervalMs;
    }

    private void Close(KcpSessionState newState, KcpDisconnectReason reason)
    {
        if (state == KcpSessionState.Closed || state == KcpSessionState.Dead)
            return;

        state = newState;

        kcp?.Release();
        kcp = null;
        (cipher as IDisposable)?.Dispose();
        cipher = null;
        fecEncoder = null;
        fecDecoder = null;
        decryptBuffer = null;
        assemblyBuffer = Array.Empty<byte>();
        unreliableScratch = Array.Empty<byte>();

        while (unreliableQueue.Count > 0)
        {
            var msg = unreliableQueue.Dequeue();
            ArrayPool<byte>.Shared.Return(msg.Buffer);
        }

        // 끊긴 뒤 같은 세션으로 다시 연결할 수 있으므로 내용만 비우고 큐는 살려 둔다.
        ReleaseOutbox(complete: false);

        Disconnected?.Invoke(this, reason);
    }

    /// <summary>세션이 쥐고 있던 자원을 모두 돌려준다.</summary>
    internal void ReleaseResources()
    {
        kcp?.Release();
        kcp = null;
        (cipher as IDisposable)?.Dispose();
        cipher = null;
        decryptBuffer = null;
        assemblyBuffer = Array.Empty<byte>();
        unreliableScratch = Array.Empty<byte>();
        while (unreliableQueue.Count > 0)
        {
            var msg = unreliableQueue.Dequeue();
            ArrayPool<byte>.Shared.Return(msg.Buffer);
        }

        ReleaseOutbox(complete: true);
    }

    /// <summary>마지막으로 상대에게서 무언가 받은 시각(ms).</summary>
    internal uint LastReceiveAt => lastReceiveAt;

    /// <summary>상대가 보낸 종료 사유(FIN 의 reason 바이트).</summary>
    public byte CloseReason => closeReason;

    private static uint NewNonce()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return value == 0 ? 1u : value;
    }

    /// <summary>클라이언트 세션인지 여부.</summary>
    internal bool IsClient => isClient;

    /// <summary>FEC 로 재전송 없이 복원해 낸 패킷 수(P5-2). FEC 를 쓰지 않으면 0.</summary>
    public long FecRecoveredPackets => fecDecoder?.RecoveredShards ?? 0;

    /// <summary>이 세션이 암호화를 쓰고 있는지 여부(P5-1).</summary>
    public bool IsEncrypted => cipher != null;
}
