using System;
using System.Threading;

namespace KcpProject.Samples.Echo;

/// <summary>에코 샘플(임시). Phase 7 에서 KcpClient/KcpServer 기반으로 재작성한다.</summary>
internal static class Program
{
    /// <summary>개발용 UDP 포트(로드맵 12.4절 배정표: 27001 = 샘플 ① Echo).</summary>
    private const int Port = 27001;

    private static void Main()
    {
#pragma warning disable CS0618 // UDPSession 은 KcpClient 로 대체 예정
        var connection = new UDPSession
        {
            AckNoDelay = true,
            WriteDelay = false,
        };

        connection.Connect("127.0.0.1", Port);

        var stopSend = false;
        var buffer = new byte[1500];
        var counter = 0;
        var sendBytes = 0;
        var recvBytes = 0;

        while (true)
        {
            connection.Update();

            if (!stopSend)
            {
                var sent = connection.Send(buffer, 0, buffer.Length);
                if (sent < 0)
                {
                    Console.WriteLine("송신 실패.");
                    break;
                }

                if (sent > 0)
                {
                    counter++;
                    sendBytes += buffer.Length;
                    if (counter >= 500)
                        stopSend = true;
                }
            }

            var n = connection.Recv(buffer, 0, buffer.Length);
            if (n == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            if (n < 0)
            {
                Console.WriteLine("수신 실패.");
                break;
            }

            recvBytes += n;
            Console.WriteLine($"{recvBytes} / {sendBytes}");
        }
#pragma warning restore CS0618
    }
}
