using KcpProject;

namespace KcpProject.Tests;

/// <summary>P2-2: 인코딩/디코딩 왕복과 와이어 포맷 골든 벡터.</summary>
public class CodecTests
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)127)]
    [InlineData((byte)255)]
    public void Encode8u_Decode8u_왕복(byte value)
    {
        var buf = new byte[4];
        var written = Kcp.ikcp_encode8u(buf, 1, value);
        byte read = 0;
        var consumed = Kcp.ikcp_decode8u(buf, 1, ref read);

        Assert.Equal(1, written);
        Assert.Equal(1, consumed);
        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)0x1234)]
    [InlineData((ushort)0xFFFF)]
    public void Encode16u_Decode16u_왕복(ushort value)
    {
        var buf = new byte[8];
        var written = Kcp.ikcp_encode16u(buf, 2, value);
        ushort read = 0;
        var consumed = Kcp.ikcp_decode16u(buf, 2, ref read);

        Assert.Equal(2, written);
        Assert.Equal(2, consumed);
        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x11223344u)]
    [InlineData(0xFFFFFFFFu)]
    public void Encode32u_Decode32u_왕복(uint value)
    {
        var buf = new byte[16];
        var written = Kcp.ikcp_encode32u(buf, 4, value);
        uint read = 0;
        var consumed = Kcp.ikcp_decode32u(buf, 4, ref read);

        Assert.Equal(4, written);
        Assert.Equal(4, consumed);
        Assert.Equal(value, read);
    }

    /// <summary>16/32비트는 리틀엔디언으로 배치되어야 한다(원본 kcp 와의 와이어 호환 근거).</summary>
    [Fact]
    public void 정수_배치는_리틀엔디언이다()
    {
        var buf = new byte[8];

        Kcp.ikcp_encode16u(buf, 0, 0x1234);
        Assert.Equal(new byte[] { 0x34, 0x12 }, buf[..2]);

        Kcp.ikcp_encode32u(buf, 0, 0x11223344);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, buf[..4]);
    }

    /// <summary>
    /// P2-2 헤더 골든 벡터. conv=0x11223344, cmd=81(PUSH), frg=2, wnd=32,
    /// ts=1000, sn=7, una=5, len=3 을 인코딩한 24바이트.
    /// 기대값은 원본 C kcp 의 ikcp_encode_seg 산출과 동일하다.
    /// </summary>
    private static readonly byte[] GoldenHeader =
    {
        0x44, 0x33, 0x22, 0x11,   // conv = 0x11223344
        0x51,                     // cmd  = 81 (IKCP_CMD_PUSH)
        0x02,                     // frg  = 2
        0x20, 0x00,               // wnd  = 32
        0xE8, 0x03, 0x00, 0x00,   // ts   = 1000
        0x07, 0x00, 0x00, 0x00,   // sn   = 7
        0x05, 0x00, 0x00, 0x00,   // una  = 5
        0x03, 0x00, 0x00, 0x00,   // len  = 3
    };

    [Fact]
    public void 헤더_골든벡터_인코딩이_원본kcp와_일치한다()
    {
        var buf = new byte[Kcp.IKCP_OVERHEAD];
        var offset = 0;
        offset += Kcp.ikcp_encode32u(buf, offset, 0x11223344);
        offset += Kcp.ikcp_encode8u(buf, offset, Kcp.IKCP_CMD_PUSH);
        offset += Kcp.ikcp_encode8u(buf, offset, 2);
        offset += Kcp.ikcp_encode16u(buf, offset, 32);
        offset += Kcp.ikcp_encode32u(buf, offset, 1000);
        offset += Kcp.ikcp_encode32u(buf, offset, 7);
        offset += Kcp.ikcp_encode32u(buf, offset, 5);
        offset += Kcp.ikcp_encode32u(buf, offset, 3);

        Assert.Equal(Kcp.IKCP_OVERHEAD, offset);
        Assert.Equal(GoldenHeader, buf);
    }

    [Fact]
    public void 헤더_골든벡터_디코딩이_원본값을_복원한다()
    {
        var offset = 0;
        uint conv = 0, ts = 0, sn = 0, una = 0, len = 0;
        byte cmd = 0, frg = 0;
        ushort wnd = 0;

        offset += Kcp.ikcp_decode32u(GoldenHeader, offset, ref conv);
        offset += Kcp.ikcp_decode8u(GoldenHeader, offset, ref cmd);
        offset += Kcp.ikcp_decode8u(GoldenHeader, offset, ref frg);
        offset += Kcp.ikcp_decode16u(GoldenHeader, offset, ref wnd);
        offset += Kcp.ikcp_decode32u(GoldenHeader, offset, ref ts);
        offset += Kcp.ikcp_decode32u(GoldenHeader, offset, ref sn);
        offset += Kcp.ikcp_decode32u(GoldenHeader, offset, ref una);
        offset += Kcp.ikcp_decode32u(GoldenHeader, offset, ref len);

        Assert.Equal(Kcp.IKCP_OVERHEAD, offset);
        Assert.Equal(0x11223344u, conv);
        Assert.Equal(Kcp.IKCP_CMD_PUSH, cmd);
        Assert.Equal(2, frg);
        Assert.Equal(32, wnd);
        Assert.Equal(1000u, ts);
        Assert.Equal(7u, sn);
        Assert.Equal(5u, una);
        Assert.Equal(3u, len);
    }

    /// <summary>KcpSegment.Encode 가 골든 헤더와 같은 바이트를 만든다(내부 구현 회귀 방지).</summary>
    [Fact]
    public void Segment_Encode가_골든헤더와_일치한다()
    {
        var seg = new KcpSegment();
        try
        {
            seg.Rent(3);
            seg.conv = 0x11223344;
            seg.cmd = Kcp.IKCP_CMD_PUSH;
            seg.frg = 2;
            seg.wnd = 32;
            seg.ts = 1000;
            seg.sn = 7;
            seg.una = 5;
            seg.Append(new byte[] { 1, 2, 3 });

            var buf = new byte[Kcp.IKCP_OVERHEAD];
            var written = seg.Encode(buf);

            Assert.Equal(Kcp.IKCP_OVERHEAD, written);
            Assert.Equal(GoldenHeader, buf);
        }
        finally
        {
            seg.Reset();
        }
    }

    /// <summary>Reset 은 멱등이어야 한다(ArrayPool 이중 반환 금지).</summary>
    [Fact]
    public void Segment_Reset은_여러번_불러도_안전하다()
    {
        var seg = new KcpSegment();
        seg.Rent(128);
        seg.Append(new byte[] { 1, 2, 3 });
        Assert.Equal(3, seg.Len);

        seg.Reset();
        seg.Reset();
        seg.Reset();

        Assert.Equal(0, seg.Len);
        Assert.Empty(seg.Data);
    }

    /// <summary>대여 배열은 요청보다 클 수 있으므로 유효 길이는 Len 으로만 판단해야 한다.</summary>
    [Fact]
    public void Segment_대여배열_길이가_아니라_Len이_유효길이다()
    {
        var seg = new KcpSegment();
        seg.Rent(100);
        seg.Append(new byte[] { 9, 9 });

        Assert.True(seg.Data.Length >= 100);
        Assert.Equal(2, seg.Len);
        Assert.Equal(2, seg.Payload.Length);

        seg.Reset();
    }

    /// <summary>
    /// 시각 비교는 랩어라운드를 넘어서도 정상이어야 한다(진단 B-01/B-02 의 근거).
    /// </summary>
    [Fact]
    public void Itimediff_랩어라운드_경계()
    {
        // 0x10 은 0xFFFF_FFF0 보다 32ms '뒤'다.
        Assert.Equal(32, Kcp._itimediff(0x10, 0xFFFFFFF0));
        Assert.Equal(-32, Kcp._itimediff(0xFFFFFFF0, 0x10));

        Assert.Equal(0, Kcp._itimediff(0, 0));
        Assert.Equal(1, Kcp._itimediff(1, 0));
        Assert.Equal(-1, Kcp._itimediff(0, 1));

        // 단순 부호 없는 비교였다면 여기서 잘못된 결론이 난다.
        Assert.True(0xFFFFFFF0u > 0x10u);
        Assert.True(Kcp._itimediff(0x10, 0xFFFFFFF0) > 0);
    }
}
