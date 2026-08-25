using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpProject;
using KcpProject.Transport;

namespace KcpProject.Samples.Echo;

/// <summary>
/// 샘플 ① 에코 — 공개 API(KcpServer / KcpClient)만으로 만든 최소 왕복 예제.
/// </summary>
/// <remarks>
/// <para>실행:</para>
/// <code>
/// dotnet run --project samples/01-Echo            # 서버 + 클라이언트를 한 프로세스에서
/// dotnet run --project samples/01-Echo -- server  # 서버만
/// dotnet run --project samples/01-Echo -- client  # 클라이언트만
/// </code>
/// <para>UDP 포트 27001 을 쓴다(로드맵 12.4절 배정표).</para>
/// </remarks>
internal static class Program
{
    /// <summary>개발용 UDP 포트(로드맵 12.4절 배정표: 27001 = 샘플 ① Echo).</summary>
    private const int Port = 27001;

    private static async Task Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "both";

        switch (mode)
        {
            case "server":
                RunServer();
                Console.WriteLine("아무 키나 누르면 종료합니다.");
                Console.ReadKey();
                break;

            case "client":
                await RunClientAsync();
                break;

            default:
                using (RunServer())
                {
                    await Task.Delay(200);
                    await RunClientAsync();
                }
                break;
        }
    }

    /// <summary>받은 그대로 돌려주는 서버.</summary>
    private static KcpServer RunServer()
    {
        var server = new KcpServer(Port);

        server.SessionConnected += session =>
            Console.WriteLine($"[서버] 접속: {session.RemoteEndPoint} (conv={session.Conv})");

        server.SessionDisconnected += (session, reason) =>
            Console.WriteLine($"[서버] 종료: {session.RemoteEndPoint} ({reason})");

        server.ReliableReceived += (session, data) =>
        {
            var text = Encoding.UTF8.GetString(data.Span);
            Console.WriteLine($"[서버] 수신: {text}");
            session.Send(data.Span);   // 에코
        };

        server.Error += ex => Console.WriteLine($"[서버] 오류: {ex.Message}");

        server.Start();
        Console.WriteLine($"[서버] UDP {Port} 에서 대기 중");
        return server;
    }

    /// <summary>메시지를 보내고 에코를 받아 출력하는 클라이언트(폴링 모델).</summary>
    private static async Task RunClientAsync()
    {
        using var client = new KcpClient();

        client.Disconnected += (_, reason) => Console.WriteLine($"[클라] 연결 종료: {reason}");
        client.ReliableReceived += (session, data) =>
            Console.WriteLine($"[클라] 에코: {Encoding.UTF8.GetString(data.Span)} (핑 {session.PingMs}ms)");

        var session = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port));
        Console.WriteLine($"[클라] 접속 완료 (conv={session.Conv}, 로컬 {client.LocalEndPoint})");

        for (var i = 1; i <= 5; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Hello KCP #{i}");
            var result = session.Send(message);
            if (result != KcpSendResult.Ok)
            {
                Console.WriteLine($"[클라] 송신 실패: {result}");
                break;
            }

            // 게임 루프처럼 매 프레임 Update 를 돌린다(MonoGame 은 Game.Update() 에서 호출).
            for (var tick = 0; tick < 40; tick++)
            {
                client.Update();
                await Task.Delay(5);
            }
        }

        var stats = session.Stats;
        Console.WriteLine($"[클라] 통계 — 보낸 세그먼트 {stats.SegmentsSent}, 재전송 {stats.RetransmitSegments}, RTT {stats.Srtt}ms");

        client.Disconnect();
        for (var tick = 0; tick < 40; tick++)
        {
            client.Update();
            await Task.Delay(5);
        }
    }
}
