using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KcpProject.Transport;

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

    // P5-3 관측성
    private readonly List<int> rttScratch = new List<int>(256);
    private uint nextMetricsAt;

    /// <summary>서버를 만든다.</summary>
    /// <param name="port">바인딩할 UDP 포트.</param>
    /// <param name="options">서버 설정. 생략하면 기본값.</param>
    /// <param name="clock">밀리초 시계. 생략하면 <see cref="KcpClock.NowMs"/>.</param>
    public KcpServer(int port, KcpServerOptions? options = null, Func<uint>? clock = null)
    {
        this.port = port;
        this.options = options ?? new KcpServerOptions();
        this.options.Session.Validate();
        this.clock = clock ?? (() => KcpClock.NowMs);
        cookies = new HandshakeCookie();

        var overhead = this.options.Session.Cipher?.Overhead ?? 0;
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
        ThrowIfDisposed();
        if (loopTask != null)
            throw new InvalidOperationException("이미 시작된 서버입니다.");

        if (udpTransport == null)
        {
            var socketTransport = new UdpSocketTransport(
                AddressFamily.InterNetwork,
                options.SocketReceiveBufferSize,
                options.SocketSendBufferSize);
            socketTransport.Bind(new IPEndPoint(IPAddress.Any, port));
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
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 수신·갱신을 한 번 돌린다. <see cref="Start(IUdpTransport?, bool)"/> 를 <c>runLoop: false</c> 로
    /// 시작했을 때 호출자가 직접 구동하는 진입점이다(블로킹하지 않는다).
    /// </summary>
    public void Pump()
    {
        var udp = transport;
        if (udp == null) return;

        DrainSocket(udp);
        RunDueSessions();
        SampleMetrics();
    }

    /// <summary>수신 루프를 멈추고 세션을 모두 정리한다.</summary>
    public void Stop()
    {
        cts?.Cancel();

        try
        {
            loopTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 취소로 인한 예외는 정상 종료 경로다.
        }

        loopTask = null;
        cts?.Dispose();
        cts = null;

        lock (sessions)
        {
            foreach (var session in sessions.Values)
                session.ReleaseResources();
            sessions.Clear();
        }
        scheduler.Clear();

        transport?.Dispose();
        transport = null;
    }

    /// <summary>수신 루프 본체. 소켓 읽기와 타이머를 한 스레드에서 처리한다.</summary>
    private void ReceiveLoop(CancellationToken token)
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

    private void DrainSocket(IUdpTransport udp)
    {
        var processed = 0;
        while (processed < options.MaxReceiveBatch &&
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
            transport?.SendTo(sendScratch.AsSpan(0, len), remote.ToEndPoint());
            return;
        }

        // 쿠키 검증 통과 → 이제서야 세션을 만든다.
        if (sessions.Count >= options.MaxSessions)
            return;   // 상한 초과는 무응답 폐기

        var endPoint = remote.ToEndPoint();
        var sessionOptions = options.Session.Clone();
        var session = new KcpSession(endPoint, sessionOptions, SendTo, clock, isClient: false);

        session.Disconnected += OnSessionDisconnected;
        session.ReliableReceived += (s, data) => ReliableReceived?.Invoke(s, data);
        session.UnreliableReceived += (s, data) => UnreliableReceived?.Invoke(s, data);

        var conv = nextConv++;
        if (nextConv == 0) nextConv = 1;   // 0 은 예약

        lock (sessions)
        {
            sessions[remote] = session;
        }

        session.AcceptAsServer(conv, now);
        scheduler.Schedule(session, now);

        SendSynAck(session, remote);
        SessionConnected?.Invoke(session);
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

        while (scheduler.TryPopDue(now, out var session))
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
        if (handler == null || options.MetricsIntervalMs <= 0)
            return;

        var now = clock();
        if (nextMetricsAt != 0 && Kcp.Itimediff(now, nextMetricsAt) < 0)
            return;

        nextMetricsAt = now + (uint)options.MetricsIntervalMs;

        List<KcpSession> snapshot;
        lock (sessions)
        {
            snapshot = new List<KcpSession>(sessions.Values);
        }

        handler(MetricsCollector.Collect(snapshot, now, rttScratch));
    }

    private void OnSessionDisconnected(KcpSession session, KcpDisconnectReason reason)
    {
        SessionDisconnected?.Invoke(session, reason);
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
        cookies.Dispose();
    }
}
