﻿using System;
using System.Collections.Generic;

namespace KcpProject;


public class KCP
{
    public const int IKCP_RTO_NDL = 30;  // no delay min rto
    public const int IKCP_RTO_MIN = 100; // normal min rto
    public const int IKCP_RTO_DEF = 200;
    public const int IKCP_RTO_MAX = 60000;
    public const int IKCP_CMD_PUSH = 81; // cmd: push data
    public const int IKCP_CMD_ACK = 82; // cmd: ack
    public const int IKCP_CMD_WASK = 83; // cmd: window probe (ask)
    public const int IKCP_CMD_WINS = 84; // cmd: window size (tell)
    public const int IKCP_ASK_SEND = 1;  // need to send IKCP_CMD_WASK
    public const int IKCP_ASK_TELL = 2;  // need to send IKCP_CMD_WINS
    public const int IKCP_WND_SND = 32;
    public const int IKCP_WND_RCV = 32;
    public const int IKCP_MTU_DEF = 1400;
    public const int IKCP_ACK_FAST = 3;
    public const int IKCP_INTERVAL = 100;
    public const int IKCP_OVERHEAD = 24;
    public const int IKCP_DEADLINK = 20;
    public const int IKCP_THRESH_INIT = 2;
    public const int IKCP_THRESH_MIN = 2;
    public const int IKCP_PROBE_INIT = 7000;   // 7 secs to probe window size
    public const int IKCP_PROBE_LIMIT = 120000; // up to 120 secs to probe window
    public const int IKCP_SN_OFFSET = 12;


    // encode 8 bits unsigned int
    public static int ikcp_encode8u(byte[] p, int offset, byte c)
    {
        p[0 + offset] = c;
        return 1;
    }

    // decode 8 bits unsigned int
    public static int ikcp_decode8u(byte[] p, int offset, ref byte c)
    {
        c = p[0 + offset];
        return 1;
    }

    /* encode 16 bits unsigned int (lsb) */
    public static int ikcp_encode16u(byte[] p, int offset, UInt16 w)
    {
        p[0 + offset] = (byte)(w >> 0);
        p[1 + offset] = (byte)(w >> 8);
        return 2;
    }

    /* decode 16 bits unsigned int (lsb) */
    public static int ikcp_decode16u(byte[] p, int offset, ref UInt16 c)
    {
        UInt16 result = 0;
        result |= (UInt16)p[0 + offset];
        result |= (UInt16)(p[1 + offset] << 8);
        c = result;
        return 2;
    }

    /* encode 32 bits unsigned int (lsb) */
    public static int ikcp_encode32u(byte[] p, int offset, UInt32 l)
    {
        p[0 + offset] = (byte)(l >> 0);
        p[1 + offset] = (byte)(l >> 8);
        p[2 + offset] = (byte)(l >> 16);
        p[3 + offset] = (byte)(l >> 24);
        return 4;
    }

    /* decode 32 bits unsigned int (lsb) */
    public static int ikcp_decode32u(byte[] p, int offset, ref UInt32 c)
    {
        UInt32 result = 0;
        result |= (UInt32)p[0 + offset];
        result |= (UInt32)(p[1 + offset] << 8);
        result |= (UInt32)(p[2 + offset] << 16);
        result |= (UInt32)(p[3 + offset] << 24);
        c = result;
        return 4;
    }

    static UInt32 _imin_(UInt32 a, UInt32 b)
    {
        return a <= b ? a : b;
    }

    private static DateTime refTime = DateTime.Now;

    private static UInt32 currentMS()
    {
        var ts = DateTime.Now.Subtract(refTime);
        return (UInt32)ts.TotalMilliseconds;
    }

    static UInt32 _imax_(UInt32 a, UInt32 b)
    {
        return a >= b ? a : b;
    }

    static UInt32 _ibound_(UInt32 lower, UInt32 middle, UInt32 upper)
    {
        return _imin_(_imax_(lower, middle), upper);
    }

    static Int32 _itimediff(UInt32 later, UInt32 earlier)
    {
        return ((Int32)(later - earlier));
    }

    // KCP Segment Definition
    internal class Segment
    {
        internal UInt32 conv = 0;
        internal UInt32 cmd = 0;
        internal UInt32 frg = 0;
        internal UInt32 wnd = 0;
        internal UInt32 ts = 0;
        internal UInt32 sn = 0;
        internal UInt32 una = 0;
        internal UInt32 rto = 0;
        internal UInt32 xmit = 0;
        internal UInt32 resendts = 0;
        internal UInt32 fastack = 0;
        internal UInt32 acked = 0;
        internal ByteBuffer data;

        private static Stack<Segment> msSegmentPool = new Stack<Segment>(32);

        public static Segment Get(int size)
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

        public static void Put(Segment seg)
        {
            seg.reset();
            lock (msSegmentPool) {
                msSegmentPool.Push(seg);
            }
        }

        private Segment(int size)
        {
            data = ByteBuffer.Allocate(size, true);
        }

        // encode a segment into buffer
        internal int encode(byte[] ptr, int offset)
        {

            var offset_ = offset;

            offset += ikcp_encode32u(ptr, offset, conv);
            offset += ikcp_encode8u(ptr, offset, (byte)cmd);
            offset += ikcp_encode8u(ptr, offset, (byte)frg);
            offset += ikcp_encode16u(ptr, offset, (UInt16)wnd);
            offset += ikcp_encode32u(ptr, offset, ts);
            offset += ikcp_encode32u(ptr, offset, sn);
            offset += ikcp_encode32u(ptr, offset, una);
            offset += ikcp_encode32u(ptr, offset, (UInt32)data.ReadableBytes);

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
            data = null;
        }
    }

    internal struct ackItem
    {
        internal UInt32 sn;
        internal UInt32 ts;
    }

    // kcp members.
    UInt32 conv; UInt32 mtu; UInt32 mss; UInt32 state;
    UInt32 snd_una; UInt32 snd_nxt; UInt32 rcv_nxt;
    UInt32 ts_recent; UInt32 ts_lastack; UInt32 ssthresh;
    Int32 rx_rttval; Int32 rx_srtt;
    UInt32 rx_rto; UInt32 rx_minrto;
    UInt32 snd_wnd; UInt32 rcv_wnd; UInt32 rmt_wnd; UInt32 cwnd; UInt32 probe;
    UInt32 interval; UInt32 ts_flush;
    UInt32 nodelay; UInt32 updated;
    UInt32 ts_probe; UInt32 probe_wait;
    UInt32 dead_link; UInt32 incr;

    Int32 fastresend;
    Int32 nocwnd; Int32 stream;

    List<Segment> snd_queue = new List<Segment>(16);
    List<Segment> rcv_queue = new List<Segment>(16);
    List<Segment> snd_buf = new List<Segment>(16);
    List<Segment> rcv_buf = new List<Segment>(16);

    List<ackItem> acklist = new List<ackItem>(16);

    byte[] buffer;
    Int32 reserved;
    Action<byte[], int> output; // buffer, size

    // send windowd & recv window
    public UInt32 SndWnd { get { return snd_wnd; } }
    public UInt32 RcvWnd { get { return rcv_wnd; } }
    public UInt32 RmtWnd { get { return rmt_wnd; } }
    public UInt32 Mss { get { return mss; } }

    // get how many packet is waiting to be sent
    public int WaitSnd { get { return snd_buf.Count + snd_queue.Count; } }

    // internal time.
    public UInt32 CurrentMS { get { return currentMS(); } }

    // log
    Action<string> writelog = null;

    public const Int32 IKCP_LOG_OUTPUT = 1;
    public const Int32 IKCP_LOG_INPUT = 2;
    public const Int32 IKCP_LOG_SEND = 4;
    public const Int32 IKCP_LOG_RECV = 8;
    public const Int32 IKCP_LOG_IN_DATA = 16;
    public const Int32 IKCP_LOG_IN_ACK = 32;
    public const Int32 IKCP_LOG_IN_PROBE = 64;
    public const Int32 IKCP_LOG_IN_WINS = 128;
    public const Int32 IKCP_LOG_OUT_DATA = 256;
    public const Int32 IKCP_LOG_OUT_ACK = 512;
    public const Int32 IKCP_LOG_OUT_PROBE = 1024;
    public const Int32 IKCP_LOG_OUT_WINS = 2048;
    public Int32 logmask;

    // 새 kcp 제어 개체를 만들 때, 'conv'는 두 엔드포인트에서 같아야 한다.
    // 동일한 연결에서 동일해야 한다.
    public KCP(UInt32 conv_, Action<byte[], int> output_)
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
        output = output_;
    }

    // 수신 대기열에서 다음 메시지의 크기를 확인한다
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


    public int Recv(byte[] buffer)
    {
        return Recv(buffer, 0, buffer.Length);
    }

    // kcp 상태 머신에서 데이터 수신
    //
    // 읽은 바이트 수를 반환한다
    //
    // 읽을 수 있는 데이터가 없으면 -1을 반환한다
    //
    // len(buffer)가 kcp.PeekSize()보다 작으면 -2를 반환한다
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

        // merge fragment.
        var count = 0;
        var n = index;
        foreach (var seg in rcv_queue)
        {
            // copy fragment data into buffer.
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

        // move available data from rcv_buf -> rcv_queue
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


        // fast recover
        if (rcv_queue.Count < rcv_wnd && fast_recover)
        {
            // ikcp_flush에서 IKCP_CMD_WINS를 다시 보낼 준비 완료
            // 원격에 내 창 크기 알려주기
            probe |= IKCP_ASK_TELL;
        }

        return n - index;
    }

    public int Send(byte[] buffer)
    {
        return Send(buffer, 0, buffer.Length);
    }

    // user/upper level send, returns below zero for error
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

        var count = 0;
        if (length <= mss)
            count = 1;
        else
            count = (int)(((length) + mss - 1) / mss);

        if (count > 255) return -2;

        if (count == 0) count = 1;

        for (var i = 0; i < count; i++)
        {
            var size = Math.Min(length, (int)mss);

            var seg = Segment.Get(size);
            seg.data.WriteBytes(buffer, index, size);
            index += size;
            length -= size;

            seg.frg = (stream == 0 ? (byte)(count - i - 1) : (byte)0);
            snd_queue.Add(seg);
        }

        return 0;
    }

    // update ack.
    void update_ack(Int32 rtt)
    {
        // https://tools.ietf.org/html/rfc6298
        if (0 == rx_srtt)
        {
            rx_srtt = rtt;
            rx_rttval = rtt >> 1;
        }
        else
        {
            Int32 delta = rtt - rx_srtt;
            rx_srtt += (delta >> 3);
            if (0 > delta) delta = -delta;

            if (rtt < rx_srtt - rx_rttval)
            {
                // 새 RTT 샘플이 다음 범위의 하단보다 낮은 경우
                // RTT 측정값이 될 것으로 예상된다
                // 정상 가중치 대비 8배 감소된 가중치를 부여한다
                rx_rttval += ((delta - rx_rttval) >> 5);
            }
            else
            {
                rx_rttval += ((delta - rx_rttval) >> 2);
            }
        }

        uint rto = (uint)(rx_srtt) + _imax_(interval, (uint)(rx_rttval) << 2);
        rx_rto = _ibound_(rx_minrto, rto, IKCP_RTO_MAX);
    }

    void shrink_buf()
    {
        if (snd_buf.Count > 0)
            snd_una = snd_buf[0].sn;
        else
            snd_una = snd_nxt;
    }

    void parse_ack(UInt32 sn)
    {

        if (_itimediff(sn, snd_una) < 0 || _itimediff(sn, snd_nxt) >= 0) return;

        foreach (var seg in snd_buf)
        {
            if (sn == seg.sn)
            {
                // mark와 여유 공간을 확보하되 세그먼트는 여기에 남겨둔다
                // 그리고 `una`가 이것을 삭제할 때까지 기다린다
                // 뒤의 세그먼트를 앞으로 이동해야 한다
                // 큰 창에서는 비용이 많이 드는 작업이다
                seg.acked = 1;
                break;
            }
            if (_itimediff(sn, seg.sn) < 0)
                break;
        }
    }

    void parse_fastack(UInt32 sn, UInt32 ts)
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

    int parse_una(UInt32 una)
    {
        var count = 0;
        foreach (var seg in snd_buf)
        {
            if (_itimediff(una, seg.sn) > 0) {
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

    void ack_push(UInt32 sn, UInt32 ts)
    {
        acklist.Add(new ackItem { sn = sn, ts = ts });
    }

    bool parse_data(Segment newseg)
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

        // move available data from rcv_buf -> rcv_queue
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

    // 낮은 수준의 패킷(예: UDP 패킷)을 수신했을 때 입력한다.
    // regular은 일반 패킷을 수신했음을 나타낸다(FEC가 아님).
    // 'ackNoDelay'는 즉각적인 ACK를 트리거하지만 대역폭에서 효율적이지 않을 것이다.
    public int Input(byte[] data, int index, int size, bool regular, bool ackNoDelay)
    {
        var s_una = snd_una;
        if (size < IKCP_OVERHEAD) return -1;

        Int32 offset = index;
        UInt32 latest = 0;
        int flag = 0;
        UInt64 inSegs = 0;
        bool windowSlides = false;

        if (ikcp_canlog(IKCP_LOG_INPUT))
        {
            ikcp_log($"[RI] {size.ToString()} bytes");
        }


        while (true)
        {
            UInt32 ts = 0;
            UInt32 sn = 0;
            UInt32 length = 0;
            UInt32 una = 0;
            UInt32 conv_ = 0;
            UInt32 current = currentMS();

            UInt16 wnd = 0;
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

            // only trust window updates from regular packets. i.e: latest update
            if (regular)
            {
                rmt_wnd = wnd;
            }

            if (parse_una(una) > 0) {
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

                var repeat = true;
                if (_itimediff(sn, rcv_nxt + rcv_wnd) < 0)
                {
                    ack_push(sn, ts);
                    if (_itimediff(sn, rcv_nxt) >= 0)
                    {
                        var seg = Segment.Get((int)length);
                        seg.conv = conv_;
                        seg.cmd = (UInt32)cmd;
                        seg.frg = (UInt32)frg;
                        seg.wnd = (UInt32)wnd;
                        seg.ts = ts;
                        seg.sn = sn;
                        seg.una = una;
                        seg.data.WriteBytes(data, offset, (int)length);
                        repeat = parse_data(seg);
                    }
                }
            }
            else if (IKCP_CMD_WASK == cmd)
            {
                // ready to send back IKCP_CMD_WINS in Ikcp_flush
                // tell remote my window size
                probe |= IKCP_ASK_TELL;

                if (ikcp_canlog(IKCP_LOG_IN_PROBE))
                {
                    ikcp_log(" input probe");
                }
            }
            else if (IKCP_CMD_WINS == cmd)
            {
                // do nothing
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

        // update rtt with the latest ts
        // ignore the FEC packet
        if (flag != 0 && regular)
        {
            var current = currentMS();
            if (_itimediff(current, latest) >= 0)
            {
                update_ack(_itimediff(current, latest));
            }
        }

        // cwnd update when packet arrived
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
                        incr += (_mss * _mss) / incr + (_mss) / 16;
                        if ((cwnd + 1) * _mss <= incr)
                        {
                            if (_mss > 0)
                                cwnd = (incr + _mss - 1) / _mss;
                            else
                                cwnd = incr + _mss - 1;
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

        if (windowSlides)   // if window has slided, flush
        {
            Flush(false);
        }
        else if (ackNoDelay && acklist.Count > 0) // // ack immediately
        {
            Flush(true);
        }

        return 0;
    }

    UInt16 wnd_unused()
    {
        if (rcv_queue.Count < rcv_wnd)
            return (UInt16)(rcv_wnd - rcv_queue.Count);
        return 0;
    }

    int makeSpace(int space, int writeIndex)
    {
        if (writeIndex + space > mtu)
        {
            if (ikcp_canlog(IKCP_LOG_OUTPUT))
            {
                ikcp_log($"[RO] {writeIndex.ToString()} bytes");
            }
            output(buffer, writeIndex);
            writeIndex = reserved;
        }
        return writeIndex;
    }

    void flushBuffer(int writeIndex)
    {
        if (writeIndex > reserved)
        {
            if (ikcp_canlog(IKCP_LOG_OUTPUT))
            {
                ikcp_log($"[RO] {writeIndex.ToString()} bytes");
            }
            output(buffer, writeIndex);
        }
    }

    // flush pending data
    public UInt32 Flush(bool ackOnly)
    {
        var seg = Segment.Get(32);
        seg.conv = conv;
        seg.cmd = IKCP_CMD_ACK;
        seg.wnd = (UInt32)wnd_unused();
        seg.una = rcv_nxt;

        var writeIndex = reserved;

        // flush acknowledges
        for (var i = 0; i < acklist.Count; i++)
        {
            writeIndex = makeSpace(KCP.IKCP_OVERHEAD, writeIndex);
            var ack = acklist[i];
            if ( _itimediff(ack.sn, rcv_nxt) >=0 || acklist.Count - 1 == i)
            {
                seg.sn = ack.sn;
                seg.ts = ack.ts;
                writeIndex += seg.encode(buffer, writeIndex);

                if (ikcp_canlog(IKCP_LOG_OUT_ACK))
                {
                    ikcp_log($"output ack: sn={seg.sn.ToString()}");
                }
            }
        }
        acklist.Clear();

        // flash remain ack segments
        if (ackOnly)
        {
            flushBuffer(writeIndex);
            Segment.Put(seg);
            return interval;
        }

        uint current = 0;
        // probe window size (if remote window size equals zero)
        if (0 == rmt_wnd)
        {
            current = currentMS();
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

        // flush window probing commands
        if ((probe & IKCP_ASK_SEND) != 0)
        {
            seg.cmd = IKCP_CMD_WASK;
            writeIndex = makeSpace(IKCP_OVERHEAD, writeIndex);
            writeIndex += seg.encode(buffer, writeIndex);
        }

        if ((probe & IKCP_ASK_TELL) != 0)
        {
            seg.cmd = IKCP_CMD_WINS;
            writeIndex = makeSpace(IKCP_OVERHEAD, writeIndex);
            writeIndex += seg.encode(buffer, writeIndex);
        }

        probe = 0;

        // calculate window size
        var cwnd_ = _imin_(snd_wnd, rmt_wnd);
        if (0 == nocwnd)
            cwnd_ = _imin_(cwnd, cwnd_);

        // sliding window, controlled by snd_nxt && sna_una+cwnd
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

        // calculate resent
        var resent = (UInt32)fastresend;
        if (fastresend <= 0) resent = 0xffffffff;

        // check for retransmissions
        current = currentMS();
        UInt64 change = 0; UInt64 lostSegs = 0; UInt64 fastRetransSegs = 0; UInt64 earlyRetransSegs = 0;
        var minrto = (Int32)interval;

        for (var k = 0; k < snd_buf.Count; k++)
        {
            var segment = snd_buf[k];
            var needsend = false;
            if (segment.acked == 1)
                continue;
            if (segment.xmit == 0)  // initial transmit
            {
                needsend = true;
                segment.rto = rx_rto;
                segment.resendts = current + segment.rto;
            }
            else if (segment.fastack >= resent) // fast retransmit
            {
                needsend = true;
                segment.fastack = 0;
                segment.rto = rx_rto;
                segment.resendts = current + segment.rto;
                change++;
                fastRetransSegs++;
            }
            else if (segment.fastack > 0 && newSegsCount == 0) // early retransmit
            {
                needsend = true;
                segment.fastack = 0;
                segment.rto = rx_rto;
                segment.resendts = current + segment.rto;
                change++;
                earlyRetransSegs++;
            }
            else if (_itimediff(current, segment.resendts) >= 0) // RTO
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
                current = CurrentMS;
                segment.xmit++;
                segment.ts = current;
                segment.wnd = seg.wnd;
                segment.una = seg.una;

                var need = IKCP_OVERHEAD + segment.data.ReadableBytes;
                writeIndex = makeSpace(need, writeIndex);
                writeIndex += segment.encode(buffer, writeIndex);
                Buffer.BlockCopy(segment.data.RawBuffer, segment.data.ReaderIndex, buffer, writeIndex, segment.data.ReadableBytes);
                writeIndex += segment.data.ReadableBytes;

                if (segment.xmit >= dead_link)
                {
                    state = 0xFFFFFFFF;
                }

                if (ikcp_canlog(IKCP_LOG_OUT_DATA))
                {
                    ikcp_log($"output psh: sn={segment.sn.ToString()} ts={segment.ts.ToString()} resendts={segment.resendts.ToString()} rto={segment.rto.ToString()} fastack={segment.fastack.ToString()}, xmit={segment.xmit.ToString()}");
                }
            }

            // get the nearest rto
            var _rto = _itimediff(segment.resendts, current);
            if (_rto > 0 && _rto < minrto)
            {
                minrto = _rto;
            }
        }

        // flash remain segments
        flushBuffer(writeIndex);

        // cwnd update
        if (nocwnd == 0)
        {
            // update ssthresh
            // rate halving, https://tools.ietf.org/html/rfc6937
            if (change > 0)
            {
                var inflght = snd_nxt - snd_una;
                ssthresh = inflght / 2;
                if (ssthresh < IKCP_THRESH_MIN)
                    ssthresh = IKCP_THRESH_MIN;
                cwnd = ssthresh + resent;
                incr = cwnd * mss;
            }

            // congestion control, https://tools.ietf.org/html/rfc5681
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
        return (UInt32)minrto;
    }

    // 업데이트 상태(10ms-100ms 마다 반복 호출)를 호출하거나, 또는
    // ikcp_check 언제 다시 호출할지(ikcp_input/_send 호출 없이).
    // 'current' - 밀리초 단위의 현재 타임스탬프.
    public void Update()
    {
        var current = currentMS();

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

    // 언제 ikcp_update를 호출할지 결정한다
    // 밀리초 단위로 ikcp_update를 호출해야 하는 시점을 반환한다
    // ikcp_input/_send 호출이 없는 경우 해당
    // 업데이트를 반복해서 호출하지 않아도 된다
    // 불필요한 ikcp_update 호출을 줄이는 데 중요하다
    // ikcp_update를 예약하거나(예: epoll과 유사한 메커니즘 구현),
    // 또는 대규모 kcp 연결을 처리할 때 ikcp_update 최적화)
    public UInt32 Check()
    {
        var current = currentMS();

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

        tm_flush_ = (int)_itimediff(ts_flush_, current);

        foreach (var seg in snd_buf)
        {
            var diff = _itimediff(seg.resendts, current);
            if (diff <= 0)
                return current;
            if (diff < tm_packet)
                tm_packet = (int)diff;
        }

        minimal = (int)tm_packet;
        if (tm_packet >= tm_flush_)
            minimal = (int)tm_flush_;
        if (minimal >= interval)
            minimal = (int)interval;

        return current + (UInt32)minimal;
    }

    // change MTU size, default is 1400
    public int SetMtu(Int32 mtu_)
    {
        if (mtu_ < 50 || mtu_ < (Int32)IKCP_OVERHEAD)
            return -1;
        if (reserved >= (int)(mtu - IKCP_OVERHEAD) || reserved < 0)
            return -1;

        var buffer_ = new byte[mtu_];
        if (null == buffer_)
            return -2;

        mtu = (UInt32)mtu_;
        mss = mtu - IKCP_OVERHEAD - (UInt32)reserved;
        buffer = buffer_;
        return 0;
    }

    // fastest: ikcp_nodelay(kcp, 1, 20, 2, 1)
    // nodelay: 0:비활성화(기본값), 1:활성화
    // interval: 내부 업데이트 타이머 간격(밀리초), 기본값은 100ms 이다
    // resend: 0:빠른 재전송 비활성화(기본값), 1:빠른 재전송 활성화
    // nc: 0:정상 혼잡 제어(기본값), 1:혼잡 제어 비활성화
    public int NoDelay(int nodelay_, int interval_, int resend_, int nc_)
    {

        if (nodelay_ >= 0)
        {
            nodelay = (UInt32)nodelay_;
            if (nodelay_ != 0)
                rx_minrto = IKCP_RTO_NDL;
            else
                rx_minrto = IKCP_RTO_MIN;
        }

        if (interval_ >= 0)
        {
            if (interval_ > 5000)
                interval_ = 5000;
            else if (interval_ < 10)
                interval_ = 10;
            interval = (UInt32)interval_;
        }

        if (resend_ >= 0)
            fastresend = resend_;

        if (nc_ >= 0)
            nocwnd = nc_;

        return 0;
    }

    // set maximum window size: sndwnd=32, rcvwnd=32 by default
    public int WndSize(int sndwnd, int rcvwnd)
    {
        if (sndwnd > 0)
            snd_wnd = (UInt32)sndwnd;

        if (rcvwnd > 0)
            rcv_wnd = (UInt32)rcvwnd;
        return 0;
    }

    public bool ReserveBytes(int reservedSize)
    {
        if (reservedSize >= (mtu - IKCP_OVERHEAD) || reservedSize < 0)
            return false;

        reserved = reservedSize;
        mss = mtu - IKCP_OVERHEAD - (uint)(reservedSize);
        return true;
    }

    public void SetStreamMode(bool enabled)
    {
        stream = enabled ? 1 : 0;
    }

    bool ikcp_canlog(int mask)
    {
        if ((mask & logmask) == 0 || writelog == null) return false;
        return true;
    }

    public void SetLogger(Action<string> logger)
    {
        writelog = logger;
    }

    public void SetLogMask(int mask)
    {
        logmask = mask;
    }

    void ikcp_log(string logStr)
    {
        writelog?.Invoke(logStr);
    }
}