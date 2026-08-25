using System;
using System.Threading;
using Gomoku.Shared;

namespace Gomoku.Server;

/// <summary>샘플 ③ 오목 서버 진입점.</summary>
internal static class Program
{
    private static void Main()
    {
        using var server = new GameServer(ProtocolLimits.Port, Log);
        server.Start();
        Log($"오목 서버가 UDP {ProtocolLimits.Port} 에서 대기 중입니다. Ctrl+C 로 종료합니다.");

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };

        // 재접속 만료 검사를 위해 1초마다 깨운다.
        while (!quit.Wait(1000))
        {
            server.Tick(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        Log($"종료합니다(방 {server.RoomCount}개, 대기 {server.QueueCount}명).");
        server.Stop();
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
