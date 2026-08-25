# 샘플 ① Echo `★1 입문`

접속해서 보낸 것을 그대로 받는다. **첫 성공 경험**을 위한 최소 샘플이다.

## 실행

터미널 두 개를 띄운다.

```bash
dotnet run --project samples/01-Echo/Server     # 먼저
dotnet run --project samples/01-Echo/Client     # 나중
```

UDP **27001** 포트를 쓴다. 다른 PC에서 접속하려면 `dotnet run --project samples/01-Echo/Client -- 192.168.0.10` 처럼 서버 IP를 인자로 준다.

## 배우는 것

- `KcpClient.ConnectAsync` → `Update` 루프 → `Send` / 수신 이벤트 → `Disconnect` 의 최소 사이클
- `KcpServer` 는 이벤트 3개(`SessionConnected` / `ReliableReceived` / `SessionDisconnected`)만 구독하면 완성된다
- **`Update()` 를 부르지 않으면 아무 일도 일어나지 않는다** — KCP는 스스로 돌지 않는 타이머 주도 프로토콜이다

## 코드 지도

| 파일 | 줄수 | 역할 |
|---|---:|---|
| `Server/Program.cs` | ~65 | 에코 서버 전부 |
| `Client/Program.cs` | ~110 | 1초마다 전송 + 에코 검증 + 정상 종료 |

## 직접 확인해 볼 것

1. **무결성** — 클라이언트를 60초 두면 `에코 불일치` 가 한 줄도 나오지 않아야 한다.
2. **강제 종료 감지** — 클라이언트를 Ctrl+C 가 아니라 **창을 닫아** 죽여 본다. 서버는 10초 안에
   `종료 ... (Timeout)` 을 찍는다. UDP에는 연결 끊김이라는 개념이 없으므로, 이 타임아웃은
   라이브러리가 PING/PONG 으로 만들어 낸 것이다.
3. **정상 종료와의 차이** — Ctrl+C 로 끄면 `(RemoteClosed)` 가 즉시 찍힌다. FIN 을 보냈기 때문이다.

## 책

- 4장 「시작하기」 — 이 코드를 한 줄씩 해설한다
- 9장 「프로젝트 ① 에코 — 첫 왕복」 — 전체 워크스루와 연습문제
