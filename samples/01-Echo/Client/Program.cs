using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpCSharpR1.Transport;

namespace Echo.Client;

/// <summary>
/// 샘플 ① 에코 클라이언트 — 1초마다 카운터 메시지를 보내고 돌아온 값을 검증한다.
/// </summary>
/// <remarks>
/// 게임 루프와 같은 <b>폴링 모델</b>이다. 매 프레임 <see cref="KcpClient.Update"/> 를 부르는 것이
/// 이 라이브러리 사용법의 전부다. MonoGame 이라면 <c>Game.Update()</c> 안에서 부르면 된다.
/// </remarks>
internal static class Program
{
    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ① Echo = 27001.</summary>
    private const int Port = 27001;

    private static async Task<int> Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";

        using var client = new KcpClient();
        using var quit = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Cancel();
        };

        var sent = 0;
        var echoed = 0;
        var mismatched = 0;
        string? expected = null;

        client.ReliableReceived += (session, data) =>
        {
            var text = Encoding.UTF8.GetString(data.Span);
            if (text == expected)
            {
                echoed++;
                Log($"에코 확인 [{echoed}/{sent}] {text} (핑 {session.PingMs}ms, RTT {session.SrttMs}ms)");
            }
            else
            {
                mismatched++;
                LogError($"에코 불일치! 보낸 것: {expected} / 받은 것: {text}");
            }
        };

        client.Disconnected += (_, reason) => Log($"연결 종료 ({reason})");

        try
        {
            var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), Port), quit.Token);
            Log($"접속 완료 conv={session.Conv}, 로컬 {client.LocalEndPoint}");

            var nextSendAt = DateTime.UtcNow;

            while (!quit.IsCancellationRequested && session.State == KcpSessionState.Connected)
            {
                if (DateTime.UtcNow >= nextSendAt)
                {
                    nextSendAt = DateTime.UtcNow.AddSeconds(1);

                    expected = $"Hello KCP #{++sent}";
                    var result = session.Send(Encoding.UTF8.GetBytes(expected));
                    if (result != KcpSendResult.Ok)
                        LogError($"송신 실패: {result}");
                }

                // 게임 루프의 한 프레임에 해당한다.
                client.Update();
                await Task.Delay(10, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 는 정상 종료 경로다.
        }
        catch (TimeoutException ex)
        {
            LogError(ex.Message);
            LogError("서버가 떠 있는지 확인하세요: dotnet run --project samples/01-Echo/Server");
            return 1;
        }

        // 정상 종료: FIN 을 보내고 잠시 더 돌려 상대가 받도록 한다.
        Log("정상 종료 중...");
        client.Disconnect();
        for (var i = 0; i < 50; i++)
        {
            client.Update();
            await Task.Delay(10, CancellationToken.None);
        }

        Log($"결과 — 보냄 {sent}, 에코 {echoed}, 불일치 {mismatched}");
        return mismatched == 0 ? 0 : 1;
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    private static void LogError(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.ForegroundColor = previous;
    }
}
