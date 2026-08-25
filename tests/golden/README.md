# 골든 패킷 벡터 (P2-5 상호운용 테스트)

이 디렉터리의 `*.bin` 은 **KCP 와이어 포맷 그대로의 UDP 페이로드**다.
`KcpProject.Tests/InteropTests.cs` 가 이 바이트를 `Kcp.Input` 에 그대로 흘려 넣어,
애플리케이션 데이터가 동일하게 복원되는지 검증한다. 코어 리팩터링(Phase 3)에서
와이어 호환이 깨지면 이 테스트가 먼저 깨진다.

## 파일

| 파일 | 내용 |
|---|---|
| `kcp_push_fragmented.bin` | UDP 패킷 1개에 PUSH 세그먼트 2개(frg=1, frg=0). 앱 메시지 `ABCDEFGHIJKLM` |
| `kcp_push_single.bin` | PUSH 세그먼트 1개(frg=0). 앱 메시지 `KCP` |
| `kcp_control.bin` | ACK + WINS 제어 세그먼트가 한 패킷에 들어 있는 경우 |

공통 헤더 값: `conv = 0x11223344`, `wnd = 32`.

## 현재 벡터의 출처

지금 커밋된 벡터는 **와이어 명세(24바이트 헤더, 리틀엔디언)대로 직접 조립한 것**이며,
`CodecTests.헤더_골든벡터_인코딩이_원본kcp와_일치한다` 로 헤더 배치가 원본 C kcp 와
같은지 별도로 못 박아 두었다. 아래 스크립트로 언제든 재생성할 수 있다.

```python
# tests/golden 생성 스크립트 (python 3)
import struct

def seg(conv, cmd, frg, wnd, ts, sn, una, payload=b''):
    return struct.pack('<IBBHIIII', conv, cmd, frg, wnd, ts, sn, una, len(payload)) + payload

CONV = 0x11223344
PUSH, ACK, WASK, WINS = 81, 82, 83, 84

open('kcp_push_fragmented.bin', 'wb').write(
    seg(CONV, PUSH, 1, 32, 1000, 0, 0, b'ABCDEFGH') +
    seg(CONV, PUSH, 0, 32, 1010, 1, 0, b'IJKLM'))
open('kcp_push_single.bin', 'wb').write(
    seg(CONV, PUSH, 0, 32, 1020, 2, 0, b'KCP'))
open('kcp_control.bin', 'wb').write(
    seg(CONV, ACK, 0, 32, 1000, 0, 0) +
    seg(CONV, WINS, 0, 31, 1030, 0, 0))
```

## 실제 kcp-go 캡처로 교체하는 방법

더 강한 상호운용 근거가 필요하면 아래 절차로 실제 구현이 만들어 낸 바이트를 덤프해
같은 파일 이름으로 덮어쓴다. 파일 이름과 기대 앱 데이터만 유지하면 테스트는 그대로 통과해야 한다.

```go
// kcp-go 예제에 덤프 훅을 넣는다 (github.com/xtaci/kcp-go)
// output 콜백에서 바이트를 그대로 파일에 쓴다.
kcp := kcp.NewKCP(0x11223344, func(buf []byte, size int) {
    f, _ := os.OpenFile("kcp_push_fragmented.bin", os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
    defer f.Close()
    f.Write(buf[:size])
})
kcp.WndSize(32, 32)
kcp.Send([]byte("ABCDEFGHIJKLM"))  // mss 를 8로 줄여 두면 2조각이 된다
kcp.Update(1000)
```

원본 C kcp(`ikcp.c`)로도 동일하게 `ikcp_setoutput` 훅에서 덤프하면 된다.

> 주의: 캡처본으로 교체할 때 `ts` 값은 구현의 시계에 따라 달라진다. 테스트는 `ts` 를
> 검증하지 않고 **복원된 애플리케이션 데이터**만 검증하므로 문제되지 않는다.
