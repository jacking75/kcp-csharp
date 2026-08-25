using System;
using System.Collections.Generic;
using Gomoku.Shared;
using KcpProject.Transport;

namespace Gomoku.Server;

/// <summary>한 플레이어의 서버 측 상태.</summary>
internal sealed class Player
{
    /// <summary>연결. 끊겨 있으면 null(재접속 대기).</summary>
    public KcpSession? Session;

    /// <summary>닉네임.</summary>
    public string Nick = string.Empty;

    /// <summary>이 플레이어의 돌 색.</summary>
    public StoneColor Color;

    /// <summary>재접속 토큰.</summary>
    public string Token = string.Empty;

    /// <summary>끊긴 시각(초). 연결돼 있으면 0.</summary>
    public long DisconnectedAtSeconds;

    /// <summary>연결되어 있는지 여부.</summary>
    public bool IsOnline => Session != null;
}

/// <summary>
/// 오목 방 하나.
/// </summary>
/// <remarks>
/// <para>
/// <b>권위 서버</b>의 최소 형태다. 판의 상태는 오직 이 객체만 소유하고, 클라이언트는
/// "여기에 두고 싶다"고 <b>요청</b>할 뿐이다. 서버가 검증하고, 통과한 것만 모두에게 알린다.
/// </para>
/// <para>
/// 클라이언트가 보내는 좌표를 그대로 믿지 않는다. 차례인지, 판 안인지, 빈 칸인지를 전부 다시 본다.
/// 클라이언트를 조작해도 규칙을 어길 수 없다는 뜻이다.
/// </para>
/// </remarks>
internal sealed class GameRoom
{
    private readonly GomokuBoard board = new();
    private readonly byte[] sendBuffer = new byte[ProtocolLimits.MaxMessageBytes];
    private readonly List<KcpSession> spectators = new();
    private readonly byte[] boardScratch = new byte[GomokuBoard.CellCount];
    private readonly Action<string> log;

    /// <summary>방 번호.</summary>
    public uint Id { get; }

    /// <summary>흑 플레이어.</summary>
    public Player Black { get; }

    /// <summary>백 플레이어.</summary>
    public Player White { get; }

    /// <summary>판이 끝났는지 여부.</summary>
    public bool IsFinished => board.IsFinished;

    /// <summary>방을 만든다.</summary>
    /// <param name="id">방 번호.</param>
    /// <param name="black">흑 플레이어.</param>
    /// <param name="white">백 플레이어.</param>
    /// <param name="log">로그 출력.</param>
    public GameRoom(uint id, Player black, Player white, Action<string> log)
    {
        Id = id;
        Black = black;
        White = white;
        this.log = log;

        black.Color = StoneColor.Black;
        white.Color = StoneColor.White;
    }

    /// <summary>세션이 이 방의 플레이어라면 찾아 준다.</summary>
    /// <param name="session">세션.</param>
    /// <returns>플레이어. 아니면 null.</returns>
    public Player? Find(KcpSession session)
    {
        if (ReferenceEquals(Black.Session, session)) return Black;
        if (ReferenceEquals(White.Session, session)) return White;
        return null;
    }

    /// <summary>토큰으로 플레이어를 찾는다.</summary>
    /// <param name="token">재접속 토큰.</param>
    /// <returns>플레이어. 없으면 null.</returns>
    public Player? FindByToken(string token)
    {
        if (Black.Token == token) return Black;
        if (White.Token == token) return White;
        return null;
    }

    /// <summary>상대 플레이어를 돌려준다.</summary>
    /// <param name="player">기준 플레이어.</param>
    /// <returns>상대.</returns>
    public Player Opponent(Player player) => ReferenceEquals(player, Black) ? White : Black;

    /// <summary>양쪽에 매칭 성사를 알린다.</summary>
    public void NotifyMatchFound()
    {
        SendMatchFound(Black, White.Nick);
        SendMatchFound(White, Black.Nick);
        BroadcastTurn();

        log($"[방 {Id}] 시작 — 흑 {Black.Nick} vs 백 {White.Nick}");
    }

    private void SendMatchFound(Player player, string opponentNick)
    {
        if (player.Session == null) return;

        var writer = new MessageWriter(sendBuffer, MessageType.MatchFound);
        writer.WriteUInt32(Id);
        writer.WriteByte((byte)player.Color);
        writer.WriteString(opponentNick);
        writer.WriteString(player.Token);
        player.Session.Send(writer.Finish());
    }

    /// <summary>착수 요청을 처리한다.</summary>
    /// <param name="player">요청한 플레이어.</param>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <returns>거부 사유. 성공이면 null.</returns>
    public GomokuError? TryPlace(Player player, int x, int y)
    {
        // 서버는 클라이언트를 믿지 않는다 — 규칙을 여기서 다시 본다.
        var result = board.Place(x, y, player.Color);

        switch (result)
        {
            case PlaceResult.NotYourTurn: return GomokuError.NotYourTurn;
            case PlaceResult.GameOver: return GomokuError.NotInRoom;
            case PlaceResult.OutOfBounds:
            case PlaceResult.Occupied: return GomokuError.InvalidMove;
        }

        BroadcastStonePlaced(x, y, player.Color);

        if (board.IsFinished)
        {
            if (board.Winner == StoneColor.None)
            {
                SendGameOver(Black, GameResult.Draw, StoneColor.None);
                SendGameOver(White, GameResult.Draw, StoneColor.None);
                log($"[방 {Id}] 무승부");
            }
            else
            {
                var winner = board.Winner == StoneColor.Black ? Black : White;
                var loser = Opponent(winner);
                SendGameOver(winner, GameResult.Win, board.Winner);
                SendGameOver(loser, GameResult.Lose, board.Winner);
                log($"[방 {Id}] {winner.Nick}({board.Winner}) 승리");
            }
        }
        else
        {
            BroadcastTurn();
        }

        return null;
    }

    /// <summary>항복을 처리한다.</summary>
    /// <param name="player">항복한 플레이어.</param>
    public void Surrender(Player player)
    {
        if (board.IsFinished) return;

        var opponent = Opponent(player);
        SendGameOver(player, GameResult.Lose, opponent.Color);
        SendGameOver(opponent, GameResult.OpponentSurrendered, opponent.Color);
        log($"[방 {Id}] {player.Nick} 항복");

        ForceFinish();
    }

    /// <summary>재접속 시간이 지나 상대가 돌아오지 않았을 때.</summary>
    /// <param name="leftPlayer">돌아오지 않은 플레이어.</param>
    public void OpponentGaveUp(Player leftPlayer)
    {
        if (board.IsFinished) return;

        var winner = Opponent(leftPlayer);
        SendGameOver(winner, GameResult.OpponentLeft, winner.Color);
        log($"[방 {Id}] {leftPlayer.Nick} 미복귀 — {winner.Nick} 승");

        ForceFinish();
    }

    /// <summary>플레이어가 끊겼음을 방에 알린다.</summary>
    /// <param name="player">끊긴 플레이어.</param>
    /// <param name="nowSeconds">현재 시각(초).</param>
    public void OnPlayerDisconnected(Player player, long nowSeconds)
    {
        player.Session = null;
        player.DisconnectedAtSeconds = nowSeconds;

        var opponent = Opponent(player);
        if (opponent.Session == null) return;

        var writer = new MessageWriter(sendBuffer, MessageType.OpponentDisconnected);
        writer.WriteByte(ProtocolLimits.ReconnectGraceSeconds);
        opponent.Session.Send(writer.Finish());

        log($"[방 {Id}] {player.Nick} 끊김 — {ProtocolLimits.ReconnectGraceSeconds}초 대기");
    }

    /// <summary>재접속한 플레이어를 방에 다시 붙인다.</summary>
    /// <param name="player">플레이어.</param>
    /// <param name="session">새 세션.</param>
    public void OnPlayerReconnected(Player player, KcpSession session)
    {
        player.Session = session;
        player.DisconnectedAtSeconds = 0;

        SendStateSync(player);

        var opponent = Opponent(player);
        if (opponent.Session != null)
        {
            var writer = new MessageWriter(sendBuffer, MessageType.OpponentReconnected);
            opponent.Session.Send(writer.Finish());
        }

        log($"[방 {Id}] {player.Nick} 복귀");
    }

    /// <summary>관전자를 받는다.</summary>
    /// <param name="session">관전자 세션.</param>
    public void AddSpectator(KcpSession session)
    {
        spectators.Add(session);

        var writer = new MessageWriter(sendBuffer, MessageType.StateSync);
        WriteBoardState(ref writer, StoneColor.None);
        session.Send(writer.Finish());

        log($"[방 {Id}] 관전자 입장 (총 {spectators.Count}명)");
    }

    /// <summary>관전자를 뺀다.</summary>
    /// <param name="session">관전자 세션.</param>
    public void RemoveSpectator(KcpSession session) => spectators.Remove(session);

    /// <summary>현재 판 상태를 한 사람에게 통째로 보낸다(재접속·관전 시작 시).</summary>
    /// <param name="player">대상.</param>
    public void SendStateSync(Player player)
    {
        if (player.Session == null) return;

        var writer = new MessageWriter(sendBuffer, MessageType.StateSync);
        WriteBoardState(ref writer, player.Color);
        player.Session.Send(writer.Finish());
    }

    /// <summary>채팅을 방 전체에 전달한다.</summary>
    /// <param name="from">보낸 사람.</param>
    /// <param name="text">내용.</param>
    public void BroadcastChat(string from, string text)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.ChatFrom);
        writer.WriteString(from);
        writer.WriteString(text);
        var packet = writer.Finish();

        Black.Session?.Send(packet);
        White.Session?.Send(packet);
        foreach (var spectator in spectators) spectator.Send(packet);
    }

    private void WriteBoardState(ref MessageWriter writer, StoneColor myColor)
    {
        board.WriteTo(boardScratch);

        writer.WriteBytes(boardScratch);
        writer.WriteByte((byte)board.Turn);
        writer.WriteByte((byte)myColor);
        writer.WriteByte(board.IsFinished ? (byte)1 : (byte)0);
        writer.WriteByte((byte)board.Winner);
    }

    private void BroadcastStonePlaced(int x, int y, StoneColor color)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.StonePlaced);
        writer.WriteByte((byte)x);
        writer.WriteByte((byte)y);
        writer.WriteByte((byte)color);
        var packet = writer.Finish();

        Black.Session?.Send(packet);
        White.Session?.Send(packet);
        foreach (var spectator in spectators) spectator.Send(packet);
    }

    private void BroadcastTurn()
    {
        var writer = new MessageWriter(sendBuffer, MessageType.TurnChanged);
        writer.WriteByte((byte)board.Turn);
        var packet = writer.Finish();

        Black.Session?.Send(packet);
        White.Session?.Send(packet);
        foreach (var spectator in spectators) spectator.Send(packet);
    }

    private void SendGameOver(Player player, GameResult result, StoneColor winner)
    {
        if (player.Session == null) return;

        var writer = new MessageWriter(sendBuffer, MessageType.GameOver);
        writer.WriteByte((byte)result);
        writer.WriteByte((byte)winner);
        player.Session.Send(writer.Finish());
    }

    private void ForceFinish()
    {
        // 보드를 종료 상태로 만든다(같은 방에 더 둘 수 없게).
        board.WriteTo(boardScratch);
        board.ReadFrom(boardScratch, board.Turn, board.Winner, finished: true);
    }
}
