using System;
using System.Collections.Generic;
using TankBattle.Shared;

namespace TankBattle.Server;

/// <summary>
/// 지연 보상용 과거 스냅숏 링버퍼.
/// </summary>
/// <remarks>
/// <para>
/// <b>문제</b>: 사수가 화면에서 보고 있는 표적은 이미 과거다.
/// 클라이언트는 보간을 위해 100ms 늦게 재생하고, 거기에 네트워크 지연(RTT/2)이 더해진다.
/// 서버가 "지금" 위치로 판정하면 잘 조준해도 빗나간다.
/// </para>
/// <para>
/// <b>해법</b>: 서버가 과거 1초(20틱)의 모든 탱크 위치를 들고 있다가,
/// 사수의 지연만큼 <b>되감아</b> 그 시점 위치로 판정한다. "내 화면에서 맞췄으면 맞은 것"이 성립한다.
/// </para>
/// <para>
/// <b>대가</b>: 맞은 쪽에서는 "엄폐물 뒤로 숨었는데 맞았다"가 생길 수 있다.
/// 모든 대전 액션 게임이 감수하는 절충이며, 되감기 상한(여기서는 1초)으로 악용을 제한한다.
/// </para>
/// </remarks>
internal sealed class LagCompensator
{
    private readonly Dictionary<uint, TankState>[] frames;
    private readonly uint[] frameTicks;

    /// <summary>보관 가능한 틱 수.</summary>
    public int Capacity => frames.Length;

    /// <summary>링버퍼를 만든다.</summary>
    public LagCompensator()
    {
        frames = new Dictionary<uint, TankState>[GameLimits.HistoryTicks];
        frameTicks = new uint[GameLimits.HistoryTicks];

        for (var i = 0; i < frames.Length; i++)
            frames[i] = new Dictionary<uint, TankState>();
    }

    /// <summary>이번 틱의 모든 탱크 위치를 기록한다.</summary>
    /// <param name="tick">틱 번호.</param>
    /// <param name="players">플레이어 목록.</param>
    public void Record(uint tick, IReadOnlyList<BattlePlayer> players)
    {
        var slot = (int)(tick % (uint)frames.Length);

        var frame = frames[slot];
        frame.Clear();
        frameTicks[slot] = tick;

        for (var i = 0; i < players.Count; i++)
            frame[players[i].Id] = players[i].State;
    }

    /// <summary>지정 틱에서의 탱크 상태를 돌려준다.</summary>
    /// <param name="tick">되감을 틱.</param>
    /// <param name="playerId">플레이어 ID.</param>
    /// <param name="fallback">기록이 없을 때 쓸 현재 상태.</param>
    /// <returns>과거 상태. 기록이 없으면 <paramref name="fallback"/>.</returns>
    public TankState StateAt(uint tick, uint playerId, TankState fallback)
    {
        var slot = (int)(tick % (uint)frames.Length);

        // 링버퍼가 이미 덮어써졌으면(1초보다 오래된 요청) 되감지 않는다.
        if (frameTicks[slot] != tick) return fallback;

        return frames[slot].TryGetValue(playerId, out var state) ? state : fallback;
    }
}
