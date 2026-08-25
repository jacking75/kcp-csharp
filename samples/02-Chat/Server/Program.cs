using System;
using System.Threading;

namespace Chat.Server;

/// <summary>샘플 ② 채팅 서버 진입점.</summary>
internal static class Program
{
    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ② Chat = 27002.</summary>
    private const int Port = 27002;

    private static void Main()
    {
        using var server = new ChatServer(Port, Log);
        server.Start();
        Log($"채팅 서버가 UDP {Port} 에서 대기 중입니다. Ctrl+C 로 종료합니다.");

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };

        quit.Wait();
        Log($"종료합니다(접속자 {server.UserCount}명).");
        server.Stop();
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
