# 벤치마크 기록

Phase 3(성능 최적화)의 기준선과 개선 결과를 기록한다. 로드맵 §6 의 공통 절차대로
**① 기준선 측정 → ② 변경 → ③ 재측정 → ④ 전/후 표 첨부** 순으로 진행했다.

## 재현 방법

```bash
# 전체
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*'

# 개별
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*SendRecvRoundtrip*'
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*InputAckBatch*'
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*FlushLargeWindow*'
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*FragmentationLarge*'
```

모든 벤치마크에 `[MemoryDiagnoser]` 가 붙어 있어 할당(B/op)이 함께 나온다.
`[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]` 로 짧게 돌도록 맞춰 두었다.

## 측정 환경

| 항목 | 값 |
|---|---|
| 측정일 | 2026-08-25 |
| OS | Windows 11 (10.0.26200) |
| 런타임 | .NET 8.0.29 (X64 RyuJIT AVX2) |
| SDK | .NET SDK 10.0.302 |
| BenchmarkDotNet | 0.13.12 |

> 절대 수치는 기계에 따라 달라진다. **비교는 같은 기계에서 같은 벤치마크 코드로 잰 전/후 값끼리만** 한다.
> 아래 표의 '기준선'과 '개선 후'는 같은 커밋의 벤치마크 코드를 각각 Phase 2 코어와 Phase 3 코어에
> 대해 실행해 얻었다(코어만 갈아 끼워 측정).

## 벤치마크 항목

| 벤치마크 | 내용 | 무엇을 보는가 |
|---|---|---|
| `SendRecvRoundtrip` | 무손실 루프백, 1KB × 1,000 메시지 왕복 | 정상 상태 송수신 경로의 종합 비용 |
| `InputAckBatch` | ACK 64개가 한 패킷에 묶여 들어오는 파싱 | `parse_ack`/`parse_fastack` 탐색 비용 (PF-05) |
| `FlushLargeWindow` | snd_buf 1,024 세그먼트 상태의 Flush 순회 | 큐 자료구조 순회 비용 (PF-04) |
| `FragmentationLarge` | 32KB 메시지 조각화 → 재조립 왕복 | 조각화/버퍼 관리 비용 (PF-02, PF-03) |

## 결과 (Phase 3 전 → 후)

| 벤치마크 | 기준선 (Phase 2 코어) | 개선 후 (Phase 3 코어) | 변화 |
|---|---:|---:|---:|
| `SendRecvRoundtrip` (1000 × 1KB) | 441.3 µs | **321.3 µs** | **−27.2%** |
| `InputAckBatch` (64 ACK) | 1.428 µs | **1.318 µs** | **−7.7%** |
| `FlushLargeWindow` (윈도 1024) | 1.921 µs | **1.642 µs** | **−14.5%** |
| `FragmentationLarge` (32KB) | 8.252 µs | **6.882 µs** | **−16.6%** |
| 정상 상태 관리 힙 할당 | 0 B/op | **0 B/op** | 유지 |

### 할당 0 B/op 에 대해

Phase 2 코어도 이미 정상 상태 할당이 0 B/op 였다(자체 `ByteBuffer` 풀 + `Segment` 풀이
워밍업 이후 재사용되기 때문). Phase 3 의 성과는 **할당을 0으로 만든 것이 아니라,
0을 유지하면서 아래를 없앤 것**이다.

- 전역 static 풀에 걸려 있던 `lock` (세션이 늘수록 경합) → 인스턴스 전용 풀 (PF-06)
- 풀에서 임의 크기 버퍼가 나오던 문제 → `ArrayPool<byte>` 대여 + 유효 길이 분리 (B-07 근본 해결)
- `List<Segment>.RemoveRange(0, n)` 의 O(n) 이동 → `RingBuffer` head 이동 O(1) (PF-04)
- `ByteBuffer.Get()/Read()` 의 호출당 임시 배열 → Span 경로로 제거 (PF-02, PF-03)

## 적용한 변경

| 작업 | 내용 |
|---|---|
| P3-2 | `KcpCodec`(BinaryPrimitives) 신설, 공개 API 에 `Span` 오버로드 추가(`Send`/`Input`/`Recv`) |
| P3-3 | `Segment.data` 를 `ByteBuffer` → `ArrayPool<byte>` 대여 배열 + 유효 길이(`Len`)로 교체, 코어에서 `ByteBuffer` 삭제 |
| P3-4 | 네 큐를 `List<Segment>` → `RingBuffer<KcpSegment>` 로 교체 |
| P3-5 | 세그먼트 풀을 전역 static(lock) → 인스턴스 필드(lock 없음)로, `Kcp.Release()` 추가 |
| P3-6 | `parse_ack`/`parse_fastack` 을 이분 탐색으로 |

## 측정 중에 배운 것 (함정 기록)

1. **`FlushLargeWindow` 초기 설계 오류** — 상대 윈도(`rmt_wnd`)의 기본값이 32라
   `WndSize(1024, 1024)` 만으로는 snd_buf 가 32개밖에 차지 않았다. WINS 패킷을 하나 넣어
   윈도를 열어 준 뒤에야 1,024개가 찬 상태를 잴 수 있었다.
2. **시계를 진행시키면 측정이 흔들린다** — 벤치마크 안에서 가상 시각을 1ms 씩 올렸더니
   200회쯤부터 RTO 가 만료돼 1,024개 전량 재전송이 섞였다. 호출 횟수에 따라 평균이 달라지므로
   지금은 시각을 고정해 **순수 순회 비용**만 잰다.
3. **`RingBuffer` 도입 직후 오히려 15% 느려졌다** — 원소 접근마다 `items`/`head`/`Length` 를
   다시 읽는 비용 때문이다. 순회 구간에서 배열·head·mask 를 지역에 붙잡는 스냅숏 뷰
   (`RingBuffer<T>.CreateView()`)를 도입해 기준선보다 빠르게 되돌렸다. 이 과정이 없었다면
   "자료구조를 바꿨으니 빨라졌겠지"라고 넘어갔을 것이다 — 측정이 먼저인 이유.

## Phase 5 — 암호화(P5-1)

`--filter '*PacketCrypto*'` 로 재현한다. 패킷 크기 1,376바이트(MTU 1400 기준 KCP 페이로드).

| 항목 | 시간 | 기준선 대비 | 할당 |
|---|---:|---:|---:|
| `PlainCopy` (암호화 없음, 복사만) | 13.41 ns | 1.00× | 0 B |
| `Encrypt` (AES-256-GCM) | 357.7 ns | 26.7× | 0 B |
| `Decrypt` (인증 + 리플레이 검사 포함) | 351.3 ns | 26.2× | 0 B |

읽는 법: 암호화를 켜면 패킷당 약 **0.36µs** 가 더 든다. 1,000 세션이 각각 초당 30패킷을 주고받으면
30,000 패킷/초 × 0.36µs ≈ **11ms/초**, 즉 코어 하나의 약 1.1% 다. 게임 서버에서 감당할 만한 비용이다.
오버헤드는 패킷당 24바이트(카운터 8 + 태그 16)다.

> `Decrypt` 측정 주의: 리플레이 윈도 때문에 같은 패킷을 두 번 복호화할 수 없다.
> 카운터가 다른 패킷 256개를 미리 만들어 두고, 반복마다 새 수신기로 한 묶음씩 처리해
> 리플레이 검사에 걸려 조기 반환되는 일이 없도록 했다(처음엔 이 함정에 빠져 3ns 라는 가짜 수치가 나왔다).

## Phase 5 — FEC(P5-2)

### 부호화 비용 (`--filter '*FecCoding*'`, 10+3 그룹, 샤드 1,376바이트)

| 항목 | 시간 | 할당 |
|---|---:|---:|
| `Encode` (패리티 3개 생성) | 30.05 µs | 0 B |
| `Reconstruct` (데이터 3개 복원) | 31.53 µs | 5,896 B |

그룹당 30µs 는 데이터 패킷 10개마다 한 번이므로 패킷당 약 3µs 다. 암호화보다 훨씬 비싸므로
**손실이 실제로 문제가 되는 회선에서만** 켜는 것이 맞다.

### 손실 20% 환경의 재전송 감소 (P5-2 ③)

`Phase5IntegrationTests.FEC가_손실_환경의_재전송을_줄인다` 가 검증하는 수치다.
100KB 를 보내면서 클라이언트가 내보내는 KCP 프레임 5개마다 1개(20%)를 결정적으로 떨어뜨렸다.

| 설정 | KCP 재전송 | FEC 복원 |
|---|---:|---:|
| FEC 없음 | 23회 | — |
| FEC 10+3 | **7회** | 16패킷 |

재전송이 **70% 줄었다**. 재전송 한 번은 최소 1 RTT 를 더 기다린다는 뜻이므로, 손실이 잦은
모바일 회선에서 체감 지연이 크게 달라진다. 대신 패리티 패킷만큼(10+3 이면 30%) 대역폭을 더 쓴다.

## Phase 7 — 1,000 세션 부하 측정(P7-6)

Phase 4 완료 조건("1,000 동시 세션·60초에서 오류 0, 힙 안정")의 검증이다.
**클라이언트와 서버를 같은 PC에서 동시에** 돌린 결과이므로, 서버 단독 용량이 아니라
`한 대에서 양쪽을 다 돌렸을 때`의 수치임에 주의한다.

```bash
dotnet run -c Release --project samples/tools/LoadTest -- server
dotnet run -c Release --project samples/tools/LoadTest --   client --sessions 1000 --rate 20 --size 256 --duration 60 --sockbuf 65536
```

| 항목 | 결과 |
|---|---|
| 세션 | 1,000 연결 / **1,000 생존**(60초 내내) |
| 메시지 | 보냄 970,627 / **에코 970,627 (손실 0.00%)** |
| 무결성 | **불일치 0건** |
| 처리량 | 송신 40.3 Mbps / 수신 39.6 Mbps (16,175 msg/s) |
| 재전송 | 21,906 / 1,940,694 세그먼트 (**1.13%**) |
| RTT | p50 30ms · p95 250ms · p99 517ms · max 1,770ms |
| GC | Gen0 6 / Gen1 3 / **Gen2 1** |
| 메모리 | 관리 힙 219.6 MB, 작업 집합 386.3 MB |

**300 세션**(같은 조건)에서는 훨씬 여유롭다 — 재전송 **0.00%**, RTT p99 **48.5ms**, Gen2 **0회**.
즉 이 PC 한 대에서 클라·서버를 함께 돌릴 때의 편안한 한계는 300~400 세션이고,
1,000 세션은 "동작은 하지만 지연 꼬리가 길어지는" 포화 지점이다.

### Gen2 1회에 대해

부하 **생성기 프로세스**의 수치다. 1,000개의 세션 객체와 소켓을 만드는 접속 단계에서
수백 MB를 할당하기 때문이며, 라이브러리의 정상 상태 송수신 경로가 **0 B/op** 인 것은
BenchmarkDotNet 측정(위 Phase 3 표)으로 따로 확인했다.

### 측정하면서 고친 것 (도구 쪽 함정 3가지)

1. **접속 단계에서 기존 세션을 안 돌렸다** — 세션을 순차로 1,000개 여는 데 십수 초가 걸리는데,
   그동안 먼저 연결된 세션들이 keepalive 를 못 보내 **서버가 10초 타임아웃으로 끊었다**.
   1,000개를 연결해도 서버에는 325개만 남던 원인. 접속 루프에서 기존 세션도 주기적으로
   `Update()` 하도록 고쳤다.
2. **서버가 `Send` 반환값을 버렸다** — 백로그 상한을 넘으면 라이브러리는 `Backpressure` 를
   돌려주고 메시지를 받지 않는데 이를 무시해, "재전송은 0인데 에코가 안 온다"는 이해 못 할
   결과가 나왔다. 지금은 버린 개수를 세어 보고한다.
3. **측정기가 GC 를 유발했다** — RTT 표본 96만 개를 `List<double>` 에 쌓아 측정 도구 자신이
   Gen2 를 만들었다. 1ms 폭 고정 히스토그램(세션당 8KB)으로 바꿔 실행 중 추가 할당을 없앴다.

세 가지 모두 **라이브러리가 아니라 측정 코드의 결함**이었다. 부하 측정에서 이상한 숫자가 나오면
측정기부터 의심하라는 사례로 책 17장에 수록한다.

## 앞으로 추가할 항목

- 서버 단독 용량(부하 생성기를 다른 PC로 분리한 측정)
- 전송 계층(핸드셰이크 포함) 종단 간 처리량
