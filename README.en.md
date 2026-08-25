# kcp-csharp

**A C# library that adds exactly as much reliability as you need on top of UDP.** A KCP implementation for real-time games.

[![CI](https://github.com/jacking75/kcp-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/jacking75/kcp-csharp/actions/workflows/ci.yml)
![net8.0](https://img.shields.io/badge/net-8.0-512BD4)
![netstandard2.1](https://img.shields.io/badge/netstandard-2.1-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

📖 **[Online book (Korean): C#과 KCP로 배우는 실시간 게임 네트워킹](https://jacking75.github.io/kcp-csharp/)**

> The full documentation and the accompanying book are written in Korean.
> This page is a short English summary of what the library does.

---

## Why not TCP

TCP guarantees that every byte arrives **in order**. That is perfect for file transfer and
harmful for games: when one packet is lost, every newer packet that already arrived is held
back until the missing one is retransmitted (head-of-line blocking). That is why a character
freezes for 250 ms and then teleports.

KCP puts reliability only where you want it.

| Channel | Guarantees | Use for |
|---|---|---|
| **Reliable** (`Send`) | delivery + ordering | chat, inventory, hit results |
| **Unreliable** (`SendUnreliable`) | none | position snapshots, inputs |

It also recovers more aggressively than TCP — 1.5× RTO backoff (TCP uses 2×), 30 ms minimum RTO
(TCP is 200 ms+), fast retransmit plus early retransmit. **You spend a little bandwidth to buy latency.**

## Quick start

```bash
git clone https://github.com/jacking75/kcp-csharp
cd kcp-csharp
dotnet run --project samples/01-Echo/Server     # terminal 1
dotnet run --project samples/01-Echo/Client     # terminal 2
```

```csharp
// Server
using var server = new KcpServer(27001);
server.ReliableReceived += (session, data) => session.Send(data.Span);   // echo
server.Start();

// Start() spawns a background thread and returns immediately — hold the process here.
using var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
quit.Wait();

server.Stop();
```

```csharp
// Client
using var client = new KcpClient();
client.ReliableReceived += (_, data) => Console.WriteLine(Encoding.UTF8.GetString(data.Span));

var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 27001));
session.Send(Encoding.UTF8.GetBytes("Hello KCP"));

while (true)
{
    client.Update();   // ★ nothing happens unless you call this
    await Task.Delay(10);
}
```

## Features

| | |
|---|---|
| **Reliable + unreliable channels** | picking per message is the point of this library |
| **Stateless cookie handshake** | HMAC based; a SYN flood cannot fill the session table |
| **Session lifetime** | keepalive (PING/PONG), timeout, graceful FIN, dead-link detection |
| **AES-256-GCM** | authenticated encryption with a 64-bit replay window |
| **Reed-Solomon FEC** | kcp-go compatible; −70 % retransmits at 20 % loss |
| **Zero-allocation steady state** | **0 B/op** on the send/receive path (verified by benchmarks) |
| **Min-heap scheduler** | only sessions that are actually due get updated |
| **Observability** | periodic snapshot of session count, retransmit rate, RTT p50/p95/p99 |
| **No external dependencies** | core and transport alike; only the samples use MonoGame |
| **Wire compatible with the original kcp** | `RawMode` talks to kcp / kcp-go servers directly |

## Measured performance

Client and server running on the same machine. Reproduction commands are in [docs/benchmarks.md](docs/benchmarks.md).

| Item | Result |
|---|---|
| 1 KB × 1,000 round trips | **321 µs** (was 441 µs, −27 %) |
| Steady-state allocation | **0 B/op** |
| 1,000 sessions · 60 s | 970,627 messages **all echoed back**, 0 mismatches, 1.13 % retransmit |
| 300 sessions · 60 s | **0.00 %** retransmit, RTT p99 **48.5 ms**, **0** Gen2 collections |
| Encryption cost | **0.36 µs** per 1,376-byte packet |

## Samples

| Sample | Level | Teaches |
|---|---|---|
| [01-Echo](samples/01-Echo) | ★1 | connect / send / receive / disconnect |
| [02-Chat](samples/02-Chat) | ★2 | designing an application protocol, server-side validation |
| [03-Gomoku](samples/03-Gomoku) | ★3 | authoritative server, reconnect, MonoGame basics |
| [04-MoveSync](samples/04-MoveSync) | ★4 | fixed tick, channel split, snapshot interpolation |
| [05-TankBattle](samples/05-TankBattle) | ★5 | client prediction, reconciliation, lag compensation |
| [tools/LoadTest](samples/tools/LoadTest) | — | load measurement (throughput, retransmits, RTT, GC) |

## Install

```bash
dotnet add package KcpProject
dotnet add package KcpProject.Transport
```

Targets `net8.0` and `netstandard2.1`.

## Build and test

```bash
dotnet build -c Release
dotnet test  -c Release
dotnet run   -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*'
```

## Lineage

[skywind3000/kcp](https://github.com/skywind3000/kcp) (C) →
[xtaci/kcp-go](https://github.com/xtaci/kcp-go) (Go) → **kcp-csharp** (C#).

The 24-byte core header is byte-for-byte compatible with the original.

## License

MIT.
