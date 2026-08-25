# 14장. 성능: 무할당 네트워킹

> **학습 목표**
> 1. 게임 서버에서 GC가 왜 문제인지, Gen0/1/2의 차이가 무엇인지 설명할 수 있습니다.
> 2. 이 라이브러리의 무할당 설계 네 가지를 코드와 벤치 수치로 짚을 수 있습니다.
> 3. BenchmarkDotNet 결과를 읽고, 자기 코드에서 재현할 수 있습니다.

## GC가 게임에서 문제가 되는 이유

.NET의 GC는 훌륭합니다. 대부분의 애플리케이션에서는 신경 쓸 필요가 없습니다.
**게임은 예외**입니다.

| 세대 | 수집 시간(대략) | 게임에서 보이는 모습 |
|---|---|---|
| Gen0 | 수십 µs ~ 1ms | 대개 안 보인다 |
| Gen1 | 1~5ms | 프레임 하나가 살짝 길어진다 |
| **Gen2** | **수십~수백 ms** | **명백한 끊김. 서버라면 틱 하나를 통째로 놓친다** |

60fps 게임의 한 프레임은 **16.6ms**입니다. Gen2가 50ms 걸리면 3프레임이 사라집니다.
20Hz 서버라면 틱 하나가 밀리고, 밀린 만큼 모든 클라이언트의 지연이 늘어납니다.

**"평균은 좋은데 가끔 튄다"** 의 흔한 범인이 GC입니다.

```mermaid
flowchart LR
    A["매 패킷 할당"] --> B["Gen0 가 자주 돈다"]
    B --> C["살아남은 객체가<br/>Gen1 로 승격"]
    C --> D["Gen2 로 승격"]
    D --> E["Gen2 수집<br/>수십~수백 ms 정지"]
    E --> F["프레임 드랍 · 틱 밀림"]
```

### 목표: 정상 상태에서 할당 0

접속·해제 때 할당하는 것은 괜찮습니다. 문제는 **매 패킷·매 틱 할당**입니다.
초당 20패킷 × 1000세션 = 2만 번의 할당이 매초 일어나면 Gen0가 쉴 새 없이 돌고,
살아남은 것들이 Gen1 → Gen2로 승격됩니다.

그래서 이 라이브러리의 목표는 **"정상 상태 송수신 경로에서 관리 힙 할당 0 B/op"** 입니다.

## 무할당 설계 네 가지

### ① ArrayPool 로 세그먼트 페이로드 빌리기

```csharp
// src/KcpProject/KcpSegment.cs
/// <para>
/// 페이로드는 <see cref="ArrayPool{T}.Shared"/> 에서 빌린 배열이다. 대여 배열은 요청보다 클 수 있으므로
/// <b>유효 길이는 반드시 <see cref="Len"/> 으로 관리</b>하며 <c>Data.Length</c> 를 길이로 쓰지 않는다.
/// </para>
internal void Rent(int capacity)
{
    ReturnBuffer();
    if (capacity > 0)
    {
        // 민감 데이터가 없으므로 clearArray: false (기본값) 로 빌려 성능을 취한다.
        Data = ArrayPool<byte>.Shared.Rent(capacity);
    }
    Len = 0;
}
```

**함정 세 가지**를 지켜야 합니다.

| 함정 | 대응 |
|---|---|
| 이중 반환 | `Reset()` 을 멱등으로. 반환 후 `Array.Empty` 센티널 |
| 대여 배열이 요청보다 큼 | `Data.Length` 를 **절대** 유효 길이로 쓰지 않음 |
| 반환 전 초기화 비용 | 민감 데이터가 없으므로 `clearArray: false` |

이중 반환은 특히 위험합니다. 같은 배열이 풀에 두 번 들어가면
**서로 다른 두 곳이 같은 메모리를 쓰게 됩니다.** 재현이 거의 불가능한 버그가 됩니다.

```csharp
// src/KcpProject/KcpSegment.cs
private void ReturnBuffer()
{
    if (Data.Length > 0)
    {
        ArrayPool<byte>.Shared.Return(Data);
        Data = Array.Empty<byte>();   // 이중 반환 방지 센티널
    }
    Len = 0;
}
```

테스트로 못 박습니다.

```csharp
// tests/KcpProject.Tests/CodecTests.cs
[Fact]
public void Segment_Reset은_여러번_불러도_안전하다()
{
    var seg = new KcpSegment();
    seg.Rent(128);
    seg.Append(new byte[] { 1, 2, 3 });

    seg.Reset();
    seg.Reset();
    seg.Reset();

    Assert.Equal(0, seg.Len);
    Assert.Empty(seg.Data);
}
```

### ② Span 기반 코덱

`BinaryPrimitives` 를 쓰면 임시 배열 없이 바이트를 읽고 씁니다.

```csharp
// src/KcpProject/KcpCodec.cs
public static int WriteHeader(Span<byte> p, uint conv, byte cmd, byte frg, ushort wnd, uint ts, uint sn, uint una, uint len)
{
    WriteU32(p, conv);
    WriteU8(p.Slice(4), cmd);
    WriteU8(p.Slice(5), frg);
    WriteU16(p.Slice(6), wnd);
    WriteU32(p.Slice(8), ts);
    WriteU32(p.Slice(12), sn);
    WriteU32(p.Slice(16), una);
    WriteU32(p.Slice(20), len);
    return Kcp.IKCP_OVERHEAD;
}
```

공개 API에도 Span 오버로드를 추가했습니다. 기존 `byte[]` 오버로드는 호환을 위해 남겼습니다.

```csharp
public int Send(ReadOnlySpan<byte> data);
public int Input(ReadOnlySpan<byte> data, bool regular, bool ackNoDelay);
public int Recv(Span<byte> buffer);
```

### ③ 원형 덱으로 큐 교체

KCP의 네 큐는 **"앞에서 n개 제거"** 가 잦습니다.
`List<T>.RemoveRange(0, n)` 은 매번 나머지를 앞으로 당깁니다 — O(n)입니다.

```csharp
// src/KcpProject/RingBuffer.cs
/// <remarks>
/// KCP 의 네 큐(snd_queue, rcv_queue, snd_buf, rcv_buf)는 "앞에서 n개 제거"가 잦다.
/// <see cref="System.Collections.Generic.List{T}"/> 의 <c>RemoveRange(0, n)</c> 는 매번 O(n) 이동이 발생하지만
/// 이 자료구조는 head 인덱스만 옮기므로 O(1) 이다(진단 PF-04).
/// </remarks>
public void RemoveFront(int n)
{
    if (n <= 0) return;
    if (n > count) n = count;

    var mask = items.Length - 1;
    // 참조 타입이면 남은 참조를 끊어 GC 를 방해하지 않게 한다.
    if (!typeof(T).IsValueType)
    {
        for (var i = 0; i < n; i++)
            items[(head + i) & mask] = default!;
    }

    head = (head + n) & mask;
    count -= n;
}
```

용량을 2의 거듭제곱으로 유지해 나눗셈 대신 **마스크 연산**을 씁니다.

### ④ 인스턴스별 풀 — lock 없애기

세그먼트 풀이 전역 static이면 세션이 늘수록 **모든 세션이 같은 락을 두드립니다.**

```csharp
// src/KcpProject/Kcp.cs
// 인스턴스 전용 세그먼트 풀. 소유 스레드 규약(2.3절) 덕분에 lock 이 필요 없다(진단 PF-06).
private readonly Stack<KcpSegment> segmentPool = new Stack<KcpSegment>(32);
```

"인스턴스 1개 = 소유 스레드 1개" 규약(5장)이 있으니 락이 필요 없습니다.
**규약이 성능을 만드는** 좋은 예입니다.

## 측정 결과

전부 `docs/benchmarks.md` 에 재현 명령어와 함께 기록되어 있습니다.

| 벤치마크 | 개선 전 | 개선 후 | 변화 |
|---|---:|---:|---:|
| `SendRecvRoundtrip` (1000 × 1KB) | 441.3 µs | **321.3 µs** | **−27.2%** |
| `InputAckBatch` (64 ACK) | 1.428 µs | **1.318 µs** | −7.7% |
| `FlushLargeWindow` (윈도 1024) | 1.921 µs | **1.642 µs** | −14.5% |
| `FragmentationLarge` (32KB) | 8.252 µs | **6.882 µs** | −16.6% |
| 정상 상태 할당 | 0 B/op | **0 B/op** | 유지 |

### "할당이 원래 0이었다"는 사실

솔직하게 말하면, 개선 전에도 정상 상태 할당은 0이었습니다.
기존의 자체 버퍼 풀과 세그먼트 풀이 이미 재사용을 하고 있었기 때문입니다.

그래서 Phase 3의 성과는 **"할당을 0으로 만든 것"이 아니라 "0을 유지하면서 아래를 없앤 것"** 입니다.

- 전역 static 풀의 `lock`(세션이 늘수록 경합)
- 풀에서 임의 크기 버퍼가 나오던 문제
- `RemoveRange(0, n)` 의 O(n) 이동
- 호출당 임시 배열

**정확하게 말하는 것이 중요합니다.** "27% 빨라졌다"는 사실이고,
"할당을 없앴다"는 과장입니다.

## BenchmarkDotNet 읽는 법

```
| Method    | Messages | Mean     | Error    | StdDev  | Allocated |
|---------- |--------- |---------:|---------:|--------:|----------:|
| Roundtrip | 1000     | 321.3 us | 15.78 us | 4.10 us |         - |
```

| 열 | 뜻 |
|---|---|
| `Mean` | 평균 실행 시간 |
| `Error` | 99.9% 신뢰 구간의 절반 — **이보다 작은 차이는 노이즈** |
| `StdDev` | 표준편차 — 크면 측정이 불안정하다는 뜻 |
| `Allocated` | 호출당 관리 힙 할당. `-` 는 0 |

**`Error` 를 반드시 보세요.** 321.3 ± 15.78 과 330.0 ± 20.0 은 사실상 같은 값입니다.
"3% 빨라졌다"고 주장하려면 Error가 그보다 작아야 합니다.

### 재현하기

```bash
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*SendRecvRoundtrip*'
```

**반드시 `-c Release`** 입니다. Debug 빌드 수치는 의미가 없습니다.

## 측정하면서 배운 것 — 함정 세 가지

`docs/benchmarks.md` 에 그대로 남겨 둔 기록입니다.

### ① 벤치마크가 의도한 것을 재지 않았다

`FlushLargeWindow` 는 "송신 버퍼 1024개일 때의 순회 비용"을 재려 했습니다.
그런데 상대 윈도(`rmt_wnd`)의 기본값이 32라 **실제로는 32개만 차 있었습니다.**

WINS 패킷을 하나 넣어 윈도를 열어 준 뒤에야 제대로 잴 수 있었습니다.

```csharp
// benchmarks/KcpProject.Benchmarks/KcpBenchmarks.cs
// 상대가 큰 윈도를 통지해야 snd_buf 가 실제로 WindowSize 만큼 찬다(rmt_wnd 기본값은 32).
var wins = new byte[Kcp.IKCP_OVERHEAD];
...
kcp.Input(wins, 0, wins.Length, true, false);
```

### ② 시계를 진행시켰더니 측정이 흔들렸다

벤치마크 안에서 가상 시각을 1ms씩 올렸더니, 200회쯤부터 RTO가 만료되어
**1024개 전량 재전송**이 섞였습니다. BenchmarkDotNet의 호출 횟수에 따라 평균이 달라졌습니다.

지금은 시각을 고정해 **순수 순회 비용**만 잽니다.

### ③ 자료구조를 바꿨더니 오히려 느려졌다

`List` → `RingBuffer` 로 바꾼 직후 `FlushLargeWindow` 가 **15% 느려졌습니다.**
원소 접근마다 `items`/`head`/`Length` 를 다시 읽는 비용 때문이었습니다.

순회 구간에서 배열·head·mask를 지역에 붙잡는 **스냅숏 뷰**를 도입해 기준선보다 빠르게 되돌렸습니다.

```csharp
// src/KcpProject/Kcp.cs
// 이 루프는 snd_buf 를 수정하지 않으므로 스냅숏 뷰로 순회한다(원소당 필드 재로드 제거).
var sndBufView = snd_buf.CreateView();
var sndBufCount = snd_buf.Count;
for (var k = 0; k < sndBufCount; k++)
{
    var segment = sndBufView[k];
    ...
}
```

**이 과정이 없었다면 "자료구조를 바꿨으니 빨라졌겠지"라고 넘어갔을 것입니다.**
측정이 먼저인 이유가 여기 있습니다.

## 서버 용량 산정 감각

부하 도구로 잰 실측(같은 PC에서 클라·서버 동시 실행):

| 세션 | 재전송률 | RTT p99 | Gen2 | 판정 |
|---:|---:|---:|---:|---|
| 300 | 0.00% | 48.5ms | 0회 | 여유 |
| 1,000 | 1.13% | 517ms | 1회 | 포화 |

**세션당 메모리** 감각치:

| 항목 | 크기 |
|---|---|
| `KcpSession` + `Kcp` 기본 | 수 KB |
| 소켓 버퍼(기본 1MB × 2) | **2MB** ← 가장 큼 |
| 송신 백로그(가득 찼을 때) | `MaxSendBacklog × MTU` |

**소켓 버퍼가 압도적**입니다. 서버는 소켓 하나로 모든 세션을 받으므로 문제없지만,
**클라이언트를 1000개 띄우는 부하 도구**에서는 2GB를 요청하게 됩니다.
그래서 도구에는 `--sockbuf` 옵션이 있습니다.

`MaxSendBacklog` 도 조심해야 합니다. 기본 1024에 MTU 1400이면 세션당 최대 1.4MB입니다.
부하 도구에서 4096으로 잡았다가 **관리 힙이 1GB까지 부풀어 오르는 것**을 봤습니다.

```csharp
// samples/tools/LoadTest/Program.cs
// 부하 도구는 밀리면 흘려보내는 편이 낫다. 상한을 크게 잡으면 세션 하나가
// 수 MB 의 대여 버퍼를 물고 있어 메모리가 폭증한다(1,000세션에서 관리 힙 1GB 를 봤다).
MaxSendBacklog = 256,
```

## 요약

- 게임에서 문제가 되는 것은 **Gen2**입니다. 정상 상태 할당을 0으로 만드는 것이 목표입니다.
- 무할당 설계 네 가지: **ArrayPool 대여 / Span 코덱 / 원형 덱 / 인스턴스별 풀**.
- ArrayPool은 **이중 반환**과 **대여 배열이 요청보다 큼**을 반드시 지켜야 합니다.
- BenchmarkDotNet의 **`Error` 보다 작은 차이는 노이즈**입니다.
- 측정은 **의도한 것을 재고 있는지부터** 확인하세요. 세 번 중 세 번 다 함정이 있었습니다.

## 연습문제

1. **할당 찾기** — 여러분의 게임 서버 코드에서 매 패킷 할당이 일어나는 곳을 찾아보세요.
   `data.ToArray()`, `string` 연결, LINQ, 람다 캡처가 흔한 범인입니다.
2. **벤치마크 작성** — 자기 프로토콜의 인코딩/디코딩 벤치마크를 `[MemoryDiagnoser]` 와 함께
   작성하고, 할당이 0인지 확인해 보세요.
3. **백로그 실험** — `MaxSendBacklog` 를 32 / 256 / 4096 으로 바꿔 가며 부하 도구를 돌리고,
   관리 힙과 RTT p99가 어떻게 변하는지 재 보세요.

---

다음 장은 보안입니다. 지금까지 만든 것에 **누가 무엇을 할 수 있는지** 살펴봅니다.
