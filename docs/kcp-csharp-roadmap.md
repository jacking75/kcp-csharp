# kcp-csharp 개선 로드맵 — 온라인 게임용 라이브러리화 구현 지시서 (v2)

> 이 문서는 kcp-csharp 를 "온라인 게임 개발에 실전 투입 가능하고, 많은 사람이 배우고 쓸 수 있는 라이브러리"로 만들기 위한 **구현 지시서**다.
> 2026-08-19 기준 `master`(fd4480a) 코드 전체를 리뷰한 결과를 근거로 작성했다.
> 각 작업에는 ID / 우선순위 / 대상 파일 / 상세 내용 / 완료 조건이 있으므로, 이 문서의 작업 ID를 지정해 그대로 구현을 지시하면 된다.

**v2 변경 사항**
- 게임 엔진 연동 대상을 Unity → **MonoGame**으로 교체 (샘플 3~5의 클라이언트, 부록 D 가이드).
- **3장 "실행 방법론"** 신설: 이슈/브랜치/PR/커밋 규칙, Definition of Done, 주차별 실행 계획.
- 각 Phase에 **"실행 절차"**(커밋 단위의 단계별 순서, 코드 스케치, 검증 방법, 함정) 추가.
- 문서화(Phase 6)를 **온라인 책** 제작 계획으로 전면 개편: 전체 목차 + 장별 수록 내용.
- 샘플을 **난이도 5단계(★1~★5) 프로젝트 5종 + 도구 1종**으로 재설계.
- 작업 ID 정리: Phase 6 = `P6-x`, Phase 7 = `P7-x`, Phase 8 = `P8-x` (v1의 P8-x(문서)/P10-x(배포) 번호를 정정).

---

## ✅ 전체 작업 체크리스트 (진행 현황판)

> 이 문서의 모든 작업을 한눈에 보는 **진행 현황판**이다. 상세 지시는 각 작업 ID의 본문 절을 볼 것.
>
> **갱신 규칙**
> 1. 작업이 끝나면 `- [ ]` → `- [x]` 로 바꾸고 항목 끝에 `(완료: YYYY-MM-DD)` 를 적는다.
> 2. 진행 중이면 `- [~]` 로 표시하고 끝에 `(진행 중)` 을 적는다.
> 3. Phase의 모든 작업이 끝나면 아래 **마일스톤** 항목을 체크하고, 14장 최종 완료 체크리스트를 갱신한다(3.5절 리추얼).
> 4. 체크 갱신은 해당 작업의 PR에 포함한다(3.3절 DoD).
>
> **진행 현황: 45 / 47 완료** — P1 7/8 · P2 7/7 · P3 7/7 · P4 9/9 · P5 3/3 · P6 5/5 · P7 6/6 · P8 1/2

### Phase 1 — 기반 정비와 버그 수정 (§4) `필수`
- [x] **P1-1** 프로젝트 분리와 멀티타깃 `필수` (완료: 2026-08-25)
- [x] **P1-2** 접근성·API 표면 정리 `필수` (B-05) (완료: 2026-08-25)
- [x] **P1-3** 단조 시계 도입 `필수` (B-01, PF-01) (완료: 2026-08-25)
- [x] **P1-4** 치명·높음 버그 수정 `필수` (B-02, B-03, B-04, B-06, B-09, B-10, B-12~B-15) (완료: 2026-08-25)
- [x] **P1-5** 풀링 안정화 `필수` (B-07, B-08, PF-06) (완료: 2026-08-25)
- [x] **P1-6** 통계 노출 `권장` (PF-07) (완료: 2026-08-25)
- [x] **P1-7** XML 문서 주석 정리 `권장` (완료: 2026-08-25)
- [~] **P1-8** CI 구축 `필수` (구현: 2026-08-25 / 워크플로 파일은 2026-08-25 제거 — 푸시 토큰에 `workflow` 스코프가 없어 사용자 결정으로 삭제. 재도입 시 로드맵 §4 P1-8 절의 내용을 그대로 쓰면 된다)

### Phase 2 — 테스트 인프라 (§5) `필수` — Phase 3보다 반드시 먼저
- [x] **P2-1** 테스트 프로젝트 셋업 `필수` (완료: 2026-08-25)
- [x] **P2-2** 코덱·유틸 단위 테스트 `필수` (완료: 2026-08-25)
- [x] **P2-3** 가상 네트워크 시뮬레이터 `필수` — 품질의 핵심 장치 (완료: 2026-08-25)
- [x] **P2-4** 프로토콜 시나리오 테스트 14종 `필수` (완료: 2026-08-25)
- [x] **P2-5** 상호운용 테스트(kcp-go 대조) `권장` (완료: 2026-08-25)
- [x] **P2-6** 장시간·스트레스 테스트 `권장` (완료: 2026-08-25)
- [x] **P2-7** 커버리지 게이트 85% `권장` (완료: 2026-08-25)

### Phase 3 — 성능 최적화 (§6) — 측정 → 개선 순서 엄수
- [x] **P3-1** 벤치마크 기준선 확보 `필수(이 페이즈 내)` (완료: 2026-08-25)
- [x] **P3-2** Span 기반 코덱 전환 `권장` (PF-02, PF-03) (완료: 2026-08-25)
- [x] **P3-3** 버퍼 관리를 ArrayPool로 전환 `권장` (B-07 근본 해결) (완료: 2026-08-25)
- [x] **P3-4** Segment 큐 자료구조 교체(RingBuffer) `권장` (PF-04) (완료: 2026-08-25)
- [x] **P3-5** 풀 소유권 정리 `권장` (PF-06) (완료: 2026-08-25)
- [x] **P3-6** ACK 탐색 개선 `선택` (PF-05) (완료: 2026-08-25)
- [x] **P3-7** 소켓 I/O 현대화 `필수(Phase 4의 전제)` (PF-08, S-05) (완료: 2026-08-25)

### Phase 4 — 전송 계층: 게임용 클라이언트/서버 세션 (§7) `필수`
- [x] **P4-1a** 프레이밍 코덱 `필수` (완료: 2026-08-25)
- [x] **P4-1b** 쿠키 핸드셰이크 `필수` (S-04, B-11 해결) (완료: 2026-08-25)
- [x] **P4-2a** KcpSession 상태 머신 `필수` (완료: 2026-08-25)
- [x] **P4-2b** 세션 타이머 `필수` (완료: 2026-08-25)
- [x] **P4-3** KcpClient `필수` (완료: 2026-08-25)
- [x] **P4-4a** KcpServer 수신 루프 `필수` (완료: 2026-08-25)
- [x] **P4-4b** Update 스케줄러 `필수` (완료: 2026-08-25)
- [x] **P4-4c** 백프레셔·이벤트·한도 `필수` (완료: 2026-08-25)
- [x] **P4-5** 통합 테스트 `필수` (완료: 2026-08-25)

### Phase 5 — 보안·신뢰성 확장 (§8)
- [x] **P5-1** 패킷 암호화(AES-GCM) `권장` (S-03) (완료: 2026-08-25)
- [x] **P5-2** FEC (Reed-Solomon) `선택` (완료: 2026-08-25)
- [x] **P5-3** 관측성 `권장` (완료: 2026-08-25)

### Phase 6 — 온라인 책 《C#과 KCP로 배우는 실시간 게임 네트워킹》 (§9) `필수`
- [x] **P6-1** 책 인프라(docsify + GitHub Pages) `필수` (완료: 2026-08-25)
- [x] **P6-2** 집필 규약 `필수` (완료: 2026-08-25)
- [x] **P6-3** 본문 집필(전 17장 + 부록) `필수` (완료: 2026-08-25)
  - [x] 1부 원리 — 1~3장 (온라인 게임과 네트워크 / ARQ 원리 / KCP 해부)
  - [x] 2부 사용법 — 4~8장 (시작하기 / 코어 API / 세션 수명 / 채널 / 튜닝)
  - [x] 3부 실전 — 9~13장 (에코 / 채팅 / 오목 / 이동 동기화 / 탱크 배틀)
  - [x] 4부 심화 — 14~17장 (성능 / 보안 / FEC / 운영)
  - [x] 부록 A~E (API / FAQ / 상호운용 / MonoGame / 용어집)
- [x] **P6-4** 집필 순서 준수(4장 → 9~13장 → 5~8장 → 1~3장 → 14~17장 → 부록) `필수` (완료: 2026-08-25)
- [x] **P6-5** 저장소 위생(README 개편·CHANGELOG·CONTRIBUTING·이슈 템플릿) `권장` (완료: 2026-08-25)

### Phase 7 — 샘플 프로젝트 5종 + 도구 (§10) `필수`
- [x] **P7-1** 샘플 ① Echo `★1` — UDP 27001, 콘솔 (완료: 2026-08-25)
- [x] **P7-2** 샘플 ② Chat `★2` — UDP 27002, 콘솔 (완료: 2026-08-25)
- [x] **P7-3** 샘플 ③ Gomoku(오목) `★3` — UDP 27003, MonoGame (완료: 2026-08-25)
- [x] **P7-4** 샘플 ④ MoveSync `★4` — UDP 27004, MonoGame (완료: 2026-08-25)
- [x] **P7-5** 샘플 ⑤ TankBattle `★5` — UDP 27005, MonoGame (완료: 2026-08-25)
- [x] **P7-6** 도구 LoadTest `필수` — UDP 27010 (완료: 2026-08-25)

### Phase 8 — 배포와 운영 (§11)
- [x] **P8-1** NuGet 패키징 `권장` (완료: 2026-08-25 / 릴리스 자동화 워크플로만 workflow 권한 문제로 보류)
- [~] **P8-2** 품질 게이트 유지 `권장` (로컬 검증은 CONTRIBUTING.md 로 대체, CI·브랜치 보호는 사용자 결정으로 보류 — 2026-08-25)

### 마일스톤 (13장)
- [~] **M1** 안전한 코어 — P1-1 ~ P1-7 완료 (2026-08-25), P1-8(CI)은 워크플로 파일 제거로 보류
- [x] **M2** 증명된 코어 — P2-1 ~ P2-7 (완료: 2026-08-25)
- [x] **M3** 빠른 코어 — P3-1 ~ P3-7 (완료: 2026-08-25 / `1.0.0-preview.1` 태그는 미부여)
- [x] **M4** 게임에 쓸 수 있는 라이브러리 — P4-1a ~ P4-5 (완료: 2026-08-25 / 1,000세션 LoadTest 검증은 P7-6, 태그 미부여)
- [x] **M5** 배우기 쉬운 라이브러리 — P6-1~5, P7-1~6, P8-1 완료 (2026-08-25) → `1.0.0`
- [x] **M6** 차별화 — P5-1 ~ P5-3 + 책 15~17장 완료 (2026-08-25)

---

## 0. 문서 사용법

- **작업 ID 체계**: `P<페이즈>-<번호>` (예: `P1-4`). 대형 작업은 `P4-2a`처럼 서브 ID로 분할. 버그 `B-xx`, 성능 `PF-xx`, 안정성 `S-xx`는 진단(1장)과 작업(4장 이후)을 상호 참조한다.
- **우선순위**: `필수`(이게 없으면 게임에 못 쓴다) / `권장`(실전 품질) / `선택`(차별화).
- **진행 순서**: Phase 1 → 2 → 3 → 4 는 순서 의존성이 있다(특히 *테스트(Phase 2)가 성능 최적화(Phase 3)보다 먼저*). Phase 5~8은 Phase 4 이후 병렬 진행 가능. 주차 단위 계획은 3.4절.
- **불변 원칙**(모든 작업에 적용):
  1. **와이어 포맷 호환 유지** — 코어 KCP 헤더(24바이트)는 원본 C kcp / kcp-go와 바이트 단위로 호환 유지. 확장(핸드셰이크·암호화·FEC)은 코어 밖의 프레이밍 계층에서 한다.
  2. **코어는 순수하게** — `Kcp` 클래스는 소켓·스레드·시계·로깅 구현을 몰라야 한다(전부 주입).
  3. **수정 전 테스트** — Phase 2 완료 후의 모든 코드 변경은 기존 테스트 통과 + 신규 테스트 추가가 전제.
  4. **성능 회귀 금지** — Phase 3에서 확보한 벤치마크 기준선 대비 회귀하는 변경은 머지하지 않는다.
  5. **개발용 포트** — 샘플·테스트의 UDP 포트는 **27001~27199** 대역에서만 고른다(배정표: 12.4절).

---

## 1. 현재 코드 진단 (코드 리뷰 결과)

### 1.1 구조 요약

| 파일 | 줄수 | 역할 | 접근성 |
|---|---|---|---|
| `KCP/KCP.cs` | ~1,240 | KCP 프로토콜 코어 (kcp-go 포팅) | `public` |
| `KCP/ByteBuffer.cs` | ~910 | 범용 바이트 버퍼 + 자체 풀 | `internal` |
| `KCP/UDPSession.cs` | ~175 | 클라이언트 전용 UDP 세션 래퍼 | `internal` |
| `Sample/Main.cs` | ~68 | 클라이언트 에코 샘플 (서버 없음) | — |
| `KcpCSharpR1.csproj` | — | net8.0 **Exe** 단일 프로젝트 (라이브러리 아님) | — |

핵심 진단: **프로토콜 코어는 kcp-go를 충실히 따르고 있으나, "라이브러리"가 아니라 "샘플이 붙은 소스 모음" 상태다.** 서버 구현이 없고, 테스트가 0개이며, 실행 파일 프로젝트라 NuGet 참조도 불가능하다.

### 1.2 버그 목록

| ID | 심각도 | 위치 | 요약 |
|---|---|---|---|
| B-01 | **치명** | `KCP.cs:92-98` | 시계가 `DateTime.Now` 기반 — 단조성 없음. OS 시계 변경(NTP 동기화, 수동 변경, DST)이 일어나면 RTO 폭주·재전송 폭풍·연결 정지 발생. `TotalMilliseconds`가 음수가 되면 `(UInt32)` 캐스팅으로 거대한 값이 되어 프로토콜 전체가 붕괴 |
| B-02 | **치명** | `UDPSession.cs:162` | `mKCP.CurrentMS >= mNextUpdateTime` — 부호 없는 직접 비교. 내부 시계가 UInt32를 랩어라운드하는 **약 49.7일 후 Update가 영원히 호출되지 않아 서버가 조용히 멈춘다** |
| B-03 | **높음** | `KCP.cs:207,1010` | 데드링크 감지 불가 — 재전송이 `dead_link`(20회)를 넘으면 `state = 0xFFFFFFFF`가 되지만 `state`가 private이고 접근자가 없다. **상대가 사라져도 애플리케이션이 영원히 알 수 없다** |
| B-04 | **높음** | `KCP.cs:426` | 조각 수 검증 오류 — `count > 255`만 검사. 원본 ikcp.c는 `count >= IKCP_WND_RCV`를 검사한다. 메시지 모드에서 조각 수가 수신 윈도(기본 32)를 넘으면 **수신측 rcv_queue가 절대 채워지지 못해 영구 교착** |
| B-05 | **높음** | `KCP/UDPSession.cs`, `KCP/ByteBuffer.cs` | 클래스가 `internal` — 라이브러리로 참조하면 `KCP` 말고는 아무것도 쓸 수 없다 |
| B-06 | 중간 | `ByteBuffer.cs:46` | `ByteBuffer(byte[] bytes)` 생성자에서 `writeIndex = bytes.Length + 1` — off-by-one. `Clone()`/`Allocate(byte[])` 경유 시 쓰레기 1바이트가 유효 데이터로 붙는다 |
| B-07 | 중간 | `ByteBuffer.cs:58-84` | 풀에서 꺼낼 때 요청 `capacity`를 무시하고 아무 크기 버퍼나 반환 |
| B-08 | 중간 | `KCP.cs:148-154` | `Segment` 풀이 무제한 성장(`Put`이 조건 없이 push) |
| B-09 | 중간 | `ByteBuffer.cs:753-780` | `WriteUTF8String` — 길이 접두사를 실제 기록 바이트가 아닌 원본 길이로 먼저 기록. 한도 초과 문자열에서 **스트림 오염** |
| B-10 | 중간 | `UDPSession.cs:30-35` | `Dns.GetHostEntry` 결과의 `AddressList[0]`을 무조건 사용 — IPv6가 먼저 오면 IPv4 서버 연결 실패. 블로킹 호출 |
| B-11 | 중간 | `UDPSession.cs:40` | conv를 클라이언트가 난수로 생성 — 합의 절차(핸드셰이크)가 없다. 서버 구현이 존재할 수 없는 구조적 원인 |
| B-12 | 낮음 | `UDPSession.cs:82` | 루프에서 `mKCP.Send(...)` 반환값(오류 코드) 무시 |
| B-13 | 낮음 | `UDPSession.cs:122` | 라이브러리 코드가 `Console.WriteLine(ex)` 직접 출력 |
| B-14 | 낮음 | `ByteBuffer.cs:891` | `Dispose()` 메서드가 있는데 `IDisposable` 미구현. Dispose 후 사용 시 NRE |
| B-15 | 낮음 | `KcpCSharpR1.csproj` | `System.Data.DataSetExtensions` 불필요 참조, `Company=Microsoft` 등 잘못된 메타데이터, Exe 출력 |

### 1.3 성능 문제

| ID | 위치 | 요약 |
|---|---|---|
| PF-01 | `KCP.cs:94-98, 628, 996` | `currentMS()`가 `DateTime.Now` 기반으로 비싸고 **핫루프 안에서 반복 호출**된다 |
| PF-02 | `ByteBuffer.cs:342-359` | `Get()`/`Read()`가 호출마다 임시 `byte[]` 할당 → GC 압박 |
| PF-03 | `ByteBuffer.cs:417-424` | `ReadBytes`가 1바이트씩 루프 복사 |
| PF-04 | `KCP.cs:221-224` | 큐가 전부 `List<Segment>` — `RemoveRange(0,n)`이 매번 O(n) 이동. 큰 윈도(256~1024)에서 CPU 낭비 |
| PF-05 | `KCP.cs:486-519` | `parse_ack`/`parse_fastack`이 ACK 1개당 snd_buf 선형 탐색 — O(n·m) |
| PF-06 | `KCP.cs:132`, `ByteBuffer.cs:24` | 풀이 전부 **프로세스 전역 static + lock** — 세션 수가 늘면 전 세션이 같은 락을 두드린다 |
| PF-07 | `KCP.cs:612,949` | `inSegs`, `lostSegs` 등 통계를 **계산해 놓고 버린다**(kcp-go SNMP의 흔적) |
| PF-08 | `UDPSession.cs:114-131` | 호출 1회당 데이터그램 1개만 수신 + 샘플의 10ms sleep 루프 — 처리량 상한이 구조적으로 낮다 |

### 1.4 안정성·보안 문제

| ID | 요약 |
|---|---|
| S-01 | 스레드 소유권 규약이 정의·문서화되어 있지 않다 |
| S-02 | 연결 수명 관리 부재 — 핸드셰이크·종료(FIN)·keepalive·타임아웃 전부 없음 |
| S-03 | 암호화·무결성 없음 — conv만 맞으면 아무 패킷이나 주입 가능 |
| S-04 | 서버 관점 방어 부재 — 스푸핑 소스 주소로 세션 테이블을 채우는 상태 고갈 공격 대비 없음 |
| S-05 | 소켓 수신 버퍼 크기 미설정 — OS 기본값에서 버스트 시 커널이 조용히 패킷 폐기 |
| S-06 | `Kcp.Input` 오류 반환(-1/-2/-3) 시 앞부분 세그먼트의 상태 변경은 적용된 채 중단 — 원본과 같은 동작이나 문서화 필요 |

### 1.5 라이브러리 관점의 공백

- **서버가 없다.** 단일 소켓 다중 클라이언트(EndPoint↔세션 매핑) 서버 컴포넌트 부재.
- **RTT를 알 수 없다.** `rx_srtt`/`rx_rto` 미노출 — 핑 표시·지연 보상 불가.
- **테스트 0개, 벤치마크 0개, CI 없음.**
- **문서가 사실상 없다.** README 6줄.
- **게임 엔진 연동 예제 없음.**
- **NuGet 패키지 없음.**

---

## 2. 목표 아키텍처

### 2.1 최종 저장소 구조

```
kcp-csharp/
├─ src/
│  ├─ KcpCSharpR1/                  # [코어] 프로토콜만. 의존성 0
│  │  ├─ Kcp.cs                    # KCP.cs 개명·리팩터링
│  │  ├─ KcpSegment.cs             # Segment 분리
│  │  ├─ KcpCodec.cs               # 인코딩/디코딩 (BinaryPrimitives)
│  │  ├─ KcpClock.cs               # Stopwatch 기반 단조 시계
│  │  ├─ KcpStats.cs               # 통계 구조체
│  │  └─ RingBuffer.cs             # Segment 큐용 원형 덱
│  └─ KcpCSharpR1.Transport/        # [전송] 소켓·세션·핸드셰이크
│     ├─ KcpClient.cs
│     ├─ KcpServer.cs
│     ├─ KcpSession.cs             # 클라/서버 공용 세션 상태 머신
│     ├─ Framing.cs                # 프레임 타입·핸드셰이크 패킷 코덱
│     ├─ HandshakeCookie.cs        # HMAC 쿠키
│     ├─ CryptoTransform.cs        # (Phase 5) AES-GCM
│     └─ Fec/                      # (Phase 5) Reed-Solomon
├─ samples/                        # 난이도순 5종 + 도구 (10장)
│  ├─ 01-Echo/                     #   ★1  콘솔        UDP 27001
│  ├─ 02-Chat/                     #   ★2  콘솔        UDP 27002
│  ├─ 03-Gomoku/                   #   ★3  MonoGame    UDP 27003
│  ├─ 04-MoveSync/                 #   ★4  MonoGame    UDP 27004
│  ├─ 05-TankBattle/               #   ★5  MonoGame    UDP 27005
│  └─ tools/LoadTest/              #   부하 측정 도구   UDP 27010
├─ tests/
│  ├─ KcpCSharpR1.Tests/            # 단위 테스트 (xUnit)
│  └─ KcpCSharpR1.SimTests/         # 가상 네트워크 시뮬레이션 테스트
├─ benchmarks/
│  └─ KcpCSharpR1.Benchmarks/       # BenchmarkDotNet
├─ docs/                           # 온라인 책 (docsify + GitHub Pages, 9장)
│  ├─ index.html  _sidebar.md  README.md
│  ├─ book/ch01-*.md ... 부록
│  └─ benchmarks.md
├─ .github/workflows/ci.yml
├─ Directory.Build.props
└─ KcpCSharpR1.sln
```

### 2.2 계층 책임

```
┌───────────────────────────────────────────────┐
│ 게임 코드 (샘플 5종이 시연)                       │
├───────────────────────────────────────────────┤
│ KcpCSharpR1.Transport                          │
│  - 소켓 I/O, 세션 수명(핸드셰이크/keepalive/종료)  │
│  - 프레이밍(신뢰 KCP / 비신뢰 raw / 제어 패킷)     │
│  - (선택) 암호화, FEC                           │
├───────────────────────────────────────────────┤
│ KcpCSharpR1 (코어)                              │
│  - ARQ 상태 머신만. 소켓·스레드·실시간 시계 무지     │
│  - 입력: Input(bytes) / Send(bytes) / Update() │
│  - 출력: output 콜백                            │
└───────────────────────────────────────────────┘
```

### 2.3 스레딩 모델 (규약으로 확정)

- **`Kcp` 인스턴스 1개 = 소유 스레드 1개.** 모든 메서드는 소유 스레드에서만 호출한다. 내부 lock을 두지 않는다.
- `KcpServer`는 **수신 루프 1개가 모든 세션의 Input/Update를 구동**하는 단일 컨슈머 모델이 기본. 게임 로직 스레드로는 큐로 메시지를 넘긴다.
- MonoGame 클라이언트는 `Game.Update()`에서 `KcpClient.Update()`를 폴링한다(메인 스레드 소유).
- 이 규약을 XML 문서 주석과 책 6장·부록 D에 명시한다.

### 2.4 이름·호환 결정 사항

| 항목 | 결정 |
|---|---|
| 네임스페이스 | `KcpCSharpR1` 유지, 전송 계층은 `KcpCSharpR1.Transport` |
| 타깃 프레임워크 | `netstandard2.1;net8.0` 멀티타깃. **MonoGame(3.8.2+)은 net8.0 타깃을 그대로 사용**하므로 별도 대응 불필요. netstandard2.1은 Godot C#·구형 런타임 호환용으로 유지 |
| 클래스명 | `KCP` → `Kcp` 개명, `[Obsolete]` 별칭 `KCP` 파생 클래스를 1개 버전 동안 유지 |
| 와이어 호환 | 코어는 원본 kcp와 완전 호환. Transport 프레이밍은 자체 포맷이며 **RawMode 옵션**으로 kcp-go 서버와 직접 통신 가능 |
| C# 스타일 | `#nullable enable`, 공개 API PascalCase, XML 문서 주석 한국어(주요 API 영어 병기) |

---

## 3. 실행 방법론 — 로드맵을 실제로 굴리는 방법

> 이 장은 "무엇을"이 아니라 **"어떤 절차와 리듬으로"** 진행하는지를 규정한다. 모든 Phase에 공통 적용.

### 3.1 작업 관리 (GitHub 기준)

1. **이슈 = 작업 ID 1개.** 이 문서의 작업(P1-1, P4-2a, …)마다 GitHub 이슈를 만든다. 제목은 `P1-3 단조 시계 도입` 형식.
2. **라벨**: `phase:1`~`phase:8`, `type:bug|feat|perf|test|docs|sample`, `priority:필수|권장|선택`.
3. **마일스톤**: M1~M6(13장)을 GitHub 마일스톤으로 등록하고 이슈를 배정한다.
4. **이슈 본문 템플릿** (`.github/ISSUE_TEMPLATE/task.md`로 저장):

```markdown
## 배경
로드맵 §<절 번호> <작업 ID> 참조. (관련 진단: B-xx / PF-xx / S-xx)

## 작업 항목
- [ ] (세부 단계를 로드맵에서 복사해 체크리스트로)

## 완료 조건
- [ ] (로드맵의 완료 조건)

## 영향 범위
- 코드: / 테스트: / 문서(책 장 번호):
```

5. **칸반**: GitHub Projects 보드 1개 — `대기 / 진행 중 / 리뷰 / 완료`. 진행 중은 항상 1~2개로 제한(1인 기준).

### 3.2 브랜치·PR·커밋 규칙

- 브랜치명: `p1-3-monotonic-clock` (작업 ID 소문자 + 요약). `master` 직접 푸시 금지(브랜치 보호 설정).
- **PR 1개 = 작업 ID 1개.** 리뷰어가 없어도 PR을 거친다(셀프 리뷰 기록 + CI 게이트가 목적). 큰 작업은 `P4-2a`, `P4-2b`처럼 서브 ID로 쪼개 PR도 나눈다. PR 500줄(diff) 초과 시 분할을 검토한다.
- 커밋 메시지: `P1-3: currentMS를 Stopwatch 기반 KcpClock으로 교체` — 작업 ID 접두사 필수. 본문에 테스트/벤치 결과 요약을 남긴다.
- PR 본문에는 이슈 링크(`Closes #nn`)와, Phase 3 이후에는 벤치마크 전/후 표를 첨부한다.

### 3.3 Definition of Done (모든 PR 공통 체크리스트)

- [ ] `dotnet build -c Release` 경고 0 (`TreatWarningsAsErrors`)
- [ ] 신규/변경 동작에 대한 테스트 추가, `dotnet test` 전체 통과 (Phase 2 이후)
- [ ] 공개 API 변경 시 XML 문서 주석 갱신
- [ ] 책/문서에 영향이 있으면 해당 장에 TODO 주석 또는 즉시 반영
- [ ] 벤치마크 회귀 없음 — 관련 벤치 전/후 수치를 PR에 기록 (Phase 3 이후)
- [ ] `CHANGELOG.md`에 한 줄 추가 (사용자 가시 변경일 때)

### 3.4 주차별 실행 계획

기준: **1인, 주 10~15시간**(전업이면 기간을 절반으로 본다). 주차는 순서와 의존관계를 나타내는 상대값이다.

| 주차 | 작업 | 산출물/체크포인트 |
|---|---|---|
| 1 | P1-1, P1-2 | 솔루션 재편 완료, 라이브러리 DLL 빌드 |
| 2 | P1-3, P1-4 | 치명·높음 버그 수정 머지 |
| 3 | P1-5 ~ P1-8 | CI 녹색 배지 → **M1** |
| 4 | P2-1, P2-2 | 테스트 프로젝트 + 코덱 골든 벡터 |
| 5 | P2-3 | SimulatedLink + KcpPair 하네스 |
| 6 | P2-4 | 시나리오 테스트 14종 통과 |
| 7 | P2-5 ~ P2-7 | 커버리지 게이트 85% → **M2** |
| 8 | P3-1, P3-2 | 벤치 기준선 기록(docs/benchmarks.md), Span 코덱 |
| 9 | P3-3, P3-4 | ArrayPool 전환, RingBuffer 교체 |
| 10 | P3-5 ~ P3-7 | 재측정: 할당 0 확인 → **M3**, `1.0.0-preview.1` 태그 |
| 11 | P4-1a/b, P4-2a | 프레이밍+쿠키, 세션 상태 머신(가상 시간 테스트) |
| 12 | P4-2b, P4-3 | 세션 타이머, KcpClient |
| 13 | P4-4a/b/c | KcpServer 수신 루프·스케줄러·백프레셔 |
| 14 | P4-5, P7-6 | 통합 테스트 + LoadTest 도구로 1,000세션 검증 → **M4**, `preview.2` |
| 15 | P7-1, P7-2 / P6-1, P6-2 | 샘플1·2 + 책 인프라(GitHub Pages 오픈)·집필 규약 |
| 16 | P7-3 / 책 4~5장 | 오목(MonoGame) + 사용법 장 초안 |
| 17 | P7-4 / 책 6~8장 | MoveSync + 세션·채널·튜닝 장 |
| 18~19 | P7-5 / 책 9~13장 | 탱크 배틀 + 실전 프로젝트 장(샘플과 1:1) |
| 20 | 책 1~3장, 부록 | 이론부·부록 완성 |
| 21 | P6-5, P8-1 | README 개편, NuGet `1.0.0` → **M5** |
| 22+ | P5-1 → 책 15장, P5-2 → 책 16장, P5-3 → 책 17장 | `1.1.0` → **M6** |

### 3.5 Phase 착수 리추얼 (매 Phase 공통)

1. 해당 Phase 절을 다시 읽고, 작업별 이슈를 일괄 생성해 마일스톤에 배정한다.
2. 진단(1장)에서 그 Phase가 해결하는 B/PF/S 항목을 이슈에 링크한다.
3. Phase 종료 시: 마일스톤 닫기, 태그/릴리스(해당 시), 이 문서의 체크리스트(14장) 갱신 커밋.

---

## 4. Phase 1 — 기반 정비와 버그 수정 (필수)

> 목표: "빌드하면 라이브러리 DLL이 나오고, 알려진 치명 버그가 없는 상태".

### P1-1. 프로젝트 분리와 멀티타깃 `필수`

**실행 절차 (커밋 순서대로):**

1. 디렉터리 생성 및 파일 이동:

```
mkdir src, tests, benchmarks, samples
git mv KCP src/KcpCSharpR1
git mv Sample samples/01-Echo        # Phase 7에서 재작성하지만 위치는 지금 옮긴다
git rm Properties/AssemblyInfo.cs KcpCSharpR1.csproj
```

2. `src/KcpCSharpR1/KcpCSharpR1.csproj` 신규 작성:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>netstandard2.1;net8.0</TargetFrameworks>
  </PropertyGroup>
</Project>
```

3. 루트 `Directory.Build.props` 작성(전 프로젝트 공통 속성은 여기로 일원화):

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <Authors>jacking75</Authors>
  </PropertyGroup>
</Project>
```

4. `src/KcpCSharpR1.Transport/` 빈 프로젝트 생성(코어 참조만 걸어 둔다), 솔루션 재구성:

```
dotnet new classlib -o src/KcpCSharpR1.Transport
dotnet sln KcpCSharpR1.sln add src/KcpCSharpR1 src/KcpCSharpR1.Transport
```

5. `samples/01-Echo`는 임시로 Exe 프로젝트를 만들어 기존 Main.cs가 빌드만 되게 유지.
6. `#nullable enable` 적용에 따른 경고를 전부 정리(주로 `output` 델리게이트, `writelog` null 처리).

**검증**: `dotnet build -c Release` 경고 0. **함정**: `TreatWarningsAsErrors` 때문에 nullable 경고가 대량으로 나온다 — 커밋 6에서 한꺼번에 처리하고, 임시로 끄는 식으로 우회하지 않는다.

### P1-2. 접근성·API 표면 정리 `필수` (B-05)

**결정**: `ByteBuffer`는 internal 유지(P3-3에서 삭제 예정), `UDPSession`은 `public` + `[Obsolete("KcpClient로 대체 예정")]`.

**실행 절차:**

1. `KCP` → `Kcp` 파일·클래스 개명. 하위 호환용:

```csharp
[Obsolete("Kcp로 이름이 변경되었습니다. 다음 메이저 버전에서 제거됩니다.")]
public class KCP : Kcp
{
    public KCP(uint conv, Action<byte[], int> output) : base(conv, output) { }
}
```

2. 읽기 전용 프로퍼티 추가 (필드는 기존 그대로 두고 노출만):

```csharp
public uint Conv => conv;
public int Srtt => rx_srtt;            // 평활 RTT(ms) — 게임 핑 표시용
public int RttVar => rx_rttval;
public uint Rto => rx_rto;
public bool IsDeadLink => state == 0xFFFFFFFF;   // B-03 해결
public uint Interval => interval;
```

**검증**: 외부 콘솔 프로젝트에서 DLL 참조 후 핑 표시·데드링크 감지 코드가 컴파일·동작.

### P1-3. 단조 시계 도입 `필수` (B-01, PF-01)

**실행 절차:**

1. `KcpClock.cs` 신설:

```csharp
using System.Diagnostics;

namespace KcpCSharpR1;

/// <summary>Stopwatch 기반 단조(monotonic) 밀리초 시계. OS 시계 변경에 영향받지 않는다.</summary>
public static class KcpClock
{
    private static readonly long start = Stopwatch.GetTimestamp();
    private static readonly double toMs = 1000.0 / Stopwatch.Frequency;

    public static uint NowMs => (uint)(long)((Stopwatch.GetTimestamp() - start) * toMs);
}
```

2. `Kcp` 생성자에 시계 주입(테스트의 가상 시간이 전제 — Phase 2의 핵심 의존):

```csharp
private readonly Func<uint> clock;

public Kcp(uint conv, Action<byte[], int> output, Func<uint>? currentTimeMs = null)
{
    clock = currentTimeMs ?? (() => KcpClock.NowMs);
    ...
}
```

3. `currentMS()` 정적 메서드와 `refTime` 필드 삭제, 모든 호출부를 `clock()`으로 교체.
4. **핫루프 정리**: `Input`은 진입 시 1회만 시계를 읽어 지역 변수 사용(`KCP.cs:628`의 세그먼트별 호출 제거), `Flush`도 진입 시 1회 + ACK 처리 후 1회로 제한(`KCP.cs:996`의 needsend 내 `CurrentMS` 프로퍼티 호출 제거).

**검증**: 코어에서 `DateTime` 참조 0건(`grep`으로 확인). 가상 시계로 "RTO가 nodelay=0에서 2배씩 증가"를 재현하는 테스트 1개.
**함정**: `CurrentMS` 공개 프로퍼티는 유지하되 `clock()` 반환으로 변경(외부 코드 호환).

### P1-4. 치명·높음 버그 수정 `필수`

각 항목을 **개별 커밋**으로 수정한다(회귀 시 bisect 용이).

**B-02 — 랩어라운드 안전 비교** (`UDPSession.Update`):

```csharp
public void Update()
{
    if (mSocket == null) return;
    var current = mKCP.CurrentMS;
    if (0 == mNextUpdateTime || (Int32)(current - mNextUpdateTime) >= 0)
    {
        mKCP.Update();
        mNextUpdateTime = mKCP.Check();
    }
}
```

**B-04 — 조각 수 검사** (`Kcp.Send`): `if (count > 255) return -2;` → `if (count >= rcv_wnd) return -2;` 로 교체(원본 ikcp.c 동일). XML 주석에 "메시지 모드 최대 메시지 크기 = (rcv_wnd − 1) × mss" 명기.

**B-06** — `ByteBuffer(byte[])` 생성자: `writeIndex = bytes.Length;`

**B-09** — `WriteUTF8String`: 인코딩 결과가 lenType의 최댓값을 넘으면 `ArgumentOutOfRangeException`. 접두사는 항상 실제 기록 바이트 수.

**B-10 — 주소 결정** (`UDPSession.Connect`):

```csharp
if (!IPAddress.TryParse(host, out var addr))
{
    var list = Dns.GetHostAddresses(host);   // GetHostEntry보다 가볍다
    addr = Array.Find(list, a => a.AddressFamily == AddressFamily.InterNetwork)
           ?? (list.Length > 0 ? list[0] : throw new SocketException((int)SocketError.HostNotFound));
}
```

**B-12** — `UDPSession.Send` 루프에서 `Kcp.Send` 반환값이 음수면 즉시 반환.
**B-13** — `Console.WriteLine` 제거. 수신 예외는 `Action<Exception>? OnError` 콜백으로 전달, 콜백 없으면 재던짐.
**B-14** — `ByteBuffer : IDisposable` 선언 추가.

**검증**: 각 수정마다 회귀 테스트 1개(테스트 프로젝트가 아직 없으면 P2-1을 먼저 최소로 세팅해도 된다 — 권장).

### P1-5. 풀링 안정화 `필수` (B-07, B-08, PF-06)

1. `Segment` 풀 상한:

```csharp
private const int MaxPoolSize = 4096;
public static void Put(Segment seg)
{
    seg.reset();
    lock (msSegmentPool)
        if (msSegmentPool.Count < MaxPoolSize) msSegmentPool.Push(seg);
    // 상한 초과분은 버린다(GC 대상)
}
```

2. `ByteBuffer.Allocate(capacity, fromPool:true)`: 풀에서 꺼낸 버퍼가 `Capacity < capacity`면 `FixSizeAndReset`으로 즉시 확장하고 반환(B-07 임시 조치 — 근본 해결은 P3-3).
3. 전역 static → 인스턴스별 풀 전환은 **P3-5로 미룬다**(여기서는 상한만).

**검증**: 임시 콘솔 부하(1분 송수신)에서 풀 카운트가 상한을 넘지 않음을 디버거/카운터로 확인.

### P1-6. 통계 노출 `권장` (PF-07)

```csharp
public struct KcpStats
{
    public ulong BytesSent, BytesReceived;
    public ulong SegmentsSent, SegmentsReceived;
    public ulong RetransmitSegments;      // RTO 재전송 (lostSegs)
    public ulong FastRetransmitSegments;
    public ulong EarlyRetransmitSegments;
    public ulong RepeatSegments;          // 중복 수신
    public int CurrentRto, Srtt;
    public int SendWindowUsage;           // snd_buf.Count
    public int WaitSend;                  // WaitSnd
}
```

`Flush`/`Input`에서 이미 계산 중인 지역 변수(`lostSegs` 등)를 누적 필드에 더하고 `GetStats()` 스냅숏 반환. **검증**: LoadTest(P7-6)가 재전송률(%)을 출력.

### P1-7. XML 문서 주석 정리 `권장`

공개 타입·메서드 전체 한국어 XML 주석. 중국어 잔존 주석(`UDPSession.cs:150` 등) 한국어화. `Send`/`Recv`/`Input` 반환 코드(-1/-2/-3) 표 문서화, S-06 동작 명기.

### P1-8. CI 구축 `필수`

`.github/workflows/ci.yml`:

```yaml
name: CI
on:
  push: { branches: [master] }
  pull_request:
jobs:
  build-test:
    strategy:
      matrix: { os: [ubuntu-latest, windows-latest] }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }
      - run: dotnet build -c Release
      - run: dotnet test -c Release --collect:"XPlat Code Coverage"
```

**검증**: PR에서 Windows+Linux 자동 실행, README 상태 배지.

---

## 5. Phase 2 — 테스트 인프라 (필수, Phase 3보다 반드시 먼저)

> 목표: "프로토콜 동작을 코드로 증명하는 안전망". 이후 모든 최적화·리팩터링의 전제.

### P2-1. 테스트 프로젝트 셋업 `필수`

```
dotnet new xunit -o tests/KcpCSharpR1.Tests
dotnet new xunit -o tests/KcpCSharpR1.SimTests
dotnet sln add tests/KcpCSharpR1.Tests tests/KcpCSharpR1.SimTests
dotnet add tests/KcpCSharpR1.Tests reference src/KcpCSharpR1
```

코어 csproj에 내부 상태 검증용:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="KcpCSharpR1.Tests" />
  <InternalsVisibleTo Include="KcpCSharpR1.SimTests" />
</ItemGroup>
```

의존성 최소화를 위해 어서션 라이브러리는 추가하지 않는다(xUnit `Assert`만).

### P2-2. 코덱·유틸 단위 테스트 `필수`

| 테스트 | 내용 |
|---|---|
| 인코딩 왕복 | `ikcp_encode8u/16u/32u` ↔ decode 왕복 동일성, 리틀엔디언 배치를 **골든 바이트 배열**과 비교 |
| 헤더 골든 벡터 | conv=0x11223344, cmd=81, frg=2, wnd=32, ts=1000, sn=7, una=5, len=3 을 인코딩한 24바이트가 원본 C kcp 산출과 일치 — 상호운용의 근거. 기대 바이트를 주석으로 명기 |
| `_itimediff` | 랩어라운드 경계(`0xFFFF_FFF0` vs `0x10`) 케이스 |
| ByteBuffer | B-06/B-09 회귀, `EnsureWritableBytes` 성장/트림 경로 |

### P2-3. 가상 네트워크 시뮬레이터 `필수` — 이 라이브러리 품질의 핵심 장치

**구성 요소 2개**: 회선을 흉내 내는 `SimulatedLink`, 두 `Kcp`를 묶는 하네스 `KcpPair`.

```csharp
/// <summary>지연·지터·손실·중복·순서뒤섞임·대역폭 제한을 재현하는 가상 단방향 링크.
/// 가상 시간 기반: Advance(now) 호출 시 도착 시각이 된 패킷을 수신자에게 배달한다.</summary>
public sealed class SimulatedLink
{
    public int DelayMs = 50;
    public int JitterMs = 0;
    public double LossRate = 0;        // 0.0~1.0
    public double DuplicateRate = 0;
    public double ReorderRate = 0;
    public int BandwidthKbps = 0;      // 0 = 무제한
    public int Mtu = 1400;             // 초과 패킷은 폐기(단편화 미지원 재현)

    private readonly Random rng;                       // 시드 고정 → 재현 가능
    private readonly List<(uint at, byte[] pkt)> inflight = new();
    public Action<byte[], int>? Deliver;               // 수신측 Kcp.Input에 연결

    public SimulatedLink(int seed) { rng = new Random(seed); }

    public void Send(byte[] data, int len)             // 송신측 Kcp의 output에 연결
    {
        if (len > Mtu) return;
        if (rng.NextDouble() < LossRate) return;
        var at = now + (uint)(DelayMs + rng.Next(-JitterMs, JitterMs + 1));
        if (ReorderRate > 0 && rng.NextDouble() < ReorderRate) at += (uint)rng.Next(1, DelayMs);
        Enqueue(at, data, len);
        if (rng.NextDouble() < DuplicateRate) Enqueue(at + 1, data, len);
        // BandwidthKbps > 0 이면 직전 패킷 전송 완료 시각 이후로 at을 미룬다(토큰 버킷)
    }

    public void Advance(uint virtualNowMs) { /* at <= now 패킷을 at 순으로 Deliver */ }
}

/// <summary>Kcp 2개 + 양방향 링크 + 가상 시계. 시나리오 테스트의 공용 하네스.</summary>
public sealed class KcpPair
{
    public Kcp A, B;
    public SimulatedLink AtoB, BtoA;
    public uint Now;                                   // 가상 시간 — Kcp 생성자에 () => Now 주입
    public MemoryStream ReceivedA, ReceivedB;          // Recv 결과 누적

    public static KcpPair Create(int seed, double loss = 0, int delayMs = 50, ...);
    public void AdvanceTo(uint t)      // 10ms 스텝: 링크 Advance → 양쪽 Update → 양쪽 Recv 드레인
    public bool RunUntil(Func<bool> cond, uint maxMs)  // cond 충족 또는 시간 초과 시 false
}
```

**예제 테스트 (시나리오 #2가 이렇게 작성된다):**

```csharp
[Fact]
public void Loss20_AllDataDelivered_InOrder()
{
    var pair = KcpPair.Create(seed: 42, loss: 0.20, delayMs: 50);
    var payload = TestData.DeterministicBytes(64 * 1024, seed: 7);
    foreach (var chunk in payload.Chunk(1024)) Assert.Equal(0, pair.A.Send(chunk));

    Assert.True(pair.RunUntil(() => pair.ReceivedB.Length == payload.Length, maxMs: 120_000));
    Assert.Equal(payload, pair.ReceivedB.ToArray());
    Assert.True(pair.A.GetStats().RetransmitSegments > 0);   // 손실이 실제로 있었음을 확인
}
```

**요구 사항**: 시드 고정 시 100% 재현. 가상 시간이므로 "2분짜리" 시나리오도 수십 ms에 끝나야 한다(실제 sleep 금지).
**함정**: `Advance`는 반드시 도착 시각 순 정렬 배달(순서 뒤섞임은 at 조작으로만 발생시킨다). 대역폭 제한은 마지막에 구현(우선순위 낮음).

### P2-4. 프로토콜 시나리오 테스트 `필수`

| # | 시나리오 | 검증 |
|---|---|---|
| 1 | 무손실 1만 메시지 | 바이트 단위 일치, 순서 보존 |
| 2 | 손실 10%/20%/40% | 전량 도달, 재전송 통계 증가 |
| 3 | 순서 뒤섞임 30% | 수신 순서 보존 |
| 4 | 중복 20% | 앱 수신은 1회(RepeatSegments 증가) |
| 5 | 지연 200ms + 지터 100ms | rx_rto 발산 없음(상한 검증) |
| 6 | 수신측 윈도 0 (Recv 정지) | 송신 정지 → WASK/WINS 프로브 → 재개 |
| 7 | mss×(rcv_wnd−1) 크기 메시지 | 정상 왕복. rcv_wnd 이상 조각은 `Send` -2 (B-04 회귀) |
| 8 | 스트림 모드 | 경계 없는 바이트열 무결성 |
| 9 | 상대 무응답 | `IsDeadLink == true` 전이 (B-03 회귀) |
| 10 | 0바이트 PUSH 수신 | rcv_queue 미적체(커밋 217ba37 회귀) |
| 11 | 시계를 0xFFFF_0000에서 시작 | 랩어라운드에서 Update/재전송 정상 (B-01/B-02 회귀) |
| 12 | conv 불일치 주입 | -1 반환, 상태 오염 없음 |
| 13 | 잘린 헤더/거짓 length 주입 | -1/-2 반환, 예외 없음 |
| 14 | `ackNoDelay` on/off | on일 때 ACK 지연 < interval |

**실행 절차**: 1→7→2→3→4→8 순으로 구현(하네스가 단단해진 뒤 나머지). 각 테스트는 `KcpPair` 옵션만 다르게 주는 20줄 내외가 되어야 한다 — 그렇지 않으면 하네스를 보강한다.

### P2-5. 상호운용 테스트 `권장`

- 원본 C kcp(또는 kcp-go)로 캡처한 패킷 시퀀스를 리소스 파일(`tests/.../golden/*.bin`)로 저장 → `Input`에 흘려 동일 앱 데이터 복원 검증.
- 캡처 방법: kcp-go의 예제에 파일 덤프 훅을 넣어 생성하고, 생성 스크립트를 `tests/golden/README.md`에 기록(재생성 가능하게).
- `선택`: CI에서 kcp-go 에코 서버를 도커로 띄워 RawMode 실통신.

### P2-6. 장시간·스트레스 테스트 `권장`

`[Trait("Category","Soak")]`로 분리, CI 야간 스케줄(`on: schedule`) 실행: 가상 시간 24시간 분량, 손실 5% 링크에서 메모리 증가율(풀 상한 준수)·통계 일관성 검증.

### P2-7. 커버리지 게이트 `권장`

coverlet로 코어(`src/KcpCSharpR1`) 라인 커버리지 **85% 이상**을 CI 게이트로. `dotnet test /p:Threshold=85 /p:ThresholdType=line /p:Include="[KcpCSharpR1]*"`.

---

## 6. Phase 3 — 성능 최적화 (권장, 순서 엄수: 측정 → 개선)

> 목표: "1만 세션급 서버에서도 GC 스파이크 없이 도는 코어".
> **공통 절차**: ① 기준선 실행·기록 → ② 브랜치에서 변경 → ③ `dotnet run -c Release --project benchmarks -- --filter '*대상*'` 재측정 → ④ PR에 전/후 표 첨부 → ⑤ 회귀 없으면 머지. 이 절차를 벗어난 "감으로 하는 최적화"는 금지.

### P3-1. 벤치마크 기준선 확보 `필수(이 페이즈 내)`

```
dotnet new console -o benchmarks/KcpCSharpR1.Benchmarks
dotnet add benchmarks/KcpCSharpR1.Benchmarks package BenchmarkDotNet
```

| 벤치마크 | 내용 | 지표 |
|---|---|---|
| `SendRecvRoundtrip` | 무손실 KcpPair, 1KB×1000 | ns/op, **B/op(할당)** |
| `InputAckBatch` | ACK 64개 묶음 파싱 | ns/op, B/op |
| `FlushLargeWindow` | snd_buf 1024 상태의 Flush | ns/op |
| `FragmentationLarge` | 32KB 메시지 조각화 왕복 | ns/op, B/op |

모든 클래스에 `[MemoryDiagnoser]`. **목표(개선 후)**: 정상 상태 Send→Flush→Input→Recv 경로 **관리 힙 할당 0 B/op**.
**완료 조건**: 기준선 수치를 `docs/benchmarks.md`에 기록(재현 명령어 포함).

### P3-2. Span 기반 코덱 전환 `권장` (PF-02, PF-03)

1. `KcpCodec.cs` 신설 — `System.Buffers.Binary.BinaryPrimitives`(netstandard2.1 포함):

```csharp
internal static class KcpCodec
{
    public static void WriteU32(Span<byte> p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(p, v);
    public static uint ReadU32(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32LittleEndian(p);
    // U16/U8 동일. 기존 ikcp_encode*/decode*는 이 위에서 재구현하고 [Obsolete]
}
```

2. 공개 API에 Span 오버로드 추가(기존 `byte[]` 오버로드 유지 — MonoGame·기존 사용자 호환):

```csharp
public int Send(ReadOnlySpan<byte> data);
public int Input(ReadOnlySpan<byte> data, bool regular, bool ackNoDelay);
public int Recv(Span<byte> buffer);
```

3. `ByteBuffer`의 `Read()/Get()` 임시 배열 제거, `ReadBytes` 루프를 `Buffer.BlockCopy`로.

**검증**: `InputAckBatch` 벤치 할당 0. **함정**: netstandard2.1에는 `byte[]`→`Span` 암시 변환이 다 되지만, `Socket` API의 Span 오버로드는 net8.0 전용 — 소켓 쪽은 `#if NET8_0_OR_GREATER` 분기(전송 계층 일이므로 Phase 4에서).

### P3-3. 버퍼 관리를 ArrayPool로 전환 `권장` (B-07 근본 해결)

1. `Segment.data`를 `ByteBuffer` → **`ArrayPool<byte>.Shared` 대여 배열 + 길이 필드**로:

```csharp
internal sealed class Segment
{
    internal byte[] data = Array.Empty<byte>();  // ArrayPool 대여. Rent는 요청보다 클 수 있으므로
    internal int len;                            // 유효 길이는 반드시 len으로 관리
    // Get(size):  data = ArrayPool<byte>.Shared.Rent(size); len = 0;
    // reset():    if (data.Length > 0) ArrayPool<byte>.Shared.Return(data); data = Array.Empty<byte>();
}
```

2. `ByteBuffer`를 코어에서 삭제. 전송 계층 수신 조립 버퍼도 ArrayPool 기반으로.

**함정 3가지를 반드시 지킨다**: ① 이중 반환 금지(`reset` 멱등 보장 — `Array.Empty` 센티널), ② Rent 배열은 요청보다 클 수 있으니 `data.Length`를 절대 유효 길이로 쓰지 않는다, ③ 민감 데이터 없으므로 `clearArray: false`(성능).
**검증**: `FragmentationLarge` 벤치 할당 0, 전 테스트 통과, `ByteBuffer.cs` 삭제 커밋.

### P3-4. Segment 큐 자료구조 교체 `권장` (PF-04)

1. `RingBuffer<T>`(2의 거듭제곱 용량 원형 덱) 구현:

```csharp
internal sealed class RingBuffer<T>
{
    private T[] items; private int head, count;   // capacity는 2^n 유지 → 마스크 인덱싱
    public int Count => count;
    public ref T this[int i] => ref items[(head + i) & (items.Length - 1)];
    public void PushBack(T item);                 // 가득 차면 2배 성장
    public ref T PeekFront();
    public void RemoveFront(int n);               // head = (head+n)&mask; count -= n;  O(1)
}
```

2. `snd_queue`/`rcv_queue`/`snd_buf`/`rcv_buf`의 `List<Segment>` 교체. `RemoveRange(0,n)` 패턴 전부 제거. `parse_data`의 중간 `Insert`는 rcv_buf에만 필요 — 뒤에서부터 밀어넣는 현행 로직을 링버퍼 인덱스 연산으로 이식.
3. RingBuffer 자체 단위 테스트(경계: 성장, 랩어라운드 인덱싱, RemoveFront 전량).

**검증**: `FlushLargeWindow` 개선 확인. 시나리오 테스트 14종 전체 통과가 이식 정확성의 근거.

### P3-5. 풀 소유권 정리 `권장` (PF-06)

Segment 풀을 전역 static → `Kcp` 인스턴스 필드로(2.3 규약상 락 제거 가능). `Kcp.Release()` 추가: 모든 큐·풀의 세그먼트를 ArrayPool에 반환(세션 종료 시 호출). **검증**: 벤치에서 lock 소멸 확인, Soak 테스트에서 Release 후 재사용 시 예외 없음.

### P3-6. ACK 탐색 개선 `선택` (PF-05)

`snd_buf`가 sn 오름차순인 성질을 이용해 `parse_ack`/`parse_fastack`을 이분 탐색으로. 벤치(`InputAckBatch`, 윈도 1024)로 이득이 확인될 때만 머지.

### P3-7. 소켓 I/O 현대화 `필수(Phase 4의 전제)` (PF-08, S-05)

Phase 4 전송 계층의 표준으로 확정해 두는 결정 사항:

- net8.0: `Socket.ReceiveFromAsync(Memory<byte>, SocketFlags, SocketAddress, CancellationToken)` — `SocketAddress` 재사용으로 수신 경로 무할당. netstandard2.1: `SocketAsyncEventArgs` 재사용 구현으로 폴백(`#if` 분기).
- 소켓 옵션: `ReceiveBufferSize = 1 << 20`(기본 1MB, 옵션화), `SendBufferSize` 동일. Windows 서버 필수: `SIO_UDP_CONNRESET` 비활성화(ICMP Port Unreachable로 인한 예외 방지):

```csharp
const int SIO_UDP_CONNRESET = -1744830452;
if (OperatingSystem.IsWindows())
    socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
```

- 수신 루프 1개가 소켓을 계속 읽어 세션 디스패치(호출당 1패킷 제한 제거).

---

## 7. Phase 4 — 전송 계층: 게임용 클라이언트/서버 세션 (필수)

> 목표: "이 저장소만으로 게임 클라·서버를 다 만들 수 있다". B-11, S-02의 근본 해결.
> **구현 순서가 중요하다**: 순수 로직(프레이밍·쿠키·상태 머신)을 소켓 없이 먼저 만들어 단위 테스트로 굳히고, 소켓은 마지막에 씌운다.

### P4-1a. 프레이밍 코덱 `필수`

UDP 페이로드 첫 1바이트 = 프레임 타입:

```
0x01 SYN        클라→서버 연결 요청   [0x01][protocolVer u8][clientNonce u32][cookie u32(최초 0)]
0x02 SYN_COOKIE 서버→클라 쿠키 응답   [0x02][protocolVer u8][clientNonce u32][cookie u32]
0x03 SYN_ACK    서버→클라 연결 수락   [0x03][conv u32][serverNonce u32]
0x04 KCP        신뢰 데이터            [0x04][KCP 원본 패킷(conv 포함) ...]
0x05 RAW        비신뢰 데이터          [0x05][conv u32][페이로드 ...]
0x06 PING       keepalive             [0x06][conv u32][timestamp u32]
0x07 PONG       keepalive 응답        [0x07][conv u32][timestamp u32(에코)]
0x08 FIN        정상 종료 통지         [0x08][conv u32][reason u8]
```

- `Framing.cs`: 각 프레임의 인코딩/디코딩을 **순수 정적 함수**로 구현(소켓 무관) + 프레임별 왕복 단위 테스트.
- **RawMode**: `KcpClient` 옵션 — 프레이밍 없이 순수 KCP 패킷만 송수신(kcp-go 등 기존 서버와 상호운용). conv는 사용자 지정.
- 이 명세를 책 6장과 부록 C에 그대로 수록한다.

### P4-1b. 쿠키 핸드셰이크 `필수` (S-04, B-11 해결)

- 서버는 SYN에 **상태를 만들지 않고** `cookie = HMAC-SHA256(serverSecret, 클라IP‖포트‖타임버킷)[0..4]`를 SYN_COOKIE로 반환.
- 클라가 쿠키를 담아 SYN 재전송 → 서버가 검증 후 **그때** 세션 생성 + conv **서버 할당**(단조 증가 카운터, 0은 예약) + SYN_ACK.
- 타임버킷 30초, 현재/직전 버킷 2개 허용(경계 재전송 대응). `serverSecret`은 기동 시 `RandomNumberGenerator`로 생성.
- `HandshakeCookie.cs`로 분리, 단위 테스트: 유효/만료/변조/다른 IP.

### P4-2a. KcpSession 상태 머신 `필수`

소켓 없이 `Action<byte[], int> rawSender`와 가상 시계만 주입받는 순수 상태 머신으로 만든다(→ 가상 시간 단위 테스트 가능).

```csharp
public enum KcpSessionState { Closed, Connecting, Connected, Closing, Dead }

public sealed class KcpSession
{
    public uint Conv { get; }
    public KcpSessionState State { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public int PingMs { get; }                 // PING/PONG 왕복
    public int SrttMs { get; }                 // Kcp.Srtt
    public KcpStats Stats { get; }

    public int Send(ReadOnlySpan<byte> data);            // 신뢰·순서 보장 (KCP)
    public int SendUnreliable(ReadOnlySpan<byte> data);  // 비신뢰 (RAW 프레임)
    public int Receive(Span<byte> buffer);               // 폴링 수신 (신뢰 채널)
    public void Disconnect(byte reason = 0);

    public event Action<KcpSession>? Disconnected;
}
```

**상태 전이표 (구현·테스트의 기준):**

| 현재 | 이벤트 | 다음 | 액션 |
|---|---|---|---|
| Closed | Connect 요청(클라) | Connecting | SYN(cookie=0) 송신, 재시도 타이머 |
| Connecting | SYN_COOKIE 수신 | Connecting | cookie 포함 SYN 재송신 |
| Connecting | SYN_ACK 수신 | Connected | conv로 Kcp 생성, Connected 통지 |
| Connecting | HandshakeTimeout(재시도 3회 소진) | Closed | 실패 통지 |
| Connected | KCP/RAW 수신 | Connected | Input / 수신 큐 적재, LastReceive 갱신 |
| Connected | PING 수신 | Connected | PONG 응답 |
| Connected | FIN 수신 | Closed | Disconnected 통지, 자원 해제 |
| Connected | Disconnect() 호출 | Closing | FIN 송신(즉시 1회 + linger 동안 재송신) |
| Closing | FinLinger 경과 | Closed | Kcp.Release, 소켓 핸들 분리 |
| Connected | SessionTimeout 또는 Kcp.IsDeadLink | Dead | Disconnected 통지 |

**수명 파라미터 (`KcpSessionOptions`):**

| 항목 | 기본값 | 동작 |
|---|---|---|
| `HandshakeTimeout` | 5초 | SYN 1초 간격 재시도 3회 후 실패 |
| `PingInterval` | 2초 | 유휴 시 PING 송신 |
| `SessionTimeout` | 10초 | 마지막 수신 후 경과 시 Dead |
| `FinLinger` | 2초 | FIN 후 잔여 재전송 처리 시간 |
| `MaxSendBacklog` | 1024 | `WaitSnd` 초과 시 Send가 오류 반환(백프레셔) |

### P4-2b. 세션 타이머 `필수`

`KcpSession.Update(uint now)` 하나가 모든 타이머(핸드셰이크 재시도, PING, 타임아웃, linger)와 `Kcp.Update`를 구동. 가상 시간 테스트: "PING 무응답 10초 → Dead", "FIN 후 2초 → Closed" 등 전이표의 각 행을 1테스트로.

### P4-3. KcpClient `필수`

```csharp
public sealed class KcpClient : IDisposable
{
    public KcpClient(KcpSessionOptions? options = null);
    public Task<KcpSession> ConnectAsync(string host, int port, CancellationToken ct = default);
    public void Update();                        // 게임 루프 폴링형 (MonoGame은 이 모델)
    public Task RunAsync(CancellationToken ct);  // 자체 루프형 (콘솔/서버 도구용)
}
```

- 두 구동 모델 중 택1 사용, 혼용 금지(문서화). MonoGame에서는 `Game.Update()`에서 `Update()` 호출(부록 D).
- DNS: `Dns.GetHostAddressesAsync` + IPv4 우선, 리터럴 IP는 즉시 사용(B-10과 동일 정책).

### P4-4a. KcpServer 수신 루프 `필수`

```csharp
public sealed class KcpServer : IDisposable
{
    public KcpServer(int port, KcpServerOptions? options = null);
    public void Start();
    public void Stop();

    public event Action<KcpSession>? SessionConnected;
    public event Action<KcpSession, ReadOnlyMemory<byte>>? ReliableReceived;
    public event Action<KcpSession, ReadOnlyMemory<byte>>? UnreliableReceived;
    public event Action<KcpSession>? SessionDisconnected;

    public IReadOnlyCollection<KcpSession> Sessions { get; }
}
```

수신 루프 의사코드(단일 소켓, 단일 루프):

```csharp
while (!ct.IsCancellationRequested)
{
    int n = await socket.ReceiveFromAsync(buffer, SocketFlags.None, senderAddr, ct);
    switch (buffer[0])
    {
        case Frame.Syn:       HandleSyn(senderAddr, buffer[..n]); break;   // 무상태 쿠키 응답 또는 세션 생성
        case Frame.Kcp:
        case Frame.Raw:
        case Frame.Ping: case Frame.Fin:
            if (sessions.TryGetValue(senderAddr, out var s)) s.OnFrame(buffer[..n], now);
            break;                                                          // 미등록 주소는 무시(무응답)
        default: break;                                                     // 알 수 없는 타입 무시
    }
}
```

- 세션 테이블: net8.0은 `Dictionary<SocketAddress, KcpSession>`(무할당 조회), netstandard2.1은 `Dictionary<IPEndPoint, ...>` 폴백.
- 한도: `MaxSessions`(기본 8192) 초과 SYN은 무응답 폐기. 쿠키 방식이라 미완료 핸드셰이크 상태는 서버에 존재하지 않는 게 정상.

### P4-4b. Update 스케줄러 `필수`

세션 1만 개에서 매 틱 전 세션 `Kcp.Update` 호출은 금지. **최소 힙 스케줄러**:

- 각 세션은 `Kcp.Check()` 결과(다음 깨울 시각)를 힙에 등록.
- 타이머 루프(주기 5ms)는 힙에서 기한 도래 세션만 pop → `Update` → 재`Check` → 재등록.
- `Send`/`Input` 발생 시 그 세션을 "즉시" 위치로 갱신(지연 ACK 방지). 힙 갱신은 lazy-deletion(스탬프 비교)으로 O(log n).

### P4-4c. 백프레셔·이벤트·한도 `필수`

- 수신 이벤트는 수신 루프 스레드에서 발화됨을 문서화("게임 로직 큐로 넘겨라" — 책 6장에서 패턴 제시).
- `MaxSendBacklog` 동작 테스트, 세션 수 상한 테스트.
- `선택` 세션 마이그레이션(모바일 IP 변경): 암호화(P5-1) 활성 시에만 — 인증된 패킷이 새 EndPoint에서 오면 테이블 키 갱신.

### P4-5. 통합 테스트 `필수`

- 실소켓 루프백 테스트(포트 27100~27199): 연결→송수신→정상 종료→타임아웃 종료 전 시나리오.
- SimulatedLink를 전송 계층 아래에 끼울 수 있게 `IUdpTransport` 인터페이스(실소켓/가상 구현 2종)를 두고, 손실 환경 핸드셰이크(SYN 손실, SYN_ACK 손실, FIN 손실) 테스트.
- **완료 조건(Phase 4 전체)**: 샘플 1(에코)이 공개 API만으로 동작. LoadTest(P7-6)로 1,000 동시 세션·60초에서 오류 0, 힙 안정.

---

## 8. Phase 5 — 보안·신뢰성 확장 (권장~선택)

### P5-1. 패킷 암호화 `권장` (S-03)

- **AES-256-GCM** (nonce 12B = conv 4B + 송신 카운터 8B, 태그 16B). KCP(0x04)·RAW(0x05) 프레임의 페이로드를 암호화, 프레임 타입 바이트는 평문.
- 키 교부: 라이브러리는 키 합의를 하지 않는다(게임은 보통 TLS 웹 API 로그인에서 세션 키 수령). `KcpSessionOptions.EncryptionKey`(32B) 주입. `선택`: X25519 핸드셰이크 내 키 교환.
- 구현: net8.0은 `System.Security.Cryptography.AesGcm` 직접 사용(MonoGame 포함 — 풀 .NET이므로 제약 없음). netstandard2.1 소비자는 `IPacketCipher` 인터페이스에 자체 구현을 주입(문서로 안내).
- 리플레이 방지: 수신 카운터 슬라이딩 윈도(64비트 비트마스크).
- **완료 조건**: 변조 패킷 폐기 / 리플레이 폐기 / on·off 성능 비교 벤치 수치 기록. 책 15장에 반영.

### P5-2. FEC (Reed-Solomon) `선택`

- kcp-go 호환 헤더(`seqid u32 | flag u16(0xF1 data, 0xF2 parity) | size u16`)로 구현해 kcp-go 대비 검증.
- 단계: ① RS 인코더/디코더 단품 + 단위 테스트 → ② shard 그룹(기본 10+3) 전송 계층 통합 → ③ 손실 20% 시뮬레이션에서 재전송률 감소를 벤치로 증명. 책 16장에 반영.

### P5-3. 관측성 `권장`

`KcpServer`가 주기 스냅숏(세션 수, 총 재전송률, RTT p50/p95/p99)을 이벤트로 제공. 포맷은 강요하지 않는다(Prometheus 연동은 사용자 몫, 책 17장에서 예시).

---

## 9. Phase 6 — 온라인 책: 《C#과 KCP로 배우는 실시간 게임 네트워킹》 (필수)

> 문서 세트 대신 **한 권의 온라인 책**을 만든다. 책이 getting-started·프로토콜 해설·튜닝 가이드·FAQ를 전부 흡수하고, 저장소에는 README / CHANGELOG / CONTRIBUTING / benchmarks.md 만 별도로 남긴다. 샘플 5종(10장)과 책 3부가 1:1로 대응하는 것이 이 책의 정체성이다.

### P6-1. 책 인프라 `필수`

- **도구: docsify** (빌드 단계 없음 — 마크다운 + index.html만으로 동작, CI 불필요) + **GitHub Pages**(master 브랜치 `/docs` 폴더 서빙 설정).
- `docs/index.html`:

```html
<!DOCTYPE html>
<html lang="ko">
<head>
  <meta charset="UTF-8">
  <title>C#과 KCP로 배우는 실시간 게임 네트워킹</title>
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="//cdn.jsdelivr.net/npm/docsify@4/themes/vue.css">
</head>
<body>
  <div id="app"></div>
  <script>
    window.$docsify = {
      name: 'kcp-csharp',
      repo: 'jacking75/kcp-csharp',
      loadSidebar: true,
      subMaxLevel: 2,
      search: 'auto',
    };
  </script>
  <script src="//cdn.jsdelivr.net/npm/docsify@4"></script>
  <script src="//cdn.jsdelivr.net/npm/docsify@4/lib/plugins/search.min.js"></script>
  <script src="//cdn.jsdelivr.net/npm/prismjs@1/components/prism-csharp.min.js"></script>
</body>
</html>
```

- `docs/_sidebar.md`에 아래 목차 전체를 등록. `docs/README.md`가 책 표지(소개 + 읽는 순서 안내).
- 다이어그램: mermaid 플러그인(`docsify-mermaid`) 추가 — 시퀀스/상태 다이어그램을 코드로 관리.
- **완료 조건**: GitHub Pages URL에서 목차·검색·코드 하이라이트 동작.

### P6-2. 집필 규약 `필수`

- 파일명: `docs/book/ch01-why-udp.md` … `docs/book/appendix-d-monogame.md`.
- **장 구성 템플릿**: ① 학습 목표(3줄) → ② 본문 → ③ 전체 코드(복붙으로 실행 가능) → ④ 요약(불릿 5개 이내) → ⑤ 연습문제 2~3개(다음 장으로 자연 연결).
- **모든 코드는 실행 가능해야 한다**: 책 코드 조각은 samples/ 의 실제 코드에서 발췌하고, 발췌 원본 경로를 각 코드 블록 위에 주석으로 명기. 라이브러리 API가 바뀌면 책이 깨지는 구조를 의도적으로 만든다(부패 방지).
- 문체: 한국어 경어체 통일. 용어는 부록 E 용어집을 단일 기준으로.
- 각 장 DoD: 코드 실행 확인, 다이어그램 1개 이상(해당 시), 요약·연습문제 포함, 사이드바 링크.

### P6-3. 전체 목차와 장별 수록 내용 `필수`

#### 1부 — 원리: 게임 네트워킹의 기초

**1장. 온라인 게임과 네트워크** *(분량 소, 코드 소)*
- 지연·손실·지터·대역폭 4요소가 게임 경험에 미치는 영향(장르별 허용 지연 표: 격투 <50ms, FPS <100ms, MMO <200ms, 턴제 <1s)
- TCP로 실시간 게임을 만들면 생기는 일: head-of-line 블로킹 — 패킷 1개 손실이 이후 모든 데이터를 막는 과정을 타임라인 그림으로
- UDP의 성질: 비연결·비신뢰·메시지 경계 보존, 그리고 "필요한 만큼만 신뢰성을 얹는다"는 발상
- 신뢰성 스펙트럼: 전부신뢰(TCP류) ↔ 신뢰+순서(KCP) ↔ 비신뢰(raw UDP) — 게임 데이터를 이 축 위에 배치해 보기
- KCP의 위치와 역사(skywind3000 kcp, kcp-go, 그리고 이 라이브러리)
- **실습**: TCP 소켓과 UDP 소켓으로 같은 데이터를 손실 환경(clumsy/tc 도구 안내)에서 보내 지연 분포를 비교하는 콘솔 실험

**2장. 신뢰성을 직접 만들기 — ARQ의 원리** *(분량 중, 코드 중)*
- 순서 번호(sn)와 확인 응답(ack)의 기본 계약
- stop-and-wait → go-back-N → selective repeat 진화 과정(각각의 그림과 처리량 계산)
- 슬라이딩 윈도: 송신 윈도/수신 윈도/혼잡 윈도의 구분
- RTO 계산: RTT 측정, SRTT/RTTVAR 평활(RFC 6298), 지수 백오프
- 빠른 재전송(중복 ACK 기반)과 조기 재전송
- **실습**: 100줄짜리 장난감 stop-and-wait 프로토콜을 SimulatedLink 위에서 구현 — 이 실습이 3장 이해의 발판

**3장. KCP 프로토콜 해부** *(분량 대, 코드 소 — 이 책의 이론 핵심)*
- 24바이트 헤더 필드별 해설(conv/cmd/frg/wnd/ts/sn/una/len) — 바이트 배치 다이어그램
- cmd 4종(PUSH/ACK/WASK/WINS)의 역할과 상호작용
- una 누적 확인 + sn 개별 ACK 병행 구조 — TCP와 다른 점
- 재전송 3경로: RTO 만료 / fastack(빠른 재전송) / early retransmit — `Flush`의 분기 그대로 설명
- 흐름 제어(wnd)와 윈도 프로브(WASK/WINS), 혼잡 제어(cwnd/ssthresh)와 `nc=1`의 의미
- KCP가 TCP 대비 지연을 줄이는 선택들: RTO 1.5배(TCP는 2배), ACK 지연 없음 옵션, 선택적 재전송
- 조각화(frg)와 메시지 모드 vs 스트림 모드
- **실습**: 라이브러리의 로그 마스크를 켜고 손실 링크에서 재전송이 일어나는 로그를 직접 관찰·해석

#### 2부 — 사용법: kcp-csharp 마스터하기

**4장. 시작하기** *(분량 소, 코드 대)*
- 설치(NuGet / 소스 참조), 지원 플랫폼(net8.0, netstandard2.1, MonoGame)
- 첫 프로그램: 에코 서버 20줄 + 클라이언트 20줄(샘플 1 코드) — 한 줄씩 해설
- Update 루프는 왜 필요한가: KCP는 스스로 돌지 않는다(타이머 주도 설계의 이유)
- 흔한 첫 실수: Update 미호출, 방화벽, 포트 충돌 — 증상과 해결

**5장. 코어 API 정복** *(분량 대, 코드 대)*
- `Kcp` 클래스 전 API 투어: 생성(conv/output), Send/Recv/Input/Update/Check/Flush의 계약과 반환 코드 표
- 메시지 모드 vs 스트림 모드 — 경계 보존 여부, 각각의 수신 코드 패턴(PeekSize→Recv)
- 최대 메시지 크기 공식 (rcv_wnd − 1) × mss 와 위반 시 동작(-2)
- `Check()` 기반 절전 스케줄링 — 언제 깨워야 하는지 계산하는 법
- 통계(KcpStats) 읽는 법: 재전송률로 회선 품질 진단
- 스레딩 규약: 인스턴스당 단일 스레드 소유 — 위반 시 증상
- **실습**: KcpPair 하네스로 손실률을 바꿔 가며 통계 변화를 관찰

**6장. 세션과 연결 수명** *(분량 대, 코드 중)*
- KcpClient/KcpServer/KcpSession API 투어
- 핸드셰이크 동작 원리: SYN→SYN_COOKIE→SYN(cookie)→SYN_ACK 시퀀스 다이어그램, 쿠키가 서버를 지키는 방식
- 세션 상태 머신(전이표 그대로 수록)과 각 상태에서 할 수 있는 일
- keepalive(PING/PONG)·타임아웃·FIN 종료 — "UDP에는 끊김이 없다, 우리가 정의한다"
- 수신 이벤트 스레드와 게임 로직 스레드 분리 패턴(큐 넘기기 예제 코드)
- RawMode로 kcp-go 서버와 통신하기
- **실습**: 클라이언트를 강제 종료시켜 서버가 타임아웃으로 세션을 정리하는 과정을 로그로 확인

**7장. 신뢰 채널과 비신뢰 채널** *(분량 중, 코드 중 — 이 책의 실전 핵심)*
- 왜 이동 스냅숏을 KCP로 보내면 안 되는가: 오래된 위치의 재전송 대기가 만드는 지연 — HOL 재현 실험
- 판단 기준표: 데이터 종류별(입력/스냅숏/전투 판정/채팅/상점) 채널 선택과 근거
- `SendUnreliable` API와 수신 처리, 비신뢰 데이터에 순서 번호를 직접 붙여 "최신 것만 취하기" 패턴
- 비신뢰 + 중복 전송(같은 입력을 3패킷에 실어 손실 견디기) 패턴 소개(13장에서 실전 사용)
- **실습**: 같은 이동 데이터를 신뢰/비신뢰로 각각 보내 손실 20% 환경에서 체감 지연 비교

**8장. 파라미터 튜닝** *(분량 중, 코드 소)*
- NoDelay 4개 인자(nodelay/interval/resend/nc)의 정확한 의미와 프리셋 4종(normal~fast3)
- 윈도 크기 산정 공식: 대역폭 × RTT ÷ MTU (계산 예제 포함)
- MTU: 왜 기본 1400이고 모바일은 1200인가(터널링·IPv6 오버헤드)
- interval의 트레이드오프: 지연 vs CPU vs 배터리
- 장르별 권장 설정 표(12장의 표를 책으로 이관)
- **실습**: LoadTest 도구로 프리셋별 RTT p95 비교 측정

#### 3부 — 실전: 다섯 개의 프로젝트 (샘플 5종과 1:1, 난이도 오름차순)

**9장. 프로젝트 ① 에코 — 첫 왕복** *(★1)*
- 샘플 1 전체 코드 워크스루(서버 → 클라 순), 연결·송수신·종료의 최소 사이클
- "보낸 바이트가 그대로 돌아온다"를 확인하는 것의 의미(무결성 검증 습관)
- 연습문제: 에코에 왕복 시간 측정을 붙여 나만의 ping 도구 만들기

**10장. 프로젝트 ② 채팅 — 애플리케이션 프로토콜 설계** *(★2)*
- 길이 접두사 + 메시지 타입 프레이밍을 손으로 구현(직렬화 라이브러리 없이) — 부분 수신·경계 처리
- 메시지 정의 표(SetNick/Say/Whisper/…)와 버전 필드를 넣는 이유
- 세션 컬렉션 관리와 브로드캐스트, 접속/퇴장 알림
- 잘못된 입력(빈 닉네임, 과대 메시지)의 서버 측 검증 — "서버는 클라를 믿지 않는다" 첫 등장
- 연습문제: 방(room) 개념 추가, 귓속말 대상 없음 오류 처리

**11장. 프로젝트 ③ 오목 — 권위 서버와 턴제 게임** *(★3, MonoGame 시작)*
- 권위 서버(authoritative server) 개념: 게임 상태는 서버만 소유, 클라는 요청과 렌더링만
- 방 생성·매칭(2인)·관전, 턴 검증(내 차례인가, 그 자리가 비었는가), 승패 판정
- **재접속**: 연결이 끊겨도 게임 상태를 유지하고 토큰으로 복귀 — 상태 스냅숏 재동기화
- MonoGame 입문: Game1 루프 구조, `Update()`에서 KcpClient 폴링, SpriteBatch로 보드 그리기, 마우스 입력
- 연습문제: 무르기(상대 동의) 기능, 제한 시간 추가

**12장. 프로젝트 ④ 이동 동기화 — 실시간의 시작** *(★4)*
- 고정 틱 서버(20Hz) 루프 작성법 — 가변 프레임 클라와의 관계
- 스냅숏 브로드캐스트를 **비신뢰 채널**로: 패킷 설계(틱 번호, 엔티티 위치 배열)와 대역폭 계산(인원수×틱레이트×엔티티 크기 — 계산표 포함)
- 클라이언트 보간(interpolation): 100ms 지연 렌더링 버퍼, 스냅숏 2개 사이 lerp — MonoGame으로 구현
- 늦게 온 스냅숏 버리기(틱 번호 비교), 입장/퇴장·채팅은 신뢰 채널로
- HUD: RTT(`SrttMs`)·재전송률 표시
- 연습문제: 틱레이트를 10/20/30Hz로 바꿔 체감 비교, 외삽(extrapolation) 실험

**13장. 프로젝트 ⑤ 탱크 배틀 — 예측과 보정** *(★5, 종합)*
- 입력 지연 문제: 내 입력이 서버를 다녀와야 반영된다면 RTT만큼 둔해진다
- **클라이언트 예측**: 입력 시퀀스 번호, 로컬 즉시 적용, 미확인 입력 버퍼
- **서버 reconciliation**: 서버가 "마지막 처리 입력 번호 + 권위 상태"를 회신 → 클라는 되감고 미확인 입력 재적용
- 입력 중복 전송: 비신뢰 패킷 1개에 최근 입력 3개를 실어 손실 내성 확보
- **지연 보상(lag compensation)**: 서버가 과거 스냅숏 링버퍼(1초)를 되감아 사격 판정 — "내 화면에서 맞췄으면 맞은 것"
- 치트 관점: 예측·보정 구조에서 서버가 반드시 검증해야 하는 것들(이동 속도, 발사 쿨다운)
- 연습문제: 예측 끄기 토글로 체감 비교, 점수판을 신뢰 채널로 구현

#### 4부 — 심화

**14장. 성능: 무할당 네트워킹** *(코드 중)*
- 게임 서버에서 GC가 문제인 이유(스파이크 = 프레임 드랍), Gen0/1/2 기초
- 이 라이브러리의 무할당 설계: ArrayPool, Span 코덱, 링버퍼, 세그먼트 풀 — 각각 before/after 벤치 수치와 함께
- BenchmarkDotNet 읽는 법·재현하는 법, 벤치 기준선 관리(docs/benchmarks.md)
- 서버 용량 산정 감각: 세션당 메모리·CPU 예산 계산

**15장. 보안** *(코드 중)*
- UDP 위협 모델: 스푸핑, 증폭 공격, 상태 고갈, 리플레이, 패킷 위조
- 쿠키 핸드셰이크가 막는 것과 못 막는 것
- AES-GCM 암호화: nonce 설계, 리플레이 윈도, 키는 어디서 오는가(TLS 로그인 → 세션 키 전달 패턴)
- 암호화 성능 비용 실측치
- 안티치트는 별개 문제: 전송 보안이 막지 못하는 것들

**16장. 손실을 이기는 또 다른 방법 — FEC** *(코드 소)*
- 재전송의 근본 한계: 복구에 최소 1 RTT
- Reed-Solomon 개념을 그림으로(데이터 10 + 패리티 3 → 아무 10개로 복원)
- 대역폭 30% 추가 vs 재전송 지연 제거 — 어떤 게임에 유리한가
- kcp-go FEC 포맷 호환 구현과 손실 20% 실측 비교

**17장. 운영** *(코드 소)*
- 봐야 할 지표: 재전송률(>5% 경보), RTT p50/p95/p99, 세션 수, 초당 바이트
- LoadTest 도구 사용법 전체(옵션 표), 결과 리포트 해석
- 흔한 장애 사례집: MTU 블랙홀, NAT 타임아웃(그래서 PING 간격 < NAT 타임아웃), 모바일 IP 변경, 수신 버퍼 오버플로
- 배포 체크리스트: 소켓 버퍼, 포트/방화벽, 서버 틱 예산

#### 부록

- **A. API 레퍼런스** — 공개 타입 전체. XML 주석에서 생성하되 docsify 페이지로 정리
- **B. FAQ** — 최소 12항목: "TCP 대신 항상 KCP?", "conv란?", "패킷이 안 온다(방화벽/NAT)", "49일 문제", "여러 스레드에서 Send 해도 되나", "Unity에서도 쓸 수 있나(netstandard2.1 경로 안내)" 등
- **C. 원본 kcp / kcp-go 상호운용** — RawMode 설정, 골든 벡터, 파라미터 대응표
- **D. MonoGame 연동 가이드** — 프로젝트 생성(`dotnet new mgdesktopgl`), `Game.Update`에 KcpClient 폴링 통합, 고정 틱과 `IsFixedTimeStep`, 텍스트 렌더링(SpriteFont 최소 사용법), 샘플 3~5의 공통 구조 해설
- **E. 용어집** — ARQ, RTO, SRTT, HOL, conv, snapshot, reconciliation … 한/영 대응표

### P6-4. 집필 순서 `필수`

1. 4장(시작하기) — 샘플 1 완성 직후. 가장 많은 독자가 읽는 장을 최우선.
2. 9~13장 — 각 샘플 완성 직후 그 장을 쓴다(코드가 신선할 때).
3. 5~8장 — API가 안정된 후(M4 이후).
4. 1~3장(이론) — 언제든 가능하나, 실전 장에서 역참조가 필요하므로 M5 전 완성.
5. 14~17장 — 해당 Phase(3, 5) 완료 후.
6. 부록 — 수시 누적, M5에서 정리.

**완료 조건(Phase 6 전체)**: 전 장 DoD 충족, GitHub Pages 공개, README에서 책으로 링크.

### P6-5. 저장소 위생 `권장`

`README.md` 전면 개편(한 줄 소개+배지 / KCP란 / 30초 시작 코드 / 기능 표 / 책·샘플 링크 / kcp 계보 / 라이선스), `README.en.md`, `CHANGELOG.md`(Keep a Changelog), `CONTRIBUTING.md`(빌드·테스트 방법, PR 규칙), 이슈 템플릿 2종.

---

## 10. Phase 7 — 샘플 프로젝트 5종 + 도구 (필수)

> 난이도 오름차순 5종. **각 샘플 = 책 3부의 한 장**과 1:1 대응. 공통 규칙:
> - 각 샘플 디렉터리에 자체 `README.md`(실행법 2줄 + 배우는 것 + 책 해당 장 링크).
> - 서버는 전부 콘솔. 클라는 샘플 1·2 콘솔, 3~5 **MonoGame**(DesktopGL, `dotnet new mgdesktopgl`).
> - 공용 코드는 각 샘플 내 `Shared` 프로젝트로(샘플 간 공유 금지 — 복붙 가능한 자기완결성이 우선).
> - 외부 패키지는 MonoGame 외 금지(직렬화도 수동 인코딩 — 학습 목적).

### P7-1. 샘플 ① Echo `★1 입문` — UDP 27001, 규모 S

- **한 줄**: 접속해서 보낸 것을 그대로 받는다. 첫 성공 경험.
- **배우는 것**: ConnectAsync/Update 루프/Send/Receive/Disconnect의 최소 사이클.
- **기능 명세**: 서버 — 세션별 에코, 접속/종료 로그. 클라 — 1초마다 카운터 메시지 전송, 에코 검증(불일치 시 빨간 로그), Ctrl+C 정상 종료.
- **구조**: `Server/Program.cs`(~40줄), `Client/Program.cs`(~40줄).
- **구현 순서**: ① 서버(이벤트 3개 구독만으로 완성) → ② 클라 → ③ README.
- **완료 조건**: 두 콘솔을 띄워 60초 무결성 100%, 클라 강제 종료 시 서버가 10초 내 세션 정리 로그.

### P7-2. 샘플 ② Chat `★2 초급` — UDP 27002, 규모 S~M

- **한 줄**: 닉네임과 귓속말이 있는 콘솔 채팅 — 애플리케이션 프로토콜 설계 입문.
- **배우는 것**: 길이 접두사+타입 프레이밍, 서버 측 입력 검증, 브로드캐스트, 세션 컬렉션.
- **프로토콜** (`[u16 len][u8 type][payload]`, 문자열은 `[u16 len][utf8]`):

| 방향 | 타입 | 페이로드 |
|---|---|---|
| C→S | 0x01 SetNick | nick |
| C→S | 0x02 Say | text |
| C→S | 0x03 Whisper | targetNick, text |
| S→C | 0x81 Welcome | motd, 접속자 목록 |
| S→C | 0x82 UserJoined / 0x83 UserLeft | nick |
| S→C | 0x84 Chat | nick, text |
| S→C | 0x8F Error | code(u8), message |

- **구현 순서**: ① Shared에 메시지 코덱+왕복 단위 테스트 → ② 서버(SetNick 검증: 중복·빈 값·16자 초과) → ③ 클라(수신 스레드 없이 폴링, 입력은 `Console.ReadLine` 스레드→큐) → ④ 귓속말·오류 처리.
- **완료 조건**: 클라 3개 동시 접속 시나리오(입장 알림, 브로드캐스트, 귓속말, 퇴장 알림) 수동 검증 + 코덱 단위 테스트.

### P7-3. 샘플 ③ Gomoku(오목) `★3 중급` — UDP 27003, 규모 M, **MonoGame 클라**

- **한 줄**: 매칭·턴 검증·재접속이 있는 2인 오목 — 권위 서버 패턴의 교과서.
- **배우는 것**: 서버가 상태를 소유하는 구조, 요청-검증-알림 사이클, 재접속 복구, MonoGame 기초(렌더링·마우스 입력·네트워크 폴링).
- **기능 명세**: 대기열 매칭(2인 자동), 15×15 보드, 턴 검증(차례·빈칸), 오목 판정, 항복, 관전자 입장, **재접속**(발급 토큰으로 30초 내 복귀 → 보드 전체 재동기화), 게임 내 채팅.
- **프로토콜 주요 메시지**: C→S `JoinQueue / PlaceStone(x,y) / Surrender / Reconnect(token) / Chat`, S→C `MatchFound(roomId, myColor, oppNick, token) / StonePlaced(x,y,color) / YourTurn / GameOver(result) / StateSync(보드 225바이트 + 차례) / Chat / Error`.
- **구조**: `Server/`(콘솔: RoomManager, GameRoom, MatchQueue), `Client/`(MonoGame: 보드 렌더, 입력, 네트워크), `Shared/`(메시지 코덱, 오목 판정 로직 — 판정을 공유해 서버·클라 동일 규칙 보장).
- **구현 순서**: ① Shared 코덱+오목 판정(단위 테스트: 가로/세로/대각/육목 무효) → ② 서버를 콘솔 텍스트 클라로 먼저 완성(MonoGame 이전에 로직 검증) → ③ MonoGame 클라(보드 그리기 → 돌 놓기 → 네트워크 연결) → ④ 재접속 → ⑤ 관전.
- **완료 조건**: 클라 2개로 완주(승패), 게임 중 클라 1개 강제 종료 후 30초 내 재실행 시 이어서 진행, 판정 단위 테스트 통과.

### P7-4. 샘플 ④ MoveSync `★4 중상급` — UDP 27004, 규모 M~L, **MonoGame 클라**

- **한 줄**: 20Hz 틱 서버와 스냅숏 보간 — 실시간 동기화의 표준형.
- **배우는 것**: 고정 틱 루프, **신뢰/비신뢰 채널 구분**(이 라이브러리 활용의 핵심 패턴), 스냅숏 설계와 대역폭 계산, 클라 보간, RTT HUD.
- **기능 명세**: 접속 시 색상·시작 위치 부여(신뢰), WASD 입력을 서버로(비신뢰, 10Hz), 서버 20Hz 틱에서 이동 적용·전체 스냅숏 브로드캐스트(비신뢰), 입장/퇴장/채팅(신뢰), 클라 100ms 보간 렌더, HUD(RTT/재전송률/수신 스냅숏 레이트), 최대 16인.
- **스냅숏 패킷**: `[u32 tick][u8 count]{ [u32 playerId][f32 x][f32 y][u8 dir] }×count` — 16인 기준 페이로드 ≈ 5+13×16 = 213B, 20Hz → 클라당 다운로드 ≈ 34kbps(계산 과정을 README와 책 12장에 수록).
- **구현 순서**: ① Shared 코덱 → ② 서버 틱 루프(입력 적용·경계 클램프·스냅숏 송신) → ③ MonoGame 클라: 보간 없이 최신 스냅숏 즉시 렌더(뚝뚝 끊기는 상태를 먼저 체험) → ④ 보간 버퍼 추가(개선 체감) → ⑤ 늦은 스냅숏 폐기(틱 비교) → ⑥ HUD.
- **완료 조건**: 클라 4개 동시 이동이 부드럽게 상호 표시, clumsy 등으로 손실 10% 걸어도 위치 동기화 유지(신뢰 채널이었다면 생겼을 끊김이 없음을 README에 비교 기록), LoadTest 봇 모드로 16인 부하 시 서버 틱 지연 < 5ms.

### P7-5. 샘플 ⑤ TankBattle `★5 고급` — UDP 27005, 규모 L, **MonoGame 클라**

- **한 줄**: 클라 예측·서버 보정·지연 보상까지 넣은 미니 대전 액션 — 이 라이브러리로 만드는 실전 아키텍처의 결정판.
- **배우는 것**: 클라이언트 예측/reconciliation, 입력 중복 전송, 지연 보상 되감기, 서버 검증(치트 방지 기초), 신뢰/비신뢰 종합 운용.
- **기능 명세**: 탱크 이동+포탑 회전+발사, 최대 8인 데스매치, 리스폰, 점수판. 입력은 비신뢰(시퀀스 번호, 최근 3개 중복 탑재, 30Hz), 스냅숏 비신뢰 20Hz(서버는 마지막 처리 입력 번호 포함), 발사/피격/사망/점수는 신뢰 채널. 서버는 이동 속도·발사 쿨다운 검증. 지연 보상: 서버가 과거 1초(20스냅숏) 링버퍼를 클라 보간 지연+RTT/2 만큼 되감아 히트 판정. 봇 모드(`--bots 7`)로 혼자 테스트 가능.
- **구조**: `Server/`(Simulation, LagCompensator, InputBuffer), `Client/`(Prediction, Reconciliation, Interpolation, HUD), `Shared/`(이동·물리 코드 공유 — 예측과 서버가 동일 코드를 써야 보정 오차가 0에 수렴).
- **구현 순서**: ① Shared: 탱크 물리(입력→상태 순수 함수) + 단위 테스트 → ② 서버 틱: MoveSync 구조 재활용 + 입력 버퍼 → ③ 클라: 예측 없이(입력을 서버 왕복으로만 반영 — 둔한 조작감을 먼저 체험) → ④ 예측+보정 추가(마지막 처리 번호로 되감기·재적용) → ⑤ 발사·히트(지연 보상 없이) → ⑥ 지연 보상 링버퍼 → ⑦ 점수·리스폰·봇.
- **완료 조건**: RTT 100ms·손실 5% 시뮬레이션 환경에서 조작감이 로컬과 구분 어려움(예측 토글로 비교), 히트 판정이 사수 화면 기준으로 일치, 봇 7 + 사람 1 데스매치 완주, 예측·보정·물리 단위 테스트 통과.

### P7-6. 도구: LoadTest `필수` — UDP 27010, 규모 M

- `LoadTest server --port 27010` / `LoadTest client --sessions 1000 --rate 20 --size 256 --duration 60 [--unreliable] [--preset fast2]`
- 종료 리포트: 처리량(Mbps), 메시지/초, 재전송률, RTT p50/p95/p99, GC 수집 횟수(Gen0/1/2), 프로세스 메모리. CSV 출력 옵션(`--csv result.csv`).
- **역할**: Phase 3·4 완료 조건의 검증 도구이자 책 8·17장의 실습 도구. TankBattle 봇 부하와는 별개의 순수 전송 부하 도구.

---

## 11. Phase 8 — 배포와 운영 (권장)

### P8-1. NuGet 패키징 `권장`

- 패키지 2개: `KcpCSharpR1`(코어), `KcpCSharpR1.Transport`. (ID 선점 시 대안: `Jacking75.KcpCSharpR1`.)

```xml
<PropertyGroup>
  <PackageId>KcpCSharpR1</PackageId>
  <Version>1.0.0</Version>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageProjectUrl>https://github.com/jacking75/kcp-csharp</PackageProjectUrl>
  <RepositoryUrl>https://github.com/jacking75/kcp-csharp</RepositoryUrl>
  <PackageTags>kcp;udp;networking;game;arq;reliable-udp;monogame</PackageTags>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
</PropertyGroup>
```

- 버전 정책: SemVer. 애초 계획은 M3/M4 에서 프리뷰 태그(`1.0.0-preview.1`, `preview.2`)를 순차로 내는
  것이었으나 실제로 배포한 적은 없어(M3·M4 완료 시점에 태그 미부여), P8-1 에서 처음 패키징하며 바로
  `1.0.0` 으로 시작했다(구현: 2026-08-25). 이 시점에 M6(P5 보안/FEC, 책 15~17장) 범위도 이미 코드에
  포함돼 있다.
- 릴리스 자동화: 태그 `v*` 푸시 → GitHub Actions가 pack + push(`NUGET_API_KEY` 시크릿) + GitHub Release
  초안 생성. **보류** — 이 저장소를 다루는 푸시 토큰에 `workflow` 스코프가 없어 `.github/workflows/*.yml`
  을 추가하는 푸시 자체가 거부된다(P1-8 CI 제거 때와 같은 제약, 2026-08-25). 스코프가 부여되면 P1-8
  절의 `ci.yml` 을 뼈대 삼아 `on: push: { tags: ['v*'] }` 잡을 추가하면 된다.

**로컬 검증**(실제로 수행, 2026-08-25):

```
dotnet pack -c Release -o artifacts/nupkg src/KcpCSharpR1/KcpCSharpR1.csproj
dotnet pack -c Release -o artifacts/nupkg src/KcpCSharpR1.Transport/KcpCSharpR1.Transport.csproj
```

nuspec 확인 결과 두 타깃(`net8.0`, `netstandard2.1`) 모두 정상 포함, Transport → 코어
`KcpCSharpR1 1.0.0` 의존성 자동 생성, README.md·LICENSE·XML 문서 동봉 확인.
nuget.org 에 `KcpCSharpR1`/`KcpCSharpR1.Transport` 두 ID 모두 미등록 확인(대안 ID 불필요).

### P8-2. 품질 게이트 유지 `권장`

- PR 필수 체크: 빌드, 테스트, 커버리지 85%, 벤치 회귀 비교(PR 본문 표). **CI 미가동 상태라 자동 강제는
  없음** — `CONTRIBUTING.md` 의 빌드·테스트·커버리지·벤치 명령을 PR 올리기 전에 로컬로 직접 돌리는
  것으로 대신한다(2026-08-25 기준 실측: 경고 0, 테스트 207개, 커버리지 93.4%).
- `master` 브랜치 보호(직접 푸시 금지, CI 통과 필수). **보류** — CI 가 없는 상태에서 "CI 통과 필수"
  조건을 걸 수 없고, 이 저장소는 현재 리뷰어 없이 소유자가 직접 `master` 에 푸시하는 방식으로
  운영되고 있어 브랜치 보호를 걸면 그 흐름 자체가 막힌다. 사용자 결정으로 보류(2026-08-25).
  CI 워크플로가 살아나면(P8-1 참고) 함께 재검토한다.

---

## 12. 게임 장르별 권장 설정 (책 8장에 수록할 표)

### 12.1 NoDelay 프리셋

| 프리셋 | 호출 | 용도 |
|---|---|---|
| normal | `NoDelay(0, 40, 2, 1)` | 일반 |
| fast | `NoDelay(0, 30, 2, 1)` | 절충 |
| fast2 | `NoDelay(1, 20, 2, 1)` | 실시간성 우선 |
| fast3 | `NoDelay(1, 10, 2, 1)` | 최저 지연 (CPU·대역폭 비용 증가) |

### 12.2 장르별 권장

| 장르 | 프리셋 | WndSize(snd,rcv) | MTU | 채널 전략 |
|---|---|---|---|---|
| 턴제·카드·퍼즐 | normal | 32, 32 | 1400 | 전부 신뢰 채널 |
| MMORPG 필드 | fast | 128, 128 | 1400(모바일 1200) | 전투 판정=신뢰, 주변 이동=비신뢰 |
| 실시간 액션·대전 | fast2 | 256, 256 | 1200 | 입력/판정=신뢰, 스냅숏=비신뢰 |
| FPS·레이싱 | fast3 | 256, 256 | 1200 | 스냅숏=비신뢰(+FEC 검토), 이벤트=신뢰 |
| 모바일 공통 | interval ≤ 30 | — | 1200 | PingInterval 2s, SessionTimeout 10s, 세션 마이그레이션 on |

### 12.3 파라미터 주의사항

- `interval` 축소 = 지연↓, CPU·ACK 트래픽·배터리 소모↑.
- `nc=1`(혼잡 제어 off)은 사설망·소규모에서만 — 대규모에서 손실 시 자기 증폭.
- 메시지 모드 최대 크기 = `(rcv_wnd − 1) × mss`. 초과분은 앱 분할 또는 스트림 모드.
- `WndSize` 산정 = 대역폭 × RTT ÷ MTU. 예: 10Mbps·RTT 100ms → 약 89패킷 → 128.

### 12.4 포트 배정 (개발용 UDP 27001~27199 대역 준수)

| 용도 | 포트 |
|---|---|
| 샘플 ① Echo | 27001 |
| 샘플 ② Chat | 27002 |
| 샘플 ③ Gomoku | 27003 |
| 샘플 ④ MoveSync | 27004 |
| 샘플 ⑤ TankBattle | 27005 |
| LoadTest | 27010 |
| 테스트 코드의 실소켓 | 27100~27199 |

---

## 13. 마일스톤 요약과 의존 관계

```
Phase 1 (기반·버그) ─▶ Phase 2 (테스트) ─▶ Phase 3 (성능) ─▶ Phase 4 (전송 계층)
                                                                │
                                      ┌─────────────────────────┼──────────────────┐
                                      ▼                         ▼                  ▼
                               Phase 5 (보안/FEC)      Phase 6 (온라인 책)   Phase 7 (샘플 5종)
                                                                └────────┬─────────┘
                                                                         ▼
                                                                  Phase 8 (배포 1.0.0)
```

| 마일스톤 | 포함 작업 | 규모 | 릴리스 |
|---|---|---|---|
| M1 안전한 코어 | P1-1 ~ P1-8 | M | — |
| M2 증명된 코어 | P2-1 ~ P2-7 | L | — |
| M3 빠른 코어 | P3-1 ~ P3-7 | L | `1.0.0-preview.1` |
| M4 게임에 쓸 수 있는 라이브러리 | P4-1 ~ P4-5 | XL | `1.0.0-preview.2` |
| M5 배우기 쉬운 라이브러리 | P6-1~5(책), P7-1~6(샘플·도구), P8-1 | XL | `1.0.0` |
| M6 차별화 | P5-1 ~ P5-3 (+책 15~17장) | L | `1.1.0` |

(규모: S < 1일, M = 며칠, L = 1~2주, XL = 수 주 — 1인 기준 감각치. 주차 단위 상세는 3.4절)

---

## 14. 최종 완료 체크리스트

**안정성**
- [x] 코어에 `DateTime` 없음, 49.7일 랩어라운드 테스트 통과 (B-01, B-02) — P1-3, 시나리오 #11
- [x] 데드링크·타임아웃·FIN으로 연결 종료가 반드시 감지됨 (B-03, S-02) — 시나리오 #9, 세션 상태 전이 테스트
- [x] 조각 수 한도 위반이 교착 대신 오류 반환 (B-04) — 시나리오 #7
- [x] 손실 40% 링크에서 데이터 무결성 100% (P2-4 #2)
- [x] 변조·거짓 length·conv 불일치 패킷에 크래시 없음 (P2-4 #12, #13)

**성능**
- [x] 정상 상태 송수신 경로 관리 힙 할당 0 B/op (P3-1 목표) — docs/benchmarks.md
- [~] 1,000 세션 LoadTest 60초에서 Gen2 GC 0회, 메모리 평탄 — 300세션 Gen2 0회 달성, 1,000세션은 Gen2 1회(부하 생성기 측 할당으로 판단). 라이브러리 정상 상태는 0 B/op
- [x] 벤치마크 수치가 `docs/benchmarks.md`에 기록·추적

**라이브러리 완성도**
- [~] NuGet 설치만으로 서버+클라 작성 가능 (외부 의존성 0) — 코드·패키징 모두 완료(P8-1, `dotnet pack`
  으로 로컬 검증), nuget.org 실제 게시는 API 키가 필요한 별도 행위라 보류
- [x] MonoGame(DesktopGL, net8.0) 프로젝트에서 참조·동작 확인 (샘플 3~5가 증명) — P7-3~P7-5
- [x] netstandard2.1 타깃 빌드 유지 (Godot C# 등 호환)
- [x] 공개 API 전체 XML 문서 주석, 코어 커버리지 85%+ — 실측 93.4%

**학습 자료**
- [~] 온라인 책 전 17장 + 부록 5종이 P6-2 DoD 충족 — GitHub Pages 실제 공개(저장소 설정)는 관리자 작업으로 남음
- [x] 책 3부의 5개 장이 샘플 5종과 1:1 대응하며 모든 코드가 실행 가능 — 9~13장 ↔ 샘플 ①~⑤
- [x] 샘플 5종(★1~★5) + LoadTest, 각각 자체 README 보유
- [x] README 30초 시작 코드가 복붙으로 실행됨 — 샘플 ① 의 실제 코드에서 발췌

---

## 부록 A. 이번 리뷰에서 "고치지 않기로" 한 것들 (근거 포함)

| 항목 | 결정 | 근거 |
|---|---|---|
| KCP 알고리즘 자체(혼잡 제어, RTO 계산식) 변경 | 안 함 | 원본 kcp/kcp-go와의 동작 호환·검증 가능성이 학습 자료로서의 가치. 개선 아이디어(BBR류)는 1.x 이후 실험 브랜치로 |
| `Input`의 부분 파싱 후 오류 중단 동작 | 유지+문서화 | 원본과 동일 동작. 바꾸면 상호운용 검증이 어려워짐 (S-06) |
| 코어의 `byte[]` 오버로드 제거 | 안 함 | 기존 사용자·netstandard2.1 소비자 호환. Span 오버로드를 추가만 한다 |
| 자체 직렬화 프레임워크 추가 | 안 함 | 범위 밖. 샘플에서 수동 인코딩을 가르치는 것이 학습 목적에 부합 |
| Unity 지원 작업 | 보류 | 연동 대상을 MonoGame으로 결정(v2). netstandard2.1 타깃은 유지하므로 Unity 사용자는 DLL 참조로 자력 사용 가능(FAQ에 안내만) |
