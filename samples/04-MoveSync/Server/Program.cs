using System;
using System.Diagnostics;
using System.Threading;
using MoveSync.Shared;

namespace MoveSync.Server;

/// <summary>샘플 ④ MoveSync 서버 진입점 — 20Hz 고정 틱 루프.</summary>
internal static class Program
{
    private static void Main()
    {
        using var server = new WorldServer(ProtocolLimits.Port, Log);
        server.Start();
        Log($"MoveSync 서버가 UDP {ProtocolLimits.Port} 에서 대기 중입니다 ({ProtocolLimits.TickRate}Hz). Ctrl+C 로 종료합니다.");

        var running = true;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

        // 고정 틱 루프: 목표 시각을 누적해 드리프트를 막는다.
        var watch = Stopwatch.StartNew();
        var tickInterval = 1000.0 / ProtocolLimits.TickRate;
        var nextTickMs = tickInterval;
        var reportAt = 5000.0;
        var maxTickMs = 0.0;

        while (running)
        {
            var now = watch.Elapsed.TotalMilliseconds;

            if (now < nextTickMs)
            {
                var sleep = (int)(nextTickMs - now);
                if (sleep > 1) Thread.Sleep(sleep - 1);
                continue;
            }

            server.Step();
            if (server.LastTickMs > maxTickMs) maxTickMs = server.LastTickMs;

            nextTickMs += tickInterval;

            // 밀렸으면 따라잡기를 포기하고 기준을 다시 잡는다(스파이럴 방지).
            if (nextTickMs < now) nextTickMs = now + tickInterval;

            if (now >= reportAt)
            {
                reportAt += 5000.0;
                Log($"틱 {server.Tick}, 인원 {server.PlayerCount}, 틱 처리 최대 {maxTickMs:F2}ms");
                maxTickMs = 0;
            }
        }

        Log("종료합니다.");
        server.Stop();
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
