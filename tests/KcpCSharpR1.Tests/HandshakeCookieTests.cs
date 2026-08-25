using System;
using System.Net;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.Tests;

/// <summary>P4-1b: 무상태 SYN 쿠키(유효/만료/변조/다른 IP).</summary>
public class HandshakeCookieTests
{
    private static AddressKey Key(string ip, int port) =>
        AddressKey.FromEndPoint(new IPEndPoint(IPAddress.Parse(ip), port));

    private static readonly byte[] Secret = new byte[32]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    };

    [Fact]
    public void 같은_시각_같은_주소면_같은_쿠키가_나온다()
    {
        using var cookies = new HandshakeCookie(Secret);
        var remote = Key("192.168.0.10", 30000);

        var a = cookies.Compute(remote, 1_700_000_000);
        var b = cookies.Compute(remote, 1_700_000_000);

        Assert.Equal(a, b);
        Assert.NotEqual(0u, a);
    }

    [Fact]
    public void 발급한_쿠키는_유효하다()
    {
        using var cookies = new HandshakeCookie(Secret);
        var remote = Key("10.0.0.1", 27100);
        var now = 1_700_000_000L;

        var cookie = cookies.Compute(remote, now);

        Assert.True(cookies.Validate(remote, cookie, now));
    }

    [Fact]
    public void 다른_주소의_쿠키는_거부한다()
    {
        using var cookies = new HandshakeCookie(Secret);
        var now = 1_700_000_000L;
        var cookie = cookies.Compute(Key("10.0.0.1", 27100), now);

        Assert.False(cookies.Validate(Key("10.0.0.2", 27100), cookie, now));   // 다른 IP
        Assert.False(cookies.Validate(Key("10.0.0.1", 27101), cookie, now));   // 다른 포트
    }

    [Fact]
    public void 변조된_쿠키는_거부한다()
    {
        using var cookies = new HandshakeCookie(Secret);
        var remote = Key("10.0.0.1", 27100);
        var now = 1_700_000_000L;
        var cookie = cookies.Compute(remote, now);

        Assert.False(cookies.Validate(remote, cookie ^ 0x1, now));
        Assert.False(cookies.Validate(remote, 0, now));            // 0 은 "쿠키 없음"
        Assert.False(cookies.Validate(remote, 0xDEADBEEF, now));
    }

    [Fact]
    public void 직전_버킷의_쿠키까지는_받아준다()
    {
        using var cookies = new HandshakeCookie(Secret);
        var remote = Key("10.0.0.1", 27100);

        // 버킷 경계 바로 앞에서 발급된 쿠키
        var issuedAt = 1_700_000_000L;
        var cookie = cookies.Compute(remote, issuedAt);

        // 한 버킷(30초) 뒤에도 유효해야 재전송이 살아남는다.
        Assert.True(cookies.Validate(remote, cookie, issuedAt + HandshakeCookie.BucketSeconds));
    }

    [Fact]
    public void 두_버킷_이상_지난_쿠키는_만료된다()
    {
        using var cookies = new HandshakeCookie(Secret);
        var remote = Key("10.0.0.1", 27100);
        var issuedAt = 1_700_000_000L;
        var cookie = cookies.Compute(remote, issuedAt);

        Assert.False(cookies.Validate(remote, cookie, issuedAt + HandshakeCookie.BucketSeconds * 3));
    }

    [Fact]
    public void 비밀키가_다르면_쿠키도_다르다()
    {
        var other = (byte[])Secret.Clone();
        other[0] ^= 0xFF;

        using var a = new HandshakeCookie(Secret);
        using var b = new HandshakeCookie(other);

        var remote = Key("10.0.0.1", 27100);
        var now = 1_700_000_000L;

        Assert.NotEqual(a.Compute(remote, now), b.Compute(remote, now));
        Assert.False(b.Validate(remote, a.Compute(remote, now), now));
    }

    [Fact]
    public void 기본_생성자는_무작위_비밀키를_쓴다()
    {
        using var a = new HandshakeCookie();
        using var b = new HandshakeCookie();
        var remote = Key("10.0.0.1", 27100);
        var now = 1_700_000_000L;

        // 서로 다른 서버 인스턴스의 쿠키는 호환되지 않아야 한다.
        Assert.NotEqual(a.Compute(remote, now), b.Compute(remote, now));
    }

    [Fact]
    public void 짧은_비밀키는_거부한다()
    {
        Assert.Throws<ArgumentException>(() => new HandshakeCookie(new byte[8]));
        Assert.Throws<ArgumentNullException>(() => new HandshakeCookie(null!));
    }
}
