using System;
using KcpProject;

namespace KcpProject.Tests;

/// <summary>P3-4: RingBuffer 경계 동작(성장, 랩어라운드 인덱싱, RemoveFront, Insert).</summary>
public class RingBufferTests
{
    private static readonly int[] ExpectedInsertOrder = { 10, 20, 30, 40, 50 };

    [Fact]
    public void PushBack과_인덱싱()
    {
        var rb = new RingBuffer<int>(4);
        for (var i = 0; i < 4; i++) rb.PushBack(i);

        Assert.Equal(4, rb.Count);
        for (var i = 0; i < 4; i++) Assert.Equal(i, rb[i]);
        Assert.Equal(0, rb.PeekFront());
    }

    [Fact]
    public void 용량을_넘으면_2배로_성장하고_순서를_유지한다()
    {
        var rb = new RingBuffer<int>(2);
        for (var i = 0; i < 100; i++) rb.PushBack(i);

        Assert.Equal(100, rb.Count);
        Assert.True(rb.Capacity >= 100);
        for (var i = 0; i < 100; i++) Assert.Equal(i, rb[i]);
    }

    [Fact]
    public void RemoveFront는_앞에서_제거한다()
    {
        var rb = new RingBuffer<int>(8);
        for (var i = 0; i < 8; i++) rb.PushBack(i);

        rb.RemoveFront(3);

        Assert.Equal(5, rb.Count);
        Assert.Equal(3, rb.PeekFront());
        for (var i = 0; i < 5; i++) Assert.Equal(i + 3, rb[i]);
    }

    [Fact]
    public void RemoveFront_전량_제거()
    {
        var rb = new RingBuffer<int>(8);
        for (var i = 0; i < 8; i++) rb.PushBack(i);

        rb.RemoveFront(8);
        Assert.Equal(0, rb.Count);

        rb.RemoveFront(5);      // 넘치게 지워도 안전해야 한다.
        Assert.Equal(0, rb.Count);

        rb.PushBack(42);
        Assert.Equal(42, rb.PeekFront());
    }

    /// <summary>head 가 배열 끝을 넘어 돌아간 상태(랩어라운드)에서도 인덱싱·성장이 정확해야 한다.</summary>
    [Fact]
    public void 랩어라운드_상태에서_인덱싱과_성장이_정확하다()
    {
        var rb = new RingBuffer<int>(4);
        for (var i = 0; i < 4; i++) rb.PushBack(i);
        rb.RemoveFront(3);                 // head = 3
        for (var i = 100; i < 103; i++) rb.PushBack(i);   // 배열 끝을 넘어 감김

        Assert.Equal(4, rb.Count);
        Assert.Equal(3, rb[0]);
        Assert.Equal(100, rb[1]);
        Assert.Equal(101, rb[2]);
        Assert.Equal(102, rb[3]);

        // 감긴 상태에서 성장시켜도 순서가 유지돼야 한다.
        for (var i = 200; i < 220; i++) rb.PushBack(i);
        Assert.Equal(3, rb[0]);
        Assert.Equal(102, rb[3]);
        Assert.Equal(200, rb[4]);
        Assert.Equal(219, rb[rb.Count - 1]);
    }

    [Fact]
    public void Insert는_지정_위치에_끼워넣는다()
    {
        var rb = new RingBuffer<int>(4);
        rb.PushBack(0);
        rb.PushBack(2);
        rb.PushBack(3);

        rb.Insert(1, 1);

        Assert.Equal(4, rb.Count);
        for (var i = 0; i < 4; i++) Assert.Equal(i, rb[i]);
    }

    [Fact]
    public void Insert는_랩어라운드와_성장을_함께_처리한다()
    {
        var rb = new RingBuffer<int>(4);
        for (var i = 0; i < 4; i++) rb.PushBack(i * 10);
        rb.RemoveFront(2);                    // head = 2, [20, 30]
        rb.PushBack(50);                      // [20, 30, 50] (감김)

        rb.Insert(2, 40);                     // [20, 30, 40, 50]
        rb.Insert(0, 10);                     // [10, 20, 30, 40, 50] (성장 유발)

        Assert.Equal(5, rb.Count);
        var actual = new[] { rb[0], rb[1], rb[2], rb[3], rb[4] };
        Assert.Equal(ExpectedInsertOrder, actual);
    }

    [Fact]
    public void 범위를_벗어난_접근은_예외()
    {
        var rb = new RingBuffer<int>(4);
        rb.PushBack(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => rb[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => rb[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => rb.Insert(5, 0));

        rb.Clear();
        Assert.Throws<InvalidOperationException>(() => rb.PeekFront());
    }

    /// <summary>참조 타입은 제거 시 참조를 끊어 GC 를 막지 않아야 한다.</summary>
    [Fact]
    public void 참조타입_제거시_슬롯을_비운다()
    {
        // 지역 변수가 테스트 프레임에 남지 않도록 별도 메서드에서 넣는다.
        static WeakReference Fill(RingBuffer<object> target)
        {
            var probe = new object();
            target.PushBack(probe);
            target.PushBack(new object());
            return new WeakReference(probe);
        }

        var rb = new RingBuffer<object>(4);
        var weak = Fill(rb);

        rb.RemoveFront(1);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive, "RemoveFront 이후에도 슬롯이 참조를 잡고 있다.");
    }
}
