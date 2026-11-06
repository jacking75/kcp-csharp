using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KcpCSharpR1;
using KcpCSharpR1.Transport;

/// <summary>소스 프로젝트 참조 없이 배포 패키지의 공개 API를 실행한다.</summary>
public static class PackageProbe
{
    public static async Task RunAsync(string expectedFramework, int port)
    {
        AssertFramework(typeof(Kcp).Assembly, expectedFramework);
        AssertFramework(typeof(KcpClient).Assembly, expectedFramework);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failures = new ConcurrentQueue<Exception>();
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverSession = new KcpSessionOptions { FinLingerMs = 100 };
        var clientSession = new KcpSessionOptions { SendOutboxCapacity = 8, FinLingerMs = 100 };
        var factoryCiphers = new ConcurrentBag<ProbeCipher>();
        if (expectedFramework == ".NETStandard,Version=v2.1")
        {
            PacketCipherFactory factory = (conversation, serverDirection) =>
            {
                var cipher = new ProbeCipher(conversation, serverDirection);
                factoryCiphers.Add(cipher);
                return cipher;
            };
            serverSession.CipherFactory = factory;
            clientSession.CipherFactory = factory;
            serverSession.CipherFactoryOverhead = ProbeCipher.HeaderSize;
            clientSession.CipherFactoryOverhead = ProbeCipher.HeaderSize;
            Console.WriteLine("Testing custom cipher factory with a NONCRYPTOGRAPHIC test-only framing cipher.");
        }
        else
        {
            var key = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
            serverSession.EncryptionKey = key;
            clientSession.EncryptionKey = key;
            Console.WriteLine("Testing built-in AES-GCM with a fresh key for this server lifetime.");
        }
        using var server = new KcpServer(port, new KcpServerOptions { Session = serverSession });
        server.Error += failures.Enqueue;
        server.SessionDisconnected += (_, _) => disconnected.TrySetResult(true);
        server.ReliableReceivedSpan += (session, data) =>
        {
            if (session.Send(data) != KcpSendResult.Ok)
                failures.Enqueue(new InvalidOperationException("Reliable echo was rejected."));
        };
        server.UnreliableReceivedSpan += (session, data) =>
        {
            if (session.SendUnreliable(data) != KcpSendResult.Ok)
                failures.Enqueue(new InvalidOperationException("Unreliable echo was rejected."));
        };
        server.Start();

        using var client = new KcpClient(clientSession);
        var events = client.EnableEventQueue(64);

        // 응답하지 않는 실제 UDP 소켓으로 연결 도중 취소와 같은 클라이언트의 재시도를 검증한다.
        using (var blackhole = new UdpSocketTransport(AddressFamily.InterNetwork))
        using (var cancelConnect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            blackhole.Bind(new IPEndPoint(IPAddress.Loopback, port + 1));
            cancelConnect.CancelAfter(50);
            try
            {
                await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port + 1), cancelConnect.Token)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("Connection to a silent peer unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (cancelConnect.IsCancellationRequested)
            {
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        Require(client.Session == null, "Canceled connection retained its session.");

        var connected = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), deadline.Token)
            .ConfigureAwait(false);
        Require(client.IsConnected, "ConnectAsync did not establish a session.");

        var payloads = new[] { Payload(4096, 11), Payload(19, 23), Payload(1024, 37) };
        Require(connected.Send(payloads[0]) == KcpSendResult.Ok, "Fragmented reliable send failed.");
        Require(connected.Send(payloads[1]) == KcpSendResult.Ok, "Second reliable send failed.");
        Require(await Task.Run(() => connected.TrySendFromAnyThread(payloads[2]), deadline.Token)
            .ConfigureAwait(false), "Cross-thread outbox send failed.");

        var snapshot = Payload(24, 51);
        var reliableCount = 0;
        var unreliableReceived = false;
        var connectedEvents = 0;
        var clock = Stopwatch.StartNew();
        var nextSnapshotAt = 0L;
        while (reliableCount != payloads.Length || !unreliableReceived)
        {
            deadline.Token.ThrowIfCancellationRequested();
            // UDP는 재전송을 보장하지 않는다. 반복 상태 전송으로 두 채널을 함께 검증한다.
            if (!unreliableReceived && clock.ElapsedMilliseconds >= nextSnapshotAt)
            {
                Require(connected.SendUnreliable(snapshot) == KcpSendResult.Ok, "Unreliable send failed.");
                nextSnapshotAt = clock.ElapsedMilliseconds + 20;
            }
            client.Update();
            while (events.TryRead(out var ev))
            {
                try
                {
                    switch (ev.Type)
                    {
                        case KcpEventType.Connected:
                            connectedEvents++;
                            break;
                        case KcpEventType.Reliable:
                            Require(reliableCount < payloads.Length, "Duplicate reliable message.");
                            Require(ev.Data.SequenceEqual(payloads[reliableCount]), "Reliable data/order mismatch.");
                            reliableCount++;
                            break;
                        case KcpEventType.Unreliable:
                            Require(ev.Data.SequenceEqual(snapshot), "Unreliable data mismatch.");
                            unreliableReceived = true;
                            break;
                        case KcpEventType.Disconnected:
                            throw new InvalidOperationException("Session disconnected during echo.");
                    }
                }
                finally { ev.ReturnPayload(); }
            }
            ThrowServerErrors(failures);
            await Task.Delay(2, deadline.Token).ConfigureAwait(false);
        }
        Require(connectedEvents == 1, "Expected exactly one connected event.");
        Require(events.Dropped == 0, "Receive queue overflowed.");
        Require(connected.OutboxRejected == 0, "Outbox rejected an accepted message.");

        client.Disconnect();
        var closedEvent = false;
        while (!closedEvent || !disconnected.Task.IsCompleted)
        {
            deadline.Token.ThrowIfCancellationRequested();
            client.Update();
            while (events.TryRead(out var ev))
            {
                try
                {
                    if (ev.Type == KcpEventType.Disconnected) closedEvent = true;
                }
                finally { ev.ReturnPayload(); }
            }
            ThrowServerErrors(failures);
            await Task.Delay(2, deadline.Token).ConfigureAwait(false);
        }
        Require(connected.State == KcpSessionState.Closed, "Client did not close gracefully.");
        client.Dispose();
        server.Stop();
        ThrowServerErrors(failures);
        if (expectedFramework == ".NETStandard,Version=v2.1")
        {
            Require(factoryCiphers.Count == 2, "Expected one distinct factory cipher per peer.");
            foreach (var cipher in factoryCiphers)
                Require(cipher.Disposed, "Session did not dispose its factory-created cipher.");
        }
        Console.WriteLine($"PASS {expectedFramework}: cancellation/retry, reliable ordering/fragmentation, " +
            "unreliable echo, event queue, cross-thread outbox, graceful close.");
    }

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 13 + seed);
        return bytes;
    }

    private static void AssertFramework(Assembly assembly, string expected)
    {
        var actual = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        Require(actual == expected, $"Wrong package asset loaded: {assembly.GetName().Name}: {actual}, expected {expected}.");
        Console.WriteLine($"Loaded {assembly.GetName().Name}: {actual} ({assembly.Location})");
    }

    private static void ThrowServerErrors(ConcurrentQueue<Exception> failures)
    {
        if (failures.TryDequeue(out var error)) throw new InvalidOperationException("Server callback failed.", error);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // 암호화가 아니다. 팩토리 수명·방향·큰 오버헤드 처리를 확인하는 테스트 전용 포맷이다.
    private sealed class ProbeCipher(uint conversation, bool serverDirection) : IPacketCipher, IDisposable
    {
        public const int HeaderSize = 96;
        public int Overhead => HeaderSize;
        public bool Disposed { get; private set; }

        public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination)
        {
            Require(!Disposed, "Disposed factory cipher was reused.");
            destination.Slice(0, HeaderSize).Fill(0xA7);
            BinaryPrimitives.WriteUInt32LittleEndian(destination, conversation);
            destination[4] = serverDirection ? (byte)1 : (byte)0;
            plaintext.CopyTo(destination.Slice(HeaderSize));
            return plaintext.Length + HeaderSize;
        }

        public bool TryDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> destination, out int plaintextLength)
        {
            Require(!Disposed, "Disposed factory cipher was reused.");
            plaintextLength = 0;
            if (ciphertext.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(ciphertext) != conversation ||
                ciphertext[4] != (serverDirection ? (byte)0 : (byte)1)) return false;
            for (var i = 5; i < HeaderSize; i++)
                if (ciphertext[i] != 0xA7) return false;
            plaintextLength = ciphertext.Length - HeaderSize;
            ciphertext.Slice(HeaderSize).CopyTo(destination);
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
