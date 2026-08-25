# KcpCSharpR1

**UDP 위에 필요한 만큼의 신뢰성을 얹는 C# 라이브러리.** 실시간 게임을 위한 KCP 구현입니다.

> [skywind3000/kcp](https://github.com/skywind3000/kcp)(C) → [xtaci/kcp-go](https://github.com/xtaci/kcp-go)(Go) 를 잇는
> C# 이식·재구성 프로젝트입니다. 자세한 계보는 [원출처](#원출처) 절을 보세요.

![net8.0](https://img.shields.io/badge/net-8.0-512BD4)
![netstandard2.1](https://img.shields.io/badge/netstandard-2.1-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

📖 **[온라인 책: C#과 KCP로 배우는 실시간 게임 네트워킹](https://jacking75.github.io/kcp-csharp/)**

---

## KCP 란

TCP는 모든 바이트가 **순서대로** 도착함을 보장합니다. 파일 전송에는 완벽하지만,
게임에서는 이 보장이 문제가 됩니다. 패킷 하나가 사라지면 **그 뒤에 도착한 최신 데이터까지 막힙니다**
(head-of-line 블로킹). 캐릭터가 0.25초 멈췄다가 순간이동하는 이유입니다.

KCP는 UDP 위에서 **필요한 곳에만** 신뢰성을 얹습니다.

| 채널 | 보장 | 쓰는 곳 |
|---|---|---|
| **신뢰**(`Send`) | 도착 + 순서 | 채팅, 아이템, 피격 판정 |
| **비신뢰**(`SendUnreliable`) | 없음 | 위치 스냅숏, 입력 |

그리고 TCP보다 공격적으로 복구합니다 — RTO 백오프 1.5배(TCP는 2배), 최소 RTO 30ms(TCP는 200ms+),
빠른 재전송 + 조기 재전송. **대역폭을 조금 더 쓰고 지연을 얻는 거래**입니다.

## 30초 시작

```bash
git clone https://github.com/jacking75/kcp-csharp
cd kcp-csharp
dotnet run --project samples/01-Echo/Server     # 터미널 1
dotnet run --project samples/01-Echo/Client     # 터미널 2
```

### 서버

```csharp
using KcpCSharpR1.Transport;

using var server = new KcpServer(27001);

server.SessionConnected += s => Console.WriteLine($"접속 {s.RemoteEndPoint}");
server.ReliableReceived += (s, data) => s.Send(data.Span);            // 에코
server.SessionDisconnected += (s, why) => Console.WriteLine($"종료 {why}");

server.Start();
Console.WriteLine("UDP 27001 에서 대기 중입니다. Ctrl+C 로 종료합니다.");

// Start() 는 배경 스레드를 띄우고 바로 반환한다. 여기서 붙잡아 두지 않으면 프로그램이 끝난다.
using var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
quit.Wait();

server.Stop();
```

전체 코드는 [samples/01-Echo/Server/Program.cs](samples/01-Echo/Server/Program.cs) 에 있습니다.

### 클라이언트

```csharp
using var client = new KcpClient();
client.ReliableReceived += (_, data) => Console.WriteLine(Encoding.UTF8.GetString(data.Span));

var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 27001));
session.Send(Encoding.UTF8.GetBytes("Hello KCP"));

while (true)          // 게임 루프에서 매 프레임
{
    client.Update();  // ★ 이것을 안 부르면 아무 일도 일어나지 않습니다
    await Task.Delay(10);
}
```

## 기능

| | |
|---|---|
| **신뢰 / 비신뢰 두 채널** | 데이터마다 골라 쓰는 것이 이 라이브러리의 핵심 |
| **무상태 쿠키 핸드셰이크** | HMAC 기반. SYN 홍수로 서버 메모리를 채울 수 없음 |
| **세션 수명 관리** | keepalive(PING/PONG), 타임아웃, FIN 정상 종료, 데드링크 감지 |
| **AES-256-GCM 암호화** | 인증 암호화 + 64비트 리플레이 윈도 |
| **Reed-Solomon FEC** | kcp-go 호환. 손실 20%에서 재전송 −70% |
| **무할당 설계** | 정상 상태 송수신 경로 **0 B/op**(벤치마크로 확인) |
| **최소 힙 스케줄러** | 세션 1만 개에서도 기한이 된 것만 갱신 |
| **관측성** | 세션 수·재전송률·RTT p50/p95/p99 주기 스냅숏 |
| **외부 의존성 0** | 코어·전송 계층 모두. 샘플의 MonoGame 만 예외 |
| **원본 kcp 와 와이어 호환** | RawMode 로 kcp-go 서버와 직접 통신 |

## 성능

같은 PC에서 클라·서버를 함께 돌린 실측입니다. 재현 명령어는 [docs/benchmarks.md](docs/benchmarks.md)에 있습니다.

| 항목 | 수치 |
|---|---|
| 1KB × 1,000 왕복 | **321 µs** (개선 전 441 µs, −27%) |
| 정상 상태 할당 | **0 B/op** |
| 1,000 세션 · 60초 | 970,627건 **전량 왕복**, 불일치 0, 재전송 1.13% |
| 300 세션 · 60초 | 재전송 **0.00%**, RTT p99 **48.5ms**, Gen2 **0회** |
| 암호화 비용 | 패킷당 **0.36 µs** (1,376B 기준) |

## 샘플

난이도 순서대로 다섯 개 + 도구 하나. 각 샘플에 자체 README가 있습니다.

| 샘플 | 난이도 | 배우는 것 |
|---|---|---|
| [01-Echo](samples/01-Echo) | ★1 | 연결·송수신·종료의 최소 사이클 |
| [02-Chat](samples/02-Chat) | ★2 | 애플리케이션 프로토콜 설계, 서버 측 검증 |
| [03-Gomoku](samples/03-Gomoku) | ★3 | 권위 서버, 재접속, MonoGame 입문 |
| [04-MoveSync](samples/04-MoveSync) | ★4 | 고정 틱, 채널 분리, 스냅숏 보간 |
| [05-TankBattle](samples/05-TankBattle) | ★5 | 클라 예측·서버 보정, 지연 보상 |
| [tools/LoadTest](samples/tools/LoadTest) | — | 부하 측정(처리량·재전송률·RTT·GC) |

## 온라인 책

**[C#과 KCP로 배우는 실시간 게임 네트워킹](https://jacking75.github.io/kcp-csharp/)** — 17장 + 부록 5종.

- **1부 원리** — 게임과 네트워크, ARQ, KCP 프로토콜 해부
- **2부 사용법** — 시작하기, 코어 API, 세션, 채널 선택, 파라미터 튜닝
- **3부 실전** — 위 샘플 5종과 1:1 대응
- **4부 심화** — 무할당 성능, 보안, FEC, 운영

책의 모든 코드는 `samples/` 의 실제 코드에서 발췌했고, **원본 경로가 코드 블록마다 적혀 있습니다.**

## 설치

```bash
# NuGet (배포 후)
dotnet add package KcpCSharpR1
dotnet add package KcpCSharpR1.Transport

# 소스 참조
dotnet add reference path/to/kcp-csharp/src/KcpCSharpR1/KcpCSharpR1.csproj
dotnet add reference path/to/kcp-csharp/src/KcpCSharpR1.Transport/KcpCSharpR1.Transport.csproj
```

| 타깃 | 용도 |
|---|---|
| `net8.0` | 서버, MonoGame 클라이언트 |
| `netstandard2.1` | Godot C#, 구형 런타임 |

## 저장소 구조

```
src/
  KcpCSharpR1/            프로토콜 코어(의존성 0)
  KcpCSharpR1.Transport/  소켓·세션·핸드셰이크·암호화·FEC
tests/
  KcpCSharpR1.Tests/      단위 테스트
  KcpCSharpR1.SimTests/   가상 네트워크 시나리오 + 실소켓 통합
benchmarks/              BenchmarkDotNet
samples/                 샘플 5종 + LoadTest
docs/                    온라인 책 + 벤치마크 기록 + 로드맵
```

## 빌드와 테스트

```bash
dotnet build -c Release              # 경고 0 유지
dotnet test  -c Release              # 207개
dotnet test  -c Release --filter "Category!=Soak"   # 장시간 테스트 제외
dotnet run   -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*'
```

## 원출처

이 저장소는 원본을 그대로 가져온 것이 아니라, 아래 계보를 따라 **C# 로 이식·재구성**한 결과물입니다.

[skywind3000/kcp](https://github.com/skywind3000/kcp)(C, MIT) →
[xtaci/kcp-go](https://github.com/xtaci/kcp-go)(Go, MIT) → **KcpCSharpR1**(C#, 이 저장소).

| 이어받은 것 | 새로 만든 것 |
|---|---|
| 프로토콜 로직(ARQ, 슬라이딩 윈도, RTO 계산, 혼잡 제어) — kcp-go 포팅 기반 | 전송 계층(핸드셰이크, 세션 수명, 스케줄러) |
| 코어 24바이트 헤더 — **원본과 바이트 단위로 호환** | AES-256-GCM 암호화, Reed-Solomon FEC |
| | 샘플 5종 + LoadTest 도구 |
| | 온라인 책 17장 + 부록 5종 |

프로토콜 로직은 C# 관용구(`Span<byte>`, `ArrayPool`, 이벤트 기반 세션, 주입 가능한 시계)로 재작성했지만
와이어 포맷은 그대로 유지했습니다. `RawMode` 를 켜면 원본 kcp / kcp-go 서버와 직접 통신할 수 있습니다
([부록 C](docs/book/appendix-c-interop.md)).

## 기여

[CONTRIBUTING.md](CONTRIBUTING.md)를 봐 주세요.
버그 리포트에는 재현 가능한 테스트를 함께 주시면 가장 좋습니다 —
가상 시간 하네스(`tests/KcpCSharpR1.SimTests/KcpPair.cs`)로 결정적인 재현이 가능합니다.

## 라이선스

MIT. [LICENSE](LICENSE)를 보세요.
