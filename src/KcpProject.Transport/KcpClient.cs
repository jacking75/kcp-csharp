using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KcpProject.Transport;

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
/// </remarks>
public sealed class KcpClient : IDisposable
{
    private readonly KcpSessionOptions options;
    private readonly Func<uint> clock;
    private readonly byte[] receiveBuffer;

    private IUdpTransport? transport;
    private KcpSession? session;
    private bool disposed;
    private bool runLoopActive;

    /// <summary>클라이언트를 만든다.</summary>
    /// <param name="options">세션 설정. 생략하면 기본값.</param>
    /// <param name="clock">밀리초 시계. 생략하면 <see cref="KcpClock.NowMs"/>.</param>
    public KcpClient(KcpSessionOptions? options = null, Func<uint>? clock = null)
    {
        this.options = options ?? new KcpSessionOptions();
        this.options.Validate();
        this.clock = clock ?? (() => KcpClock.NowMs);
        receiveBuffer = new byte[this.options.Mtu + (this.options.Cipher?.Overhead ?? 0) + 64];
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
        ThrowIfDisposed();

        var socketTransport = new UdpSocketTransport(remote.AddressFamily);
        socketTransport.Bind(new IPEndPoint(
            remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

        return await ConnectAsync(socketTransport, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>지정한 전송 계층으로 연결한다(테스트에서 가상 전송을 끼울 때 쓴다).</summary>
    /// <param name="udpTransport">전송 계층. 소유권이 이 클라이언트로 넘어간다.</param>
    /// <param name="remote">서버 주소.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>연결된 세션.</returns>
    /// <exception cref="TimeoutException">핸드셰이크가 시간 안에 끝나지 않은 경우.</exception>
    public async Task<KcpSession> ConnectAsync(IUdpTransport udpTransport, IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        if (udpTransport == null) throw new ArgumentNullException(nameof(udpTransport));
        if (remote == null) throw new ArgumentNullException(nameof(remote));
        ThrowIfDisposed();

        transport = udpTransport;
        session = CreateSession(remote);

        var now = clock();
        session.StartConnect(now);

        var deadline = now + (uint)options.HandshakeTimeoutMs + 500u;
        while (session.State == KcpSessionState.Connecting)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Pump();

            if (session.State == KcpSessionState.Connected)
                break;

            if (Kcp.Itimediff(clock(), deadline) >= 0)
                break;

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        if (session.State != KcpSessionState.Connected)
        {
            var failed = session;
            session = null;
            transport.Dispose();
            transport = null;
            throw new TimeoutException($"{remote} 로의 KCP 핸드셰이크가 실패했습니다(상태: {failed.State}).");
        }

        return session;
    }

    private KcpSession CreateSession(IPEndPoint remote)
    {
        var created = new KcpSession(remote, options, SendTo, clock, isClient: true);
        created.Connected += s => Connected?.Invoke(s);
        created.Disconnected += (s, reason) => Disconnected?.Invoke(s, reason);
        created.ReliableReceived += (s, data) => ReliableReceived?.Invoke(s, data);
        created.UnreliableReceived += (s, data) => UnreliableReceived?.Invoke(s, data);
        return created;
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
        if (runLoopActive)
            throw new InvalidOperationException("RunAsync 로 구동 중에는 Update 를 호출하지 않습니다(구동 모델 혼용 금지).");

        Pump();
    }

    private void Pump()
    {
        var current = session;
        var udp = transport;
        if (current == null || udp == null)
            return;

        // 도착한 패킷을 전부 빨아들인다(호출당 1패킷 제한 없음, 진단 PF-08).
        while (udp.TryReceive(receiveBuffer, out var received, out var remote))
        {
            if (received <= 0) continue;
            if (remote != current.RemoteKey) continue;   // 서버가 아닌 곳에서 온 패킷은 무시
            current.OnFrame(receiveBuffer.AsSpan(0, received), clock());
        }

        current.Update(clock());
    }

    /// <summary>자체 루프형 구동. 취소될 때까지 수신·갱신을 반복한다.</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>루프가 끝나면 완료되는 태스크.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (runLoopActive)
            throw new InvalidOperationException("이미 RunAsync 로 구동 중입니다.");

        runLoopActive = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var udp = transport;
                if (udp == null) break;

                if (!udp.WaitForData(1000))
                {
                    Pump();
                    await Task.Yield();
                    continue;
                }

                Pump();
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

    /// <summary>소켓과 세션 자원을 정리한다.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        session?.ReleaseResources();
        session = null;

        transport?.Dispose();
        transport = null;
    }
}
