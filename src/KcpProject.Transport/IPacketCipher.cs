using System;

namespace KcpProject.Transport;

/// <summary>
/// 패킷 단위 암호화기(P5-1).
/// </summary>
/// <remarks>
/// <para>
/// KCP(0x04)·RAW(0x05) 프레임의 <b>페이로드만</b> 암호화한다. 프레임 타입 바이트는 평문으로 남아
/// 수신측이 복호화 전에 어떤 프레임인지 판단할 수 있다.
/// </para>
/// <para>
/// 라이브러리는 키 합의를 하지 않는다. 게임은 보통 TLS 웹 API 로그인에서 세션 키를 받아 오므로,
/// 그 키를 <see cref="KcpSessionOptions.Cipher"/> 에 주입해 쓴다.
/// netstandard2.1 소비자는 이 인터페이스에 자체 구현을 넣으면 된다.
/// </para>
/// </remarks>
public interface IPacketCipher
{
    /// <summary>암호문이 평문보다 커지는 최대 바이트 수(nonce + 인증 태그).</summary>
    int Overhead { get; }

    /// <summary>평문을 암호화한다.</summary>
    /// <param name="plaintext">평문.</param>
    /// <param name="destination">암호문 대상(<c>plaintext.Length + Overhead</c> 이상).</param>
    /// <returns>기록한 바이트 수.</returns>
    int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination);

    /// <summary>암호문을 복호화한다.</summary>
    /// <param name="ciphertext">암호문.</param>
    /// <param name="destination">평문 대상.</param>
    /// <param name="plaintextLength">복원한 평문 길이.</param>
    /// <returns>
    /// 성공하면 true. 인증 실패(변조)나 리플레이면 false 를 반환하며, 이때 패킷은 조용히 버려야 한다.
    /// </returns>
    bool TryDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> destination, out int plaintextLength);
}
