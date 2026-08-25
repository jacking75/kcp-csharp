# C#과 KCP로 배우는 실시간 게임 네트워킹

> UDP 위에 필요한 만큼의 신뢰성을 얹어, 끊기지 않고 튀지 않는 게임을 만드는 법.

이 책은 **[kcp-csharp](https://github.com/jacking75/kcp-csharp)** 라이브러리를 만들면서 정리한
실시간 게임 네트워킹 교재입니다. 이론만 늘어놓지 않고, **실제로 돌아가는 코드**로 설명합니다.
책에 나오는 모든 코드는 저장소의 `samples/` 에 있는 진짜 예제에서 가져온 것이며,
각 코드 블록 위에 원본 경로가 적혀 있습니다.

## 이 책이 답하는 질문

- TCP로 게임을 만들면 왜 렉이 생기나요? UDP를 쓰면 뭐가 다른가요?
- 패킷이 사라졌는지 어떻게 알고, 어떻게 다시 보내나요?
- 캐릭터 위치를 신뢰 채널로 보내면 왜 오히려 끊겨 보이나요?
- 방향키를 눌렀는데 0.1초 뒤에 움직이는 문제는 어떻게 없애나요?
- "분명히 맞췄는데 안 맞았다"는 왜 생기고, 어떻게 고치나요?

## 읽는 순서

처음이라면 **4장 → 9장 → 10장** 순서로 손부터 움직여 보세요.
돌아가는 것을 본 뒤에 1~3장으로 돌아오면 훨씬 잘 읽힙니다.

| 이런 분 | 이렇게 |
|---|---|
| **일단 돌려 보고 싶다** | 4장(시작하기) → 9장(에코) → 10장(채팅) |
| **원리부터 알고 싶다** | 1장 → 2장 → 3장 → 5장 |
| **실시간 동기화가 급하다** | 7장(채널) → 12장(이동 동기화) → 13장(예측·보정) |
| **서버를 운영해야 한다** | 6장(세션) → 8장(튜닝) → 14장(성능) → 17장(운영) |
| **보안이 걱정된다** | 15장(보안) → 16장(FEC) |

## 3부와 샘플은 1:1로 대응합니다

| 장 | 샘플 | 난이도 | 배우는 것 |
|---|---|---|---|
| 9장 | `samples/01-Echo` | ★1 | 연결·송수신·종료의 최소 사이클 |
| 10장 | `samples/02-Chat` | ★2 | 애플리케이션 프로토콜 설계 |
| 11장 | `samples/03-Gomoku` | ★3 | 권위 서버, 재접속, MonoGame 입문 |
| 12장 | `samples/04-MoveSync` | ★4 | 고정 틱, 채널 분리, 스냅숏 보간 |
| 13장 | `samples/05-TankBattle` | ★5 | 예측·보정, 지연 보상 |

## 30초 만에 확인하기

```bash
git clone https://github.com/jacking75/kcp-csharp
cd kcp-csharp
dotnet run --project samples/01-Echo/Server     # 터미널 1
dotnet run --project samples/01-Echo/Client     # 터미널 2
```

```csharp
// 서버 — 이게 전부입니다
using var server = new KcpServer(27001);
server.ReliableReceived += (session, data) => session.Send(data.Span);   // 에코
server.Start();
```

```csharp
// 클라이언트
using var client = new KcpClient();
var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 27001));
session.Send(Encoding.UTF8.GetBytes("Hello KCP"));

while (true) { client.Update(); await Task.Delay(10); }   // 게임 루프에서 매 프레임
```

## 준비물

- **.NET 8 SDK** — 라이브러리는 `netstandard2.1` / `net8.0` 멀티타깃입니다.
- **MonoGame** — 11~13장의 클라이언트에만 필요합니다(NuGet 자동 복원).
- 손실을 흉내 낼 도구(선택) — Windows [clumsy](https://jagt.github.io/clumsy/), Linux `tc netem`.

## 이 책의 약속

1. **모든 코드는 실행됩니다.** 발췌한 원본 경로를 코드 블록마다 적어 두었습니다.
   라이브러리 API가 바뀌면 샘플이 깨지고, 샘플이 깨지면 이 책도 고쳐야 합니다 — 일부러 그렇게 묶었습니다.
2. **숫자는 실측입니다.** 성능 수치는 [벤치마크 기록](benchmarks.md)에 재현 명령어와 함께 있습니다.
3. **실패도 적습니다.** 만들면서 빠졌던 함정(측정 실수, 잘못된 가정)을 지우지 않고 남겼습니다.

## 라이선스와 계보

이 라이브러리는 [skywind3000/kcp](https://github.com/skywind3000/kcp)(C)와
[xtaci/kcp-go](https://github.com/xtaci/kcp-go)(Go)의 계보를 잇는 C# 구현입니다.
코어의 24바이트 헤더는 원본과 **바이트 단위로 호환**되므로, 원본 kcp 서버와도 통신할 수 있습니다(부록 C).

MIT 라이선스.
