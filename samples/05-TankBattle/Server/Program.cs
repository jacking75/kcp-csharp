using System;
using System.Diagnostics;
using System.Threading;
using TankBattle.Shared;

namespace TankBattle.Server;

/// <summary>샘플 ⑤ TankBattle 서버 진입점.</summary>
internal static class Program
{
    /// <summary>진입점.</summary>
    /// <param name="args">[--bots N] [--no-lagcomp]</param>
    private static void Main(string[] args)
    {
        var bots = 0;
        var lagCompensation = true;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bots" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
            {
                bots = Math.Clamp(n, 0, GameLimits.MaxPlayers - 1);
                i++;
            }
            else if (args[i] == "--no-lagcomp")
            {
                lagCompensation = false;
            }
        }

        using var server = new BattleServer(GameLimits.Port, bots, lagCompensation, Log);
        server.Start();
        Log($"TankBattle 서버가 UDP {GameLimits.Port} 에서 대기 중입니다 " +
            $"({GameLimits.TickRate}Hz, 봇 {bots}, 지연보상 {(lagCompensation ? "ON" : "OFF")}). Ctrl+C 로 종료합니다.");

        var running = true;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

        var watch = Stopwatch.StartNew();
        var tickInterval = 1000.0 / GameLimits.TickRate;
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
