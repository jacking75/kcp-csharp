using System;
using System.Runtime.CompilerServices;

namespace KcpProject;

/// <summary>
/// 2의 거듭제곱 용량을 가진 원형 덱(circular deque).
/// </summary>
/// <remarks>
/// KCP 의 네 큐(snd_queue, rcv_queue, snd_buf, rcv_buf)는 "앞에서 n개 제거"가 잦다.
/// <see cref="System.Collections.Generic.List{T}"/> 의 <c>RemoveRange(0, n)</c> 는 매번 O(n) 이동이 발생하지만
/// 이 자료구조는 head 인덱스만 옮기므로 O(1) 이다(진단 PF-04).
/// </remarks>
/// <typeparam name="T">원소 타입.</typeparam>
internal sealed class RingBuffer<T>
{
    private T[] items;
    private int head;
    private int count;

    /// <summary>초기 용량을 지정해 만든다(2의 거듭제곱으로 올림).</summary>
    /// <param name="capacity">초기 용량.</param>
    public RingBuffer(int capacity = 16)
    {
        items = new T[NextPowerOfTwo(Math.Max(2, capacity))];
    }

    /// <summary>현재 원소 수.</summary>
    public int Count => count;

    /// <summary>내부 배열 용량.</summary>
    public int Capacity => items.Length;

    /// <summary>앞에서 <paramref name="index"/> 번째 원소에 대한 참조.</summary>
    /// <param name="index">0 기반 인덱스.</param>
    /// <returns>원소 참조.</returns>
    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref items[(head + index) & (items.Length - 1)];
        }
    }

    /// <summary>
    /// 경계 검사 없이 <paramref name="index"/> 번째 원소를 반환한다.
    /// </summary>
    /// <remarks>
    /// <c>for (i = 0; i &lt; Count; i++)</c> 처럼 <b>범위가 이미 보장된 핫루프 전용</b>이다.
    /// Flush/Check 의 snd_buf 순회는 윈도 크기(최대 수천)만큼 매 틱 돌기 때문에
    /// 원소당 경계 검사 한 번이 그대로 비용이 된다(P3-4 벤치에서 확인).
    /// </remarks>
    /// <param name="index">0 기반 인덱스(호출자가 범위를 보장해야 한다).</param>
    /// <returns>원소 참조.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T AtUnchecked(int index) => ref items[(head + index) & (items.Length - 1)];

    /// <summary>
    /// 순회 전용 스냅숏 뷰. 배열·head·mask 를 지역에 붙잡아 두어 원소마다 필드를 다시 읽지 않는다.
    /// </summary>
    /// <remarks>
    /// 뷰를 만든 뒤 원본을 수정(PushBack/RemoveFront/Insert)하면 뷰는 무효가 된다.
    /// Flush/Check 의 snd_buf 순회처럼 <b>루프 안에서 큐를 건드리지 않는 구간</b>에서만 쓴다.
    /// </remarks>
    /// <returns>스냅숏 뷰.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public View CreateView() => new View(items, head);

    /// <summary>순회 전용 스냅숏 뷰(<see cref="CreateView"/> 참조).</summary>
    public readonly struct View
    {
        private readonly T[] items;
        private readonly int head;
        private readonly int mask;

        internal View(T[] items, int head)
        {
            this.items = items;
            this.head = head;
            this.mask = items.Length - 1;
        }

        /// <summary>앞에서 <paramref name="index"/> 번째 원소.</summary>
        /// <param name="index">0 기반 인덱스(호출자가 범위를 보장해야 한다).</param>
        /// <returns>원소.</returns>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => items[(head + index) & mask];
        }
    }

    /// <summary>뒤에 원소를 추가한다.</summary>
    /// <param name="item">추가할 원소.</param>
    public void PushBack(T item)
    {
        EnsureCapacity(count + 1);
        items[(head + count) & (items.Length - 1)] = item;
        count++;
    }

    /// <summary>맨 앞 원소에 대한 참조를 반환한다.</summary>
    /// <returns>맨 앞 원소.</returns>
    public ref T PeekFront()
    {
        if (count == 0)
            throw new InvalidOperationException("비어 있는 RingBuffer 입니다.");
        return ref items[head];
    }

    /// <summary>지정한 위치에 원소를 끼워 넣는다(뒤쪽 원소를 한 칸씩 민다).</summary>
    /// <param name="index">삽입 위치(0 ~ Count).</param>
    /// <param name="item">삽입할 원소.</param>
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)count)
            throw new ArgumentOutOfRangeException(nameof(index));

        EnsureCapacity(count + 1);

        var mask = items.Length - 1;
        for (var i = count; i > index; i--)
        {
            items[(head + i) & mask] = items[(head + i - 1) & mask];
        }
        items[(head + index) & mask] = item;
        count++;
    }

    /// <summary>앞에서 <paramref name="n"/> 개를 제거한다. O(1).</summary>
    /// <param name="n">제거할 개수.</param>
    public void RemoveFront(int n)
    {
        if (n <= 0) return;
        if (n > count) n = count;

        var mask = items.Length - 1;
        // 참조 타입이면 남은 참조를 끊어 GC 를 방해하지 않게 한다.
        if (!typeof(T).IsValueType)
        {
            for (var i = 0; i < n; i++)
                items[(head + i) & mask] = default!;
        }

        head = (head + n) & mask;
        count -= n;
    }

    /// <summary>모든 원소를 제거한다.</summary>
    public void Clear()
    {
        RemoveFront(count);
        head = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= items.Length)
            return;

        var newCapacity = items.Length;
        while (newCapacity < required)
            newCapacity <<= 1;

        var newItems = new T[newCapacity];
        var mask = items.Length - 1;
        for (var i = 0; i < count; i++)
            newItems[i] = items[(head + i) & mask];

        items = newItems;
        head = 0;
    }

    private static int NextPowerOfTwo(int value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}
