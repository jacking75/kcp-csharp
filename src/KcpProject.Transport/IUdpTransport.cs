using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KcpProject.Transport;

/// <summary>
/// UDP 데이터그램 송수신 추상화.
/// </summary>
/// <remarks>
/// <para>
/// 전송 계층(<c>KcpClient</c>, <c>KcpServer</c>)은 실소켓을 직접 알지 못하고 이 인터페이스만 쓴다.
/// 덕분에 테스트에서 손실·지연을 흉내 내는 가상 구현을 그대로 끼워 넣어 핸드셰이크·세션 수명을
/// 실제 네트워크 없이 검증할 수 있다(P4-5).
/// </para>
/// <para><b>스레딩 규약</b>: 수신 루프 1개가 <see cref="ReceiveAsync"/> 를 독점 호출한다.
/// <see cref="SendTo"/> 는 같은 루프 또는 소유 스레드에서 호출한다.</para>
/// </remarks>
public interface IUdpTransport : IDisposable
{
    /// <summary>바인딩된 로컬 주소. 바인딩 전이면 null.</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>로컬 주소에 바인딩한다(서버 또는 고정 포트 클라이언트).</summary>
    /// <param name="localEndPoint">바인딩할 주소. 포트 0 이면 임의 포트.</param>
    void Bind(IPEndPoint localEndPoint);

    /// <summary>데이터그램 하나를 보낸다.</summary>
    /// <param name="data">보낼 바이트.</param>
    /// <param name="remote">수신자 주소.</param>
    /// <returns>보낸 바이트 수.</returns>
    int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote);

    /// <summary>데이터그램 하나를 받는다.</summary>
    /// <param name="buffer">수신 버퍼.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>수신 결과(길이와 송신자 주소).</returns>
    ValueTask<UdpReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IUdpTransport.ReceiveAsync"/> 의 결과.
/// </summary>
public readonly struct UdpReceiveResult
{
    /// <summary>수신한 바이트 수. 0 이면 유효한 데이터그램이 아니다.</summary>
    public int Length { get; }

    /// <summary>보낸 쪽의 주소 키(세션 테이블 조회용, 무할당).</summary>
    public AddressKey Remote { get; }

    /// <summary>결과를 만든다.</summary>
    /// <param name="length">수신 길이.</param>
    /// <param name="remote">송신자 주소 키.</param>
    public UdpReceiveResult(int length, AddressKey remote)
    {
        Length = length;
        Remote = remote;
    }
}
