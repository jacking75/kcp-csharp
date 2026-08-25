using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KcpCSharpR1.Transport;

/// <summary>
/// 무상태 SYN 쿠키 발급·검증기(P4-1b).
/// </summary>
/// <remarks>
/// <para>
/// 서버는 SYN 을 받아도 <b>세션을 만들지 않는다</b>. 대신
/// <c>cookie = HMAC-SHA256(serverSecret, 클라IP‖포트‖타임버킷)</c> 의 앞 4바이트를 돌려주고 잊는다.
/// 클라이언트가 그 쿠키를 담아 SYN 을 다시 보내야 비로소 세션을 만든다.
/// </para>
/// <para>
/// 이렇게 하면 소스 주소를 위조한 SYN 홍수로 서버 세션 테이블을 채우는 상태 고갈 공격이 통하지 않는다
/// (위조한 주소로는 쿠키를 받아 볼 수 없다). 진단 S-04 의 해결책이다.
/// </para>
/// <para>
/// 타임버킷은 기본 30초다. 경계에서 재전송이 어긋나는 것을 막기 위해 <b>현재와 직전 버킷 2개</b>를 허용한다.
/// 따라서 쿠키의 유효 기간은 30~60초다.
/// </para>
/// </remarks>
public sealed class HandshakeCookie : IDisposable
{
    /// <summary>타임버킷 길이(초).</summary>
    public const int BucketSeconds = 30;

    private readonly byte[] secret;
    private readonly object gate = new object();
    private bool disposed;

    /// <summary>서버 비밀키를 새로 만들어 초기화한다(기동 시 1회).</summary>
    public HandshakeCookie()
    {
        secret = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(secret);
    }

    /// <summary>비밀키를 직접 지정한다(테스트·다중 서버 공유용).</summary>
    /// <param name="serverSecret">32바이트 이상의 비밀키.</param>
    public HandshakeCookie(byte[] serverSecret)
    {
        if (serverSecret == null) throw new ArgumentNullException(nameof(serverSecret));
        if (serverSecret.Length < 16)
            throw new ArgumentException("서버 비밀키는 16바이트 이상이어야 합니다.", nameof(serverSecret));

        secret = (byte[])serverSecret.Clone();
    }

    /// <summary>지정 시각 기준의 쿠키를 계산한다.</summary>
    /// <param name="remote">클라이언트 주소.</param>
    /// <param name="unixSeconds">현재 시각(Unix 초).</param>
    /// <returns>쿠키 값(0 이 되지 않도록 보정한다).</returns>
    public uint Compute(AddressKey remote, long unixSeconds)
    {
        return ComputeForBucket(remote, unixSeconds / BucketSeconds);
    }

    /// <summary>쿠키가 유효한지 검사한다(현재 버킷과 직전 버킷 허용).</summary>
    /// <param name="remote">클라이언트 주소.</param>
    /// <param name="cookie">클라이언트가 보낸 쿠키.</param>
    /// <param name="unixSeconds">현재 시각(Unix 초).</param>
    /// <returns>유효하면 true.</returns>
    public bool Validate(AddressKey remote, uint cookie, long unixSeconds)
    {
        if (cookie == 0) return false;

        var bucket = unixSeconds / BucketSeconds;

        // 상수 시간 비교는 여기서 의미가 크지 않지만(공격자가 쿠키를 대량 시도해도 세션이 안 생김),
        // 습관적으로 두 후보 모두 계산한 뒤 OR 로 합친다.
        var current = ComputeForBucket(remote, bucket) == cookie;
        var previous = ComputeForBucket(remote, bucket - 1) == cookie;
        return current || previous;
    }

    private uint ComputeForBucket(AddressKey remote, long bucket)
    {
        if (disposed) throw new ObjectDisposedException(nameof(HandshakeCookie));

        Span<byte> message = stackalloc byte[AddressKey.SerializedSize + sizeof(long)];
        remote.WriteTo(message);
        BinaryPrimitives.WriteInt64LittleEndian(message.Slice(AddressKey.SerializedSize), bucket);

        Span<byte> hash = stackalloc byte[32];
        lock (gate)
        {
#if NET8_0_OR_GREATER
            HMACSHA256.HashData(secret, message, hash);
#else
            using var hmac = new HMACSHA256(secret);
            var result = hmac.ComputeHash(message.ToArray());
            result.AsSpan(0, 32).CopyTo(hash);
#endif
        }

        var cookie = BinaryPrimitives.ReadUInt32LittleEndian(hash);

        // 0 은 "쿠키 없음"을 뜻하므로 예약한다.
        return cookie == 0 ? 1u : cookie;
    }

    /// <summary>비밀키를 지운다.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Array.Clear(secret, 0, secret.Length);
    }
}
