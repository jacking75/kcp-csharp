# 벤치마크 기록

Phase 3(성능 최적화)의 기준선과 개선 결과를 기록한다. 로드맵 §6 의 공통 절차대로
**① 기준선 측정 → ② 변경 → ③ 재측정 → ④ 전/후 표 첨부** 순으로 진행했다.

## 재현 방법

```bash
# 전체
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*'

# 개별
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*SendRecvRoundtrip*'
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*InputAckBatch*'
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*FlushLargeWindow*'
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*FragmentationLarge*'
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

## 앞으로 추가할 항목

- 1,000 세션 LoadTest(P7-6) 의 60초 Gen2 GC 횟수·메모리 평탄성
- 암호화 on/off 비교(P5-1)
- FEC on/off 시 손실 20% 환경의 재전송률(P5-2)
