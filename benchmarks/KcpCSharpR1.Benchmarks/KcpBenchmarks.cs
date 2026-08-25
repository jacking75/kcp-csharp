using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using KcpCSharpR1;

namespace KcpCSharpR1.Benchmarks;

/// <summary>
/// 벤치마크 안에서 쓰는 최소 하네스. 손실 없이 즉시 배달하는 두 Kcp 를 가상 시계로 묶는다.
/// (테스트 프로젝트의 SimulatedLink 와 달리 지연이 0이라 순수 CPU 비용만 측정된다.)
/// </summary>
internal sealed class Loopback
{
    public readonly Kcp A;
    public readonly Kcp B = null!;
    public uint Now;

    public Loopback(uint conv = 0x11223344, int wnd = 128)
    {
        // 람다는 호출 시점에 필드를 읽으므로 생성 순서상 아직 null 인 것은 문제되지 않는다.
        A = new Kcp(conv, (buf, len) => B!.Input(buf, 0, len, true, true), () => Now);
        B = new Kcp(conv, (buf, len) => A!.Input(buf, 0, len, true, true), () => Now);
        A.NoDelay(1, 10, 2, 1);
        B.NoDelay(1, 10, 2, 1);
        A.WndSize(wnd, wnd);
        B.WndSize(wnd, wnd);
    }

    public void Tick(uint stepMs = 10)
    {
        Now += stepMs;
        A.Update();
        B.Update();
    }
}

/// <summary>P3-1 벤치마크 기준선. 모든 클래스에 MemoryDiagnoser 를 붙여 할당(B/op)을 함께 본다.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class SendRecvRoundtrip
{
    private Loopback link = null!;
    private byte[] payload = null!;
    private byte[] recvBuffer = null!;

    /// <summary>왕복시킬 메시지 수.</summary>
    [Params(1000)]
    public int Messages;

    /// <summary>준비.</summary>
    [GlobalSetup]
    public void Setup()
    {
        payload = new byte[1024];
        new Random(7).NextBytes(payload);
        recvBuffer = new byte[64 * 1024];
        link = new Loopback();
    }

    /// <summary>1KB 메시지 N개를 무손실 경로로 왕복시킨다.</summary>
    [Benchmark]
    public long Roundtrip()
    {
        long received = 0;
        var sent = 0;

        while (sent < Messages || link.A.WaitSnd > 0)
        {
            while (sent < Messages && link.A.WaitSnd < 64)
            {
                if (link.A.Send(payload) != 0) break;
                sent++;
            }

            link.Tick();

            while (true)
            {
                var size = link.B.PeekSize();
                if (size < 0) break;
                var n = link.B.Recv(recvBuffer, 0, recvBuffer.Length);
                if (n < 0) break;
                received += n;
            }
        }

        return received;
    }
}

/// <summary>ACK 64개가 한 패킷에 묶여 들어오는 경로의 비용.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class InputAckBatch
{
    private Kcp kcp = null!;
    private byte[] ackPacket = null!;
    private uint now;

    /// <summary>한 패킷에 담을 ACK 수.</summary>
    [Params(64)]
    public int AckCount;

    /// <summary>준비: snd_buf 를 채워 두고 그에 대한 ACK 묶음을 미리 만들어 둔다.</summary>
    [GlobalSetup]
    public void Setup()
    {
        now = 1;
        kcp = new Kcp(0x11223344, (_, _) => { }, () => now);
        kcp.NoDelay(1, 10, 2, 1);
        kcp.WndSize(1024, 1024);

        var payload = new byte[512];
        for (var i = 0; i < AckCount * 4; i++)
            kcp.Send(payload);
        kcp.Flush(false);

        ackPacket = new byte[AckCount * Kcp.IKCP_OVERHEAD];
        var offset = 0;
        for (var i = 0; i < AckCount; i++)
        {
            offset += Kcp.ikcp_encode32u(ackPacket, offset, 0x11223344);
            offset += Kcp.ikcp_encode8u(ackPacket, offset, Kcp.IKCP_CMD_ACK);
            offset += Kcp.ikcp_encode8u(ackPacket, offset, 0);
            offset += Kcp.ikcp_encode16u(ackPacket, offset, 1024);
            offset += Kcp.ikcp_encode32u(ackPacket, offset, 0);          // ts
            offset += Kcp.ikcp_encode32u(ackPacket, offset, (uint)i);    // sn
            offset += Kcp.ikcp_encode32u(ackPacket, offset, 0);          // una
            offset += Kcp.ikcp_encode32u(ackPacket, offset, 0);          // len
        }
    }

    /// <summary>ACK 묶음 1패킷 파싱.</summary>
    [Benchmark]
    public int ParseAckBatch() => kcp.Input(ackPacket, 0, ackPacket.Length, true, false);
}

/// <summary>송신 버퍼가 큰 상태에서의 Flush 비용(윈도 1024).</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class FlushLargeWindow
{
    private Kcp kcp = null!;
    private uint now = 1;

    /// <summary>송신 버퍼에 채워 둘 세그먼트 수.</summary>
    [Params(1024)]
    public int WindowSize;

    /// <summary>준비.</summary>
    [GlobalSetup]
    public void Setup()
    {
        kcp = new Kcp(0x11223344, (_, _) => { }, () => now);
        kcp.NoDelay(1, 10, 2, 1);
        kcp.WndSize(WindowSize, WindowSize);

        // 상대가 큰 윈도를 통지해야 snd_buf 가 실제로 WindowSize 만큼 찬다(rmt_wnd 기본값은 32).
        var wins = new byte[Kcp.IKCP_OVERHEAD];
        var o = 0;
        o += Kcp.ikcp_encode32u(wins, o, 0x11223344);
        o += Kcp.ikcp_encode8u(wins, o, Kcp.IKCP_CMD_WINS);
        o += Kcp.ikcp_encode8u(wins, o, 0);
        o += Kcp.ikcp_encode16u(wins, o, (ushort)WindowSize);
        o += Kcp.ikcp_encode32u(wins, o, 0);
        o += Kcp.ikcp_encode32u(wins, o, 0);
        o += Kcp.ikcp_encode32u(wins, o, 0);
        Kcp.ikcp_encode32u(wins, o, 0);
        kcp.Input(wins, 0, wins.Length, true, false);

        var payload = new byte[1024];
        for (var i = 0; i < WindowSize; i++)
            kcp.Send(payload);
        kcp.Flush(false);   // snd_queue -> snd_buf
    }

    /// <summary>
    /// 재전송 대상이 없는 상태의 snd_buf 순회 비용.
    /// 시계를 진행시키면 RTO 만료로 1024개 전량 재전송이 섞여 측정이 흔들리므로 시각은 고정한다.
    /// </summary>
    [Benchmark]
    public uint Flush() => kcp.Flush(false);
}

/// <summary>32KB 메시지 조각화 왕복.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class FragmentationLarge
{
    private Loopback link = null!;
    private byte[] payload = null!;
    private byte[] recvBuffer = null!;

    /// <summary>메시지 크기(바이트).</summary>
    [Params(32 * 1024)]
    public int MessageSize;

    /// <summary>준비.</summary>
    [GlobalSetup]
    public void Setup()
    {
        payload = new byte[MessageSize];
        new Random(11).NextBytes(payload);
        recvBuffer = new byte[MessageSize * 2];
        link = new Loopback(wnd: 256);
    }

    /// <summary>큰 메시지 1개를 조각화해 보내고 재조립까지 마친다.</summary>
    [Benchmark]
    public int FragmentRoundtrip()
    {
        if (link.A.Send(payload) != 0)
            throw new InvalidOperationException("Send 실패");

        var total = 0;
        while (total < MessageSize)
        {
            link.Tick();

            while (true)
            {
                var size = link.B.PeekSize();
                if (size < 0) break;
                var n = link.B.Recv(recvBuffer, 0, recvBuffer.Length);
                if (n < 0) break;
                total += n;
            }
        }

        return total;
    }
}
