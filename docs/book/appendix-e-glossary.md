# 부록 E. 용어집

이 책의 용어는 여기를 단일 기준으로 씁니다.

## 프로토콜 기초

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **ARQ** | Automatic Repeat reQuest | "안 왔으면 다시 보내기". 신뢰성 전송의 기본 발상 → [2장](ch02-arq.md) |
| **순서 번호** | sequence number, `sn` | 보낸 것마다 붙이는 번호 |
| **확인 응답** | acknowledgement, ACK | "받았다"는 통지 |
| **누적 확인** | cumulative ACK, `una` | "이 번호 미만은 전부 받았다" |
| **대화 번호** | conversation id, `conv` | 이 연결을 식별하는 4바이트. 양 끝이 같아야 함 → [3장](ch03-kcp-anatomy.md) |
| **조각** | fragment, `frg` | MTU보다 큰 메시지를 쪼갠 단위. 남은 조각 수를 담음 |
| **세그먼트** | segment | KCP의 전송 단위(24바이트 헤더 + 페이로드) |

## 시간과 재전송

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **왕복 지연** | RTT, round-trip time | 보내고 확인이 돌아오기까지의 시간 |
| **평활 RTT** | SRTT, smoothed RTT | 여러 RTT 표본을 평활한 값. `Srtt` 속성 |
| **RTT 편차** | RTTVAR | RTT가 흔들리는 폭 |
| **재전송 타임아웃** | RTO, retransmission timeout | 이만큼 기다려도 확인이 없으면 다시 보냄. `srtt + 4×rttval` |
| **지수 백오프** | exponential backoff | 재전송할 때마다 대기 시간을 늘림(KCP는 1.5배) |
| **빠른 재전송** | fast retransmit | 중복 ACK n회로 손실을 RTO보다 빨리 감지 |
| **조기 재전송** | early retransmit | 보낼 새 데이터가 없을 때 더 공격적으로 재전송 |
| **데드링크** | dead link | 같은 세그먼트를 20회 재전송해도 확인 못 받은 상태 |
| **랩어라운드** | wraparound | `uint` 밀리초가 약 49.7일마다 0으로 돌아가는 것 → [5장](ch05-core-api.md) |

## 윈도와 흐름 제어

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **슬라이딩 윈도** | sliding window | 확인 없이 미리 보낼 수 있는 범위 |
| **송신 윈도** | send window, `snd_wnd` | 내가 한 번에 보낼 의향 |
| **수신 윈도** | receive window, `rcv_wnd` | 상대가 받을 수 있는 양 |
| **혼잡 윈도** | congestion window, `cwnd` | 네트워크가 감당할 것 같은 양 |
| **흐름 제어** | flow control | 받는 쪽이 감당할 만큼만 보내기 |
| **혼잡 제어** | congestion control | 네트워크가 감당할 만큼만 보내기 |
| **윈도 프로브** | window probe, WASK/WINS | 상대 윈도가 0일 때 주기적으로 물어보기 → [3장](ch03-kcp-anatomy.md) |
| **대역폭-지연 곱** | BDP, bandwidth-delay product | `대역폭 × RTT`. 필요한 윈도 크기의 기준 → [8장](ch08-tuning.md) |
| **백프레셔** | backpressure | 큐가 가득 차면 받아 주지 않고 거절하기 → [6장](ch06-session.md) |

## 지연과 손실

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **지연** | latency | 보내고 도착하기까지의 시간 |
| **지터** | jitter | 지연이 들쭉날쭉한 정도 |
| **손실** | packet loss | 패킷이 사라지는 비율 |
| **HOL 블로킹** | head-of-line blocking | 앞의 것이 안 와서 뒤의 것도 못 쓰는 현상 → [1장](ch01-game-and-network.md), [7장](ch07-channels.md) |
| **MTU** | maximum transmission unit | 한 패킷의 최대 크기. 기본 1400, 모바일 1200 |
| **MTU 블랙홀** | MTU black hole | 큰 패킷만 조용히 사라지는 장애 → [17장](ch17-operations.md) |

## 채널

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **신뢰 채널** | reliable channel | 도착·순서를 보장. `Send()` → [7장](ch07-channels.md) |
| **비신뢰 채널** | unreliable channel | 보장 없음. `SendUnreliable()` |
| **메시지 모드** | message mode | 보낸 단위 그대로 도착(경계 보존) |
| **스트림 모드** | stream mode | 경계가 사라짐. 직접 프레이밍 필요 |
| **중복 전송** | redundancy | 같은 데이터를 여러 번 미리 보내 손실을 견딤 → [13장](ch13-tankbattle.md) |

## 세션

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **핸드셰이크** | handshake | 연결을 성립시키는 절차. SYN → SYN_COOKIE → SYN → SYN_ACK |
| **쿠키** | SYN cookie | 서버가 상태를 만들지 않고 발급하는 검증값 → [6장](ch06-session.md) |
| **프레이밍** | framing | 패킷의 종류와 경계를 표시하는 방식 |
| **keepalive** | keepalive | 살아 있음을 알리는 주기적 신호. PING/PONG |
| **NAT 타임아웃** | NAT timeout | 공유기가 UDP 매핑을 지우기까지의 시간 → [17장](ch17-operations.md) |
| **세션 마이그레이션** | session migration | IP가 바뀌어도 세션을 유지하는 것 |

## 게임 동기화

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **권위 서버** | authoritative server | 게임 상태를 서버만 소유하는 구조 → [11장](ch11-gomoku.md) |
| **고정 틱** | fixed tick | 일정 주기로 시뮬레이션을 진행하는 루프 → [12장](ch12-movesync.md) |
| **틱레이트** | tick rate | 초당 틱 수(이 책의 샘플은 20Hz) |
| **스냅숏** | snapshot | 특정 시점의 월드 상태 전체 |
| **보간** | interpolation | 두 스냅숏 사이를 메워 부드럽게 그리기 → [12장](ch12-movesync.md) |
| **외삽** | extrapolation | 마지막 상태에서 미래를 추정해 그리기 |
| **클라이언트 예측** | client-side prediction | 서버 응답을 기다리지 않고 입력을 즉시 반영 → [13장](ch13-tankbattle.md) |
| **보정** | reconciliation | 서버 상태로 되감고 미확인 입력을 재적용 |
| **지연 보상** | lag compensation | 사수가 보던 시점으로 되감아 판정 |
| **되감기** | rewind | 과거 상태로 되돌리는 것 |
| **결정성** | determinism | 같은 입력이면 항상 같은 결과가 나오는 성질. 예측의 전제 |
| **관심 영역** | AOI, area of interest | 각 플레이어에게 보낼 대상을 시야로 제한 |
| **고무줄 현상** | rubber-banding | 예측이 틀려 캐릭터가 되돌아가는 현상 |

## 보안

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **스푸핑** | spoofing | 소스 주소를 위조하는 것 |
| **상태 고갈** | state exhaustion | 서버 메모리를 소진시키는 공격 |
| **증폭 공격** | amplification attack | 작은 요청으로 큰 응답을 유도해 제3자를 공격 |
| **리플레이** | replay | 정상 패킷을 녹음했다가 다시 보내는 공격 |
| **인증 암호화** | AEAD | 암호화와 무결성 검증을 함께 하는 방식(AES-GCM) → [15장](ch15-security.md) |
| **nonce** | nonce | 암호화마다 달라야 하는 값. **재사용은 치명적** |
| **인증 태그** | authentication tag | 변조를 검출하는 16바이트 |
| **슬라이딩 윈도(리플레이)** | anti-replay window | 최근 64개 카운터를 비트마스크로 기억 |

## FEC

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **FEC** | forward error correction | 여분 데이터를 미리 보내 재전송 없이 복구 → [16장](ch16-fec.md) |
| **리드-솔로몬** | Reed-Solomon | 대표적인 소거 부호 |
| **소거 부호** | erasure code | "어느 것이 사라졌는지 아는" 상황의 복구 부호 |
| **샤드** | shard | FEC 그룹의 한 조각 |
| **데이터 샤드 / 패리티 샤드** | data / parity shard | 원본 / 복구용 여분 |
| **유한체** | Galois field, GF(2^8) | 바이트 단위 사칙연산을 정의한 대수 구조 |
| **조직 부호** | systematic code | 데이터 샤드가 변형 없이 그대로 나가는 부호 |

## 성능

| 한국어 | 영어 | 뜻 |
|---|---|---|
| **무할당** | zero-allocation | 정상 상태에서 관리 힙 할당이 0인 것 → [14장](ch14-performance.md) |
| **세대별 GC** | generational GC | Gen0/1/2. 게임에서 문제가 되는 것은 **Gen2** |
| **객체 풀** | object pool | 재사용을 위해 객체를 모아 두는 것 |
| **`ArrayPool`** | — | .NET의 배열 풀. 대여 배열은 요청보다 클 수 있음 |
| **원형 덱** | circular deque, ring buffer | 앞뒤 삽입·삭제가 O(1)인 자료구조 |
| **백분위** | percentile | p50(중앙값), p95, p99. **게임은 p99를 봐야 함** |

## 이 저장소 고유

| 용어 | 뜻 |
|---|---|
| **진단 ID** | 로드맵의 문제 번호. `B-xx`(버그), `PF-xx`(성능), `S-xx`(안정성) |
| **작업 ID** | 로드맵의 작업 번호. `P<페이즈>-<번호>` (예: `P4-2a`) |
| **가상 시계** | 테스트에서 주입하는 시계. 실제 대기 없이 시간을 조종 |
| **`SimulatedLink`** | 지연·손실·중복·순서뒤섞임을 재현하는 가상 링크 |
| **`KcpPair`** | `Kcp` 2개 + 양방향 링크 + 가상 시계를 묶은 테스트 하네스 |
| **골든 벡터** | 와이어 포맷 그대로 저장한 바이트. 상호운용의 근거 |
| **RawMode** | 프레이밍 없이 순수 KCP 패킷만 주고받는 모드 → [부록 C](appendix-c-interop.md) |
