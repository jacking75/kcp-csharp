# 부록 C. 원본 kcp / kcp-go 상호운용

이 라이브러리의 **코어는 원본 C kcp / kcp-go 와 바이트 단위로 호환**됩니다.
즉 C# 클라이언트가 Go 서버에 붙을 수 있고, 그 반대도 됩니다.

## 무엇이 호환되고 무엇이 안 되나

| 계층 | 호환 | 비고 |
|---|---|---|
| **KCP 코어**(24바이트 헤더) | ✅ | 원본과 동일 |
| 프레이밍(0x01~0x08) | ❌ | 이 라이브러리 고유 |
| 핸드셰이크(쿠키) | ❌ | 고유 |
| PING/PONG, FIN | ❌ | 고유 |
| FEC 헤더 | ✅ | kcp-go 포맷과 동일 |
| 암호화 | ❌ | kcp-go는 다른 방식 |

**코어만 호환**입니다. 그래서 `RawMode` 가 있습니다.

## RawMode

프레이밍을 벗기고 **순수 KCP 패킷만** 주고받습니다.

```csharp
var options = new KcpSessionOptions
{
    RawMode = true,
    RawModeConv = 0x11223344,   // 상대와 미리 약속한 값
};

using var client = new KcpClient(options);
```

```csharp
// src/KcpCSharpR1.Transport/KcpSessionOptions.cs
/// <summary>
/// true 면 프레이밍 없이 순수 KCP 패킷만 주고받는다(kcp-go 등과 상호운용).
/// 이 경우 핸드셰이크·PING·FIN 이 없으므로 conv 를 직접 지정해야 한다.
/// </summary>
public bool RawMode { get; set; }
```

### RawMode에서 직접 해야 하는 것

| 항목 | 대안 |
|---|---|
| `conv` 합의 | 로그인 API에서 받아 오거나 고정값 |
| 연결 확인 | 애플리케이션 레벨 hello 메시지 |
| keepalive | 애플리케이션 레벨 ping |
| 종료 통지 | 애플리케이션 레벨 bye 메시지 |
| 비신뢰 채널 | **없습니다**(RAW 프레임이 프레이밍의 일부) |

RawMode는 "기존 서버에 붙어야 한다"는 제약이 있을 때 쓰는 것이고,
새로 만든다면 기본 모드가 훨씬 편합니다.

## 파라미터 대응표

| 이 라이브러리 | 원본 C kcp | kcp-go |
|---|---|---|
| `NoDelay(nodelay, interval, resend, nc)` | `ikcp_nodelay(kcp, ...)` | `SetNoDelay(...)` |
| `WndSize(snd, rcv)` | `ikcp_wndsize(kcp, ...)` | `SetWindowSize(...)` |
| `SetMtu(mtu)` | `ikcp_setmtu(kcp, mtu)` | `SetMtu(mtu)` |
| `SetStreamMode(bool)` | `kcp->stream = 1` | `SetStreamMode(bool)` |
| `Send(data)` | `ikcp_send(kcp, buf, len)` | `Send(b []byte)` |
| `Recv(buffer)` | `ikcp_recv(kcp, buf, len)` | `Recv(b []byte)` |
| `Input(data, regular, ackNoDelay)` | `ikcp_input(kcp, data, size)` | `Input(data, regular, ackNoDelay)` |
| `Update()` | `ikcp_update(kcp, current)` | `Update()` |
| `Check()` | `ikcp_check(kcp, current)` | `Check()` |
| `Flush(ackOnly)` | `ikcp_flush(kcp)` | `flush(ackOnly)` |
| `ReserveBytes(n)` | — | `ReserveBytes(n)` |

### 시각 처리의 차이

원본 C kcp는 `ikcp_update(kcp, current)` 처럼 **현재 시각을 인자로 받습니다.**
이 라이브러리는 **생성자에 시계를 주입**합니다.

```csharp
var kcp = new Kcp(conv, output, () => myClock.NowMs);
kcp.Update();   // 시각 인자 없음
```

가상 시계를 주입해 테스트할 수 있다는 것이 장점입니다(5장).
C 코드를 포팅한다면 `ikcp_update(kcp, t)` → `clock = () => t; kcp.Update();` 로 옮기면 됩니다.

## 골든 벡터

`tests/golden/` 에 와이어 포맷 그대로의 바이트가 있습니다.
코어를 리팩터링해도 이 바이트가 바뀌면 테스트가 먼저 깨집니다.

| 파일 | 내용 |
|---|---|
| `kcp_push_fragmented.bin` | UDP 패킷 1개에 PUSH 세그먼트 2개(frg=1, frg=0). 앱 메시지 `ABCDEFGHIJKLM` |
| `kcp_push_single.bin` | PUSH 세그먼트 1개. 앱 메시지 `KCP` |
| `kcp_control.bin` | ACK + WINS 제어 세그먼트 |

공통 헤더: `conv = 0x11223344`, `wnd = 32`.

```csharp
// tests/KcpCSharpR1.Tests/InteropTests.cs
[Fact]
public void 골든_조각패킷이_원본_메시지로_복원된다()
{
    var packet = LoadGolden("kcp_push_fragmented.bin");
    var kcp = NewPeer();

    Assert.Equal(0, kcp.Input(packet, 0, packet.Length, true, false));

    var size = kcp.PeekSize();
    Assert.Equal(13, size);

    var buf = new byte[size];
    Assert.Equal(13, kcp.Recv(buf));
    Assert.Equal("ABCDEFGHIJKLM", Encoding.ASCII.GetString(buf));
}
```

### 재생성

현재 벡터는 **와이어 명세대로 직접 조립한 것**입니다.
`tests/golden/README.md` 에 재생성 스크립트가 있습니다.

```python
import struct

def seg(conv, cmd, frg, wnd, ts, sn, una, payload=b''):
    return struct.pack('<IBBHIIII', conv, cmd, frg, wnd, ts, sn, una, len(payload)) + payload

CONV = 0x11223344
PUSH, ACK, WASK, WINS = 81, 82, 83, 84

open('kcp_push_fragmented.bin', 'wb').write(
    seg(CONV, PUSH, 1, 32, 1000, 0, 0, b'ABCDEFGH') +
    seg(CONV, PUSH, 0, 32, 1010, 1, 0, b'IJKLM'))
```

### 실제 kcp-go 캡처로 교체하기

더 강한 근거가 필요하면 실제 구현이 만든 바이트를 덤프해 같은 이름으로 덮어씁니다.

```go
// kcp-go 예제에 덤프 훅을 넣는다 (github.com/xtaci/kcp-go)
kcp := kcp.NewKCP(0x11223344, func(buf []byte, size int) {
    f, _ := os.OpenFile("kcp_push_fragmented.bin", os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
    defer f.Close()
    f.Write(buf[:size])
})
kcp.WndSize(32, 32)
kcp.Send([]byte("ABCDEFGHIJKLM"))
kcp.Update(1000)
```

원본 C kcp(`ikcp.c`)도 `ikcp_setoutput` 훅에서 같은 방식으로 덤프할 수 있습니다.

> 테스트는 `ts` 를 검증하지 않고 **복원된 애플리케이션 데이터**만 검증하므로,
> 구현마다 시계가 달라도 문제되지 않습니다.

## FEC 상호운용

FEC 헤더는 kcp-go 포맷과 동일합니다.

```
[seqid u32][flag u16]
데이터 샤드는 뒤에 [size u16][페이로드...]
```

| flag | 뜻 |
|---:|---|
| `0xF1` | 데이터 샤드 |
| `0xF2` | 패리티 샤드 |

리드-솔로몬 파라미터도 같습니다.

| 항목 | 값 |
|---|---|
| 유한체 | GF(2^8) |
| 기약 다항식 | `0x11D` (`x^8 + x^4 + x^3 + x^2 + 1`) |
| 생성원 | 2 |
| 행렬 | 반데르몬드 → 조직 부호 |

klauspost/reedsolomon(kcp-go 가 쓰는 구현)과 같은 설정이라 산출 바이트가 호환됩니다.

## 암호화는 호환되지 않습니다

kcp-go는 여러 블록 암호(AES, Salsa20, TEA…)를 CFB 모드 등으로 지원합니다.
이 라이브러리는 **AES-256-GCM 인증 암호화**를 씁니다(15장).

호환이 필요하면 `IPacketCipher` 를 구현해 kcp-go와 같은 방식을 만들어 주입하세요.

```csharp
public interface IPacketCipher
{
    int Overhead { get; }
    int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination);
    bool TryDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> destination, out int plaintextLength);
}
```

> 다만 kcp-go의 기본 방식 중 일부는 **인증이 없습니다**(CFB 단독).
> 변조를 막지 못하므로 새로 만든다면 권하지 않습니다(15장).

## 상호운용 체크리스트

- [ ] `conv` 값을 양쪽이 합의했는가
- [ ] `NoDelay` 인자 4개가 같은가(다르면 동작은 하지만 성능이 달라집니다)
- [ ] `WndSize` 가 같거나, 최소한 상대의 수신 윈도를 넘는 메시지를 안 보내는가
- [ ] `SetStreamMode` 가 양쪽 같은가 (**다르면 데이터가 깨집니다**)
- [ ] MTU가 양쪽 같거나, 작은 쪽에 맞췄는가
- [ ] `ReserveBytes` 를 썼다면 양쪽 같은가
