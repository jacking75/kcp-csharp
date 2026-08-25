using System;
using System.IO;
using System.Text;
using KcpProject;

namespace KcpProject.Tests;

/// <summary>
/// P2-5 상호운용 테스트. 와이어 포맷 그대로 저장한 골든 패킷을 <see cref="Kcp.Input(byte[], int, int, bool, bool)"/> 에
/// 흘려 넣어 애플리케이션 데이터가 동일하게 복원되는지 검증한다.
/// 벡터의 출처와 재생성 방법은 <c>tests/golden/README.md</c> 참조.
/// </summary>
public class InteropTests
{
    private const uint GoldenConv = 0x11223344;

    private static byte[] LoadGolden(string name)
    {
        // 테스트 실행 디렉터리(bin/...)에서 저장소 루트의 tests/golden 을 찾는다.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "golden", name);
            if (File.Exists(candidate))
                return File.ReadAllBytes(candidate);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new FileNotFoundException($"골든 벡터를 찾지 못했습니다: {name}");
    }

    private static Kcp NewPeer() => new Kcp(GoldenConv, (_, _) => { }, () => 0);

    /// <summary>조각난 메시지 2세그먼트가 한 UDP 패킷에 들어 있어도 원본 메시지로 복원돼야 한다.</summary>
    [Fact]
    public void 골든_조각패킷이_원본_메시지로_복원된다()
    {
        var packet = LoadGolden("kcp_push_fragmented.bin");
        var kcp = NewPeer();

        Assert.Equal(0, kcp.Input(packet, 0, packet.Length, true, false));

        var size = kcp.PeekSize();
        Assert.Equal(13, size);

        var buf = new byte[size];
        Assert.Equal(13, kcp.Recv(buf));
        Assert.Equal("ABCDEFGHIJKLM", Encoding.ASCII.GetString(buf));
    }

    /// <summary>단일 세그먼트 메시지.</summary>
    [Fact]
    public void 골든_단일패킷이_원본_메시지로_복원된다()
    {
        // sn=2 이므로 앞선 sn=0,1 이 먼저 와야 큐로 넘어간다.
        var first = LoadGolden("kcp_push_fragmented.bin");
        var second = LoadGolden("kcp_push_single.bin");
        var kcp = NewPeer();

        Assert.Equal(0, kcp.Input(first, 0, first.Length, true, false));
        Assert.Equal(0, kcp.Input(second, 0, second.Length, true, false));

        var buf = new byte[64];
        Assert.Equal(13, kcp.Recv(buf));
        Assert.Equal("ABCDEFGHIJKLM", Encoding.ASCII.GetString(buf, 0, 13));

        Assert.Equal(3, kcp.Recv(buf));
        Assert.Equal("KCP", Encoding.ASCII.GetString(buf, 0, 3));

        Assert.Equal(-1, kcp.PeekSize());
    }

    /// <summary>ACK + WINS 제어 패킷은 데이터 없이 상태만 갱신한다.</summary>
    [Fact]
    public void 골든_제어패킷은_데이터를_만들지_않는다()
    {
        var packet = LoadGolden("kcp_control.bin");
        var kcp = NewPeer();

        Assert.Equal(0, kcp.Input(packet, 0, packet.Length, true, false));

        Assert.Equal(-1, kcp.PeekSize());
        Assert.Equal(31u, kcp.RmtWnd);   // WINS 가 알린 윈도(31)가 반영돼야 한다.
    }

    /// <summary>conv 가 다른 상대의 패킷은 거부한다(세션 오염 방지).</summary>
    [Fact]
    public void 골든패킷도_conv가_다르면_거부된다()
    {
        var packet = LoadGolden("kcp_push_fragmented.bin");
        var kcp = new Kcp(0xDEADBEEF, (_, _) => { }, () => 0);

        Assert.Equal(-1, kcp.Input(packet, 0, packet.Length, true, false));
        Assert.Equal(-1, kcp.PeekSize());
    }
}
