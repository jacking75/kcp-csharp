# 도구: LoadTest

게임 로직이 전혀 없는 **순수 전송 부하** 측정 도구. Phase 3·4 완료 조건을 검증하는 데 쓰고,
책 8장(파라미터 튜닝)·17장(운영)의 실습 도구이기도 하다.

## 실행

```bash
# 받는 쪽(에코 서버)
dotnet run -c Release --project samples/tools/LoadTest -- server

# 거는 쪽
dotnet run -c Release --project samples/tools/LoadTest -- client --sessions 1000 --rate 20 --size 256 --duration 60
```

UDP **27010** 포트를 쓴다. **반드시 `-c Release`** 로 돌린다(Debug 수치는 의미가 없다).

## 옵션

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `--host` | `127.0.0.1` | 서버 주소(클라 모드) |
| `--port` | `27010` | UDP 포트 |
| `--sessions` | `100` | 동시 세션 수 |
| `--rate` | `20` | 세션당 초당 메시지 수 |
| `--size` | `256` | 메시지 크기(바이트, 최소 8) |
| `--duration` | `60` | 측정 시간(초) |
| `--unreliable` | 꺼짐 | 신뢰 채널 대신 비신뢰(RAW) 채널 사용 |
| `--preset` | `fast2` | `normal` / `fast` / `fast2` / `fast3` |
| `--csv` | 없음 | 결과를 CSV에 덧붙인다(비교 실험용) |
| `--sockbuf` | `65536` | 세션당 소켓 버퍼(바이트) |
| `--threads` | `0`(자동) | 세션을 나눠 구동할 워커 스레드 수 |

> `--threads` 가 왜 있나: 세션 하나는 반드시 한 스레드가 소유해야 한다(라이브러리 스레딩 규약).
> 스레드마다 겹치지 않는 세션 묶음을 맡으므로 lock 이 없다. 단일 스레드로는 세션 수백 개를 넘기면
> keepalive 를 제때 못 보내 서버가 세션을 끊는다.
>
> `--sockbuf` 가 왜 있나: 라이브러리 기본값은 1MB다. 세션 1,000개면 송·수신 합쳐 2GB를 요청하게 된다.
> 부하 도구는 세션이 많으므로 64KB로 낮춰 잡는다. **실제 서버는 기본값(1MB)을 쓰는 것이 맞다**(진단 S-05).

## 리포트 읽는 법

```
│ 메시지    보냄 6,550 / 에코 6,550 (819 msg/s)
│ 무결성    불일치 0건, 에코 손실 0.00%
│ 처리량    송신 2.00 Mbps / 수신 1.99 Mbps
│ 재전송    0 / 13,100 세그먼트 (0.00%)
│ RTT       p50 18.0ms  p95 46.4ms  p99 48.1ms  (표본 6,550)
│ GC        Gen0 0 / Gen1 0 / Gen2 0
│ 메모리    관리 힙 10.2 MB, 작업 집합 43.9 MB
```

| 지표 | 봐야 할 것 |
|---|---|
| **불일치** | 0이 아니면 심각한 버그다. 다른 지표를 볼 필요도 없다. |
| **에코 손실** | 신뢰 채널이면 0%여야 한다. 비신뢰 채널에서는 손실률이 그대로 보인다. |
| **재전송률** | 5%를 넘으면 회선이나 서버 처리 지연을 의심한다(도구가 경고를 찍는다). |
| **RTT p99** | p50보다 p99를 본다. 평균은 끊김을 숨긴다. |
| **Gen2** | 0이어야 한다. Gen2 수집은 게임에서 프레임 스파이크로 나타난다. |

RTT는 **애플리케이션 왕복 시간**이다(보낸 순간 → 에코가 돌아온 순간). KCP 내부의 `Srtt` 와는
다르며, 서버의 처리 지연과 클라이언트의 폴링 주기가 함께 들어간다.

## 실험 예시

```bash
# 프리셋별 RTT 비교 (책 8장 실습)
for p in normal fast fast2 fast3; do
  dotnet run -c Release --project samples/tools/LoadTest -- \
    client --sessions 200 --duration 30 --preset $p --csv preset.csv
done

# 신뢰 vs 비신뢰 채널 비교 (책 7장 실습)
dotnet run -c Release --project samples/tools/LoadTest -- client --sessions 200 --duration 30 --csv channel.csv
dotnet run -c Release --project samples/tools/LoadTest -- client --sessions 200 --duration 30 --unreliable --csv channel.csv
```

손실을 걸어 보려면 Windows는 [clumsy](https://jagt.github.io/clumsy/), Linux는 `tc netem` 을 쓴다.

```bash
sudo tc qdisc add dev lo root netem loss 10% delay 50ms   # 걸기
sudo tc qdisc del dev lo root                              # 풀기
```

## 실측 기록

`docs/benchmarks.md` 의 「Phase 7 — 1,000 세션 부하 측정」 절에 결과와,
**측정하다 발견한 도구 쪽 함정 3가지**를 기록해 두었다. 이상한 숫자가 나오면 측정기부터 의심하자.

## 책

- 8장 「파라미터 튜닝」 — 프리셋별 RTT p95 비교 실습
- 17장 「운영」 — 지표 해석과 장애 사례집
