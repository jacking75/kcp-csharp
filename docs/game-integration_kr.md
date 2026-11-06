# 게임에 KcpCSharpR1 연동하기

[English](game-integration.md) · [저장소](../README_kr.md)

클라이언트/서버 게임에는 `KcpCSharpR1.Transport`를 설치한다. 코어 패키지는 자동으로 따라온다.
이 문서는 현재 체크아웃의 소스를 기준으로 한다. 이전 패키지를 사용한다면
[변경 이력](../CHANGELOG.md)에서 차이를 확인한다.

```bash
dotnet add package KcpCSharpR1.Transport
```

## 연결의 소유 스레드 정하기

세션마다 수신·KCP 갱신·송신을 담당하는 소유자가 하나 있어야 한다. 코어 내부에는 lock이 없다.
옵션은 클라이언트/서버 생성 전에 설정하고 실행 중에는 변경하지 않는다.

| 구동 방식 | 수신·갱신 소유자 | 게임 스레드의 송신 API |
|---|---|---|
| MonoGame 폴링 | 연결 완료 후 `Game.Update` | `session.Send` / `session.SendUnreliable` |
| 클라이언트 자체 루프 | `client.RunAsync` | `session.TrySendFromAnyThread` — 신뢰 채널 전용 |
| 서버 자체 루프 | `server.Start()` | `session.TrySendFromAnyThread` — 신뢰 채널 전용 |
| 서버 수동 루프 | `server.Start(null, runLoop: false)` 후 `server.Pump()` | 같은 루프에서 직접 송신 |

`ConnectAsync` 또는 `RunAsync`와 `Update`를 동시에 실행하지 않는다. 서버 자체 루프가 돌 때
별도로 `Pump`를 호출하지 않는다. `async`는 특정 스레드를 보장하지 않는다. 직접 구독한 이벤트는
네트워크 소유자가 호출하며, `ConnectAsync`가 완료되기 전에도 호출될 수 있다.

### MonoGame: 연결 완료 후 게임 루프로 인계

`ConnectAsync`는 핸드셰이크를 자체 구동한다. 로딩 화면에서는 다음 순서를 사용한다.

1. 클라이언트 생성 후 `EnableEventQueue`로 큐를 먼저 만든다. 게임 상태를 바꾸는 핸들러를
   클라이언트의 직접 이벤트에 등록하지 않는다.
2. `ConnectAsync(endpoint, cancellationToken)`를 한 번 시작하고 태스크를 보관한다.
   `Game.Update`에서 연결 완료를 동기 대기하지 않는다.
3. 태스크가 진행 중이면 로딩 화면만 갱신한다. `client.Update`, 송신, 설정 변경은 하지 않는다.
4. 게임 스레드에서 `IsCompleted`를 확인한 뒤 `GetAwaiter().GetResult()`로 결과를 관측한다.
   완료를 확인했으므로 여기서는 대기하지 않는다. 연결 실패를 처리하고, 성공했다면 세션을
   보관한 뒤 같은 게임 스레드에서 게임 참가 메시지를 보낸다.
5. 이후 매 프레임 `client.Update()` → 이벤트 큐 소비 → 입력 송신 순서로 구동한다.
   이때부터 게임 스레드가 소유자다. `RunAsync`를 추가로 시작하지 않는다.

폴링 방식에서도 큐를 사용하는 이유는 핸드셰이크 도중 첫 게임 메시지가 도착할 수 있기 때문이다.
큐가 이 메시지를 보관하므로 작업자 스레드에서 게임 상태를 변경하지 않는다.
실제 구현은 [PollingKcpClient.cs](../samples/Common/PollingKcpClient.cs), 사용 예는
[MoveSyncGame.cs](../samples/04-MoveSync/Client/MoveSyncGame.cs)의 `Initialize`, `OnConnected`,
`Update`를 참고한다. Gomoku·MoveSync·TankBattle이 같은 어댑터를 공유한다.

큐는 소비자 스레드 하나에서 읽는다. 파싱 중 예외가 나도 페이로드를 반환한다.

```csharp
while (events.TryRead(out var ev))
{
    try
    {
        // Connected, Disconnected, Reliable, Unreliable을 여기서 처리한다.
        // ev.Data는 동기적으로 파싱하고, 이벤트 수명 밖에 보관할 데이터만 복사한다.
    }
    finally
    {
        ev.ReturnPayload();
    }
}
```

로딩 화면에서 취소하면 토큰을 취소하고 연결 태스크의 종료를 관측한 뒤 클라이언트를 폐기한다.
`OperationCanceledException`, `TimeoutException`, 소켓/DNS 오류는 화면에서 구분해서 처리한다.
연결 태스크를 fire-and-forget으로 버리지 않는다.

### 백그라운드 네트워크

생성 전에 `SendOutboxCapacity`를 설정하고, 실행 전에 이벤트 큐를 활성화한다.
연결 성공 후 `client.RunAsync(token)`의 태스크를 보관한다. 이 방식에서는 `client.Update`를
호출하지 않는다. 게임 스레드는 이벤트 큐를 소비하고 `TrySendFromAnyThread`로 신뢰 메시지만 넣는다.
큐를 켜도 직접 이벤트는 계속 발생하므로, 양쪽에서 같은 메시지를 중복 처리하지 않도록 한다.

`SendUnreliable`, `Disconnect`, 일반 세션 API는 다른 스레드에서 호출하는 명령 API가 아니다.
백그라운드 방식에서 비신뢰 송신이 필요하면 애플리케이션의 제한된 명령 큐를 네트워크 소유자가
소비하게 하거나, 폴링 방식을 사용한다. `KcpEvent.Session` 참조를 받았다고 소유권이 넘어오지는 않는다.

권위 서버는 시뮬레이션 스레드에서 직접 `Pump()`를 구동할 수도 있다. KCP 갱신 간격을 만족하도록
자주 호출하고, DB·디스크 접근 등 블로킹 작업을 해당 스레드에서 실행하지 않는다.

## 전송 결과, 백프레셔, 버퍼 수명

| API | 성공의 의미 | 호출자 책임 |
|---|---|---|
| `Send` | 로컬 KCP 송신 경로에 수락됨 | `KcpSendResult` 확인. 상대의 ACK가 아님 |
| `SendUnreliable` | UDP 송신 경로에 제출됨 | 손실·중복·순서 역전 처리 |
| `TrySendFromAnyThread` | 제한된 로컬 아웃박스에 복사됨 | 반환값 확인. 전달 완료가 아님 |
| `ReliableReceived` / `UnreliableReceived` | 동기 콜백 | 메모리는 콜백 동안만 유효 |
| `ReliableReceivedSpan` / `UnreliableReceivedSpan` | 동기 Span 콜백 | 즉시 파싱. 기반 버퍼 보관 금지 |
| `KcpEventQueue.TryRead` | 이벤트 한 건을 소비자에게 넘김 | 받은 값에 `finally`에서 `ReturnPayload` 호출 |

`KcpEvent` 구조체를 복사해서 양쪽 사본으로 버퍼를 반환하지 않는다. 반환 후 `ev.Data`를
보관하지 않는다. 송신 API는 반환 전에 입력을 복사하거나 소비하므로 호출자 송신 버퍼는 재사용할 수 있다.

`MaxSendBacklog`의 단위는 KCP **세그먼트**다. `SendOutboxCapacity`와 수신 큐 용량의 단위는
**메시지/이벤트 수**이며 바이트가 아니다. 큐 개수와 애플리케이션 메시지 크기를 함께 제한한다.
`TooLarge`, `Backpressure`, `NotConnected`를 처리한다. 필수 명령은 제한된 애플리케이션 큐에서
재시도하고, 대체 가능한 상태는 최신 값으로 합쳐 오래된 스냅숏이 계속 쌓이지 않게 한다.

게임 상태를 전달하는 큐는 중요 이벤트가 누락될 때 해당 세션을 종료하는 정책을 명시한다.

```csharp
var events = server.EnableEventQueue(4096,
    KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
// KcpClient 에도 같은 오버로드가 있다.
```

중요 이벤트는 신뢰 메시지와 연결·종료 이벤트다. 신뢰 메시지나 연결 이벤트를 넣지 못하면
해당 세션에 FIN 종료를 요청한다. 비신뢰 메시지는 버리기만 하며, 종료 이벤트 누락은 이미 끝난
세션을 다시 종료하지 않고 계수한다. **큐가 가득 찬 상태에서는 종료 이벤트도 전달을 보장할 수 없다.**
`HasCriticalOverflow`와 `DroppedReliable`, `DroppedConnected`, `DroppedDisconnected`의 변화를
확인해 실제 세션 상태와 게임 상태를 다시 맞춘다. 누계는 큐를 비우거나 서버를 재시작해도 유지된다.
`DroppedUnreliable`은 스냅숏 손실, `Dropped`는 전체 누락 수다. 기존 인수 하나짜리 오버로드는
호환성을 위해 중요 이벤트도 버리고 연결을 유지하는 `DropNewest` 정책을 사용한다.

샘플 어댑터는 종류에 관계없이 이벤트가 누락되면 오류를 보고하고 연결을 종료한다.
`OutboxRejected`, RTT, 재전송률, 게임 루프 정지도 관측한다. 소비 처리량을 확인한 뒤 용량을 조정한다.

## 명령과 스냅숏 구분

참가·퇴장, 채팅, 아이템 변경, 경기 결과는 신뢰 순서 보장 채널을 사용한다. 신뢰 채널에는
head-of-line 블로킹이 있다. 조각 하나가 누락되면 같은 세션의 이후 신뢰 메시지가 대기하므로
대용량 에셋 다운로드를 섞지 않는다. 세션이 죽으면 전달 보장도 끝난다. 영속 거래에는
애플리케이션 ACK와 멱등 요청 ID가 필요하다.

대체 가능한 스냅숏과 반복 입력 상태는 비신뢰 채널을 사용한다. 최신성·순서는 자동 보장되지 않는다.
메시지에 엔티티/틱 또는 시퀀스를 넣고 중복·과거 값을 버리며, 새 세션에서는 추적 상태를 초기화한다.
32비트 부호 없는 시퀀스의 일반적인 비교는 다음과 같다.

```csharp
static bool IsNewer(uint candidate, uint previous)
    => unchecked((int)(candidate - previous)) > 0;
```

시퀀스 거리가 32비트 범위의 절반보다 작다는 전제가 필요하다. 첫 값은 별도로 수락한다.
“한 번 발사” 같은 입력 에지는 신뢰 전송이나 애플리케이션의 중복 전송/ACK가 필요하다.
두 채널 사이에는 순서 보장이 없다. 신뢰 spawn 메시지보다 스냅숏이 먼저 올 수 있으므로
식별자·세대 정보를 넣고 이 상황을 처리한다.

비신뢰 페이로드는 프레이밍·암호화 오버헤드를 뺀 MTU 안에 유지한다. 이 전송 계층은 비신뢰
메시지를 분할하지 않으며, 초과 시 `SendUnreliable`이 `TooLarge`를 반환한다.
[MoveSync](../samples/04-MoveSync/README.md)는 보간,
[TankBattle](../samples/05-TankBattle/README.md)은 예측·서버 보정 예제다.

## 종료와 재접속

폴링 방식에서는 소유 스레드에서 `Disconnect`를 호출한 뒤 세션이 닫히거나 애플리케이션의 종료
제한 시간이 지날 때까지 갱신한다. FIN 요청은 상대가 대기 중 게임 메시지를 소비했다는 뜻이 아니다.
`Dispose`는 로컬 자원 정리이며 정상 종료 ACK가 아니다.

백그라운드 클라이언트는 송신 생산자를 멈추고 루프 토큰을 취소한 다음 루프 태스크가 끝날 때까지
기다린 후 직접 세션 API나 Dispose를 호출한다. 정상 종료가 필요하다면 소유권을 넘겨받은 쪽에서
`Disconnect` 후 linger 동안 수동 갱신을 이어 갈 수 있다. 호스트 폐기 전에 이벤트 소비자도 멈춘다.
서버는 게임 생산자·소비자를 멈춘 다음 네트워크 콜백 바깥에서 `Stop`/`Dispose`를 호출한다.
네트워크 콜백을 무기한 블로킹하면 서버 종료도 기다리게 되므로 콜백을 짧게 유지한다.

재접속마다 새 클라이언트를 만들면 소유권 구분이 간단하다. 이전 세션 참조를 버리고 시퀀스를
초기화하며 재인증 후 서버의 권위 상태를 요청한다. 전송 계층 재접속이 게임 참가 상태나 거래를
자동 복구하지는 않는다.

## 타깃과 기능의 경계

- 빌드 타깃은 `net8.0`, `netstandard2.1`이다. 코어에는 패키지 의존성이 없다.
  Transport의 `netstandard2.1`은 `System.Threading.Channels` 패키지를 참조하며,
  `net8.0`은 프레임워크 구현을 사용한다.
- 제공 그래픽 클라이언트는 MonoGame 기반이다. .NET Standard 타깃은 모든 엔진·모바일·콘솔·AOT
  환경의 검증을 뜻하지 않는다. 실제 타깃에서 소켓·스레드·패키징을 확인해야 한다.
  기본 전송 계층은 네이티브 UDP 소켓을 사용하며, 이 저장소에는 브라우저/WebGL 전송 구현이 없다.
- 내장 `AesGcmPacketCipher`는 `net8.0` 빌드에서 구현된다. `netstandard2.1` 빌드에서는
  `PlatformNotSupportedException`이 발생하므로 암호화가 필요하면 호환 `IPacketCipher`를 구현해야 한다.
- 사용자 정의 암호화는 세션 옵션의 `CipherFactory`와 `CipherFactoryOverhead`를 지정한다.
  팩터리는 conversation ID와 송신 방향을 받아 세션마다 새 암호화기를 만들어야 하며,
  실제 오버헤드는 선언한 값과 같아야 한다. 세션이 암호화기를 소유하고 폐기한다.
  서버는 nonce·리플레이 상태가 세션 간 공유되는 것을 막기 위해 직접 지정한 `Session.Cipher`를
  거부한다. 내장 .NET 8 서버 암호화는 `EncryptionKey`를 사용한다.
- FEC와 암호화를 동시에 켤 수 없다. 키 교부, 플레이어 인증·인가, 매치메이킹, NAT 통과는
  애플리케이션 책임이다. 쿠키 핸드셰이크가 플레이어를 인증하지는 않는다.
- 내장 AES-GCM의 nonce에는 conversation ID가 들어간다. 서버 인스턴스마다 ID가 다시 시작하므로
  **서버를 새로 시작할 때 새 EncryptionKey를 사용하고, 서로 다른 서버 인스턴스에 키를 공유하지 않는다.**
  고정 게임 공용 키를 재사용하면 같은 키/nonce 조합이 반복될 수 있다.
- `RawMode`는 원본 KCP 연동을 위한 **Transport 옵션**이다. 핸드셰이크·keepalive·FIN·비신뢰
  채널이 없으며, 양쪽에서 conversation ID와 프로토콜 설정을 맞춰야 한다.

출시 전 실제 게임 프로토콜로 손실·순서 역전, 게임 루프 정지, 큐 포화, 연결 취소, 재접속을 검증한다.
[결정적 시뮬레이션 하네스](../tests/KcpCSharpR1.SimTests)와
[LoadTest](../samples/tools/LoadTest/README.md)를 출발점으로 사용할 수 있다.
