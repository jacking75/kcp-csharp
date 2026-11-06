using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;

namespace KcpCSharpR1.Transport;

/// <summary><see cref="KcpEvent"/> 의 종류.</summary>
public enum KcpEventType
{
    /// <summary>새 세션이 연결됐다.</summary>
    Connected,

    /// <summary>세션이 끊겼다. <see cref="KcpEvent.Reason"/> 에 사유가 담긴다.</summary>
    Disconnected,

    /// <summary>신뢰 채널 메시지가 도착했다.</summary>
    Reliable,

    /// <summary>비신뢰 채널 메시지가 도착했다.</summary>
    Unreliable,
}

/// <summary>수신 이벤트 큐가 가득 찼을 때의 처리 정책.</summary>
public enum KcpEventQueueOverflowPolicy
{
    /// <summary>새 이벤트를 버리고 누계만 올린다. 기존 동작과 호환된다.</summary>
    DropNewest,

    /// <summary>
    /// 신뢰 메시지 또는 연결 이벤트가 누락되면 해당 세션의 정상 종료를 요청한다.
    /// 비신뢰 메시지는 버리기만 하며, 종료 이벤트 누락은 이미 종료된 세션을 다시 종료하지 않는다.
    /// </summary>
    DisconnectOnCriticalOverflow,
}

/// <summary>
/// 게임 스레드로 넘기는 수신 이벤트 한 건(진단 M-2).
/// </summary>
/// <remarks>
/// <para>
/// 페이로드는 <see cref="ArrayPool{T}"/> 대여 버퍼다. <b>소비한 뒤 반드시 <see cref="ReturnPayload"/> 를
/// 불러야 한다.</b> 부르지 않으면 풀이 마르고, 두 번 부르면 풀이 오염된다.
/// </para>
/// <para>
/// 값 타입이지만 <see cref="ReturnPayload"/> 가 자기 사본의 참조를 끊으므로,
/// 아래처럼 <c>out</c> 으로 받은 지역 변수에 대해서는 이중 반환이 자연스럽게 막힌다.
/// </para>
/// <code>
/// while (server.Events!.TryRead(out var ev))
/// {
///     Handle(ev);
///     ev.ReturnPayload();
/// }
/// </code>
/// </remarks>
public struct KcpEvent
{
    private byte[]? payload;

    internal KcpEvent(KcpEventType type, KcpSession session, byte[]? payload, int payloadLength, KcpDisconnectReason reason)
    {
        Type = type;
        Session = session;
        this.payload = payload;
        PayloadLength = payloadLength;
        Reason = reason;
    }

    /// <summary>이벤트 종류.</summary>
    public KcpEventType Type { get; }

    /// <summary>이벤트가 일어난 세션.</summary>
    public KcpSession Session { get; }

    /// <summary>페이로드 길이. 메시지 이벤트가 아니면 0.</summary>
    public int PayloadLength { get; }

    /// <summary>종료 사유. <see cref="KcpEventType.Disconnected"/> 일 때만 의미가 있다.</summary>
    public KcpDisconnectReason Reason { get; }

    /// <summary>페이로드. <see cref="ReturnPayload"/> 를 부른 뒤에는 비어 있다.</summary>
    public ReadOnlySpan<byte> Data => payload == null ? default : payload.AsSpan(0, PayloadLength);

    /// <summary>페이로드 버퍼를 풀에 돌려준다. 여러 번 불러도 안전하다.</summary>
    public void ReturnPayload()
    {
        var buffer = payload;
        if (buffer == null) return;

        payload = null;   // 이중 반환 방지 센티널
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// 수신 이벤트를 게임 스레드로 넘기는 바운디드 SPSC 큐(진단 M-2).
/// </summary>
/// <remarks>
/// <para>
/// KCP 의 이벤트는 수신 루프 스레드에서 발화한다. "핸들러에서는 큐에 넣기만 하라"가 정석인데,
/// 그 큐를 매번 사용자가 짜야 하고 잘못 짜면 레이스가 난다. 이 클래스가 그 정석 패턴이다.
/// </para>
/// <para>
/// <b>쓰는 쪽은 수신 루프 스레드 하나, 읽는 쪽은 게임 스레드 하나</b>를 전제한다.
/// 큐가 가득 차면 이벤트를 버리고 <see cref="Dropped"/> 를 올린다 — 신뢰 채널 메시지도 예외가 아니므로,
/// 게임 루프가 밀리지 않을 만큼 용량을 넉넉히 잡고 <see cref="Dropped"/> 를 반드시 관측해야 한다.
/// <see cref="KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow"/> 를 선택하면 신뢰 메시지·연결
/// 이벤트 누락 시 해당 세션을 종료한다. 종료 알림 자체도 큐에서 누락될 수 있으므로
/// 소비자는 <see cref="HasCriticalOverflow"/> 를 확인해 게임 상태를 재동기화해야 한다.
/// </para>
/// <para>
/// 호스트를 멈추면(<c>KcpServer.Stop</c> / <c>Dispose</c>) 남은 이벤트의 대여 버퍼가 회수된다.
/// <b>소비자 루프를 먼저 멈춘 뒤</b> 호출해야 꺼내 든 이벤트의 페이로드가 사라지지 않는다.
/// </para>
/// </remarks>
public sealed class KcpEventQueue
{
    private readonly Channel<KcpEvent> channel;
    private long dropped;
    private readonly long[] droppedByType = new long[4];

    internal KcpEventQueue(int capacity, KcpEventQueueOverflowPolicy overflowPolicy = KcpEventQueueOverflowPolicy.DropNewest)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "0보다 커야 합니다.");
        if (overflowPolicy != KcpEventQueueOverflowPolicy.DropNewest &&
            overflowPolicy != KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow)
            throw new ArgumentOutOfRangeException(nameof(overflowPolicy));

        OverflowPolicy = overflowPolicy;

        channel = Channel.CreateBounded<KcpEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>큐에 쌓여 있는 이벤트 수.</summary>
    public int Count => channel.Reader.Count;

    /// <summary>큐가 가득 차서 버린 이벤트 누계. 0 이 아니면 용량이 부족한 것이다.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    /// <summary>큐 생성 시 선택한 오버플로 정책.</summary>
    public KcpEventQueueOverflowPolicy OverflowPolicy { get; }

    /// <summary>누락된 연결 이벤트 누계.</summary>
    public long DroppedConnected => Interlocked.Read(ref droppedByType[(int)KcpEventType.Connected]);

    /// <summary>누락된 종료 이벤트 누계.</summary>
    public long DroppedDisconnected => Interlocked.Read(ref droppedByType[(int)KcpEventType.Disconnected]);

    /// <summary>누락된 신뢰 메시지 누계.</summary>
    public long DroppedReliable => Interlocked.Read(ref droppedByType[(int)KcpEventType.Reliable]);

    /// <summary>누락된 비신뢰 메시지 누계.</summary>
    public long DroppedUnreliable => Interlocked.Read(ref droppedByType[(int)KcpEventType.Unreliable]);

    /// <summary>
    /// 신뢰 메시지·연결·종료 이벤트가 한 번이라도 누락됐는지 여부.
    /// 큐를 비우거나 서버를 재시작해도 초기화되지 않는다. 소비자는 누계 변화와 세션 상태를 확인해야 한다.
    /// </summary>
    public bool HasCriticalOverflow => DroppedReliable != 0 || DroppedConnected != 0 || DroppedDisconnected != 0;

    /// <summary>이벤트를 하나 꺼낸다.</summary>
    /// <param name="ev">꺼낸 이벤트. 소비 후 <see cref="KcpEvent.ReturnPayload"/> 를 불러야 한다.</param>
    /// <returns>꺼냈으면 true, 비어 있으면 false.</returns>
    public bool TryRead(out KcpEvent ev) => channel.Reader.TryRead(out ev);

    /// <summary>이벤트가 들어올 때까지 기다린다(자체 루프를 도는 소비자용).</summary>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>읽을 것이 있으면 true, 큐가 닫혔으면 false.</returns>
    public System.Threading.Tasks.ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        => channel.Reader.WaitToReadAsync(cancellationToken);

    /// <summary>이벤트를 넣는다(수신 루프 스레드 전용).</summary>
    /// <returns>넣었으면 true. 가득 차서 버렸으면 false.</returns>
    internal bool Publish(KcpEventType type, KcpSession session, ReadOnlySpan<byte> data, KcpDisconnectReason reason)
    {
        byte[]? buffer = null;
        var length = 0;

        if (data.Length > 0)
        {
            buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(buffer);
            length = data.Length;
        }

        if (channel.Writer.TryWrite(new KcpEvent(type, session, buffer, length, reason)))
            return true;

        // 넣지 못했으면 대여 버퍼를 즉시 돌려준다(누수 방지).
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);

        Interlocked.Increment(ref dropped);
        Interlocked.Increment(ref droppedByType[(int)type]);
        if (OverflowPolicy == KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow &&
            (type == KcpEventType.Reliable || type == KcpEventType.Connected) &&
            session.State == KcpSessionState.Connected)
        {
            // FIN 종료는 즉시 또는 다음 Update 에서 종료 이벤트를 만든다. 그 이벤트가 누락돼도
            // 이 분기에 다시 들어오지 않으므로 큐가 꽉 찬 상태에서 재귀 종료가 발생하지 않는다.
            session.Disconnect();
        }
        return false;
    }

    /// <summary>남은 대여 버퍼를 모두 돌려준다.</summary>
    /// <param name="complete">
    /// true 면 쓰기 자체를 영구히 닫는다(호스트 폐기). false 면 내용만 비운다 —
    /// 멈췄다 다시 시작하는 서버가 같은 큐를 계속 쓸 수 있어야 하기 때문이다.
    /// </param>
    internal void Release(bool complete)
    {
        // 먼저 쓰기를 닫아야 드레인 도중 새 이벤트가 들어오는 경합이 사라진다.
        if (complete)
            channel.Writer.TryComplete();

        while (channel.Reader.TryRead(out var ev))
            ev.ReturnPayload();
    }
}
