# Integrating KcpCSharpR1 into a game

[한국어](game-integration_kr.md) · [Repository](../README.md)

Use `KcpCSharpR1.Transport` for a client/server game. It includes the protocol core
as a dependency. This guide describes the source in this checkout; consult
[CHANGELOG.md](../CHANGELOG.md) before using an older package.

```bash
dotnet add package KcpCSharpR1.Transport
```

## Choose an owner for the connection

Each session has one owner that processes network input, updates KCP, and sends
messages. The protocol core has no internal locks. Configure options before
constructing the client/server and do not mutate them while running.

| Model | Receive/update owner | Game thread sends with |
|---|---|---|
| MonoGame polling | `Game.Update`, after connection completes | `session.Send` / `session.SendUnreliable` |
| Client background loop | `client.RunAsync` | `session.TrySendFromAnyThread` (reliable only) |
| Server background loop | `server.Start()` | `session.TrySendFromAnyThread` (reliable only) |
| Server manual loop | `server.Start(null, runLoop: false)`, then `server.Pump()` | Direct sends on that same loop |

Do not run `Update` together with `ConnectAsync` or `RunAsync`, or call `Pump`
beside the server's background loop. An `async` method does not guarantee a
particular thread. Direct callbacks execute on the network owner, including
callbacks raised while `ConnectAsync` is still running.

### MonoGame: hand off after connecting

`ConnectAsync` drives its own handshake. A safe loading-screen sequence is:

1. Construct the client and call `EnableEventQueue` before connecting. Keep the
   returned queue. Do not subscribe game-state handlers to direct events.
2. Start one `ConnectAsync(endpoint, cancellationToken)` and retain its task.
   Do not block `Game.Update` waiting for it.
3. While that task is incomplete, render the loading screen without calling
   `client.Update`, sending, or changing the client configuration.
4. On the game thread, once the task is complete, observe its result with
   `GetAwaiter().GetResult()` inside your connection-error handling. This is
   nonblocking only because `IsCompleted` was checked first. Store the session
   and send the game's join message from this thread.
5. Every subsequent frame, call `client.Update()`, drain the event queue, and
   send input. The connection now belongs to the game thread. Do not also start
   `RunAsync`.

The queue is useful even in polling mode: the handshake may receive the first
game messages before `ConnectAsync` returns. It keeps those callbacks from
modifying the game world on a worker thread.
The shared [PollingKcpClient.cs](../samples/Common/PollingKcpClient.cs) implements
this handoff for Gomoku, MoveSync, and TankBattle. See `Initialize`, `OnConnected`,
and `Update` in [MoveSyncGame.cs](../samples/04-MoveSync/Client/MoveSyncGame.cs).

Drain on one consumer thread. Return every payload even if parsing fails:

```csharp
while (events.TryRead(out var ev))
{
    try
    {
        // Handle Connected, Disconnected, Reliable, and Unreliable here.
        // Parse ev.Data synchronously; copy anything that must outlive this event.
    }
    finally
    {
        ev.ReturnPayload();
    }
}
```

For a loading-screen cancel, cancel the connection token and observe the
connection task before disposing the client. Handle `OperationCanceledException`,
`TimeoutException`, and socket/DNS errors as distinct UI outcomes. Avoid
fire-and-forget connection tasks.

### Background networking

Set `SendOutboxCapacity` before constructing the client/server and enable its
event queue before starting. After a successful connection, retain the task
returned by `client.RunAsync(token)`. Do not call `client.Update` in this model.
The game thread only consumes queued events and submits reliable messages with
`TrySendFromAnyThread`. Direct callbacks remain active when the queue is enabled;
subscribing both mechanisms can process the same message twice.

`SendUnreliable`, `Disconnect`, and general session APIs are not cross-thread
commands. For background unreliable sending, arrange a bounded application
command queue that the network owner drains, or use the polling model. Merely
having a session reference from `KcpEvent.Session` does not transfer ownership.

For an authoritative game server, manual `Pump()` on its simulation thread is
another option. Run it often enough for the configured KCP interval and keep
database, disk, and other blocking work off that thread.

## Delivery, pressure, and buffer ownership

| API | Meaning of success | Caller responsibility |
|---|---|---|
| `Send` | Accepted into the local KCP send path | Check `KcpSendResult`; success is not a peer acknowledgment |
| `SendUnreliable` | Submitted to the UDP send path | Tolerate loss, duplicates, and reordering |
| `TrySendFromAnyThread` | Copied into a bounded local outbox | Check the Boolean result; it does not confirm delivery |
| `ReliableReceived` / `UnreliableReceived` | Synchronous callback | Memory is valid only during the callback |
| `ReliableReceivedSpan` / `UnreliableReceivedSpan` | Synchronous span callback | Parse immediately; do not retain the underlying storage |
| `KcpEventQueue.TryRead` | Transferred one queued event to the consumer | Call `ReturnPayload` once on the received value, in `finally` |

Do not copy a `KcpEvent` struct and return the payload through both copies.
Do not retain `ev.Data` after returning its buffer. Send APIs copy or consume
their input synchronously, so the caller can reuse its send buffer after return.

`MaxSendBacklog` is measured in KCP **segments**; `SendOutboxCapacity` and receive
queue capacity are measured in **messages/events**, not bytes. Limit application
message sizes as well as queue counts. Check `TooLarge`, `Backpressure`, and
`NotConnected` results. Retry essential commands from a bounded application queue;
coalesce replaceable state instead of retrying old snapshots indefinitely.

For game state, opt into disconnecting the affected session when a critical event
cannot fit in the bounded queue:

```csharp
var events = server.EnableEventQueue(4096,
    KcpEventQueueOverflowPolicy.DisconnectOnCriticalOverflow);
// The same overload is available on KcpClient.
```

Critical events are reliable messages, connections, and disconnections. A lost
reliable message or connection event requests FIN shutdown of that session;
unreliable overflow only drops the snapshot. A lost disconnection event is counted
without attempting to disconnect again. **The queue cannot guarantee delivery of
the shutdown event when it is already full.** Inspect `HasCriticalOverflow` and
changes to `DroppedReliable`, `DroppedConnected`, and `DroppedDisconnected` to
reconcile game state against session state. These counters persist across queue
drains and server restarts. `DroppedUnreliable` identifies snapshot losses, and
`Dropped` totals all types. The one-argument overload retains the original
`DropNewest` policy, which also drops critical events without closing sessions.

The sample adapter stops on any lost event and reports an error. Also monitor
`OutboxRejected`, RTT, retransmits, and game-loop stalls. Increase capacity only
after measuring consumer throughput.

## Separate commands from snapshots

Use reliable ordered messages for join/leave, chat, inventory changes, and match
results. The reliable channel has head-of-line blocking: a missing fragment holds
back later reliable messages on that session. Avoid bulk asset downloads there.
Delivery also ends when the session dies; durable transactions need application
acknowledgments and idempotent request IDs.

Use unreliable messages for replaceable snapshots and repeated input state.
Neither freshness nor ordering is provided automatically. Put an entity/tick or
sequence number in each message, reject duplicate/older values, and reset the
tracking state for a new session. A common unsigned 32-bit comparison is:

```csharp
static bool IsNewer(uint candidate, uint previous)
    => unchecked((int)(candidate - previous)) > 0;
```

This requires the sequence distance to stay below half the 32-bit range; accept
the first value separately. An input edge such as “fire once” needs reliable
delivery or an application redundancy/acknowledgment scheme. There is no ordering
between the reliable and unreliable channels: a snapshot can precede a reliable
spawn message. Include identity/generation information and handle that case.

Keep unreliable payloads within the configured MTU after framing/encryption
overhead; they are not fragmented by this transport. `SendUnreliable` reports
`TooLarge` when the limit is exceeded. See [MoveSync](../samples/04-MoveSync/README.md)
for interpolation and [TankBattle](../samples/05-TankBattle/README.md) for prediction
and reconciliation.

## Disconnect and reconnect

In polling mode, call `Disconnect` on the owner thread, then keep updating until
the session closes or the application's shutdown deadline expires. A FIN request
is not a guarantee that pending gameplay messages were consumed by the peer.
`Dispose` releases local resources; it is not a graceful-close acknowledgment.

For a background client, stop producers, cancel its loop token, and await the
loop task before calling direct session APIs or disposing. If graceful close is
needed, the owner can then call `Disconnect` and continue with serialized manual
updates during the linger interval. Stop the event consumer before disposing the
host. For a server, stop game producers/consumers, then call `Stop`/`Dispose` from
outside its network callbacks. Server shutdown waits for the network loop to
finish; callbacks must not block indefinitely.

A new client instance per reconnect is a simple ownership boundary. Discard old
session references, reset sequence tracking, reauthenticate, and ask the server
for an authoritative state snapshot. Transport reconnect does not restore game
membership or transactions automatically.

## Target and feature boundaries

- The projects build `net8.0` and `netstandard2.1`. The core has no package
  dependencies. Transport's `netstandard2.1` target references
  `System.Threading.Channels`; `net8.0` uses the framework implementation.
- The supplied graphical clients use MonoGame. A .NET Standard target is not
  certification for every engine, mobile device, console, or AOT runtime. Validate
  sockets, threading, and packaging on the exact target. The built-in transport
  uses native UDP sockets; this repository contains no browser/WebGL transport.
- Built-in `AesGcmPacketCipher` is implemented in the `net8.0` build. Its
  `netstandard2.1` build throws `PlatformNotSupportedException`; that target needs
  a compatible `IPacketCipher` implementation if encryption is required.
- For custom encryption, set `CipherFactory` and `CipherFactoryOverhead` on the
  session options. The factory receives the conversation ID and send direction
  and must create a new cipher for each session; the returned overhead must match
  the declared value. The session owns and disposes that cipher. A server rejects
  a directly supplied `Session.Cipher`, because sharing nonce/replay state between
  sessions is unsafe. Use `EncryptionKey` for the built-in .NET 8 server cipher.
- FEC and encryption cannot be enabled together. Encryption keys, player
  authentication, authorization, matchmaking, and NAT traversal are application
  responsibilities. The cookie handshake is not player authentication.
- Built-in AES-GCM includes the conversation ID in its nonce. IDs restart for
  each server instance, so **use a fresh EncryptionKey for every server lifetime
  and distinct keys across server instances**. A static global game key can
  repeat a key/nonce pair after a restart.
- `RawMode` is a Transport option for original KCP interoperability. It omits
  this transport's handshake, keepalive, FIN, and unreliable channel. Both peers
  must agree on the conversation ID and protocol settings.

Before shipping, exercise packet loss/reordering, a stalled game loop, a full
queue, connection cancellation, and reconnect against your real game protocol.
The deterministic harness in [tests/KcpCSharpR1.SimTests](../tests/KcpCSharpR1.SimTests)
and [LoadTest](../samples/tools/LoadTest/README.md) provide starting points.
