using System;
using System.Collections.Generic;

namespace KcpProject.Transport;

/// <summary>
/// 세션의 다음 갱신 시각을 관리하는 최소 힙 스케줄러(P4-4b).
/// </summary>
/// <remarks>
/// <para>
/// 세션 1만 개에서 매 틱 전 세션의 <c>Kcp.Update</c> 를 부르면 대부분이 헛일이다.
/// 각 세션이 알려 준 "다음에 깨워야 할 시각"을 힙에 넣고, 기한이 된 것만 꺼내 갱신한다.
/// </para>
/// <para>
/// 갱신은 <b>lazy deletion</b> 방식이다. 세션의 예정 시각이 앞당겨지면(Send/Input 발생) 힙에서 찾아
/// 지우지 않고 새 항목을 넣는다. 꺼낼 때 세션이 기억하는 최신 스탬프와 다르면 오래된 항목으로 보고 버린다.
/// 따라서 모든 연산이 O(log n) 이다.
/// </para>
/// <para><b>스레딩 규약</b>: 수신 루프 스레드 하나만 접근한다.</para>
/// </remarks>
internal sealed class SessionScheduler
{
    private struct Entry
    {
        public uint DueTime;
        public long Stamp;
        public KcpSession Session;
    }

    private readonly List<Entry> heap = new List<Entry>(256);
    private readonly Dictionary<KcpSession, long> stamps = new Dictionary<KcpSession, long>();
    private long nextStamp = 1;

    /// <summary>힙에 들어 있는 항목 수(만료된 중복 포함).</summary>
    public int Count => heap.Count;

    /// <summary>추적 중인 세션 수.</summary>
    public int SessionCount => stamps.Count;

    /// <summary>세션의 다음 갱신 시각을 등록하거나 앞당긴다.</summary>
    /// <param name="session">세션.</param>
    /// <param name="dueTime">갱신할 시각(ms).</param>
    public void Schedule(KcpSession session, uint dueTime)
    {
        var stamp = nextStamp++;
        stamps[session] = stamp;
        Push(new Entry { DueTime = dueTime, Stamp = stamp, Session = session });
    }

    /// <summary>세션을 스케줄에서 제거한다(항목은 pop 될 때 자연히 버려진다).</summary>
    /// <param name="session">세션.</param>
    public void Remove(KcpSession session)
    {
        stamps.Remove(session);
    }

    /// <summary>
    /// 기한이 된 세션을 하나 꺼낸다.
    /// </summary>
    /// <param name="now">현재 시각(ms).</param>
    /// <param name="session">꺼낸 세션.</param>
    /// <returns>꺼낼 것이 있으면 true.</returns>
    public bool TryPopDue(uint now, out KcpSession session)
    {
        while (heap.Count > 0)
        {
            var top = heap[0];
            if (Kcp.Itimediff(now, top.DueTime) < 0)
                break;

            PopRoot();

            // lazy deletion: 최신 스탬프가 아니면 오래된 항목이다.
            if (stamps.TryGetValue(top.Session, out var current) && current == top.Stamp)
            {
                stamps.Remove(top.Session);
                session = top.Session;
                return true;
            }
        }

        session = null!;
        return false;
    }

    /// <summary>가장 이른 기한까지 남은 시간(ms). 없으면 <paramref name="fallback"/>.</summary>
    /// <param name="now">현재 시각.</param>
    /// <param name="fallback">힙이 비었을 때 반환할 값.</param>
    /// <returns>남은 밀리초(0 이상).</returns>
    public int TimeToNext(uint now, int fallback)
    {
        if (heap.Count == 0) return fallback;
        var diff = Kcp.Itimediff(heap[0].DueTime, now);
        return diff <= 0 ? 0 : Math.Min(diff, fallback);
    }

    /// <summary>전부 비운다.</summary>
    public void Clear()
    {
        heap.Clear();
        stamps.Clear();
    }

    private void Push(Entry entry)
    {
        heap.Add(entry);
        var i = heap.Count - 1;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (Kcp.Itimediff(heap[parent].DueTime, entry.DueTime) <= 0)
                break;
            heap[i] = heap[parent];
            i = parent;
        }
        heap[i] = entry;
    }

    private void PopRoot()
    {
        var last = heap[heap.Count - 1];
        heap.RemoveAt(heap.Count - 1);
        if (heap.Count == 0)
            return;

        var i = 0;
        while (true)
        {
            var left = (i << 1) + 1;
            if (left >= heap.Count) break;

            var right = left + 1;
            var child = right < heap.Count && Kcp.Itimediff(heap[right].DueTime, heap[left].DueTime) < 0
                ? right
                : left;

            if (Kcp.Itimediff(heap[child].DueTime, last.DueTime) >= 0)
                break;

            heap[i] = heap[child];
            i = child;
        }
        heap[i] = last;
    }
}
