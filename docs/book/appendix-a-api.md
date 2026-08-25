# 부록 A. API 레퍼런스

공개 타입 전체를 한 곳에 모았습니다. 자세한 설명은 각 장을 참조하세요.

> 소스의 XML 문서 주석이 단일 기준입니다. 이 문서와 다르면 소스가 맞습니다.

## 네임스페이스

| 네임스페이스 | 어셈블리 | 내용 |
|---|---|---|
| `KcpProject` | `KcpProject` | 프로토콜 코어. **의존성 0** |
| `KcpProject.Transport` | `KcpProject.Transport` | 소켓·세션·핸드셰이크 |
| `KcpProject.Transport.Fec` | `KcpProject.Transport` | FEC(리드-솔로몬) |

---

## `KcpProject.Kcp` — 프로토콜 코어

→ [5장](ch05-core-api.md)

### 생성

```csharp
public Kcp(uint conv_, Action<byte[], int> output_, Func<uint>? currentTimeMs = null)
```

| 인자 | 설명 |
|---|---|
| `conv_` | 대화 번호. 양 끝이 같아야 합니다 |
| `output_` | 패킷 송신 콜백. 버퍼는 호출 후 재사용되므로 즉시 보내거나 복사해야 합니다 |
| `currentTimeMs` | 밀리초 시계. 생략하면 `KcpClock.NowMs` |

### 데이터

| 시그니처 | 반환 |
|---|---|
| `int Send(byte[] buffer)` | 0 성공 / -1 길이 0 / -2 조각 수 초과 |
| `int Send(byte[] buffer, int index, int length)` | 동일 |
| `int Send(ReadOnlySpan<byte> data)` | 동일(무할당) |
| `int Recv(byte[] buffer)` | 읽은 바이트 / -1 없음 / -2 버퍼 부족 |
| `int Recv(byte[] buffer, int index, int length)` | 동일 |
| `int Recv(Span<byte> buffer)` | 동일(무할당) |
| `int PeekSize()` | 다음 메시지 크기 / -1 없음 |
| `int Input(byte[] data, int index, int size, bool regular, bool ackNoDelay)` | 0 / -1 / -2 / -3 |
| `int Input(ReadOnlySpan<byte> data, bool regular, bool ackNoDelay)` | 동일(무할당) |

### 구동

| 시그니처 | 설명 |
|---|---|
| `void Update()` | 재전송·ACK·플러시 수행. 주기적으로 호출 |
| `uint Check()` | 다음 `Update()` 시각. **반드시 `Itimediff` 로 비교** |
| `uint Flush(bool ackOnly)` | 즉시 내보내기. 반환값은 다음 재전송까지 남은 최소 시간 |
| `void Release()` | 빌린 버퍼를 `ArrayPool` 에 반환. 세션 종료 시 |

### 설정

| 시그니처 | 설명 |
|---|---|
| `int NoDelay(int nodelay, int interval, int resend, int nc)` | → [8장](ch08-tuning.md) |
| `int WndSize(int sndwnd, int rcvwnd)` | 송·수신 윈도(세그먼트 수) |
| `int SetMtu(int mtu)` | MTU. 기본 1400 |
| `bool ReserveBytes(int size)` | 패킷 앞에 상위 계층용 예약 영역 확보 |
| `void SetStreamMode(bool enabled)` | 스트림 모드 on/off |
| `void SetLogger(Action<string>? logger)` | 로거 |
| `void SetLogMask(int mask)` | `IKCP_LOG_*` 조합 |

### 조회

| 속성 | 설명 |
|---|---|
| `uint Conv` | 대화 번호 |
| `int Srtt` | 평활 RTT(ms). **게임의 핑 표시에 그대로 사용 가능** |
| `int RttVar` | RTT 변동 폭 |
| `uint Rto` | 현재 재전송 타임아웃(ms) |
| `uint Interval` | 내부 갱신 간격 |
| `uint SndWnd` / `RcvWnd` / `RmtWnd` | 송신·수신·상대 윈도 |
| `uint Mss` | 세그먼트 최대 페이로드 |
| `uint State` | 내부 상태. 데드링크면 `IKCP_STATE_DEAD_LINK` |
| `bool IsDeadLink` | 20회 재전송해도 확인 못 받음 |
| `int WaitSnd` | 대기 중인 세그먼트 수 |
| `uint CurrentMS` | 주입된 시계의 현재 시각 |
| `KcpStats GetStats()` | 통계 스냅숏 |

### 정적

| 시그니처 | 설명 |
|---|---|
| `static int Itimediff(uint later, uint earlier)` | **랩어라운드 안전한 시각 비교** |
| `static int ikcp_encode8u/16u/32u(...)` | 리틀엔디언 인코딩(호환 유지용) |
| `static int ikcp_decode8u/16u/32u(...)` | 디코딩 |

### 상수(일부)

| 상수 | 값 |
|---|---|
| `IKCP_MTU_DEF` | 1400 |
| `IKCP_OVERHEAD` | 24 |
| `IKCP_WND_SND` / `IKCP_WND_RCV` | 32 |
| `IKCP_RTO_NDL` / `IKCP_RTO_MIN` / `IKCP_RTO_DEF` / `IKCP_RTO_MAX` | 30 / 100 / 200 / 60000 |
| `IKCP_CMD_PUSH` / `ACK` / `WASK` / `WINS` | 81 / 82 / 83 / 84 |
| `IKCP_DEADLINK` | 20 |

---

## `KcpProject.KcpStats`

→ [5장](ch05-core-api.md)

| 필드 | 설명 |
|---|---|
| `BytesSent` / `BytesReceived` | 총 바이트(헤더 포함) |
| `SegmentsSent` / `SegmentsReceived` | 총 세그먼트 |
| `RetransmitSegments` | RTO 만료 재전송 |
| `FastRetransmitSegments` | 빠른 재전송 |
| `EarlyRetransmitSegments` | 조기 재전송 |
| `RepeatSegments` | 중복 수신 |
| `CurrentRto` / `Srtt` | 현재 RTO / 평활 RTT |
| `SendWindowUsage` / `WaitSend` | 송신 버퍼 사용량 / 총 대기 |

---

## `KcpProject.KcpClock`

| 멤버 | 설명 |
|---|---|
| `static uint NowMs` | 단조(monotonic) 밀리초. **`DateTime` 을 쓰지 않습니다** |

---

## `KcpProject.Transport.KcpClient`

→ [4장](ch04-getting-started.md), [6장](ch06-session.md)

```csharp
public KcpClient(KcpSessionOptions? options = null, Func<uint>? clock = null)
```

| 멤버 | 설명 |
|---|---|
| `Task<KcpSession> ConnectAsync(string host, int port, CancellationToken ct = default)` | DNS는 IPv4 우선 |
| `Task<KcpSession> ConnectAsync(IPEndPoint remote, CancellationToken ct = default)` | |
| `Task<KcpSession> ConnectAsync(IUdpTransport transport, IPEndPoint remote, …)` | 가상 전송 주입(테스트) |
| `void Update()` | **폴링형 구동.** 매 프레임 호출 |
| `Task RunAsync(CancellationToken ct = default)` | 자체 루프형 구동 |
| `void Disconnect(byte reason = 0)` | FIN 송신 |
| `KcpSession? Session` | 연결된 세션 |
| `bool IsConnected` | |
| `IPEndPoint? LocalEndPoint` | |

이벤트: `Connected`, `Disconnected`, `ReliableReceived`, `UnreliableReceived`

> **구동 모델을 섞지 마세요.** `RunAsync` 중에 `Update()` 를 부르면 예외가 납니다.

---

## `KcpProject.Transport.KcpServer`

→ [6장](ch06-session.md), [17장](ch17-operations.md)

```csharp
public KcpServer(int port, KcpServerOptions? options = null, Func<uint>? clock = null)
```

| 멤버 | 설명 |
|---|---|
| `void Start()` | 소켓 열고 전용 수신 스레드 시작 |
| `void Start(IUdpTransport? transport)` | 전송 계층 주입 |
| `void Start(IUdpTransport? transport, bool runLoop)` | `runLoop: false` 면 직접 `Pump()` |
| `void Pump()` | 수신·갱신 1회(비블로킹) |
| `void Stop()` | |
| `bool TryGetSession(IPEndPoint remote, out KcpSession session)` | |
| `int SessionCount` | |
| `IReadOnlyCollection<KcpSession> Sessions` | 스냅숏(복사본) |
| `IPEndPoint? LocalEndPoint` | |

이벤트: `SessionConnected`, `SessionDisconnected`, `ReliableReceived`, `UnreliableReceived`, `Error`, `MetricsSampled`

> **이벤트는 수신 루프 스레드에서 발화합니다.**

---

## `KcpProject.Transport.KcpSession`

→ [6장](ch06-session.md)

| 멤버 | 설명 |
|---|---|
| `KcpSendResult Send(ReadOnlySpan<byte> data)` | 신뢰 채널 |
| `KcpSendResult SendUnreliable(ReadOnlySpan<byte> data)` | 비신뢰 채널 |
| `int Receive(Span<byte> buffer)` | 폴링 수신(신뢰) |
| `int ReceiveUnreliable(Span<byte> buffer)` | 폴링 수신(비신뢰) |
| `int PeekSize()` | 다음 신뢰 메시지 크기 |
| `void Disconnect(byte reason = 0)` | FIN |
| `uint Update(uint now)` | 타이머 구동. 다음 갱신 시각 반환 |
| `void OnFrame(ReadOnlySpan<byte> frame, uint now)` | 프레임 처리 |
| `void StartConnect(uint now)` | 클라이언트 핸드셰이크 시작 |

| 속성 | 설명 |
|---|---|
| `uint Conv` | 서버가 할당한 번호 |
| `KcpSessionState State` | `Closed`/`Connecting`/`Connected`/`Closing`/`Dead` |
| `IPEndPoint RemoteEndPoint` / `AddressKey RemoteKey` | |
| `int PingMs` | PING/PONG 왕복. 측정 전이면 -1 |
| `int SrttMs` | KCP의 평활 RTT |
| `KcpStats Stats` | |
| `Kcp? Kcp` | 내부 인스턴스(고급 조회) |
| `object? UserState` | 애플리케이션용 태그 |
| `byte CloseReason` | 상대가 보낸 FIN의 reason |
| `long FecRecoveredPackets` | FEC 복원 수 |
| `bool IsEncrypted` | |

### `KcpSendResult`

| 값 | 뜻 |
|---|---|
| `Ok` | 큐에 적재 |
| `NotConnected` | 연결 상태가 아님 |
| `Backpressure` | 백로그 상한 초과. **메시지는 버려집니다** |
| `TooLarge` | 최대 크기 초과 |
| `Error` | 그 밖의 오류 |

### `KcpDisconnectReason`

`RemoteClosed` / `LocalClosed` / `Timeout` / `DeadLink` / `HandshakeFailed`

---

## `KcpProject.Transport.KcpSessionOptions`

→ [6장](ch06-session.md), [8장](ch08-tuning.md)

| 속성 | 기본값 |
|---|---|
| `HandshakeTimeoutMs` | 5000 |
| `HandshakeRetryIntervalMs` | 1000 |
| `HandshakeMaxRetries` | 3 |
| `PingIntervalMs` | 2000 |
| `SessionTimeoutMs` | 10000 |
| `FinLingerMs` | 2000 |
| `MaxSendBacklog` | 1024 |
| `NoDelay` / `IntervalMs` / `FastResend` / `NoCongestionControl` | 1 / 10 / 2 / 1 |
| `SendWindow` / `ReceiveWindow` | 128 / 128 |
| `Mtu` | 1400 |
| `AckNoDelay` | true |
| `StreamMode` | false |
| `RawMode` / `RawModeConv` | false / 0 |
| `EncryptionKey` | null(32바이트) |
| `Cipher` | null(`IPacketCipher`) |
| `Fec` | null(`FecOptions`) |

## `KcpProject.Transport.KcpServerOptions`

| 속성 | 기본값 |
|---|---|
| `Session` | `new KcpSessionOptions()` |
| `MaxSessions` | 8192 |
| `PollMicroseconds` | 5000 |
| `MaxReceiveBatch` | 1024 |
| `SocketReceiveBufferSize` / `SocketSendBufferSize` | 1MB / 1MB |
| `MetricsIntervalMs` | 5000 |

---

## `KcpProject.Transport.Framing`

→ [부록 C](appendix-c-interop.md)

프레임 타입: `Syn`(0x01) / `SynCookie`(0x02) / `SynAck`(0x03) / `Kcp`(0x04) /
`Raw`(0x05) / `Ping`(0x06) / `Pong`(0x07) / `Fin`(0x08)

전부 정적 `Write*` / `TryRead*` 쌍입니다.

## `KcpProject.Transport.HandshakeCookie`

→ [6장](ch06-session.md), [15장](ch15-security.md)

| 멤버 | 설명 |
|---|---|
| `HandshakeCookie()` | 무작위 비밀키 생성 |
| `HandshakeCookie(byte[] serverSecret)` | 비밀키 지정(다중 서버 공유) |
| `uint Compute(AddressKey remote, long unixSeconds)` | 쿠키 발급 |
| `bool Validate(AddressKey remote, uint cookie, long unixSeconds)` | 현재·직전 버킷 허용 |
| `const int BucketSeconds` | 30 |

## `KcpProject.Transport.AesGcmPacketCipher`

→ [15장](ch15-security.md)

| 멤버 | 설명 |
|---|---|
| `AesGcmPacketCipher(byte[] key, uint conv, bool serverDirection)` | 키 32바이트 |
| `int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination)` | |
| `bool TryDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> destination, out int length)` | 변조·리플레이면 false |
| `const int OverheadBytes` | 24(카운터 8 + 태그 16) |
| `const int KeySize` | 32 |

> netstandard2.1 런타임에는 `AesGcm` 이 없습니다. `IPacketCipher` 를 직접 구현해 주입하세요.

## `KcpProject.Transport.IUdpTransport`

| 멤버 | 설명 |
|---|---|
| `void Bind(IPEndPoint local)` | |
| `int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)` | |
| `bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)` | 비블로킹 |
| `bool WaitForData(int microseconds)` | |
| `IPEndPoint? LocalEndPoint` | |

구현: `UdpSocketTransport`(실소켓). 테스트용 가상 구현은 `tests/KcpProject.SimTests/VirtualNetwork.cs`.

## `KcpProject.Transport.Fec.FecOptions`

→ [16장](ch16-fec.md)

| 속성 | 기본값 |
|---|---|
| `DataShards` | 10 |
| `ParityShards` | 3 |

## `KcpProject.Transport.KcpServerMetrics`

→ [17장](ch17-operations.md)

`TimestampMs` / `SessionCount` / `SegmentsSent` / `RetransmitSegments` /
`RetransmitRatePercent` / `RttP50` / `RttP95` / `RttP99` / `BytesSent` / `BytesReceived` /
`FecRecoveredPackets`

---

## 레거시

| 타입 | 상태 |
|---|---|
| `KcpProject.KCP` | `[Obsolete]` — `Kcp` 로 이름 변경 |
| `KcpProject.UDPSession` | `[Obsolete]` — `KcpClient` 로 대체 |

다음 메이저 버전에서 제거됩니다.
