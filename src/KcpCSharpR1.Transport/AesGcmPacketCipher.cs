using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KcpCSharpR1.Transport;

/// <summary>
/// AES-256-GCM 패킷 암호화기(P5-1).
/// </summary>
/// <remarks>
/// <para>와이어 포맷: <c>[counter u64][암호문][태그 16B]</c> — 오버헤드 <see cref="OverheadBytes"/>(24) 바이트.</para>
/// <para>
/// nonce(12B) = <c>conv(4B) ‖ counter(8B)</c> 다. 같은 키로 같은 nonce 를 두 번 쓰면 GCM 은 완전히 무너지므로,
/// 방향(클라→서버 / 서버→클라)마다 카운터 공간을 나눈다. 서버 방향은 카운터의 최상위 비트를 1로 시작한다.
/// </para>
/// <para>
/// 리플레이 방지: 64비트 슬라이딩 윈도 비트마스크로 이미 받은 카운터를 기억한다.
/// 윈도보다 오래된 패킷과 중복 패킷은 <see cref="TryDecrypt"/> 가 false 를 돌려준다.
/// </para>
/// <para>
/// <b>키 교부는 라이브러리의 일이 아니다.</b> 게임은 보통 TLS 웹 API 로그인에서 세션 키를 받아 오므로,
/// 그 32바이트를 <see cref="KcpSessionOptions.EncryptionKey"/> 에 넣어 주면 된다.
/// </para>
/// <para><b>스레딩 규약</b>: 세션 1개당 1개, 소유 스레드에서만 사용한다.</para>
/// </remarks>
public sealed class AesGcmPacketCipher : IPacketCipher, IDisposable
{
    /// <summary>암호문이 평문보다 커지는 바이트 수(카운터 8 + 태그 16).</summary>
    public const int OverheadBytes = 8 + 16;

    /// <summary>필요한 키 길이(바이트).</summary>
    public const int KeySize = 32;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly uint conv;
    private readonly bool serverDirection;
    private ulong sendCounter;

    // 리플레이 방지용 슬라이딩 윈도
    private ulong highestSeen;
    private ulong window;
    private bool anyReceived;

#if NET8_0_OR_GREATER
    private readonly AesGcm aes;
#endif

    private bool disposed;

    /// <summary>암호화기를 만든다.</summary>
    /// <param name="key">32바이트 키.</param>
    /// <param name="conv">이 세션의 대화 번호(nonce 에 섞는다).</param>
    /// <param name="serverDirection">서버가 보내는 방향이면 true.</param>
    /// <exception cref="PlatformNotSupportedException">
    /// netstandard2.1 런타임에는 <c>AesGcm</c> 이 없다. 그 경우 <see cref="IPacketCipher"/> 를 직접 구현해
    /// <see cref="KcpSessionOptions.Cipher"/> 에 주입해야 한다.
    /// </exception>
    public AesGcmPacketCipher(byte[] key, uint conv, bool serverDirection)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (key.Length != KeySize)
            throw new ArgumentException($"키는 {KeySize}바이트여야 합니다(현재 {key.Length}).", nameof(key));

        this.conv = conv;
        this.serverDirection = serverDirection;

        // 방향마다 카운터 공간을 나눠 nonce 재사용을 원천 차단한다.
        sendCounter = serverDirection ? 1UL << 63 : 0UL;

#if NET8_0_OR_GREATER
        aes = new AesGcm(key, TagSize);
#else
        throw new PlatformNotSupportedException(
            "AesGcm 은 .NET Core 3.0 이상에서만 제공됩니다. netstandard2.1 소비자는 IPacketCipher 를 직접 구현해 주입하세요.");
#endif
    }

    /// <inheritdoc/>
    public int Overhead => OverheadBytes;

    /// <summary>이 방향에서 다음에 쓸 카운터(테스트·진단용).</summary>
    public ulong NextCounter => sendCounter;

    /// <inheritdoc/>
    public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
#if NET8_0_OR_GREATER
        if (disposed) throw new ObjectDisposedException(nameof(AesGcmPacketCipher));
        if (destination.Length < plaintext.Length + OverheadBytes)
            throw new ArgumentException("대상 버퍼가 작습니다.", nameof(destination));

        var counter = sendCounter++;

        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(nonce, counter);

        BinaryPrimitives.WriteUInt64LittleEndian(destination, counter);
        var ciphertext = destination.Slice(8, plaintext.Length);
        var tag = destination.Slice(8 + plaintext.Length, TagSize);

        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return plaintext.Length + OverheadBytes;
#else
        _ = plaintext;
        _ = destination;
        throw new PlatformNotSupportedException("AesGcm 을 쓸 수 없는 런타임입니다.");
#endif
    }

    /// <inheritdoc/>
    public bool TryDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> destination, out int plaintextLength)
    {
        plaintextLength = 0;
#if NET8_0_OR_GREATER
        if (disposed) return false;
        if (ciphertext.Length < OverheadBytes) return false;

        var counter = BinaryPrimitives.ReadUInt64LittleEndian(ciphertext);

        // 인증하기 전에 리플레이 윈도를 먼저 본다(계산 낭비를 줄인다).
        if (IsReplay(counter)) return false;

        var payloadLength = ciphertext.Length - OverheadBytes;
        if (destination.Length < payloadLength) return false;

        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(nonce, counter);

        var body = ciphertext.Slice(8, payloadLength);
        var tag = ciphertext.Slice(8 + payloadLength, TagSize);

        try
        {
            aes.Decrypt(nonce, body, tag, destination.Slice(0, payloadLength));
        }
        catch (CryptographicException)
        {
            // 인증 실패(변조). 윈도는 갱신하지 않는다.
            return false;
        }

        MarkSeen(counter);
        plaintextLength = payloadLength;
        return true;
#else
        _ = ciphertext;
        _ = destination;
        return false;
#endif
    }

    private void BuildNonce(Span<byte> nonce, ulong counter)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, conv);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), counter);
    }

    /// <summary>이미 받은 카운터이거나 윈도보다 오래됐으면 true.</summary>
    private bool IsReplay(ulong counter)
    {
        // 상대 방향의 카운터 공간이 아니면 거부한다.
        var expectServerHighBit = !serverDirection;   // 내가 클라면 상대(서버)의 카운터는 상위 비트 1
        var hasHighBit = (counter & (1UL << 63)) != 0;
        if (hasHighBit != expectServerHighBit) return true;

        if (!anyReceived) return false;

        if (counter > highestSeen) return false;

        var delta = highestSeen - counter;
        if (delta >= 64) return true;                 // 윈도 밖(너무 오래됨)
        return (window & (1UL << (int)delta)) != 0;   // 이미 본 것
    }

    private void MarkSeen(ulong counter)
    {
        if (!anyReceived)
        {
            anyReceived = true;
            highestSeen = counter;
            window = 1;
            return;
        }

        if (counter > highestSeen)
        {
            var shift = counter - highestSeen;
            window = shift >= 64 ? 1UL : (window << (int)shift) | 1UL;
            highestSeen = counter;
            return;
        }

        var delta = highestSeen - counter;
        if (delta < 64)
            window |= 1UL << (int)delta;
    }

    /// <summary>키 자원을 해제한다.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
#if NET8_0_OR_GREATER
        aes.Dispose();
#endif
    }
}
