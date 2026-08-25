# KcpCSharpR1

🌐 **English** | [한국어](README_kr.md)

**A C# library that adds exactly as much reliability as you need on top of UDP.** A KCP implementation for real-time games.

> A C# port and rebuild that continues the lineage from [skywind3000/kcp](https://github.com/skywind3000/kcp) (C)
> through [xtaci/kcp-go](https://github.com/xtaci/kcp-go) (Go). See [Lineage](#lineage) below for details.

![net8.0](https://img.shields.io/badge/net-8.0-512BD4)
![netstandard2.1](https://img.shields.io/badge/netstandard-2.1-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

📖 **[Online book (Korean): C#과 KCP로 배우는 실시간 게임 네트워킹](https://jacking75.github.io/kcp-csharp/)**

---

## What Is KCP

TCP guarantees that every byte arrives **in order**. That's perfect for file transfer, but
it becomes a problem in games. When one packet is lost, **every newer packet that already
arrived is held back too** (head-of-line blocking). That's why a character freezes for
0.25 seconds and then teleports.

KCP puts reliability on top of UDP **only where you need it**.

| Channel | Guarantees | Use for |
|---|---|---|
| **Reliable** (`Send`) | delivery + ordering | chat, inventory, hit results |
| **Unreliable** (`SendUnreliable`) | none | position snapshots, input |

It also recovers more aggressively than TCP — 1.5× RTO backoff (TCP uses 2×), 30 ms minimum RTO
(TCP is 200 ms+), fast retransmit plus early retransmit. **A trade: spend a bit more bandwidth,
buy back latency.**

## 30-Second Quick Start

```bash
git clone https://github.com/jacking75/kcp-csharp
cd kcp-csharp
dotnet run --project samples/01-Echo/Server     # terminal 1
dotnet run --project samples/01-Echo/Client     # terminal 2
```

### Server

```csharp
using KcpCSharpR1.Transport;

using var server = new KcpServer(27001);

server.SessionConnected += s => Console.WriteLine($"connected {s.RemoteEndPoint}");
server.ReliableReceived += (s, data) => s.Send(data.Span);            // echo
server.SessionDisconnected += (s, why) => Console.WriteLine($"closed {why}");

server.Start();
Console.WriteLine("Listening on UDP 27001. Press Ctrl+C to stop.");

// Start() spins up a background thread and returns immediately.
// Without something blocking here, the program would just exit.
using var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
quit.Wait();

server.Stop();
```

Full code: [samples/01-Echo/Server/Program.cs](samples/01-Echo/Server/Program.cs).

### Client

```csharp
using var client = new KcpClient();
client.ReliableReceived += (_, data) => Console.WriteLine(Encoding.UTF8.GetString(data.Span));

var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 27001));
session.Send(Encoding.UTF8.GetBytes("Hello KCP"));

while (true)          // every frame, in your game loop
{
    client.Update();  // ★ nothing happens unless you call this
    await Task.Delay(10);
}
```

## Features

| | |
|---|---|
| **Two channels: reliable / unreliable** | Choosing per message is the core of this library |
| **Stateless cookie handshake** | HMAC-based. A SYN flood can't fill up server memory |
| **Session lifecycle management** | keepalive (PING/PONG), timeouts, graceful FIN close, dead-link detection |
| **AES-256-GCM encryption** | Authenticated encryption + a 64-bit replay window |
| **Reed-Solomon FEC** | kcp-go compatible. −70% retransmits at 20% loss |
| **Allocation-free design** | **0 B/op** on the steady-state send/receive path (verified by benchmark) |
| **Zero-copy receive** | `ReliableReceivedSpan` hands you the KCP segment directly, no copy |
| **Thread-boundary APIs** | A send outbox for game threads + a receive event queue |
| **Min-heap scheduler** | Only updates sessions that are actually due, even with 10,000 sessions |
| **Observability** | Periodic snapshots of session count, retransmit rate, RTT p50/p95/p99 |
| **Zero external dependencies** | Both core and transport layer. MonoGame in the samples is the only exception |
| **Wire-compatible with original kcp** | `RawMode` talks directly to a kcp-go server |

## Performance

Measured with client and server running together on the same PC. Reproduction commands are in
[docs/benchmarks.md](docs/benchmarks.md).

| Metric | Value |
|---|---|
| 1KB × 1,000 round trips | **321 µs** (441 µs before optimization, −27%) |
| Steady-state allocation | **0 B/op** |
| 1,000 sessions · 60s | 970,627 messages **all round-tripped**, 0 mismatches, 1.13% retransmit |
| 300 sessions · 60s | **0.00%** retransmit, RTT p99 **48.5ms**, **0** Gen2 collections |
| Encryption cost | **0.36 µs** per packet (at 1,376B) |

> The 1,000-session numbers above were measured with **client and server on the same PC**.
> They are not a standalone server capacity figure.

### How Many Clients Can It Handle

The receive loop is a single thread, so the ceiling is set by "what else you put on that thread."

| Setup | Recommended per process |
|---|---:|
| Plain KCP + game logic on a separate thread (`EnableEventQueue`) | 2,000–3,000 |
| Plain KCP + game logic run inside the event handler | 500–1,000 |
| Add AES-GCM | 10–15% less than the above |
| FEC on | 300–500 |

Session lookup is O(1) and the timer is an O(log n) min-heap, so the data structures were
designed with 10,000 sessions in mind. In practice, what you hit first is **memory**
(`MaxSendBacklog` × 2KB) and **game logic riding on the receive thread**. Sizing methodology
and the scale-out order are in [Chapter 14](https://jacking75.github.io/kcp-csharp/#/book/ch14-performance)
and [Chapter 17](https://jacking75.github.io/kcp-csharp/#/book/ch17-operations) of the book
(Korean only).

## Samples

Five samples in increasing difficulty, plus one tool. Each sample has its own README.

| Sample | Difficulty | What you learn |
|---|---|---|
| [01-Echo](samples/01-Echo) | ★1 | The minimal cycle: connect, send/receive, close |
| [02-Chat](samples/02-Chat) | ★2 | Designing an application protocol, server-side validation |
| [03-Gomoku](samples/03-Gomoku) | ★3 | Authoritative server, reconnection, intro to MonoGame |
| [04-MoveSync](samples/04-MoveSync) | ★4 | Fixed tick rate, channel separation, snapshot interpolation |
| [05-TankBattle](samples/05-TankBattle) | ★5 | Client prediction / server reconciliation, lag compensation |
| [tools/LoadTest](samples/tools/LoadTest) | — | Load measurement (throughput, retransmit rate, RTT, GC) |

## Online Book

**[Learning Real-Time Game Networking with C# and KCP](https://jacking75.github.io/kcp-csharp/)**
— 17 chapters + 5 appendices. (The book itself is available in Korean only.)

- **Part 1, Principles** — games and networking, ARQ, anatomy of the KCP protocol
- **Part 2, Usage** — getting started, the core API, sessions, choosing a channel, tuning
- **Part 3, Practice** — a 1:1 match with the five samples above
- **Part 4, Depth** — allocation-free performance, security, FEC, operations

Every code snippet in the book is lifted straight from the code in `samples/`, and
**the original file path is noted with every code block.**

**Want a deep, file-by-file walkthrough of the source itself?** See
[`docs/code-guide.md`](docs/code-guide.md) — a very detailed explanation of every file in the
core and transport layer, line by line (Korean only).

## Installation

```bash
# NuGet (after publishing)
dotnet add package KcpCSharpR1
dotnet add package KcpCSharpR1.Transport

# Source reference
dotnet add reference path/to/kcp-csharp/src/KcpCSharpR1/KcpCSharpR1.csproj
dotnet add reference path/to/kcp-csharp/src/KcpCSharpR1.Transport/KcpCSharpR1.Transport.csproj
```

| Target | Use for |
|---|---|
| `net8.0` | Servers, MonoGame clients |
| `netstandard2.1` | Godot C#, older runtimes |

### Recommended Host Settings

Even an allocation-free library will still show GC pauses **if the host's GC settings are wrong.**
Add this to your server app's csproj.

```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <TieredPGO>true</TieredPGO>
</PropertyGroup>
```

For a client (MonoGame), leave workstation GC as-is and rely on zero-copy receive
(`ReliableReceivedSpan`) instead. Details are in
[Chapter 17, Operations](https://jacking75.github.io/kcp-csharp/#/book/ch17-operations) of the
book (Korean only).

## NuGet Publishing (For Maintainers)

The procedure for pushing a new version to nuget.org. Only two projects are packable —
`src/KcpCSharpR1` and `src/KcpCSharpR1.Transport` (`Directory.Build.props` defaults
`IsPackable=false` and turns it on only for these two).

### 1. Bump the Version

Change `<Version>` in `Directory.Build.props`. A two-part SemVer (e.g. `0.11`) is fine —
NuGet normalizes it to three parts (`0.11.0`) automatically.

```xml
<Version>0.11.0</Version>
```

### 2. Verify Build and Tests

```bash
dotnet build -c Release      # 0 warnings
dotnet test  -c Release      # all passing
```

### 3. Pack

```bash
dotnet pack -c Release -o artifacts/nupkg src/KcpCSharpR1/KcpCSharpR1.csproj
dotnet pack -c Release -o artifacts/nupkg src/KcpCSharpR1.Transport/KcpCSharpR1.Transport.csproj
```

`artifacts/` is gitignored. Make it a habit to open the `.nupkg` and check that both targets'
(`net8.0`/`netstandard2.1`) dlls and xml docs are there, along with `README.md` and `LICENSE`,
and that the `.nuspec`'s `<dependencies>` points at the version you intended.

### 4. API Key (One-Time Setup)

[nuget.org profile](https://www.nuget.org/profiles/jacking75) → **API Keys** → **Create**.

- Select Scopes: **Push (new package & package version)**
- Glob Pattern: `KcpCSharpR1*` — scope it to just these two packages
- Expiration: up to 365 days; reissue when it expires

Once you register the key locally, you can push without `--api-key` afterward
(on Windows it's encrypted with DPAPI and stored at `%APPDATA%\NuGet\NuGet.Config`).

```bash
dotnet nuget setapikey <your_key> --source https://api.nuget.org/v3/index.json
```

### 5. Push — Core First, Transport Second

Push in this order since Transport depends on the core. The `.snupkg` (symbol package) is
pushed automatically by the same command.

```bash
dotnet nuget push artifacts/nupkg/KcpCSharpR1.<version>.nupkg           --source https://api.nuget.org/v3/index.json
dotnet nuget push artifacts/nupkg/KcpCSharpR1.Transport.<version>.nupkg --source https://api.nuget.org/v3/index.json
```

**A version number, once pushed, cannot be undone** (you can unlist it, but not delete or
reuse it). Before pushing, double-check that the files in `artifacts/nupkg/` are the ones you
just built.

## Repository Structure

```
src/
  KcpCSharpR1/            Protocol core (zero dependencies)
  KcpCSharpR1.Transport/  Sockets, sessions, handshake, encryption, FEC
tests/
  KcpCSharpR1.Tests/      Unit tests
  KcpCSharpR1.SimTests/   Virtual-network scenarios + real-socket integration
benchmarks/              BenchmarkDotNet
samples/                 5 samples + LoadTest
docs/                    Online book + benchmark log + roadmap
```

## Build and Test

```bash
dotnet build -c Release              # keep it at 0 warnings
dotnet test  -c Release              # 238 tests
dotnet test  -c Release --filter "Category!=Soak"   # skip long-running tests
dotnet run   -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*'
```

## Lineage

This repository isn't a straight copy of the original — it's a **port and rebuild in C#**
that follows the lineage below.

[skywind3000/kcp](https://github.com/skywind3000/kcp) (C, MIT) →
[xtaci/kcp-go](https://github.com/xtaci/kcp-go) (Go, MIT) → **KcpCSharpR1** (C#, this repository).

| Carried over | Newly built |
|---|---|
| Protocol logic (ARQ, sliding window, RTO calculation, congestion control) — ported from kcp-go | Transport layer (handshake, session lifecycle, scheduler) |
| The core 24-byte header — **byte-for-byte compatible with the original** | AES-256-GCM encryption, Reed-Solomon FEC |
| | 5 samples + the LoadTest tool |
| | The 17-chapter online book + 5 appendices |

The protocol logic was rewritten using C# idioms (`Span<byte>`, `ArrayPool`, event-based
sessions, an injectable clock), but the wire format was kept as-is. Turning on `RawMode` lets
you talk directly to an original kcp / kcp-go server
([Appendix C](docs/book/appendix-c-interop.md), Korean only).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
Bug reports are most useful when they come with a reproducible test — the virtual-time
harness (`tests/KcpCSharpR1.SimTests/KcpPair.cs`) makes deterministic repro possible.

## License

MIT. See [LICENSE](LICENSE).
