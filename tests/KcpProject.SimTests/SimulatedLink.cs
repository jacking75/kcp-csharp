using System;
using System.Collections.Generic;

namespace KcpProject.SimTests;

/// <summary>
/// 지연·지터·손실·중복·순서뒤섞임·대역폭 제한을 재현하는 가상 단방향 링크.
/// </summary>
/// <remarks>
/// <para>
/// 가상 시간 기반이다. <see cref="Send"/> 는 패킷에 도착 시각을 매겨 대기열에 넣고,
/// <see cref="Advance"/> 는 도착 시각이 된 패킷을 <b>도착 시각 순서대로</b> <see cref="Deliver"/> 로 배달한다.
/// 실제 sleep 이 없으므로 "2분짜리" 시나리오도 수십 ms 안에 끝난다.
/// </para>
/// <para>시드를 고정하면 100% 재현된다. 난수는 이 링크 인스턴스 전용이다.</para>
/// </remarks>
public sealed class SimulatedLink
{
    /// <summary>편도 기본 지연(ms).</summary>
    public int DelayMs = 50;

    /// <summary>지연 흔들림(±ms).</summary>
    public int JitterMs;

    /// <summary>패킷 손실률 0.0~1.0.</summary>
    public double LossRate;

    /// <summary>패킷 중복률 0.0~1.0.</summary>
    public double DuplicateRate;

    /// <summary>순서 뒤섞임 발생률 0.0~1.0. 해당 패킷의 도착 시각을 뒤로 민다.</summary>
    public double ReorderRate;

    /// <summary>대역폭 제한(kbps). 0 이면 무제한.</summary>
    public int BandwidthKbps;

    /// <summary>이 크기를 넘는 패킷은 폐기한다(경로 MTU 초과 재현).</summary>
    public int Mtu = 1400;

    /// <summary>true 면 이후 모든 패킷을 조용히 버린다(상대 소멸·블랙홀 재현).</summary>
    public bool Blackhole;

    /// <summary>수신측으로 패킷을 전달하는 콜백. 보통 상대 <see cref="Kcp.Input(byte[], int, int, bool, bool)"/> 에 연결한다.</summary>
    public Action<byte[], int>? Deliver;

    /// <summary>이 링크에 투입된 패킷 수.</summary>
    public long SentPackets { get; private set; }

    /// <summary>손실·MTU 초과·블랙홀로 버려진 패킷 수.</summary>
    public long DroppedPackets { get; private set; }

    /// <summary>실제로 배달된 패킷 수(중복 배달 포함).</summary>
    public long DeliveredPackets { get; private set; }

    private readonly Random rng;
    private readonly List<Packet> inflight = new();
    private uint now;
    private long sequence;
    private uint nextFreeAt;

    private struct Packet
    {
        public uint At;
        public long Seq;
        public byte[] Data;
        public int Len;
    }

    /// <summary>시드를 지정해 링크를 만든다. 같은 시드는 같은 시나리오를 재현한다.</summary>
    /// <param name="seed">난수 시드.</param>
    public SimulatedLink(int seed)
    {
        rng = new Random(seed);
    }

    /// <summary>현재 가상 시각(ms).</summary>
    public uint Now => now;

    /// <summary>아직 배달되지 않고 링크에 떠 있는 패킷 수.</summary>
    public int InFlight => inflight.Count;

    /// <summary>패킷을 링크에 투입한다. 보통 송신측 <see cref="Kcp"/> 의 output 콜백에 연결한다.</summary>
    /// <param name="data">패킷 버퍼(호출 후 재사용되므로 내부에서 복사한다).</param>
    /// <param name="len">유효 길이.</param>
    public void Send(byte[] data, int len)
    {
        SentPackets++;

        if (Blackhole || len > Mtu)
        {
            DroppedPackets++;
            return;
        }

        if (LossRate > 0 && rng.NextDouble() < LossRate)
        {
            DroppedPackets++;
            return;
        }

        var at = now + (uint)DelayMs;
        if (JitterMs > 0)
            at = (uint)((int)at + rng.Next(-JitterMs, JitterMs + 1));

        if (ReorderRate > 0 && rng.NextDouble() < ReorderRate)
            at += (uint)rng.Next(1, Math.Max(2, DelayMs));

        at = ApplyBandwidth(at, len);

        Enqueue(at, data, len);

        if (DuplicateRate > 0 && rng.NextDouble() < DuplicateRate)
            Enqueue(at + 1, data, len);
    }

    /// <summary>토큰 버킷: 직전 패킷의 전송 완료 시각 이후로 도착 시각을 민다.</summary>
    private uint ApplyBandwidth(uint at, int len)
    {
        if (BandwidthKbps <= 0)
            return at;

        var transmitMs = (uint)Math.Max(1, len * 8 / BandwidthKbps);
        if (Kcp._itimediff(at, nextFreeAt) < 0)
            at = nextFreeAt;
        nextFreeAt = at + transmitMs;
        return at;
    }

    private void Enqueue(uint at, byte[] data, int len)
    {
        var copy = new byte[len];
        Buffer.BlockCopy(data, 0, copy, 0, len);
        inflight.Add(new Packet { At = at, Seq = sequence++, Data = copy, Len = len });
    }

    /// <summary>가상 시각을 진행시키고, 도착 시각이 된 패킷을 도착 순서대로 배달한다.</summary>
    /// <param name="virtualNowMs">새 가상 시각(ms).</param>
    public void Advance(uint virtualNowMs)
    {
        now = virtualNowMs;
        if (inflight.Count == 0)
            return;

        // 도착 시각(같으면 투입 순서) 순으로 정렬해 배달한다.
        // 순서 뒤섞임은 오직 At 조작으로만 만들어야 재현성이 유지된다.
        inflight.Sort(static (x, y) =>
        {
            var d = Kcp._itimediff(x.At, y.At);
            return d != 0 ? d : x.Seq.CompareTo(y.Seq);
        });

        var delivered = 0;
        for (var i = 0; i < inflight.Count; i++)
        {
            var pkt = inflight[i];
            if (Kcp._itimediff(now, pkt.At) < 0)
                break;

            delivered++;
            DeliveredPackets++;
            Deliver?.Invoke(pkt.Data, pkt.Len);
        }

        if (delivered > 0)
            inflight.RemoveRange(0, delivered);
    }

    /// <summary>떠 있는 패킷을 모두 버린다.</summary>
    public void ClearInFlight()
    {
        DroppedPackets += inflight.Count;
        inflight.Clear();
    }
}
