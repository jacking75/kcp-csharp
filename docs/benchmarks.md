# Benchmark Log

🌐 **English** | [한국어](benchmarks_kr.md)

This records the baseline and improvement results from Phase 3 (performance optimization).
Following the common procedure in roadmap §6: **① measure baseline → ② change → ③ re-measure
→ ④ attach before/after table.**

## How to Reproduce

```bash
# all of them
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*'

# individually
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*SendRecvRoundtrip*'
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*InputAckBatch*'
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*FlushLargeWindow*'
dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*FragmentationLarge*'
```

Every benchmark carries `[MemoryDiagnoser]`, so allocation (B/op) is reported alongside timing.
`[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]` keeps runs short.

## Measurement Environment

| Item | Value |
|---|---|
| Date | 2026-08-25 |
| OS | Windows 11 (10.0.26200) |
| Runtime | .NET 8.0.29 (X64 RyuJIT AVX2) |
| SDK | .NET SDK 10.0.302 |
| BenchmarkDotNet | 0.13.12 |

> Absolute numbers vary by machine. **Only compare before/after values measured on the same
> machine with the same benchmark code.** The "baseline" and "after" columns below were obtained
> by running the same commit's benchmark code against the Phase 2 core and the Phase 3 core in
> turn (swapping only the core).

## Benchmark Items

| Benchmark | What it does | What it measures |
|---|---|---|
| `SendRecvRoundtrip` | Lossless loopback, 1,000 round trips of 1KB messages | Overall cost of the steady-state send/receive path |
| `InputAckBatch` | Parsing 64 ACKs bundled into one packet | `parse_ack`/`parse_fastack` lookup cost (PF-05) |
| `FlushLargeWindow` | Flush traversal with 1,024 segments in snd_buf | Queue data structure traversal cost (PF-04) |
| `FragmentationLarge` | Round trip of a 32KB message: fragment → reassemble | Fragmentation/buffer management cost (PF-02, PF-03) |

## Results (Phase 3 Before → After)

| Benchmark | Baseline (Phase 2 core) | After (Phase 3 core) | Change |
|---|---:|---:|---:|
| `SendRecvRoundtrip` (1000 × 1KB) | 441.3 µs | **321.3 µs** | **−27.2%** |
| `InputAckBatch` (64 ACK) | 1.428 µs | **1.318 µs** | **−7.7%** |
| `FlushLargeWindow` (window 1024) | 1.921 µs | **1.642 µs** | **−14.5%** |
| `FragmentationLarge` (32KB) | 8.252 µs | **6.882 µs** | **−16.6%** |
| Steady-state managed-heap allocation | 0 B/op | **0 B/op** | unchanged |

### On the 0 B/op Allocation

The Phase 2 core was already at 0 B/op in steady state (its own `ByteBuffer` pool and `Segment`
pool are reused after warmup). Phase 3's win isn't "getting allocation to zero" — it's
**removing the following while keeping it at zero:**

- A `lock` on a global static pool (more contention as sessions grow) → per-instance pools (PF-06)
- The pool handing back arbitrarily-sized buffers → `ArrayPool<byte>` rental + a separate valid-length field (fundamental fix for B-07)
- O(n) shifting from `List<Segment>.RemoveRange(0, n)` → O(1) head movement in `RingBuffer` (PF-04)
- A temporary array allocated on every call to `ByteBuffer.Get()`/`Read()` → removed via the Span path (PF-02, PF-03)

## Changes Applied

| Task | What it did |
|---|---|
| P3-2 | Introduced `KcpCodec` (BinaryPrimitives), added `Span` overloads to the public API (`Send`/`Input`/`Recv`) |
| P3-3 | Replaced `Segment.data`'s `ByteBuffer` with an `ArrayPool<byte>`-rented array plus a valid-length field (`Len`); removed `ByteBuffer` from the core |
| P3-4 | Replaced all four queues, `List<Segment>` → `RingBuffer<KcpSegment>` |
| P3-5 | Moved the segment pool from global static (locked) → an instance field (lock-free); added `Kcp.Release()` |
| P3-6 | Switched `parse_ack`/`parse_fastack` to binary search |

## What We Learned While Measuring (Pitfall Log)

1. **An initial design bug in `FlushLargeWindow`** — the remote window (`rmt_wnd`) defaults to
   32, so `WndSize(1024, 1024)` alone only filled snd_buf to 32 entries. Only after sending a
   WINS packet to open up the window could we measure the state with 1,024 entries filled.
2. **Advancing the clock made the measurement noisy** — advancing the virtual clock by 1ms
   inside the benchmark caused RTOs to expire around iteration 200, mixing in a full retransmit
   of all 1,024 segments. Since the average then depends on the call count, we now hold the
   clock fixed and measure **pure traversal cost** only.
3. **Introducing `RingBuffer` made things 15% slower at first** — the cost of re-reading
   `items`/`head`/`Length` on every element access. Introducing a snapshot view
   (`RingBuffer<T>.CreateView()`) that pins the array, head, and mask locally for the traversal
   section brought it back below baseline. Without this step, we would have simply concluded
   "we changed the data structure, so it must be faster" — which is exactly why measurement
   comes first.

## Phase 5 — Encryption (P5-1)

Reproduce with `--filter '*PacketCrypto*'`. Packet size is 1,376 bytes (the KCP payload at
MTU 1400).

| Item | Time | vs. baseline | Allocation |
|---|---:|---:|---:|
| `PlainCopy` (no encryption, copy only) | 13.41 ns | 1.00× | 0 B |
| `Encrypt` (AES-256-GCM) | 357.7 ns | 26.7× | 0 B |
| `Decrypt` (including authentication + replay check) | 351.3 ns | 26.2× | 0 B |

How to read this: turning on encryption costs about **0.36µs** more per packet. If 1,000
sessions each exchange 30 packets/sec, that's 30,000 packets/sec × 0.36µs ≈ **11ms/sec**, roughly
1.1% of a single core — an affordable cost for a game server. The overhead is 24 bytes per
packet (8-byte counter + 16-byte tag).

> Note on the `Decrypt` measurement: because of the replay window, the same packet can't be
> decrypted twice. We pre-generate 256 packets with distinct counters and process them in
> batches with a fresh receiver each round, so none get short-circuited by the replay check
> (we fell into this trap once and got a bogus 3ns reading).

## Phase 5 — FEC (P5-2)

### Encoding Cost (`--filter '*FecCoding*'`, 10+3 group, 1,376-byte shards)

| Item | Time | Allocation |
|---|---:|---:|
| `Encode` (generate 3 parity shards) | 30.05 µs | 0 B |
| `Reconstruct` (recover 3 data shards) | 31.53 µs | 5,896 B → **0 B** (after G-4) |

30µs per group happens once every 10 data packets, so roughly 3µs per packet — much more
expensive than encryption, which is why it's meant to be turned on **only on links where loss is
actually a problem.**

### Retransmit Reduction at 20% Loss (P5-2 ③)

The figures verified by `Phase5IntegrationTests.FEC가_손실_환경의_재전송을_줄인다`
("FEC reduces retransmits in a lossy environment"). While sending 100KB, we deterministically
dropped 1 in every 5 (20%) of the KCP frames the client sent out.

| Setting | KCP retransmits | FEC recoveries |
|---|---:|---:|
| No FEC | 23 | — |
| FEC 10+3 | **7** | 16 packets |

Retransmits dropped by **70%**. Since each retransmit means waiting at least one more RTT, this
makes a real difference in perceived latency on lossy mobile links. The trade-off is spending
extra bandwidth on parity packets (30% with a 10+3 configuration).

## Phase 7 — 1,000-Session Load Test (P7-6)

This verifies Phase 4's completion condition ("0 errors and a stable heap across 1,000
concurrent sessions for 60 seconds"). It was run **with client and server on the same PC at the
same time**, so note that these numbers are not standalone server capacity but rather
`the numbers when running both sides on one machine`.

```bash
dotnet run -c Release --project samples/tools/LoadTest -- server
dotnet run -c Release --project samples/tools/LoadTest --   client --sessions 1000 --rate 20 --size 256 --duration 60 --sockbuf 65536
```

| Item | Result |
|---|---|
| Sessions | 1,000 connected / **1,000 survived** (the full 60 seconds) |
| Messages | sent 970,627 / **echoed 970,627 (0.00% loss)** |
| Integrity | **0 mismatches** |
| Throughput | send 40.3 Mbps / receive 39.6 Mbps (16,175 msg/s) |
| Retransmit | 21,906 / 1,940,694 segments (**1.13%**) |
| RTT | p50 30ms · p95 250ms · p99 517ms · max 1,770ms |
| GC | Gen0 6 / Gen1 3 / **Gen2 1** |
| Memory | managed heap 219.6 MB, working set 386.3 MB |

At **300 sessions** (same conditions) there's much more headroom — **0.00%** retransmit, RTT p99
**48.5ms**, **0** Gen2 collections. In other words, on this one PC the comfortable ceiling for
running client and server together is 300–400 sessions, and 1,000 sessions is the saturation
point where "it works, but the latency tail gets long."

### Why You Shouldn't Read These Numbers as Server Capacity

**The load generator is far heavier than the server.** The generator creates a **separate
`KcpClient` and a separate socket per session** and runs `Update()` on each one. Polling 1,000
sockets individually and receiving everything through a single socket differ in cost by an order
of magnitude. A significant part of why p99 stretched out to 517ms is likely the generator, not
the server.

To summarize, this measurement only proves two things:

- **It works with 0 loss and 0 mismatches at 1,000 sessions for 60 seconds** (Phase 4's
  completion condition).
- On one PC running both sides, 300–400 is comfortable and 1,000 is saturated.

What it does *not* prove: **standalone server capacity.** That has never been measured. Doing so
would require putting the load generator on a separate machine (see "Items to Add" below).

The structural reasoning behind capacity (a single receive thread sets the ceiling, separating
game logic accounts for a 2–3× difference, etc.) is written up in the "How Many Clients Can It
Handle" section of [Chapter 17, Operations](book/ch17-operations.md) of the book (Korean only).

### On the Single Gen2 Collection

This is a number from the load **generator process**, not the library. It comes from allocating
hundreds of MB during the connection phase, where 1,000 session objects and sockets get created.
The library's steady-state send/receive path being **0 B/op** was separately confirmed with
BenchmarkDotNet (the Phase 3 table above).

### What We Fixed While Measuring (3 Tool-Side Pitfalls)

1. **Existing sessions weren't pumped during the connection phase** — opening 1,000 sessions
   sequentially takes over ten seconds, and during that time the already-connected sessions
   couldn't send keepalives, so **the server dropped them on the 10-second timeout**. This was
   why only 325 of 1,000 connections survived on the server side. We fixed it by periodically
   calling `Update()` on existing sessions inside the connection loop too.
2. **The server discarded `Send`'s return value** — when the backlog cap is exceeded, the
   library returns `Backpressure` and refuses the message, but the tool ignored this, producing
   the baffling result "0 retransmits, but no echo comes back." It now counts and reports how
   many were dropped.
3. **The measurement tool itself triggered GC** — accumulating 960,000 RTT samples into a
   `List<double>` made the measurement tool generate its own Gen2 collections. Switching to a
   fixed 1ms-wide histogram (8KB per session) eliminated the extra allocation during the run.

All three were **flaws in the measurement code, not the library**. We wrote this up as a case
study in Chapter 17 of the book: when a load test produces a weird number, suspect the measuring
instrument first.

## Performance Proposal (G/C/M/A) Before/After — 2026-08-25

After applying the items from `docs/note/performance-improvements.md`, we measured before (checked
out from commit `7bec0b3` via a worktree) and after **within the same session**, alternating
between the two. Don't compare the absolute values against the Phase 3 table above directly —
the machine state is different now. **What matters is the difference between the two columns
measured under the same conditions.**

| Benchmark | Before (7bec0b3) | After | Verdict |
|---|---:|---:|---|
| `SendRecvRoundtrip` (1000 × 1KB) | 366.0 µs ± 59.6 | 365.1 µs ± 48.7 | same (within error) |
| `InputAckBatch` (64 ACK) | 1.486 µs ± 0.45 | 1.541 µs ± 0.36 | same (within error) |
| `FlushLargeWindow` (window 1024) | 1.672 µs ± 0.38 | 1.900 µs ± 0.50 | same (within error) |
| `FragmentationLarge` (32KB) | 7.515 µs ± 0.55 | 7.497 µs ± 0.59 | same (within error) |
| `FecCoding.Reconstruct` (10+3) | 31.53 µs / **5,896 B** | 31.54 µs / **0 B** | **allocation eliminated** |
| `FecCoding.Encode` (10+3) | 30.05 µs / 0 B | 28.47 µs / 0 B | same (within error) |

In short, **core timing cost is unchanged; only the per-packet allocation in FEC recovery
disappeared.** The rest of the improvements (G-1, G-2, G-3, G-5, C-1) live on the socket/event
path, and since the BenchmarkDotNet harness has no socket, they don't show up here. Those are
pinned down directly in unit tests with `GC.GetAllocatedBytesForCurrentThread()` instead.

| Test | What it confirms |
|---|---|
| `RecvInPlaceTests.무복사_수신은_힙_할당을_남기지_않는다` | 20 drain calls of `Kcp.RecvInPlace` = **0 B** |
| `ReedSolomonTests.복원은_힙_할당을_남기지_않는다` | 100 calls of `Reconstruct` = **0 B** |

> `SendRecvRoundtrip` looking larger than the Phase 3 table's 321.3 µs is not a regression.
> Measuring the same commit (`7bec0b3`) right now gives 366.0 µs. Absolute values swing heavily
> with machine state, so **regression calls must always be made from an A/B within the same
> session.**

## Items to Add

- Standalone server capacity (measured with the load generator on a separate PC)
- End-to-end transport-layer throughput (including handshake)
