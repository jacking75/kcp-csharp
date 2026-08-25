using System;
using System.IO;

namespace KcpCSharpR1.SimTests;

/// <summary>
/// <see cref="Kcp"/> 2개 + 양방향 <see cref="SimulatedLink"/> + 가상 시계를 묶은 시나리오 테스트 하네스.
/// </summary>
/// <remarks>
/// 두 Kcp 인스턴스는 모두 <see cref="Now"/> 를 시계로 주입받는다. 따라서 테스트는
/// <see cref="AdvanceTo"/> / <see cref="RunUntil"/> 로 시간을 직접 조종한다(실제 대기 없음).
/// </remarks>
public sealed class KcpPair
{
    /// <summary>가상 시간 진행 단위(ms).</summary>
    public const uint StepMs = 10;

    /// <summary>A 엔드포인트.</summary>
    public Kcp A { get; private set; } = null!;

    /// <summary>B 엔드포인트.</summary>
    public Kcp B { get; private set; } = null!;

    /// <summary>A → B 방향 링크.</summary>
    public SimulatedLink AtoB { get; private set; } = null!;

    /// <summary>B → A 방향 링크.</summary>
    public SimulatedLink BtoA { get; private set; } = null!;

    /// <summary>현재 가상 시각(ms).</summary>
    public uint Now;

    /// <summary>A 가 애플리케이션 계층으로 꺼낸 데이터 누적.</summary>
    public MemoryStream ReceivedA { get; } = new MemoryStream();

    /// <summary>B 가 애플리케이션 계층으로 꺼낸 데이터 누적.</summary>
    public MemoryStream ReceivedB { get; } = new MemoryStream();

    /// <summary>false 면 A 가 Recv 를 하지 않는다(수신 윈도 0 시나리오용).</summary>
    public bool DrainA = true;

    /// <summary>false 면 B 가 Recv 를 하지 않는다(수신 윈도 0 시나리오용).</summary>
    public bool DrainB = true;

    /// <summary>true 면 <see cref="Kcp.Input(byte[], int, int, bool, bool)"/> 에 ackNoDelay 를 준다.</summary>
    public bool AckNoDelay;

    /// <summary>A 가 마지막으로 수신 처리한 가상 시각.</summary>
    public uint LastInputAtA { get; private set; }

    /// <summary>B 가 마지막으로 수신 처리한 가상 시각.</summary>
    public uint LastInputAtB { get; private set; }

    private byte[] recvBuffer = new byte[64 * 1024];

    private KcpPair() { }

    /// <summary>하네스를 만든다.</summary>
    /// <param name="seed">난수 시드(재현성).</param>
    /// <param name="loss">손실률 0.0~1.0.</param>
    /// <param name="delayMs">편도 지연(ms).</param>
    /// <param name="jitterMs">지터(±ms).</param>
    /// <param name="duplicate">중복률.</param>
    /// <param name="reorder">순서 뒤섞임률.</param>
    /// <param name="startTime">가상 시계 시작 시각(랩어라운드 시나리오용).</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="configure">두 Kcp 에 공통으로 적용할 추가 설정.</param>
    /// <returns>구성된 하네스.</returns>
    public static KcpPair Create(
        int seed,
        double loss = 0,
        int delayMs = 50,
        int jitterMs = 0,
        double duplicate = 0,
        double reorder = 0,
        uint startTime = 0,
        uint conv = 0x11223344,
        Action<Kcp>? configure = null)
    {
        var pair = new KcpPair { Now = startTime };

        pair.AtoB = new SimulatedLink(seed) { DelayMs = delayMs, JitterMs = jitterMs, LossRate = loss, DuplicateRate = duplicate, ReorderRate = reorder };
        pair.BtoA = new SimulatedLink(seed + 1) { DelayMs = delayMs, JitterMs = jitterMs, LossRate = loss, DuplicateRate = duplicate, ReorderRate = reorder };

        pair.A = new Kcp(conv, (buf, len) => pair.AtoB.Send(buf, len), () => pair.Now);
        pair.B = new Kcp(conv, (buf, len) => pair.BtoA.Send(buf, len), () => pair.Now);

        // 게임에서 흔히 쓰는 빠른 설정: nodelay=1, interval=10, resend=2, nc=1
        pair.A.NoDelay(1, 10, 2, 1);
        pair.B.NoDelay(1, 10, 2, 1);
        configure?.Invoke(pair.A);
        configure?.Invoke(pair.B);

        pair.AtoB.Deliver = (buf, len) =>
        {
            pair.LastInputAtB = pair.Now;
            pair.B.Input(buf, 0, len, true, pair.AckNoDelay);
        };
        pair.BtoA.Deliver = (buf, len) =>
        {
            pair.LastInputAtA = pair.Now;
            pair.A.Input(buf, 0, len, true, pair.AckNoDelay);
        };

        // 링크의 가상 시각을 시작 시각에 맞춘다.
        pair.AtoB.Advance(startTime);
        pair.BtoA.Advance(startTime);

        return pair;
    }

    /// <summary>가상 시간을 <see cref="StepMs"/> 단위로 목표 시각까지 진행시킨다.</summary>
    /// <param name="targetMs">목표 가상 시각(ms).</param>
    public void AdvanceTo(uint targetMs)
    {
        while (Kcp._itimediff(targetMs, Now) > 0)
        {
            Step();
        }
    }

    /// <summary>한 스텝(<see cref="StepMs"/>)만 진행시킨다.</summary>
    public void Step()
    {
        Now += StepMs;

        AtoB.Advance(Now);
        BtoA.Advance(Now);

        A.Update();
        B.Update();

        Drain();
    }

    /// <summary>조건이 참이 될 때까지 진행시킨다.</summary>
    /// <param name="condition">종료 조건.</param>
    /// <param name="maxMs">최대 진행 시간(가상 ms).</param>
    /// <returns>조건을 만족하면 true, 시간 초과면 false.</returns>
    public bool RunUntil(Func<bool> condition, uint maxMs)
    {
        var deadline = Now + maxMs;
        while (Kcp._itimediff(deadline, Now) > 0)
        {
            if (condition())
                return true;
            Step();
        }
        return condition();
    }

    /// <summary>양쪽의 수신 큐를 비워 <see cref="ReceivedA"/> / <see cref="ReceivedB"/> 에 누적한다.</summary>
    public void Drain()
    {
        if (DrainA) DrainOne(A, ReceivedA);
        if (DrainB) DrainOne(B, ReceivedB);
    }

    private void DrainOne(Kcp kcp, MemoryStream sink)
    {
        while (true)
        {
            var size = kcp.PeekSize();
            if (size < 0) break;

            if (size > recvBuffer.Length)
                recvBuffer = new byte[Math.Max(size, recvBuffer.Length * 2)];

            // Recv 는 메시지 1개를 반드시 소비하므로(0바이트 메시지 포함) 루프는 항상 진행한다.
            var n = kcp.Recv(recvBuffer, 0, recvBuffer.Length);
            if (n < 0) break;
            if (n > 0) sink.Write(recvBuffer, 0, n);
        }
    }
}

/// <summary>재현 가능한 테스트 데이터 생성기.</summary>
public static class TestData
{
    /// <summary>시드로 결정되는 바이트열을 만든다.</summary>
    /// <param name="length">길이.</param>
    /// <param name="seed">시드.</param>
    /// <returns>바이트 배열.</returns>
    public static byte[] DeterministicBytes(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }
}
