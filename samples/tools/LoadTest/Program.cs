using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KcpProject;
using KcpProject.Transport;

namespace LoadTest;

/// <summary>
/// 순수 전송 부하 측정 도구(P7-6).
/// </summary>
/// <remarks>
/// Phase 3·4 완료 조건의 검증 도구이자 책 8장(튜닝)·17장(운영)의 실습 도구다.
/// 게임 로직이 전혀 없는 <b>전송 계층만의 부하</b>를 잰다.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!Options.TryParse(args, out var options, out var error))
        {
            Console.WriteLine(error);
            Console.WriteLine();
            Console.WriteLine(Options.Usage);
            return 1;
        }

        try
        {
            return options.Mode == RunMode.Server
                ? RunServer(options)
                : await RunClientAsync(options);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    // ---------------- 서버 ----------------

    private static int RunServer(Options options)
    {
        var serverOptions = new KcpServerOptions { MaxSessions = 65536 };
        options.ApplyPreset(serverOptions.Session);
        serverOptions.MetricsIntervalMs = 5_000;

        using var server = new KcpServer(options.Port, serverOptions);

        long echoed = 0;
        long dropped = 0;

        // 받은 것을 그대로 돌려준다. 클라이언트는 왕복 시간으로 RTT 를 잰다.
        //
        // ★ Send 의 반환값을 반드시 본다. 세션의 송신 백로그가 상한(MaxSendBacklog)을 넘으면
        //   라이브러리는 Backpressure 를 돌려주고 메시지를 받아 주지 않는다. 이것을 무시하면
        //   "재전송은 0인데 에코가 안 온다"는 이해 못 할 결과가 나온다(실제로 그랬다).
        server.ReliableReceived += (session, data) =>
        {
            if (session.Send(data.Span) == KcpSendResult.Ok) Interlocked.Increment(ref echoed);
            else Interlocked.Increment(ref dropped);
        };
        server.UnreliableReceived += (session, data) =>
        {
            if (session.SendUnreliable(data.Span) == KcpSendResult.Ok) Interlocked.Increment(ref echoed);
            else Interlocked.Increment(ref dropped);
        };

        server.MetricsSampled += metrics =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {metrics}, 에코 {Interlocked.Read(ref echoed):N0}" +
                              $", 백프레셔로 버림 {Interlocked.Read(ref dropped):N0}");

        server.Error += ex => Console.WriteLine($"오류: {ex.Message}");

        server.Start();
        Console.WriteLine($"LoadTest 서버가 UDP {options.Port} 에서 대기 중입니다(프리셋 {options.Preset}). Ctrl+C 로 종료합니다.");

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        quit.Wait();

        var totalDropped = Interlocked.Read(ref dropped);
        Console.WriteLine($"종료합니다. 총 에코 {Interlocked.Read(ref echoed):N0}건, 백프레셔로 버린 것 {totalDropped:N0}건.");
        if (totalDropped > 0)
        {
            Console.WriteLine("! 서버가 감당할 수 있는 양을 넘었습니다. 세션 수나 전송률을 줄이거나,");
            Console.WriteLine("  MaxSendBacklog 를 키우고(지연 증가) 다시 재 보세요.");
        }
        server.Stop();
        return 0;
    }

    // ---------------- 클라이언트 ----------------

    /// <summary>부하 세션 하나의 상태.</summary>
    private sealed class LoadSession
    {
        public KcpClient Client = null!;
        public KcpSession Session = null!;
        public byte[] Payload = null!;
        public long NextSendTicks;
        // 워커 스레드가 갱신하고 메인 스레드가 읽는다 — Interlocked 로만 만진다.
        public long Sent;
        public long Echoed;
        public long Mismatched;
        public readonly LatencyHistogram Rtts = new();
    }

    private static async Task<int> RunClientAsync(Options options)
    {
        var remote = new IPEndPoint(await ResolveAsync(options.Host), options.Port);
        Console.WriteLine($"{remote} 로 세션 {options.Sessions}개를 엽니다...");

        var sessions = new List<LoadSession>(options.Sessions);
        var sendIntervalTicks = Stopwatch.Frequency / Math.Max(1, options.Rate);

        for (var i = 0; i < options.Sessions; i++)
        {
            var sessionOptions = new KcpSessionOptions
            {
                SessionTimeoutMs = 30_000,
                PingIntervalMs = 5_000,
                // 부하 도구는 밀리면 흘려보내는 편이 낫다. 상한을 크게 잡으면 세션 하나가
                // 수 MB 의 대여 버퍼를 물고 있어 메모리가 폭증한다(1,000세션에서 관리 힙 1GB 를 봤다).
                MaxSendBacklog = 256,
            };
            options.ApplyPreset(sessionOptions);

            var load = new LoadSession
            {
                Client = new KcpClient(sessionOptions),
                Payload = new byte[options.Size],
            };

            // 세션마다 알아볼 수 있는 값을 채워 둔다(무결성 검증용).
            for (var b = 8; b < load.Payload.Length; b++)
                load.Payload[b] = (byte)(i + b);

            load.Client.ReliableReceived += (_, data) => OnEcho(load, data.Span);
            load.Client.UnreliableReceived += (_, data) => OnEcho(load, data.Span);

            try
            {
                // 세션이 많으므로 소켓 버퍼를 낮춘다(기본 1MB × 2 × N 은 메모리를 통째로 먹는다).
                var transport = new UdpSocketTransport(
                    AddressFamily.InterNetwork, options.SocketBufferSize, options.SocketBufferSize);
                transport.Bind(new IPEndPoint(IPAddress.Any, 0));

                load.Session = await load.Client.ConnectAsync(transport, remote);
                sessions.Add(load);
            }
            catch (TimeoutException)
            {
                load.Client.Dispose();
                Console.WriteLine($"세션 {i} 연결 실패(타임아웃). 서버가 떠 있는지 확인하세요.");
                break;
            }

            // ★ 이미 연결된 세션들도 계속 돌려 줘야 한다.
            // 세션을 순차로 여는 동안(1,000개면 십수 초) 앞서 연결된 세션이 keepalive 를 못 보내면
            // 서버가 SessionTimeout(기본 10초)으로 끊어 버린다. 실제로 1,000세션 중 325개만
            // 살아남는 현상을 이 한 줄이 없어서 겪었다.
            if ((i + 1) % 16 == 0)
            {
                foreach (var existing in sessions) existing.Client.Update();
            }

            if ((i + 1) % 100 == 0)
            {
                var alive = 0;
                foreach (var existing in sessions)
                    if (existing.Session.State == KcpSessionState.Connected) alive++;
                Console.WriteLine($"  {i + 1}/{options.Sessions} 연결 (생존 {alive})");
            }
        }

        if (sessions.Count == 0)
        {
            Console.WriteLine("연결된 세션이 없습니다.");
            return 1;
        }

        var threadCount = options.Threads > 0
            ? options.Threads
            : Math.Clamp(Environment.ProcessorCount - 1, 1, 16);

        Console.WriteLine($"{sessions.Count}개 연결 완료. 워커 {threadCount}개로 {options.DurationSeconds}초 측정합니다...");

        // 측정 시작 직전에 기준선을 뜬다.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = Report.SampleRuntime();

        var watch = Stopwatch.StartNew();
        var endTicks = Stopwatch.Frequency * options.DurationSeconds;

        // 세션을 스레드별로 나눈다. 세션 하나는 반드시 한 스레드가 소유한다(라이브러리 스레딩 규약).
        var partitions = new List<LoadSession>[threadCount];
        for (var i = 0; i < threadCount; i++) partitions[i] = new List<LoadSession>();
        for (var i = 0; i < sessions.Count; i++) partitions[i % threadCount].Add(sessions[i]);

        var workers = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var partition = partitions[i];
            workers[i] = new Thread(() => DriveSessions(partition, options, watch, endTicks))
            {
                IsBackground = true,
                Name = $"load-{i}",
            };
            workers[i].Start();
        }

        // 진행 상황만 메인 스레드에서 찍는다.
        var nextProgress = Stopwatch.Frequency * 5;
        while (watch.ElapsedTicks < endTicks)
        {
            if (watch.ElapsedTicks >= nextProgress)
            {
                nextProgress += Stopwatch.Frequency * 5;
                long sent = 0, echoed = 0, alive = 0;
                foreach (var load in sessions)
                {
                    sent += Interlocked.Read(ref load.Sent);
                    echoed += Interlocked.Read(ref load.Echoed);
                    if (load.Session.State == KcpSessionState.Connected) alive++;
                }
                Console.WriteLine($"  {watch.Elapsed.TotalSeconds,5:F0}초 — 보냄 {sent:N0}, 에코 {echoed:N0}, 살아 있는 세션 {alive:N0}");
            }

            await Task.Delay(50);
        }

        foreach (var worker in workers) worker.Join(TimeSpan.FromSeconds(5));
        watch.Stop();

        // 남은 에코를 끝까지 받아 낸다.
        // 세션이 많으면 측정 종료 시점에 아직 회선에 떠 있는 메시지가 상당하다.
        // 1초만 기다리고 끊으면 "에코 손실"이 실제보다 훨씬 크게 잡힌다(측정 함정).
        Console.WriteLine("  잔여 에코 수신 중...");
        var drain = Stopwatch.StartNew();
        var lastEchoed = CountEchoed(sessions);
        var idleMs = 0L;

        while (drain.ElapsedMilliseconds < 30_000 && idleMs < 3_000)
        {
            foreach (var load in sessions) load.Client.Update();
            await Task.Delay(1);

            var echoed = CountEchoed(sessions);
            if (echoed > lastEchoed)
            {
                lastEchoed = echoed;
                idleMs = 0;
            }
            else
            {
                idleMs += 1;
            }
        }

        var after = Report.SampleRuntime();
        var report = BuildReport(options, sessions, watch.Elapsed.TotalSeconds, before, after);

        report.Print(options);
        if (options.CsvPath != null)
            report.WriteCsv(options.CsvPath, options);

        foreach (var load in sessions)
        {
            load.Client.Disconnect();
            load.Client.Update();
        }
        foreach (var load in sessions) load.Client.Dispose();

        return report.Mismatched == 0 ? 0 : 1;
    }

    /// <summary>한 워커 스레드가 자기 몫의 세션을 구동한다.</summary>
    private static void DriveSessions(List<LoadSession> partition, Options options, Stopwatch watch, long endTicks)
    {
        var sendIntervalTicks = Stopwatch.Frequency / Math.Max(1, options.Rate);

        while (watch.ElapsedTicks < endTicks)
        {
            var now = watch.ElapsedTicks;

            foreach (var load in partition)
            {
                if (load.Session.State != KcpSessionState.Connected) continue;

                if (now >= load.NextSendTicks)
                {
                    load.NextSendTicks = now + sendIntervalTicks;

                    // 앞 8바이트에 송신 시각을 넣어 두면 에코가 돌아왔을 때 RTT 가 나온다.
                    BinaryPrimitives.WriteInt64LittleEndian(load.Payload, Stopwatch.GetTimestamp());

                    var result = options.Unreliable
                        ? load.Session.SendUnreliable(load.Payload)
                        : load.Session.Send(load.Payload);

                    if (result == KcpSendResult.Ok) Interlocked.Increment(ref load.Sent);
                }

                load.Client.Update();
            }

            // 세션이 적으면 CPU 를 다 태우지 않도록 잠깐 쉰다.
            if (partition.Count < 64) Thread.Sleep(1);
            else Thread.Yield();
        }
    }

    private static long CountEchoed(List<LoadSession> sessions)
    {
        long total = 0;
        foreach (var load in sessions) total += load.Echoed;
        return total;
    }

    private static void OnEcho(LoadSession load, ReadOnlySpan<byte> data)
    {
        if (data.Length != load.Payload.Length)
        {
            Interlocked.Increment(ref load.Mismatched);
            return;
        }

        var sentAt = BinaryPrimitives.ReadInt64LittleEndian(data);
        Interlocked.Increment(ref load.Echoed);

        // 본문(타임스탬프 뒤)이 그대로인지 확인한다.
        if (!data.Slice(8).SequenceEqual(load.Payload.AsSpan(8)))
            Interlocked.Increment(ref load.Mismatched);

        load.Rtts.Record(TicksToMs(Stopwatch.GetTimestamp() - sentAt));
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static Report BuildReport(
        Options options,
        List<LoadSession> sessions,
        double elapsedSeconds,
        (int Gen0, int Gen1, int Gen2, long Heap, long WorkingSet) before,
        (int Gen0, int Gen1, int Gen2, long Heap, long WorkingSet) after)
    {
        long sent = 0, echoed = 0, mismatched = 0;
        var alive = 0;
        ulong bytesSent = 0, bytesReceived = 0, segmentsSent = 0, retransmit = 0;
        var rtts = new LatencyHistogram();

        foreach (var load in sessions)
        {
            sent += Interlocked.Read(ref load.Sent);
            echoed += Interlocked.Read(ref load.Echoed);
            mismatched += Interlocked.Read(ref load.Mismatched);
            if (load.Session.State == KcpSessionState.Connected) alive++;
            rtts.Merge(load.Rtts);

            var stats = load.Session.Stats;
            bytesSent += stats.BytesSent;
            bytesReceived += stats.BytesReceived;
            segmentsSent += stats.SegmentsSent;
            retransmit += stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments;
        }

        return new Report
        {
            ElapsedSeconds = elapsedSeconds,
            Sessions = sessions.Count,
            AliveSessions = alive,
            MessagesSent = sent,
            MessagesEchoed = echoed,
            Mismatched = mismatched,
            BytesSent = bytesSent,
            BytesReceived = bytesReceived,
            SegmentsSent = segmentsSent,
            RetransmitSegments = retransmit,
            Gen0 = after.Gen0 - before.Gen0,
            Gen1 = after.Gen1 - before.Gen1,
            Gen2 = after.Gen2 - before.Gen2,
            ManagedHeapBytes = after.Heap,
            WorkingSetBytes = after.WorkingSet,
            Rtt = rtts,
        };
    }

    private static async Task<IPAddress> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;

        var addresses = await Dns.GetHostAddressesAsync(host);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork) return address;
        }
        return addresses.Length > 0 ? addresses[0] : IPAddress.Loopback;
    }
}
