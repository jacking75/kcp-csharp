using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpCSharpR1.Transport;

namespace KcpSamples.Common;

/// <summary>연결 태스크에서 게임 루프로 소유권을 넘기는 샘플용 어댑터.</summary>
/// <remarks>생성·Connect·Poll·Dispose 는 게임 스레드에서 직렬로 호출한다.</remarks>
internal sealed class PollingKcpClient : IDisposable
{
    private readonly KcpClient client;
    private readonly KcpEventQueue events;
    private readonly CancellationTokenSource cancellation = new();
    private Task<KcpSession>? connection;
    private bool connectionObserved;
    private bool disposed;

    public PollingKcpClient(KcpSessionOptions? options = null, int eventCapacity = 1024)
    {
        client = new KcpClient(options);
        events = client.EnableEventQueue(eventCapacity, KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
    }

    public KcpSession? Session { get; private set; }
    public event Action<KcpSession>? Connected;
    public event Action<Exception>? Error;
    public event Action<KcpSession, KcpDisconnectReason>? Disconnected;
    public event KcpMessageHandler? ReliableReceived;
    public event KcpMessageHandler? UnreliableReceived;

    public void Connect(string host, int port)
    {
        CheckCanConnect();
        connection = client.ConnectAsync(host, port, cancellation.Token);
    }

    // 가상 전송으로도 실제 ConnectAsync 수명 경계를 검증할 수 있게 한다.
    internal void Connect(IUdpTransport transport, IPEndPoint remote)
    {
        CheckCanConnect();
        connection = client.ConnectAsync(transport, remote, cancellation.Token);
    }

    private void CheckCanConnect()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PollingKcpClient));
        if (connection != null) throw new InvalidOperationException("재접속은 새 어댑터 인스턴스로 시작합니다.");
    }

    /// <summary>완료된 연결과 수신 이벤트를 게임 스레드에서만 적용한다.</summary>
    public void Poll()
    {
        if (disposed || connection == null) return;
        if (!connectionObserved)
        {
            // ConnectAsync 가 자체 Pump 를 소유하는 동안 게임 루프는 접근하지 않는다.
            if (!connection.IsCompleted) return;
            connectionObserved = true;
            try
            {
                Session = connection.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (CheckOverflow()) return;
                Error?.Invoke(ex);
                return;
            }

            if (CheckOverflow()) return;
            Connected?.Invoke(Session);
        }

        if (disposed || Session == null) return;
        client.Update();
        if (CheckOverflow()) return;

        while (!disposed && events.TryRead(out var ev))
        {
            try
            {
                switch (ev.Type)
                {
                    case KcpEventType.Reliable:
                        ReliableReceived?.Invoke(ev.Session, ev.Data);
                        break;
                    case KcpEventType.Unreliable:
                        UnreliableReceived?.Invoke(ev.Session, ev.Data);
                        break;
                    case KcpEventType.Disconnected:
                        Disconnected?.Invoke(ev.Session, ev.Reason);
                        break;
                }
            }
            finally
            {
                ev.ReturnPayload();
            }
        }
    }

    private bool CheckOverflow()
    {
        if (events.Dropped == 0) return false;

        // 신뢰 메시지/종료 이벤트까지 누락될 수 있으므로 손상된 게임 상태로 계속하지 않는다.
        var error = new InvalidOperationException($"수신 큐가 넘쳐 {events.Dropped}개 이벤트가 누락됐습니다. 재접속이 필요합니다.");
        Dispose();
        Error?.Invoke(error);
        return true;
    }

    /// <summary>진행 중 연결을 취소·관측한 뒤 소켓을 정리한다.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();

        try
        {
            // 라이브러리 연결은 SynchronizationContext 를 포획하지 않는다.
            // 게임 종료 때만 기다려 소켓 대기 중의 Dispose 경합을 막는다.
            connection?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 종료 중에는 취소/실패를 관측만 한다. 정상 실행의 실패는 Poll 에서 Error 로 전달한다.
        }

        try
        {
            if (client.IsConnected) client.Disconnect();
        }
        finally
        {
            client.Dispose();
            cancellation.Dispose();
            Session = null;
        }
    }
}
