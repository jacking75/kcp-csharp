using System;
using System.Collections.Generic;
using System.Net;
using KcpProject;
using KcpProject.Transport;

namespace KcpProject.Tests;

/// <summary>P4-4b: 최소 힙 스케줄러(순서, lazy deletion, 랩어라운드).</summary>
public class SessionSchedulerTests
{
    private static KcpSession NewSession(int port)
    {
        var remote = new IPEndPoint(IPAddress.Loopback, port);
        return new KcpSession(remote, new KcpSessionOptions(), static (_, _) => { }, static () => 0);
    }

    [Fact]
    public void 기한이_이른_순서로_꺼낸다()
    {
        var scheduler = new SessionScheduler();
        var a = NewSession(1);
        var b = NewSession(2);
        var c = NewSession(3);

        scheduler.Schedule(a, 300);
        scheduler.Schedule(b, 100);
        scheduler.Schedule(c, 200);

        Assert.True(scheduler.TryPopDue(1000, out var first));
        Assert.True(scheduler.TryPopDue(1000, out var second));
        Assert.True(scheduler.TryPopDue(1000, out var third));

        Assert.Same(b, first);
        Assert.Same(c, second);
        Assert.Same(a, third);
        Assert.False(scheduler.TryPopDue(1000, out _));
    }

    [Fact]
    public void 기한이_안된_세션은_꺼내지_않는다()
    {
        var scheduler = new SessionScheduler();
        var a = NewSession(1);
        scheduler.Schedule(a, 500);

        Assert.False(scheduler.TryPopDue(499, out _));
        Assert.True(scheduler.TryPopDue(500, out var popped));
        Assert.Same(a, popped);
    }

    /// <summary>같은 세션을 다시 등록하면 예전 항목은 버려져야 한다(lazy deletion).</summary>
    [Fact]
    public void 재등록하면_예전_항목은_무시된다()
    {
        var scheduler = new SessionScheduler();
        var a = NewSession(1);

        scheduler.Schedule(a, 1000);
        scheduler.Schedule(a, 100);     // 앞당김

        Assert.True(scheduler.TryPopDue(200, out var popped));
        Assert.Same(a, popped);

        // 예전 항목(1000)은 힙에 남아 있지만 스탬프가 낡아 버려져야 한다.
        Assert.False(scheduler.TryPopDue(5000, out _));
    }

    [Fact]
    public void 제거한_세션은_꺼내지_않는다()
    {
        var scheduler = new SessionScheduler();
        var a = NewSession(1);
        var b = NewSession(2);

        scheduler.Schedule(a, 100);
        scheduler.Schedule(b, 200);
        scheduler.Remove(a);

        Assert.True(scheduler.TryPopDue(1000, out var popped));
        Assert.Same(b, popped);
        Assert.False(scheduler.TryPopDue(1000, out _));
    }

    [Fact]
    public void TimeToNext는_가장_이른_기한까지_남은_시간이다()
    {
        var scheduler = new SessionScheduler();

        Assert.Equal(5, scheduler.TimeToNext(1000, fallback: 5));

        scheduler.Schedule(NewSession(1), 1050);
        Assert.Equal(50, scheduler.TimeToNext(1000, fallback: 500));

        // 이미 지난 기한이면 0(즉시 처리).
        Assert.Equal(0, scheduler.TimeToNext(2000, fallback: 500));

        // fallback 보다 크면 fallback 으로 잘린다.
        Assert.Equal(10, scheduler.TimeToNext(1000, fallback: 10));
    }

    /// <summary>시각이 32비트를 넘어 감겨도 순서가 유지돼야 한다(진단 B-02).</summary>
    [Fact]
    public void 랩어라운드_경계에서도_순서가_유지된다()
    {
        var scheduler = new SessionScheduler();
        var before = NewSession(1);
        var after = NewSession(2);

        scheduler.Schedule(before, 0xFFFF_FFF0);   // 랩어라운드 직전
        scheduler.Schedule(after, 0x0000_0010);    // 랩어라운드 직후(= 32ms 뒤)

        var now = 0xFFFF_FFF0u;
        Assert.True(scheduler.TryPopDue(now, out var first));
        Assert.Same(before, first);
        Assert.False(scheduler.TryPopDue(now, out _));

        now = 0x0000_0010;
        Assert.True(scheduler.TryPopDue(now, out var second));
        Assert.Same(after, second);
    }

    [Fact]
    public void 다수_세션에서도_전부_기한순으로_나온다()
    {
        var scheduler = new SessionScheduler();
        var rng = new Random(1234);
        var expected = new List<(uint Due, KcpSession Session)>();

        for (var i = 0; i < 1000; i++)
        {
            var due = (uint)rng.Next(0, 10_000);
            var session = NewSession(10_000 + i);
            expected.Add((due, session));
            scheduler.Schedule(session, due);
        }

        expected.Sort((x, y) => x.Due.CompareTo(y.Due));

        var popped = new List<KcpSession>();
        while (scheduler.TryPopDue(20_000, out var session))
            popped.Add(session);

        Assert.Equal(expected.Count, popped.Count);

        // 기한이 같은 항목끼리의 순서는 보장하지 않으므로 기한 수열만 비교한다.
        var lookup = new Dictionary<KcpSession, uint>();
        foreach (var (due, session) in expected) lookup[session] = due;

        uint previous = 0;
        foreach (var session in popped)
        {
            var due = lookup[session];
            Assert.True(due >= previous, $"기한 역전: {previous} -> {due}");
            previous = due;
        }
    }

    [Fact]
    public void Clear는_전부_비운다()
    {
        var scheduler = new SessionScheduler();
        scheduler.Schedule(NewSession(1), 10);
        scheduler.Schedule(NewSession(2), 20);

        scheduler.Clear();

        Assert.Equal(0, scheduler.Count);
        Assert.Equal(0, scheduler.SessionCount);
        Assert.False(scheduler.TryPopDue(1000, out _));
    }
}
