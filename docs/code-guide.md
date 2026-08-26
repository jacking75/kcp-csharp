# kcp-csharp 코드 완전 해설서

> 이 문서는 `src/KcpCSharpR1`, `src/KcpCSharpR1.Transport` 두 프로젝트의 **소스 코드 자체**를
> 처음부터 끝까지 설명합니다. [온라인 책](https://jacking75.github.io/kcp-csharp/)이
> "이 라이브러리를 어떻게 쓰는가"를 샘플과 함께 가르친다면, 이 문서는 **"이 라이브러리는
> 어떻게 만들어져 있는가"**를 코드 한 줄 한 줄 수준으로 설명합니다.

## 이 문서를 읽어야 하는 사람

- 라이브러리 내부를 수정하거나 기여하고 싶은 사람
- "왜 하필 이렇게 짰을까?"가 궁금해서 소스를 열어 본 사람
- KCP/ARQ 프로토콜을 실제 구현체로 배우고 싶은 사람 (원본 C `kcp`, Go `kcp-go`를 아는 사람이라면
  "C#에서는 이 부분을 이렇게 바꿨구나"를 대조하며 읽기 좋습니다)
- 코드 리뷰나 성능 튜닝을 위해 특정 파일의 동작을 정확히 알아야 하는 사람

**전제 지식은 최소화했습니다.** C# 문법과 UDP가 TCP와 다르다는 정도만 알면 됩니다. ARQ·슬라이딩
윈도·리드-솔로몬 같은 개념은 이 문서 안에서 코드와 함께 설명합니다. 더 원론적인 설명이 필요하면
책 [2장(ARQ 원리)](book/ch02-arq.md)·[3장(KCP 해부)](book/ch03-kcp-anatomy.md)을 먼저 보는 것도
좋습니다 — 이 문서와 그 두 장은 같은 코드를 다른 각도(개념 우선 vs 코드 우선)에서 다룹니다.

## 읽는 법

문서 전체가 실제 소스 파일 순서를 그대로 따라갑니다. 처음부터 끝까지 읽어도 되고, 아래 지도에서
궁금한 파일을 찾아 그 절만 읽어도 됩니다. 모든 코드 블록은 **저장소의 실제 코드**이며, 지어낸
예시가 아닙니다. 코드가 바뀌면 이 문서도 함께 고쳐야 합니다.

---

## 목차

1. [큰 그림](#1-큰-그림)
2. [파일 지도](#2-파일-지도)
3. [코어 — `src/KcpCSharpR1`](#3-코어--srckcpcsharpr1)
   - 3.1 [`KcpCodec.cs` — 와이어 인코딩](#31-kcpcodeccs--와이어-인코딩)
   - 3.2 [`KcpSegment.cs` — 세그먼트와 ArrayPool](#32-kcpsegmentcs--세그먼트와-arraypool)
   - 3.3 [`RingBuffer.cs` — 원형 덱](#33-ringbuffercs--원형-덱)
   - 3.4 [`Kcp.cs` — 프로토콜 상태 머신](#34-kcpcs--프로토콜-상태-머신)
   - 3.5 [`KcpClock.cs` — 단조 시계](#35-kcpclockcs--단조-시계)
   - 3.6 [`KcpStats.cs` / `PooledBuffer.cs` / `UDPSession.cs`](#36-kcpstatscs--pooledbuffercs--udpsessioncs)
4. [전송 계층 — `src/KcpCSharpR1.Transport`](#4-전송-계층--srckcpcsharpr1transport)
   - 4.1 [`Framing.cs` — 프레임 8종](#41-framingcs--프레임-8종)
   - 4.2 [`AddressKey.cs` — 값 타입 주소](#42-addresskeycs--값-타입-주소)
   - 4.3 [`IUdpTransport.cs` / `UdpSocketTransport.cs` — 소켓 계층](#43-iudptransportcs--udpsockettransportcs--소켓-계층)
   - 4.4 [`HandshakeCookie.cs` — 무상태 SYN 쿠키](#44-handshakecookiecs--무상태-syn-쿠키)
   - 4.5 [`KcpSession.cs` — 세션 상태 머신](#45-kcpsessioncs--세션-상태-머신)
   - 4.6 [`KcpServer.cs` — 단일 수신 루프 서버](#46-kcpservercs--단일-수신-루프-서버)
   - 4.7 [`KcpClient.cs` — 클라이언트](#47-kcpclientcs--클라이언트)
   - 4.8 [`SessionScheduler.cs` — 최소 힙 타이머](#48-sessionschedulercs--최소-힙-타이머)
   - 4.9 [`KcpSessionOptions.cs` / `KcpServerOptions`](#49-kcpsessionoptionscs--kcpserveroptions)
   - 4.10 [`AesGcmPacketCipher.cs` — 패킷 암호화](#410-aesgcmpacketciphercs--패킷-암호화)
   - 4.11 [`Fec/` — Reed-Solomon FEC](#411-fec--reed-solomon-fec)
   - 4.12 [`KcpEventQueue.cs` — 게임 스레드 이벤트 큐](#412-kcpeventqueuecs--게임-스레드-이벤트-큐)
   - 4.13 [`ServerMetrics.cs` — 관측성](#413-servermetricscs--관측성)
5. [전체 데이터 흐름](#5-전체-데이터-흐름)
6. [스레딩 모델](#6-스레딩-모델)
7. [메모리·할당 모델](#7-메모리할당-모델)
8. [코드를 확장하려면](#8-코드를-확장하려면)
9. [테스트가 이 코드를 검증하는 방법](#9-테스트가-이-코드를-검증하는-방법)
10. [더 읽을 거리](#10-더-읽을-거리)

---

## 1. 큰 그림

이 라이브러리는 두 프로젝트로 나뉩니다. **경계가 명확합니다.**

```
┌─────────────────────────────────────────────────────────────────┐
│  src/KcpCSharpR1.Transport   (전송 계층 · 소켓 · 세션 · 보안)      │
│                                                                   │
│   KcpServer / KcpClient                                          │
│        │ 소유 스레드 1개가 소켓 I/O + 세션 타이머를 다 돈다        │
│        ▼                                                          │
│   KcpSession (상태 머신) ── 핸드셰이크 · keepalive · FIN 종료      │
│        │ 프레이밍(Framing) → [선택] FEC → [선택] 암호화             │
│        ▼                                                          │
│   IUdpTransport (소켓 추상화) ── UdpSocketTransport(실소켓)        │
└─────────────────────────────────┬─────────────────────────────────┘
                                   │ Kcp 인스턴스를 소유하고 구동한다
┌─────────────────────────────────▼─────────────────────────────────┐
│  src/KcpCSharpR1              (코어 · 순수 프로토콜 상태 머신)      │
│                                                                   │
│   Kcp — 24바이트 헤더, ARQ(재전송), 슬라이딩 윈도, 혼잡 제어         │
│   소켓도, 스레드도, 시계도 모른다. 전부 생성자로 주입받는다.         │
└─────────────────────────────────────────────────────────────────┘
```

**코어(`Kcp` 클래스)는 원본 C `kcp` / Go `kcp-go`의 이식입니다.** 24바이트 헤더와 프로토콜
동작(재전송 타이밍, 윈도 계산)은 원본과 **바이트 단위로 호환**되도록 고정되어 있습니다. 이 계층을
읽을 때는 "C#답게 고쳐도 되는 것"과 "원본과 똑같아야 하는 것"을 구분하는 것이 중요합니다 —
**알고리즘과 와이어 포맷은 원본 그대로**, **자료구조와 메모리 관리는 C#에 맞게 새로 짰습니다**
(`ArrayPool`, `Span<T>`, 원형 덱 등).

**전송 계층(`KcpCSharpR1.Transport`)은 이 저장소가 새로 만든 것**입니다. 원본 kcp/kcp-go에는
핸드셰이크·세션 수명·암호화·FEC가 없습니다(kcp-go는 있지만 설계가 다릅니다). 이 계층이 그 위에
게임 서버가 실제로 필요로 하는 것들 — 연결 개념, 다중 클라이언트, 보안, 손실 복구 — 을 얹습니다.

세 가지 불변 원칙이 코드 전체를 관통합니다. (근거: `CLAUDE.md`)

1. **와이어 호환** — 코어 24바이트 헤더는 원본과 바이트 단위로 같다. 확장(핸드셰이크, 암호화,
   FEC)은 전부 프레이밍 계층(1바이트 타입 헤더)에서 이루어진다.
2. **소유 스레드 규약** — `Kcp` 인스턴스 1개, `KcpSession` 1개는 각각 스레드 1개에서만 건드린다.
   코어 안에는 `lock`이 단 하나도 없다. 다른 스레드에서 접근해야 할 때는 큐를 거친다
   ([6장](#6-스레딩-모델)에서 상세히 다룹니다).
3. **무할당 정상 상태** — 연결이 맺어진 뒤 평상시 송수신 경로는 할당이 0바이트다. `ArrayPool`
   대여, 원형 덱, 세션 상주 버퍼가 이걸 만든다 ([7장](#7-메모리할당-모델)).

---

## 2. 파일 지도

| 파일 | 줄 수 | 한 줄 요약 |
|---|---:|---|
| **코어 — `src/KcpCSharpR1`** | | |
| `Kcp.cs` | ~1,540 | 프로토콜 상태 머신 본체. 이 라이브러리의 심장 |
| `KcpSegment.cs` | ~104 | 세그먼트(24B 헤더 + 페이로드) 표현, `ArrayPool` 대여 |
| `RingBuffer.cs` | ~186 | 원형 덱. `Kcp`의 네 개 큐가 이걸 쓴다 |
| `KcpCodec.cs` | ~53 | 리틀엔디언 정수 인코딩(`BinaryPrimitives` 래퍼) |
| `KcpClock.cs` | ~26 | `Stopwatch` 기반 단조 밀리초 시계 |
| `KcpStats.cs` | ~47 | 누적 통계 값 타입 |
| `PooledBuffer.cs` | ~108 | 레거시(`UDPSession`) 전용 성장형 버퍼 |
| `UDPSession.cs` | ~229 | `[Obsolete]` 레거시 최소 클라이언트 |
| **전송 계층 — `src/KcpCSharpR1.Transport`** | | |
| `Framing.cs` | ~283 | 프레임 타입 8종의 와이어 포맷 인코딩/디코딩 |
| `AddressKey.cs` | ~200 | 값 타입 IP+포트 키(세션 테이블용) |
| `IUdpTransport.cs` | ~91 | 소켓 추상 인터페이스 |
| `UdpSocketTransport.cs` | ~340 | 실소켓 구현. 무할당 송수신의 핵심 |
| `HandshakeCookie.cs` | ~114 | HMAC 기반 무상태 SYN 쿠키 |
| `KcpSession.cs` | ~1,052 | 세션 상태 머신. 두 번째로 복잡한 파일 |
| `KcpServer.cs` | ~527 | 다중 클라이언트 서버(단일 수신 루프) |
| `KcpClient.cs` | ~417 | 클라이언트(폴링형 / 자체 루프형) |
| `SessionScheduler.cs` | ~146 | 세션 타이머용 최소 힙 |
| `KcpSessionOptions.cs` | ~170 | 세션·서버 설정 |
| `IPacketCipher.cs` | ~39 | 암호화기 인터페이스 |
| `AesGcmPacketCipher.cs` | ~210 | AES-256-GCM 구현 |
| `Fec/GaloisField.cs` | ~108 | GF(2⁸) 유한체 연산 |
| `Fec/ReedSolomon.cs` | ~260 | 리드-솔로몬 소거 부호 |
| `Fec/FecCodec.cs` | ~334 | FEC 프레이밍 + 인코더/디코더 |
| `KcpEventQueue.cs` | ~171 | 게임 스레드로 넘기는 이벤트 큐 |
| `ServerMetrics.cs` | ~112 | 서버 상태 스냅숏 계산 |
| `Polyfills/IsExternalInit.cs` | 수 줄 | netstandard2.1에서 `init` 접근자를 쓰기 위한 폴백 타입 |

`samples/`, `tests/`, `benchmarks/`는 이 문서의 범위 밖입니다. 샘플 사용법은
[책 3부(9~13장)](https://jacking75.github.io/kcp-csharp/)를, 테스트 구조는
[9절](#9-테스트가-이-코드를-검증하는-방법)의 간단한 안내를 참고하세요.

---

## 3. 코어 — `src/KcpCSharpR1`

### 3.1 `KcpCodec.cs` — 와이어 인코딩

가장 작고 가장 자주 불리는 파일입니다. 하는 일은 하나뿐입니다 — **리틀엔디언 정수를 `Span<byte>`에
읽고 쓴다.**

```csharp
internal static class KcpCodec
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteU16(Span<byte> p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(p, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadU32(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32LittleEndian(p);
    // ...
}
```

왜 직접 비트 연산을 안 하고 `BinaryPrimitives`를 감쌌을까요? 세 가지 이유입니다.

1. **임시 배열이 안 생긴다.** `BitConverter.GetBytes()`는 매번 `byte[]`를 새로 만드는데,
   `BinaryPrimitives`는 이미 있는 `Span`에 직접 씁니다.
2. **`[MethodImpl(AggressiveInlining)]`** 로 JIT에게 인라이닝을 강하게 요청합니다. 세그먼트
   헤더는 필드가 8개라 이 함수가 세그먼트 하나당 8번 불립니다 — 함수 호출 오버헤드가 누적되면
   무시할 수 없는 크기가 됩니다.
3. **공개 API(`Kcp.ikcp_encode8u` 등)는 호환을 위해 남겨 두고**, 내부 구현만 이 클래스로
   바꿨습니다. 원본 kcp의 함수 이름을 그대로 쓰던 옛 코드가 있어도 깨지지 않습니다.

`WriteHeader`가 이 파일의 유일한 "조립" 함수입니다 — 세그먼트 24바이트 헤더 전체를 한 번에 씁니다.

```csharp
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
    return Kcp.IKCP_OVERHEAD;   // = 24
}
```

바이트 배치가 정확히 아래 24바이트 헤더 그림과 일치합니다 — 이건 우연이 아니라 **원본 kcp와 바이트
단위로 호환**되어야 한다는 불변 원칙 그 자체입니다.

```
 오프셋   0        4    5    6      8            12           16           20          24
        +--------+----+----+------+------------+------------+------------+------------+
        |  conv  |cmd |frg | wnd  |     ts     |     sn     |    una     |    len     |
        +--------+----+----+------+------------+------------+------------+------------+
  타입      u32    u8   u8   u16        u32          u32          u32          u32
```

| 필드 | 뜻 |
|---|---|
| `conv` | 대화 번호. 양 끝이 같아야 이 패킷을 받아들인다 |
| `cmd` | 명령 종류 — `PUSH`(81, 데이터) / `ACK`(82) / `WASK`(83, 윈도 질의) / `WINS`(84, 윈도 통지) |
| `frg` | **남은 조각 수**(내림차순). 0이면 이 조각이 메시지의 마지막 조각 |
| `wnd` | 내 수신 윈도의 남은 자리(상대에게 알려 줌) |
| `ts` | 보낸 시각(ms). RTT 계산에 쓴다 |
| `sn` | 순서 번호(0부터 증가) |
| `una` | "이 번호 미만은 다 받았다"(TCP의 누적 ACK와 같은 개념) |
| `len` | 뒤에 붙는 페이로드 길이 |

### 3.2 `KcpSegment.cs` — 세그먼트와 ArrayPool

`KcpSegment`는 세그먼트 하나(헤더 필드들 + 페이로드)를 표현하는 **internal 클래스**입니다.
`internal`인 이유는 사용자가 직접 다룰 일이 없고, 재사용(풀링)되는 가변 객체이기 때문입니다.

```csharp
internal sealed class KcpSegment
{
    internal uint conv, cmd, frg, wnd, ts, sn, una, rto, xmit, resendts, fastack, acked;

    internal byte[] Data = Array.Empty<byte>();   // ArrayPool 에서 빌린 배열
    internal int Len;                              // 유효 길이(Data.Length 와 다를 수 있다!)

    internal ReadOnlySpan<byte> Payload => Data.AsSpan(0, Len);
    internal int WritableBytes => Data.Length - Len;
```

**`Data.Length`를 유효 길이로 쓰면 안 됩니다.** `ArrayPool<byte>.Shared.Rent(100)`을 부르면
정확히 100바이트가 아니라 **다음으로 큰 버킷 크기**(보통 2의 거듭제곱, 128)가 돌아옵니다. 그래서
`Len` 필드를 따로 두고, `Payload`는 항상 `Data.AsSpan(0, Len)`로 자릅니다. 이 구분을 놓치면
가비지 바이트가 페이로드에 섞여 나갑니다 — 실제로 이 라이브러리 초기 버그(B-07)가 이거였습니다.

`Rent`/`Reset`이 대여·반환을 감쌉니다.

```csharp
internal void Rent(int capacity)
{
    ReturnBuffer();                                          // 이미 뭔가 들고 있으면 먼저 돌려준다
    if (capacity > 0)
        Data = ArrayPool<byte>.Shared.Rent(capacity);         // clearArray: false (기본값) — 민감 데이터 없으니 빠르게
    Len = 0;
}

internal void Reset()
{
    conv = cmd = frg = wnd = ts = sn = una = rto = xmit = resendts = fastack = acked = 0;
    ReturnBuffer();
}

private void ReturnBuffer()
{
    if (Data.Length > 0)
    {
        ArrayPool<byte>.Shared.Return(Data);
        Data = Array.Empty<byte>();   // 센티널: 이중 반환을 막는다
    }
    Len = 0;
}
```

`Data = Array.Empty<byte>()`가 **이중 반환 방지 센티널**입니다. `ArrayPool.Return`을 같은
배열에 두 번 부르면 풀이 오염되어(같은 배열이 두 사용자에게 동시에 대여될 수 있음) 데이터가
섞이는 조용한 버그가 됩니다. `Array.Empty<byte>()`는 항상 길이 0인 공유 싱글턴이라, `Data.Length
> 0` 검사 한 번으로 "이미 반환했음"을 판별할 수 있습니다.

`KcpSegment` 자체도 재사용됩니다 — `Kcp`가 인스턴스별 스택(`segmentPool`)에 다 쓴 세그먼트를
보관해 뒀다가 다음에 `new KcpSegment()` 대신 꺼내 씁니다. 이건 [3.4절](#34-kcpcs--프로토콜-상태-머신)에서
`Kcp.RentSegment`/`ReturnSegment`로 설명합니다.

### 3.3 `RingBuffer.cs` — 원형 덱

`Kcp`는 세그먼트를 담는 큐 4개(`snd_queue`, `snd_buf`, `rcv_buf`, `rcv_queue`)를 씁니다. 전부
`List<KcpSegment>`가 아니라 자체 `RingBuffer<T>`를 씁니다. 이유는 **"앞에서 n개 제거"의 비용**
때문입니다.

```csharp
// List<T> 였다면...
list.RemoveRange(0, n);   // 남은 원소를 전부 앞으로 당겨야 한다 — O(전체 길이)
```

KCP는 ACK를 받을 때마다 `snd_buf`에서 확인된 세그먼트를 앞에서부터 지우고, 메시지를 꺼낼 때마다
`rcv_queue` 앞에서 지웁니다. 윈도가 클수록(수백~수천 세그먼트) 이 이동 비용이 누적됩니다.

`RingBuffer<T>`는 **head 인덱스만 옮겨서** O(1)에 앞을 지웁니다.

```csharp
internal sealed class RingBuffer<T>
{
    private T[] items;
    private int head;
    private int count;

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref items[(head + index) & (items.Length - 1)];
        }
    }

    public void RemoveFront(int n)
    {
        if (n <= 0) return;
        if (n > count) n = count;

        var mask = items.Length - 1;
        if (!typeof(T).IsValueType)              // 참조를 끊어 GC 를 방해하지 않는다
            for (var i = 0; i < n; i++) items[(head + i) & mask] = default!;

        head = (head + n) & mask;                // ★ 이동은 이 한 줄뿐이다
        count -= n;
    }
```

용량은 항상 **2의 거듭제곱**입니다. `& (items.Length - 1)`이 나머지 연산(`% items.Length`)보다
훨씬 빠른 비트 AND로 인덱스를 감싸기 때문입니다. `NextPowerOfTwo`가 생성자에서 이를 보장합니다.

세 가지 접근 방식이 용도별로 나뉩니다.

| 멤버 | 언제 쓰나 | 특징 |
|---|---|---|
| `this[int index]` | 일반 접근 | 경계 검사 있음. `ref T` 반환이라 구조체도 복사 없이 수정 가능 |
| `AtUnchecked(int index)` | 범위가 이미 보장된 핫루프 | 경계 검사 생략. `[MethodImpl(AggressiveInlining)]` |
| `CreateView()` | 순회 중 큐를 안 건드리는 구간 | `items`/`head`/`mask`를 지역 변수에 스냅숏 |

`CreateView()`는 흥미로운 최적화입니다. `Kcp.Flush`가 `snd_buf`를 수천 번 순회하는 루프에서,
매 원소마다 `this.items`, `this.head`, `this.items.Length - 1`을 다시 읽으면(필드 역참조) 그
자체가 비용입니다. `View` 구조체가 이 세 값을 지역에 붙잡아 두면 JIT가 레지스터에 캐시하기
좋아집니다. **실측으로 15% 차이가 났습니다** — `docs/benchmarks.md`의 "측정하면서 배운 것" 절에
이 과정이 기록되어 있습니다: `RingBuffer`를 처음 도입했을 때 오히려 15% 느려졌다가, `View`를
추가하고서야 기준선보다 빨라졌습니다.

```csharp
public readonly struct View
{
    private readonly T[] items;
    private readonly int head;
    private readonly int mask;

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => items[(head + index) & mask];
    }
}
```

`View`는 **순회 전용**입니다. 뷰를 만든 뒤 원본에 `PushBack`/`RemoveFront`/`Insert`를 하면 배열이
통째로 바뀔 수 있어(용량 초과 시) 뷰가 무효가 됩니다. `Kcp.Flush`와 `Kcp.Check`는 이 구간에서
큐를 건드리지 않는다는 것을 보장한 뒤에만 `CreateView()`를 씁니다.

`Insert(int index, T item)`은 `rcv_buf`에 세그먼트를 정렬된 위치에 끼워 넣을 때 씁니다(수신
패킷이 순서대로 오지 않을 수 있으므로). 뒤쪽 원소를 한 칸씩 미는 O(n) 연산이지만, 수신 윈도가
크지 않은 한 실용적인 비용입니다.

### 3.4 `Kcp.cs` — 프로토콜 상태 머신

이 파일이 라이브러리의 핵심입니다. 클래스 주석이 설계 원칙을 정확히 말해 줍니다.

> 이 클래스는 **순수한 프로토콜 상태 머신**이다. 소켓·스레드·실시간 시계·로깅 구현을 알지
> 못하며 전부 주입받는다.

#### 3.4.1 상태 필드 전체 지도

`Kcp`는 필드가 많습니다. 역할별로 묶어서 보는 게 빠릅니다.

```csharp
// 연결 식별 · 크기
private uint conv;            // 대화 번호
private uint mtu, mss;        // MTU, 세그먼트당 최대 페이로드(= mtu - 24 - reserved)

// 송신 시퀀스
private uint snd_una;         // 아직 확인 안 된 가장 오래된 sn (= 상대에게 보낼 una 값의 근거)
private uint snd_nxt;         // 다음에 쓸 sn

// 수신 시퀀스
private uint rcv_nxt;         // 다음에 받아야 할 sn (이보다 작은 sn 은 이미 처리됨)

// RTT · RTO (RFC 6298 스타일)
private int rx_srtt, rx_rttval;   // 평활 RTT, RTT 편차
private uint rx_rto, rx_minrto;   // 현재 RTO, 최소 RTO(nodelay 모드면 30ms, 아니면 100ms)

// 윈도 · 혼잡 제어
private uint snd_wnd, rcv_wnd;    // 내가 설정한 송신/수신 윈도 크기
private uint rmt_wnd;             // 상대가 통지한 수신 윈도(내가 이만큼만 보낼 수 있다)
private uint cwnd, ssthresh, incr; // 혼잡 윈도, 혼잡 임계값, TCP 스타일 증가 누적치
private int nocwnd;                // 1 이면 혼잡 제어를 아예 끔(게임에서 흔한 선택)

// 큐 4개 (전부 RingBuffer<KcpSegment>)
private readonly RingBuffer<KcpSegment> snd_queue;  // 아직 윈도에 안 들어간 송신 대기열
private readonly RingBuffer<KcpSegment> snd_buf;    // 윈도에 들어가 전송된(ACK 대기 중인) 세그먼트
private readonly RingBuffer<KcpSegment> rcv_buf;    // 순서가 안 맞아 대기 중인 수신 세그먼트
private readonly RingBuffer<KcpSegment> rcv_queue;  // 순서가 맞아 애플리케이션에 넘길 준비가 된 세그먼트

private readonly List<ackItem> acklist;             // 보내야 할 ACK 목록(sn, ts 쌍)
```

네 개의 큐 이름이 헷갈리기 쉬운데, **송신은 `queue → buf`, 수신은 `buf → queue`** 로 기억하면
됩니다. `Send()`로 넣은 데이터는 `snd_queue`에서 대기하다가 윈도가 열리면 `snd_buf`로 옮겨져
실제로 나갑니다. 수신 패킷은 순서에 안 맞으면 `rcv_buf`에서 기다리다가, 순서가 맞아떨어지면
`rcv_queue`로 옮겨져 `Recv()`로 꺼낼 수 있게 됩니다.

#### 3.4.2 세그먼트 풀 — `RentSegment` / `ReturnSegment`

```csharp
private readonly Stack<KcpSegment> segmentPool = new Stack<KcpSegment>(32);
internal const int MaxSegmentPoolSize = 4096;

private KcpSegment RentSegment(int capacity)
{
    var seg = segmentPool.Count > 0 ? segmentPool.Pop() : new KcpSegment();
    seg.Rent(capacity);   // 내부적으로 ArrayPool.Rent
    return seg;
}

private void ReturnSegment(KcpSegment seg)
{
    seg.Reset();                                    // 필드 초기화 + ArrayPool.Return
    if (segmentPool.Count < MaxSegmentPoolSize)
        segmentPool.Push(seg);                       // 상한 넘으면 그냥 버린다(GC 가 회수)
}
```

이건 **두 단계 풀링**입니다. `KcpSegment` 객체 자체(필드 12개짜리 클래스)는 인스턴스별
`segmentPool`에서 재사용되고, 그 안의 `byte[] Data`는 `ArrayPool<byte>.Shared`에서 별도로
대여됩니다. 왜 두 단계일까요 — 객체 재사용은 GC 압력(Gen0 수집 빈도)을 줄이고, 배열 재사용은
큰 배열이 여러 크기로 필요할 때 `ArrayPool`의 버킷 시스템을 활용하기 위해서입니다.

**`segmentPool`이 `lock` 없이 안전한 이유**가 소유 스레드 규약의 실증적인 예입니다. 이 스택은
인스턴스 필드고, `Kcp` 인스턴스 하나는 스레드 하나에서만 건드입니다. 전역 `static` 풀이었다면
세션이 늘수록 여러 스레드가 경합해 `lock`이 필요했을 겁니다(실제로 초기 버전은 그랬고, `PF-06`
진단으로 인스턴스 풀로 바뀌었습니다 — `docs/benchmarks.md` 참고).

`MaxSegmentPoolSize`(4096) 상한이 있는 이유: 순간적으로 세그먼트를 아주 많이 쓴(예: 큰 메시지
조각화) 세션이 그 뒤로 계속 그 메모리를 쥐고 있지 않도록, 상한을 넘는 반환분은 그냥 버려서 GC가
회수하게 둡니다.

#### 3.4.3 송신 경로 — `Send()`

```csharp
public int Send(ReadOnlySpan<byte> data)
{
    if (0 == data.Length) return -1;

    if (stream != 0 && snd_queue.Count > 0)
    {
        // 스트림 모드: 마지막 세그먼트에 빈 자리가 있으면 이어 붙인다
        var last = snd_queue.AtUnchecked(snd_queue.Count - 1);
        if (last.Len < mss)
        {
            var writen = Math.Min((int)mss - last.Len, data.Length);
            last.Append(data.Slice(0, writen));
            data = data.Slice(writen);
        }
    }
    if (data.Length == 0) return 0;

    var count = data.Length <= mss ? 1 : (int)((data.Length + mss - 1) / mss);

    // 조각 수가 수신 윈도 이상이면 상대가 절대 재조립할 수 없다(진단 B-04)
    if (count >= rcv_wnd) return -2;

    for (var i = 0; i < count; i++)
    {
        var size = Math.Min(data.Length, (int)mss);
        var seg = RentSegment(stream != 0 ? (int)mss : size);   // 스트림 모드는 항상 mss 크기로 빌린다(뒤에 이어붙이려고)
        seg.Append(data.Slice(0, size));
        data = data.Slice(size);

        seg.frg = stream == 0 ? (uint)(count - i - 1) : 0u;     // 조각 번호는 내림차순! 마지막이 0
        snd_queue.PushBack(seg);
    }
    return 0;
}
```

**`frg`가 내림차순인 이유**가 이 코드에서 가장 이해하기 헷갈리는 부분입니다. 3조각짜리 메시지라면
`frg`는 `2, 1, 0` 순서로 매겨집니다. 수신측이 `rcv_queue`의 맨 앞 세그먼트를 봤을 때 `frg == 0`이면
"이게 메시지의 마지막 조각"이라는 뜻이고, 그 앞까지 쌓인 조각 수를 세면 메시지가 다 도착했는지
알 수 있습니다(3.4.5절의 `PeekSize` 참고). 조각 수를 미리 헤더에 담지 않고 "카운트다운"으로
표현한 것이 원본 kcp의 설계입니다.

**`count >= rcv_wnd`에서 -2를 반환하는 이유**: 메시지 하나가 K개 조각으로 쪼개졌는데 상대의
수신 윈도가 K보다 작으면, 상대는 이 메시지의 조각들을 절대 동시에 `rcv_queue`에 다 채울 수 없어
**영구 교착**이 됩니다(뒤 조각이 오면 앞 조각이 밀려나는 게 아니라, 앞 조각이 확인되지 않으면
`rcv_nxt`가 전진하지 않아 뒤 조각을 아예 받아들이지 않습니다). 그래서 메시지 모드로 보낼 수 있는
최대 크기는 `(RcvWnd - 1) × Mss`로 제한됩니다.

#### 3.4.4 슬라이딩 윈도 — `Flush()`의 `snd_queue → snd_buf` 이동

`Send()`는 데이터를 `snd_queue`에 넣기만 합니다. 실제로 **윈도 안으로 들여보내는 일**은
`Flush()`가 매 틱마다 합니다.

```csharp
var cwnd_ = _imin_(snd_wnd, rmt_wnd);
if (0 == nocwnd) cwnd_ = _imin_(cwnd, cwnd_);      // 혼잡 윈도까지 반영

var newSegsCount = 0;
for (var k = 0; k < snd_queue.Count; k++)
{
    if (_itimediff(snd_nxt, snd_una + cwnd_) >= 0)  // "아직 확인 안 된 것 + 윈도" 를 넘으면 멈춘다
        break;

    var newseg = snd_queue.AtUnchecked(k);
    newseg.sn = snd_nxt;                            // 여기서 비로소 순서 번호가 매겨진다
    snd_buf.PushBack(newseg);
    snd_nxt++;
    newSegsCount++;
}
if (newSegsCount > 0) snd_queue.RemoveFront(newSegsCount);
```

**슬라이딩 윈도의 본질이 이 15줄에 다 있습니다.** "윈도"란 `[snd_una, snd_una + cwnd_)` 구간을
말합니다 — 아직 확인받지 못한 세그먼트가 이 구간을 넘으면 더 이상 새로 보내지 않고 ACK를
기다립니다. `snd_una`는 ACK가 들어올 때마다(`parse_una`) 전진하므로, 윈도는 확인이 쌓일수록
"미끄러지듯" 앞으로 나아갑니다.

세그먼트에 `sn`이 매겨지는 시점이 `Send()`가 아니라 **여기, 윈도에 들어가는 순간**이라는 점이
중요합니다. `snd_queue`에 쌓여 아직 안 보낸 데이터는 아직 순서 번호조차 없습니다.

#### 3.4.5 수신 경로 — `Input()` → `parse_data` → `rcv_buf` → `rcv_queue`

`Input()`은 패킷 하나(UDP 데이터그램 하나, 여러 세그먼트가 이어붙어 있을 수 있음)를 받아 루프를
돌며 세그먼트를 하나씩 해석합니다.

```csharp
public int Input(ReadOnlySpan<byte> data, bool regular, bool ackNoDelay)
{
    var current = clock();          // 핫루프 밖에서 시계를 1회만 읽는다(PF-01)

    while (true)
    {
        if (size - offset < IKCP_OVERHEAD) break;
        // ... 헤더 24바이트를 읽는다: conv, cmd, frg, wnd, ts, sn, una, length ...
        if (conv != conv_) return -1;                              // 다른 대화의 패킷은 통째로 버린다

        if (regular) rmt_wnd = wnd;                                 // 상대 윈도 갱신(FEC 복원 패킷은 신뢰 안 함)
        if (parse_una(una) > 0) windowSlides = true;                // 누적 ACK로 snd_buf 정리
        shrink_buf();                                                // snd_una 재계산

        if (cmd == IKCP_CMD_ACK)
        {
            parse_ack(sn);                                          // 이 sn 세그먼트에 확인 표시
            parse_fastack(sn, ts);                                  // 이보다 이른 세그먼트들의 fastack 카운트 증가
            flag |= 1; latest = ts;
        }
        else if (cmd == IKCP_CMD_PUSH)
        {
            if (_itimediff(sn, rcv_nxt) >= 0)
            {
                var seg = RentSegment((int)length);
                // ... 필드 채우고 페이로드 복사 ...
                if (parse_data(seg))               // true 면 중복
                    ReturnSegment(seg);
            }
        }
        else if (cmd == IKCP_CMD_WASK) { probe |= IKCP_ASK_TELL; }   // "네 윈도 크기 알려줘" → 다음 Flush 에서 WINS 응답
        else if (cmd == IKCP_CMD_WINS) { /* 그냥 로그만 */ }
        else return -3;                                              // 모르는 cmd

        offset += (int)length;
    }

    if (flag != 0 && regular)
        update_ack(_itimediff(current, latest));    // 가장 최근 ACK 로 RTT 갱신

    // ... 혼잡 윈도 갱신 ...
    if (windowSlides) Flush(false);
    else if (ackNoDelay && acklist.Count > 0) Flush(true);
    return 0;
}
```

`parse_data`가 실제로 큐에 세그먼트를 넣는 함수입니다.

```csharp
private bool parse_data(KcpSegment newseg)
{
    var sn = newseg.sn;
    if (_itimediff(sn, rcv_nxt + rcv_wnd) >= 0 || _itimediff(sn, rcv_nxt) < 0)
        return true;   // 윈도 밖이거나 이미 지나간 sn → 중복으로 취급

    // rcv_buf 는 sn 오름차순 정렬 상태를 유지한다. 뒤에서부터 훑어 삽입 위치를 찾는다.
    var repeat = false;
    var insert_idx = 0;
    for (var i = rcv_buf.Count - 1; i >= 0; i--)
    {
        var seg = rcv_buf.AtUnchecked(i);
        if (seg.sn == sn) { repeat = true; break; }
        if (_itimediff(sn, seg.sn) > 0) { insert_idx = i + 1; break; }
    }
    if (!repeat)
    {
        if (insert_idx == rcv_buf.Count) rcv_buf.PushBack(newseg);
        else rcv_buf.Insert(insert_idx, newseg);
    }

    MoveRcvBufToQueue();
    return repeat;
}
```

여기서 `rcv_buf`가 **정렬된 상태를 유지하도록 삽입 위치를 찾는 이유**는, UDP는 순서를 보장하지
않아 `sn=5`가 `sn=3`보다 먼저 도착할 수 있기 때문입니다. `rcv_buf`는 "도착했지만 아직 차례가
안 된" 세그먼트들의 대기실이고, 정렬 상태를 유지해야 `MoveRcvBufToQueue`가 "지금 몇 번부터
연속으로 찼는지"를 앞에서부터 순서대로 검사할 수 있습니다.

**`MoveRcvBufToQueue`는 이 라이브러리에서 실제로 버그가 있었다가 고쳐진 함수라 특히 자세히
볼 가치가 있습니다.**

```csharp
private void MoveRcvBufToQueue()
{
    var count = 0;
    for (var i = 0; i < rcv_buf.Count; i++)
    {
        var seg = rcv_buf.AtUnchecked(i);
        if (seg.sn == rcv_nxt && rcv_queue.Count + count < rcv_wnd)
        {
            rcv_nxt++;
            count++;
        }
        else break;
    }
    if (count > 0)
    {
        for (var i = 0; i < count; i++)
            rcv_queue.PushBack(rcv_buf.AtUnchecked(i));
        rcv_buf.RemoveFront(count);
    }
}
```

옮길 수 있는 개수(`count`)를 **루프로 다 센 다음에 한 번에 옮깁니다.** 왜 세는 도중에
`rcv_queue.PushBack`을 바로 하지 않을까요? 원래 이 코드는 그렇게 되어 있었습니다 —

```csharp
// 예전 코드(버그 있음) — 루프 안에서 즉시 옮기며 세는 방식
for (var i = 0; i < rcv_buf.Count; i++)
{
    if (seg.sn == rcv_nxt && rcv_queue.Count + count < rcv_wnd)   // ← rcv_queue.Count 를 매번 다시 읽는다
    {
        rcv_queue.PushBack(seg);   // rcv_queue.Count 가 여기서 이미 늘어난다
        ...
    }
}
```

`rcv_queue.Count + count`라는 한도 검사가 **의도한 것과 다르게 이중으로 카운트되는** 문제가
있었습니다. 루프 안에서 실제로 `PushBack`을 하면 `rcv_queue.Count` 자체가 늘어나는데, 거기에
`count`(이번 루프에서 옮긴 개수)까지 다시 더하니 한도를 실제보다 훨씬 일찍 넘긴 것으로 오판해
**실효 수신 윈도가 절반이 되는** 문제가 있었습니다. 예를 들어 `rcv_wnd = 128`인데 실제로는 64개
정도에서 멈추는 식입니다. kcp-go 원본은 "먼저 개수를 세고, 큐가 안 변하는 상태에서 세다가, 다
세고 난 뒤에 한 번에 옮기는" 방식이라 이 문제가 없습니다. 지금 코드(`MoveRcvBufToQueue`)는 그
방식을 정확히 따라, `rcv_queue.Count + count`를 계산하는 동안 `rcv_queue`가 바뀌지 않도록
했습니다 — **세는 것과 옮기는 것을 분리**한 것이 핵심입니다. `Recv`, `RecvInPlace`, `parse_data`
세 곳이 전부 이 하나의 함수를 공유하므로, 이제는 한 곳만 정확하면 전체가 정확합니다.

#### 3.4.6 메시지 조립 — `PeekSize()` / `Recv()`

```csharp
public int PeekSize()
{
    if (0 == rcv_queue.Count) return -1;
    ref var seq = ref rcv_queue.PeekFront();
    if (0 == seq.frg) return seq.Len;                    // 단일 조각이면 그냥 그 길이

    if (rcv_queue.Count < seq.frg + 1) return -1;         // 아직 조각이 덜 모였다

    var length = 0;
    for (var i = 0; i < rcv_queue.Count; i++)
    {
        var item = rcv_queue.AtUnchecked(i);
        length += item.Len;
        if (0 == item.frg) break;                          // frg==0 을 만나면 메시지 끝
    }
    return length;
}
```

`rcv_queue.Count < seq.frg + 1`이 "메시지가 아직 다 안 모였다"는 검사입니다. 맨 앞 조각의
`frg`가 예를 들어 2(3조각 메시지의 첫 조각)라면, 큐에 최소 3개가 쌓여야(`frg+1`) 메시지 전체를
계산할 수 있습니다.

`Recv(Span<byte> buffer)`는 `PeekSize()`로 크기를 확인한 뒤 조각들을 순서대로 이어 붙입니다
(코드는 3.4.7절에서 `RecvInPlace`와 비교하며 다시 봅니다).

#### 3.4.7 무복사 수신 — `RecvInPlace`

이 메서드는 성능 개선 작업(`docs/note/performance-improvements.md`의 G-2)에서 나중에 추가된
것이라 원본 kcp/kcp-go에는 없습니다. **문제의식**: 게임 메시지는 거의 항상 MTU 한 장(약
1,376바이트)에 들어가는 단일 조각(`frg == 0`)입니다. 그런데 기존 `Recv()`는 항상 "호출자 버퍼로
복사"하는 경로였습니다.

```csharp
public delegate void InPlaceMessageHandler(ReadOnlySpan<byte> message);

public int RecvInPlace(InPlaceMessageHandler handler)
{
    if (handler == null) throw new ArgumentNullException(nameof(handler));
    if (rcv_queue.Count == 0) return -1;

    var seg = rcv_queue.PeekFront();
    if (seg.frg != 0) return -3;              // 다조각: 무복사로 못 넘긴다. 호출자가 기존 Recv 경로로 폴백

    var fast_recover = rcv_queue.Count >= rcv_wnd;
    var length = seg.Len;

    rcv_queue.RemoveFront(1);                  // 큐에서 먼저 뗀다(핸들러 재진입에도 안전하도록)

    try
    {
        handler(seg.Payload);                  // ★ 세그먼트의 내부 버�퍼를 그대로 넘긴다. 복사 없음
    }
    finally
    {
        ReturnSegment(seg);                     // 핸들러가 예외를 던져도 반드시 풀에 돌려준다
    }

    MoveRcvBufToQueue();
    if (rcv_queue.Count < rcv_wnd && fast_recover) probe |= IKCP_ASK_TELL;
    return length;
}
```

**`handler`에 넘기는 `seg.Payload`는 `seg`가 대여한 `ArrayPool` 배열을 직접 가리키는 Span입니다.**
`ReturnSegment(seg)`가 `finally`에서 그 배열을 풀에 돌려주므로, **`handler`가 반환하고 나면
그 메모리는 다른 누군가에게 재대여될 수 있습니다.** 그래서 이 API의 규약은 "핸들러 밖으로 Span을
들고 나가면 안 된다"입니다 — 필요하면 핸들러 안에서 `ToArray()` 등으로 복사해야 합니다.

반환값 `-3`이 이 API의 핵심 설계입니다. 다조각 메시지를 만나면 **큐를 전혀 건드리지 않고** -3을
돌려주므로, 호출자(`KcpSession.DrainReliable`, 4.5절)는 이때만 기존 `PeekSize`+`Recv` 조립
경로로 넘어갑니다. 정상적인 게임 메시지(단일 조각)는 항상 무복사 경로를 타고, 드물게 큰
메시지만 복사 경로를 탑니다.

#### 3.4.8 ARQ — 재전송 3경로와 RTO 계산

`Flush(bool ackOnly)`가 재전송 로직이 있는 곳입니다. `snd_buf`의 각 세그먼트를 순회하며 "지금
보내야 하는가"를 판단합니다.

```csharp
// 순회 중 큐를 안 건드리므로 스냅숏 뷰 사용(원소당 필드 재로드 제거)
var sndBufView = snd_buf.CreateView();
var sndBufCount = snd_buf.Count;
for (var k = 0; k < sndBufCount; k++)
{
    var segment = sndBufView[k];
    var needsend = false;
    if (segment.acked == 1)
        continue;                                                    // 이미 확인됨 — snd_una 가 곧 앞에서부터 지워 줄 것이다

    if (segment.xmit == 0)  // ① 최초 전송
    {
        needsend = true;
        segment.rto = rx_rto;
        segment.resendts = current + segment.rto;
    }
    else if (segment.fastack >= resent) // ② 빠른 재전송(fast retransmit)
    {
        needsend = true;
        segment.fastack = 0;
        segment.rto = rx_rto;
        segment.resendts = current + segment.rto;
        change++;                                                     // ★ 혼잡 윈도 절반 감소(rate halving) 신호
        fastRetransSegs++;
    }
    else if (segment.fastack > 0 && newSegsCount == 0) // ③ 조기 재전송(early retransmit)
    {
        needsend = true;
        segment.fastack = 0;
        segment.resendts = current + segment.rto;
        change++;
        earlyRetransSegs++;
    }
    else if (_itimediff(current, segment.resendts) >= 0) // ④ RTO 타임아웃
    {
        needsend = true;
        if (nodelay == 0)
            segment.rto += rx_rto;
        else
            segment.rto += rx_rto / 2;                                 // nodelay 모드는 백오프를 절반만
        segment.fastack = 0;
        segment.resendts = current + segment.rto;
        lostSegs++;
    }

    if (needsend)
    {
        segment.xmit++;
        // ... 헤더+페이로드를 buffer 에 인코딩 ...
        if (segment.xmit >= dead_link) state = IKCP_STATE_DEAD_LINK;   // 진단 B-03
    }
}
```

네 가지 경로(①최초, ②빠른, ③조기, ④타임아웃)가 우선순위 순서로 검사됩니다. **빠른 재전송**은
TCP의 "중복 ACK 3개면 재전송"과 같은 개념입니다 — `fastack`은 `parse_fastack`이 채우는데, 이
세그먼트보다 나중에 보낸 세그먼트가 먼저 확인되면(즉 이 세그먼트를 건너뛴 ACK가 왔다는 뜻)
카운트가 올라갑니다. 카운트가 `resend`(설정값, 보통 2) 이상이면 타임아웃을 기다리지 않고 즉시
재전송합니다 — RTT를 기다리지 않으니 훨씬 빠릅니다.

**조기 재전송**은 kcp 고유의 확장입니다. `fastack > 0`(즉 손실 의심 신호가 있음)인데 "이번
플러시에서 새로 보낼 세그먼트가 없다"(`newSegsCount == 0`, 곧 파이프라인이 유휴 상태)면, 굳이
`resend` 임계값을 다 채우지 않아도 재전송합니다. 어차피 이번 틱에 다른 걸 안 보낼 거라면
의심스러운 세그먼트를 먼저 밀어 넣어 지연을 줄이자는 발상입니다.

**RTO 계산**(`update_ack`)은 RFC 6298의 변형입니다.

```csharp
private void update_ack(int rtt)
{
    if (0 == rx_srtt)
    {
        rx_srtt = rtt;
        rx_rttval = rtt >> 1;
    }
    else
    {
        int delta = rtt - rx_srtt;
        rx_srtt += delta >> 3;                       // srtt += (rtt - srtt) / 8
        if (delta < 0) delta = -delta;

        if (rtt < rx_srtt - rx_rttval)
            rx_rttval += (delta - rx_rttval) >> 5;    // 예상보다 훨씬 빠른 샘플 → 가중치 8배 낮춤(1/32)
        else
            rx_rttval += (delta - rx_rttval) >> 2;    // 일반적인 경우 → rttval += (delta - rttval) / 4
    }

    uint rto = (uint)rx_srtt + _imax_(interval, (uint)rx_rttval << 2);
    rx_rto = _ibound_(rx_minrto, rto, IKCP_RTO_MAX);
}
```

`rto = srtt + max(interval, 4 × rttval)`이 최종 공식입니다. `rx_minrto`가 게임에서 중요한
튜닝 지점입니다 — `NoDelay(1, ...)`(nodelay 모드)를 켜면 최소 RTO가 **30ms**(`IKCP_RTO_NDL`)까지
내려가지만, 끄면 TCP와 같은 **100ms**(`IKCP_RTO_MIN`)입니다. 게임에서 거의 항상 nodelay를
켜는 이유가 이겁니다.

#### 3.4.9 혼잡 제어

`nocwnd == 0`(기본은 1, 즉 꺼짐)일 때만 동작합니다. 게임 서버는 대역폭보다 지연이 중요해서
대부분 **혼잡 제어를 끄고** 씁니다(`KcpSessionOptions.NoCongestionControl = 1`이 기본값).
켰을 때의 동작은 두 갈래입니다.

```csharp
// Input() 안 — 패킷이 정상 도착했을 때(RFC 5681 스타일 증가)
if (_itimediff(snd_una, s_una) > 0)      // 뭔가 새로 확인됐다
{
    if (cwnd < ssthresh) { cwnd++; incr += mss; }                     // 느린 시작(slow start): 매 ACK 마다 +1
    else { incr += mss * mss / incr + mss / 16; /* cwnd 를 incr 기준으로 재계산 */ }  // 혼잡 회피: 선형 증가
}

// Flush() 안 — 손실이 감지됐을 때(RFC 6937 rate halving + RFC 5681 감소)
if (change > 0)             // 빠른/조기 재전송이 있었다 → 절반으로
{
    ssthresh = (snd_nxt - snd_una) / 2;
    cwnd = ssthresh + resent;
}
if (lostSegs > 0)           // RTO 타임아웃이 있었다 → 훨씬 공격적으로 줄인다
{
    ssthresh = cwnd / 2;
    cwnd = 1;
}
```

`change`(빠른/조기 재전송)와 `lostSegs`(RTO 타임아웃)를 **다르게 취급**하는 것이 요점입니다.
빠른 재전송은 "패킷 하나가 새치기당했을 뿐 파이프라인은 살아있다"는 신호라 절반만 줄이고
(RFC 6937 rate halving), RTO 타임아웃은 "한동안 아무 응답이 없었다"는 훨씬 심각한 신호라
`cwnd = 1`로 급격히 줄입니다(RFC 5681).

#### 3.4.10 `Update()` / `Check()` — 언제 얼마나 자주 돌려야 하나

```csharp
public void Update()
{
    var current = clock();
    if (0 == updated) { updated = 1; ts_flush = current; }

    var slap = _itimediff(current, ts_flush);
    if (slap >= 10000 || slap < -10000) { ts_flush = current; slap = 0; }  // 시계가 크게 튀면 재동기화

    if (slap >= 0)
    {
        ts_flush += interval;
        if (_itimediff(current, ts_flush) >= 0) ts_flush = current + interval;
        Flush(false);
    }
}
```

`Update()`는 `interval`(기본 100ms, 게임은 보통 10ms로 낮춤)마다 실제로 `Flush`를 부르도록
스스로 페이싱합니다. 매 호출마다 `Flush`를 부르는 게 아니라 **아직 간격이 안 됐으면 아무것도
안 합니다** — 그래서 `Update()`를 아주 자주 불러도(예: 게임 루프 매 프레임) 낭비가 크지 않습니다.

`Check()`는 "다음에 `Update`를 부르면 좋은 시각"을 알려 줍니다. `snd_buf`를 순회해 가장 이른
`resendts`(재전송 예정 시각)를 찾고, `ts_flush`(다음 정기 플러시 시각)와 비교해 더 이른 쪽을
반환합니다. 이 값을 상위 계층(`KcpSession.Update`, `SessionScheduler`)이 타이머 스케줄링에
씁니다 — **매 세션을 매 틱 깨우는 대신, 각 세션이 "다음에 언제 깨워 줘"를 스스로 계산**하게
해서 대량 세션에서 불필요한 순회를 없앱니다(4.8절 `SessionScheduler` 참고).

두 함수 모두 시각 비교에 반드시 `_itimediff` (부호 있는 뺄셈)를 씁니다. `uint` 시각은 약 49.7일
마다 랩어라운드하는데, `current >= ts_flush` 같은 단순 크기 비교는 랩어라운드 순간
`0xFFFFFFFF >= 0x00000001`처럼 항상 참/거짓이 뒤집혀 갱신이 영원히 멈출 수 있습니다(원 진단
B-02). `(int)(a - b)`는 오버플로를 이용해 이 경우에도 올바른 부호를 돌려줍니다.

#### 3.4.11 `reserved` — 상위 계층을 위한 예약 영역

`Kcp`는 `ReserveBytes(int)`로 **플러시 버퍼 앞부분을 상위 계층에 내줄** 수 있습니다.

```csharp
public bool ReserveBytes(int reservedSize)
{
    if (reservedSize >= mtu - IKCP_OVERHEAD || reservedSize < 0) return false;
    reserved = reservedSize;
    mss = mtu - IKCP_OVERHEAD - (uint)reservedSize;   // mss 가 그만큼 줄어든다
    return true;
}
```

`makeSpace`/`flushBuffer`가 `buffer`를 채울 때 항상 `reserved` 오프셋부터 씁니다.

```csharp
var writeIndex = reserved;   // 0 이 아니라 reserved 부터 시작
// ... 세그먼트들을 인코딩 ...
output(buffer, writeIndex);  // reserved 앞부분은 그대로 남아 있다
```

`KcpSession`(4.5절)이 `CreateKcp`에서 `instance.ReserveBytes(reservedBytes)`를 부를 때
`reservedBytes`는 "프레임 타입 1바이트(+FEC를 쓰면 FEC 데이터 헤더 8바이트: seqid 4 + flag 2 +
size 2)"입니다. 그러면 `Kcp`가
채운 `buffer`의 **맨 앞 몇 바이트가 항상 비어 있는 채로** `output` 콜백(`KcpSession.OnKcpOutput`)에
전달되고, 거기서 `buffer[0] = (byte)FrameType.Kcp`처럼 **복사 없이 그 자리에 프레임 타입만
채워 넣고** 그대로 내보낼 수 있습니다. 이게 프레이밍 계층이 KCP 패킷을 감싸면서도 별도의 복사를
하지 않는 트릭입니다.

#### 3.4.12 로깅 — 비활성 시 완전 무비용

```csharp
private bool ikcp_canlog(int mask) => (mask & logmask) != 0 && writelog != null;

if (ikcp_canlog(IKCP_LOG_OUT_DATA))
{
    ikcp_log($"output psh: sn={segment.sn.ToString()} ...");   // 문자열 보간은 이 블록 안에서만 평가된다
}
```

`if (ikcp_canlog(...))`로 감싼 이유는 **문자열 보간(`$"..."`) 자체가 할당**이기 때문입니다.
로깅이 꺼져 있으면(`logmask`가 해당 비트를 안 갖고 있거나 `writelog`가 null) 이 블록에 아예
진입하지 않으므로 문자열이 만들어지지 않습니다. `ikcp_canlog`를 호출하는 비용(정수 AND
+ null 체크)만 남고, 로그 자체의 비용은 0입니다.

### 3.5 `KcpClock.cs` — 단조 시계

```csharp
public static class KcpClock
{
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    public static uint NowMs => (uint)(long)((Stopwatch.GetTimestamp() - StartTimestamp) * TicksToMs);
}
```

`DateTime.Now`/`DateTime.UtcNow`를 안 쓰는 이유가 클래스 주석에 명시되어 있습니다: **NTP
동기화, 수동 시각 변경, 서머타임 전환에서 시계가 뒤로 흐를 수 있습니다.** KCP는 32비트 밀리초
타임스탬프로 RTO·RTT를 계산하는데, 시계가 뒤로 튀면 계산된 RTO가 음수나 비정상적으로 커져
재전송 폭풍이나 연결 정지로 이어집니다(진단 B-01).

`Stopwatch`(내부적으로 Windows의 `QueryPerformanceCounter`, Linux의 `clock_gettime(CLOCK_MONOTONIC)`)는
**단조(monotonic)**입니다 — 시스템 시각이 바뀌어도 절대 뒤로 가지 않습니다. `NowMs`는 **프로세스
시작 시점을 0으로 하는 상대 시각**이고, `uint`라 약 49.7일마다 랩어라운드합니다. 이건 버그가
아니라 설계입니다 — 모든 시각 비교가 `Kcp.Itimediff`(부호 있는 뺄셈)를 쓰도록 강제해서,
랩어라운드가 실제로 일어나도 정상 동작하게 만들었습니다.

### 3.6 `KcpStats.cs` / `PooledBuffer.cs` / `UDPSession.cs`

**`KcpStats`**는 단순한 값 타입(`struct`)입니다. `BytesSent`, `RetransmitSegments`,
`FastRetransmitSegments` 등을 누적하고, `Kcp.GetStats()`가 호출 시점의 스냅숏을 복사해
돌려줍니다. `KcpServer`의 관측성 기능(4.13절 `ServerMetrics`)이 이 값들로 재전송률·RTT
백분위를 계산합니다.

**`PooledBuffer`**는 `ArrayPool` 기반의 성장 가능한 읽기/쓰기 버퍼입니다. 지금은 레거시
`UDPSession`(아래)에서만 쓰이고, 신규 코드(전송 계층)에서는 쓰이지 않습니다. `EnsureWritable`이
"이미 읽은 앞부분을 당겨서 재사용 → 그래도 부족하면 확장"의 순서로 동작합니다.

**`UDPSession`**은 `[Obsolete]`가 붙은 레거시 클래스입니다. 핸드셰이크가 없어 `conv`를 클라이언트가
`new Random().Next(...)`로 즉석에서 정하는데, 서버와 사전에 합의할 방법이 없어 실사용이
불가능한 설계였습니다(진단 B-11). 지금은 `KcpCSharpR1.Transport.KcpClient`(4.7절)로 완전히
대체되었고, 기존 사용자 호환을 위해 한 메이저 버전만 유지됩니다. 코드를 읽을 때는 **참고하지
말고 건너뛰어도 됩니다** — `[ExcludeFromCodeCoverage]`가 붙어 테스트 게이트에서도 제외되어
있습니다.

---

## 4. 전송 계층 — `src/KcpCSharpR1.Transport`

### 4.1 `Framing.cs` — 프레임 8종

전송 계층의 모든 UDP 페이로드는 **첫 1바이트가 프레임 타입**입니다.

```csharp
public enum FrameType : byte
{
    Syn = 0x01, SynCookie = 0x02, SynAck = 0x03,
    Kcp = 0x04, Raw = 0x05,
    Ping = 0x06, Pong = 0x07, Fin = 0x08,
}
```

| 타입 | 방향 | 와이어 포맷 | 언제 |
|---|---|---|---|
| `SYN` | 클라→서버 | `[0x01][ver u8][nonce u32][cookie u32]` (10B) | 연결 요청 |
| `SYN_COOKIE` | 서버→클라 | `[0x02][ver u8][nonce u32][cookie u32]` (10B) | 쿠키 발급(세션 없음) |
| `SYN_ACK` | 서버→클라 | `[0x03][conv u32][nonce u32]` (9B) | 연결 수락(conv 할당) |
| `KCP` | 양방향 | `[0x04][KCP 원본 패킷...]` | 신뢰 채널 데이터 |
| `RAW` | 양방향 | `[0x05][conv u32][페이로드...]` | 비신뢰 채널 데이터 |
| `PING` | 양방향 | `[0x06][conv u32][ts u32]` (9B) | keepalive 요청 |
| `PONG` | 양방향 | `[0x07][conv u32][ts u32]` (9B) | keepalive 응답(ts 그대로 에코) |
| `FIN` | 양방향 | `[0x08][conv u32][reason u8]` (6B) | 정상 종료 통지 |

이 클래스는 **소켓을 전혀 모르는 순수 정적 함수**로만 이루어져 있습니다(`WriteSyn`, `TryReadSyn`
같은 인코드/디코드 쌍). 그 덕분에 `tests/KcpCSharpR1.Tests/FramingTests.cs`가 소켓 없이
바이트 왕복만으로 와이어 포맷을 못 박을 수 있습니다. 대표로 하나만 보면 나머지는 패턴이 같습니다.

```csharp
public static int WriteSyn(Span<byte> destination, FrameType type, uint clientNonce, uint cookie)
{
    destination[0] = (byte)type;
    destination[1] = ProtocolVersion;
    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(2), clientNonce);
    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(6), cookie);
    return SynSize;   // 10
}

public static bool TryReadSyn(ReadOnlySpan<byte> frame, out byte version, out uint clientNonce, out uint cookie)
{
    version = 0; clientNonce = 0; cookie = 0;
    if (frame.Length < SynSize) return false;   // 짧은 프레임(위조·손상)은 조용히 거부

    version = frame[1];
    clientNonce = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(2));
    cookie = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(6));
    return true;
}
```

`TryRead*` 계열이 전부 **길이 부족 시 `false`를 돌려주고 예외를 던지지 않는** 이유는, UDP
페이로드는 누구든 아무 바이트나 보낼 수 있어 이 계층이 사실상 첫 번째 방어선이기 때문입니다.
`KcpSession.OnFrame`(4.5절)은 `false`가 오면 그 프레임을 그냥 무시합니다.

`RawMode`(코어를 감싸지 않고 순수 KCP 패킷만 주고받는 상호운용 모드, [부록 C](book/appendix-c-interop.md))는
이 프레이밍 계층 자체를 건너뜁니다 — `KcpSession.OnFrame`에서 `options.RawMode`가 true면
`Framing.PeekType` 분기를 타지 않고 곧바로 `kcp.Input()`을 부릅니다.

### 4.2 `AddressKey.cs` — 값 타입 주소

`KcpServer`는 세션 수천 개를 `IP+포트`로 조회해야 합니다. `IPEndPoint`를 딕셔너리 키로 쓰면
**패킷마다 새 `IPEndPoint` 객체**가 필요해 할당이 생깁니다. `AddressKey`는 IP+포트를
`readonly struct`(값 타입, 16바이트 주소를 `ulong` 2개로 압축)로 표현해 이 문제를 없앱니다.

```csharp
public readonly struct AddressKey : IEquatable<AddressKey>
{
    private readonly ulong hi;      // IPv6 16바이트를 8+8 로 나눈 상위
    private readonly ulong lo;      // 하위 (IPv4 는 IPv4-매핑 IPv6 형태로 정규화해서 담는다: ::ffff:a.b.c.d)
    private readonly uint scopeId;  // IPv6 링크 로컬 스코프
    private readonly ushort port;
```

**IPv4를 IPv4-매핑 IPv6로 정규화**하는 이유는 키 비교·해시를 하나의 표현으로 통일하기
위해서입니다. `FromIPv4`/`FromIPv6`는 소켓 수신 경로에서 바이트를 직접 읽어 `IPAddress` 객체를
거치지 않고 키를 만듭니다(무할당 경로).

```csharp
public static AddressKey FromIPv4(ReadOnlySpan<byte> v4Bytes, int port)
{
    Span<byte> bytes = stackalloc byte[16];
    bytes[10] = 0xFF; bytes[11] = 0xFF;              // ::ffff: 프리픽스
    v4Bytes.CopyTo(bytes.Slice(12));
    var hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);
    var lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8));
    return new AddressKey(hi, lo, 0, (ushort)port);
}
```

`ToEndPoint()`는 **반대 방향**(키 → `IPEndPoint`)으로, 세션을 새로 만들 때 딱 한 번만
부릅니다 — 세션은 그 `IPEndPoint` 인스턴스를 계속 재사용하므로(4.3절의 `SendTo` 캐싱과
맞물립니다), 이후로는 할당이 다시 생기지 않습니다.

`GetHashCode`는 상위/하위 64비트를 XOR한 뒤 시프트-XOR로 섞고, 포트를 곱해 넣습니다 —
표준적인 해시 결합 패턴입니다.

`WriteAddressBytes`/`ScopeId`/`IsIPv4`는 성능 개선 작업(G-1)에서 추가된 멤버로,
`UdpSocketTransport`(4.3절)가 `SocketAddress` 버퍼에 직접 주소를 써 넣을 때 씁니다.

### 4.3 `IUdpTransport.cs` / `UdpSocketTransport.cs` — 소켓 계층

**`IUdpTransport`**는 소켓을 추상화한 인터페이스입니다. `KcpServer`/`KcpClient`는 이
인터페이스만 알고 실소켓을 직접 만지지 않습니다 — 덕분에 테스트에서 손실·지연을 흉내 내는
가상 구현(`tests/KcpCSharpR1.SimTests/VirtualNetwork.cs`)을 그대로 끼워 넣어 핸드셰이크·
세션 수명을 실제 네트워크 없이, 결정적으로 검증할 수 있습니다.

```csharp
public interface IUdpTransport : IDisposable
{
    IPEndPoint? LocalEndPoint { get; }
    void Bind(IPEndPoint localEndPoint);

    int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote);
    int SendTo(ReadOnlySpan<byte> data, AddressKey remote) => SendTo(data, remote.ToEndPoint());   // 기본 구현

    bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote);
    bool TryReceive(byte[] buffer, out int received, out AddressKey remote)
        => TryReceive(buffer.AsSpan(), out received, out remote);   // 기본 구현

    bool WaitForData(int microseconds);
}
```

**C# 8+의 인터페이스 기본 구현**을 활용해, `SendTo(AddressKey)`와 `TryReceive(byte[])`
오버로드를 인터페이스 자체에 기본 동작으로 넣어 뒀습니다. 이러면 `IUdpTransport`를 직접 구현한
기존 코드(예: 테스트의 가상 전송)가 **재컴파일만으로 자동으로 새 오버로드를 갖게 되고**, 성능이
중요한 구현(`UdpSocketTransport`)만 선택적으로 재정의해 진짜 이득을 봅니다. 공개 API를 깨지
않고 추가하는 전형적인 패턴입니다.

**`UdpSocketTransport`**가 실제 소켓 구현입니다. 이 파일은 무할당 송수신을 위해 net8.0과
netstandard2.1 경로가 `#if NET8_0_OR_GREATER`로 갈립니다.

**수신 — net8.0**: `Socket.ReceiveFrom(Span<byte>, SocketFlags, SocketAddress)` 오버로드를
씁니다. 매 호출마다 새 `EndPoint`를 만드는 옛 오버로드와 달리, **재사용 가능한 `SocketAddress`
버퍼**에 결과를 씁니다.

```csharp
private readonly SocketAddress receiveAddress;   // 필드 — 생성 시 1회만 만든다

public bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)
{
    var n = socket.ReceiveFrom(buffer, SocketFlags.None, receiveAddress);   // receiveAddress 를 매번 재사용
    received = n;
    remote = ToKey(receiveAddress);      // SocketAddress 버퍼를 직접 읽어 AddressKey 로 변환(할당 없음)
    return true;
}

private static AddressKey ToKey(SocketAddress address)
{
    var span = address.Buffer.Span;                       // SocketAddress 내부 버퍼를 Span 으로 직접 본다
    var port = (ushort)((span[2] << 8) | span[3]);         // sockaddr_in 의 포트는 빅엔디언 2바이트
    if (address.Family == AddressFamily.InterNetwork)
        return AddressKey.FromIPv4(span.Slice(4, 4), port);
    // ... IPv6 분기 ...
}
```

`ToKey`가 `SocketAddress → IPEndPoint`로 변환하지 않고 **버퍼를 직접 파싱**하는 이유는,
`IPEndPoint`를 만드는 것 자체가 `IPAddress` 객체 할당을 수반하기 때문입니다. BSD 소켓 API의
`sockaddr_in` 구조체 레이아웃(`[family 2][port BE 2][addr 4][zero 8]`)을 그대로 읽어
`AddressKey`(값 타입)로 직행합니다.

**송신 — net8.0, 진단 G-1**: `Socket.SendTo(ReadOnlySpan<byte>, SocketFlags, EndPoint)`는
호출마다 내부에서 `EndPoint.Serialize()`로 `SocketAddress`를 새로 만듭니다. 세션의 상대 주소는
불변이므로, `ConditionalWeakTable`로 `IPEndPoint` 인스턴스 하나당 직렬화 결과를 캐시합니다.

```csharp
private readonly ConditionalWeakTable<IPEndPoint, SocketAddress> sendAddressCache = new();

public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
{
    var address = sendAddressCache.GetValue(remote, static ep => ep.Serialize());   // 인스턴스당 정확히 1회 직렬화
    return socket.SendTo(data, SocketFlags.None, address);
}
```

`ConditionalWeakTable`을 쓴 이유는 **수명 관리가 저절로 되기 때문**입니다 — 세션이 사라져
`IPEndPoint`에 대한 다른 참조가 없어지면, 캐시 항목도 GC가 알아서 회수합니다. 명시적으로
`Remove`를 호출할 코드가 필요 없습니다. 단, **이 캐시가 맞으려면 세션이 같은 `IPEndPoint`
인스턴스를 계속 재사용해야 합니다** — `IUdpTransport.SendTo`의 XML 문서 주석이 이 전제를
명시하는 이유입니다.

세션이 아직 없는 일회성 송신(핸드셰이크 쿠키 응답 등)을 위해 `SendTo(ReadOnlySpan<byte>,
AddressKey)`도 재정의합니다. 이건 `ConditionalWeakTable` 캐시가 안 맞는 경로(주소 키는
값 타입이라 캐시 키로 못 씀)라, **재사용 버퍼에 직접 `sockaddr` 바이트를 써 넣는** 방식으로
IPEndPoint·IPAddress 할당 자체를 없앱니다.

```csharp
private bool TryWriteKey(AddressKey key, SocketAddress destination)
{
    var span = destination.Buffer.Span;
    Span<byte> bytes = stackalloc byte[16];
    key.WriteAddressBytes(bytes);

    if (family == AddressFamily.InterNetwork)
    {
        // sockaddr_in: [family 2][port BE 2][addr 4][zero 8]
        span[2] = (byte)(key.Port >> 8);
        span[3] = (byte)key.Port;
        bytes.Slice(12, 4).CopyTo(span.Slice(4));   // IPv4-매핑 부분만 뽑아 addr 필드에
        return true;
    }
    // ... sockaddr_in6 분기 ...
}
```

**Windows `SIO_UDP_CONNRESET`**: 생성자에서 이 IOCTL을 꺼 둡니다. 끄지 않으면 상대가
사라져 ICMP Port Unreachable이 돌아왔을 때 **다음 `ReceiveFrom` 호출이 `SocketException`으로
터집니다** — UDP는 원래 연결이 없어 이런 예외가 나면 안 되는데, Windows의 구현 특유의 동작입니다.
이 설정을 빼먹으면 클라이언트 하나가 갑자기 죽을 때마다 서버 수신 루프 전체가 죽습니다.

**netstandard2.1 폴백**: Span 오버로드가 없는 구형 타깃에서는 `receiveScratch`(내부 스크래치
배열)로 받은 뒤 호출자 버퍼로 복사합니다. 다만 `TryReceive(byte[], ...)` 오버로드(진단 C-1)는
호출자 버퍼가 배열이라는 걸 알고 있을 때 이 복사조차 없앱니다 — `socket.ReceiveFrom(buffer, ...)`로
바로 받습니다.

### 4.4 `HandshakeCookie.cs` — 무상태 SYN 쿠키

서버는 SYN을 받아도 **곧바로 세션을 만들지 않습니다.** 대신 쿠키를 계산해 돌려주고 그 요청은
잊습니다.

```csharp
public uint Compute(AddressKey remote, long unixSeconds)
    => ComputeForBucket(remote, unixSeconds / BucketSeconds);   // BucketSeconds = 30

private uint ComputeForBucket(AddressKey remote, long bucket)
{
    Span<byte> message = stackalloc byte[AddressKey.SerializedSize + sizeof(long)];
    remote.WriteTo(message);                                     // 클라 IP + 포트
    BinaryPrimitives.WriteInt64LittleEndian(message.Slice(AddressKey.SerializedSize), bucket);

    Span<byte> hash = stackalloc byte[32];
    HMACSHA256.HashData(secret, message, hash);                   // cookie = HMAC-SHA256(secret, IP‖포트‖시간버킷)

    var cookie = BinaryPrimitives.ReadUInt32LittleEndian(hash);
    return cookie == 0 ? 1u : cookie;                              // 0 은 "쿠키 없음"으로 예약되어 있어 피한다
}
```

**공격 시나리오**: 공격자가 소스 IP를 위조한 SYN을 대량으로 보내 서버 세션 테이블을 채우려 하면
(SYN flood), 서버는 위조된 주소로 쿠키를 "보낼 뿐" 세션을 만들지 않습니다. 공격자는 위조한
주소로 온 응답(쿠키)을 **받아 볼 수 없으므로**(자기 것이 아닌 IP니까), 그 쿠키를 담아 SYN을
다시 보낼 수 없습니다. 진짜 클라이언트만 쿠키를 받아 그걸 담아 재전송하고, **그때 비로소
세션이 만들어집니다**(`KcpServer.HandleSyn`, 4.6절).

**시간 버킷**: 쿠키는 30초 단위로 값이 바뀝니다. 검증 시 **현재 버킷과 직전 버킷 두 개를
허용**해서, 30초 경계에서 클라이언트의 재전송이 어긋나 거부당하는 일을 막습니다 — 결과적으로
쿠키의 실제 유효 기간은 30~60초입니다.

```csharp
public bool Validate(AddressKey remote, uint cookie, long unixSeconds)
{
    var bucket = unixSeconds / BucketSeconds;
    return ComputeForBucket(remote, bucket) == cookie || ComputeForBucket(remote, bucket - 1) == cookie;
}
```

비밀키(`secret`)는 서버 기동 시 `RandomNumberGenerator`로 32바이트를 만들거나, 여러 서버가
쿠키를 공유해야 하는 배포(로드밸런서 뒤 여러 인스턴스)를 위해 생성자로 직접 줄 수 있습니다.
`Dispose()`는 `Array.Clear`로 키를 메모리에서 지웁니다.

### 4.5 `KcpSession.cs` — 세션 상태 머신

**전송 계층에서 가장 복잡한 파일**입니다. `Kcp` 인스턴스 하나를 소유하고, 그 위에 핸드셰이크·
keepalive·정상 종료·(선택)암호화·(선택)FEC를 얹습니다. **소켓을 모릅니다** — 송신은 주입된
`PacketSender` 델리게이트가, 시각은 주입된 시계가 담당해서, 가상 시간 단위 테스트로 상태
전이표 전체를 실제 네트워크 없이 검증할 수 있습니다.

#### 4.5.1 상태 전이도

```
        StartConnect()                    OnSynAck() 성공
Closed ─────────────────► Connecting ─────────────────────► Connected
  ▲                            │                                │  │
  │        HandshakeTimeout/   │                                │  │ Disconnect()
  │        MaxRetries 초과     │                                │  ▼
  └────────────────────────────┘                          Closing ─┐
  ▲                                                              │  │ FinLingerMs 경과
  │                    타임아웃/데드링크                          │  │
  └─────────────────────────── Dead ◄───────────────────────────┘  │
  ▲                                                                 │
  └─────────────────────────────────────────────────────────────────┘
                          (FIN 수신 즉시, 또는 RawMode 아닌 Disconnect)
```

`RawMode`는 예외입니다 — 생성자에서 곧바로 `Connected`로 시작합니다(핸드셰이크·PING·FIN이
아예 없는 kcp-go 상호운용 모드이므로).

#### 4.5.2 필드 구성 — "무엇을 위해 이렇게 많은 캐시 필드가 있나"

`KcpSession`의 필드 절반은 **성능 개선 작업에서 추가된 캐시/버퍼**입니다. 처음 보면 왜 이렇게
많은지 의아할 수 있어 정리합니다.

| 필드 | 목적 | 관련 |
|---|---|---|
| `decryptBuffer` | 암호화 세션의 상주 복호화 버퍼(패킷마다 `ArrayPool.Rent`하지 않음) | G-3 |
| `assemblyBuffer` | 다조각 메시지 조립용 상주 버퍼(필요 시 2배씩 성장) | G-2 |
| `unreliableScratch` | `UnreliableReceived`(Memory 이벤트) 구독자를 위한 상주 복사 버퍼 | G-2 |
| `inPlaceCallback` | `Kcp.RecvInPlace`에 넘길 델리게이트를 세션당 1개만 만들어 재사용 | G-2 |
| `draining` | 드레인 재진입 가드(아래 4.5.5절에서 상세) | G-2 |
| `fecOutputCallback` / `fecRecoveredCallback` | FEC 인/디코더 콜백을 세션당 1개만 만듦(패킷마다 클로저 생성 방지) | G-4 |
| `outbox` | 게임 스레드용 송신 아웃박스(`Channel`) | M-1 |

이 캐시들의 공통 목적은 하나입니다 — **"패킷마다 새로 만들지 않는다."** 델리게이트, 버퍼, 콜백을
세션 생성 시 1회만 만들어 필드에 고정해 두면, 정상 상태 송수신에서 할당이 사라집니다.

#### 4.5.3 핸드셰이크

클라이언트가 `StartConnect`를 부르면 SYN을 보내고 재시도 타이머를 겁니다.

```csharp
public void StartConnect(uint now)
{
    clientNonce = NewNonce();       // RandomNumberGenerator 로 4바이트 난수(응답 대조용)
    cookie = 0;
    state = KcpSessionState.Connecting;
    SendSyn(now);
}

private void SendSyn(uint now)
{
    handshakeAttempts++;
    nextHandshakeAt = now + (uint)options.HandshakeRetryIntervalMs;
    var len = Framing.WriteSyn(frameBuffer, FrameType.Syn, clientNonce, cookie);
    sender(frameBuffer.AsSpan(0, len), RemoteEndPoint);
}
```

서버가 `SYN_COOKIE`로 응답하면(`OnSynCookie`) 쿠키를 받아 **즉시 SYN을 다시 보냅니다**(재시도
횟수는 복구): 이번엔 쿠키가 채워져 있으니 서버가 검증을 통과시키고 세션을 만들어 `SYN_ACK`를
돌려줍니다. `OnSynAck`가 이를 받아 `conv`를 확정하고 `CreateKcp`로 실제 `Kcp` 인스턴스를
만듭니다.

```csharp
private void OnSynAck(ReadOnlySpan<byte> frame, uint now)
{
    if (!Framing.TryReadSynAck(frame, out var assignedConv, out _)) return;
    conv = assignedConv;
    CreateKcp(assignedConv);
    state = KcpSessionState.Connected;
    Connected?.Invoke(this);
}
```

`CreateKcp`가 암호화·FEC까지 조립하는 지점입니다.

```csharp
private void CreateKcp(uint conversation)
{
    cipher = options.Cipher;
    if (cipher == null && options.EncryptionKey != null)
    {
        cipher = new AesGcmPacketCipher(options.EncryptionKey, conversation, serverDirection: !isClient);
    }

    if (cipher != null && decryptBuffer == null)
        decryptBuffer = new byte[frameBuffer.Length];

    if (options.Fec != null && !options.RawMode)
    {
        var maxShardSize = options.Mtu - Framing.KcpHeaderSize - FecFrame.HeaderSize;
        fecEncoder = new FecEncoder(options.Fec, maxShardSize, Framing.KcpHeaderSize);
        fecDecoder = new FecDecoder(options.Fec, maxShardSize);
    }

    var instance = new Kcp(conversation, OnKcpOutput, clock);
    instance.SetMtu(options.Mtu - (cipher?.Overhead ?? 0));   // 암호화 오버헤드만큼 KCP MTU 를 줄인다
    if (reservedBytes > 0) instance.ReserveBytes(reservedBytes);
    instance.NoDelay(options.NoDelay, options.IntervalMs, options.FastResend, options.NoCongestionControl);
    instance.WndSize(options.SendWindow, options.ReceiveWindow);
    kcp = instance;
}
```

**`Cipher`는 `conv`와 방향(`serverDirection`)을 받아 만들어집니다** — AES-GCM의 nonce가
`conv`를 포함하기 때문에(4.10절), 라이브러리 사용자는 키만 주면 되고 nonce 재사용 방지는
자동으로 됩니다.

#### 4.5.4 프레임 디스패치 — `OnFrame`

수신 프레임 하나를 타입별로 분기합니다.

```csharp
public void OnFrame(ReadOnlySpan<byte> frame, uint now)
{
    if (state == KcpSessionState.Dead || state == KcpSessionState.Closed) return;   // 끝난 세션은 어떤 프레임도 무시

    if (options.RawMode) { kcp?.Input(frame, true, options.AckNoDelay); DrainReliable(); return; }

    switch (Framing.PeekType(frame))
    {
        case FrameType.SynCookie: OnSynCookie(frame, now); break;
        case FrameType.SynAck:    OnSynAck(frame, now); break;
        case FrameType.Kcp:       OnKcpFrame(frame, now); break;
        case FrameType.Raw:       OnRawFrame(frame, now); break;
        case FrameType.Ping:      OnPing(frame, now); break;
        case FrameType.Pong:      OnPong(frame, now); break;
        case FrameType.Fin:       OnFin(frame, now); break;
        default: break;   // 알 수 없는 프레임은 조용히 버린다
    }
}
```

**끝난 세션이 프레임을 무시하는 이유**의 주석이 특히 눈여겨볼 만합니다 — "특히 PONG을 돌려주면
상대가 아직 살아 있다고 오해해 영원히 타임아웃되지 않는다." 세션이 죽은 뒤에도 응답을 계속하면
상대방의 keepalive 판정을 오염시킬 수 있다는 미묘한 지점입니다.

`OnKcpFrame`이 신뢰 채널의 핵심 경로입니다 — FEC → 복호화 → `kcp.Input` 순서로 처리합니다.

```csharp
private void OnKcpFrame(ReadOnlySpan<byte> frame, uint now)
{
    if (kcp == null || state == KcpSessionState.Closed) return;
    if (!Framing.TryReadKcp(frame, out var payload)) return;

    lastReceiveAt = now;

    if (fecDecoder != null)
    {
        var isData = fecDecoder.Decode(payload, out var original, fecRecoveredCallback);
        if (isData) kcp.Input(original, true, options.AckNoDelay);
        DrainReliable();
        return;
    }

    if (cipher == null)
    {
        kcp.Input(payload, true, options.AckNoDelay);
    }
    else
    {
        var plain = decryptBuffer!;                                        // G-3: 상주 버퍼, Rent/Return 없음
        if (!cipher.TryDecrypt(payload, plain, out var plainLength)) return;   // 변조·리플레이는 조용히 버린다
        kcp.Input(plain.AsSpan(0, plainLength), true, options.AckNoDelay);
    }
    DrainReliable();
}
```

FEC와 암호화가 **동시에 켜질 수 없다**는 것이 `KcpSessionOptions.Validate()`에서 강제됩니다
(둘을 함께 지원하려면 FEC가 복원한 패킷도 복호화 경로를 타야 하는데, 아직 구현되어 있지
않습니다 — 로드맵에 남겨진 선택 사항입니다).

#### 4.5.5 수신 드레인 — `DrainReliable`과 재진입 가드

`DrainReliable`은 `kcp.Input()` 직후 항상 불려 "지금 꺼낼 수 있는 메시지를 전부" 이벤트로
쏩니다. G-2(무복사 수신) 이후로는 `RecvInPlace`를 우선 시도하고, 다조각일 때만 조립 버퍼로
폴백합니다.

```csharp
private void DrainReliable()
{
    var spanHandler = ReliableReceivedSpan;
    var memoryHandler = ReliableReceived;
    if (spanHandler == null && memoryHandler == null) return;

    if (draining) return;   // ★ 재진입 가드
    draining = true;
    inPlaceTarget = spanHandler;
    try
    {
        while (true)
        {
            var instance = kcp;
            if (instance == null) break;

            if (memoryHandler == null)
            {
                var inPlace = instance.RecvInPlace(inPlaceCallback);
                if (inPlace >= 0) continue;      // 무복사로 처리 완료, 다음 메시지로
                if (inPlace != -3) break;         // -1: 더 없음
                // -3: 다조각 → 아래 조립 경로로
            }

            var size = instance.PeekSize();
            if (size < 0) break;
            var buffer = EnsureBuffer(ref assemblyBuffer, size);
            var n = instance.Recv(buffer.AsSpan(0, size));
            if (n < 0) break;

            spanHandler?.Invoke(this, buffer.AsSpan(0, n));
            memoryHandler?.Invoke(this, new ReadOnlyMemory<byte>(buffer, 0, n));
        }
    }
    finally { inPlaceTarget = null; draining = false; }
}
```

**왜 재진입 가드가 필요한가**: `assemblyBuffer`가 세션 상주 필드로 바뀌면서(무할당화의 대가),
만약 이벤트 핸들러 콜백 **안에서** 사용자가 `session.Update()`를 다시 부르면(드물지만 가능한
사용 패턴), 안쪽 `DrainReliable` 호출이 바깥쪽이 아직 참조 중인 `assemblyBuffer`를 덮어쓸 수
있습니다. `draining` 플래그가 이를 막습니다 — 안쪽 호출은 그냥 조용히 반환하고, 바깥쪽 루프가
`while(true)`로 계속 돌고 있으므로 메시지가 유실되지 않고 순서도 흐트러지지 않습니다(테스트
`핸들러가_세션을_다시_구동해도_메시지가_중복되거나_사라지지_않는다`가 이걸 검증합니다).

`EnsureBuffer`가 조립 버퍼의 성장 전략입니다 — 2배씩 늘려(1KB 시작) 재할당 횟수를 로그
스케일로 줄입니다.

```csharp
private static byte[] EnsureBuffer(ref byte[] buffer, int size)
{
    if (size < 1) size = 1;
    if (buffer.Length >= size) return buffer;

    var capacity = buffer.Length == 0 ? 1024 : buffer.Length;
    while (capacity < size && capacity <= (1 << 29))
        capacity <<= 1;
    if (capacity < size) capacity = size;   // 시프트가 오버플로되는 극단적으로 큰 요청에 대한 안전망

    buffer = new byte[capacity];
    return buffer;
}
```

#### 4.5.6 송신 아웃박스 — `TrySendFromAnyThread`

`Send()`는 소유 스레드 전용입니다. 게임 로직이 다른 스레드에서 돌 때를 위해, 옵션으로 켤 수
있는 바운디드 MPSC(multi-producer single-consumer) 큐를 둡니다.

```csharp
public bool TrySendFromAnyThread(ReadOnlySpan<byte> data)
{
    var queue = outbox
        ?? throw new InvalidOperationException(
            "아웃박스를 쓰려면 KcpSessionOptions.SendOutboxCapacity 를 0 보다 크게 설정해야 합니다(M-1).");

    if (data.Length == 0) return false;

    var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
    data.CopyTo(buffer);                                    // 스레드 경계를 넘기 위한 필수 복사 1회

    if (queue.Writer.TryWrite(new PooledMessage { Buffer = buffer, Length = data.Length }))
    {
        Interlocked.Increment(ref outboxAccepted);
        return true;
    }
    ArrayPool<byte>.Shared.Return(buffer);                   // 못 넣었으면 즉시 반환(누수 방지)
    Interlocked.Increment(ref outboxRejected);
    return false;
}

private void DrainOutbox()   // Update() 첫머리에서, 소유 스레드에서만 호출
{
    var queue = outbox;
    if (queue == null) return;

    var reader = queue.Reader;

    while (state == KcpSessionState.Connected &&
           kcp != null &&
           kcp.WaitSnd < options.MaxSendBacklog &&
           reader.TryRead(out var msg))
    {
        // 조각 수가 수신 윈도를 넘는 메시지는 KCP 가 거부한다(-2). 조용히 사라지지 않게 계수한다.
        if (kcp.Send(msg.Buffer.AsSpan(0, msg.Length)) != 0)
            System.Threading.Interlocked.Increment(ref outboxRejected);

        ArrayPool<byte>.Shared.Return(msg.Buffer);
    }
}
```

`Channel<T>`(`System.Threading.Channels`)를 쓴 이유는 **lock-free 큐가 이미 검증되어
있고**, `BoundedChannelOptions`로 백프레셔(용량 초과 시 거부)를 손쉽게 얻을 수 있기 때문입니다.
`DrainOutbox`가 `KcpSession.Update`의 **가장 첫 줄**에서 불리므로, 다른 스레드가 넣은 메시지는
다음 소유 스레드 틱에서 KCP로 들어갑니다 — 여러 스레드가 동시에 `Send`를 부르는 대신, "큐에
넣기(아무 스레드) → 소유 스레드가 꺼내 실제로 Send(소유 스레드)"로 소유 스레드 규약을 지킵니다.

`ReleaseOutbox(bool complete)`가 종료 시 대여 버퍼를 정리하는데, `complete` 플래그로 "내용만
비움"과 "영구히 닫음"을 구분합니다 — `Close()`(재연결 가능한 일시적 종료)는 `complete: false`,
`ReleaseResources()`(세션 수명 자체가 끝남)는 `complete: true`를 씁니다.

### 4.6 `KcpServer.cs` — 단일 수신 루프 서버

**단일 컨슈머 모델**입니다. 수신 루프 스레드 하나가 소켓 읽기·세션 디스패치·타이머 갱신을
전부 담당합니다.

```csharp
private void ReceiveLoop(CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        var now = clock();
        var waitMicros = scheduler.TimeToNext(now, options.PollMicroseconds / 1000) * 1000;   // 다음 세션 기한까지만 잔다
        if (waitMicros > options.PollMicroseconds) waitMicros = options.PollMicroseconds;      // 상한(기본 5ms)

        if (udp.WaitForData(waitMicros)) DrainSocket(udp);
        RunDueSessions();
        SampleMetrics();
    }
}
```

**대기 시간이 고정이 아니라 `scheduler.TimeToNext`로 동적**입니다. 세션 스케줄러(4.8절)가
"가장 빨리 깨워야 할 세션이 몇 ms 뒤인지"를 알려 주므로, 유휴 세션만 있을 때는 오래 자고,
곧 재전송해야 할 세션이 있으면 짧게 잡니다.

```csharp
private void DrainSocket(IUdpTransport udp)
{
    var processed = 0;
    while (processed < options.MaxReceiveBatch &&
           udp.TryReceive(receiveBuffer, out var received, out var remote))
    {
        processed++;
        if (received <= 0) continue;

        var now = clock();                                  // 패킷마다 새로 읽는다(배치 안에서도 정확한 시각)
        var frame = receiveBuffer.AsSpan(0, received);

        if (sessions.TryGetValue(remote, out var session))
        {
            if (Framing.PeekType(frame) == FrameType.Syn) { SendSynAck(session, remote); continue; }  // SYN 재전송(SYN_ACK 유실 대응)
            session.OnFrame(frame, now);
            scheduler.Schedule(session, now);   // 방금 뭔가 왔으니 즉시 갱신 대상으로
            continue;
        }

        if (Framing.PeekType(frame) == FrameType.Syn) HandleSyn(frame, remote, now);
        // 모르는 주소의 다른 프레임은 무응답으로 버린다(스캐닝 대응)
    }
}
```

`MaxReceiveBatch` 상한이 있는 이유는 **타이머 굶주림 방지**입니다. 수신 큐가 끊임없이 차
있으면(고부하 상황) `DrainSocket`이 영원히 안 끝나 `RunDueSessions`(재전송 처리)가 밀릴 수
있으므로, 한 번에 처리할 패킷 수를 제한해 둡니다.

**세션 생성은 오직 `HandleSyn`에서, 쿠키가 검증된 뒤에만** 일어납니다(4.4절 참고). 생성 직후
캐시해 둔 델리게이트로 이벤트를 구독합니다.

```csharp
var session = new KcpSession(endPoint, sessionOptions, sendToCallback, clock, isClient: false);
session.Disconnected += onSessionDisconnected;          // 생성자에서 미리 만들어 둔 델리게이트(클로저 없음, G-5)
session.ReliableReceivedSpan += onSessionReliable;
session.UnreliableReceivedSpan += onSessionUnreliable;
```

`sendToCallback`, `onSessionDisconnected` 등이 **서버 생성자에서 한 번만** 만들어지는 이유는,
세션이 새로 생길 때마다 `(s, data) => ReliableReceived?.Invoke(s, data)` 같은 람다를 쓰면
그때마다 클로저 객체가 생기기 때문입니다(G-5). 대신 인스턴스 메서드(`OnSessionReliable` 등)를
가리키는 델리게이트를 필드에 캐시해 두고 재사용합니다.

`RunDueSessions`가 `SessionScheduler`와 맞물려 도는 부분입니다.

```csharp
private void RunDueSessions()
{
    var now = clock();
    while (scheduler.TryPopDue(now, out var session))
    {
        var next = session.Update(now);
        if (session.State == KcpSessionState.Closed || session.State == KcpSessionState.Dead)
        {
            pendingRemoval.Add(session);   // 순회 중 딕셔너리를 바로 못 지우므로 나중에 일괄 제거
            continue;
        }
        if (Kcp.Itimediff(next, now) <= 0) next = now + (uint)options.Session.IntervalMs;   // 최소 해상도 보장
        scheduler.Schedule(session, next);
    }
    foreach (var session in pendingRemoval) RemoveSession(session);
    pendingRemoval.Clear();
}
```

**이벤트 큐(`EnableEventQueue`, M-2)**가 켜져 있으면, C# 이벤트 발화 지점마다
`events?.Publish(...)`가 같이 불립니다(연결/종료/신뢰/비신뢰 4곳). 4.12절에서 자세히 다룹니다.

### 4.7 `KcpClient.cs` — 클라이언트

두 가지 구동 모델을 제공하되 **섞어 쓰면 예외**를 던집니다(`runLoopActive` 플래그로 감지).

- **폴링형** — 게임 루프에서 `Update()`를 매 프레임 부른다. MonoGame의 `Game.Update()` 안에서
  호출하면 된다.
- **자체 루프형** — `RunAsync()`를 한 번 띄워 두고 이벤트로 받는다. 콘솔 도구·부하 테스트에
  적합하다.

두 모델의 실체는 결국 같은 `Pump()`입니다.

```csharp
private uint Pump()
{
    var current = session;
    var udp = transport;
    if (current == null || udp == null)
        return clock() + (uint)options.IntervalMs;

    var now = clock();   // G-5: 시계는 진입 시 1회만 읽는다(코어 PF-01 과 같은 원칙)

    // 도착한 패킷을 전부 빨아들인다(호출당 1패킷 제한 없음, 진단 PF-08)
    while (udp.TryReceive(receiveBuffer, out var received, out var remote))
    {
        if (received <= 0) continue;
        if (remote != current.RemoteKey) continue;   // 서버가 아닌 곳에서 온 패킷은 무시(스푸핑 방어)
        current.OnFrame(receiveBuffer.AsSpan(0, received), now);
    }

    return current.Update(now);   // 다음 갱신 시각을 반환한다
}
```

**`ConnectAsync`가 1ms `Task.Delay` 폴링을 안 쓰는 이유(A-1)** — 예전에는 연결이 될 때까지
`await Task.Delay(1)`을 반복했습니다. 지금은 `TaskCompletionSource`가 세션의 `Connected`/
`Disconnected` 이벤트로 즉시 완성되고, 그 사이의 대기는 소켓의 `WaitForData`(패킷이 오면 그
순간 깨어남)로 처리합니다.

```csharp
var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
void OnHandshakeDone(KcpSession _) => handshake.TrySetResult(true);
void OnHandshakeFailed(KcpSession _, KcpDisconnectReason __) => handshake.TrySetResult(false);
connecting.Connected += OnHandshakeDone;
connecting.Disconnected += OnHandshakeFailed;

while (connecting.State == KcpSessionState.Connecting && !handshake.Task.IsCompleted)
{
    var next = Pump();                                          // SYN_ACK 이 오면 이 안에서 이벤트가 곧바로 발화한다
    if (connecting.State != KcpSessionState.Connecting || handshake.Task.IsCompleted) break;

    var now = clock();
    var remaining = Kcp.Itimediff(deadline, now);
    if (remaining <= 0) break;

    // 다음 SYN 재시도 시각까지 기다린다(상한 MaxLoopWaitMs, 남은 데드라인을 넘지 않는다)
    var waitMs = Kcp.Itimediff(next, now);
    if (waitMs > remaining) waitMs = remaining;
    if (waitMs > MaxLoopWaitMs) waitMs = MaxLoopWaitMs;
    if (waitMs < 1) waitMs = 1;

    var before = clock();
    var ready = await WaitForDataAsync(udpTransport, waitMs, cancellationToken).ConfigureAwait(false);
    if (!ready && Kcp.Itimediff(clock(), before) <= 0)
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);   // 가상 전송(테스트 더블)의 바쁜 대기 방지
}
```

`connecting.State == KcpSessionState.Connecting` 조건이 루프에 들어간 이유는 `RawMode`
세션이 **생성 시점에 이미 `Connected`**라서 `Connected` 이벤트 자체가 발화하지 않기 때문입니다
— 상태만 보고도 즉시 빠져나갈 수 있어야 `RawMode` 클라이언트가 핸드셰이크 타임아웃(기본
5.5초)만큼 불필요하게 기다리는 일이 없습니다.

**`RunAsync`의 동적 대기(A-3)** 도 같은 원리입니다 — 예전엔 고정 1ms로 폴링했지만, 지금은
`Pump()`가 돌려준 "다음 갱신 시각"까지만(상한 `MaxLoopWaitMs = 5ms`) 소켓을 기다립니다.

### 4.8 `SessionScheduler.cs` — 최소 힙 타이머

세션이 1만 개일 때 매 틱마다 전부의 `Update`를 부르면 대부분 헛일입니다. 각 세션이
`Update(now)`의 반환값으로 "다음에 언제 깨워 줘"를 알려 주면, 그 시각들을 **최소 힙**에 넣고
기한이 된 것만 꺼냅니다.

```csharp
public void Schedule(KcpSession session, uint dueTime)
{
    var stamp = nextStamp++;
    stamps[session] = stamp;                                    // 이 세션의 "최신 예약" 표시
    Push(new Entry { DueTime = dueTime, Stamp = stamp, Session = session });
}

public bool TryPopDue(uint now, out KcpSession session)
{
    while (heap.Count > 0)
    {
        var top = heap[0];
        if (Kcp.Itimediff(now, top.DueTime) < 0) break;   // 아직 이르다
        PopRoot();
        if (stamps.TryGetValue(top.Session, out var current) && current == top.Stamp)   // lazy deletion 검사
        {
            stamps.Remove(top.Session);
            session = top.Session;
            return true;
        }
        // stamp 가 다르면 이미 갱신된 낡은 항목 — 그냥 버리고 계속 검사
    }
    session = null!;
    return false;
}
```

**lazy deletion**이 핵심 설계입니다. 세션이 패킷을 받아서(`DrainSocket`) 예정 시각을 앞당겨야
할 때, 힙에서 옛 항목을 찾아 지우는 대신(O(n) 탐색) **그냥 새 항목을 하나 더 넣습니다.**
`stamps` 딕셔너리는 세션마다 "가장 최근에 예약된 항목의 도장(stamp)"만 기억합니다. 힙에서
어떤 항목을 꺼냈을 때 그 항목의 stamp가 `stamps`에 기억된 최신 stamp와 다르면, 그건 이미
낡아진(더 최근 예약으로 덮어써진) 항목이므로 조용히 버립니다. 이 방식 덕분에 **`Schedule`,
`TryPopDue` 모두 O(log n)**을 유지하면서, "이미 예약된 항목을 앞당기는" 연산에서 O(n) 탐색이
사라집니다. 대가는 힙에 중복 항목이 잠깐 쌓일 수 있다는 것뿐이고, 그 항목들은 `TryPopDue`가
지나가면서 자연히 청소됩니다.

`Push`/`PopRoot`는 표준 이진 힙 구현입니다(부모 `(i-1)>>1`, 자식 `2i+1`/`2i+2`) — `Kcp.Itimediff`로
비교하는 것만 일반적인 힙과 다릅니다(랩어라운드 안전 비교).

### 4.9 `KcpSessionOptions.cs` / `KcpServerOptions`

두 설정 클래스가 라이브러리의 거의 모든 튜닝 지점을 모아 둡니다. 표로 정리합니다(기본값과
간단한 의미만; 더 깊은 튜닝 가이드는 [책 8장](book/ch08-tuning.md) 참고).

**`KcpSessionOptions`** (세션·`Kcp` 파라미터)

| 속성 | 기본값 | 의미 |
|---|---:|---|
| `HandshakeTimeoutMs` / `HandshakeRetryIntervalMs` / `HandshakeMaxRetries` | 5000 / 1000 / 3 | 핸드셰이크 재시도 정책 |
| `PingIntervalMs` | 2000 | keepalive 주기 |
| `SessionTimeoutMs` | 10000 | 이 시간 동안 무응답이면 `Dead` |
| `FinLingerMs` | 2000 | FIN 이후 잔여 재전송 대기 시간 |
| `MaxSendBacklog` | 1024 | `Kcp.WaitSnd` 상한 — 넘으면 `Send`가 `Backpressure` |
| `SendOutboxCapacity` | 0 | 0이면 아웃박스 비활성(4.5.6절) |
| `NoDelay` / `IntervalMs` / `FastResend` / `NoCongestionControl` | 1 / 10 / 2 / 1 | `Kcp.NoDelay()`에 그대로 전달 |
| `SendWindow` / `ReceiveWindow` | 128 / 128 | `Kcp.WndSize()`에 전달 |
| `Mtu` | 1400 | UDP 페이로드 상한(프레이밍 1바이트 포함) |
| `AckNoDelay` | true | 수신 즉시 ACK(지연↓ 대역폭↑) |
| `StreamMode` | false | 메시지 경계 제거 |
| `RawMode` / `RawModeConv` | false / 0 | kcp-go 상호운용 모드 |
| `EncryptionKey` / `Cipher` | null / null | AES-256-GCM 키 또는 커스텀 암호화기 |
| `Fec` | null | Reed-Solomon FEC 설정 |

`Validate()`가 이 값들의 조합 규칙을 강제합니다 — 가장 중요한 건 `Fec != null &&
(EncryptionKey != null || Cipher != null)`이면 예외를 던지는 것(4.5.4절에서 언급한 FEC/암호화
동시 사용 불가 제약).

`Clone()`은 `MemberwiseClone()`입니다 — `KcpServer`가 클라이언트마다 독립된 옵션 사본을
갖도록(같은 `KcpSessionOptions` 인스턴스를 여러 세션이 공유하면 안 되는 이유는 없지만, 세션별로
`RawModeConv` 등을 다르게 주고 싶을 수 있어 사본을 씁니다).

**`KcpServerOptions`** (서버 전용)

| 속성 | 기본값 | 의미 |
|---|---:|---|
| `MaxSessions` | 8192 | 동시 세션 상한(초과 SYN은 무응답 폐기) |
| `PollMicroseconds` | 5000 | 수신 루프 대기 상한 |
| `MaxReceiveBatch` | 1024 | 한 배치에서 처리할 최대 패킷 수 |
| `SocketReceiveBufferSize` / `SocketSendBufferSize` | 1MB / 1MB | OS 소켓 버퍼 |
| `MetricsIntervalMs` | 5000 | `MetricsSampled` 발화 주기(0 이하면 끔) |

### 4.10 `AesGcmPacketCipher.cs` — 패킷 암호화

`IPacketCipher` 인터페이스(암호화기 인터페이스, `Encrypt`/`TryDecrypt` 두 메서드)의 기본
구현입니다. 와이어 포맷은 `[counter u64][암호문][태그 16B]` — 오버헤드 24바이트.

**nonce 구성이 이 파일에서 가장 중요한 부분입니다.**

```csharp
private void BuildNonce(Span<byte> nonce, ulong counter)
{
    BinaryPrimitives.WriteUInt32LittleEndian(nonce, conv);              // nonce[0..4)  = conv
    BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), counter);  // nonce[4..12) = counter
}
```

GCM은 **같은 키로 같은 nonce를 두 번 쓰면 인증·기밀성이 완전히 무너집니다**(평문을 XOR로
복구당할 수 있음). 이를 막는 두 겹의 장치가 있습니다.

1. **`conv`가 nonce에 섞입니다.** 세션마다 `conv`가 다르므로, 여러 세션이 같은 `EncryptionKey`를
   공유해도(문서가 권장하지는 않지만 허용은 함) nonce 공간이 겹치지 않습니다.
2. **방향마다 카운터 공간을 나눕니다.**

```csharp
sendCounter = serverDirection ? 1UL << 63 : 0UL;   // 서버는 최상위 비트가 1인 영역에서, 클라는 0인 영역에서 시작
```

같은 세션 안에서도 클라→서버와 서버→클라 두 개의 독립된 `AesGcmPacketCipher` 인스턴스가
있고(`CreateKcp`에서 `serverDirection: !isClient`로 만듦), 각자 자기 방향의 카운터 절반만
씁니다. 이러면 한 세션 안에서 두 방향이 우연히 같은 카운터를 쓸 일이 아예 구조적으로 없습니다.

**리플레이 방지 — 64비트 슬라이딩 윈도 비트마스크:**

```csharp
private bool IsReplay(ulong counter)
{
    var expectServerHighBit = !serverDirection;                          // 상대 방향 카운터인지 확인
    if (((counter & (1UL << 63)) != 0) != expectServerHighBit) return true;

    if (!anyReceived) return false;
    if (counter > highestSeen) return false;                              // 새로운 카운터는 항상 허용

    var delta = highestSeen - counter;
    if (delta >= 64) return true;                                          // 윈도보다 오래됨
    return (window & (1UL << (int)delta)) != 0;                            // 이미 본 비트인지 검사
}

private void MarkSeen(ulong counter)
{
    if (!anyReceived)                                                       // 이 방향에서 처음 받은 패킷
    {
        anyReceived = true;
        highestSeen = counter;
        window = 1;
        return;
    }

    if (counter > highestSeen)
    {
        var shift = counter - highestSeen;
        window = shift >= 64 ? 1UL : (window << (int)shift) | 1UL;         // 윈도를 밀면서 새 카운터 비트를 세운다
        highestSeen = counter;
        return;
    }

    var delta = highestSeen - counter;
    if (delta < 64)
        window |= 1UL << (int)delta;                                        // 과거 자리의 비트만 세운다(윈도 밖이면 무시)
}
```

이건 TCP 리플레이 방지나 IPsec 안티리플레이 윈도와 같은 고전적인 기법입니다 — `highestSeen`
(가장 큰 카운터)을 기준으로 최근 64개의 "받았음" 비트를 유지합니다. 순서가 뒤바뀐 패킷(UDP라
흔함)도 윈도 안이면 정상 처리하되, 이미 본 카운터거나 윈도보다 64개 이상 오래된 카운터는
거부합니다. **인증(태그 검증) 전에 먼저 리플레이 검사를 하는 순서**(`TryDecrypt`의 주석 "계산
낭비를 줄인다")도 눈여겨볼 지점입니다 — 어차피 버릴 패킷에 AES 복호화 연산을 낭비하지
않습니다.

`Encrypt`/`TryDecrypt`는 `Span<byte> nonce = stackalloc byte[12]`로 nonce를 스택에 만들어
할당이 없습니다. `#if NET8_0_OR_GREATER`로 감싸진 이유는 `System.Security.Cryptography.AesGcm`
클래스가 .NET Core 3.0+ 전용이기 때문입니다 — netstandard2.1(Godot C# 등) 사용자는
`IPacketCipher`를 직접 구현해 `KcpSessionOptions.Cipher`에 주입해야 합니다.

### 4.11 `Fec/` — Reed-Solomon FEC

FEC(Forward Error Correction, 전진 오류 정정)는 데이터 K개마다 패리티 M개를 더 보내서, **최대
M개까지 손실돼도 재전송 없이 원본을 복원**하는 기법입니다. 이 라이브러리는 kcp-go와 같은
GF(2⁸) 기반 리드-솔로몬 소거 부호(klauspost/reedsolomon과 같은 방식)를 씁니다. 세 파일이 계층을
이룹니다: `GaloisField`(유한체 산술) → `ReedSolomon`(행렬 연산) → `FecCodec`(프레이밍 + 그룹
관리).

#### 4.11.1 `GaloisField.cs` — GF(2⁸) 산술

일반 정수 연산이 아니라 **유한체 연산**을 쓰는 이유: 리드-솔로몬 부호가 성립하려면 나눗셈이
항상 가능해야 하는데(역원이 항상 존재), 보통의 바이트 산술(mod 256)은 그렇지 않습니다.
GF(2⁸)(기약 다항식 `x⁸+x⁴+x³+x²+1`, 즉 0x11D)는 0이 아닌 모든 원소가 곱셈 역원을 갖는
체(field)입니다.

```csharp
static GaloisField()
{
    var x = 1;
    for (var i = 0; i < 255; i++)
    {
        ExpTable[i] = (byte)x;
        LogTable[x] = (byte)i;
        x <<= 1;
        if ((x & 0x100) != 0) x ^= Polynomial;   // 오버플로 시 기약 다항식으로 접어 넣는다
    }
    for (var i = 255; i < 512; i++) ExpTable[i] = ExpTable[i - 255];   // 로그 덧셈이 255 를 넘어도 안전하게
}

public static byte Multiply(byte a, byte b)
{
    if (a == 0 || b == 0) return 0;
    return ExpTable[LogTable[a] + LogTable[b]];   // a*b = exp(log(a) + log(b)) — 로그 표로 곱셈을 덧셈으로
}
```

**로그/지수 표를 미리 계산해 두는 것**이 이 파일의 전부입니다 — 생성원 2의 거듭제곱을
`ExpTable`에, 그 역함수를 `LogTable`에 담아 두면, GF(2⁸)의 곱셈이 "로그를 더하고 다시
지수를 취하는" O(1) 테이블 조회가 됩니다(일반 로그의 `log(ab) = log(a) + log(b)` 성질을
그대로 씁니다). `MultiplyAddInto`(`dest ^= source * multiplier`)가 실제 부호화·복원 연산의
최소 단위입니다 — GF(2⁸)에서 덧셈은 XOR입니다.

#### 4.11.2 `ReedSolomon.cs` — 부호화 행렬과 복원

```csharp
public ReedSolomon(int dataShards, int parityShards)
{
    // (dataShards + parityShards 는 256을 넘을 수 없다 등, 유효성 검사는 생략)
    DataShards = dataShards;
    ParityShards = parityShards;

    encodeMatrix = BuildEncodeMatrix(dataShards, parityShards);   // (k+m) x k 조직 부호 행렬

    parityRows = new byte[parityShards][];
    for (var i = 0; i < parityShards; i++)
        parityRows[i] = encodeMatrix[dataShards + i];             // 아래 m 행이 패리티 계산에 쓰인다

    // G-4: 복원용 스크래치를 생성 시 1회 잡아 두고 재사용한다
    subMatrix = new byte[dataShards][];
    subShards = new byte[dataShards][];
    invertWork = new byte[dataShards][];
    invertResult = new byte[dataShards][];
    for (var i = 0; i < dataShards; i++)
    {
        invertWork[i] = new byte[dataShards * 2];                 // 가우스 소거법의 [행렬 | 단위행렬] 확대 행렬
        invertResult[i] = new byte[dataShards];
    }
}
```

**조직 부호(systematic code)**란 "상단 k×k가 단위행렬"인 부호화 행렬을 말합니다 — 그러면
데이터 샤드는 인코딩 과정에서 **그대로 나가고**(행렬 곱이 자기 자신이 되므로), 패리티 샤드만
계산하면 됩니다. `BuildEncodeMatrix`는 반데르몬드 행렬을 만든 뒤, 그 행렬의 상단 k×k 부분의
역행렬을 곱해 상단을 단위행렬로 만듭니다(가우스 소거법 `Invert`로 역행렬을 구함).

```csharp
public void Encode(byte[][] shards, int shardSize)
{
    for (var p = 0; p < ParityShards; p++)
    {
        var target = shards[DataShards + p].AsSpan(0, shardSize);
        target.Clear();
        var row = parityRows[p];
        for (var d = 0; d < DataShards; d++)
            GaloisField.MultiplyAddInto(row[d], shards[d].AsSpan(0, shardSize), target);   // target = Σ row[d] * data[d]
    }
}
```

패리티 샤드 하나는 **부호화 행렬의 한 행과 모든 데이터 샤드의 (행렬 곱)**입니다 — 표준적인
선형 부호화 연산을 GF(2⁸) 위에서 한 것입니다.

**복원(`Reconstruct`, 진단 G-4로 무할당화됨)**이 더 흥미롭습니다. 살아남은 샤드 K개만 있으면
원본을 복원할 수 있다는 것이 리드-솔로몬의 핵심 성질입니다.

```csharp
public bool Reconstruct(byte[][] shards, bool[] present, int shardSize)
{
    // (인자 null·길이 검증은 생략)

    var available = 0;
    for (var i = 0; i < TotalShards; i++)
        if (present[i]) available++;

    if (available < DataShards) return false;   // 살아남은 샤드가 K개 미만이면 애초에 복원 불가능

    var missingData = false;
    for (var i = 0; i < DataShards; i++)
        if (!present[i]) { missingData = true; break; }

    if (!missingData) return true;   // 데이터 샤드가 이미 다 있으면 복원할 것이 없다

    // 살아 있는 샤드 k 개를 골라 "부분 행렬"을 만든다(encodeMatrix 의 해당 행들만 추출)
    var used = 0;
    for (var i = 0; i < TotalShards && used < DataShards; i++)
    {
        if (!present[i]) continue;
        subMatrix[used] = encodeMatrix[i];   // 참조만 — 복사 없음(원본을 안 바꾸므로 안전)
        subShards[used] = shards[i];
        used++;
    }

    if (!InvertInto(subMatrix, invertWork, invertResult)) return false;   // 특이행렬이면(선형독립 아니면) 실패

    for (var i = 0; i < DataShards; i++)
    {
        if (present[i]) continue;
        var recovered = shards[i].AsSpan(0, shardSize);
        recovered.Clear();
        var row = invertResult[i];
        for (var c = 0; c < DataShards; c++)
            GaloisField.MultiplyAddInto(row[c], subShards[c].AsSpan(0, shardSize), recovered);
        present[i] = true;
    }
    return true;
}
```

수학적으로: 살아남은 K개 샤드는 "부호화 행렬의 K개 행 × 원본 데이터"입니다. 그 K개 행으로
이루어진 K×K 부분 행렬이 역행렬을 가지면(반데르몬드 행렬의 성질상 서로 다른 행은 항상 선형
독립이므로 역행렬이 존재), **역행렬 × 살아남은 샤드 = 원본 데이터**를 그대로 복원할 수
있습니다. `InvertInto`가 가우스 소거법으로 이 역행렬을 구합니다.

**API가 `byte[]?[]` + null이 아니라 `byte[][]` + `bool[] present`로 바뀐 것(G-4)**이 무할당화의
핵심입니다. 예전에는 복원할 때마다 `subMatrix`, `subShards`, 역행렬 작업 버퍼를 매번 `new`로
만들었는데(복원 1회당 약 1.4KB 이상 할당), 지금은 생성자에서 한 번 잡아 둔 필드를 재사용합니다.
`present[i] = true`로 "복원됨"을 표시하는 것도, 결과를 새 배열로 반환하지 않고 **`shards[i]`
자리에 제자리로 채우기** 때문입니다.

#### 4.11.3 `FecCodec.cs` — 프레이밍과 그룹 관리

**프레이밍**: FEC 헤더는 `[seqid u32][flag u16]`(6바이트) — 데이터 샤드는 그 뒤에
`[size u16]`을 더 붙입니다. `seqid`가 전역 증가 일련번호이고, `groupIndex = seqid / 전체샤드수`,
`position = seqid % 전체샤드수`로 그룹과 그룹 내 위치를 유도합니다.

**인코더**는 데이터 샤드가 그룹 크기(`DataShards`)만큼 쌓이면 패리티를 계산해 뒤이어 내보냅니다.

```csharp
public void Encode(Span<byte> frame, FecOutput output)
{
    var body = frame.Slice(frameTypeOffset);              // [seqid][flag][size][payload]
    var dataRegion = body.Slice(FecFrame.HeaderSize);     // [size][payload]

    BinaryPrimitives.WriteUInt32LittleEndian(body, nextSeq);
    BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(4), FecFrame.TypeData);
    BinaryPrimitives.WriteUInt16LittleEndian(dataRegion, (ushort)dataRegion.Length);

    // 그룹 캐시에 복사하고 나머지는 0 으로 채운다(패리티 계산 길이를 맞추기 위해)
    var slot = shards[filled];
    dataRegion.CopyTo(slot);
    if (dataRegion.Length < maxShardSize)
        slot.AsSpan(dataRegion.Length).Clear();

    if (dataRegion.Length > groupMaxSize)
        groupMaxSize = dataRegion.Length;

    nextSeq++;
    filled++;

    output(frame);   // 데이터 패킷은 그룹이 다 찼든 안 찼든 바로 내보낸다(지연 없음)

    if (filled < rs.DataShards)
        return;

    // 그룹 완성 → 패리티 계산
    rs.Encode(shards, groupMaxSize);

    parityFrame[0] = frame[0];   // 같은 프레임 타입으로 내보낸다
    for (var p = 0; p < rs.ParityShards; p++)
    {
        var target = parityFrame.AsSpan(frameTypeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(target, nextSeq);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(4), FecFrame.TypeParity);
        shards[rs.DataShards + p].AsSpan(0, groupMaxSize).CopyTo(target.Slice(FecFrame.HeaderSize));

        nextSeq++;
        output(parityFrame.AsSpan(0, frameTypeOffset + FecFrame.HeaderSize + groupMaxSize));
    }

    filled = 0;
    groupMaxSize = 0;
}
```

데이터 패킷은 **FEC와 무관하게 즉시 전송**됩니다(FEC는 순수 추가 정보이지 지연 요소가 아닙니다).
패리티는 그룹이 다 찼을 때만 한꺼번에 뒤따라 나갑니다.

**디코더**는 도착한 샤드를 그룹별로 모으고, 필요할 때만 복원을 시도합니다(4.11.2절의
`ReedSolomon.Reconstruct` 호출부).

```csharp
public bool Decode(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> payload, FecOutput onRecovered)
{
    payload = default;
    if (body.Length < FecFrame.HeaderSize) return false;

    var seq = BinaryPrimitives.ReadUInt32LittleEndian(body);
    var flag = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4));
    var shardData = body.Slice(FecFrame.HeaderSize);
    if (shardData.Length == 0 || shardData.Length > maxShardSize) return false;

    var isData = flag == FecFrame.TypeData;
    if (!isData && flag != FecFrame.TypeParity) return false;

    var groupIndex = seq / (uint)rs.TotalShards;
    var position = (int)(seq % (uint)rs.TotalShards);

    // 데이터 샤드 위치에 패리티가 오거나 그 반대면 규격 위반이다
    if (isData != position < rs.DataShards) return false;

    if (isData)
    {
        // [size u16][payload...]
        var size = BinaryPrimitives.ReadUInt16LittleEndian(shardData);
        if (size < 2 || size > shardData.Length) return false;
        payload = shardData.Slice(2, size - 2);
    }

    var group = GetGroup(groupIndex);
    if (group == null) return isData;   // 너무 오래된 그룹이면 복원은 포기하고 데이터만 넘긴다

    if (!group.Present[position])
    {
        // 미리 잡아 둔 슬롯에 복사한다(패킷당 할당 0). 뒷부분은 반드시 0 으로 비운다
        var slot = group.Shards[position];
        shardData.CopyTo(slot);
        if (shardData.Length < maxShardSize)
            slot.AsSpan(shardData.Length).Clear();

        group.Present[position] = true;
        group.Received++;
        if (shardData.Length > group.MaxSize) group.MaxSize = shardData.Length;
    }

    TryRecover(group, onRecovered);
    return isData;
}

private void TryRecover(Group group, FecOutput onRecovered)
{
    if (group.Recovered || group.Received < rs.DataShards)
        return;                                                // 아직 데이터 샤드 수만큼 안 모였다

    var missing = false;
    for (var i = 0; i < rs.DataShards; i++)
        if (!group.Present[i]) { missing = true; break; }

    if (!missing)
    {
        group.Recovered = true;                                 // 다 도착했으니 더 볼 것이 없다
        return;
    }

    Array.Copy(group.Present, presentScratch, rs.TotalShards);   // 사본 하나만 뜬다(스크래치 재사용)
    if (!rs.Reconstruct(group.Shards, presentScratch, group.MaxSize))
        return;

    group.Recovered = true;

    for (var i = 0; i < rs.DataShards; i++)
    {
        if (group.Present[i]) continue;      // 원래 있었던 자리는 이미 위로 올라갔다
        if (!presentScratch[i]) continue;     // 복원되지 않았다

        var shard = group.Shards[i];
        var size = BinaryPrimitives.ReadUInt16LittleEndian(shard);
        if (size < 2 || size > group.MaxSize) continue;

        RecoveredShards++;
        onRecovered(shard.AsSpan(2, size - 2));   // 복원된 KCP 패킷을 콜백으로
    }
}
```

**그룹 슬랩 재사용(G-4)**: `FecDecoder.Group`은 처음 만들어질 때 `TotalShards`개의 샤드
버퍼를 **전부 미리 할당**해 둡니다. 그룹이 다 처리되고 재활용될 때(최대 16그룹, `MaxGroups`)는
`Array.Clear(group.Present, ...)`만 하고 버퍼 자체는 그대로 씁니다. 도착 여부를 `byte[]?`의
`null` 대신 별도 `bool[] Present` 배열로 관리하는 것도 이 재사용을 위해서입니다 — null이면
"이 자리는 실제로 채워진 적 없음"을 배열 자체로 표현할 수 없어(재사용 시 이전 그룹의 낡은
데이터가 남아 있음) 별도 플래그가 필요합니다.

이 재사용의 대가로 **세션당 고정 메모리** `MaxGroups(16) × TotalShards(13, 기본 10+3) ×
maxShardSize(약 1,393B) ≈ 283KB`가 듭니다 — FEC를 켠 세션에서만 지불하는 비용입니다.

`FecOptions`가 사용자 설정입니다(`DataShards` 기본 10, `ParityShards` 기본 3 — 손실률 약
23%까지 재전송 없이 복구). 그룹을 키우면(예: 20+6) 패리티 비율은 같지만 한 그룹이 완성되는 데
걸리는 시간(=지연)이 늘어나는 트레이드오프가 있습니다 — [16장(FEC)](book/ch16-fec.md)에서
더 다룹니다.

### 4.12 `KcpEventQueue.cs` — 게임 스레드 이벤트 큐

`KcpServer`/`KcpClient`의 C# 이벤트는 수신 루프 스레드에서 발화합니다. 게임 로직은 대개 다른
스레드에서 도는데, "핸들러에서는 큐에 넣기만 하라"는 정석 패턴을 사용자가 매번 직접 짜지
않도록 이 클래스가 그 패턴을 라이브러리 API로 제공합니다.

```csharp
public struct KcpEvent
{
    private byte[]? payload;   // ArrayPool 대여 버퍼

    internal KcpEvent(KcpEventType type, KcpSession session, byte[]? payload, int payloadLength, KcpDisconnectReason reason)
    {
        Type = type; Session = session; this.payload = payload;
        PayloadLength = payloadLength; Reason = reason;
    }

    public KcpEventType Type { get; }        // Connected / Disconnected / Reliable / Unreliable
    public KcpSession Session { get; }
    public int PayloadLength { get; }
    public KcpDisconnectReason Reason { get; }
    public ReadOnlySpan<byte> Data => payload == null ? default : payload.AsSpan(0, PayloadLength);

    public void ReturnPayload()
    {
        var buffer = payload;
        if (buffer == null) return;
        payload = null;                        // 이중 반환 방지 센티널(KcpSegment.Reset 과 같은 패턴)
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

public sealed class KcpEventQueue
{
    private readonly Channel<KcpEvent> channel;   // SingleReader/SingleWriter, FullMode = Wait
    private long dropped;

    internal bool Publish(KcpEventType type, KcpSession session, ReadOnlySpan<byte> data, KcpDisconnectReason reason)
    {
        byte[]? buffer = null;
        if (data.Length > 0) { buffer = ArrayPool<byte>.Shared.Rent(data.Length); data.CopyTo(buffer); }

        if (channel.Writer.TryWrite(new KcpEvent(type, session, buffer, data.Length, reason))) return true;

        if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);   // 못 넣었으면 즉시 반환
        Interlocked.Increment(ref dropped);
        return false;
    }
}
```

**바운디드 큐가 가득 차면 이벤트를 그냥 버립니다** — `FullMode = BoundedChannelFullMode.Wait`은
`TryWrite`가 즉시 `false`를 반환하는 논블로킹 시도를 의미합니다(수신 루프 스레드가 게임
스레드를 기다리며 블로킹되면 안 되므로). `Dropped` 카운터가 이걸 관측 가능하게 만듭니다 —
**신뢰 채널 메시지도 예외 없이 버려질 수 있다**는 점이 이 API의 가장 중요한 계약입니다. 큐
용량을 게임 루프의 처리 속도에 맞춰 충분히 잡고 `Dropped`를 지표로 반드시 내보내야 합니다.

`Publish`가 `internal`인 이유: 이벤트를 넣는 건 라이브러리(수신 루프 스레드)만 할 수 있고,
사용자는 `TryRead`로 꺼내기만 합니다 — API 오용(사용자가 가짜 이벤트를 주입)을 컴파일 타임에
막습니다.

### 4.13 `ServerMetrics.cs` — 관측성

`KcpServer.MetricsSampled` 이벤트가 주기적으로(기본 5초) 내보내는 스냅숏을 계산하는
정적 클래스입니다.

```csharp
internal static class MetricsCollector
{
    public static KcpServerMetrics Collect(List<KcpSession> sessions, uint now, List<int> rttScratch)
    {
        rttScratch.Clear();     // G-5: 호출자가 스크래치 리스트를 재사용하도록 넘겨받는다(매 호출 new 안 함)

        ulong segmentsSent = 0, retransmit = 0, bytesSent = 0, bytesReceived = 0;
        long fecRecovered = 0;

        foreach (var session in sessions)
        {
            var stats = session.Stats;
            segmentsSent += stats.SegmentsSent;
            retransmit += stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments;
            bytesSent += stats.BytesSent;
            bytesReceived += stats.BytesReceived;
            fecRecovered += session.FecRecoveredPackets;

            if (stats.Srtt > 0) rttScratch.Add(stats.Srtt);
        }
        rttScratch.Sort();

        return new KcpServerMetrics
        {
            TimestampMs = now,
            SessionCount = sessions.Count,
            SegmentsSent = segmentsSent,
            RetransmitSegments = retransmit,
            BytesSent = bytesSent,
            BytesReceived = bytesReceived,
            FecRecoveredPackets = fecRecovered,
            RttP50 = Percentile(rttScratch, 0.50),
            RttP95 = Percentile(rttScratch, 0.95),
            RttP99 = Percentile(rttScratch, 0.99),
        };
    }

    private static int Percentile(List<int> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;

        var rank = (int)Math.Ceiling(percentile * sorted.Count);   // nearest-rank 방식 백분위
        if (rank < 1) rank = 1;
        if (rank > sorted.Count) rank = sorted.Count;
        return sorted[rank - 1];
    }
}
```

`Collect`가 세션 리스트를 매번 정렬해 백분위를 구하는 방식은 세션 수가 많을수록(수천) 비용이
`O(n log n)`이 되지만, 5초에 한 번만 도는 데다 정확한 p50/p95/p99가 관측성의 핵심 지표이므로
근사 알고리즘 대신 정확한 정렬을 택했습니다. `KcpServer.SampleMetrics`가 호출자 쪽에서
`rttScratch`/`metricsScratch` 리스트를 재사용해 이 함수 자체는 인자로 받은 리스트 외에 새
컬렉션을 만들지 않습니다(G-5).

---

## 5. 전체 데이터 흐름

지금까지 파일 단위로 봤다면, 이번엔 **패킷 하나의 일생**을 처음부터 끝까지 따라가 봅니다.

### 송신 — `session.Send(data)`부터 UDP 데이터그램이 나가기까지

```
KcpSession.Send(data)
  └─ kcp.Send(data)                                    [Kcp.cs §3.4.3]
       └─ 세그먼트로 조각화해 snd_queue 에 PushBack

  (다음 Update 틱, 또는 ackNoDelay 로 즉시)
KcpSession.Update(now) → UpdateConnected → kcp.Update() → kcp.Flush(false)   [Kcp.cs §3.4.4/3.4.10]
  └─ snd_queue → snd_buf 이동(윈도 확인)
  └─ snd_buf 순회 → 보낼 세그먼트를 buffer(reserved 만큼 앞을 비워 둠)에 인코딩
  └─ output(buffer, length) 콜백 호출
       │
       ▼
KcpSession.OnKcpOutput(buffer, length)                   [KcpSession.cs §4.5.3]
  ├─ FEC 켜짐?  → fecEncoder.Encode(...) → sender(패킷, remote)  [그룹이 차면 패리티도 함께]
  ├─ 암호화 켜짐? → cipher.Encrypt(payload, frameBuffer) → sender(...)
  └─ 둘 다 아님  → buffer[0] = FrameType.Kcp 만 채워서 그대로 sender(...)
       │
       ▼
KcpServer/KcpClient 의 SendTo(data, remote)               [KcpServer.cs / KcpClient.cs]
       │
       ▼
IUdpTransport.SendTo(data, remote)                        [UdpSocketTransport.cs §4.3]
  └─ socket.SendTo(data, SocketFlags.None, cachedSocketAddress)   ← 무할당
```

### 수신 — UDP 데이터그램이 도착해서 애플리케이션 콜백까지

```
socket 에 패킷 도착
       │
       ▼
KcpServer.ReceiveLoop → DrainSocket                        [KcpServer.cs §4.6]
  └─ udp.TryReceive(receiveBuffer, out received, out remoteKey)   ← 무할당(SocketAddress 재사용)
  └─ sessions.TryGetValue(remoteKey, out session)
       │
       ▼
session.OnFrame(frame, now)                                 [KcpSession.cs §4.5.4]
  └─ Framing.PeekType(frame) 으로 분기
       │  (FrameType.Kcp 인 경우)
       ▼
OnKcpFrame
  ├─ FEC 켜짐?  → fecDecoder.Decode(...) → (복원됐으면 콜백으로 kcp.Input 재귀 호출)
  ├─ 암호화 켜짐? → cipher.TryDecrypt(payload, decryptBuffer) → kcp.Input(plain, ...)
  └─ 둘 다 아님  → kcp.Input(payload, true, ackNoDelay)
       │
       ▼
Kcp.Input(data, ...)                                         [Kcp.cs §3.4.5]
  └─ 세그먼트 파싱 → parse_data (rcv_buf 삽입 → MoveRcvBufToQueue → rcv_queue)
       │
       ▼
KcpSession.DrainReliable()                                   [KcpSession.cs §4.5.5]
  └─ kcp.RecvInPlace(...) [단일 조각, 무복사]  또는  PeekSize+Recv [다조각, 조립 버퍼]
       │
       ▼
사용자 이벤트 핸들러 (ReliableReceivedSpan / ReliableReceived / EnableEventQueue 로 받은 큐)
```

한 가지 강조할 점: **송신 경로에서 FEC/암호화는 KCP 바깥, 프레이밍 안쪽**에 있습니다. 즉 KCP는
FEC나 암호화의 존재를 전혀 모르고, 그저 `output` 콜백에 바이트를 넘길 뿐입니다. FEC와 암호화는
`KcpSession`이 그 바이트를 감싸거나(송신) 벗기는(수신) 책임을 집니다 — 코어의 순수성이 이렇게
지켜집니다.

---

## 6. 스레딩 모델

**규약은 단순합니다: `Kcp` 인스턴스 1개, `KcpSession` 1개는 스레드 1개에서만 만진다.** 코드
전체에서 이 규약이 어떻게 실현되는지 짚어 봅니다.

**코어(`Kcp`)에 `lock (...)` 문이 단 하나도 없습니다.** 파일을 훑어보면 `lock`이라는 단어가
등장하는 곳은 딱 두 군데인데, 둘 다 **주석**("소유 스레드 규약 덕분에 lock이 필요 없다")이지
실제 잠금 코드가 아닙니다. `segmentPool`(Stack), 네 개의 `RingBuffer`, `acklist`(List) 전부 락 없이
씁니다 — 소유 스레드가 하나뿐이라는 전제가 지켜지는 한 안전합니다.

**전송 계층에서 `lock`이 등장하는 유일한 곳은 세션 테이블입니다.**

```csharp
// KcpServer.cs
private readonly Dictionary<AddressKey, KcpSession> sessions = ...;

public IReadOnlyCollection<KcpSession> Sessions
{
    get { lock (sessions) { return new List<KcpSession>(sessions.Values); } }   // 외부 스레드가 조회할 수 있으므로
}
```

`sessions` 딕셔너리는 수신 루프 스레드가 **쓰기**(추가/삭제)를 독점하지만, `KcpServer.Sessions`
프로퍼티나 `TryGetSession`은 어느 스레드에서든 불릴 수 있는 공개 API입니다. 그래서 이 딕셔너리를
건드리는 모든 곳(쓰기든 읽기든)이 같은 `lock (sessions)`를 씁니다. 이건 "소유 스레드 규약을
어기는 예외"가 아니라, **세션 테이블 자체가 여러 스레드가 봐야 하는 명시적 공유 자원**이라는
것을 인정하고 최소한의 락으로 보호한 것입니다. `HandshakeCookie`의 `secret` HMAC 계산에 쓰는
`lock (gate)`도 같은 이유입니다(서버 시작 후 비밀키는 불변이지만, `#else` netstandard2.1
경로에서 `HMACSHA256` 인스턴스가 스레드 안전하지 않아서 보호합니다).

**세션 자체(`KcpSession`, `Kcp`)를 다른 스레드가 건드려야 할 때는 락 대신 큐를 씁니다.** 이게
M-1(송신 아웃박스)과 M-2(수신 이벤트 큐)의 존재 이유입니다.

```
[게임 스레드]                              [수신 루프 스레드 = 소유 스레드]
                                             ┌─────────────────────────┐
session.TrySendFromAnyThread(data)  ──►  Channel  ──► DrainOutbox() (Update 첫머리)
                                             │            └─ kcp.Send(...)  ← 여기서만 실제 Send
                                             │
DrainReliable 이 이벤트 발화     ◄──  events.Publish(...) ──► [소유 스레드에서]
    │
    ▼
events.TryRead(out ev)  (게임 스레드에서 폴링)
```

두 경우 모두 **"큐에 넣기/꺼내기"만 스레드 경계를 넘고, 실제 KCP 상태 변경은 항상 소유
스레드에서** 일어납니다. `Channel<T>`(`System.Threading.Channels`)가 이 경계의 동시성 안전을
보장하는 유일한 지점입니다 — 검증된 lock-free 큐 구현에 기대고, 나머지 코드는 여전히 "스레드
하나"라는 가정 아래 단순하게 짤 수 있습니다.

**`KcpServer.Stop()`이 수신 루프 스레드를 확실히 멈춘 뒤에만 세션을 정리하는 것**도 같은
규약의 연장입니다 — `cts.Cancel()` 후 `loopTask.Wait(...)`로 루프 스레드가 완전히 빠져나온
걸 확인하고 나서야 `session.ReleaseResources()`를 호출합니다. 그렇지 않으면 정리 중인 세션을
루프 스레드가 여전히 건드릴 수 있는 경합이 생깁니다.

---

## 7. 메모리·할당 모델

"정상 상태 송수신 경로 0 B/op"라는 목표가 코드 곳곳의 설계를 결정합니다. 어디서 무엇을
재사용하는지 표로 모았습니다.

| 무엇 | 어떻게 재사용하나 | 관련 파일 |
|---|---|---|
| 세그먼트 객체(`KcpSegment`) | 인스턴스별 `Stack<KcpSegment>`(`segmentPool`) | `Kcp.cs` |
| 세그먼트 페이로드(`byte[]`) | `ArrayPool<byte>.Shared` 대여, `Len` 필드로 유효 길이 구분 | `KcpSegment.cs` |
| 플러시 스테이징 버퍼 | `Kcp.buffer` — 세션당 1개, MTU 크기로 고정 | `Kcp.cs` |
| 복호화 버퍼 | `KcpSession.decryptBuffer` — 세션당 1개, 상주 | `KcpSession.cs` (G-3) |
| 다조각 조립 버퍼 | `KcpSession.assemblyBuffer` — 세션당 1개, 2배씩 성장 | `KcpSession.cs` (G-2) |
| 무복사 단일 조각 수신 | 세그먼트 페이로드를 콜백에 직접 전달, 복사 자체가 없음 | `Kcp.RecvInPlace` (G-2) |
| 송신 주소(net8.0) | `ConditionalWeakTable<IPEndPoint, SocketAddress>` | `UdpSocketTransport.cs` (G-1) |
| 수신 주소(net8.0) | 재사용 `SocketAddress` 필드 | `UdpSocketTransport.cs` |
| FEC 그룹 샤드 | 그룹당 샤드 버퍼를 생성 시 전부 할당해 재활용 | `FecCodec.cs` (G-4) |
| 리드-솔로몬 복원 스크래치 | 부분 행렬·역행렬 작업 버퍼를 생성 시 1회 할당 | `ReedSolomon.cs` (G-4) |
| 이벤트 스냅숏 리스트 | `KcpServer.metricsScratch`/`rttScratch` — 주기마다 `Clear()`만 | `KcpServer.cs` (G-5) |
| 이벤트 구독 델리게이트 | 서버/클라이언트당 1개씩 캐시, 세션마다 새로 안 만듦 | `KcpServer.cs`/`KcpClient.cs` (G-5) |

**이중 반환 방지 센티널 패턴**이 세 곳(`KcpSegment.Reset`, `KcpEvent.ReturnPayload`)에서
반복됩니다 — 대여한 배열을 반환한 자리에 `Array.Empty<byte>()`나 `null`을 대입해, "이미
반환했는지"를 배열 자체로 판별합니다. `ArrayPool`은 같은 배열이 두 번 반환되는 것을 감지하지
못하므로(디버그 빌드가 아니면), 이 패턴이 없으면 조용한 메모리 오염 버그가 됩니다.

**"필수 복사"와 "제거 가능한 복사"를 구분하는 것**이 이 라이브러리 성능 작업의 반복되는
주제입니다. 다음은 **일부러 손대지 않은** 복사들입니다(`docs/note/performance-improvements.md`
"조사 결과, 손대지 않는 것이 맞는 곳" 절 참고).

- `Kcp.Send`의 사용자 데이터 복사 — 재전송을 위해 라이브러리가 데이터를 소유해야 하므로 필수.
- `Flush`의 세그먼트 → 스테이징 버퍼 복사 — 여러 세그먼트를 한 UDP 데이터그램으로 묶는 배칭
  메커니즘 자체가 복사다.
- `TrySendFromAnyThread`의 복사 1회 — 스레드 경계를 넘기 위한 필수 복사(호출자 버퍼 수명과
  분리해야 함).
- AES-GCM 암호화 출력 — memcpy가 아니라 암호 변환 자체라 복사로 셀 수 없다.

---

## 8. 코드를 확장하려면

**커스텀 암호화기**: `IPacketCipher`를 구현해 `KcpSessionOptions.Cipher`에 주입하면
`AesGcmPacketCipher` 대신 씁니다. netstandard2.1(AesGcm이 없는 런타임)에서 필수입니다.
`Overhead`가 정확해야 합니다 — `KcpSession.CreateKcp`가 이 값으로 `Kcp.SetMtu`를 계산하므로,
실제 암호문이 이보다 커지면 버퍼 오버런이 납니다.

**커스텀 전송**: `IUdpTransport`를 구현하면 실소켓 대신 다른 전송(예: 테스트용 가상 네트워크,
`WebSocket` 위에 UDP를 흉내 내는 어댑터 등)을 끼울 수 있습니다. `KcpServer.Start(IUdpTransport)`
/ `KcpClient.ConnectAsync(IUdpTransport, ...)` 오버로드가 진입점입니다. 최소한 `TryReceive`가
호출당 1패킷 제한 없이 반복 호출될 것을 전제해야 합니다(4.6절 `DrainSocket`처럼 `false`가
나올 때까지 반복해서 부릅니다).

**새 `FrameType` 추가**: `Framing.cs`의 enum에 값을 추가하고 `Write*`/`TryRead*` 쌍을
만든 뒤, `KcpSession.OnFrame`의 `switch`에 분기를 추가합니다. **`0x01~0x08`은 이미 쓰이고
있으므로, 새 타입은 반드시 그 바깥 값을 써야 원본 kcp/kcp-go와 `RawMode` 상호운용이 깨지지
않습니다.**

**코어(`Kcp`) 수정**: 와이어 포맷(24바이트 헤더)과 프로토콜 동작(RTO 계산, 재전송 조건, 윈도
계산)은 **원본과 바이트·타이밍 단위로 호환되어야 합니다.** `tests/KcpCSharpR1.Tests/InteropTests.cs`,
`tests/KcpCSharpR1.SimTests/ScenarioTests.cs`가 이 호환성의 안전망입니다. 자료구조(어떤 컬렉션을
쓰는지)나 메모리 관리(언제 할당하는지)는 자유롭게 바꿔도 되지만, 바꾼 뒤 반드시
`docs/benchmarks.md`의 절차대로 전/후 벤치마크를 남겨야 합니다(로드맵 §6, `CLAUDE.md`의 불변
원칙 3).

---

## 9. 테스트가 이 코드를 검증하는 방법

깊이 들어가지 않고 지도만 그립니다 — 테스트 자체를 깊이 읽고 싶다면 `tests/` 폴더를 직접
열어 보는 게 가장 빠릅니다.

| 테스트 프로젝트 | 방식 | 무엇을 검증하나 |
|---|---|---|
| `KcpCSharpR1.Tests` | 순수 단위 테스트 | 코덱 왕복, `RingBuffer`, `PooledBuffer`, 프레이밍, 암호화, Reed-Solomon, 핸드셰이크 쿠키, 세션 스케줄러 |
| `KcpCSharpR1.SimTests` | 가상 시간 + 가상 네트워크 | 프로토콜 시나리오(손실·지연·중복·재정렬), 세션 상태 전이, 서버 한도, 실소켓 통합 |

**가상 시간 하네스(`KcpPair`, `tests/KcpCSharpR1.SimTests/KcpPair.cs`)**가 핵심 도구입니다.
`Kcp` 두 개를 `SimulatedLink`(설정 가능한 손실률·지연·중복률·재정렬률)로 연결하고, 실제
`Thread.Sleep` 없이 가상 시각을 직접 진행시켜(`AdvanceTo`) 수백만 ms짜리 시나리오를 순식간에,
**결정적으로**(같은 시드면 항상 같은 결과) 재현합니다. `Kcp`가 시계를 주입받는 설계
([3.4절](#34-kcpcs--프로토콜-상태-머신))가 없었다면 이런 테스트는 불가능했을 것입니다 — 이게
"코어는 소켓·스레드·시계를 모른다"는 원칙이 실제로 값어치를 하는 지점입니다.

`VirtualNetwork`/`VirtualTransport`(`tests/KcpCSharpR1.SimTests/VirtualNetwork.cs`)가 같은
아이디어를 전송 계층까지 확장합니다 — `IUdpTransport`를 구현한 가상 소켓으로, `KcpServer`/
`KcpSession`의 핸드셰이크·세션 수명·재접속을 실제 네트워크 없이 검증합니다.

---

## 10. 더 읽을 거리

- **개념 위주로 다시 보고 싶다면** — 책 [2장 ARQ 원리](book/ch02-arq.md),
  [3장 KCP 프로토콜 해부](book/ch03-kcp-anatomy.md), [5장 코어 API](book/ch05-core-api.md),
  [6장 세션 수명](book/ch06-session.md)
- **API 시그니처만 빠르게 찾고 싶다면** — [부록 A: API 레퍼런스](book/appendix-a-api.md)
- **성능 개선의 배경과 벤치마크 수치** — `docs/note/performance-improvements.md`,
  `docs/benchmarks.md`
- **구현 지시서(작업 단위로 무엇을 왜 했는지)** — `docs/note/kcp-csharp-roadmap.md`
- **원본과의 상호운용** — 책 [부록 C](book/appendix-c-interop.md)
- **보안·FEC를 실전에 적용하는 법** — 책 [15장 보안](book/ch15-security.md),
  [16장 FEC](book/ch16-fec.md)

이 문서에서 설명이 부족하거나 실제 코드와 어긋난 부분을 발견하면, 그건 코드가 바뀌었는데 이
문서가 못 따라간 것일 가능성이 높습니다 — `CONTRIBUTING.md`를 참고해 이슈나 PR로 알려 주세요.
