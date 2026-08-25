using System;
using System.Collections.Generic;
using TankBattle.Shared;

namespace TankBattle.Client;

/// <summary>
/// 클라이언트 예측과 서버 보정(reconciliation).
/// </summary>
/// <remarks>
/// <para><b>문제</b>: 입력을 서버에 보내고 결과를 기다리면 RTT 만큼 조작이 둔해진다.
/// RTT 100ms면 방향키를 누르고 0.1초 뒤에 움직이기 시작한다 — 액션 게임에서는 치명적이다.</para>
///
/// <para><b>예측</b>: 입력을 보내는 동시에 <b>내 화면에서는 즉시 적용</b>한다.
/// 서버와 같은 물리 코드(<see cref="TankPhysics"/>)를 쓰므로 대개 같은 결과가 나온다.</para>
///
/// <para><b>보정</b>: 서버 스냅숏에는 "너의 몇 번 입력까지 반영했다"가 들어 있다.
/// 그 시점의 <b>서버 위치를 진실로 받아들이고</b>, 아직 서버가 모르는 입력들을 다시 적용해 현재를 만든다.
/// 예측이 맞았다면 결과가 같아 화면이 흔들리지 않고, 틀렸다면(충돌·치트 방지 클램프 등) 그때만 보정된다.</para>
///
/// <para>이 되감기-재적용이 성립하려면 물리가 <b>순수 함수</b>여야 한다. 그래서 Shared 에 두었다.</para>
/// </remarks>
internal sealed class PredictionState
{
    /// <summary>아직 서버가 확인해 주지 않은 입력들.</summary>
    private readonly List<TankInput> unacknowledged = new();

    /// <summary>예측으로 계산한 내 현재 상태(화면에 그리는 값).</summary>
    public TankState Predicted;

    /// <summary>마지막으로 서버가 알려 준 권위 상태.</summary>
    public TankState Authoritative;

    /// <summary>다음에 매길 입력 시퀀스.</summary>
    public uint NextSequence = 1;

    /// <summary>예측을 켤지. 끄면 서버 상태를 그대로 그린다(비교 실험용).</summary>
    public bool Enabled = true;

    /// <summary>보정으로 위치가 튄 횟수(예측이 틀린 횟수).</summary>
    public long CorrectionCount { get; private set; }

    /// <summary>마지막 보정에서 벌어진 거리(픽셀).</summary>
    public float LastCorrectionDistance { get; private set; }

    /// <summary>아직 확인 안 된 입력 수.</summary>
    public int PendingInputCount => unacknowledged.Count;

    /// <summary>입력을 하나 만들어 즉시 예측 적용하고, 미확인 목록에 넣는다.</summary>
    /// <param name="throttle">전후진.</param>
    /// <param name="steer">회전.</param>
    /// <param name="turretAngle">포탑 각도.</param>
    /// <param name="fire">발사 여부.</param>
    /// <param name="tick">클라이언트가 추정하는 현재 틱.</param>
    /// <returns>서버로 보낼 입력.</returns>
    public TankInput CreateInput(sbyte throttle, sbyte steer, float turretAngle, bool fire, uint tick)
    {
        var input = new TankInput
        {
            Sequence = NextSequence++,
            Throttle = throttle,
            Steer = steer,
            TurretAngle = turretAngle,
            Fire = fire,
        };

        unacknowledged.Add(input);

        // 미확인 입력이 무한정 쌓이지 않게 한다(연결이 나빠 오래 확인이 안 오는 경우).
        if (unacknowledged.Count > 120) unacknowledged.RemoveAt(0);

        if (Enabled)
        {
            // ★ 예측: 서버 응답을 기다리지 않고 지금 바로 적용한다.
            Predicted = TankPhysics.Step(Predicted, input, tick);
        }

        return input;
    }

    /// <summary>
    /// 서버 스냅숏을 받아 보정한다.
    /// </summary>
    /// <param name="serverState">서버가 알려 준 내 상태.</param>
    /// <param name="lastProcessedSeq">서버가 반영한 마지막 입력 시퀀스.</param>
    /// <param name="serverTick">서버 틱.</param>
    public void Reconcile(TankState serverState, uint lastProcessedSeq, uint serverTick)
    {
        Authoritative = serverState;

        if (!Enabled)
        {
            // 예측을 껐다면 서버 값을 그대로 쓴다 → 입력이 RTT 만큼 늦게 반영되는 것을 체감할 수 있다.
            Predicted = serverState;
            unacknowledged.Clear();
            return;
        }

        // 서버가 이미 처리한 입력은 버린다.
        unacknowledged.RemoveAll(input => input.Sequence <= lastProcessedSeq);

        // 서버 상태에서 출발해 미확인 입력을 다시 적용한다(되감기 → 재적용).
        var replayed = serverState;
        for (var i = 0; i < unacknowledged.Count; i++)
            replayed = TankPhysics.Step(replayed, unacknowledged[i], serverTick + (uint)i);

        // 예측과 얼마나 어긋났는지 재 본다.
        var dx = replayed.X - Predicted.X;
        var dy = replayed.Y - Predicted.Y;
        var distance = MathF.Sqrt(dx * dx + dy * dy);

        if (distance > 0.5f)
        {
            CorrectionCount++;
            LastCorrectionDistance = distance;
        }

        Predicted = replayed;
    }

    /// <summary>사망·리스폰처럼 서버가 위치를 통째로 정하는 경우 예측을 초기화한다.</summary>
    /// <param name="state">새 상태.</param>
    public void Reset(TankState state)
    {
        Predicted = state;
        Authoritative = state;
        unacknowledged.Clear();
    }
}
