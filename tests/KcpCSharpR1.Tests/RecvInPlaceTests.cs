using System;
using System.Collections.Generic;
using KcpCSharpR1;

namespace KcpCSharpR1.Tests;

/// <summary>진단 G-2: 코어의 무복사 수신(<see cref="Kcp.RecvInPlace"/>) 검증.</summary>
public class RecvInPlaceTests
{
    /// <summary>손실 없이 즉시 배달하는 두 Kcp + 가상 시계.</summary>
    private sealed class Loopback
    {
        public readonly Kcp A;
        public readonly Kcp B = null!;
        public uint Now;

        public Loopback(Action<Kcp>? configure = null)
        {
            A = new Kcp(0x2468, (buf, len) => B!.Input(buf, 0, len, true, true), () => Now);
            B = new Kcp(0x2468, (buf, len) => A!.Input(buf, 0, len, true, true), () => Now);
            A.NoDelay(1, 10, 2, 1);
            B.NoDelay(1, 10, 2, 1);
            configure?.Invoke(A);
            configure?.Invoke(B);
        }

        public void Tick(int times = 5, uint stepMs = 10)
        {
            for (var i = 0; i < times; i++)
            {
                Now += stepMs;
                A.Update();
                B.Update();
            }
        }
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(seed + i);
        return data;
    }

    [Fact]
    public void 핸들러가_null이면_예외()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);
        Assert.Throws<ArgumentNullException>(() => kcp.RecvInPlace(null!));
    }

    [Fact]
    public void 읽을_것이_없으면_음수1()
    {
        var kcp = new Kcp(1, (_, _) => { }, () => 0);
        Assert.Equal(-1, kcp.RecvInPlace(_ => Assert.Fail("호출되면 안 된다.")));
    }

    [Fact]
    public void 단일_조각은_복사_없이_그대로_넘어온다()
    {
        var link = new Loopback();
        var payload = Pattern(200, 7);

        Assert.Equal(0, link.A.Send(payload));
        link.Tick();

        byte[]? seen = null;
        var n = link.B.RecvInPlace(data => seen = data.ToArray());

        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, seen);
        Assert.Equal(-1, link.B.PeekSize());   // 큐가 비었다.
    }

    [Fact]
    public void 여러_메시지를_이어서_꺼낼_수_있다()
    {
        var link = new Loopback();
        for (var i = 0; i < 5; i++)
            Assert.Equal(0, link.A.Send(Pattern(64, (byte)(i * 10))));

        link.Tick();

        var received = new List<byte[]>();
        while (true)
        {
            var n = link.B.RecvInPlace(data => received.Add(data.ToArray()));
            if (n < 0) break;
        }

        Assert.Equal(5, received.Count);
        for (var i = 0; i < 5; i++)
            Assert.Equal(Pattern(64, (byte)(i * 10)), received[i]);
    }

    [Fact]
    public void 다조각_메시지는_음수3을_돌려주고_큐를_건드리지_않는다()
    {
        var link = new Loopback();
        var big = Pattern(5_000, 3);   // mss(1376) 를 넘어 여러 조각으로 쪼개진다.

        Assert.Equal(0, link.A.Send(big));
        link.Tick(10);

        var called = false;
        Assert.Equal(-3, link.B.RecvInPlace(_ => called = true));
        Assert.False(called);

        // 큐가 그대로 남아 있어야 기존 조립 경로로 폴백할 수 있다.
        Assert.Equal(big.Length, link.B.PeekSize());

        var buffer = new byte[big.Length];
        Assert.Equal(big.Length, link.B.Recv(buffer));
        Assert.Equal(big, buffer);
    }

    [Fact]
    public void 무복사_경로와_조립_경로를_섞어도_순서가_유지된다()
    {
        var link = new Loopback();
        var small1 = Pattern(100, 1);
        var big = Pattern(4_000, 2);
        var small2 = Pattern(120, 3);

        Assert.Equal(0, link.A.Send(small1));
        Assert.Equal(0, link.A.Send(big));
        Assert.Equal(0, link.A.Send(small2));
        link.Tick(10);

        var order = new List<byte[]>();
        var scratch = new byte[8192];

        while (true)
        {
            var n = link.B.RecvInPlace(data => order.Add(data.ToArray()));
            if (n >= 0) continue;
            if (n == -1) break;

            // -3: 다조각이므로 조립 경로로 폴백한다.
            var size = link.B.PeekSize();
            Assert.True(size > 0);
            var read = link.B.Recv(scratch.AsSpan(0, size));
            Assert.Equal(size, read);
            order.Add(scratch[..read]);
        }

        Assert.Equal(3, order.Count);
        Assert.Equal(small1, order[0]);
        Assert.Equal(big, order[1]);
        Assert.Equal(small2, order[2]);
    }

    [Fact]
    public void 핸들러가_던져도_세그먼트는_풀로_돌아간다()
    {
        var link = new Loopback();
        Assert.Equal(0, link.A.Send(Pattern(50, 9)));
        link.Tick();

        Assert.Throws<InvalidOperationException>(
            () => link.B.RecvInPlace(_ => throw new InvalidOperationException("테스트")));

        // 메시지는 이미 큐에서 빠졌고, 이후 동작이 정상이어야 한다(이중 반환·누수 없음).
        Assert.Equal(-1, link.B.PeekSize());

        Assert.Equal(0, link.A.Send(Pattern(50, 11)));
        link.Tick();

        byte[]? seen = null;
        Assert.Equal(50, link.B.RecvInPlace(data => seen = data.ToArray()));
        Assert.Equal(Pattern(50, 11), seen);
    }

    [Fact]
    public void 무복사_수신은_힙_할당을_남기지_않는다()
    {
        // 측정 대상은 '수신 경로'다. 송신·Update 는 워밍업과 준비 구간에 두고 드레인만 잰다.
        var link = new Loopback(k => k.WndSize(128, 128));
        var payload = Pattern(300, 5);
        var total = 0;
        Kcp.InPlaceMessageHandler handler = data => total += data.Length;

        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 20; i++) link.A.Send(payload);
            link.Tick(10);
            while (link.B.RecvInPlace(handler) >= 0) { }
        }

        for (var i = 0; i < 20; i++) link.A.Send(payload);
        link.Tick(10);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var drained = 0;
        while (link.B.RecvInPlace(handler) >= 0) drained++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(20, drained);
        Assert.True(total > 0);
        Assert.True(allocated == 0, $"무복사 수신 {drained}회에서 {allocated}바이트를 할당했습니다(0 이어야 합니다).");
    }

    [Fact]
    public void 수신_윈도가_꽉_찬_뒤에도_rcv_buf가_이어서_흘러간다()
    {
        // MoveRcvBufToQueue 가 kcp-go 와 같은 한도 계산을 쓰는지 본다.
        // (루프 안에서 옮기면서 세면 실효 윈도가 절반이 되어 rcv_buf 가 늦게 빠진다.)
        var link = new Loopback(k => k.WndSize(128, 32));
        const int count = 40;

        for (var i = 0; i < count; i++)
            Assert.Equal(0, link.A.Send(Pattern(64, (byte)i)));

        link.Tick(20);

        var drained = 0;
        while (link.B.RecvInPlace(_ => drained++) >= 0) { }

        // 첫 드레인에서 수신 윈도(32) 만큼은 반드시 올라와야 한다.
        Assert.True(drained >= 31, $"첫 드레인에서 {drained}개만 올라왔습니다(31개 이상이어야 합니다).");

        link.Tick(20);
        while (link.B.RecvInPlace(_ => drained++) >= 0) { }
        Assert.Equal(count, drained);
    }
}
