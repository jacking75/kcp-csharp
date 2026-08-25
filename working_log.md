# 작업 로그

## 2026-08-25 18:18 KST — README 설치 절에 패키지 용도·설치 범위 안내 보강

README.md/README_kr.md 의 "설치" 절이 `dotnet add package` 두 줄만 나열할 뿐, 두 패키지가
각각 무엇이고 왜 나뉘어 있는지, 보통 하나만 설치하면 되는지에 대한 설명이 없었다(책 4장에는
한 줄 있었지만 README 자체엔 전무). 두 패키지의 역할·설치 대상을 표로 정리하고,
KcpCSharpR1.Transport 가 KcpCSharpR1 을 자동으로 끌고 오므로 대부분은 Transport 하나만
설치하면 된다는 점을 명시했다. 코어만 단독으로 쓰는 경우(자체 소켓 계층 보유, RawMode 상호운용)
는 별도로 표시했다.

## 2026-08-25 18:00 KST — 코드 완전 해설서(docs/code-guide.md) 신설

kcp-csharp 소스를 파일 단위로 아주 상세하게 설명하는 한국어 전용 문서를 새로 만들었다.
기존 docs/ 문서 중 이런 "코드 자체를 처음부터 끝까지 설명"하는 문서가 없어 요청받아 작성했다
(온라인 책은 사용법 위주라 범위가 다름). 코어(Kcp.cs 의 ARQ·슬라이딩 윈도·RTO 계산·재전송
3경로·MoveRcvBufToQueue 버그 스토리 등)와 전송 계층(KcpSession 상태 머신·핸드셰이크·아웃박스,
KcpServer/KcpClient, AES-GCM nonce/리플레이 윈도, Reed-Solomon FEC 수학) 전체 20여 개 파일을
다뤘고, 전체 데이터 흐름·스레딩 모델·메모리 모델 절도 추가했다(2,580줄). 작성 후 코드 블록
전체를 실제 소스와 대조 검증해, 조기 재전송 change++ 누락·EnsureBuffer 안전 가드 누락·
AesGcm.MarkSeen 첫 수신 초기화 분기 누락 등 실제 동작과 어긋나는 축약을 10여 곳 찾아 고쳤다
(전부 "이해를 돕기 위한 요약"이 실제로는 다른 동작을 암시하던 문제). docs/_sidebar.md·
docs/README.md·양쪽 최상위 README에 이 문서로의 링크를 추가했다.

## 2026-08-25 17:23 KST — README·벤치마크 문서 한/영 이중화

README.md 를 영어로 전면 번역하고, 기존 한국어 내용은 README_kr.md 로 옮겼다(상호 언어
전환 링크 추가). 오래되어 있던 짧은 영어 요약본 README.en.md 는 이제 중복이라 삭제했다.
docs/ 문서 중 "개발자가 봐야 하는 문서"의 범위는 사용자와 상의해 온라인 책(docs/book)은
제외하고 docs/benchmarks.md 로 한정했다 — benchmarks.md 를 영어로 번역하고 한국어 원문은
docs/benchmarks_kr.md 로 분리했다. 책은 한국어 전용으로 유지하므로 책 내부 링크
(docs/_sidebar.md, docs/README.md)는 benchmarks_kr.md 를 가리키도록 고쳐 책의 언어
일관성을 지켰다. 로드맵(kcp-csharp-roadmap.md)과 성능 개선 제안서는 내부 관리용 문서로
보고 번역 대상에서 제외했다. 번역하면서 README 의 테스트 개수(207 -> 238)도 최신화했다.
NuGet 패키징이 README.md 를 PackageReadmeFile 로 쓰므로 영어화가 패키지 배포에도 유리하다.

## 2026-08-25 17:05 KST — 동시 접속 용량 산정 내용을 문서화

"1,000명을 받을 수 있는 구조인가"에 대한 분석을 책·README·벤치마크 문서에 반영했다.
17장에 "몇 명까지 받을 수 있나 — 구조가 정하는 상한" 절을 신설해 구성별 권장 세션 수 표,
수신 스레드 1개가 상한을 정한다는 점, 게임 로직 분리(EnableEventQueue)가 세션 수를 3배 가른다는
점, 1,000 이상을 노릴 때의 4단계 순서를 정리했다. 14장의 "서버 용량 산정 감각"은 "세션당 메모리
예산"으로 바꿔, 세그먼트가 ArrayPool 버킷 때문에 실제 2,048B라는 점과 최악 2.6MB/세션 계산을
넣었다. benchmarks.md 에는 1,000세션 실측을 서버 용량으로 읽으면 안 되는 이유(부하 생성기가
세션마다 소켓을 따로 쓴다)를 명시했다. FAQ 에 Q14 를 추가하고 Q5·Q9·Q11 을 신규 API 기준으로
갱신했다. FEC 고정 메모리 수치는 실제 계산값 283KB 로 정정했다(코드 주석 포함).

## 2026-08-25 16:18 KST — 성능 개선 제안서 구현 (G/C/M/A 11건) + 부수 버그 수정

`docs/performance-improvements.md` 의 13건 중 11건을 구현했다. 무복사 수신(`Kcp.RecvInPlace` +
`ReliableReceivedSpan`), 송신 주소 캐시(G-1), 상주 복호화 버퍼(G-3), FEC 디코더·ReedSolomon
무할당화(G-4), 스레드 경계 API 2종(M-1 아웃박스 / M-2 이벤트 큐), ConnectAsync 폴링 제거(A-1)가
핵심이다. 구현 중 `Kcp.Recv` 의 실효 수신 윈도가 절반이던 문제와 콜백 안 종료 시 NRE 가능성,
FEC 경로의 패킷당 클로저 할당도 함께 고쳤다. M-3b·A-2 는 제안서 자신이 명시한 착수 조건
(부하 측정 선행 · 결정적 동시성 테스트 확보)이 아직 없어 로드맵에 보류로 등재했다.
테스트 207 → 238개 전량 통과, 빌드 경고 0. 같은 세션 A/B 벤치마크로 코어 회귀 없음을 확인했다.

## 2026-08-25 15:11 KST — 추가 성능 개선 후보 분석 및 제안서 작성

코어·Transport 전체를 전수 분석해 GC 할당, 불필요한 복사, 멀티스레드 확장, 비동기 API 네 축의
추가 개선 후보 13건을 `docs/performance-improvements.md` 로 정리했다. 핵심은 송신 경로
SocketAddress 패킷당 할당 제거(G-1), Span 이벤트로 수신 rent+복사 제거(G-2), FEC 디코더
무할당화(G-4), ConnectAsync 1ms 폴링 제거(A-1)이며, 이미 최적화가 끝나 손댈 필요 없는 영역도
재확인 결과로 함께 기록했다. 코드 변경 없는 분석·문서 작업이다.

## 2026-08-25 15:00 KST — README 에 NuGet 배포 절차 추가

관리자가 새 버전을 nuget.org 에 올릴 때 따라할 수 있도록 README.md 에 "NuGet 배포(관리자용)"
절을 추가했다. 버전 올리기 → 빌드/테스트 확인 → dotnet pack → API 키 발급·등록(1회) →
push(코어 먼저, Transport 나중) 순서와, 버전 번호는 되돌릴 수 없다는 주의사항을 담았다.
직전 대화에서 실제로 수행한 절차를 그대로 문서화한 것이다.

## 2026-08-25 14:55 KST — 첫 배포 버전을 1.0.0 → 0.10.0 으로 변경

사용자 결정으로 최초 NuGet 배포 버전을 1.0.0 대신 0.10.0 으로 낮췄다. Directory.Build.props 의
Version 을 0.10 으로 바꾸고 두 패키지를 다시 pack 해, NuGet 이 "0.10" 을 "0.10.0" 으로 정규화하고
Transport → 코어 의존성도 0.10.0 을 정확히 가리키는지 nuspec 으로 직접 확인했다.
직전 커밋에서 1.0.0 을 기정사실처럼 적어 둔 CHANGELOG 와 로드맵 P8-1/M5 서술을 0.10.0 기준으로
고쳤다(로드맵의 원래 마일스톤 계획표에 남아 있는 `1.0.0` 언급은 과거 계획 서술이라 그대로 둠).
빌드 경고 0 · 테스트 207개 전량 통과.

## 2026-08-25 14:25 KST — 로드맵 Phase 8 배포 준비 (P8-1 완료, P8-2 부분)

P8-1 NuGet 패키징을 구현했다. src/KcpCSharpR1, src/KcpCSharpR1.Transport 두 프로젝트에
PackageId·MIT 라이선스·README/LICENSE 동봉·심볼 패키지(.snupkg) 설정을 추가하고,
Directory.Build.props 에 공통 패키징 속성 + 기본 IsPackable=false 를 두어 샘플/테스트는 제외했다.
버전은 1.0.0(프리뷰 태그는 낸 적 없어 바로 시작)으로 정하고, `dotnet pack` 으로 실제 빌드해
nuspec 의존성(Transport → 코어 1.0.0)·양쪽 타깃(net8.0/netstandard2.1)·README/LICENSE 동봉을
직접 확인했다. nuget.org 에 KcpCSharpR1/KcpCSharpR1.Transport 두 ID 모두 미등록임을 확인했다.
P8-1 의 릴리스 자동화(태그 push → GitHub Actions)와 P8-2 의 CI·master 브랜치 보호는
푸시 토큰에 workflow 스코프가 없는 문제 + 현재 직접 푸시 운영 방식과의 충돌 때문에
사용자 결정으로 보류했다(로드맵에 사유 기록). CHANGELOG 를 1.0.0 릴리스 항목으로 정리했다.
로드맵 45/47, M5 완료.

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
