using System;
using TankBattle.Shared;

namespace TankBattle.Tests;

/// <summary>
/// P7-5 ①: 탱크 물리 단위 테스트.
/// </summary>
/// <remarks>
/// <b>물리가 순수 함수라는 것을 못 박는 테스트다.</b> 이것이 깨지면 클라이언트 예측이 서버와 어긋나
/// 화면이 튄다(rubber-banding). 예측·보정 구조의 토대이므로 가장 먼저 지켜야 한다.
/// </remarks>
public class TankPhysicsTests
{
    private static TankState NewTank(float x = 100, float y = 100, float angle = 0) => new()
    {
        X = x,
        Y = y,
        BodyAngle = angle,
        Alive = true,
    };

    private static TankInput Move(sbyte throttle = 0, sbyte steer = 0, float turret = 0, bool fire = false, uint seq = 1) => new()
    {
        Sequence = seq,
        Throttle = throttle,
        Steer = steer,
        TurretAngle = turret,
        Fire = fire,
    };

    /// <summary>같은 입력이면 언제 몇 번을 돌려도 결과가 같아야 한다 — 예측의 대전제.</summary>
    [Fact]
    public void 같은_입력은_항상_같은_결과를_낸다()
    {
        var input = Move(throttle: 1, steer: 1, turret: 0.5f);

        var a = NewTank();
        var b = NewTank();

        for (var i = 0; i < 100; i++)
        {
            a = TankPhysics.Step(a, input, (uint)i);
            b = TankPhysics.Step(b, input, (uint)i);
        }

        Assert.Equal(a.X, b.X, 6);
        Assert.Equal(a.Y, b.Y, 6);
        Assert.Equal(a.BodyAngle, b.BodyAngle, 6);
    }

    /// <summary>서버의 되감기-재적용과 클라이언트의 순차 적용 결과가 같아야 한다.</summary>
    [Fact]
    public void 되감아_재적용해도_같은_결과가_나온다()
    {
        var inputs = new[]
        {
            Move(throttle: 1, seq: 1),
            Move(throttle: 1, steer: 1, seq: 2),
            Move(throttle: -1, steer: -1, seq: 3),
            Move(throttle: 1, turret: 1.2f, seq: 4),
        };

        // ① 한 번에 쭉 적용
        var direct = NewTank();
        for (var i = 0; i < inputs.Length; i++)
            direct = TankPhysics.Step(direct, inputs[i], (uint)i);

        // ② 중간 상태를 저장했다가 거기서부터 재적용(보정과 같은 절차)
        var checkpoint = NewTank();
        checkpoint = TankPhysics.Step(checkpoint, inputs[0], 0);
        checkpoint = TankPhysics.Step(checkpoint, inputs[1], 1);

        var replayed = checkpoint;
        replayed = TankPhysics.Step(replayed, inputs[2], 2);
        replayed = TankPhysics.Step(replayed, inputs[3], 3);

        Assert.Equal(direct.X, replayed.X, 6);
        Assert.Equal(direct.Y, replayed.Y, 6);
        Assert.Equal(direct.BodyAngle, replayed.BodyAngle, 6);
    }

    [Fact]
    public void 전진하면_바라보는_방향으로_이동한다()
    {
        var tank = NewTank(angle: 0);   // +X 방향
        var moved = TankPhysics.Step(tank, Move(throttle: 1), 1);

        Assert.True(moved.X > tank.X);
        Assert.Equal(tank.Y, moved.Y, 4);

        var expected = GameLimits.MoveSpeed / GameLimits.TickRate;
        Assert.Equal(expected, moved.X - tank.X, 3);
    }

    [Fact]
    public void 후진하면_반대로_이동한다()
    {
        var tank = NewTank(angle: 0);
        var moved = TankPhysics.Step(tank, Move(throttle: -1), 1);

        Assert.True(moved.X < tank.X);
    }

    [Fact]
    public void 회전_속도가_상수와_일치한다()
    {
        var tank = NewTank(angle: 0);
        var turned = TankPhysics.Step(tank, Move(steer: 1), 1);

        Assert.Equal(GameLimits.TurnSpeed / GameLimits.TickRate, turned.BodyAngle, 5);
    }

    [Fact]
    public void 월드_밖으로_나갈_수_없다()
    {
        var tank = NewTank(x: 10, y: 10, angle: MathF.PI);   // -X 방향

        for (var i = 0; i < 100; i++)
            tank = TankPhysics.Step(tank, Move(throttle: 1), (uint)i);

        Assert.True(tank.X >= GameLimits.TankRadius - 0.001f);
        Assert.True(tank.Y >= GameLimits.TankRadius - 0.001f);
        Assert.True(tank.X <= GameLimits.WorldWidth - GameLimits.TankRadius + 0.001f);
    }

    [Fact]
    public void 죽은_탱크는_움직이지_않는다()
    {
        var tank = NewTank();
        tank.Alive = false;

        var after = TankPhysics.Step(tank, Move(throttle: 1, steer: 1), 1);

        Assert.Equal(tank.X, after.X);
        Assert.Equal(tank.Y, after.Y);
        Assert.Equal(tank.BodyAngle, after.BodyAngle);
    }

    [Fact]
    public void 각도는_항상_정규화된다()
    {
        var tank = NewTank(angle: 3.1f);

        for (var i = 0; i < 200; i++)
            tank = TankPhysics.Step(tank, Move(steer: 1), (uint)i);

        Assert.InRange(tank.BodyAngle, -MathF.PI, MathF.PI);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(3.5f, 3.5f - MathF.PI * 2f)]
    [InlineData(-3.5f, -3.5f + MathF.PI * 2f)]
    public void Wrap은_구간_안으로_접는다(float input, float expected)
    {
        Assert.Equal(expected, TankPhysics.Wrap(input), 5);
    }

    // ---- 발사 쿨다운(치트 방지의 기본) ----

    [Fact]
    public void 쿨다운_안에는_다시_쏠_수_없다()
    {
        var tank = NewTank();

        Assert.True(TankPhysics.CanFire(tank, 100));
        tank = TankPhysics.ApplyFireCooldown(tank, 100);

        Assert.False(TankPhysics.CanFire(tank, 100));
        Assert.False(TankPhysics.CanFire(tank, 100 + GameLimits.FireCooldownTicks - 1));
        Assert.True(TankPhysics.CanFire(tank, 100 + GameLimits.FireCooldownTicks));
    }

    [Fact]
    public void 죽은_탱크는_쏠_수_없다()
    {
        var tank = NewTank();
        tank.Alive = false;

        Assert.False(TankPhysics.CanFire(tank, 1000));
    }

    // ---- 히트 판정 ----

    [Fact]
    public void 정면의_표적을_맞힌다()
    {
        var target = NewTank(x: 300, y: 100);

        Assert.True(TankPhysics.RayHitsTank(100, 100, 0f, GameLimits.FireRange, target, out var distance));
        Assert.Equal(200f, distance, 2);
    }

    [Fact]
    public void 빗나간_각도는_맞지_않는다()
    {
        var target = NewTank(x: 300, y: 100);

        // 위쪽으로 45도 쏘면 지나간다.
        Assert.False(TankPhysics.RayHitsTank(100, 100, -MathF.PI / 4, GameLimits.FireRange, target, out _));
    }

    [Fact]
    public void 사거리_밖은_맞지_않는다()
    {
        var target = NewTank(x: 100 + GameLimits.FireRange + 50, y: 100);

        Assert.False(TankPhysics.RayHitsTank(100, 100, 0f, GameLimits.FireRange, target, out _));
    }

    [Fact]
    public void 뒤쪽의_표적은_맞지_않는다()
    {
        var target = NewTank(x: 50, y: 100);

        // +X 로 쏘는데 표적은 -X 쪽에 있다.
        Assert.False(TankPhysics.RayHitsTank(100, 100, 0f, GameLimits.FireRange, target, out _));
    }

    [Fact]
    public void 죽은_표적은_맞지_않는다()
    {
        var target = NewTank(x: 300, y: 100);
        target.Alive = false;

        Assert.False(TankPhysics.RayHitsTank(100, 100, 0f, GameLimits.FireRange, target, out _));
    }

    /// <summary>
    /// 지연 보상이 성립하려면 "과거 위치로 판정하면 맞고, 현재 위치로 판정하면 빗나가는" 상황이
    /// 실제로 만들어져야 한다. 이 테스트가 그 상황을 만든다.
    /// </summary>
    [Fact]
    public void 과거_위치로는_맞고_현재_위치로는_빗나간다()
    {
        var past = NewTank(x: 300, y: 100);      // 사수가 보던 위치

        var present = past;
        for (var i = 0; i < 10; i++)             // 그 사이 옆으로 이동했다
        {
            present.BodyAngle = MathF.PI / 2;    // +Y
            present = TankPhysics.Step(present, Move(throttle: 1), (uint)i);
        }

        Assert.True(TankPhysics.RayHitsTank(100, 100, 0f, GameLimits.FireRange, past, out _));
        Assert.False(TankPhysics.RayHitsTank(100, 100, 0f, GameLimits.FireRange, present, out _));
    }
}
