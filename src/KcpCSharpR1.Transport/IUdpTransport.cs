using System;
using System.Net;

namespace KcpCSharpR1.Transport;

/// <summary>
/// UDP 데이터그램 송수신 추상화.
/// </summary>
/// <remarks>
/// <para>
/// 전송 계층(<c>KcpClient</c>, <c>KcpServer</c>)은 실소켓을 직접 알지 못하고 이 인터페이스만 쓴다.
/// 덕분에 테스트에서 손실·지연을 흉내 내는 가상 구현을 그대로 끼워 넣어 핸드셰이크·세션 수명을
/// 실제 네트워크 없이 검증할 수 있다(P4-5).
/// </para>
/// <para>
/// <b>동기 폴링 모델</b>이다. 수신 루프 1개가 <see cref="WaitForData"/> 로 잠들었다가
/// <see cref="TryReceive"/> 로 도착한 패킷을 모두 빨아들이고, 같은 루프에서 세션 타이머를 돌린다.
/// 소켓 I/O 와 세션 상태 변경이 한 스레드에 묶이므로 코어의 "인스턴스 1개 = 소유 스레드 1개"
/// 규약(로드맵 2.3절)이 자연스럽게 지켜진다.
/// </para>
/// <para>
/// 호출당 1패킷 제한을 두지 않는다. <see cref="TryReceive"/> 가 false 를 돌려줄 때까지
/// 반복 호출해 커널 버퍼를 비우는 것이 정상 사용법이다(진단 PF-08).
/// </para>
/// </remarks>
public interface IUdpTransport : IDisposable
{
    /// <summary>바인딩된 로컬 주소. 바인딩 전이면 null.</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>로컬 주소에 바인딩한다.</summary>
    /// <param name="localEndPoint">바인딩할 주소. 포트 0 이면 임의 포트.</param>
    void Bind(IPEndPoint localEndPoint);

    /// <summary>데이터그램 하나를 보낸다.</summary>
    /// <param name="data">보낼 바이트.</param>
    /// <param name="remote">수신자 주소.</param>
    /// <returns>보낸 바이트 수. 실패하면 0(UDP 이므로 개별 실패는 상위 재전송이 흡수한다).</returns>
    int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote);

    /// <summary>도착해 있는 데이터그램 하나를 꺼낸다(블로킹하지 않는다).</summary>
    /// <param name="buffer">수신 버퍼.</param>
    /// <param name="received">수신한 바이트 수.</param>
    /// <param name="remote">보낸 쪽 주소 키.</param>
    /// <returns>패킷을 하나 꺼냈으면 true, 지금 도착한 것이 없으면 false.</returns>
    bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote);

    /// <summary>데이터가 도착할 때까지(또는 시간이 다 될 때까지) 기다린다.</summary>
    /// <param name="microseconds">최대 대기 시간(마이크로초). 0 이면 즉시 반환.</param>
    /// <returns>읽을 데이터가 있을 가능성이 있으면 true.</returns>
    bool WaitForData(int microseconds);
}
