# 샘플 ③ Gomoku(오목) `★3 중급`

매칭·턴 검증·재접속이 있는 2인 오목. **권위 서버(authoritative server) 패턴의 교과서**이자
MonoGame 클라이언트가 처음 등장하는 샘플이다.

## 실행

터미널 3개.

```bash
dotnet run --project samples/03-Gomoku/Server
dotnet run --project samples/03-Gomoku/Client -- 127.0.0.1 alice
dotnet run --project samples/03-Gomoku/Client -- 127.0.0.1 bob
```

UDP **27003** 포트. 두 명이 모이면 자동으로 매칭된다.

| 조작 | 동작 |
|---|---|
| 마우스 클릭 | 교점에 돌 놓기 |
| `R` | 항복 |
| `ESC` | 종료 |

## 배우는 것

- **권위 서버**: 판의 상태는 서버만 소유한다. 클라이언트는 "여기 두고 싶다"고 **요청**할 뿐이다.
- 요청 → 검증 → 알림 사이클. 서버는 차례·범위·빈칸을 전부 다시 본다.
- **재접속**: 창을 닫아도 30초 안에 토큰으로 돌아오면 판이 이어진다.
- **MonoGame 기초**: `Game.Update()` 에서 네트워크 폴링, `SpriteBatch` 로 보드 렌더링, 마우스 입력.

## MonoGame 과 KCP 를 잇는 법

```csharp
protected override void Update(GameTime gameTime)
{
    network.Poll();   // ★ 이 한 줄이 전부다 (내부에서 KcpClient.Update() 호출)
    ...
}
```

수신 콜백이 **게임 스레드에서** 발화하므로 렌더링 상태를 lock 없이 직접 고쳐도 안전하다.
서버는 반대로 전용 수신 스레드를 쓴다 — 두 모델의 차이는 책 6장에서 다룬다.

> 이 샘플은 **콘텐츠 파이프라인(.mgcb)을 쓰지 않는다.** 보드와 돌은 도형으로,
> 글자는 코드에 넣은 5×7 비트맵 폰트(`TinyFont.cs`)로 그린다. 에셋 없이 `dotnet run` 만으로 돌아간다.
> 실무에서는 `SpriteFont` 를 쓰는 편이 낫다(부록 D).

## 프로토콜

프레이밍은 샘플 ②와 같다(`[u16 len][u8 type][payload]`).

| 방향 | 타입 | 페이로드 |
|---|---|---|
| C→S | `0x01` JoinQueue | nick |
| C→S | `0x02` PlaceStone | x(u8), y(u8) |
| C→S | `0x03` Surrender | — |
| C→S | `0x04` Reconnect | token |
| C→S | `0x05` Chat | text |
| C→S | `0x06` Spectate | nick |
| S→C | `0x81` MatchFound | roomId(u32), myColor(u8), oppNick, token |
| S→C | `0x82` StonePlaced | x(u8), y(u8), color(u8) |
| S→C | `0x83` TurnChanged | turnColor(u8) |
| S→C | `0x84` GameOver | result(u8), winner(u8) |
| S→C | `0x85` StateSync | board(225B), turn(u8), myColor(u8), finished(u8), winner(u8) |
| S→C | `0x86` ChatFrom | nick, text |
| S→C | `0x87` QueueStatus | 대기 인원(u16) |
| S→C | `0x88` OpponentDisconnected | 남은 초(u8) |
| S→C | `0x89` OpponentReconnected | — |
| S→C | `0x8F` Error | code(u8), message |

**StateSync 가 225바이트인 이유**: 15×15 판을 칸당 1바이트로 통째로 보낸다. 재접속·관전처럼
"처음부터 다시 맞춰야 하는" 순간에는 증분(diff)보다 전체 스냅숏이 단순하고 안전하다.

## 코드 지도

| 파일 | 역할 |
|---|---|
| `Shared/GomokuBoard.cs` | 판·규칙·판정. **서버와 클라이언트가 같은 코드를 쓴다** |
| `Shared/Protocol.cs`, `MessageCodec.cs` | 메시지 정의와 코덱 |
| `Server/GameRoom.cs` | 한 판의 상태·검증·브로드캐스트 |
| `Server/GameServer.cs` | 대기열 매칭, 방 관리, 재접속 토큰 |
| `Client/NetworkClient.cs` | 폴링 네트워크 계층(게임 스레드에서 발화) |
| `Client/GomokuGame.cs` | MonoGame 렌더링·입력 |
| `Client/TinyFont.cs` | 에셋 없는 5×7 비트맵 폰트 |
| `Tests/GomokuBoardTests.cs` | 가로/세로/대각/육목 무효 등 18종 |

```bash
dotnet test samples/03-Gomoku/Tests
```

## 규칙

정확히 **5목**이어야 승리다. **6목 이상은 승리가 아니다**(육목 무효).
렌주의 금수(삼삼·사사·장목) 규칙은 넣지 않았다 — 연습문제로 남겨 둔다.

## 직접 확인해 볼 것

1. **재접속** — 대국 중 한쪽 클라이언트 창을 닫는다. 상대 화면에 `OPPONENT LOST CONNECTION - WAITING 30S`
   가 뜬다. 화면 우하단의 토큰을 인자로 넘겨 다시 실행하면 판이 그대로 이어진다.
   ```bash
   dotnet run --project samples/03-Gomoku/Client -- 127.0.0.1 alice <토큰>
   ```
2. **30초를 넘기면** 상대가 `OPPONENT LEFT - YOU WIN!` 으로 이긴다.
3. **치트가 통하지 않는지** — `NetworkClient.RequestPlace` 의 클라이언트 측 검사를 지우고
   상대 차례에 돌을 놓아 본다. 서버가 `ERR[NotYourTurn]` 을 돌려준다.

## 책

- 11장 「프로젝트 ③ 오목 — 권위 서버와 턴제 게임」
- 부록 D 「MonoGame 연동 가이드」
