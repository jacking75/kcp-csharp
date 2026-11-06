using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KcpCSharpR1.Transport;

/// <summary>
/// 단일 소켓으로 다수 클라이언트를 받는 KCP 서버(P4-4).
/// </summary>
/// <remarks>
/// <para>
/// <b>단일 컨슈머 모델</b>이다. 수신 루프 스레드 하나가 소켓 읽기·세션 디스패치·타이머 갱신을 모두
/// 담당하므로 코어의 "인스턴스 1개 = 소유 스레드 1개" 규약(로드맵 2.3절)이 지켜진다.
/// </para>
/// <para>
/// <b>이벤트는 수신 루프 스레드에서 발화한다.</b> 게임 로직은 별도 스레드에서 돌리고,
/// 이벤트 핸들러에서는 큐에 넣기만 하는 것이 정석이다(책 6장에서 패턴 제시).
/// </para>
/// <para>
/// SYN 은 <see cref="HandshakeCookie"/> 로 무상태 처리한다. 쿠키를 담아 다시 온 SYN 에 대해서만
/// 세션을 만들기 때문에, 소스 주소를 위조한 SYN 홍수로는 세션 테이블을 채울 수 없다(진단 S-04).
/// </para>
/// </remarks>
public sealed class KcpServer : IDisposable
{
    private readonly KcpServerOptions options;
    private readonly Func<uint> clock;
    private readonly Dictionary<AddressKey, KcpSession> sessions = new Dictionary<AddressKey, KcpSession>();
    private readonly List<KcpSession> pendingRemoval = new List<KcpSession>();
    private readonly SessionScheduler scheduler = new SessionScheduler();
    private readonly HandshakeCookie cookies;
    private readonly byte[] receiveBuffer;
    private readonly byte[] sendScratch;
    private readonly int port;

    private IUdpTransport? transport;
    private CancellationTokenSource? cts;
    private Task? loopTask;
    private uint nextConv = 1;      // 0 은 예약
    private bool disposed;
    private readonly object lifecycleGate = new object();
    private int receiveLoopThreadId;
    private bool pumping;
    private volatile bool stopRequested;

    // P5-3 관측성
    private readonly List<int> rttScratch = new List<int>(256);
    private readonly List<KcpSession> metricsScratch = new List<KcpSession>(256);
    private uint nextMetricsAt;

    // G-5: 세션마다 구독 클로저를 만들지 않도록 델리게이트를 서버당 1개씩만 만들어 둔다.
    private readonly KcpSession.PacketSender sendToCallback;
    private readonly Action<KcpSession, KcpDisconnectReason> onSessionDisconnected;
    private readonly KcpMessageHandler onSessionReliable;
    private readonly KcpMessageHandler onSessionUnreliable;

    // G-2: ReadOnlyMemory 이벤트 구독자가 있을 때만 쓰는 상주 복사 버퍼.
    private byte[] eventScratch = Array.Empty<byte>();

    // M-2: 게임 스레드로 넘기는 이벤트 큐. EnableEventQueue 를 부른 경우에만 생긴다.
    private KcpEventQueue? events;

    /// <summary>서버를 만든다.</summary>
    /// <param name="port">바인딩할 UDP 포트.</param>
    /// <param name="options">서버 설정. 생략하면 기본값.</param>
    /// <param name="clock">밀리초 시계. 생략하면 <see cref="KcpClock.NowMs"/>.</param>
    public KcpServer(int port, KcpServerOptions? options = null, Func<uint>? clock = null)
    {
        this.port = port;
        this.options = (options ?? new KcpServerOptions()).Clone();
        this.options.Validate();
        if (this.options.Session.Cipher != null)
            throw new ArgumentException("서버는 세션별 암호화기를 만들어야 합니다. Session.Cipher 대신 CipherFactory 를 사용하세요.", nameof(options));
        this.clock = clock ?? (() => KcpClock.NowMs);
        cookies = new HandshakeCookie();

        sendToCallback = SendTo;
        onSessionDisconnected = OnSessionDisconnected;
        onSessionReliable = OnSessionReliable;
        onSessionUnreliable = OnSessionUnreliable;

        var overhead = this.options.Session.CipherOverhead;
        receiveBuffer = new byte[this.options.Session.Mtu + overhead + 64];
        sendScratch = new byte[64];
    }

    /// <summary>바인딩된 로컬 주소. <see cref="Start()"/> 전이면 null.</summary>
    public IPEndPoint? LocalEndPoint => transport?.LocalEndPoint;

    /// <summary>현재 세션 수.</summary>
    public int SessionCount => sessions.Count;

    /// <summary>현재 세션 목록의 스냅숏.</summary>
    /// <remarks>수신 루프 스레드 밖에서 호출하면 순회 도중 변경될 수 있으므로 복사본을 만든다.</remarks>
    public IReadOnlyCollection<KcpSession> Sessions
    {
        get
        {
            lock (sessions)
            {
                return new List<KcpSession>(sessions.Values);
            }
        }
    }

    /// <summary>새 세션이 연결되면 발생한다.</summary>
    public event Action<KcpSession>? SessionConnected;

    /// <summary>세션이 끊기면 발생한다.</summary>
    public event Action<KcpSession, KcpDisconnectReason>? SessionDisconnected;

    /// <summary>신뢰 채널 메시지를 받으면 발생한다(메모리는 콜백 동안만 유효).</summary>
    public event Action<KcpSession, ReadOnlyMemory<byte>>? ReliableReceived;

    /// <summary>비신뢰 채널 메시지를 받으면 발생한다(메모리는 콜백 동안만 유효).</summary>
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
    /// 이벤트는 수신 루프 스레드에서 발화하므로 게임 상태를 직접 만지면 레이스가 난다.
    /// 이 큐를 켜면 연결·종료·메시지가 전부 큐로 들어오고, 게임 루프에서 다음처럼 꺼내 쓰면 된다.
    /// </para>
    /// <code>
    /// while (server.Events!.TryRead(out var ev)) { Handle(ev); ev.ReturnPayload(); }
    /// </code>
    /// <para>
    /// C# 이벤트(<see cref="ReliableReceived"/> 등)와 함께 써도 되지만, 큐만 쓰는 편이 규약이 단순하다.
    /// <b><see cref="Start()"/> 전에</b> 불러야 한다.
    /// </para>
    /// </remarks>
    /// <param name="capacity">큐 용량(이벤트 수). 가득 차면 이벤트를 버리고 <see cref="KcpEventQueue.Dropped"/> 가 오른다.</param>
    /// <returns>만들어진 큐.</returns>
    /// <exception cref="InvalidOperationException">이미 시작된 서버인 경우.</exception>
    public KcpEventQueue EnableEventQueue(int capacity = 4096)
        => EnableEventQueue(capacity, KcpEventQueueOverflowPolicy.DropNewest);

    /// <summary>오버플로 정책을 지정해 게임 스레드용 수신 큐를 켠다.</summary>
    /// <param name="capacity">이벤트 수 상한.</param>
    /// <param name="overflowPolicy">신뢰 이벤트 누락 시 해당 세션을 종료할지 결정한다.</param>
    /// <returns>큐. 이미 켜져 있으면 최초 설정을 유지한 기존 큐.</returns>
    /// <remarks>Start 전에 호출한다. 종료 알림도 누락될 수 있으므로 HasCriticalOverflow 를 관측한다.</remarks>
    public KcpEventQueue EnableEventQueue(int capacity, KcpEventQueueOverflowPolicy overflowPolicy)
    {
        ThrowIfDisposed();
        if (transport != null)
            throw new InvalidOperationException("이벤트 큐는 서버를 시작하기 전에 켜야 합니다.");

        return events ??= new KcpEventQueue(capacity, overflowPolicy);
    }

    /// <summary>수신 루프에서 예외가 발생하면 발생한다(루프는 계속 돈다).</summary>
    public event Action<Exception>? Error;

    /// <summary>
    /// <see cref="KcpServerOptions.MetricsIntervalMs"/> 주기로 서버 상태 스냅숏을 알린다(P5-3).
    /// 수신 루프 스레드에서 발화하므로 핸들러는 짧아야 한다.
    /// </summary>
    public event Action<KcpServerMetrics>? MetricsSampled;

    /// <summary>소켓을 열고 수신 루프를 시작한다.</summary>
    public void Start()
    {
        Start(null);
    }

    /// <summary>지정한 전송 계층으로 시작한다(테스트에서 가상 전송을 끼울 때 쓴다).</summary>
    /// <param name="udpTransport">전송 계층. null 이면 실소켓을 만든다. 소유권이 서버로 넘어간다.</param>
    public void Start(IUdpTransport? udpTransport)
    {
        Start(udpTransport, runLoop: true);
    }

    /// <summary>
    /// 전송 계층과 구동 방식을 지정해 시작한다.
    /// </summary>
    /// <param name="udpTransport">전송 계층. null 이면 실소켓을 만든다.</param>
    /// <param name="runLoop">
    /// true 면 전용 스레드에서 수신 루프를 돌린다. false 면 루프를 만들지 않으므로
    /// 호출자가 <see cref="Pump"/> 를 직접 돌려야 한다(자체 게임 루프에 얹거나 결정적 테스트를 할 때).
    /// </param>
    public void Start(IUdpTransport? udpTransport, bool runLoop)
    {
        lock (lifecycleGate)
            StartCore(udpTransport, runLoop);
    }

    private void StartCore(IUdpTransport? udpTransport, bool runLoop)
    {
        ThrowIfDisposed();
        if (transport != null || (loopTask != null && !loopTask.IsCompleted))
            throw new InvalidOperationException("이미 시작된 서버입니다.");

        cts?.Dispose();
        cts = null;
        loopTask = null;
        stopRequested = false;

        if (udpTransport == null)
        {
            var socketTransport = new UdpSocketTransport(
                AddressFamily.InterNetwork,
                options.SocketReceiveBufferSize,
                options.SocketSendBufferSize);
            try
            {
                socketTransport.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch
            {
                socketTransport.Dispose();
                throw;
            }
            transport = socketTransport;
        }
        else
        {
            transport = udpTransport;
        }

        if (!runLoop)
            return;

        cts = new CancellationTokenSource();
        var token = cts.Token;
        loopTask = Task.Factory.StartNew(
            () => ReceiveLoop(token),
            // 시작 직후 취소해도 본체의 finally 는 반드시 실행해야 한다.
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 수신·갱신을 한 번 돌린다. <see cref="Start(IUdpTransport?, bool)"/> 를 <c>runLoop: false</c> 로
    /// 시작했을 때 호출자가 직접 구동하는 진입점이다(블로킹하지 않는다).
    /// </summary>
    public void Pump()
    {
        lock (lifecycleGate)
        {
            ThrowIfDisposed();
            if (loopTask != null)
                throw new InvalidOperationException("수신 루프가 실행 중이면 Pump 를 호출할 수 없습니다.");
            if (pumping)
                throw new InvalidOperationException("Pump 를 콜백에서 다시 호출할 수 없습니다.");
            var udp = transport;
            if (udp == null) return;

            pumping = true;
            try
            {
                DrainSocket(udp);
                RunDueSessions();
                SampleMetrics();
            }
            finally
            {
                pumping = false;
                if (stopRequested)
                    ReleaseStoppedResources();
            }
        }
    }

    /// <summary>수신 루프를 멈추고 세션을 모두 정리한다.</summary>
    /// <remarks>
    /// 외부 스레드에서는 진행 중인 콜백과 루프가 끝날 때까지 기다린다.
    /// 수신 루프 콜백 안에서는 종료만 요청하고 즉시 반환하며, 콜백이 반환한 뒤 자원을 정리한다.
    /// </remarks>
    public void Stop()
    {
        Task? stopping;
        lock (lifecycleGate)
        {
            stopRequested = true;
            cts?.Cancel();
            stopping = loopTask;
            if (pumping) return;
            if (stopping == null)
            {
                ReleaseStoppedResources();
                return;
            }

            if (Environment.CurrentManagedThreadId == Volatile.Read(ref receiveLoopThreadId))
                return;
        }

        try
        {
            stopping.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // 취소로 인한 예외는 정상 종료 경로다.
        }
        finally
        {
            lock (lifecycleGate)
            {
                if (ReferenceEquals(loopTask, stopping))
                {
                    loopTask = null;
                    cts?.Dispose();
                    cts = null;
                    // 루프가 먼저 끝난 뒤 Dispose 했을 수도 있으므로 영구 자원도 정리한다.
                    if (disposed)
                        ReleaseStoppedResources();
                }
            }
        }
    }

    private void ReleaseStoppedResources()
    {
        lock (sessions)
        {
            foreach (var session in sessions.Values)
                session.ReleaseResources();
            sessions.Clear();
        }
        scheduler.Clear();
        pendingRemoval.Clear();
        metricsScratch.Clear();
        rttScratch.Clear();
        nextMetricsAt = 0;

        // M-2: 큐에 남은 대여 버퍼를 돌려준다(다시 Start 할 수 있으므로 큐 자체는 살려 둔다).
        events?.Release(complete: disposed);

        transport?.Dispose();
        transport = null;
        cts?.Dispose();
        cts = null;
        if (disposed)
            cookies.Dispose();
    }

    /// <summary>수신 루프 본체. 소켓 읽기와 타이머를 한 스레드에서 처리한다.</summary>
    private void ReceiveLoop(CancellationToken token)
    {
        Volatile.Write(ref receiveLoopThreadId, Environment.CurrentManagedThreadId);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var udp = transport;
                    if (udp == null) break;

                    var now = clock();
                    var waitMicros = scheduler.TimeToNext(now, options.PollMicroseconds / 1000) * 1000;
                    if (waitMicros > options.PollMicroseconds) waitMicros = options.PollMicroseconds;

                    if (udp.WaitForData(waitMicros))
                    {
                        DrainSocket(udp);
                    }

                    RunDueSessions();
                    SampleMetrics();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                }
            }
        }
        finally
        {
            lock (lifecycleGate)
            {
                ReleaseStoppedResources();
                Volatile.Write(ref receiveLoopThreadId, 0);
            }
        }
    }

    private void DrainSocket(IUdpTransport udp)
    {
        var processed = 0;
        while (!stopRequested && processed < options.MaxReceiveBatch &&
               udp.TryReceive(receiveBuffer, out var received, out var remote))
        {
            processed++;
            if (received <= 0) continue;

            var now = clock();
            var frame = receiveBuffer.AsSpan(0, received);

            if (sessions.TryGetValue(remote, out var session))
            {
                // 이미 아는 상대다. SYN 재전송이면(SYN_ACK 유실) 다시 수락 응답을 보낸다.
                if (Framing.PeekType(frame) == FrameType.Syn)
                {
                    SendSynAck(session, remote);
                    continue;
                }

                session.OnFrame(frame, now);
                scheduler.Schedule(session, now);   // 방금 무언가 왔으니 즉시 갱신 대상
                continue;
            }

            if (Framing.PeekType(frame) == FrameType.Syn)
            {
                HandleSyn(frame, remote, now);
            }

            // 미등록 주소의 그 밖의 프레임은 무응답으로 버린다(스캐닝 대응).
        }
    }

    /// <summary>SYN 처리. 쿠키가 없으면 쿠키만 돌려주고 상태를 만들지 않는다(P4-1b).</summary>
    private void HandleSyn(ReadOnlySpan<byte> frame, AddressKey remote, uint now)
    {
        if (!Framing.TryReadSyn(frame, out var version, out var clientNonce, out var cookie))
            return;
        if (version != Framing.ProtocolVersion)
            return;

        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (cookie == 0 || !cookies.Validate(remote, cookie, unixSeconds))
        {
            var issued = cookies.Compute(remote, unixSeconds);
            var len = Framing.WriteSyn(sendScratch, FrameType.SynCookie, clientNonce, issued);
            // 세션이 아직 없는 경로다. 주소 키 오버로드로 보내 IPEndPoint 할당을 없앤다(진단 G-1).
            // SYN 홍수 시 이 경로가 가장 뜨거우므로 여기서 할당이 생기면 곧바로 GC 압력이 된다.
            transport?.SendTo(sendScratch.AsSpan(0, len), remote);
            return;
        }

        // 쿠키 검증 통과 → 이제서야 세션을 만든다.
        if (sessions.Count >= options.MaxSessions)
            return;   // 상한 초과는 무응답 폐기

        var endPoint = remote.ToEndPoint();
        var sessionOptions = options.Session.Clone();
        var session = new KcpSession(endPoint, sessionOptions, sendToCallback, clock, isClient: false);

        // G-5: 캐시해 둔 델리게이트를 그대로 붙인다(세션마다 클로저 클래스 + 델리게이트 할당이 생기지 않는다).
        // G-2: Span 이벤트로 구독해 세션이 무복사 경로를 탈 수 있게 한다.
        session.Disconnected += onSessionDisconnected;
        session.ReliableReceivedSpan += onSessionReliable;
        session.UnreliableReceivedSpan += onSessionUnreliable;

        var conv = nextConv++;
        if (nextConv == 0) nextConv = 1;   // 0 은 예약

        lock (sessions)
        {
            sessions[remote] = session;
        }

        try
        {
            session.AcceptAsServer(conv, now);
        }
        catch
        {
            // 팩터리 등 세션 초기화 실패가 미완성 세션을 주소 테이블에 남기지 않게 한다.
            lock (sessions)
                sessions.Remove(remote);
            session.ReleaseResources();
            throw;
        }
        scheduler.Schedule(session, now);

        SendSynAck(session, remote);
        SessionConnected?.Invoke(session);
        events?.Publish(KcpEventType.Connected, session, default, default);
    }

    private void SendSynAck(KcpSession session, AddressKey remote)
    {
        var len = Framing.WriteSynAck(sendScratch, session.Conv, (uint)Environment.TickCount);
        transport?.SendTo(sendScratch.AsSpan(0, len), session.RemoteEndPoint);
        _ = remote;
    }

    private void RunDueSessions()
    {
        var now = clock();

        while (!stopRequested && scheduler.TryPopDue(now, out var session))
        {
            var next = session.Update(now);

            if (session.State == KcpSessionState.Closed || session.State == KcpSessionState.Dead)
            {
                pendingRemoval.Add(session);
                continue;
            }

            // 최소 해상도를 보장한다(너무 먼 미래로 잡히지 않게).
            if (Kcp.Itimediff(next, now) <= 0)
                next = now + (uint)options.Session.IntervalMs;

            scheduler.Schedule(session, next);
        }

        if (pendingRemoval.Count > 0)
        {
            foreach (var session in pendingRemoval)
                RemoveSession(session);
            pendingRemoval.Clear();
        }
    }

    /// <summary>주기가 되면 관측 스냅숏을 만들어 이벤트로 알린다(P5-3).</summary>
    private void SampleMetrics()
    {
        var handler = MetricsSampled;
        if (stopRequested || handler == null || options.MetricsIntervalMs <= 0)
            return;

        var now = clock();
        if (nextMetricsAt != 0 && Kcp.Itimediff(now, nextMetricsAt) < 0)
            return;

        nextMetricsAt = now + (uint)options.MetricsIntervalMs;

        // G-5: 주기마다 리스트를 새로 만들지 않는다(rttScratch 와 같은 방식).
        metricsScratch.Clear();
        lock (sessions)
        {
            foreach (var session in sessions.Values)
                metricsScratch.Add(session);
        }

        handler(MetricsCollector.Collect(metricsScratch, now, rttScratch));
    }

    private void OnSessionDisconnected(KcpSession session, KcpDisconnectReason reason)
    {
        SessionDisconnected?.Invoke(session, reason);
        events?.Publish(KcpEventType.Disconnected, session, default, reason);
    }

    private void OnSessionReliable(KcpSession session, ReadOnlySpan<byte> data)
    {
        ReliableReceivedSpan?.Invoke(session, data);
        RaiseMemory(ReliableReceived, session, data);
        events?.Publish(KcpEventType.Reliable, session, data, default);
    }

    private void OnSessionUnreliable(KcpSession session, ReadOnlySpan<byte> data)
    {
        UnreliableReceivedSpan?.Invoke(session, data);
        RaiseMemory(UnreliableReceived, session, data);
        events?.Publish(KcpEventType.Unreliable, session, data, default);
    }

    /// <summary>ReadOnlyMemory 이벤트 구독자가 있을 때만 상주 스크래치로 복사해 전달한다.</summary>
    private void RaiseMemory(Action<KcpSession, ReadOnlyMemory<byte>>? handler, KcpSession session, ReadOnlySpan<byte> data)
    {
        if (handler == null) return;

        if (eventScratch.Length < data.Length)
            eventScratch = new byte[Math.Max(data.Length, Math.Max(1024, eventScratch.Length * 2))];

        data.CopyTo(eventScratch);
        handler(session, new ReadOnlyMemory<byte>(eventScratch, 0, data.Length));
    }

    private void RemoveSession(KcpSession session)
    {
        scheduler.Remove(session);
        lock (sessions)
        {
            sessions.Remove(session.RemoteKey);
        }
        session.ReleaseResources();
    }

    private void SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        transport?.SendTo(data, remote);
    }

    /// <summary>지정 주소의 세션을 찾는다.</summary>
    /// <param name="remote">주소.</param>
    /// <param name="session">찾은 세션.</param>
    /// <returns>있으면 true.</returns>
    public bool TryGetSession(IPEndPoint remote, out KcpSession session)
    {
        lock (sessions)
        {
            return sessions.TryGetValue(AddressKey.FromEndPoint(remote), out session!);
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(KcpServer));
    }

    /// <summary>서버를 멈추고 자원을 정리한다.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Stop();
    }
}
