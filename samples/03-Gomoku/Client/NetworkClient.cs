using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Gomoku.Shared;
using KcpCSharpR1.Transport;

namespace Gomoku.Client;

/// <summary>클라이언트가 보는 화면 상태.</summary>
internal enum ClientPhase
{
    /// <summary>서버에 붙는 중.</summary>
    Connecting,

    /// <summary>대기열에서 상대를 기다리는 중.</summary>
    Queued,

    /// <summary>대국 중.</summary>
    Playing,

    /// <summary>대국이 끝났다.</summary>
    Finished,

    /// <summary>연결이 끊겼다.</summary>
    Disconnected,
}

/// <summary>
/// 게임 루프에서 폴링하는 네트워크 계층.
/// </summary>
/// <remarks>
/// <para>
/// <b>MonoGame 과 엮는 방법의 핵심</b>: <see cref="Poll"/> 을 <c>Game.Update()</c> 에서 매 프레임 부른다.
/// 그러면 수신 콜백도 게임 스레드에서 발화하므로, 렌더링 상태를 lock 없이 그대로 고쳐도 안전하다.
/// (서버는 반대로 전용 수신 스레드를 쓴다 — 책 6장에서 두 모델을 비교한다.)
/// </para>
/// </remarks>
internal sealed class NetworkClient : IDisposable
{
    private readonly KcpClient client = new();
    private readonly byte[] sendBuffer = new byte[ProtocolLimits.MaxMessageBytes];
    private readonly string nick;

    private KcpSession? session;

    /// <summary>서버가 보낸 판 상태(클라이언트 사본).</summary>
    public GomokuBoard Board { get; } = new();

    /// <summary>현재 화면 단계.</summary>
    public ClientPhase Phase { get; private set; } = ClientPhase.Connecting;

    /// <summary>내 돌 색.</summary>
    public StoneColor MyColor { get; private set; } = StoneColor.None;

    /// <summary>상대 닉네임.</summary>
    public string OpponentNick { get; private set; } = "?";

    /// <summary>재접속 토큰. 창을 닫았다 다시 켜면 이 값으로 돌아온다.</summary>
    public string ReconnectToken { get; private set; } = string.Empty;

    /// <summary>화면 하단에 찍을 안내 문구.</summary>
    public string StatusText { get; private set; } = "CONNECTING...";

    /// <summary>최근 채팅 몇 줄.</summary>
    public List<string> ChatLines { get; } = new();

    /// <summary>마지막으로 놓인 돌의 좌표(하이라이트용). 없으면 -1.</summary>
    public (int X, int Y) LastMove { get; private set; } = (-1, -1);

    /// <summary>상대가 끊겨 재접속을 기다리는 중인지.</summary>
    public bool OpponentAway { get; private set; }

    /// <summary>RTT(ms).</summary>
    public int SrttMs => session?.SrttMs ?? 0;

    /// <summary>핑(ms).</summary>
    public int PingMs => session?.PingMs ?? -1;

    /// <summary>연결되어 있는지.</summary>
    public bool IsConnected => session?.State == KcpSessionState.Connected;

    /// <summary>네트워크 계층을 만든다.</summary>
    /// <param name="nick">내 닉네임.</param>
    public NetworkClient(string nick)
    {
        this.nick = nick;
        client.ReliableReceived += (_, data) => Handle(data.Span);
        client.Disconnected += (_, reason) =>
        {
            Phase = ClientPhase.Disconnected;
            StatusText = $"DISCONNECTED ({reason})";
        };
    }

    /// <summary>서버에 접속하고 대기열에 들어간다.</summary>
    /// <param name="host">서버 주소.</param>
    /// <param name="token">재접속 토큰(있으면 대기열 대신 복귀를 시도한다).</param>
    /// <returns>비동기 작업.</returns>
    public async Task ConnectAsync(string host, string? token)
    {
        try
        {
            session = await client.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), ProtocolLimits.Port));
        }
        catch (Exception ex)
        {
            Phase = ClientPhase.Disconnected;
            StatusText = $"CONNECT FAILED: {ex.Message}";
            return;
        }

        if (!string.IsNullOrEmpty(token))
        {
            var writer = new MessageWriter(sendBuffer, MessageType.Reconnect);
            writer.WriteString(token);
            session.Send(writer.Finish());
            StatusText = "RECONNECTING...";
        }
        else
        {
            var writer = new MessageWriter(sendBuffer, MessageType.JoinQueue);
            writer.WriteString(nick);
            session.Send(writer.Finish());
            Phase = ClientPhase.Queued;
            StatusText = "WAITING FOR OPPONENT...";
        }
    }

    /// <summary>매 프레임 호출한다.</summary>
    public void Poll() => client.Update();

    /// <summary>돌을 놓아 달라고 서버에 요청한다.</summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    public void RequestPlace(int x, int y)
    {
        if (session == null || Phase != ClientPhase.Playing) return;
        if (!GomokuBoard.InBounds(x, y)) return;

        // 클라이언트도 먼저 걸러 보지만, 최종 판단은 서버가 한다.
        if (Board.Turn != MyColor || Board[x, y] != StoneColor.None) return;

        var writer = new MessageWriter(sendBuffer, MessageType.PlaceStone);
        writer.WriteByte((byte)x);
        writer.WriteByte((byte)y);
        session.Send(writer.Finish());
    }

    /// <summary>항복한다.</summary>
    public void Surrender()
    {
        if (session == null || Phase != ClientPhase.Playing) return;

        var writer = new MessageWriter(sendBuffer, MessageType.Surrender);
        session.Send(writer.Finish());
    }

    private void Handle(ReadOnlySpan<byte> packet)
    {
        if (!MessageReader.TryParse(packet, out var reader))
            return;

        switch (reader.Type)
        {
            case MessageType.MatchFound:
            {
                if (!reader.TryReadUInt32(out _) ||
                    !reader.TryReadByte(out var color) ||
                    !reader.TryReadString(out var oppNick) ||
                    !reader.TryReadString(out var token)) return;

                MyColor = (StoneColor)color;
                OpponentNick = oppNick;
                ReconnectToken = token;
                Board.Reset();
                Phase = ClientPhase.Playing;
                AddChat($"MATCH: VS {oppNick}");
                UpdateTurnText();
                break;
            }

            case MessageType.StonePlaced:
            {
                if (!reader.TryReadByte(out var x) ||
                    !reader.TryReadByte(out var y) ||
                    !reader.TryReadByte(out var color)) return;

                Board.Place(x, y, (StoneColor)color);
                LastMove = (x, y);
                UpdateTurnText();
                break;
            }

            case MessageType.TurnChanged:
                if (reader.TryReadByte(out _)) UpdateTurnText();
                break;

            case MessageType.GameOver:
            {
                if (!reader.TryReadByte(out var result) || !reader.TryReadByte(out _)) return;

                Phase = ClientPhase.Finished;
                StatusText = (GameResult)result switch
                {
                    GameResult.Win => "YOU WIN!",
                    GameResult.Lose => "YOU LOSE",
                    GameResult.Draw => "DRAW",
                    GameResult.OpponentSurrendered => "OPPONENT SURRENDERED - YOU WIN!",
                    GameResult.OpponentLeft => "OPPONENT LEFT - YOU WIN!",
                    _ => "GAME OVER",
                };
                AddChat(StatusText);
                break;
            }

            case MessageType.StateSync:
            {
                if (!reader.TryReadBytes(GomokuBoard.CellCount, out var cells) ||
                    !reader.TryReadByte(out var turn) ||
                    !reader.TryReadByte(out var myColor) ||
                    !reader.TryReadByte(out var finished) ||
                    !reader.TryReadByte(out var winner)) return;

                MyColor = (StoneColor)myColor;
                Board.ReadFrom(cells, (StoneColor)turn, (StoneColor)winner, finished != 0);
                Phase = finished != 0 ? ClientPhase.Finished : ClientPhase.Playing;
                OpponentAway = false;
                AddChat("STATE SYNCED");
                UpdateTurnText();
                break;
            }

            case MessageType.ChatFrom:
                if (reader.TryReadString(out var from) && reader.TryReadString(out var text))
                    AddChat($"{from}: {text}");
                break;

            case MessageType.QueueStatus:
                if (reader.TryReadUInt16(out var waiting))
                    StatusText = $"IN QUEUE ({waiting} WAITING)";
                break;

            case MessageType.OpponentDisconnected:
                if (reader.TryReadByte(out var seconds))
                {
                    OpponentAway = true;
                    StatusText = $"OPPONENT LOST CONNECTION - WAITING {seconds}S";
                }
                break;

            case MessageType.OpponentReconnected:
                OpponentAway = false;
                AddChat("OPPONENT IS BACK");
                UpdateTurnText();
                break;

            case MessageType.Error:
                if (reader.TryReadByte(out var code) && reader.TryReadString(out var message))
                {
                    AddChat($"ERR[{(GomokuError)code}] {message}");
                    if ((GomokuError)code == GomokuError.BadToken)
                    {
                        // 복귀할 방이 없으면 새 판을 신청한다.
                        ReconnectToken = string.Empty;
                        var writer = new MessageWriter(sendBuffer, MessageType.JoinQueue);
                        writer.WriteString(nick);
                        session?.Send(writer.Finish());
                        Phase = ClientPhase.Queued;
                        StatusText = "WAITING FOR OPPONENT...";
                    }
                }
                break;
        }
    }

    private void UpdateTurnText()
    {
        if (Phase != ClientPhase.Playing) return;
        if (OpponentAway) return;

        StatusText = Board.Turn == MyColor ? "YOUR TURN" : "OPPONENT'S TURN";
    }

    private void AddChat(string line)
    {
        ChatLines.Add(line);
        if (ChatLines.Count > 8) ChatLines.RemoveAt(0);
    }

    /// <summary>연결을 정리한다.</summary>
    public void Dispose()
    {
        client.Disconnect();
        client.Update();
        client.Dispose();
    }
}
