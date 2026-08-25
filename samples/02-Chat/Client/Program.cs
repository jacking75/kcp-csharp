using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Chat.Shared;
using KcpCSharpR1.Transport;

namespace Chat.Client;

/// <summary>
/// 샘플 ② 채팅 클라이언트(콘솔).
/// </summary>
/// <remarks>
/// <para>
/// <b>콘솔 입력과 네트워크를 섞는 법</b>: <c>Console.ReadLine</c> 은 블로킹이라 폴링 루프를 멈춰 버린다.
/// 그래서 입력 전용 스레드를 하나 두고 <see cref="ConcurrentQueue{T}"/> 로 넘긴 뒤,
/// 메인 루프가 큐를 꺼내 처리한다. 게임에서 UI 스레드와 네트워크 스레드를 잇는 방식과 같다.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ② Chat = 27002.</summary>
    private const int Port = 27002;

    private static readonly ConcurrentQueue<string> InputQueue = new();
    private static readonly byte[] SendBuffer = new byte[ChatLimits.MaxMessageBytes];

    private static async Task<int> Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var nick = args.Length > 1 ? args[1] : $"user{Environment.ProcessId % 1000}";

        using var client = new KcpClient();
        using var quit = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Cancel();
        };

        client.ReliableReceived += (_, data) => HandleMessage(data.Span);
        client.Disconnected += (_, reason) =>
        {
            Print(ConsoleColor.DarkGray, $"* 연결 종료 ({reason})");
            quit.Cancel();
        };

        KcpSession session;
        try
        {
            session = await client.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), Port), quit.Token);
        }
        catch (TimeoutException ex)
        {
            Print(ConsoleColor.Red, ex.Message);
            Print(ConsoleColor.Red, "서버를 먼저 띄우세요: dotnet run --project samples/02-Chat/Server");
            return 1;
        }

        Print(ConsoleColor.DarkGray, $"* 접속 완료 (conv={session.Conv}). 닉네임 '{nick}' 으로 입장합니다.");
        SendSetNick(session, nick);

        StartInputThread(quit.Token);

        while (!quit.IsCancellationRequested && session.State == KcpSessionState.Connected)
        {
            while (InputQueue.TryDequeue(out var line))
            {
                if (!HandleCommand(session, line))
                {
                    quit.Cancel();
                    break;
                }
            }

            client.Update();
            await Task.Delay(10, CancellationToken.None);
        }

        client.Disconnect();
        for (var i = 0; i < 50; i++)
        {
            client.Update();
            await Task.Delay(10, CancellationToken.None);
        }

        return 0;
    }

    /// <summary>입력 한 줄을 처리한다. false 를 돌려주면 종료한다.</summary>
    private static bool HandleCommand(KcpSession session, string line)
    {
        if (line.Length == 0) return true;

        if (!line.StartsWith('/'))
        {
            SendSay(session, line);
            return true;
        }

        var space = line.IndexOf(' ');
        var command = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

        switch (command)
        {
            case "/help":
                Print(ConsoleColor.DarkGray, "* /nick <이름>  /w <이름> <내용>  /who  /quit");
                return true;

            case "/nick":
                if (rest.Length == 0) { Print(ConsoleColor.Red, "* 사용법: /nick <이름>"); return true; }
                SendSetNick(session, rest);
                return true;

            case "/w":
                var target = rest.IndexOf(' ');
                if (target <= 0)
                {
                    Print(ConsoleColor.Red, "* 사용법: /w <이름> <내용>");
                    return true;
                }
                SendWhisper(session, rest.Substring(0, target), rest.Substring(target + 1));
                return true;

            case "/who":
                Print(ConsoleColor.DarkGray, $"* RTT {session.SrttMs}ms, 핑 {session.PingMs}ms, 재전송 {session.Stats.RetransmitSegments}");
                return true;

            case "/quit":
                return false;

            default:
                Print(ConsoleColor.Red, $"* 알 수 없는 명령: {command}");
                return true;
        }
    }

    private static void SendSetNick(KcpSession session, string nick)
    {
        var writer = new MessageWriter(SendBuffer, MessageType.SetNick);
        writer.WriteString(nick);
        session.Send(writer.Finish());
    }

    private static void SendSay(KcpSession session, string text)
    {
        var writer = new MessageWriter(SendBuffer, MessageType.Say);
        writer.WriteString(text);
        session.Send(writer.Finish());
    }

    private static void SendWhisper(KcpSession session, string target, string text)
    {
        var writer = new MessageWriter(SendBuffer, MessageType.Whisper);
        writer.WriteString(target);
        writer.WriteString(text);
        session.Send(writer.Finish());
    }

    private static void HandleMessage(ReadOnlySpan<byte> packet)
    {
        if (!MessageReader.TryParse(packet, out var reader))
        {
            Print(ConsoleColor.Red, "* 깨진 패킷을 받았습니다.");
            return;
        }

        switch (reader.Type)
        {
            case MessageType.Welcome:
            {
                if (!reader.TryReadString(out var motd) || !reader.TryReadUInt16(out var count)) return;
                Print(ConsoleColor.Cyan, $"* {motd}");

                var names = new string[count];
                for (var i = 0; i < count; i++)
                {
                    if (!reader.TryReadString(out names[i])) return;
                }
                Print(ConsoleColor.DarkGray, count == 0
                    ? "* 지금은 아무도 없습니다."
                    : $"* 접속자 {count}명: {string.Join(", ", names)}");
                break;
            }

            case MessageType.UserJoined:
                if (reader.TryReadString(out var joined))
                    Print(ConsoleColor.DarkGreen, $"* {joined} 님이 들어왔습니다.");
                break;

            case MessageType.UserLeft:
                if (reader.TryReadString(out var left))
                    Print(ConsoleColor.DarkYellow, $"* {left} 님이 나갔습니다.");
                break;

            case MessageType.Chat:
                if (reader.TryReadString(out var nick) && reader.TryReadString(out var text))
                    Print(ConsoleColor.Gray, $"{nick}: {text}");
                break;

            case MessageType.WhisperFrom:
                if (reader.TryReadString(out var from) && reader.TryReadString(out var whisper))
                    Print(ConsoleColor.Magenta, $"({from} 님의 귓속말) {whisper}");
                break;

            case MessageType.Error:
                if (reader.TryReadByte(out var code) && reader.TryReadString(out var message))
                    Print(ConsoleColor.Red, $"* 오류[{(ChatError)code}] {message}");
                break;

            default:
                Print(ConsoleColor.Red, $"* 알 수 없는 메시지 0x{(byte)reader.Type:X2}");
                break;
        }
    }

    private static void StartInputThread(CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line == null) break;
                InputQueue.Enqueue(line.Trim());
            }
        })
        {
            IsBackground = true,
            Name = "console-input",
        };
        thread.Start();
    }

    private static void Print(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
