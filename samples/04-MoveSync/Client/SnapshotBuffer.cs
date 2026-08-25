using System;
using System.Collections.Generic;
using MoveSync.Shared;

namespace MoveSync.Client;

/// <summary>스냅숏 한 장에 담긴 엔티티 하나.</summary>
internal struct EntitySnapshot
{
    /// <summary>플레이어 ID.</summary>
    public uint Id;

    /// <summary>위치 X.</summary>
    public float X;

    /// <summary>위치 Y.</summary>
    public float Y;

    /// <summary>이동 방향(그림에 화살표를 그릴 때 쓴다).</summary>
    public MoveDirection Direction;
}

/// <summary>서버가 보낸 스냅숏 한 장.</summary>
internal sealed class WorldSnapshot
{
    /// <summary>서버 틱 번호.</summary>
    public uint Tick;

    /// <summary>클라이언트가 받은 시각(ms).</summary>
    public double ReceivedAtMs;

    /// <summary>엔티티 목록.</summary>
    public readonly List<EntitySnapshot> Entities = new();
}

/// <summary>
/// 스냅숏 보간 버퍼.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 보간이 필요한가</b>: 서버는 50ms(20Hz)마다 위치를 보낸다. 받은 즉시 그리면
/// 초당 20번만 위치가 바뀌어 뚝뚝 끊겨 보인다. 게다가 스냅숏 하나만 유실돼도 100ms 동안 멈춘다.
/// </para>
/// <para>
/// <b>해법</b>: 일부러 <see cref="ProtocolLimits.InterpolationDelayMs"/>(100ms)만큼 <b>과거를 재생한다</b>.
/// 그러면 항상 "지나간 두 스냅숏" 사이에 있게 되므로, 그 사이를 선형 보간해 매 프레임 부드럽게 그릴 수 있다.
/// 100ms의 지연을 내주고 부드러움을 사는 거래다.
/// </para>
/// <para>
/// <b>비신뢰 채널이라 순서가 뒤바뀔 수 있다</b>. 틱 번호가 이미 받은 것보다 작으면 그냥 버린다.
/// </para>
/// </remarks>
internal sealed class SnapshotBuffer
{
    private const int MaxSnapshots = 32;

    private readonly List<WorldSnapshot> snapshots = new();

    /// <summary>지금까지 받은 스냅숏 수.</summary>
    public long ReceivedCount { get; private set; }

    /// <summary>순서가 뒤바뀌어 버린 스냅숏 수.</summary>
    public long DiscardedCount { get; private set; }

    /// <summary>마지막으로 받은 틱 번호.</summary>
    public uint LatestTick { get; private set; }

    /// <summary>버퍼에 들어 있는 스냅숏 수.</summary>
    public int Count => snapshots.Count;

    /// <summary>스냅숏을 넣는다.</summary>
    /// <param name="snapshot">스냅숏.</param>
    public void Add(WorldSnapshot snapshot)
    {
        ReceivedCount++;

        // 늦게 도착한 낡은 스냅숏은 버린다(비신뢰 채널의 정상 동작).
        if (snapshot.Tick <= LatestTick)
        {
            DiscardedCount++;
            return;
        }

        LatestTick = snapshot.Tick;
        snapshots.Add(snapshot);

        while (snapshots.Count > MaxSnapshots)
            snapshots.RemoveAt(0);
    }

    /// <summary>
    /// 렌더 시각에 해당하는 위치를 보간해 채운다.
    /// </summary>
    /// <param name="nowMs">현재 시각(ms).</param>
    /// <param name="output">결과를 담을 사전(ID → 위치).</param>
    /// <param name="interpolate">false 면 보간 없이 최신 스냅숏을 그대로 쓴다(비교 실험용).</param>
    /// <returns>그릴 것이 있으면 true.</returns>
    public bool Sample(double nowMs, Dictionary<uint, EntitySnapshot> output, bool interpolate)
    {
        output.Clear();
        if (snapshots.Count == 0) return false;

        if (!interpolate)
        {
            // 보간 끄기: 최신 스냅숏을 그대로 그린다 → 20Hz 로 뚝뚝 끊긴다.
            foreach (var entity in snapshots[^1].Entities)
                output[entity.Id] = entity;
            return true;
        }

        var renderTime = nowMs - ProtocolLimits.InterpolationDelayMs;

        // renderTime 을 사이에 두는 두 스냅숏을 찾는다.
        WorldSnapshot? older = null;
        WorldSnapshot? newer = null;

        for (var i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].ReceivedAtMs <= renderTime)
            {
                older = snapshots[i];
                newer = i + 1 < snapshots.Count ? snapshots[i + 1] : null;
                break;
            }
        }

        if (older == null)
        {
            // 아직 100ms 치가 안 쌓였다 — 가장 오래된 것을 그대로 보여 준다.
            foreach (var entity in snapshots[0].Entities)
                output[entity.Id] = entity;
            return true;
        }

        if (newer == null)
        {
            // 다음 스냅숏이 아직 안 왔다 — 마지막 위치를 유지한다(외삽하지 않는다).
            foreach (var entity in older.Entities)
                output[entity.Id] = entity;
            return true;
        }

        var span = newer.ReceivedAtMs - older.ReceivedAtMs;
        var t = span <= 0.0001 ? 1f : (float)Math.Clamp((renderTime - older.ReceivedAtMs) / span, 0, 1);

        // 두 스냅숏 사이를 선형 보간한다.
        foreach (var from in older.Entities)
        {
            var target = from;

            foreach (var to in newer.Entities)
            {
                if (to.Id != from.Id) continue;

                target.X = from.X + (to.X - from.X) * t;
                target.Y = from.Y + (to.Y - from.Y) * t;
                target.Direction = to.Direction;
                break;
            }

            output[from.Id] = target;
        }

        return true;
    }
}
