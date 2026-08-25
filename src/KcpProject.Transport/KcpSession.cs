using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using KcpProject.Transport.Fec;

namespace KcpProject.Transport;

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

        options.Validate();
        RemoteKey = AddressKey.FromEndPoint(remote);

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
            fecEncoder.Encode(buffer.AsSpan(0, length), packet => sender(packet, RemoteEndPoint));
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
            var isData = fecDecoder.Decode(payload, out var original,
                recovered => kcp.Input(recovered, false, options.AckNoDelay));

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
            var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            try
            {
                if (!cipher.TryDecrypt(payload, buffer, out var plainLength))
                    return;   // 변조·리플레이 패킷은 조용히 버린다.
                kcp.Input(buffer.AsSpan(0, plainLength), true, options.AckNoDelay);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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

        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            if (!cipher.TryDecrypt(payload, buffer, out var plainLength))
                return;
            DeliverUnreliable(buffer.AsSpan(0, plainLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
        var handler = UnreliableReceived;
        if (handler != null)
        {
            var scratch = ArrayPool<byte>.Shared.Rent(payload.Length);
            try
            {
                payload.CopyTo(scratch);
                handler(this, new ReadOnlyMemory<byte>(scratch, 0, payload.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
            return;
        }

        // 폴링 모델: 큐에 쌓아 둔다. 상한을 넘으면 가장 오래된 것을 버린다(비신뢰 채널이므로 허용).
        if (unreliableQueue.Count >= MaxUnreliableQueue)
        {
            var dropped = unreliableQueue.Dequeue();
            ArrayPool<byte>.Shared.Return(dropped.Buffer);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
        payload.CopyTo(buffer);
        unreliableQueue.Enqueue(new PooledMessage { Buffer = buffer, Length = payload.Length });
    }

    private void DrainReliable()
    {
        var handler = ReliableReceived;
        if (handler == null || kcp == null)
            return;

        while (true)
        {
            var size = kcp.PeekSize();
            if (size < 0) break;

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, size));
            try
            {
                var n = kcp.Recv(buffer.AsSpan(0, buffer.Length));
                if (n < 0) break;
                handler(this, new ReadOnlyMemory<byte>(buffer, 0, n));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
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

        while (unreliableQueue.Count > 0)
        {
            var msg = unreliableQueue.Dequeue();
            ArrayPool<byte>.Shared.Return(msg.Buffer);
        }

        Disconnected?.Invoke(this, reason);
    }

    /// <summary>세션이 쥐고 있던 자원을 모두 돌려준다.</summary>
    internal void ReleaseResources()
    {
        kcp?.Release();
        kcp = null;
        (cipher as IDisposable)?.Dispose();
        cipher = null;
        while (unreliableQueue.Count > 0)
        {
            var msg = unreliableQueue.Dequeue();
            ArrayPool<byte>.Shared.Return(msg.Buffer);
        }
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
