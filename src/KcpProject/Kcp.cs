using System;
using System.Collections.Generic;

namespace KcpProject;

/// <summary>
/// KCP(신뢰성 UDP ARQ 프로토콜) 코어 상태 머신.
/// </summary>
/// <remarks>
/// <para>
/// 이 클래스는 <b>순수한 프로토콜 상태 머신</b>이다. 소켓·스레드·실시간 시계·로깅 구현을 알지 못하며
/// 전부 주입받는다. 실제 패킷 송신은 생성자에 넘긴 output 콜백이 담당한다.
/// </para>
/// <para>
/// <b>스레딩 규약</b>: 인스턴스 1개는 소유 스레드 1개에서만 사용한다. 내부에 lock 이 없으므로
/// 여러 스레드에서 동시에 호출하면 상태가 깨진다.
/// </para>
/// <para>
/// <b>사용 흐름</b>: 애플리케이션 데이터는 <see cref="Send(byte[], int, int)"/> 로 넣고,
/// 소켓에서 받은 UDP 페이로드는 <see cref="Input(byte[], int, int, bool, bool)"/> 로 넣는다.
/// 주기적으로 <see cref="Update"/> 를 호출하면 재전송·ACK 가 처리되며,
/// 도착한 애플리케이션 메시지는 <see cref="Recv(byte[], int, int)"/> 로 꺼낸다.
/// 다음 <see cref="Update"/> 호출 시각은 <see cref="Check"/> 로 알 수 있다.
/// </para>
/// <para>
/// 와이어 포맷(24바이트 헤더)은 원본 C kcp / kcp-go 와 바이트 단위로 호환된다.
/// </para>
/// </remarks>
public class Kcp
{
    /// <summary>nodelay 모드의 최소 RTO(ms).</summary>
    public const int IKCP_RTO_NDL = 30;

    /// <summary>일반 모드의 최소 RTO(ms).</summary>
    public const int IKCP_RTO_MIN = 100;

    /// <summary>기본 RTO(ms).</summary>
    public const int IKCP_RTO_DEF = 200;

    /// <summary>최대 RTO(ms).</summary>
    public const int IKCP_RTO_MAX = 60000;

    /// <summary>명령: 데이터 push.</summary>
    public const int IKCP_CMD_PUSH = 81;

    /// <summary>명령: 수신 확인(ACK).</summary>
    public const int IKCP_CMD_ACK = 82;

    /// <summary>명령: 윈도 크기 질의(window probe).</summary>
    public const int IKCP_CMD_WASK = 83;

    /// <summary>명령: 윈도 크기 통지(window tell).</summary>
    public const int IKCP_CMD_WINS = 84;

    /// <summary>플래그: WASK 를 보내야 함.</summary>
    public const int IKCP_ASK_SEND = 1;

    /// <summary>플래그: WINS 를 보내야 함.</summary>
    public const int IKCP_ASK_TELL = 2;

    /// <summary>기본 송신 윈도(세그먼트 수).</summary>
    public const int IKCP_WND_SND = 32;

    /// <summary>기본 수신 윈도(세그먼트 수).</summary>
    public const int IKCP_WND_RCV = 32;

    /// <summary>기본 MTU(바이트).</summary>
    public const int IKCP_MTU_DEF = 1400;

    /// <summary>빠른 재전송 기본 임계값.</summary>
    public const int IKCP_ACK_FAST = 3;

    /// <summary>기본 내부 갱신 간격(ms).</summary>
    public const int IKCP_INTERVAL = 100;

    /// <summary>KCP 헤더 크기(바이트).</summary>
    public const int IKCP_OVERHEAD = 24;

    /// <summary>데드링크 판정 재전송 횟수.</summary>
    public const int IKCP_DEADLINK = 20;

    /// <summary>혼잡 윈도 임계값 초기치.</summary>
    public const int IKCP_THRESH_INIT = 2;

    /// <summary>혼잡 윈도 임계값 최솟값.</summary>
    public const int IKCP_THRESH_MIN = 2;

    /// <summary>윈도 프로브 초기 대기(ms).</summary>
    public const int IKCP_PROBE_INIT = 7000;

    /// <summary>윈도 프로브 최대 대기(ms).</summary>
    public const int IKCP_PROBE_LIMIT = 120000;

    /// <summary>헤더에서 sn 필드의 바이트 오프셋.</summary>
    public const int IKCP_SN_OFFSET = 12;

    /// <summary>데드링크 상태를 나타내는 <see cref="State"/> 값.</summary>
    public const uint IKCP_STATE_DEAD_LINK = 0xFFFFFFFF;

    /// <summary>부호 없는 8비트 정수를 기록한다.</summary>
    /// <param name="p">대상 버퍼.</param>
    /// <param name="offset">기록 위치.</param>
    /// <param name="c">값.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int ikcp_encode8u(byte[] p, int offset, byte c)
    {
        p[offset] = c;
        return 1;
    }

    /// <summary>부호 없는 8비트 정수를 읽는다.</summary>
    /// <param name="p">원본 버퍼.</param>
    /// <param name="offset">읽기 위치.</param>
    /// <param name="c">읽은 값.</param>
    /// <returns>읽은 바이트 수.</returns>
    public static int ikcp_decode8u(byte[] p, int offset, ref byte c)
    {
        c = p[offset];
        return 1;
    }

    /// <summary>부호 없는 16비트 정수를 리틀엔디언으로 기록한다.</summary>
    /// <param name="p">대상 버퍼.</param>
    /// <param name="offset">기록 위치.</param>
    /// <param name="w">값.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int ikcp_encode16u(byte[] p, int offset, ushort w)
    {
        p[offset] = (byte)(w >> 0);
        p[offset + 1] = (byte)(w >> 8);
        return 2;
    }

    /// <summary>부호 없는 16비트 정수를 리틀엔디언으로 읽는다.</summary>
    /// <param name="p">원본 버퍼.</param>
    /// <param name="offset">읽기 위치.</param>
    /// <param name="c">읽은 값.</param>
    /// <returns>읽은 바이트 수.</returns>
    public static int ikcp_decode16u(byte[] p, int offset, ref ushort c)
    {
        ushort result = 0;
        result |= p[offset];
        result |= (ushort)(p[offset + 1] << 8);
        c = result;
        return 2;
    }

    /// <summary>부호 없는 32비트 정수를 리틀엔디언으로 기록한다.</summary>
    /// <param name="p">대상 버퍼.</param>
    /// <param name="offset">기록 위치.</param>
    /// <param name="l">값.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int ikcp_encode32u(byte[] p, int offset, uint l)
    {
        p[offset] = (byte)(l >> 0);
        p[offset + 1] = (byte)(l >> 8);
        p[offset + 2] = (byte)(l >> 16);
        p[offset + 3] = (byte)(l >> 24);
        return 4;
    }

    /// <summary>부호 없는 32비트 정수를 리틀엔디언으로 읽는다.</summary>
    /// <param name="p">원본 버퍼.</param>
    /// <param name="offset">읽기 위치.</param>
    /// <param name="c">읽은 값.</param>
    /// <returns>읽은 바이트 수.</returns>
    public static int ikcp_decode32u(byte[] p, int offset, ref uint c)
    {
        uint result = 0;
        result |= p[offset];
        result |= (uint)(p[offset + 1] << 8);
        result |= (uint)(p[offset + 2] << 16);
        result |= (uint)(p[offset + 3] << 24);
        c = result;
        return 4;
    }

    private static uint _imin_(uint a, uint b) => a <= b ? a : b;

    private static uint _imax_(uint a, uint b) => a >= b ? a : b;

    private static uint _ibound_(uint lower, uint middle, uint upper) => _imin_(_imax_(lower, middle), upper);

    /// <summary>
    /// 32비트 시각의 부호 있는 차이. 랩어라운드(약 49.7일)를 넘어서도 안전하게 비교하려면
    /// 반드시 이 함수를 써야 한다(진단 B-02).
    /// </summary>
    internal static int _itimediff(uint later, uint earlier) => (int)(later - earlier);

    /// <summary>KCP 세그먼트(헤더 + 페이로드).</summary>
    internal sealed class Segment
    {
        internal uint conv;
        internal uint cmd;
        internal uint frg;
        internal uint wnd;
        internal uint ts;
        internal uint sn;
        internal uint una;
        internal uint rto;
        internal uint xmit;
        internal uint resendts;
        internal uint fastack;
        internal uint acked;
        internal ByteBuffer data;

        /// <summary>세그먼트 풀 상한. 초과분은 버려 GC 대상으로 둔다(진단 B-08).</summary>
        internal const int MaxPoolSize = 4096;

        private static readonly Stack<Segment> msSegmentPool = new Stack<Segment>(32);

        internal static Segment Get(int size)
        {
            lock (msSegmentPool)
            {
                if (msSegmentPool.Count > 0)
                {
                    var seg = msSegmentPool.Pop();
                    seg.data = ByteBuffer.Allocate(size, true);
                    return seg;
                }
            }
            return new Segment(size);
        }

        internal static void Put(Segment seg)
        {
            seg.reset();
            lock (msSegmentPool)
            {
                if (msSegmentPool.Count < MaxPoolSize)
                    msSegmentPool.Push(seg);
            }
        }

        /// <summary>테스트 전용: 현재 풀에 보관 중인 세그먼트 수.</summary>
        internal static int PoolCount
        {
            get { lock (msSegmentPool) { return msSegmentPool.Count; } }
        }

        private Segment(int size)
        {
            data = ByteBuffer.Allocate(size, true);
        }

        /// <summary>세그먼트 헤더 24바이트를 버퍼에 기록하고 기록한 바이트 수를 반환한다.</summary>
        internal int encode(byte[] ptr, int offset)
        {
            var offset_ = offset;

            offset += ikcp_encode32u(ptr, offset, conv);
            offset += ikcp_encode8u(ptr, offset, (byte)cmd);
            offset += ikcp_encode8u(ptr, offset, (byte)frg);
            offset += ikcp_encode16u(ptr, offset, (ushort)wnd);
            offset += ikcp_encode32u(ptr, offset, ts);
            offset += ikcp_encode32u(ptr, offset, sn);
            offset += ikcp_encode32u(ptr, offset, una);
            offset += ikcp_encode32u(ptr, offset, (uint)data.ReadableBytes);

            return offset - offset_;
        }

        internal void reset()
        {
            conv = 0;
            cmd = 0;
            frg = 0;
            wnd = 0;
            ts = 0;
            sn = 0;
            una = 0;
            rto = 0;
            xmit = 0;
            resendts = 0;
            fastack = 0;
            acked = 0;

            data.Clear();
            data.Dispose();
            data = null!;
        }
    }

    internal struct ackItem
    {
        internal uint sn;
        internal uint ts;
    }

    // ---- KCP 상태 ----
    private uint conv;
    private uint mtu;
    private uint mss;
    private uint state;
    private uint snd_una;
    private uint snd_nxt;
    private uint rcv_nxt;
    private uint ssthresh;
    private int rx_rttval;
    private int rx_srtt;
    private uint rx_rto;
    private uint rx_minrto;
    private uint snd_wnd;
    private uint rcv_wnd;
    private uint rmt_wnd;
    private uint cwnd;
    private uint probe;
    private uint interval;
    private uint ts_flush;
    private uint nodelay;
    private uint updated;
    private uint ts_probe;
    private uint probe_wait;
    private uint dead_link;
    private uint incr;

    private int fastresend;
    private int nocwnd;
    private int stream;

    private readonly List<Segment> snd_queue = new List<Segment>(16);
    private readonly List<Segment> rcv_queue = new List<Segment>(16);
    private readonly List<Segment> snd_buf = new List<Segment>(16);
    private readonly List<Segment> rcv_buf = new List<Segment>(16);

    private readonly List<ackItem> acklist = new List<ackItem>(16);

    private byte[] buffer;
    private int reserved;
    private readonly Action<byte[], int> output;

    /// <summary>주입된 단조 밀리초 시계(진단 B-01, PF-01).</summary>
    private readonly Func<uint> clock;

    // ---- 통계(진단 PF-07) ----
    private KcpStats stats;

    /// <summary>송신 윈도 크기(세그먼트 수).</summary>
    public uint SndWnd => snd_wnd;

    /// <summary>수신 윈도 크기(세그먼트 수).</summary>
    public uint RcvWnd => rcv_wnd;

    /// <summary>상대가 통지한 수신 윈도 크기.</summary>
    public uint RmtWnd => rmt_wnd;

    /// <summary>세그먼트 1개에 담을 수 있는 최대 페이로드(MTU - 헤더 - 예약영역).</summary>
    public uint Mss => mss;

    /// <summary>이 연결의 대화 번호(conversation id). 양 끝이 같아야 한다.</summary>
    public uint Conv => conv;

    /// <summary>평활 RTT(ms). 게임의 핑 표시에 그대로 쓸 수 있다.</summary>
    public int Srtt => rx_srtt;

    /// <summary>RTT 편차(ms).</summary>
    public int RttVar => rx_rttval;

    /// <summary>현재 재전송 타임아웃(ms).</summary>
    public uint Rto => rx_rto;

    /// <summary>내부 갱신 간격(ms).</summary>
    public uint Interval => interval;

    /// <summary>내부 상태 값. 데드링크에서 <see cref="IKCP_STATE_DEAD_LINK"/> 가 된다.</summary>
    public uint State => state;

    /// <summary>
    /// 같은 세그먼트를 <see cref="IKCP_DEADLINK"/> 회 재전송하고도 확인받지 못했는지 여부(진단 B-03).
    /// true 가 되면 상대가 사라진 것으로 보고 연결을 정리해야 한다.
    /// </summary>
    public bool IsDeadLink => state == IKCP_STATE_DEAD_LINK;

    /// <summary>아직 상대에게 확인되지 않은 대기 세그먼트 수(송신 버퍼 + 송신 큐).</summary>
    public int WaitSnd => snd_buf.Count + snd_queue.Count;

    /// <summary>주입된 시계가 보고하는 현재 밀리초 시각.</summary>
    public uint CurrentMS => clock();

    private Action<string>? writelog;

    /// <summary>로그 마스크: output 호출.</summary>
    public const int IKCP_LOG_OUTPUT = 1;

    /// <summary>로그 마스크: input 호출.</summary>
    public const int IKCP_LOG_INPUT = 2;

    /// <summary>로그 마스크: send 호출.</summary>
    public const int IKCP_LOG_SEND = 4;

    /// <summary>로그 마스크: recv 호출.</summary>
    public const int IKCP_LOG_RECV = 8;

    /// <summary>로그 마스크: 데이터 수신.</summary>
    public const int IKCP_LOG_IN_DATA = 16;

    /// <summary>로그 마스크: ACK 수신.</summary>
    public const int IKCP_LOG_IN_ACK = 32;

    /// <summary>로그 마스크: 윈도 프로브 수신.</summary>
    public const int IKCP_LOG_IN_PROBE = 64;

    /// <summary>로그 마스크: 윈도 통지 수신.</summary>
    public const int IKCP_LOG_IN_WINS = 128;

    /// <summary>로그 마스크: 데이터 송신.</summary>
    public const int IKCP_LOG_OUT_DATA = 256;

    /// <summary>로그 마스크: ACK 송신.</summary>
    public const int IKCP_LOG_OUT_ACK = 512;

    /// <summary>로그 마스크: 윈도 프로브 송신.</summary>
    public const int IKCP_LOG_OUT_PROBE = 1024;

    /// <summary>로그 마스크: 윈도 통지 송신.</summary>
    public const int IKCP_LOG_OUT_WINS = 2048;

    /// <summary>현재 로그 마스크.</summary>
    public int logmask;

    /// <summary>새 KCP 제어 객체를 만든다.</summary>
    /// <param name="conv_">대화 번호. 같은 연결의 두 엔드포인트에서 동일해야 한다.</param>
    /// <param name="output_">
    /// 하위 계층(보통 UDP 소켓)으로 패킷을 내보내는 콜백. (버퍼, 길이) 를 받으며,
    /// 버퍼는 호출 이후 재사용되므로 콜백 안에서 즉시 송신하거나 복사해야 한다.
    /// </param>
    /// <param name="currentTimeMs">
    /// 밀리초 시계. 생략하면 <see cref="KcpClock.NowMs"/>(단조 시계)를 사용한다.
    /// 테스트는 여기에 가상 시계를 주입해 실제 대기 없이 시나리오를 재현한다.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="output_"/> 가 null 인 경우.</exception>
    public Kcp(uint conv_, Action<byte[], int> output_, Func<uint>? currentTimeMs = null)
    {
        conv = conv_;
        snd_wnd = IKCP_WND_SND;
        rcv_wnd = IKCP_WND_RCV;
        rmt_wnd = IKCP_WND_RCV;
        mtu = IKCP_MTU_DEF;
        mss = mtu - IKCP_OVERHEAD;
        rx_rto = IKCP_RTO_DEF;
        rx_minrto = IKCP_RTO_MIN;
        interval = IKCP_INTERVAL;
        ts_flush = IKCP_INTERVAL;
        ssthresh = IKCP_THRESH_INIT;
        dead_link = IKCP_DEADLINK;
        buffer = new byte[mtu];
        output = output_ ?? throw new ArgumentNullException(nameof(output_));
        clock = currentTimeMs ?? (() => KcpClock.NowMs);
    }

    /// <summary>수신 대기열에 있는 다음 메시지의 크기를 확인한다.</summary>
    /// <returns>다음 메시지의 바이트 수. 완성된 메시지가 없으면 -1.</returns>
    public int PeekSize()
    {
        if (0 == rcv_queue.Count) return -1;

        var seq = rcv_queue[0];

        if (0 == seq.frg) return seq.data.ReadableBytes;

        if (rcv_queue.Count < seq.frg + 1) return -1;

        int length = 0;

        foreach (var item in rcv_queue)
        {
            length += item.data.ReadableBytes;
            if (0 == item.frg)
                break;
        }

        return length;
    }

    /// <summary>수신 대기열에서 메시지 하나를 꺼낸다.</summary>
    /// <param name="buffer">대상 버퍼.</param>
    /// <returns>읽은 바이트 수. 읽을 데이터가 없으면 -1, 버퍼가 작으면 -2.</returns>
    public int Recv(byte[] buffer)
    {
        return Recv(buffer, 0, buffer.Length);
    }

    /// <summary>수신 대기열에서 메시지 하나를 꺼낸다.</summary>
    /// <param name="buffer">대상 버퍼.</param>
    /// <param name="index">기록 시작 위치.</param>
    /// <param name="length">기록 가능한 최대 바이트 수.</param>
    /// <returns>
    /// 읽은 바이트 수. 읽을 수 있는 데이터가 없으면 -1,
    /// <paramref name="length"/> 가 <see cref="PeekSize"/> 보다 작으면 -2.
    /// </returns>
    public int Recv(byte[] buffer, int index, int length)
    {
        var peekSize = PeekSize();
        if (peekSize < 0)
            return -1;

        if (peekSize > length)
            return -2;

        var fast_recover = false;
        if (rcv_queue.Count >= rcv_wnd)
            fast_recover = true;

        // 조각을 이어 붙인다.
        var count = 0;
        var n = index;
        foreach (var seg in rcv_queue)
        {
            Buffer.BlockCopy(seg.data.RawBuffer, seg.data.ReaderIndex, buffer, n, seg.data.ReadableBytes);
            n += seg.data.ReadableBytes;

            count++;
            var fragment = seg.frg;

            if (ikcp_canlog(IKCP_LOG_RECV))
            {
                ikcp_log($"recv sn={seg.sn.ToString()}");
            }

            Segment.Put(seg);
            if (0 == fragment) break;
        }

        if (count > 0)
        {
            rcv_queue.RemoveRange(0, count);
        }

        // rcv_buf -> rcv_queue 로 이어지는 데이터를 옮긴다.
        count = 0;
        foreach (var seg in rcv_buf)
        {
            if (seg.sn == rcv_nxt && rcv_queue.Count + count < rcv_wnd)
            {
                rcv_queue.Add(seg);
                rcv_nxt++;
                count++;
            }
            else
            {
                break;
            }
        }

        if (count > 0)
        {
            rcv_buf.RemoveRange(0, count);
        }

        // fast recover: 윈도가 다시 열렸음을 상대에게 알린다.
        if (rcv_queue.Count < rcv_wnd && fast_recover)
        {
            probe |= IKCP_ASK_TELL;
        }

        return n - index;
    }

    /// <summary>애플리케이션 데이터를 송신 큐에 넣는다.</summary>
    /// <param name="buffer">보낼 데이터.</param>
    /// <returns>0 이면 성공. 음수는 오류.</returns>
    public int Send(byte[] buffer)
    {
        return Send(buffer, 0, buffer.Length);
    }

    /// <summary>애플리케이션 데이터를 송신 큐에 넣는다.</summary>
    /// <param name="buffer">보낼 데이터.</param>
    /// <param name="index">시작 위치.</param>
    /// <param name="length">길이.</param>
    /// <returns>
    /// <list type="table">
    /// <item><term>0</term><description>성공(송신 큐에 적재).</description></item>
    /// <item><term>-1</term><description><paramref name="length"/> 가 0.</description></item>
    /// <item><term>-2</term><description>
    /// 조각 수가 수신 윈도 이상이라 상대가 절대 재조립할 수 없다. 메시지 모드에서 보낼 수 있는
    /// 최대 메시지 크기는 <c>(RcvWnd - 1) × Mss</c> 이다(진단 B-04).
    /// </description></item>
    /// </list>
    /// </returns>
    public int Send(byte[] buffer, int index, int length)
    {
        if (0 == length) return -1;

        if (stream != 0)
        {
            var n = snd_queue.Count;
            if (n > 0)
            {
                var seg = snd_queue[n - 1];
                if (seg.data.ReadableBytes < mss)
                {
                    var capacity = (int)(mss - seg.data.ReadableBytes);
                    var writen = Math.Min(capacity, length);
                    seg.data.WriteBytes(buffer, index, writen);
                    index += writen;
                    length -= writen;
                }
            }
        }

        if (length == 0)
            return 0;

        var count = length <= mss ? 1 : (int)((length + mss - 1) / mss);

        // 원본 ikcp.c 와 동일한 검사. 조각 수가 수신 윈도 이상이면 수신측 rcv_queue 가
        // 절대 채워지지 못해 영구 교착이 된다(진단 B-04).
        if (count >= rcv_wnd) return -2;

        if (count == 0) count = 1;

        for (var i = 0; i < count; i++)
        {
            var size = Math.Min(length, (int)mss);

            var seg = Segment.Get(size);
            seg.data.WriteBytes(buffer, index, size);
            index += size;
            length -= size;

            seg.frg = stream == 0 ? (byte)(count - i - 1) : (byte)0;
            snd_queue.Add(seg);
        }

        return 0;
    }

    /// <summary>RTT 샘플로 srtt/rttval/rto 를 갱신한다(RFC 6298).</summary>
    private void update_ack(int rtt)
    {
        if (0 == rx_srtt)
        {
            rx_srtt = rtt;
            rx_rttval = rtt >> 1;
        }
        else
        {
            int delta = rtt - rx_srtt;
            rx_srtt += delta >> 3;
            if (0 > delta) delta = -delta;

            if (rtt < rx_srtt - rx_rttval)
            {
                // 새 RTT 샘플이 예상 범위의 하단보다 낮으면 가중치를 8배 낮춘다.
                rx_rttval += (delta - rx_rttval) >> 5;
            }
            else
            {
                rx_rttval += (delta - rx_rttval) >> 2;
            }
        }

        uint rto = (uint)rx_srtt + _imax_(interval, (uint)rx_rttval << 2);
        rx_rto = _ibound_(rx_minrto, rto, IKCP_RTO_MAX);
    }

    private void shrink_buf()
    {
        snd_una = snd_buf.Count > 0 ? snd_buf[0].sn : snd_nxt;
    }

    private void parse_ack(uint sn)
    {
        if (_itimediff(sn, snd_una) < 0 || _itimediff(sn, snd_nxt) >= 0) return;

        foreach (var seg in snd_buf)
        {
            if (sn == seg.sn)
            {
                // 확인 표시만 하고 세그먼트는 남겨 둔다. una 가 지울 때까지 기다린다.
                seg.acked = 1;
                break;
            }
            if (_itimediff(sn, seg.sn) < 0)
                break;
        }
    }

    private void parse_fastack(uint sn, uint ts)
    {
        if (_itimediff(sn, snd_una) < 0 || _itimediff(sn, snd_nxt) >= 0)
            return;

        foreach (var seg in snd_buf)
        {
            if (_itimediff(sn, seg.sn) < 0)
                break;
            else if (sn != seg.sn && _itimediff(seg.ts, ts) <= 0)
                seg.fastack++;
        }
    }

    private int parse_una(uint una)
    {
        var count = 0;
        foreach (var seg in snd_buf)
        {
            if (_itimediff(una, seg.sn) > 0)
            {
                count++;
                Segment.Put(seg);
            }
            else
                break;
        }

        if (count > 0)
            snd_buf.RemoveRange(0, count);
        return count;
    }

    private void ack_push(uint sn, uint ts)
    {
        acklist.Add(new ackItem { sn = sn, ts = ts });
    }

    /// <summary>수신 세그먼트를 rcv_buf 에 순서대로 넣는다. 중복이면 true 를 반환한다.</summary>
    private bool parse_data(Segment newseg)
    {
        var sn = newseg.sn;
        if (_itimediff(sn, rcv_nxt + rcv_wnd) >= 0 || _itimediff(sn, rcv_nxt) < 0)
            return true;

        var n = rcv_buf.Count - 1;
        var insert_idx = 0;
        var repeat = false;
        for (var i = n; i >= 0; i--)
        {
            var seg = rcv_buf[i];
            if (seg.sn == sn)
            {
                repeat = true;
                break;
            }

            if (_itimediff(sn, seg.sn) > 0)
            {
                insert_idx = i + 1;
                break;
            }
        }

        if (!repeat)
        {
            if (insert_idx == n + 1)
                rcv_buf.Add(newseg);
            else
                rcv_buf.Insert(insert_idx, newseg);
        }

        // rcv_buf -> rcv_queue
        var count = 0;
        foreach (var seg in rcv_buf)
        {
            if (seg.sn == rcv_nxt && rcv_queue.Count + count < rcv_wnd)
            {
                rcv_nxt++;
                count++;
            }
            else
            {
                break;
            }
        }

        if (count > 0)
        {
            for (var i = 0; i < count; i++)
                rcv_queue.Add(rcv_buf[i]);
            rcv_buf.RemoveRange(0, count);
        }
        return repeat;
    }

    /// <summary>하위 계층에서 받은 패킷을 입력한다.</summary>
    /// <param name="data">패킷 버퍼.</param>
    /// <param name="index">시작 위치.</param>
    /// <param name="size">길이.</param>
    /// <param name="regular">FEC 복원 패킷이 아닌 실제 수신 패킷이면 true. 윈도·RTT 갱신에 쓰인다.</param>
    /// <param name="ackNoDelay">true 면 ACK 를 즉시 내보낸다. 지연은 줄지만 대역폭 효율은 떨어진다.</param>
    /// <returns>
    /// <list type="table">
    /// <item><term>0</term><description>정상 처리.</description></item>
    /// <item><term>-1</term><description>패킷이 헤더보다 짧거나 conv 가 다르다.</description></item>
    /// <item><term>-2</term><description>헤더의 length 필드가 실제 남은 바이트보다 크다(잘린 패킷).</description></item>
    /// <item><term>-3</term><description>알 수 없는 cmd 값이다.</description></item>
    /// </list>
    /// 오류를 반환해도 <b>그 앞까지 해석한 세그먼트의 상태 변경은 이미 적용된 상태</b>다.
    /// 원본 ikcp.c 와 같은 동작이며, 상호운용 검증을 위해 그대로 유지한다(진단 S-06).
    /// </returns>
    public int Input(byte[] data, int index, int size, bool regular, bool ackNoDelay)
    {
        var s_una = snd_una;
        if (size < IKCP_OVERHEAD) return -1;

        int offset = index;
        uint latest = 0;
        int flag = 0;
        ulong inSegs = 0;
        bool windowSlides = false;

        // 핫루프 밖에서 시계를 1회만 읽는다(진단 PF-01).
        uint current = clock();

        stats.BytesReceived += (ulong)size;

        if (ikcp_canlog(IKCP_LOG_INPUT))
        {
            ikcp_log($"[RI] {size.ToString()} bytes");
        }

        while (true)
        {
            uint ts = 0;
            uint sn = 0;
            uint length = 0;
            uint una = 0;
            uint conv_ = 0;

            ushort wnd = 0;
            byte cmd = 0;
            byte frg = 0;

            if (size - (offset - index) < IKCP_OVERHEAD) break;

            offset += ikcp_decode32u(data, offset, ref conv_);

            if (conv != conv_) return -1;

            offset += ikcp_decode8u(data, offset, ref cmd);
            offset += ikcp_decode8u(data, offset, ref frg);
            offset += ikcp_decode16u(data, offset, ref wnd);
            offset += ikcp_decode32u(data, offset, ref ts);
            offset += ikcp_decode32u(data, offset, ref sn);
            offset += ikcp_decode32u(data, offset, ref una);
            offset += ikcp_decode32u(data, offset, ref length);

            if (size - (offset - index) < length) return -2;

            switch (cmd)
            {
                case IKCP_CMD_PUSH:
                case IKCP_CMD_ACK:
                case IKCP_CMD_WASK:
                case IKCP_CMD_WINS:
                    break;
                default:
                    return -3;
            }

            // 윈도 갱신은 실제 수신 패킷에서만 신뢰한다.
            if (regular)
            {
                rmt_wnd = wnd;
            }

            if (parse_una(una) > 0)
            {
                windowSlides = true;
            }

            shrink_buf();

            if (IKCP_CMD_ACK == cmd)
            {
                parse_ack(sn);
                parse_fastack(sn, ts);
                flag |= 1;
                latest = ts;

                if (ikcp_canlog(IKCP_LOG_IN_ACK))
                {
                    ikcp_log($" input ack: sn={sn.ToString()} ts={ts.ToString()} rtt={_itimediff(current, ts).ToString()} rto={rx_rto.ToString()}");
                }
            }
            else if (IKCP_CMD_PUSH == cmd)
            {
                if (ikcp_canlog(IKCP_LOG_IN_DATA))
                {
                    ikcp_log($" input psh: sn={sn.ToString()} ts={ts.ToString()}");
                }

                if (_itimediff(sn, rcv_nxt + rcv_wnd) < 0)
                {
                    ack_push(sn, ts);
                    if (_itimediff(sn, rcv_nxt) >= 0)
                    {
                        var seg = Segment.Get((int)length);
                        seg.conv = conv_;
                        seg.cmd = cmd;
                        seg.frg = frg;
                        seg.wnd = wnd;
                        seg.ts = ts;
                        seg.sn = sn;
                        seg.una = una;
                        seg.data.WriteBytes(data, offset, (int)length);
                        if (parse_data(seg))
                        {
                            // 이미 rcv_buf 에 있는 sn 이다. 새로 만든 세그먼트는 버린다.
                            stats.RepeatSegments++;
                            Segment.Put(seg);
                        }
                    }
                    else
                    {
                        stats.RepeatSegments++;
                    }
                }
                else
                {
                    stats.RepeatSegments++;
                }
            }
            else if (IKCP_CMD_WASK == cmd)
            {
                // Flush 에서 WINS 로 내 윈도 크기를 알려 준다.
                probe |= IKCP_ASK_TELL;

                if (ikcp_canlog(IKCP_LOG_IN_PROBE))
                {
                    ikcp_log(" input probe");
                }
            }
            else if (IKCP_CMD_WINS == cmd)
            {
                if (ikcp_canlog(IKCP_LOG_IN_WINS))
                {
                    ikcp_log($" input wins: {wnd.ToString()}");
                }
            }
            else
            {
                return -3;
            }

            inSegs++;
            offset += (int)length;
        }

        stats.SegmentsReceived += inSegs;

        // 가장 최근 ts 로 RTT 를 갱신한다(FEC 패킷은 제외).
        if (flag != 0 && regular)
        {
            if (_itimediff(current, latest) >= 0)
            {
                update_ack(_itimediff(current, latest));
            }
        }

        // 패킷 도착에 따른 혼잡 윈도 갱신
        if (nocwnd == 0)
        {
            if (_itimediff(snd_una, s_una) > 0)
            {
                if (cwnd < rmt_wnd)
                {
                    var _mss = mss;
                    if (cwnd < ssthresh)
                    {
                        cwnd++;
                        incr += _mss;
                    }
                    else
                    {
                        if (incr < _mss)
                        {
                            incr = _mss;
                        }
                        incr += _mss * _mss / incr + _mss / 16;
                        if ((cwnd + 1) * _mss <= incr)
                        {
                            cwnd = _mss > 0 ? (incr + _mss - 1) / _mss : incr + _mss - 1;
                        }
                    }
                    if (cwnd > rmt_wnd)
                    {
                        cwnd = rmt_wnd;
                        incr = rmt_wnd * _mss;
                    }
                }
            }
        }

        if (windowSlides)
        {
            Flush(false);
        }
        else if (ackNoDelay && acklist.Count > 0)
        {
            Flush(true);
        }

        return 0;
    }

    private ushort wnd_unused()
    {
        if (rcv_queue.Count < rcv_wnd)
            return (ushort)(rcv_wnd - rcv_queue.Count);
        return 0;
    }

    private int makeSpace(int space, int writeIndex)
    {
        if (writeIndex + space > mtu)
        {
            if (ikcp_canlog(IKCP_LOG_OUTPUT))
            {
                ikcp_log($"[RO] {writeIndex.ToString()} bytes");
            }
            output(buffer, writeIndex);
            stats.BytesSent += (ulong)writeIndex;
            writeIndex = reserved;
        }
        return writeIndex;
    }

    private void flushBuffer(int writeIndex)
    {
        if (writeIndex > reserved)
        {
            if (ikcp_canlog(IKCP_LOG_OUTPUT))
            {
                ikcp_log($"[RO] {writeIndex.ToString()} bytes");
            }
            output(buffer, writeIndex);
            stats.BytesSent += (ulong)writeIndex;
        }
    }

    /// <summary>보류 중인 ACK·데이터·프로브를 실제로 내보낸다.</summary>
    /// <param name="ackOnly">true 면 ACK 만 내보내고 즉시 반환한다.</param>
    /// <returns>다음 재전송까지 남은 최소 시간(ms).</returns>
    public uint Flush(bool ackOnly)
    {
        var seg = Segment.Get(32);
        seg.conv = conv;
        seg.cmd = IKCP_CMD_ACK;
        seg.wnd = wnd_unused();
        seg.una = rcv_nxt;

        var writeIndex = reserved;

        // ACK 내보내기
        for (var i = 0; i < acklist.Count; i++)
        {
            writeIndex = makeSpace(IKCP_OVERHEAD, writeIndex);
            var ack = acklist[i];
            if (_itimediff(ack.sn, rcv_nxt) >= 0 || acklist.Count - 1 == i)
            {
                seg.sn = ack.sn;
                seg.ts = ack.ts;
                writeIndex += seg.encode(buffer, writeIndex);
                stats.SegmentsSent++;

                if (ikcp_canlog(IKCP_LOG_OUT_ACK))
                {
                    ikcp_log($"output ack: sn={seg.sn.ToString()}");
                }
            }
        }
        acklist.Clear();

        if (ackOnly)
        {
            flushBuffer(writeIndex);
            Segment.Put(seg);
            return interval;
        }

        // 진입 시 1회만 시계를 읽는다(진단 PF-01).
        uint current = clock();

        // 상대 윈도가 0 이면 윈도 크기를 물어본다.
        if (0 == rmt_wnd)
        {
            if (0 == probe_wait)
            {
                probe_wait = IKCP_PROBE_INIT;
                ts_probe = current + probe_wait;
            }
            else
            {
                if (_itimediff(current, ts_probe) >= 0)
                {
                    if (probe_wait < IKCP_PROBE_INIT)
                        probe_wait = IKCP_PROBE_INIT;
                    probe_wait += probe_wait / 2;
                    if (probe_wait > IKCP_PROBE_LIMIT)
                        probe_wait = IKCP_PROBE_LIMIT;
                    ts_probe = current + probe_wait;
                    probe |= IKCP_ASK_SEND;
                }
            }
        }
        else
        {
            ts_probe = 0;
            probe_wait = 0;
        }

        if ((probe & IKCP_ASK_SEND) != 0)
        {
            seg.cmd = IKCP_CMD_WASK;
            writeIndex = makeSpace(IKCP_OVERHEAD, writeIndex);
            writeIndex += seg.encode(buffer, writeIndex);
            stats.SegmentsSent++;
        }

        if ((probe & IKCP_ASK_TELL) != 0)
        {
            seg.cmd = IKCP_CMD_WINS;
            writeIndex = makeSpace(IKCP_OVERHEAD, writeIndex);
            writeIndex += seg.encode(buffer, writeIndex);
            stats.SegmentsSent++;
        }

        probe = 0;

        // 윈도 크기 계산
        var cwnd_ = _imin_(snd_wnd, rmt_wnd);
        if (0 == nocwnd)
            cwnd_ = _imin_(cwnd, cwnd_);

        // snd_queue -> snd_buf (슬라이딩 윈도)
        var newSegsCount = 0;
        for (var k = 0; k < snd_queue.Count; k++)
        {
            if (_itimediff(snd_nxt, snd_una + cwnd_) >= 0)
                break;

            var newseg = snd_queue[k];
            newseg.conv = conv;
            newseg.cmd = IKCP_CMD_PUSH;
            newseg.sn = snd_nxt;
            snd_buf.Add(newseg);
            snd_nxt++;
            newSegsCount++;
        }

        if (newSegsCount > 0)
        {
            snd_queue.RemoveRange(0, newSegsCount);
        }

        // 빠른 재전송 임계값
        var resent = (uint)fastresend;
        if (fastresend <= 0) resent = 0xffffffff;

        ulong change = 0;
        ulong lostSegs = 0;
        ulong fastRetransSegs = 0;
        ulong earlyRetransSegs = 0;
        var minrto = (int)interval;

        for (var k = 0; k < snd_buf.Count; k++)
        {
            var segment = snd_buf[k];
            var needsend = false;
            if (segment.acked == 1)
                continue;
            if (segment.xmit == 0)  // 최초 전송
            {
                needsend = true;
                segment.rto = rx_rto;
                segment.resendts = current + segment.rto;
            }
            else if (segment.fastack >= resent) // 빠른 재전송
            {
                needsend = true;
                segment.fastack = 0;
                segment.rto = rx_rto;
                segment.resendts = current + segment.rto;
                change++;
                fastRetransSegs++;
            }
            else if (segment.fastack > 0 && newSegsCount == 0) // 조기 재전송
            {
                needsend = true;
                segment.fastack = 0;
                segment.rto = rx_rto;
                segment.resendts = current + segment.rto;
                change++;
                earlyRetransSegs++;
            }
            else if (_itimediff(current, segment.resendts) >= 0) // RTO 만료
            {
                needsend = true;
                if (nodelay == 0)
                    segment.rto += rx_rto;
                else
                    segment.rto += rx_rto / 2;
                segment.fastack = 0;
                segment.resendts = current + segment.rto;
                lostSegs++;
            }

            if (needsend)
            {
                segment.xmit++;
                segment.ts = current;
                segment.wnd = seg.wnd;
                segment.una = seg.una;

                var need = IKCP_OVERHEAD + segment.data.ReadableBytes;
                writeIndex = makeSpace(need, writeIndex);
                writeIndex += segment.encode(buffer, writeIndex);
                Buffer.BlockCopy(segment.data.RawBuffer, segment.data.ReaderIndex, buffer, writeIndex, segment.data.ReadableBytes);
                writeIndex += segment.data.ReadableBytes;
                stats.SegmentsSent++;

                if (segment.xmit >= dead_link)
                {
                    state = IKCP_STATE_DEAD_LINK;
                }

                if (ikcp_canlog(IKCP_LOG_OUT_DATA))
                {
                    ikcp_log($"output psh: sn={segment.sn.ToString()} ts={segment.ts.ToString()} resendts={segment.resendts.ToString()} rto={segment.rto.ToString()} fastack={segment.fastack.ToString()}, xmit={segment.xmit.ToString()}");
                }
            }

            // 가장 가까운 재전송 시각
            var _rto = _itimediff(segment.resendts, current);
            if (_rto > 0 && _rto < minrto)
            {
                minrto = _rto;
            }
        }

        flushBuffer(writeIndex);

        stats.RetransmitSegments += lostSegs;
        stats.FastRetransmitSegments += fastRetransSegs;
        stats.EarlyRetransmitSegments += earlyRetransSegs;

        // 혼잡 윈도 갱신
        if (nocwnd == 0)
        {
            // rate halving, RFC 6937
            if (change > 0)
            {
                var inflght = snd_nxt - snd_una;
                ssthresh = inflght / 2;
                if (ssthresh < IKCP_THRESH_MIN)
                    ssthresh = IKCP_THRESH_MIN;
                cwnd = ssthresh + resent;
                incr = cwnd * mss;
            }

            // 혼잡 제어, RFC 5681
            if (lostSegs > 0)
            {
                ssthresh = cwnd / 2;
                if (ssthresh < IKCP_THRESH_MIN)
                    ssthresh = IKCP_THRESH_MIN;
                cwnd = 1;
                incr = mss;
            }

            if (cwnd < 1)
            {
                cwnd = 1;
                incr = mss;
            }
        }

        Segment.Put(seg);
        return (uint)minrto;
    }

    /// <summary>
    /// 상태를 갱신한다. 10~100ms 주기로 반복 호출하거나, <see cref="Check"/> 가 알려 준 시각에 호출한다.
    /// </summary>
    public void Update()
    {
        var current = clock();

        if (0 == updated)
        {
            updated = 1;
            ts_flush = current;
        }

        var slap = _itimediff(current, ts_flush);

        if (slap >= 10000 || slap < -10000)
        {
            ts_flush = current;
            slap = 0;
        }

        if (slap >= 0)
        {
            ts_flush += interval;
            if (_itimediff(current, ts_flush) >= 0)
                ts_flush = current + interval;
            Flush(false);
        }
    }

    /// <summary>
    /// 다음에 <see cref="Update"/> 를 호출해야 하는 시각(ms)을 반환한다.
    /// </summary>
    /// <remarks>
    /// 반환값과 현재 시각의 비교는 반드시 <c>(int)(now - check) &gt;= 0</c> 처럼 부호 있는 차이로 해야 한다.
    /// 단순 크기 비교는 약 49.7일 후 랩어라운드에서 영원히 갱신되지 않는 정지를 만든다(진단 B-02).
    /// </remarks>
    /// <returns>다음 <see cref="Update"/> 호출 시각(ms).</returns>
    public uint Check()
    {
        var current = clock();

        var ts_flush_ = ts_flush;
        var tm_flush_ = 0x7fffffff;
        var tm_packet = 0x7fffffff;
        var minimal = 0;

        if (updated == 0)
            return current;

        if (_itimediff(current, ts_flush_) >= 10000 || _itimediff(current, ts_flush_) < -10000)
            ts_flush_ = current;

        if (_itimediff(current, ts_flush_) >= 0)
            return current;

        tm_flush_ = _itimediff(ts_flush_, current);

        foreach (var seg in snd_buf)
        {
            var diff = _itimediff(seg.resendts, current);
            if (diff <= 0)
                return current;
            if (diff < tm_packet)
                tm_packet = diff;
        }

        minimal = tm_packet;
        if (tm_packet >= tm_flush_)
            minimal = tm_flush_;
        if (minimal >= interval)
            minimal = (int)interval;

        return current + (uint)minimal;
    }

    /// <summary>MTU 를 바꾼다(기본 1400).</summary>
    /// <param name="mtu_">새 MTU.</param>
    /// <returns>0 이면 성공, 음수는 오류.</returns>
    public int SetMtu(int mtu_)
    {
        if (mtu_ < 50 || mtu_ < IKCP_OVERHEAD)
            return -1;
        if (reserved >= (int)(mtu - IKCP_OVERHEAD) || reserved < 0)
            return -1;

        var buffer_ = new byte[mtu_];

        mtu = (uint)mtu_;
        mss = mtu - IKCP_OVERHEAD - (uint)reserved;
        buffer = buffer_;
        return 0;
    }

    /// <summary>지연·재전송 동작을 설정한다. 가장 빠른 설정은 <c>NoDelay(1, 20, 2, 1)</c>.</summary>
    /// <param name="nodelay_">0: 비활성(기본), 1: 활성.</param>
    /// <param name="interval_">내부 갱신 간격(ms). 기본 100.</param>
    /// <param name="resend_">0: 빠른 재전송 끔(기본), n: 중복 ACK n회에 재전송.</param>
    /// <param name="nc_">0: 혼잡 제어 사용(기본), 1: 사용 안 함.</param>
    /// <returns>항상 0.</returns>
    public int NoDelay(int nodelay_, int interval_, int resend_, int nc_)
    {
        if (nodelay_ >= 0)
        {
            nodelay = (uint)nodelay_;
            rx_minrto = (uint)(nodelay_ != 0 ? IKCP_RTO_NDL : IKCP_RTO_MIN);
        }

        if (interval_ >= 0)
        {
            if (interval_ > 5000)
                interval_ = 5000;
            else if (interval_ < 10)
                interval_ = 10;
            interval = (uint)interval_;
        }

        if (resend_ >= 0)
            fastresend = resend_;

        if (nc_ >= 0)
            nocwnd = nc_;

        return 0;
    }

    /// <summary>최대 윈도 크기를 설정한다(기본 송신 32 / 수신 32).</summary>
    /// <param name="sndwnd">송신 윈도(세그먼트 수). 0 이하면 변경하지 않는다.</param>
    /// <param name="rcvwnd">수신 윈도(세그먼트 수). 0 이하면 변경하지 않는다.</param>
    /// <returns>항상 0.</returns>
    public int WndSize(int sndwnd, int rcvwnd)
    {
        if (sndwnd > 0)
            snd_wnd = (uint)sndwnd;

        if (rcvwnd > 0)
            rcv_wnd = (uint)rcvwnd;
        return 0;
    }

    /// <summary>
    /// 각 패킷 앞에 상위 계층이 쓸 예약 영역을 확보한다(FEC 헤더, 암호화 nonce 등).
    /// </summary>
    /// <param name="reservedSize">예약 바이트 수.</param>
    /// <returns>성공하면 true.</returns>
    public bool ReserveBytes(int reservedSize)
    {
        if (reservedSize >= mtu - IKCP_OVERHEAD || reservedSize < 0)
            return false;

        reserved = reservedSize;
        mss = mtu - IKCP_OVERHEAD - (uint)reservedSize;
        return true;
    }

    /// <summary>스트림 모드를 켜고 끈다. 켜면 메시지 경계가 사라지고 바이트 스트림이 된다.</summary>
    /// <param name="enabled">true 면 스트림 모드.</param>
    public void SetStreamMode(bool enabled)
    {
        stream = enabled ? 1 : 0;
    }

    /// <summary>현재까지 누적된 통계의 스냅숏을 반환한다.</summary>
    /// <returns>통계 값 복사본.</returns>
    public KcpStats GetStats()
    {
        var snapshot = stats;
        snapshot.CurrentRto = (int)rx_rto;
        snapshot.Srtt = rx_srtt;
        snapshot.SendWindowUsage = snd_buf.Count;
        snapshot.WaitSend = WaitSnd;
        return snapshot;
    }

    private bool ikcp_canlog(int mask)
    {
        if ((mask & logmask) == 0 || writelog == null) return false;
        return true;
    }

    /// <summary>로거를 설정한다. null 이면 로그를 남기지 않는다.</summary>
    /// <param name="logger">로그 콜백.</param>
    public void SetLogger(Action<string>? logger)
    {
        writelog = logger;
    }

    /// <summary>로그 마스크를 설정한다(<c>IKCP_LOG_*</c> 상수의 조합).</summary>
    /// <param name="mask">마스크.</param>
    public void SetLogMask(int mask)
    {
        logmask = mask;
    }

    private void ikcp_log(string logStr)
    {
        writelog?.Invoke(logStr);
    }
}

/// <summary>
/// 이전 이름 호환용 별칭. 새 코드는 <see cref="Kcp"/> 를 사용한다.
/// </summary>
[Obsolete("Kcp 로 이름이 변경되었습니다. 다음 메이저 버전에서 제거됩니다.")]
public class KCP : Kcp
{
    /// <summary>새 KCP 제어 객체를 만든다.</summary>
    /// <param name="conv">대화 번호.</param>
    /// <param name="output">패킷 송신 콜백.</param>
    public KCP(uint conv, Action<byte[], int> output) : base(conv, output) { }
}
