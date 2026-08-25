using System;

namespace TankBattle.Shared;

/// <summary>탱크 하나의 상태(위치·각도·체력 등).</summary>
public struct TankState
{
    /// <summary>위치 X.</summary>
    public float X;

    /// <summary>위치 Y.</summary>
    public float Y;

    /// <summary>차체 각도(라디안).</summary>
    public float BodyAngle;

    /// <summary>포탑 각도(라디안).</summary>
    public float TurretAngle;

    /// <summary>살아 있는지.</summary>
    public bool Alive;

    /// <summary>다음에 발사할 수 있는 시각(틱).</summary>
    public uint NextFireTick;
}

/// <summary>한 틱 분량의 입력.</summary>
public struct TankInput
{
    /// <summary>입력 시퀀스 번호. 클라이언트가 1부터 매긴다.</summary>
    public uint Sequence;

    /// <summary>전진(+1) / 후진(-1) / 정지(0).</summary>
    public sbyte Throttle;

    /// <summary>좌회전(-1) / 우회전(+1) / 정지(0).</summary>
    public sbyte Steer;

    /// <summary>포탑 목표 각도(라디안).</summary>
    public float TurretAngle;

    /// <summary>발사 버튼을 눌렀는지.</summary>
    public bool Fire;
}

/// <summary>
/// 탱크 물리 — <b>입력 + 상태 → 새 상태</b> 의 순수 함수.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 파일이 이 샘플에서 가장 중요하다.</b> 서버와 클라이언트가 <b>같은 코드</b>로 시뮬레이션해야
/// 클라이언트 예측이 서버 결과와 어긋나지 않는다. 여기에 난수나 시간 의존이 들어가는 순간
/// 예측은 무너지고 화면이 튄다(rubber-banding).
/// </para>
/// <para>
/// 규칙: <b>순수 함수만</b>. 전역 상태 금지, <c>DateTime.Now</c> 금지, <c>Random</c> 금지.
/// 같은 입력이면 언제 어디서 돌려도 같은 결과가 나와야 한다.
/// </para>
/// </remarks>
public static class TankPhysics
{
    /// <summary>한 틱을 진행시킨다.</summary>
    /// <param name="state">현재 상태.</param>
    /// <param name="input">이번 틱 입력.</param>
    /// <param name="tick">현재 틱 번호(발사 쿨다운 판정용).</param>
    /// <returns>다음 상태.</returns>
    public static TankState Step(TankState state, TankInput input, uint tick)
    {
        if (!state.Alive) return state;

        const float dt = 1f / GameLimits.TickRate;

        // 회전
        state.BodyAngle += input.Steer * GameLimits.TurnSpeed * dt;
        state.BodyAngle = Wrap(state.BodyAngle);

        // 전후진
        var speed = input.Throttle * GameLimits.MoveSpeed * dt;
        state.X += MathF.Cos(state.BodyAngle) * speed;
        state.Y += MathF.Sin(state.BodyAngle) * speed;

        // 월드 경계
        state.X = Math.Clamp(state.X, GameLimits.TankRadius, GameLimits.WorldWidth - GameLimits.TankRadius);
        state.Y = Math.Clamp(state.Y, GameLimits.TankRadius, GameLimits.WorldHeight - GameLimits.TankRadius);

        // 포탑은 즉시 따라간다(조준 지연을 넣고 싶으면 여기서 보간한다).
        state.TurretAngle = Wrap(input.TurretAngle);

        return state;
    }

    /// <summary>발사할 수 있는 상태인지 확인한다(쿨다운 검증 — 서버와 클라 공통).</summary>
    /// <param name="state">탱크 상태.</param>
    /// <param name="tick">현재 틱.</param>
    /// <returns>발사 가능하면 true.</returns>
    public static bool CanFire(in TankState state, uint tick) =>
        state.Alive && tick >= state.NextFireTick;

    /// <summary>발사 후 쿨다운을 적용한 상태를 돌려준다.</summary>
    /// <param name="state">탱크 상태.</param>
    /// <param name="tick">현재 틱.</param>
    /// <returns>쿨다운이 반영된 상태.</returns>
    public static TankState ApplyFireCooldown(TankState state, uint tick)
    {
        state.NextFireTick = tick + GameLimits.FireCooldownTicks;
        return state;
    }

    /// <summary>각도를 -π ~ π 로 정규화한다.</summary>
    /// <param name="angle">각도(라디안).</param>
    /// <returns>정규화된 각도.</returns>
    public static float Wrap(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.PI * 2f;
        while (angle < -MathF.PI) angle += MathF.PI * 2f;
        return angle;
    }

    /// <summary>
    /// 광선(포탄 경로)이 탱크에 맞는지 판정한다.
    /// </summary>
    /// <param name="originX">발사 지점 X.</param>
    /// <param name="originY">발사 지점 Y.</param>
    /// <param name="angle">발사 각도(라디안).</param>
    /// <param name="range">사거리.</param>
    /// <param name="target">표적 상태.</param>
    /// <param name="distance">맞았다면 발사 지점에서의 거리.</param>
    /// <returns>맞으면 true.</returns>
    public static bool RayHitsTank(float originX, float originY, float angle, float range, in TankState target, out float distance)
    {
        distance = 0;
        if (!target.Alive) return false;

        var dx = target.X - originX;
        var dy = target.Y - originY;

        var dirX = MathF.Cos(angle);
        var dirY = MathF.Sin(angle);

        // 표적 중심을 광선 방향으로 투영한다.
        var projection = dx * dirX + dy * dirY;
        if (projection < 0 || projection > range) return false;

        // 광선에서 표적 중심까지의 수직 거리
        var closestX = originX + dirX * projection;
        var closestY = originY + dirY * projection;
        var offX = target.X - closestX;
        var offY = target.Y - closestY;

        if (offX * offX + offY * offY > GameLimits.TankRadius * GameLimits.TankRadius)
            return false;

        distance = projection;
        return true;
    }
}
