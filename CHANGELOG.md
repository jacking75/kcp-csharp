# 변경 이력

이 파일의 형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를 따르며,
버전은 [유의적 버전(SemVer)](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

### 추가됨

- **무복사 수신(G-2)** — `Kcp.RecvInPlace(InPlaceMessageHandler)` 와
  `ReliableReceivedSpan` / `UnreliableReceivedSpan` 이벤트(`KcpSession`·`KcpServer`·`KcpClient`).
  `ReadOnlyMemory` 이벤트 구독자가 없으면 KCP 세그먼트를 복사·대여 없이 그대로 전달합니다.
- **스레드 경계 API** — `KcpSession.TrySendFromAnyThread` + `KcpSessionOptions.SendOutboxCapacity`(M-1),
  `KcpEventQueue` / `KcpEvent` + `KcpServer.EnableEventQueue()` · `KcpClient.EnableEventQueue()`(M-2).
- `IUdpTransport.SendTo(ReadOnlySpan<byte>, AddressKey)` 와 `TryReceive(byte[], …)` 오버로드(G-1·C-1).
  둘 다 기본 구현이 있어 기존 구현체를 깨지 않습니다.
- `AddressKey.ScopeId` / `IsIPv4` / `WriteAddressBytes`.

### 변경됨

- **송신 경로 무할당화(G-1)** — `UdpSocketTransport` 가 `IPEndPoint → SocketAddress` 직렬화를
  세션당 1회만 하고, 핸드셰이크 쿠키 응답은 재사용 `SocketAddress` 로 보냅니다.
- **암호화 세션 무할당화(G-3)** — 패킷마다의 `ArrayPool` 대여 대신 세션 상주 복호화 버퍼.
- **FEC 무할당화(G-4)** — 디코더가 그룹 샤드 버퍼를 미리 잡아 재활용하고,
  `ReedSolomon.Reconstruct` 가 스크래치를 재사용합니다(복원 5,896 B → **0 B**).
  내부 API 시그니처가 `Reconstruct(byte[][], bool[], int)` 로 바뀌었습니다(`internal`).
- **비동기 대기 개선(A-1·A-3)** — `ConnectAsync` 의 1ms `Task.Delay` 폴링 제거,
  `RunAsync` 의 고정 1ms 대기를 다음 갱신 시각 기반(상한 5ms)으로 변경.
- `netstandard2.1` 타깃의 `KcpCSharpR1.Transport` 가 `System.Threading.Channels` 를 참조합니다
  (net8.0 은 BCL 내장이라 추가 의존성 없음, 코어는 여전히 의존성 0).

### 고쳐짐

- `Kcp.Recv` 가 rcv_buf → rcv_queue 이동 시 큐 길이를 이중으로 세어 **실효 수신 윈도가 절반**이었습니다.
  `parse_data` 와 같은 kcp-go 방식으로 통일했습니다.
- 수신 콜백 안에서 세션을 끊으면 `KcpSession.DrainReliable` 이 `NullReferenceException` 을
  던질 수 있었습니다.
- FEC 송·수신 경로의 람다가 **패킷마다** 클로저를 할당하고 있었습니다.

### 알려진 미완

- nuget.org 실제 게시(API 키 필요, 별도 결정 사항) — 패키징 자체는 완료, 로컬 `dotnet pack` 검증 완료
- CI(GitHub Actions), 릴리스 자동화, `master` 브랜치 보호 — 푸시 토큰에 `workflow` 스코프가 없어 보류.
  자세한 사유는 `docs/kcp-csharp-roadmap.md` 의 P8-1·P8-2 절 참고

## [0.10.0] - 2026-08-25

라이브러리·전송 계층·보안·FEC·테스트·벤치마크·샘플 5종·온라인 책·NuGet 패키징까지 갖춘 첫 배포 대상
버전입니다. 아직 API 가 굳어졌다고 보기엔 이르다고 판단해 `1.0.0` 대신 `0.10.0` 으로 낮춰서 시작합니다.

### 추가됨

- **패키징(P8-1)** — `KcpCSharpR1`, `KcpCSharpR1.Transport` 두 NuGet 패키지. MIT 라이선스, README·XML
  문서 동봉, `net8.0`/`netstandard2.1` 양쪽 타깃, Transport → 코어 자동 의존성. 심볼 패키지(`.snupkg`)
  포함

- **전송 계층** `KcpCSharpR1.Transport`
  - `KcpClient` / `KcpServer` / `KcpSession` — 핸드셰이크·keepalive·정상 종료를 포함한 세션 관리
  - 무상태 SYN 쿠키 핸드셰이크(HMAC-SHA256, 30초 타임버킷) — 상태 고갈 공격 방어
  - 신뢰(KCP) / 비신뢰(RAW) 두 채널
  - 프레임 8종(SYN / SYN_COOKIE / SYN_ACK / KCP / RAW / PING / PONG / FIN)
  - `RawMode` — 프레이밍 없이 원본 kcp / kcp-go 서버와 상호운용
  - 최소 힙 세션 스케줄러(lazy deletion) — 기한이 된 세션만 갱신
  - 백프레셔(`MaxSendBacklog`), 세션 수 상한(`MaxSessions`)
  - `IUdpTransport` 추상화 — 실소켓 / 가상 전송 교체 가능
- **보안** — AES-256-GCM 인증 암호화, 64비트 슬라이딩 리플레이 윈도
- **FEC** — Reed-Solomon(GF(2^8), 조직 부호), kcp-go 호환 헤더
- **관측성** — `KcpServer.MetricsSampled`(세션 수, 재전송률, RTT p50/p95/p99)
- **코어**
  - `KcpClock` — Stopwatch 기반 단조 시계
  - `KcpStats` / `GetStats()` — 재전송·바이트·세그먼트 통계
  - `Kcp.Itimediff` — 랩어라운드 안전한 시각 비교(공개)
  - `Span` 오버로드(`Send` / `Input` / `Recv`)
  - `Kcp.Release()` — 대여 버퍼 반환
- **테스트** 207개 — 코덱 골든 벡터, 프로토콜 시나리오 14종, 가상 24시간 소크,
  실소켓 통합, 샘플별 단위 테스트
- **벤치마크** — BenchmarkDotNet 6종, 결과를 `docs/benchmarks.md` 에 기록
- **샘플 5종 + 부하 도구** — Echo / Chat / Gomoku / MoveSync / TankBattle / LoadTest
- **온라인 책** — 17장 + 부록 5종(docsify + GitHub Pages)
- **품질 기준** — 빌드 경고 0, 코어 라인 커버리지 85% 이상(실측 93.4%). 검증 명령은 `CONTRIBUTING.md` 참조

### 변경됨

- `KCP` → `Kcp` 로 개명. 기존 이름은 `[Obsolete]` 별칭으로 유지
- `UDPSession` → `public` + `[Obsolete]`. `KcpClient` 로 대체
- 프로젝트 구조를 `src` / `tests` / `benchmarks` / `samples` / `docs` 로 재편
- 타깃을 `netstandard2.1;net8.0` 멀티타깃으로 (기존 net8.0 Exe → 라이브러리)
- 세그먼트 페이로드를 자체 `ByteBuffer` → `ArrayPool<byte>` 대여 배열로
- 네 큐를 `List<Segment>` → `RingBuffer<KcpSegment>` 로 (앞에서 제거가 O(1))
- 세그먼트 풀을 전역 static(lock) → 인스턴스 필드(lock 없음)
- `parse_ack` / `parse_fastack` 을 이분 탐색으로
- 소켓 옵션 기본값을 1MB로 상향, Windows `SIO_UDP_CONNRESET` 비활성화

### 제거됨

- 코어에서 `ByteBuffer` 삭제(`ArrayPool` 로 대체)
- `System.Data.DataSetExtensions` 불필요 참조

### 고쳐짐

| 진단 | 내용 |
|---|---|
| B-01 | 시계가 `DateTime.Now` 기반이라 OS 시각 변경 시 RTO 폭주 → 단조 시계 주입 |
| B-02 | 부호 없는 시각 비교로 약 49.7일 후 갱신이 영구 정지 → `Itimediff` 로 통일 |
| B-03 | 데드링크를 애플리케이션이 알 수 없음 → `IsDeadLink` 노출 |
| B-04 | 조각 수 검사가 `> 255` 뿐이라 수신 윈도 초과 시 영구 교착 → `>= rcv_wnd` 로 |
| B-05 | 주요 타입이 `internal` 이라 라이브러리로 쓸 수 없음 |
| B-06 | `ByteBuffer(byte[])` 의 off-by-one 으로 쓰레기 1바이트가 붙음 |
| B-07 | 풀에서 요청보다 작은 버퍼가 나옴 → `ArrayPool` 로 근본 해결 |
| B-08 | 세그먼트 풀이 무제한 성장 → 상한 4096 |
| B-09 | `WriteUTF8String` 이 한도 초과 시 스트림을 오염시킴 |
| B-10 | `Dns.GetHostEntry` 결과의 첫 주소를 무조건 사용 → IPv4 우선 |
| B-12 | `Kcp.Send` 반환값 무시 |
| B-13 | 라이브러리가 `Console.WriteLine` 으로 직접 출력 → `OnError` 콜백 |
| B-14 | `Dispose()` 는 있는데 `IDisposable` 미구현 |
| B-15 | csproj 메타데이터 오류(`Company=Microsoft` 등) |
| PF-01 | 핫루프 안에서 시계를 반복 호출 |
| PF-02/03 | 호출마다 임시 배열 생성 → Span 경로 |
| PF-04 | `RemoveRange(0, n)` 의 O(n) 이동 |
| PF-05 | ACK 처리가 선형 탐색 |
| PF-06 | 전역 static 풀의 lock 경합 |
| PF-07 | 통계를 계산해 놓고 버림 |
| PF-08 | 호출당 1패킷만 수신 |
| S-03 | 암호화·무결성 없음 |
| S-04 | 상태 고갈 공격 대비 없음 |
| S-05 | 소켓 수신 버퍼 미설정 |
| — | 종료된 세션이 PING 에 PONG 을 돌려줘 상대가 영원히 타임아웃되지 않던 문제 |
| — | `.gitignore` 가 `*.csproj` / `*.sln` 을 무시해 프로젝트 파일이 커밋되지 않던 문제 |

### 알려진 제약

- FEC 와 암호화를 동시에 켤 수 없습니다(설정 검증에서 예외).
  두 계층의 순서가 와이어 포맷에 영향을 주는데 명세가 정해지지 않아 명시적으로 막았습니다.
- 세션 마이그레이션(모바일 IP 변경)은 아직 없습니다. 재접속으로 대응하세요.
- `netstandard2.1` 런타임에는 `AesGcm` 이 없어 `IPacketCipher` 직접 구현이 필요합니다.

---

## [0.x] — 이전

`master` 브랜치의 초기 포팅 버전입니다. kcp-go 를 C# 로 옮긴 코어와 클라이언트 전용 샘플만 있었고,
서버 구현·테스트·문서가 없었습니다.
