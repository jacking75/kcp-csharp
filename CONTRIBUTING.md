# 기여 안내

## 개발 환경

- **.NET 8 SDK** 이상
- MonoGame 샘플(03~05)을 빌드하려면 NuGet 접근이 필요합니다(패키지 자동 복원)

```bash
git clone https://github.com/jacking75/kcp-csharp
cd kcp-csharp
dotnet build -c Release
dotnet test  -c Release
```

## 빌드와 테스트

```bash
dotnet build -c Release                              # 경고 0 이어야 합니다
dotnet test  -c Release                              # 전체 207개
dotnet test  -c Release --filter "Category!=Soak"    # 장시간 테스트 제외(빠름)
dotnet test  -c Release --filter "Category=Soak"     # 가상 24시간 소크만
```

### 벤치마크

```bash
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*SendRecvRoundtrip*'
```

**반드시 `-c Release`** 입니다. Debug 수치는 의미가 없습니다.

### 커버리지

```bash
dotnet test tests/KcpProject.Tests -c Release \
  -p:CollectCoverage=true -p:Include="[KcpProject]*" \
  -p:CoverletOutputFormat=json -p:CoverletOutput=./coverage/coverage.json

dotnet test tests/KcpProject.SimTests -c Release \
  -p:CollectCoverage=true -p:Include="[KcpProject]*" \
  -p:MergeWith=./coverage/coverage.json \
  -p:Threshold=85 -p:ThresholdType=line
```

코어(`KcpProject`) 라인 커버리지 **85% 이상**이 기준입니다. 현재 93.4%입니다.
PR 을 올리기 전에 로컬에서 직접 확인해 주세요.

## 코드 규칙

### 지켜야 할 불변 원칙

1. **와이어 포맷 호환 유지** — 코어 KCP 헤더(24바이트)는 원본 C kcp / kcp-go 와 바이트 단위로 호환됩니다.
   확장은 코어 밖의 프레이밍 계층에서 합니다.
2. **코어는 순수하게** — `Kcp` 클래스는 소켓·스레드·시계·로깅 구현을 몰라야 합니다(전부 주입).
3. **인스턴스 1개 = 소유 스레드 1개** — 코어에 lock 을 넣지 않습니다. 성능이 여기서 나옵니다.
4. **테스트 선행** — 모든 변경은 기존 테스트 통과 + 신규 테스트 추가가 전제입니다.
5. **성능 회귀 금지** — 벤치마크 기준선 대비 회귀하는 변경은 머지하지 않습니다.
6. **개발용 포트는 27001~27199** — 샘플·테스트가 쓰는 UDP 포트 대역입니다.

### 스타일

- `#nullable enable`, 공개 API는 PascalCase
- **경고 0 유지** (`TreatWarningsAsErrors`)
- 공개 타입·멤버에 **한국어 XML 문서 주석** 필수
- 주석은 "무엇을"이 아니라 **"왜"** 를 씁니다
- 외부 패키지 의존 최소화 — 코어는 의존성 0, 샘플은 MonoGame 만 허용

### 커밋과 브랜치

- 커밋 메시지: **한국어**, 작업 ID 접두사 (`P4-2a: 세션 상태 머신 구현`)
- 브랜치: `p4-2a-세션-상태-머신`
- **PR 1개 = 작업 ID 1개**
- `master` 직접 푸시 금지

작업 ID는 [docs/kcp-csharp-roadmap.md](docs/kcp-csharp-roadmap.md)의 체크리스트를 따릅니다.
작업을 완료하면 **같은 커밋에서** 로드맵의 체크박스도 갱신하세요.

## 버그 리포트

**재현 가능한 테스트를 함께 주시면 가장 좋습니다.**

이 저장소에는 가상 시간 하네스가 있어 네트워크 상황을 결정적으로 재현할 수 있습니다.

```csharp
// tests/KcpProject.SimTests/KcpPair.cs
var pair = KcpPair.Create(seed: 42, loss: 0.20, delayMs: 50);
pair.AckNoDelay = true;

pair.A.Send(payload);
Assert.True(pair.RunUntil(() => pair.ReceivedB.Length == payload.Length, maxMs: 60_000));
```

시드를 고정하면 **100% 같은 손실 패턴**이 재현됩니다. 실제 대기가 없어 24시간짜리 시나리오도 수 초입니다.

전송 계층 문제라면 가상 망을 씁니다.

```csharp
// tests/KcpProject.SimTests/VirtualNetwork.cs
var network = new VirtualNetwork(seed) { DelayMs = 20, LossRate = 0.1 };
network.DropFilter = (from, to, data) => Framing.PeekType(data) == FrameType.SynAck;  // 특정 프레임만 떨어뜨리기
```

### 리포트에 포함하면 좋은 것

- 재현 코드 또는 실패하는 테스트
- `session.Stats` 값(재전송률·RTT)
- 환경(OS, .NET 버전, 유선/무선, 모바일 여부)
- 몇 세션·초당 몇 패킷인지

## 테스트 작성 요령

### 시나리오 테스트

`tests/KcpProject.SimTests/ScenarioTests.cs` 를 참고하세요. 각 테스트는
`KcpPair` 옵션만 다르게 주는 **20줄 내외**여야 합니다. 그보다 길어지면 하네스를 보강할 때입니다.

**끝 조건에 주의하세요.** "마지막 바이트가 도착했다"로 끝내면 ACK 왕복 전에 멈춥니다.

```csharp
// 전량 확인(WaitSnd == 0)까지 진행한다
return pending.Count == 0 && pair.ReceivedB.Length == payload.Length && pair.A.WaitSnd == 0;
```

### 실소켓 테스트

포트 **27100~27199** 대역만 씁니다. `SocketIntegrationTests.NextPort()` 가 순번을 돌립니다.

### 장시간 테스트

```csharp
[Trait("Category", "Soak")]
```

를 붙이면 `--filter "Category!=Soak"` 로 걸러집니다. 평소에는 제외하고 필요할 때만 돌립니다.

## 성능 변경 절차

1. **기준선을 먼저 잽니다** (`docs/benchmarks.md` 에 기록되어 있는지 확인)
2. 브랜치에서 변경
3. 같은 명령으로 재측정
4. **PR 본문에 전/후 표를 첨부**
5. 회귀가 없으면 머지

**감으로 하는 최적화는 받지 않습니다.** 이 저장소의 성능 작업 중 하나는
자료구조를 "개선"했다가 오히려 15% 느려진 것을 벤치마크가 잡아낸 적이 있습니다
(`docs/benchmarks.md` 의 함정 기록 참조).

### 측정할 때 주의

- `Error` 보다 작은 차이는 노이즈입니다
- 벤치마크가 **의도한 것을 재고 있는지** 먼저 확인하세요
- 이상한 숫자가 나오면 **측정 코드부터 의심**하세요

## 책(docs/) 수정

[docs/book/_writing-guide.md](docs/book/_writing-guide.md)의 규약을 따릅니다. 요약하면

- 장 구성: 학습 목표(3줄) → 본문 → 전체 코드 → 요약(5개 이내) → 연습문제(2~3개)
- **모든 코드는 `samples/` 의 실제 코드에서 발췌**하고 원본 경로를 주석에 적습니다
- 한국어 경어체, 용어는 [부록 E 용어집](docs/book/appendix-e-glossary.md) 기준
- 다이어그램은 mermaid(이미지 파일 금지)
- 잘 안 됐던 시도도 지우지 않고 남깁니다

로컬에서 책을 보려면 docsify 서버가 필요합니다.

```bash
npm i -g docsify-cli
docsify serve docs
```

## 질문

이슈로 물어보세요. [부록 B FAQ](docs/book/appendix-b-faq.md)에 이미 답이 있을 수도 있습니다.
