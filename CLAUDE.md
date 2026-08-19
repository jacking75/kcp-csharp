# CLAUDE.md

## 프로젝트
KCP(신뢰성 UDP ARQ 프로토콜)의 C# 구현. kcp-go 포팅 기반이며, 온라인 게임에 실전 투입 가능한 라이브러리 + 학습 자료(온라인 책, 샘플)로 발전시키는 중.

## 단일 기준 문서
**`docs/kcp-csharp-roadmap.md`** 가 모든 작업의 구현 지시서다.
- 작업 지시는 작업 ID로 온다: `P1-3`(Phase-번호), 버그 `B-xx`, 성능 `PF-xx`, 안정성 `S-xx` → 해당 절을 먼저 읽고 그대로 구현할 것.
- Phase 순서: 1 기반·버그 → 2 테스트(성능보다 반드시 먼저) → 3 성능 → 4 전송 계층(서버) → 5 보안/FEC · 6 온라인 책 · 7 샘플 · 8 배포.

## 불변 원칙
1. 코어 KCP 헤더(24바이트)는 원본 kcp/kcp-go와 **와이어 호환 유지**. 확장은 Transport 프레이밍 계층에서.
2. 코어(`Kcp`)는 소켓·스레드·시계·로깅을 모른다(전부 주입). 인스턴스 1개 = 소유 스레드 1개, 내부 lock 금지.
3. Phase 2 이후 모든 변경은 테스트 선행, Phase 3 이후 벤치마크 회귀 금지.
4. 샘플·테스트의 UDP 포트는 **27001~27199** 대역만 사용(배정표: 로드맵 12.4절).
5. 게임 엔진 연동은 **MonoGame**(Unity 아님). 타깃: `netstandard2.1;net8.0`.

## 빌드·테스트
```
dotnet build -c Release
dotnet test  -c Release     # tests/ 생성(P2-1) 이후
```
경고 0 유지(`TreatWarningsAsErrors` 도입 예정). 외부 패키지 의존 최소화 — 코어는 의존성 0.

## 컨벤션
- 대화·주석·XML 문서·커밋 메시지: 한국어. 커밋은 `P1-3: 요약` 형식(작업 ID 접두사).
- 브랜치 `p1-3-요약`, PR 1개 = 작업 ID 1개. master 직접 푸시 금지.
- 네임스페이스 `KcpProject` / `KcpProject.Transport` 유지. `#nullable enable`, 공개 API PascalCase.

## 현재 구조 (Phase 1에서 재편 예정)
- `KCP/KCP.cs` 코어, `KCP/ByteBuffer.cs` 버퍼(internal), `KCP/UDPSession.cs` 클라 세션(internal), `Sample/Main.cs` 클라 샘플. 서버·테스트는 아직 없음.
- 목표 구조(src/tests/samples/benchmarks/docs 분리)는 로드맵 2.1절.
