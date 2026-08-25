# 샘플 ② Chat `★2 초급`

닉네임과 귓속말이 있는 콘솔 채팅. **애플리케이션 프로토콜을 손으로 설계**해 보는 샘플이다.

## 실행

터미널을 3개 이상 띄워 본다.

```bash
dotnet run --project samples/02-Chat/Server
dotnet run --project samples/02-Chat/Client -- 127.0.0.1 헝배
dotnet run --project samples/02-Chat/Client -- 127.0.0.1 철수
```

UDP **27002** 포트. 인자는 `[서버IP] [닉네임]` 이며 둘 다 생략할 수 있다.

## 명령

| 입력 | 동작 |
|---|---|
| 아무 글자 | 전체 발언 |
| `/nick <이름>` | 닉네임 변경 |
| `/w <이름> <내용>` | 귓속말 |
| `/who` | 내 회선 상태(RTT·핑·재전송) |
| `/quit` | 종료 |

## 배우는 것

- **길이 접두사 + 타입 프레이밍**을 직렬화 라이브러리 없이 직접 구현
- 서버 측 입력 검증 — "서버는 클라이언트를 믿지 않는다"가 처음 등장하는 곳
- 세션 컬렉션 관리와 브로드캐스트
- 콘솔 입력(블로킹)과 네트워크 폴링 루프를 큐로 잇는 패턴

## 프로토콜

```
[u16 length][u8 type][payload...]      length = 자기 자신을 뺀 뒤쪽 전체 길이
문자열                                  [u16 바이트수][UTF-8]
```

| 방향 | 타입 | 페이로드 |
|---|---|---|
| C→S | `0x01` SetNick | nick |
| C→S | `0x02` Say | text |
| C→S | `0x03` Whisper | targetNick, text |
| S→C | `0x81` Welcome | motd, 접속자 수(u16), nick × n |
| S→C | `0x82` UserJoined / `0x83` UserLeft | nick |
| S→C | `0x84` Chat | nick, text |
| S→C | `0x85` WhisperFrom | fromNick, text |
| S→C | `0x8F` Error | code(u8), message |

타입의 최상위 비트로 방향을 구분한다(`0x00~0x7F` C→S, `0x80~0xFF` S→C). 로그만 봐도 방향을 알 수 있다.

> **길이 접두사가 왜 필요한가?**
> KCP를 메시지 모드로 쓰면 경계가 보존되므로 사실 `length` 는 없어도 된다.
> 그래도 넣는 이유는 ① 스트림 모드로 바꾸거나 TCP로 옮겨도 코드가 그대로 살고,
> ② 나중에 한 패킷에 여러 메시지를 묶는 최적화를 넣을 수 있기 때문이다.
> 실무 프로토콜은 대부분 이 형태다.

## 코드 지도

| 파일 | 역할 |
|---|---|
| `Shared/Protocol.cs` | 메시지 타입·오류 코드·한도 정의 |
| `Shared/MessageCodec.cs` | `MessageWriter` / `MessageReader` (전부 `Try` 형태) |
| `Server/ChatServer.cs` | 검증·브로드캐스트·귓속말 |
| `Client/Program.cs` | 입력 스레드 → 큐 → 폴링 루프 |
| `Tests/MessageCodecTests.cs` | 코덱 왕복 + 변조 패킷 방어 13종 |

```bash
dotnet test samples/02-Chat/Tests
```

## 직접 확인해 볼 것

1. **중복 닉네임** — 두 클라이언트가 같은 `/nick` 을 시도하면 `오류[NickTaken]` 이 뜬다.
2. **없는 대상에게 귓속말** — `/w 없는사람 안녕` → `오류[TargetNotFound]`.
3. **닉 없이 발언** — 클라이언트를 고쳐 `SetNick` 을 생략하고 `Say` 를 보내면 `오류[NotJoined]`.
   서버가 순서까지 검증한다는 뜻이다.
4. **깨진 패킷** — 코덱 테스트가 무작위 바이트 1,000개를 흘려 넣어도 예외가 없음을 보장한다.

## 책

- 10장 「프로젝트 ② 채팅 — 애플리케이션 프로토콜 설계」
