# 성능 개선 제안서 — GC·복사·멀티스레드·비동기

> 작성일: 2026-08-25. 대상 코드: Phase 8 완료 시점(0.10.0)의 `src/KcpCSharpR1`, `src/KcpCSharpR1.Transport`.
>
> 이 문서는 **현재 코드베이스를 전수 분석해 찾아낸 추가 성능 개선 후보**를 정리한 제안서다.
> 로드맵의 진단 ID(PF-01~08)와 구분하기 위해 새 접두사를 쓴다:
> **G**(GC/할당), **C**(복사 회피), **M**(멀티스레드), **A**(비동기 API).

## 구현 현황 (2026-08-25 반영 완료)

| ID | 상태 | 결과 |
|---|---|---|
| G-1 | ✅ 완료 | `UdpSocketTransport` 에 `ConditionalWeakTable` 주소 캐시 + `SendTo(ReadOnlySpan, AddressKey)` 무할당 오버로드 |
| G-2 | ✅ 완료 | `Kcp.RecvInPlace` + `ReliableReceivedSpan` / `UnreliableReceivedSpan`(세션·서버·클라이언트), 조립 버퍼 상주화 |
| G-3 | ✅ 완료 | 세션 상주 `decryptBuffer` |
| G-4 | ✅ 완료 | FEC 그룹 슬랩 선할당 + `Present` 배열, `ReedSolomon.Reconstruct` 스크래치 재사용(복원 0 B/op 테스트) |
| G-5 | ✅ 완료 | 서버·클라이언트 구독 델리게이트 캐시, 메트릭 스냅숏 리스트 재사용, `KcpClient.Pump` 시계 1회 읽기, FEC 콜백 클로저 제거 |
| C-1 | ✅ 완료 | `IUdpTransport.TryReceive(byte[], ...)` 기본 구현 + netstandard2.1 직접 수신 |
| A-1 | ✅ 완료 | `ConnectAsync` 의 1ms `Task.Delay` 폴링 제거(TCS + 소켓 대기) |
| A-3 | ✅ 완료 | `RunAsync` 의 고정 1ms 대기를 다음 갱신 시각 기반(상한 5ms)으로 변경 |
| M-1 | ✅ 완료 | `KcpSessionOptions.SendOutboxCapacity` + `KcpSession.TrySendFromAnyThread` |
| M-2 | ✅ 완료 | `KcpEventQueue` / `KcpEvent` + `KcpServer.EnableEventQueue` · `KcpClient.EnableEventQueue` |
| M-3 (a) | ✅ 완료 | N포트 × N서버 운영 패턴을 책 17장에 문서화(라이브러리 변경 0) |
| M-3 (b) | ⏸ 보류 | 단일 포트 샤딩. 이 문서 3절의 착수 조건(먼저 (a)로 측정 → 결정적 동시성 테스트 선행)이 아직 충족되지 않았다 |
| M-4 | ✅ 완료 | 호스트 GC/스레드 권장 설정을 README + 책 17장에 문서화 |
| A-2 | ⏸ 보류 | 서버 수신 루프의 비동기 전환. 4절이 스스로 밝힌 대로 "수신 완료·타이머 만료를 하나의 실행 큐로 직렬화하는 설계"가 선행되어야 한다 |

**보류 2건에 대해** — 둘 다 이 문서가 착수 조건을 명시한 항목이고, 그 조건(부하 측정으로 병목 확인,
결정적 동시성 테스트 확보)이 아직 갖춰지지 않았다. 조건 없이 손대면 규약이 단순하다는 현 구조의
최대 강점을 신규 동시성 코드로 맞바꾸는 셈이 된다. 두 항목은 로드맵에 `M-3b` / `A-2` 로 등재해 두었다.

**부수 수정** — 구현 중 발견해 함께 고친 것:
- `Kcp.Recv` 의 rcv_buf → rcv_queue 이동이 큐 길이를 이중으로 세어 실효 수신 윈도가 절반이었다.
  `parse_data` 와 같은 kcp-go 방식으로 통일(`MoveRcvBufToQueue`).
- `KcpSession.DrainReliable` 이 콜백 안에서 세션이 끊겨 `kcp` 가 null 이 되면 터질 수 있었다.
- FEC 송수신 경로의 람다 2개가 **패킷마다** 클로저를 만들고 있었다.

---

## 전제 — 어떤 개선도 아래를 깨지 않는다

1. 코어 KCP 24바이트 헤더의 **와이어 호환**(원본 kcp/kcp-go)은 그대로다. 아래 제안 중 와이어 포맷을 건드리는 것은 하나도 없다.
2. **소유 스레드 규약**(인스턴스 1개 = 스레드 1개, 코어 내부 lock 금지)은 그대로다. 멀티스레드 제안(M-1~M-3)은 전부 이 규약을 *우회*가 아니라 *보존*하는 설계다.
3. 공개 API 는 **추가만 하고 깨지 않는다**(기존 이벤트·메서드 시그니처 유지).
4. Phase 3 원칙대로 **변경 전후를 같은 벤치마크로 측정**하고 회귀가 없을 때만 반영한다(`docs/benchmarks.md` 절차).

## 요약 — 우선순위표

| ID | 제안 | 분류 | 기대 효과 | 위험 | 난이도 |
|---|---|---|---|---|---|
| G-1 | 송신 경로 `SocketAddress` 할당 제거 | GC | 서버 **송신 패킷당 힙 할당 1개 제거** | 낮음 | 하 |
| G-2 | 수신 이벤트의 rent+복사 제거(Span 이벤트) | GC·복사 | 수신 **메시지당 Rent/Return+복사 제거** | 낮음 | 중 |
| G-3 | 세션 상주 복호화 버퍼 | GC | 암호화 세션의 패킷당 Rent/Return 제거 | 낮음 | 하 |
| G-4 | FEC 디코더·ReedSolomon 무할당화 | GC | FEC 켠 세션의 **패킷당 ~1.4KB 할당 제거** | 중 | 중 |
| A-1 | `ConnectAsync` 의 1ms 폴링 제거(TCS) | 비동기 | 연결 지연·불필요한 wakeup 제거 | 낮음 | 하 |
| G-5 | 소소한 정리(브리지 클로저·스냅숏 리스트 등) | GC | 세션 생성·메트릭 주기 할당 소거 | 없음 | 하 |
| C-1 | netstandard2.1 수신 폴백 이중 복사 제거 | 복사 | 구형 타깃 수신 경로 memcpy 1회 제거 | 낮음 | 하 |
| M-1 | 게임 스레드용 송신 아웃박스(`Channel`) | 멀티스레드 | 스레드 안전 Send API 제공 | 중 | 중 |
| M-2 | 수신 이벤트 → 게임 스레드 채널 어댑터 | 멀티스레드 | 규약 위반 사고 예방 + 정석 패턴 API 화 | 낮음 | 중 |
| M-3 | 세션 샤딩(수신 1 + 워커 N) | 멀티스레드 | 암호화·FEC 부하의 **코어 수 확장** | 높음 | 상 |
| A-2 | 서버 수신 루프의 비동기 수신 전환(선택) | 비동기 | 유휴 CPU·wakeup 지연 절감 | 중 | 상 |
| M-4 | 호스트 GC/스레드풀 권장 설정 문서화 | 문서 | 배포처 GC 일시정지 단축 | 없음 | 하 |

권장 착수 순서: **G-1 → G-2 → G-3 → A-1 → G-5·C-1 → G-4 → M-1·M-2 → (필요 시) M-3 → (선택) A-2**.

---

## 1. GC 효율화 — 할당 제거

### G-1. net8.0 송신 경로의 `SocketAddress` 할당 제거 (우선순위 1)

**현황** — `UdpSocketTransport.SendTo` (`src/KcpCSharpR1.Transport/UdpSocketTransport.cs:97`):

```csharp
return socket.SendTo(data, SocketFlags.None, remote);   // remote: IPEndPoint
```

`Socket.SendTo(ReadOnlySpan<byte>, SocketFlags, EndPoint)` 는 내부에서 `remote.Serialize()` 를 호출해
**호출마다 `SocketAddress` 를 새로 만든다**. 수신 경로는 P3-7 에서 `SocketAddress` 재사용으로 무할당화했지만,
송신 경로는 그대로 남아 있다. 서버가 초당 수만 패킷을 내보내면 그만큼의 Gen0 할당이 생긴다.

**개선** — .NET 8 에는 무할당용 오버로드 `SendTo(ReadOnlySpan<byte>, SocketFlags, SocketAddress)` 가 있다.
세션의 상대 주소는 불변이므로 `IPEndPoint → SocketAddress` 직렬화를 1회만 하면 된다.

인터페이스를 깨지 않는 가장 작은 구현은 `ConditionalWeakTable` 캐시다. 세션이 사라지면 `IPEndPoint` 와 함께
캐시 항목도 GC 로 자연 소멸하므로 수명 관리 코드가 필요 없다:

```csharp
// UdpSocketTransport 내부 (net8.0 전용)
private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPEndPoint, SocketAddress>
    sendAddressCache = new();

public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
{
    // 세션마다 IPEndPoint 인스턴스가 고정이므로 참조 키 캐시가 정확히 1회만 직렬화한다.
    var address = sendAddressCache.GetValue(remote, static ep => ep.Serialize());
    return socket.SendTo(data, SocketFlags.None, address);
}
```

더 명시적으로 가려면 `IUdpTransport` 에 `SendTo(ReadOnlySpan<byte>, SocketAddress)` 오버로드를 **추가**하고
`KcpSession` 이 생성 시 `RemoteEndPoint.Serialize()` 결과를 들고 다니게 한다(기존 오버로드는 유지).

**검증** — LoadTest(`samples/tools/LoadTest`)를 돌리며 `dotnet-counters` 의 Gen0 할당률 전후 비교.
벤치마크에는 소켓이 없으므로 `SendTo` 를 직접 재는 마이크로벤치를 추가해도 된다.

**위험** — 낮음. `ConditionalWeakTable` 은 스레드 안전하고, 잘못돼도 기능 변화가 없다(성능만 되돌아감).

### G-2. 수신 이벤트 경로의 Rent+복사 제거 — Span 기반 이벤트 (우선순위 2)

**현황** — 수신 메시지를 이벤트로 전달할 때마다 ArrayPool 대여 + 복사가 일어난다:

- `KcpSession.DrainReliable` (`KcpSession.cs:535`): `PeekSize` → `Rent` → `kcp.Recv`(세그먼트→대여 버퍼 복사) → 핸들러 → `Return`.
- `KcpSession.DeliverUnreliable` (`KcpSession.cs:499`): payload 는 이미 서버의 `receiveBuffer` 안에 살아 있는데,
  `ReadOnlyMemory<byte>` 계약을 맞추기 위해 **한 번 더 대여 버퍼로 복사**해서 넘긴다.

이벤트 타입이 `Action<KcpSession, ReadOnlyMemory<byte>>` 라서 생기는 구조적 비용이다.
`ReadOnlySpan<byte>` 는 제네릭 `Action<>` 에 못 들어가지만, **커스텀 델리게이트로는 쓸 수 있다.**

**개선** — Span 기반 이벤트를 *추가*하고(기존 이벤트는 하위 호환으로 유지), 구독자가 있으면 그 경로를 우선한다:

```csharp
/// <summary>수신 메시지 핸들러(무복사). data 는 콜백 동안만 유효하다.</summary>
public delegate void KcpMessageHandler(KcpSession session, ReadOnlySpan<byte> data);

public event KcpMessageHandler? ReliableReceivedSpan;
public event KcpMessageHandler? UnreliableReceivedSpan;

private void DeliverUnreliable(ReadOnlySpan<byte> payload)
{
    var spanHandler = UnreliableReceivedSpan;
    if (spanHandler != null)
    {
        spanHandler(this, payload);   // 복사 0, 할당 0
        return;
    }
    // ...기존 ReadOnlyMemory / 폴링 큐 경로 유지...
}
```

신뢰 채널 쪽은 한 단계 더 갈 수 있다. 게임 메시지 대부분은 MTU 한 장에 들어가는 단일 조각(frg == 0)이므로,
코어에 **단일 조각 무복사 수신** 을 추가하면 `Recv` 의 세그먼트→버퍼 복사 자체가 사라진다:

```csharp
// Kcp 에 추가 (코어는 여전히 소켓·스레드를 모른다)
/// <summary>다음 메시지가 단일 조각이면 내부 세그먼트의 페이로드를 직접 넘긴다(무복사).</summary>
/// <remarks>handler 가 반환하면 세그먼트는 풀로 돌아간다. Span 을 보관하면 안 된다.</remarks>
public int RecvInPlace(MessageHandler handler)   // delegate void MessageHandler(ReadOnlySpan<byte> msg)
{
    if (rcv_queue.Count == 0) return -1;
    ref var front = ref rcv_queue.PeekFront();
    if (front.frg != 0)
        return -3;   // 다조각: 호출자는 기존 Recv(Span) 조립 경로로 폴백

    var seg = front;
    rcv_queue.RemoveFront(1);
    handler(seg.Payload);
    ReturnSegment(seg);
    MoveRcvBufToQueue();          // 기존 rcv_buf → rcv_queue 이동 + fast recover 로직 재사용
    return seg.Len;
}
```

`DrainReliable` 은 `RecvInPlace` 를 먼저 시도하고, 다조각(-3)일 때만 기존 대여+조립 경로를 탄다.
다조각 조립 버퍼도 매번 Rent/Return 하지 말고 **세션 상주 버퍼**(필요 시 성장) 하나를 재사용하면
정상 상태에서 수신 경로 할당이 0 이 된다.

**검증** — `SendRecvRoundtrip` 벤치마크에 이벤트 드레인 변형을 추가해 B/op 0 확인.
기존 `ReadOnlyMemory` 이벤트와 폴링 API 의 동작 불변은 기존 테스트(207개)가 보증한다.

**위험** — 낮음. 새 API 추가이며 기존 경로는 그대로다. 문서에 "Span 은 콜백 밖 보관 금지"를 명시해야 한다
(기존 `ReadOnlyMemory` 이벤트도 이미 같은 규약이다).

### G-3. 암호화 세션의 상주 복호화 버퍼 (우선순위 3)

**현황** — `KcpSession.OnKcpFrame`/`OnRawFrame` (`KcpSession.cs:417`, `KcpSession.cs:447`)이
**암호화된 패킷 1개마다** `ArrayPool.Rent` → 복호화 → `Return` 을 반복한다.

**개선** — 복호화 대상 크기는 MTU 로 상한이 정해져 있다. 세션 생성 시(암호화가 켜진 경우에만)
`decryptBuffer = new byte[options.Mtu]` 를 한 번 만들어 재사용한다. 세션은 소유 스레드 1개에서만 돌므로
재진입 걱정이 없고, `frameBuffer` 와 같은 수명 관리 방식이라 코드도 단순해진다.

```csharp
private byte[]? decryptBuffer;   // CreateKcp 에서 cipher != null 일 때만 할당

private void OnKcpFrame(ReadOnlySpan<byte> frame, uint now)
{
    // ...
    if (!cipher.TryDecrypt(payload, decryptBuffer, out var plainLength))
        return;
    kcp.Input(decryptBuffer.AsSpan(0, plainLength), true, options.AckNoDelay);
    // Rent/Return, try/finally 모두 삭제
}
```

`ArrayPool.Rent/Return` 은 싸지만 공짜는 아니다(버킷 인덱스 계산 + 스레드별 캐시/전역 풀 접근 2회/패킷).
상주 버퍼는 그 비용과 실수 여지(반환 누락·이중 반환)를 함께 없앤다.

**위험** — 낮음. 세션당 MTU 크기(기본 1.4KB) 메모리가 늘어나는 것뿐이며, 암호화 세션에만 적용된다.

### G-4. FEC 디코더·ReedSolomon 무할당화 (FEC 사용 시 우선순위 상승)

**현황** — FEC 를 켜면 수신 경로에 패킷당 할당이 생긴다. `src/KcpCSharpR1.Transport/Fec/FecCodec.cs`:

- `Decode` (`FecCodec.cs:220`): 샤드가 도착할 때마다 `new byte[maxShardSize]` — **패킷당 ~1.4KB**.
- `TryRecover` (`FecCodec.cs:249`): 복원 시도마다 `new byte[]?[TotalShards]` 스냅숏 배열.
- `ReedSolomon.Reconstruct` (`ReedSolomon.cs:103-122`): 복원마다 `subMatrix`/`subShards`/역행렬/`recovered` 할당.

인코더는 생성 시 슬랩을 만들어 재사용하는데(잘 되어 있음) 디코더만 매번 새로 만든다.

**개선** — 디코더도 인코더처럼 **그룹 재활용 시 버퍼까지 재사용**한다:

1. `Group` 생성 시 `Shards[i] = new byte[maxShardSize]` 를 **미리 전부** 만들어 두고,
   도착 여부는 별도 `bool[] Present` 로 추적한다(현재의 null 체크 대체). 그룹 재활용 시 `Present` 만 clear.
   → `Decode` 의 패킷당 할당이 0 이 된다. 고정 메모리는 세션당 `MaxGroups(16) × TotalShards(13) × 1.4KB ≈ 291KB`.
   부담스러우면 `MaxGroups` 를 옵션으로 내리거나, 그룹 슬랩을 `ArrayPool` 대여로 바꾼다.
2. `TryRecover` 의 스냅숏 배열은 디코더 필드로 승격해 재사용한다(`Array.Clear` 후 채움).
3. `ReedSolomon` 에 복원용 스크래치(`subMatrix`, `subShards`, 역행렬 작업 버퍼, `recovered`)를
   생성 시 1회 할당해 두고 `Reconstruct` 가 재사용한다. `ReedSolomon` 인스턴스는 세션당 1개, 소유 스레드
   전용이므로 lock 없이 안전하다. `Encode` 쪽 반데르몬드 행렬은 생성 시 1회라 손댈 필요 없다.

**검증** — `ReedSolomonTests`(158줄) + `Phase5IntegrationTests` 가 정확성을 보증한다.
`CryptoBenchmarks` 옆에 FEC 디코드 벤치마크를 추가해 B/op 전후 비교.

**위험** — 중간. 복원 로직의 버퍼 소유권이 바뀌므로(복원 결과를 그룹 슬랩에 쓰면 콜백에 넘긴 Span 의
수명 규약을 지켜야 한다) 테스트 선행이 필수다. 정확성 테스트가 이미 촘촘해서 안전망은 있다.

### G-5. 소소한 정리 (전부 난이도 하, 위험 없음)

| 위치 | 현황 | 개선 |
|---|---|---|
| `KcpServer.HandleSyn` (`KcpServer.cs:302`) | 세션마다 `(s, data) => ReliableReceived?.Invoke(...)` 클로저 2개 할당 | 인스턴스 메서드로 바꿔 메서드 그룹 구독(클로저 클래스 제거) |
| `KcpServer.SampleMetrics`·`Sessions` | 주기마다 `new List<KcpSession>(...)` | 스크래치 리스트 재사용(`rttScratch` 와 같은 방식). 외부 노출용 `Sessions` 는 복사 유지 |
| `KcpClient.Pump` | 수신 패킷마다 `clock()` 호출 | 루프 진입 시 1회 읽고 재사용(코어 PF-01 과 같은 원칙) |
| `KcpSession.DeliverUnreliable` 폴링 큐 | 메시지당 Rent(복사) — 정책상 유지 | G-2 의 Span 이벤트 사용을 권장 경로로 문서화 |

참고로 `clock = currentTimeMs ?? (() => KcpClock.NowMs)` 의 람다는 캡처가 없어 컴파일러가 정적 캐시하므로
인스턴스마다 할당되지 않는다. 손댈 필요 없다.

---

## 2. 불필요한 복사 제거

수신 이벤트 경로의 복사(가장 큰 건)는 **G-2 에서 함께 해결**된다. 그 외:

### C-1. netstandard2.1 수신 폴백의 이중 복사 제거

**현황** — `UdpSocketTransport.TryReceive` 폴백 경로가 `receiveScratch` 로 받은 뒤 호출자 버퍼로
한 번 더 복사한다(Span 오버로드가 없어서). 그런데 실제 호출자(`KcpServer`, `KcpClient`)의 버퍼는
전부 `byte[]` 다.

**개선** — `IUdpTransport` 에 `bool TryReceive(byte[] buffer, ...)` 오버로드를 추가하고 배열이면
`ReceiveFrom(buffer, ...)` 로 직접 받는다. net8.0 경로는 기존 그대로 두면 된다.

**위험** — 낮음. netstandard2.1(MonoGame 콘솔 타깃 등) 사용자만 이득을 본다.

### C-2. 조사 결과, 손대지 않는 것이 맞는 곳

- **암호화 송신 경로** (`KcpSession.OnKcpOutput`): KCP 스테이징 버퍼 → `frameBuffer` 로의 이동은
  memcpy 가 아니라 AES-GCM **변환 출력**이라 추가 복사가 아니다. "in-place 암호화"로 한 단계 줄이는 것은
  `AesGcm` 의 입출력 겹침 허용 여부가 문서상 보장되지 않아 **권장하지 않는다**(안정성 우선 원칙).
- **`Kcp.Send` 의 사용자 데이터 복사**: 재전송을 위해 라이브러리가 데이터를 소유해야 하므로 필수 복사다.
- **FEC 인코더의 그룹 캐시 복사**: 패리티 계산을 위한 필수 복사다.
- **`Flush` 의 세그먼트 → MTU 스테이징 버퍼 복사**: 여러 세그먼트를 한 UDP 데이터그램으로 묶는(batching)
  핵심 메커니즘이라 복사가 곧 이득이다.

---

## 3. 멀티스레드 고성능화

현재 구조(수신 루프 1개가 소켓·세션·암호화·FEC 전부 담당)는 규약이 단순하고 데이터 레이스가 없다는 것이
최대 강점이다. 이를 유지하면서 확장하는 순서는 다음과 같다.

### M-1. 게임 스레드용 송신 아웃박스 (`Channel<T>`)

**현황** — `KcpSession.Send` 는 소유(수신 루프) 스레드 전용이다. 게임 로직 스레드에서 바로 부르면
코어 상태가 깨진다. 지금은 사용자가 직접 큐를 만들어야 한다.

**개선** — 서버/클라이언트에 옵션으로 **bounded MPSC 아웃박스**를 제공한다:

```csharp
// KcpSession 에 추가
private Channel<PooledMessage>? outbox;   // 옵션 켤 때만 생성. Bounded(MaxSendBacklog)

/// <summary>어느 스레드에서든 호출 가능한 송신. 큐가 차면 false(백프레셔).</summary>
public bool TrySendFromAnyThread(ReadOnlySpan<byte> data)
{
    var buf = ArrayPool<byte>.Shared.Rent(data.Length);
    data.CopyTo(buf);
    if (outbox!.Writer.TryWrite(new PooledMessage { Buffer = buf, Length = data.Length }))
        return true;
    ArrayPool<byte>.Shared.Return(buf);
    return false;
}

// 소유 스레드의 Update 첫머리에서 드레인
private void DrainOutbox()
{
    while (outbox!.Reader.TryRead(out var msg))
    {
        kcp?.Send(msg.Buffer.AsSpan(0, msg.Length));
        ArrayPool<byte>.Shared.Return(msg.Buffer);
    }
}
```

복사 1회가 추가되지만 이것은 스레드 경계를 넘기 위한 **필수 복사**다(호출자 버퍼 수명과 분리).
`Channel.CreateBounded` 는 lock-free fast path 라 게임 스레드를 거의 막지 않는다.
기존 `Send` 는 그대로 두어, 이벤트 콜백 안(소유 스레드)에서는 무복사 경로를 계속 쓸 수 있게 한다.

**위험** — 중간. 드레인 시점(Update 직전)과 백프레셔 의미(`MaxSendBacklog` 와 이중 상한이 되지 않게
아웃박스 용량을 별도 옵션으로)를 명확히 설계해야 한다. 테스트 선행.

### M-2. 수신 이벤트 → 게임 스레드 채널 어댑터

**현황** — 이벤트는 수신 루프 스레드에서 발화한다. "핸들러에서는 큐에 넣기만 하라"가 정석인데
그 큐를 매번 사용자가 짜야 하고, 잘못 짜면(핸들러에서 게임 상태 직접 접근) 레이스가 난다.

**개선** — 정석 패턴을 라이브러리 API 로 굳힌다. SPSC 채널 + ArrayPool 페이로드 규약:

```csharp
public readonly struct KcpEvent
{
    public KcpEventType Type { get; init; }        // Connected / Disconnected / Reliable / Unreliable
    public KcpSession Session { get; init; }
    public byte[]? Payload { get; init; }          // ArrayPool 대여. 소비 후 Return 필수
    public int PayloadLength { get; init; }
    public KcpDisconnectReason Reason { get; init; }
}

// 사용: 게임 루프에서
while (server.Events.TryRead(out var ev)) { Handle(ev); ev.ReturnPayload(); }
```

`SingleWriter = true, SingleReader = true` 채널이면 오버헤드가 거의 없다. 페이로드 대여/반환을
`KcpEvent.ReturnPayload()` 헬퍼로 감싸 이중 반환을 막는다. 게임 6장(책)의 패턴을 코드로 제공하는 것.

**위험** — 낮음. 순수 추가 API 다.

### M-3. 세션 샤딩 — 수신 1 + 워커 N (대규모 서버용, 필요해질 때)

**현황** — 세션 수천 개 + AES-GCM + FEC 면 단일 스레드의 암호화 처리량이 먼저 한계에 닿는다.
(암호화 없는 순수 KCP 는 단일 스레드로도 매우 멀리 간다 — 먼저 LoadTest 로 병목을 확인하고 착수할 것.)

**개선안 두 가지** (둘 다 소유 스레드 규약을 보존):

**(a) N 포트 × N 서버 — 코드 변경 0.** `KcpServer` 를 포트 27001~2700N 에 N 개 띄우고 로그인/매치메이킹
서버가 클라이언트에 포트를 배정한다. 세션은 자기 서버(스레드)에만 속하므로 규약이 자동으로 지켜진다.
운영으로 푸는 방법이라 라이브러리는 그대로다. **가장 먼저 검토할 것.**

**(b) 단일 포트 샤딩 — 수신 스레드 1 + 워커 샤드 N.**

```
[recv 스레드]  socket.ReceiveFrom → AddressKey 해시 % N → 샤드 n 의 SPSC 링에 패킷 복사
[샤드 n 스레드] 링 드레인 → 자기 세션들의 OnFrame/Update → socket.SendTo (SendTo 는 스레드 안전)
```

- 세션은 **생성 시 샤드에 고정**되고 이후 그 샤드 스레드만 만진다 → 세션 관점에서는 지금과 동일한
  단일 소유 스레드. 코어·세션 코드는 한 줄도 안 바뀐다.
- 스레드 경계를 넘는 패킷 1회 복사(MTU 이하)는 필수 비용. 샤드별 수신 링은 고정 슬랩(예: 4MB)으로
  만들어 무할당으로 돌린다.
- 핸드셰이크(SYN/쿠키)는 수신 스레드에서 무상태 처리 후, 세션 생성만 담당 샤드에 위임한다.
- `SessionScheduler` 는 샤드마다 1개씩 둔다.

**위험** — 높음(신규 동시성 코드). 반드시 (a)로 충분한지 먼저 측정하고, (b)는 SimTests 수준의
결정적 테스트(가상 링·가상 시계)를 갖춘 뒤 진행한다.

### M-4. 호스트 권장 설정 문서화

라이브러리 코드는 아니지만 효과가 크다. README 또는 책 배포 장에 추가:

```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>       <!-- 서버 앱 -->
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <TieredPgo>true</TieredPgo>                                   <!-- net8 기본이지만 명시 -->
</PropertyGroup>
```

- 서버: Server GC(+.NET 8 DATAS 는 메모리 상황 보고 선택). 워크스테이션 GC 대비 일시정지가 짧고 처리량이 높다.
- 클라이언트(MonoGame): 워크스테이션 GC 유지 + 이 라이브러리의 무할당 경로(G-2 적용 시) 사용을 안내.
- 라이브러리가 무할당이어도 **호스트 GC 설정이 나쁘면 스파이크는 남는다**는 점을 명시.

---

## 4. 비동기 API

### A-1. `KcpClient.ConnectAsync` 의 1ms 폴링 제거 (쉽고 확실)

**현황** — `KcpClient.cs:130`: 핸드셰이크 완료를 `await Task.Delay(1)` 루프로 폴링한다.
1ms 마다 타이머 wakeup + 상태 검사이며, 완료 감지도 최대 1ms 늦는다.

**개선** — 세션의 `Connected`/`Disconnected` 이벤트로 `TaskCompletionSource` 를 완성한다:

```csharp
var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
session.Connected += _ => tcs.TrySetResult(true);
session.Disconnected += (_, reason) => tcs.TrySetResult(false);

session.StartConnect(now);
// 핸드셰이크 재시도 타이머는 여전히 Pump 가 돌려야 하므로,
// "Pump 루프 + tcs 대기"를 결합한다: 완료되거나 타임아웃까지 Pump 를 짧게 반복하되
// Task.Delay(1) 대신 tcs.Task 와 Task.Delay(HandshakeRetryIntervalMs) 중 먼저 오는 쪽을 기다린다.
var done = await Task.WhenAny(tcs.Task, Task.Delay(retryInterval, ct)).ConfigureAwait(false);
```

핸드셰이크는 연결당 1회라 할당 몇 개는 문제가 아니다. 핵심은 **1ms 폴링 루프 제거**와 완료 즉시 반환이다.

**위험** — 낮음. `SessionStateMachineTests` 가 상태 전이를 이미 검증한다.

### A-2. 서버 수신 루프의 비동기 수신 전환 (선택 — 트레이드오프 명시)

**현황** — `ReceiveLoop` 는 `socket.Poll(≤5ms)` → 논블로킹 드레인 → 타이머 처리 구조다.
`KcpClient.RunAsync` 도 `WaitForData(1ms)` + `Task.Yield()` 로 사실상 1ms 폴링이다.

**평가를 먼저** — 10ms 간격으로 항상 틱이 도는 게임 서버에서는 **현행 Poll 구조가 합리적**이다.
지연 상한(5ms)이 예측 가능하고, 스레드 1개가 어차피 상주하며, 코드가 단순하다.
비동기 전환이 이득인 경우는 (1) 유휴 세션이 많아 빈 Poll syscall 이 아까울 때,
(2) 한 프로세스에 서버 인스턴스를 여러 개 띄워 전용 스레드 N 개가 부담일 때다.

**전환한다면** — .NET 8 의 `ReceiveFromAsync(Memory<byte>, SocketFlags, SocketAddress, CancellationToken)` 를 쓴다.
이 오버로드는 내부 상태 머신을 재사용해 **amortized 무할당**이고, `SocketAddress` 재사용으로 수신 주소 할당도 없다:

```csharp
// IUdpTransport 에 추가 (net8.0 전용 구현)
ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketAddress remote, CancellationToken ct);

private async Task ReceiveLoopAsync(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        var wait = scheduler.TimeToNext(clock(), options.PollMicroseconds / 1000);
        // 수신을 항상 걸어 두고, 타이머 기한이 먼저 오면 취소가 아니라 '타이머만 처리'한다.
        var recvTask = udp.ReceiveAsync(receiveBuffer, receiveAddress, token);
        if (recvTask.IsCompleted || wait <= 0)
        {
            // 완료됐으면 즉시 처리, 아니면 타이머 우선
        }
        // ... RunDueSessions(); SampleMetrics();
    }
}
```

주의점: 취소 가능한 `ReceiveFromAsync` 를 매 사이클 취소하면 취소 경로 비용이 커진다.
정석은 "수신은 계속 pending 으로 두고, 타이머는 별도 주기로 처리"하는 이중 구조인데, 그러면 세션 접근이
두 흐름에서 일어나 소유 스레드 규약이 흔들린다. **전환 시에는 수신 완료·타이머 만료를 하나의 실행 큐로
직렬화하는 설계가 선행되어야 한다.** 이 복잡성 때문에 우선순위를 마지막에 두었다.

**위험** — 중간~높음(동시성 설계 필요). 반드시 M-3 (b)와 같이 결정적 테스트를 갖추고 진행.

### A-3. 소소한 것 — `RunAsync` 의 Poll 시간 동적화

`KcpClient.RunAsync`(`KcpClient.cs:207`)의 `WaitForData(1000)`(1ms 고정)을 세션 `Update` 가 돌려준
다음 갱신 시각까지 남은 시간(상한 예: 5ms)으로 바꾸면 유휴 시 wakeup 횟수가 크게 준다. 두 줄 수정.

---

## 5. 이미 잘 되어 있는 것 (이번 분석에서 재확인)

추가 개선이 필요 없다고 판단한 항목. 다음 분석 때 중복 조사하지 않도록 기록한다.

| 영역 | 상태 |
|---|---|
| 코어 정상 상태 할당 | 0 B/op 유지(세그먼트 풀 + ArrayPool + RingBuffer). 벤치마크로 못 박혀 있음 |
| 큐 자료구조 | RingBuffer O(1) 앞 제거, 스냅숏 View 순회, `AtUnchecked` 핫루프 — PF-04 완료 상태 양호 |
| ACK 탐색 | `BinarySearchSn` 이분 탐색(PF-05) 적용됨 |
| 시계 | 핫루프 진입 시 1회 읽기(PF-01), `Stopwatch` 단조 시계, 랩어라운드 안전 비교 |
| 수신 주소 | net8 `SocketAddress` 재사용 + `AddressKey` 값 타입 키 — 수신 경로 무할당(PF-08/S-05) |
| 세션 테이블 lock | 변이는 수신 루프 스레드 단독, lock 은 외부 스냅숏 보호용으로 최소 — 경합 없음 |
| 로깅 | 마스크 가드 뒤에서만 문자열 보간 — 비활성 시 무할당 |
| FEC 인코더 | 생성 시 슬랩 재사용 — 무할당(디코더만 G-4 대상) |
| `SessionScheduler` | 최소 힙 + lazy deletion, O(log n) — 대량 세션 대비 완료 |

## 6. 진행 방법

1. 각 항목은 로드맵 규칙대로 **테스트 선행 → 구현 → 같은 커밋에 벤치마크 전후표** 를 남긴다.
2. 측정 도구: `benchmarks/KcpCSharpR1.Benchmarks`(MemoryDiagnoser 로 B/op), `samples/tools/LoadTest`(엔드 투 엔드),
   `dotnet-counters monitor --counters System.Runtime`(GC 할당률·Gen0 횟수).
3. G-1~G-5, C-1, A-1 은 독립적이라 순서 무관하게 각각 작은 커밋으로 진행할 수 있다.
   M-1~M-3, A-2 는 API·설계 결정이 필요하므로 착수 전에 로드맵에 작업 ID 로 등재하고 절을 추가한다.
