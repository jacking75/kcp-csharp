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

        // 받은 것을 그대로 돌려준다. 클라이언트는 왕복 시간으로 RTT 를 잰다.
        server.ReliableReceived += (session, data) =>
        {
            session.Send(data.Span);
            Interlocked.Increment(ref echoed);
        };
        server.UnreliableReceived += (session, data) =>
        {
            session.SendUnreliable(data.Span);
            Interlocked.Increment(ref echoed);
        };

        server.MetricsSampled += metrics =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {metrics}, 에코 {Interlocked.Read(ref echoed):N0}");

        server.Error += ex => Console.WriteLine($"오류: {ex.Message}");

        server.Start();
        Console.WriteLine($"LoadTest 서버가 UDP {options.Port} 에서 대기 중입니다(프리셋 {options.Preset}). Ctrl+C 로 종료합니다.");

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        quit.Wait();

        Console.WriteLine($"종료합니다. 총 에코 {Interlocked.Read(ref echoed):N0}건.");
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
        public long Sent;
        public long Echoed;
        public long Mismatched;
        public readonly List<double> Rtts = new();
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
                MaxSendBacklog = 4096,
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

            if ((i + 1) % 100 == 0)
                Console.WriteLine($"  {i + 1}/{options.Sessions} 연결");
        }

        if (sessions.Count == 0)
        {
            Console.WriteLine("연결된 세션이 없습니다.");
            return 1;
        }

        Console.WriteLine($"{sessions.Count}개 연결 완료. {options.DurationSeconds}초 측정합니다...");

        // 측정 시작 직전에 기준선을 뜬다.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = Report.SampleRuntime();

        var watch = Stopwatch.StartNew();
        var endTicks = watch.ElapsedTicks + Stopwatch.Frequency * options.DurationSeconds;
        var nextProgress = Stopwatch.Frequency * 5;

        while (watch.ElapsedTicks < endTicks)
        {
            var now = watch.ElapsedTicks;

            foreach (var load in sessions)
            {
                if (load.Session.State != KcpSessionState.Connected) continue;

                if (now >= load.NextSendTicks)
                {
                    load.NextSendTicks = now + sendIntervalTicks;

                    // 앞 8바이트에 송신 시각을 넣어 두면 에코가 돌아왔을 때 RTT 가 나온다.
                    // 프로세스 전역 단조 카운터를 쓴다(측정 루프의 Stopwatch 와 섞으면 기준이 어긋난다).
                    BinaryPrimitives.WriteInt64LittleEndian(load.Payload, Stopwatch.GetTimestamp());

                    var result = options.Unreliable
                        ? load.Session.SendUnreliable(load.Payload)
                        : load.Session.Send(load.Payload);

                    if (result == KcpSendResult.Ok) load.Sent++;
                }

                load.Client.Update();
            }

            if (now >= nextProgress)
            {
                nextProgress += Stopwatch.Frequency * 5;
                long sent = 0, echoed = 0;
                foreach (var load in sessions) { sent += load.Sent; echoed += load.Echoed; }
                Console.WriteLine($"  {watch.Elapsed.TotalSeconds,5:F0}초 — 보냄 {sent:N0}, 에코 {echoed:N0}");
            }

            await Task.Delay(1);
        }

        watch.Stop();

        // 남은 에코를 조금 더 받는다.
        var drain = Stopwatch.StartNew();
        while (drain.ElapsedMilliseconds < 1_000)
        {
            foreach (var load in sessions) load.Client.Update();
            await Task.Delay(1);
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

    private static void OnEcho(LoadSession load, ReadOnlySpan<byte> data)
    {
        if (data.Length != load.Payload.Length)
        {
            load.Mismatched++;
            return;
        }

        var sentAt = BinaryPrimitives.ReadInt64LittleEndian(data);
        load.Echoed++;

        // 본문(타임스탬프 뒤)이 그대로인지 확인한다.
        if (!data.Slice(8).SequenceEqual(load.Payload.AsSpan(8)))
            load.Mismatched++;

        load.Rtts.Add(TicksToMs(Stopwatch.GetTimestamp() - sentAt));
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
        ulong bytesSent = 0, bytesReceived = 0, segmentsSent = 0, retransmit = 0;
        var rtts = new List<double>();

        foreach (var load in sessions)
        {
            sent += load.Sent;
            echoed += load.Echoed;
            mismatched += load.Mismatched;
            rtts.AddRange(load.Rtts);

            var stats = load.Session.Stats;
            bytesSent += stats.BytesSent;
            bytesReceived += stats.BytesReceived;
            segmentsSent += stats.SegmentsSent;
            retransmit += stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments;
        }

        rtts.Sort();

        return new Report
        {
            ElapsedSeconds = elapsedSeconds,
            Sessions = sessions.Count,
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
            RttSamples = rtts,
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
