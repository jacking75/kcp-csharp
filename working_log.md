# 작업 로그

## 2026-08-25 14:09 KST — 프로젝트 이름 변경 (KcpProject → KcpCSharpR1)

C# 네임스페이스·프로젝트·솔루션·패키지 이름을 KcpProject/KcpProject.Transport 에서
KcpCSharpR1/KcpCSharpR1.Transport 로 전체 변경했다. 100개 파일(cs 59·md 24·csproj 16·sln 1)에서
KcpProject 문자열을 치환하고, 솔루션 파일과 7개 프로젝트 폴더·csproj 를 git mv 로 함께 옮겼다.
README/샘플 창 제목/채팅 Motd/문서 등 표시용 이름도 KcpCSharpR1 로 맞추되, 실제 GitHub 저장소
슬러그(jacking75/kcp-csharp)와 로드맵 문서 파일명(docs/kcp-csharp-roadmap.md)은 그대로 두었다.
README 에 원출처(skywind3000/kcp → xtaci/kcp-go → KcpCSharpR1) 절을 추가했다.
빌드 경고 0 · 테스트 207개 전량 통과.

## 2026-08-25 13:52 KST — CI 워크플로 제거

`.github/workflows/ci.yml` 을 삭제했다. 푸시에 쓰는 토큰에 `workflow` 스코프가 없어
원격이 워크플로 파일 생성을 거부했고, 사용자 결정으로 CI 를 빼는 쪽을 택했다.
깨지는 참조도 함께 정리했다 — README 한/영의 CI 배지 제거, CHANGELOG 의 CI 항목을
품질 기준 서술로 대체, CONTRIBUTING 의 "CI 게이트"·"야간 스케줄" 표현을 로컬 검증 안내로 수정.
로드맵 P1-8 과 M1 을 `[~]` 로 되돌리고 사유를 적었다(진행 44/47). 빌드 경고 0 · 테스트 207개 통과.

## 2026-08-25 13:40 KST — 로드맵 Phase 6 온라인 책 완성 (P6-1 ~ P6-5)

온라인 책 《C#과 KCP로 배우는 실시간 게임 네트워킹》 전 17장 + 부록 5종(API / FAQ / 상호운용 / MonoGame / 용어집)을 집필하고,
docsify + GitHub Pages 인프라(`docs/index.html`, `_sidebar.md`, `.nojekyll`)와 집필 규약(`_writing-guide.md`)을 갖췄다.
모든 장이 DoD(학습 목표 3줄 → 본문 → 요약 → 연습문제, 코드는 실제 `samples/` 경로 명시)를 충족한다.
저장소 위생으로 README 전면 개편(+영문판), CHANGELOG(Keep a Changelog), CONTRIBUTING, 이슈 템플릿 2종을 추가했다.
README 30초 시작 코드는 Echo 샘플로 실제 구동 확인했고, 빌드 경고 0 · 테스트 207개 전량 통과. 로드맵 45/47.

## 2026-08-25 11:43 KST — 로드맵 Phase 1~5 전체 구현 (P1-1 ~ P5-3, 34개 작업)

`docs/kcp-csharp-roadmap.md` 의 Phase 1~5 작업 34개를 모두 구현하고 페이즈 단위로 5개 커밋을 만들었다(브랜치 `phase1-5-구현`).
Phase 1: src/tests/benchmarks/samples 재편 + netstandard2.1;net8.0 멀티타깃, 단조 시계(B-01), 치명·높음 버그 9종 수정, CI.
Phase 2: 가상 네트워크 시뮬레이터(SimulatedLink/KcpPair)와 프로토콜 시나리오 14종, 골든 벡터, 소크 테스트, 커버리지 게이트.
Phase 3: Span 코덱·ArrayPool 세그먼트·RingBuffer 큐·인스턴스 풀·이분 탐색으로 재구성(왕복 -27%, 할당 0 유지), 벤치 기준선 기록.
Phase 4: 쿠키 핸드셰이크·세션 상태 머신·KcpClient/KcpServer·최소 힙 스케줄러. 샘플 ① 에코가 공개 API만으로 동작.
Phase 5: AES-256-GCM 암호화(리플레이 윈도), 리드-솔로몬 FEC(손실 20%에서 재전송 -70%), 서버 관측 스냅숏.
결과: 빌드 경고 0, 테스트 157개 통과, 코어 라인 커버리지 93.4%.

## 2026-08-25 10:02 KST — 로드맵 전체 작업 체크리스트 신설

`docs/kcp-csharp-roadmap.md` 상단(v2 변경 사항 바로 아래)에 `## ✅ 전체 작업 체크리스트 (진행 현황판)` 절을 추가했다.
Phase 1~8의 작업 ID 47개 + 마일스톤 M1~M6을 체크박스로 정리하고, P6-3(책 집필)은 1~4부·부록 하위 체크로 세분했다.
각 항목에 우선순위(`필수`/`권장`/`선택`)와 관련 진단 ID(B/PF/S)를 병기했으며, 갱신 규칙(완료 시 `- [x]` + 완료일, 진행 중 `- [~]`, 진행 현황 카운트 갱신)을 절 상단에 명시했다.
`CLAUDE.md` 「단일 기준 문서」에 "작업 완료 시 같은 커밋에서 체크리스트를 갱신한다"는 규칙을 추가해 이후 모든 작업에 자동 적용되도록 했다.
