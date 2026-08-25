---
name: 버그 리포트
about: 동작이 이상하거나 예상과 다를 때
title: '[BUG] '
labels: 'type:bug'
assignees: ''
---

## 무슨 일이 일어났나요

<!-- 관찰한 증상을 한두 줄로 -->

## 예상한 동작

<!-- 어떻게 되어야 한다고 생각했나요 -->

## 재현 방법

<!--
재현 가능한 테스트를 주시면 가장 좋습니다.
가상 시간 하네스를 쓰면 손실 패턴까지 결정적으로 재현됩니다.

var pair = KcpPair.Create(seed: 42, loss: 0.20, delayMs: 50);
pair.A.Send(payload);
Assert.True(pair.RunUntil(() => pair.ReceivedB.Length == payload.Length, maxMs: 60_000));

전송 계층 문제라면 tests/KcpCSharpR1.SimTests/VirtualNetwork.cs 를 참고하세요.
-->

1.
2.
3.

## 통계

<!-- session.Stats 값을 넣어 주세요. 원인 파악에 큰 도움이 됩니다. -->

- 재전송률:
- RTT(Srtt):
- 세션 수 / 초당 패킷:

## 환경

- OS:
- .NET 버전:
- 회선(유선 / 무선 / 모바일):
- 라이브러리 버전 또는 커밋:

## 로그

<!--
KCP 내부 로그를 켜면 도움이 됩니다.

kcp.SetLogger(Console.WriteLine);
kcp.SetLogMask(Kcp.IKCP_LOG_OUT_DATA | Kcp.IKCP_LOG_IN_ACK);
-->

```
```

## 확인해 본 것

- [ ] `Update()` 를 주기적으로 부르고 있다
- [ ] `Send()` 의 반환값이 `Ok` 였다
- [ ] 방화벽에서 UDP 포트가 열려 있다
- [ ] [부록 B FAQ](https://jacking75.github.io/kcp-csharp/#/book/appendix-b-faq) 를 찾아봤다
