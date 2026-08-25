using System;
using System.Collections.Generic;
using System.Net;
using KcpCSharpR1;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// 여러 <see cref="IUdpTransport"/> 끝점을 묶는 가상 UDP 망(P4-5).
/// </summary>
/// <remarks>
/// 가상 시간 기반이라 실제 소켓·대기 없이 손실·지연 환경의 핸드셰이크와 세션 수명을 결정적으로 재현한다.
/// <see cref="Advance"/> 를 부를 때 도착 시각이 된 패킷이 상대 인박스로 옮겨진다.
/// </remarks>
public sealed class VirtualNetwork
{
    private readonly List<Packet> inflight = new();
    private readonly Dictionary<AddressKey, VirtualTransport> endpoints = new();
    private readonly Random rng;
    private long sequence;

    private struct Packet
    {
        public uint At;
        public long Seq;
        public AddressKey From;
        public AddressKey To;
        public byte[] Data;
        public int Length;
    }

    /// <summary>현재 가상 시각(ms).</summary>
    public uint Now { get; private set; }

    /// <summary>편도 지연(ms).</summary>
    public int DelayMs { get; set; } = 20;

    /// <summary>손실률 0.0~1.0.</summary>
    public double LossRate { get; set; }

    /// <summary>이 함수가 true 를 반환하는 패킷은 버린다(특정 프레임만 골라 떨어뜨릴 때).</summary>
    public PacketDropFilter? DropFilter { get; set; }

    /// <summary>망에 투입된 패킷 수.</summary>
    public long SentPackets { get; private set; }

    /// <summary>버려진 패킷 수.</summary>
    public long DroppedPackets { get; private set; }

    /// <summary>가상 망을 만든다.</summary>
    /// <param name="seed">난수 시드.</param>
    public VirtualNetwork(int seed = 1)
    {
        rng = new Random(seed);
    }

    /// <summary>끝점을 만들어 망에 등록한다.</summary>
    /// <param name="local">이 끝점의 주소.</param>
    /// <returns>전송 계층 구현.</returns>
    public VirtualTransport CreateTransport(IPEndPoint local)
    {
        var transport = new VirtualTransport(this, local);
        endpoints[AddressKey.FromEndPoint(local)] = transport;
        return transport;
    }

    internal void Enqueue(AddressKey from, AddressKey to, ReadOnlySpan<byte> data)
    {
        SentPackets++;

        if (DropFilter != null && DropFilter(from, to, data))
        {
            DroppedPackets++;
            return;
        }

        if (LossRate > 0 && rng.NextDouble() < LossRate)
        {
            DroppedPackets++;
            return;
        }

        var copy = new byte[data.Length];
        data.CopyTo(copy);
        inflight.Add(new Packet
        {
            At = Now + (uint)DelayMs,
            Seq = sequence++,
            From = from,
            To = to,
            Data = copy,
            Length = data.Length,
        });
    }

    /// <summary>가상 시각을 진행시키고 도착한 패킷을 인박스로 옮긴다.</summary>
    /// <param name="now">새 가상 시각(ms).</param>
    public void Advance(uint now)
    {
        Now = now;
        if (inflight.Count == 0) return;

        inflight.Sort(static (x, y) =>
        {
            var d = Kcp.Itimediff(x.At, y.At);
            return d != 0 ? d : x.Seq.CompareTo(y.Seq);
        });

        var delivered = 0;
        for (var i = 0; i < inflight.Count; i++)
        {
            var pkt = inflight[i];
            if (Kcp.Itimediff(now, pkt.At) < 0) break;

            delivered++;
            if (endpoints.TryGetValue(pkt.To, out var target))
                target.Deliver(pkt.From, pkt.Data, pkt.Length);
        }

        if (delivered > 0)
            inflight.RemoveRange(0, delivered);
    }
}

/// <summary>패킷을 버릴지 결정하는 필터.</summary>
/// <param name="from">보낸 쪽.</param>
/// <param name="to">받는 쪽.</param>
/// <param name="data">패킷 내용.</param>
/// <returns>true 면 버린다.</returns>
public delegate bool PacketDropFilter(AddressKey from, AddressKey to, ReadOnlySpan<byte> data);

/// <summary>가상 망에 붙는 <see cref="IUdpTransport"/> 구현.</summary>
public sealed class VirtualTransport : IUdpTransport
{
    private readonly VirtualNetwork network;
    private readonly Queue<(byte[] Data, int Length, AddressKey From)> inbox = new();
    private readonly AddressKey selfKey;
    private bool disposed;

    internal VirtualTransport(VirtualNetwork network, IPEndPoint local)
    {
        this.network = network;
        LocalEndPoint = local;
        selfKey = AddressKey.FromEndPoint(local);
    }

    /// <inheritdoc/>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <inheritdoc/>
    public void Bind(IPEndPoint localEndPoint)
    {
        LocalEndPoint = localEndPoint;
    }

    /// <inheritdoc/>
    public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        if (disposed) return 0;
        network.Enqueue(selfKey, AddressKey.FromEndPoint(remote), data);
        return data.Length;
    }

    /// <inheritdoc/>
    public bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)
    {
        received = 0;
        remote = default;
        if (disposed || inbox.Count == 0) return false;

        var (data, length, from) = inbox.Dequeue();
        if (length > buffer.Length) return false;

        data.AsSpan(0, length).CopyTo(buffer);
        received = length;
        remote = from;
        return true;
    }

    /// <inheritdoc/>
    public bool WaitForData(int microseconds) => !disposed && inbox.Count > 0;

    internal void Deliver(AddressKey from, byte[] data, int length)
    {
        if (disposed) return;
        inbox.Enqueue((data, length, from));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        disposed = true;
        inbox.Clear();
    }
}
