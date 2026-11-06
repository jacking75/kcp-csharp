using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KcpCSharpR1.Transport;

/// <summary>
/// KCP 클라이언트(P4-3).
/// </summary>
/// <remarks>
/// <para>
/// 구동 모델은 두 가지이며 <b>택일해서 쓰고 섞지 않는다</b>.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>폴링형</b> — 게임 루프에서 매 프레임 <see cref="Update"/> 를 부른다.
/// MonoGame 은 <c>Game.Update()</c> 안에서 호출하면 된다. 수신 이벤트가 게임 스레드에서 발화한다.
/// </description></item>
/// <item><description>
/// <b>자체 루프형</b> — <see cref="RunAsync"/> 를 한 번 띄워 두고 이벤트로 받는다.
/// 콘솔 도구·부하 테스트에 적합하다. 콜백은 그 루프 스레드에서 발화한다.
/// </description></item>
/// </list>
/// <para>
/// <c>ConnectAsync</c> 자체가 핸드셰이크를 구동하므로 완료를 기다린 뒤 Update 또는 RunAsync 를 시작한다.
/// 재연결은 기존 세션이 Closed/Dead 이고 자체 루프가 끝난 뒤 가능하다.
/// 수명 API 는 직렬로 호출한다. 자체 루프를 취소한 뒤 태스크 완료를 기다리고 Dispose 한다.
/// </para>
/// </remarks>
public sealed class KcpClient : IDisposable
{
    /// <summary>자체 루프가 한 번에 기다리는 최대 시간(ms). 유휴 시 wakeup 횟수를 줄인다(진단 A-3).</summary>
    private const int MaxLoopWaitMs = 5;

    private readonly KcpSessionOptions options;
    private readonly Func<uint> clock;
    private readonly byte[] receiveBuffer;

    // G-5: 세션 구독 클로저를 만들지 않도록 델리게이트를 클라이언트당 1개씩만 만들어 둔다.
    private readonly KcpSession.PacketSender sendToCallback;
    private readonly Action<KcpSession> onSessionConnected;
    private readonly Action<KcpSession, KcpDisconnectReason> onSessionDisconnected;
    private readonly KcpMessageHandler onSessionReliable;
    private readonly KcpMessageHandler onSessionUnreliable;

    // G-2: ReadOnlyMemory 이벤트 구독자가 있을 때만 쓰는 상주 복사 버퍼.
    private byte[] eventScratch = Array.Empty<byte>();

    // M-2: 게임 스레드로 넘기는 이벤트 큐. EnableEventQueue 를 부른 경우에만 생긴다.
    private KcpEventQueue? events;

    private IUdpTransport? transport;
    private KcpSession? session;
    private bool disposed;
    private bool runLoopActive;
    private bool connectInProgress;

    /// <summary>클라이언트를 만든다.</summary>
    /// <param name="options">세션 설정. 생략하면 기본값.</param>
    /// <param name="clock">밀리초 시계. 생략하면 <see cref="KcpClock.NowMs"/>.</param>
    public KcpClient(KcpSessionOptions? options = null, Func<uint>? clock = null)
    {
        this.options = (options ?? new KcpSessionOptions()).Clone();
        this.options.Validate();
        this.clock = clock ?? (() => KcpClock.NowMs);
        receiveBuffer = new byte[this.options.Mtu + this.options.CipherOverhead + 64];

        sendToCallback = SendTo;
        onSessionConnected = OnSessionConnected;
        onSessionDisconnected = OnSessionDisconnected;
        onSessionReliable = OnSessionReliable;
        onSessionUnreliable = OnSessionUnreliable;
    }

    /// <summary>연결된 세션. 연결 전이면 null.</summary>
    public KcpSession? Session => session;

    /// <summary>연결되어 있는지 여부.</summary>
    public bool IsConnected => session?.State == KcpSessionState.Connected;

    /// <summary>로컬 주소.</summary>
    public IPEndPoint? LocalEndPoint => transport?.LocalEndPoint;

    /// <summary>연결이 성립했을 때 발생한다.</summary>
    public event Action<KcpSession>? Connected;

    /// <summary>연결이 끊겼을 때 발생한다.</summary>
    public event Action<KcpSession, KcpDisconnectReason>? Disconnected;

    /// <summary>신뢰 채널 메시지를 받았을 때 발생한다(메모리는 콜백 동안만 유효).</summary>
    public event Action<KcpSession, ReadOnlyMemory<byte>>? ReliableReceived;

    /// <summary>비신뢰 채널 메시지를 받았을 때 발생한다(메모리는 콜백 동안만 유효).</summary>
    public event Action<KcpSession, ReadOnlyMemory<byte>>? UnreliableReceived;

    /// <summary>
    /// 신뢰 채널 메시지를 <b>무복사</b>로 전달한다(진단 G-2).
    /// </summary>
    /// <remarks>
    /// <see cref="ReliableReceived"/> 구독자가 없으면 KCP 세그먼트에서 그대로 넘어오므로
    /// 수신 경로에 대여·복사·할당이 남지 않는다. Span 은 콜백 밖으로 보관할 수 없다.
    /// </remarks>
    public event KcpMessageHandler? ReliableReceivedSpan;

    /// <summary>비신뢰 채널 메시지를 <b>무복사</b>로 전달한다(진단 G-2).</summary>
    /// <remarks>Span 은 콜백 밖으로 보관할 수 없다.</remarks>
    public event KcpMessageHandler? UnreliableReceivedSpan;

    /// <summary>
    /// 게임 스레드로 이벤트를 넘기는 큐(진단 M-2). <see cref="EnableEventQueue(int)"/> 를 부르기 전에는 null.
    /// </summary>
    public KcpEventQueue? Events => events;

    /// <summary>
    /// 수신 이벤트를 게임 스레드로 넘기는 큐를 켠다(진단 M-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RunAsync"/> 로 구동할 때 특히 쓸모 있다. 콜백이 루프 스레드에서 발화하므로
    /// 게임 상태를 직접 만질 수 없는데, 이 큐를 쓰면 게임 루프에서 안전하게 꺼내 쓸 수 있다.
    /// </para>
    /// <code>
    /// while (client.Events!.TryRead(out var ev)) { Handle(ev); ev.ReturnPayload(); }
    /// </code>
    /// <para><see cref="ConnectAsync(IPEndPoint, CancellationToken)"/> 전에 부르는 것을 권한다.</para>
    /// </remarks>
    /// <param name="capacity">큐 용량(이벤트 수). 가득 차면 이벤트를 버리고 <see cref="KcpEventQueue.Dropped"/> 가 오른다.</param>
    /// <returns>만들어진 큐.</returns>
    public KcpEventQueue EnableEventQueue(int capacity = 1024)
        => EnableEventQueue(capacity, KcpEventQueueOverflowPolicy.DropNewest);

    /// <summary>오버플로 정책을 지정해 게임 스레드용 수신 큐를 켠다.</summary>
    /// <param name="capacity">이벤트 수 상한.</param>
    /// <param name="overflowPolicy">신뢰 이벤트 누락 시 연결을 종료할지 결정한다.</param>
    /// <returns>큐. 이미 켜져 있으면 최초 설정을 유지한 기존 큐.</returns>
    /// <remarks>ConnectAsync 전에 호출한다. 종료 알림도 누락될 수 있으므로 HasCriticalOverflow 를 관측한다.</remarks>
    public KcpEventQueue EnableEventQueue(int capacity, KcpEventQueueOverflowPolicy overflowPolicy)
    {
        ThrowIfDisposed();
        return events ??= new KcpEventQueue(capacity, overflowPolicy);
    }

    /// <summary>서버에 연결한다.</summary>
    /// <param name="host">호스트 이름 또는 IP 리터럴.</param>
    /// <param name="port">포트.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>연결된 세션.</returns>
    /// <exception cref="TimeoutException">핸드셰이크가 시간 안에 끝나지 않은 경우.</exception>
    public async Task<KcpSession> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>서버에 연결한다.</summary>
    /// <param name="remote">서버 주소.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>연결된 세션.</returns>
    /// <exception cref="TimeoutException">핸드셰이크가 시간 안에 끝나지 않은 경우.</exception>
    public async Task<KcpSession> ConnectAsync(IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        if (remote == null) throw new ArgumentNullException(nameof(remote));
        ThrowIfCannotConnect();

        var socketTransport = new UdpSocketTransport(remote.AddressFamily);
        try
        {
            socketTransport.Bind(new IPEndPoint(
                remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        }
        catch
        {
            socketTransport.Dispose();
            throw;
        }

        return await ConnectAsync(socketTransport, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>지정한 전송 계층으로 연결한다(테스트에서 가상 전송을 끼울 때 쓴다).</summary>
    /// <param name="udpTransport">전송 계층. 인수·수명 상태 검증을 통과하면 소유권이 클라이언트로 넘어가며 취소·실패 시에도 정리한다.</param>
    /// <param name="remote">서버 주소.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>연결된 세션.</returns>
    /// <exception cref="TimeoutException">핸드셰이크가 시간 안에 끝나지 않은 경우.</exception>
    public async Task<KcpSession> ConnectAsync(IUdpTransport udpTransport, IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        if (udpTransport == null) throw new ArgumentNullException(nameof(udpTransport));
        if (remote == null) throw new ArgumentNullException(nameof(remote));
        ThrowIfCannotConnect();

        connectInProgress = true;
        try
        {
            // 종료된 이전 연결의 소켓과 대여 버퍼를 재연결 전에 돌려준다.
            session?.ReleaseResources();
            session = null;
            transport?.Dispose();
            transport = udpTransport;
            cancellationToken.ThrowIfCancellationRequested();
            return await ConnectCoreAsync(udpTransport, remote, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session?.ReleaseResources();
            session = null;
            transport?.Dispose();
            transport = null;
            throw;
        }
        finally
        {
            connectInProgress = false;
        }
    }

    private async Task<KcpSession> ConnectCoreAsync(IUdpTransport udpTransport, IPEndPoint remote, CancellationToken cancellationToken)
    {
        var connecting = CreateSession(remote);
        session = connecting;

        // A-1: 1ms Task.Delay 폴링을 없앤다.
        //  - 완료·실패는 세션 이벤트가 완성하는 TaskCompletionSource 로 즉시 알아챈다.
        //  - 그 사이의 대기는 타이머가 아니라 소켓이 한다(패킷이 오면 그 순간 깨어난다).
        var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHandshakeDone(KcpSession _) => handshake.TrySetResult(true);
        void OnHandshakeFailed(KcpSession _, KcpDisconnectReason __) => handshake.TrySetResult(false);

        connecting.Connected += OnHandshakeDone;
        connecting.Disconnected += OnHandshakeFailed;

        try
        {
            var start = clock();
            connecting.StartConnect(start);
            var deadline = start + (uint)options.HandshakeTimeoutMs + 500u;

            // RawMode 세션은 생성 시점에 이미 Connected 라 이벤트가 발화하지 않는다.
            // 상태도 함께 보고 빠져나가야 곧바로 반환된다.
            while (connecting.State == KcpSessionState.Connecting && !handshake.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();

                // Pump 안에서 SYN_ACK 을 처리하면 위 이벤트가 이 스레드에서 곧바로 발화한다.
                var next = Pump();
                if (connecting.State != KcpSessionState.Connecting || handshake.Task.IsCompleted)
                    break;

                var now = clock();
                var remaining = Kcp.Itimediff(deadline, now);
                if (remaining <= 0)
                    break;

                // 다음 SYN 재시도 시각까지 기다린다(재시도 간격은 세션이 알려 준다).
                var waitMs = Kcp.Itimediff(next, now);
                if (waitMs > remaining) waitMs = remaining;
                if (waitMs > MaxLoopWaitMs) waitMs = MaxLoopWaitMs;
                if (waitMs < 1) waitMs = 1;

                var before = clock();
                var ready = await WaitForDataAsync(udpTransport, waitMs, cancellationToken).ConfigureAwait(false);
                if (!ready && Kcp.Itimediff(clock(), before) <= 0)
                {
                    // 실제로 기다려 주지 않는 전송 계층(테스트 더블 등)에서 바쁜 대기가 되지 않게 한다.
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            connecting.Connected -= OnHandshakeDone;
            connecting.Disconnected -= OnHandshakeFailed;
        }

        if (connecting.State != KcpSessionState.Connected)
        {
            throw new TimeoutException($"{remote} 로의 KCP 핸드셰이크가 실패했습니다(상태: {connecting.State}).");
        }

        return connecting;
    }

    /// <summary>소켓 대기를 비동기로 감싼다(연결당 몇 번만 도는 일회성 경로).</summary>
    private static Task<bool> WaitForDataAsync(IUdpTransport udp, int milliseconds, CancellationToken cancellationToken)
        => Task.Run(() => udp.WaitForData(milliseconds * 1000), cancellationToken);

    private KcpSession CreateSession(IPEndPoint remote)
    {
        // G-5: 캐시해 둔 델리게이트로 구독한다(클로저 클래스가 생기지 않는다).
        // G-2: Span 이벤트로 구독해 세션이 무복사 경로를 탈 수 있게 한다.
        var created = new KcpSession(remote, options, sendToCallback, clock, isClient: true);
        created.Connected += onSessionConnected;
        created.Disconnected += onSessionDisconnected;
        created.ReliableReceivedSpan += onSessionReliable;
        created.UnreliableReceivedSpan += onSessionUnreliable;
        return created;
    }

    private void OnSessionConnected(KcpSession s)
    {
        Connected?.Invoke(s);
        events?.Publish(KcpEventType.Connected, s, default, default);
    }

    private void OnSessionDisconnected(KcpSession s, KcpDisconnectReason reason)
    {
        Disconnected?.Invoke(s, reason);
        events?.Publish(KcpEventType.Disconnected, s, default, reason);
    }

    private void OnSessionReliable(KcpSession s, ReadOnlySpan<byte> data)
    {
        ReliableReceivedSpan?.Invoke(s, data);
        RaiseMemory(ReliableReceived, s, data);
        events?.Publish(KcpEventType.Reliable, s, data, default);
    }

    private void OnSessionUnreliable(KcpSession s, ReadOnlySpan<byte> data)
    {
        UnreliableReceivedSpan?.Invoke(s, data);
        RaiseMemory(UnreliableReceived, s, data);
        events?.Publish(KcpEventType.Unreliable, s, data, default);
    }

    /// <summary>ReadOnlyMemory 이벤트 구독자가 있을 때만 상주 스크래치로 복사해 전달한다.</summary>
    private void RaiseMemory(Action<KcpSession, ReadOnlyMemory<byte>>? handler, KcpSession s, ReadOnlySpan<byte> data)
    {
        if (handler == null) return;

        if (eventScratch.Length < data.Length)
            eventScratch = new byte[Math.Max(data.Length, Math.Max(1024, eventScratch.Length * 2))];

        data.CopyTo(eventScratch);
        handler(s, new ReadOnlyMemory<byte>(eventScratch, 0, data.Length));
    }

    private void SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        transport?.SendTo(data, remote);
    }

    /// <summary>
    /// 폴링형 구동. 게임 루프에서 매 프레임 호출한다.
    /// 도착한 패킷을 모두 처리하고 세션 타이머를 한 번 돌린다.
    /// </summary>
    public void Update()
    {
        ThrowIfDisposed();
        if (connectInProgress)
            throw new InvalidOperationException("ConnectAsync 완료 후 Update 를 호출해야 합니다.");
        if (runLoopActive)
            throw new InvalidOperationException("RunAsync 로 구동 중에는 Update 를 호출하지 않습니다(구동 모델 혼용 금지).");

        Pump();
    }

    /// <summary>수신·갱신을 한 번 돌린다.</summary>
    /// <returns>다음에 구동하면 좋은 시각(ms). 세션이 없으면 현재 시각 + 기본 간격.</returns>
    private uint Pump()
    {
        var current = session;
        var udp = transport;
        if (current == null || udp == null)
            return clock() + (uint)options.IntervalMs;

        // G-5: 시계는 진입 시 1회만 읽는다(코어 PF-01 과 같은 원칙).
        var now = clock();

        // 도착한 패킷을 전부 빨아들인다(호출당 1패킷 제한 없음, 진단 PF-08).
        while (udp.TryReceive(receiveBuffer, out var received, out var remote))
        {
            if (received <= 0) continue;
            if (remote != current.RemoteKey) continue;   // 서버가 아닌 곳에서 온 패킷은 무시
            current.OnFrame(receiveBuffer.AsSpan(0, received), now);
        }

        return current.Update(now);
    }

    /// <summary>자체 루프형 구동. 취소될 때까지 수신·갱신을 반복한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>루프가 끝나면 완료되는 태스크.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (connectInProgress)
            throw new InvalidOperationException("ConnectAsync 완료 후 RunAsync 를 호출해야 합니다.");
        if (runLoopActive)
            throw new InvalidOperationException("이미 RunAsync 로 구동 중입니다.");

        runLoopActive = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var udp = transport;
                if (udp == null) break;

                // A-3: 고정 1ms 대신 세션이 알려 준 다음 갱신 시각까지 기다린다(유휴 시 wakeup 급감).
                var next = Pump();

                var waitMs = Kcp.Itimediff(next, clock());
                if (waitMs < 1) waitMs = 1;                     // 최소 1ms 는 유지해 바쁜 대기를 막는다.
                if (waitMs > MaxLoopWaitMs) waitMs = MaxLoopWaitMs;

                if (!udp.WaitForData(waitMs * 1000))
                    await Task.Yield();
            }
        }
        finally
        {
            runLoopActive = false;
        }
    }

    /// <summary>정상 종료(FIN)를 보낸다.</summary>
    /// <param name="reason">애플리케이션 정의 종료 사유.</param>
    public void Disconnect(byte reason = 0)
    {
        session?.Disconnect(reason);
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

#if NET8_0_OR_GREATER
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
#else
        var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
#endif

        // 진단 B-10 과 같은 정책: IPv4 우선.
        var v4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
        if (v4 != null) return v4;
        if (addresses.Length > 0) return addresses[0];

        throw new SocketException((int)SocketError.HostNotFound);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(KcpClient));
    }

    private void ThrowIfCannotConnect()
    {
        ThrowIfDisposed();
        if (connectInProgress || runLoopActive ||
            (session != null && session.State != KcpSessionState.Closed && session.State != KcpSessionState.Dead))
            throw new InvalidOperationException("기존 연결과 구동 루프가 종료된 뒤에 다시 연결해야 합니다.");
    }

    /// <summary>소켓과 세션 자원을 정리한다.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        session?.ReleaseResources();
        session = null;

        // M-2: 큐에 남은 대여 버퍼를 돌려준다(클라이언트는 여기서 수명이 끝난다).
        events?.Release(complete: true);

        transport?.Dispose();
        transport = null;
    }
}
