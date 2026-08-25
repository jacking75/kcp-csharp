using System;
using System.Text;
using System.Threading;
using KcpProject.Transport;

namespace Echo.Server;

/// <summary>
/// 샘플 ① 에코 서버 — 받은 것을 그대로 돌려준다.
/// </summary>
/// <remarks>
/// 이 파일이 이 샘플의 전부다. <see cref="KcpServer"/> 를 만들고 이벤트 3개를 구독하면 끝난다.
/// KCP·재전송·핸드셰이크·타임아웃은 전부 라이브러리가 처리한다.
/// </remarks>
internal static class Program
{
    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ① Echo = 27001.</summary>
    private const int Port = 27001;

    private static void Main()
    {
        using var server = new KcpServer(Port);

        // ① 누가 들어왔는지
        server.SessionConnected += session =>
            Log($"접속 {session.RemoteEndPoint} (conv={session.Conv}, 현재 {server.SessionCount}명)");

        // ② 받은 것을 그대로 돌려준다
        server.ReliableReceived += (session, data) =>
        {
            var text = Encoding.UTF8.GetString(data.Span);
            Log($"수신 [{session.Conv}] {text}");

            var result = session.Send(data.Span);
            if (result != KcpSendResult.Ok)
                Log($"에코 실패 [{session.Conv}] {result}");
        };

        // ③ 왜 나갔는지 (정상 종료 / 타임아웃 / 데드링크)
        server.SessionDisconnected += (session, reason) =>
            Log($"종료 {session.RemoteEndPoint} ({reason})");

        server.Error += ex => Log($"오류 {ex.Message}");

        server.Start();
        Log($"UDP {Port} 에서 대기 중입니다. Ctrl+C 로 종료합니다.");

        // Ctrl+C 를 눌러도 서버를 깔끔히 닫는다.
        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };

        quit.Wait();
        Log("종료합니다.");
        server.Stop();
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
