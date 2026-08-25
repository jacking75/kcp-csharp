using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Gomoku.Shared;
using KcpCSharpR1.Transport;

namespace Gomoku.Server;

/// <summary>
/// 오목 서버: 매칭 대기열 + 방 관리 + 재접속.
/// </summary>
/// <remarks>
/// 모든 콜백은 <see cref="KcpServer"/> 의 수신 루프 스레드 하나에서만 발화하므로 lock 이 없다.
/// 재접속 만료 검사는 주기 타이머 대신 <see cref="Tick"/> 에서 처리한다.
/// </remarks>
internal sealed class GameServer : IDisposable
{
    private readonly KcpServer server;
    private readonly Action<string> log;
    private readonly byte[] sendBuffer = new byte[ProtocolLimits.MaxMessageBytes];

    private readonly List<Player> queue = new();
    private readonly List<GameRoom> rooms = new();
    private readonly Dictionary<KcpSession, Player> sessionToPlayer = new();
    private readonly Dictionary<KcpSession, GameRoom> sessionToRoom = new();
    private readonly Dictionary<KcpSession, GameRoom> spectatorRooms = new();

    private uint nextRoomId = 1;

    /// <summary>서버를 만든다.</summary>
    /// <param name="port">UDP 포트.</param>
    /// <param name="log">로그 출력.</param>
    public GameServer(int port, Action<string> log)
    {
        this.log = log;

        var options = new KcpServerOptions();
        options.Session.SessionTimeoutMs = 10_000;

        server = new KcpServer(port, options);
        server.SessionDisconnected += OnDisconnected;
        server.ReliableReceived += OnReceived;
        server.Error += ex => log($"오류: {ex.Message}");
    }

    /// <summary>진행 중인 방 수.</summary>
    public int RoomCount => rooms.Count;

    /// <summary>대기열 인원.</summary>
    public int QueueCount => queue.Count;

    /// <summary>수신 루프를 시작한다.</summary>
    public void Start() => server.Start();

    /// <summary>수신 루프를 멈춘다.</summary>
    public void Stop() => server.Stop();

    /// <summary>재접속 만료를 검사한다. 1초에 한 번 호출한다.</summary>
    /// <param name="nowSeconds">현재 시각(초).</param>
    public void Tick(long nowSeconds)
    {
        for (var i = rooms.Count - 1; i >= 0; i--)
        {
            var room = rooms[i];

            CheckGrace(room, room.Black, nowSeconds);
            CheckGrace(room, room.White, nowSeconds);

            if (room.IsFinished && !room.Black.IsOnline && !room.White.IsOnline)
                rooms.RemoveAt(i);
        }
    }

    private static void CheckGrace(GameRoom room, Player player, long nowSeconds)
    {
        if (player.IsOnline || player.DisconnectedAtSeconds == 0) return;
        if (nowSeconds - player.DisconnectedAtSeconds < ProtocolLimits.ReconnectGraceSeconds) return;

        player.DisconnectedAtSeconds = 0;
        room.OpponentGaveUp(player);
    }

    private void OnDisconnected(KcpSession session, KcpDisconnectReason reason)
    {
        if (spectatorRooms.TryGetValue(session, out var spectated))
        {
            spectated.RemoveSpectator(session);
            spectatorRooms.Remove(session);
        }

        if (!sessionToPlayer.TryGetValue(session, out var player))
            return;

        sessionToPlayer.Remove(session);
        queue.Remove(player);

        if (sessionToRoom.TryGetValue(session, out var room))
        {
            sessionToRoom.Remove(session);
            // 세션은 끊겼지만 플레이어는 남는다 — 30초 안에 토큰으로 돌아올 수 있다.
            room.OnPlayerDisconnected(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        else
        {
            log($"퇴장 {player.Nick} ({reason})");
        }
    }

    private void OnReceived(KcpSession session, ReadOnlyMemory<byte> data)
    {
        if (!MessageReader.TryParse(data.Span, out var reader))
        {
            SendError(session, GomokuError.MalformedPacket, "패킷을 해석할 수 없습니다.");
            return;
        }

        switch (reader.Type)
        {
            case MessageType.JoinQueue: HandleJoinQueue(session, ref reader); break;
            case MessageType.PlaceStone: HandlePlaceStone(session, ref reader); break;
            case MessageType.Surrender: HandleSurrender(session); break;
            case MessageType.Reconnect: HandleReconnect(session, ref reader); break;
            case MessageType.Chat: HandleChat(session, ref reader); break;
            case MessageType.Spectate: HandleSpectate(session, ref reader); break;

            default:
                SendError(session, GomokuError.MalformedPacket, $"알 수 없는 타입 0x{(byte)reader.Type:X2}");
                break;
        }
    }

    private void HandleJoinQueue(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var nick))
        {
            SendError(session, GomokuError.MalformedPacket, "닉네임을 읽을 수 없습니다.");
            return;
        }

        nick = nick.Trim();
        if (nick.Length == 0 || nick.Length > ProtocolLimits.MaxNickLength || nick.Any(char.IsControl))
        {
            SendError(session, GomokuError.NickInvalid, $"닉네임은 1~{ProtocolLimits.MaxNickLength}자여야 합니다.");
            return;
        }

        if (sessionToPlayer.ContainsKey(session)) return;   // 이미 참가 중

        var player = new Player
        {
            Session = session,
            Nick = nick,
            Token = NewToken(),
        };

        sessionToPlayer[session] = player;
        queue.Add(player);
        log($"대기열 {nick} (대기 {queue.Count}명)");

        if (queue.Count < 2)
        {
            var writer = new MessageWriter(sendBuffer, MessageType.QueueStatus);
            writer.WriteUInt16((ushort)queue.Count);
            session.Send(writer.Finish());
            return;
        }

        // 2명이 모이면 방을 만든다.
        var black = queue[0];
        var white = queue[1];
        queue.RemoveRange(0, 2);

        var room = new GameRoom(nextRoomId++, black, white, log);
        rooms.Add(room);

        if (black.Session != null) sessionToRoom[black.Session] = room;
        if (white.Session != null) sessionToRoom[white.Session] = room;

        room.NotifyMatchFound();
    }

    private void HandlePlaceStone(KcpSession session, ref MessageReader reader)
    {
        if (!TryGetRoomAndPlayer(session, out var room, out var player)) return;

        if (!reader.TryReadByte(out var x) || !reader.TryReadByte(out var y))
        {
            SendError(session, GomokuError.MalformedPacket, "좌표를 읽을 수 없습니다.");
            return;
        }

        var error = room.TryPlace(player, x, y);
        if (error != null)
        {
            SendError(session, error.Value, error.Value switch
            {
                GomokuError.NotYourTurn => "지금은 상대 차례입니다.",
                GomokuError.InvalidMove => "그 자리에는 둘 수 없습니다.",
                _ => "둘 수 없습니다.",
            });
        }
    }

    private void HandleSurrender(KcpSession session)
    {
        if (!TryGetRoomAndPlayer(session, out var room, out var player)) return;
        room.Surrender(player);
    }

    private void HandleReconnect(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var token))
        {
            SendError(session, GomokuError.MalformedPacket, "토큰을 읽을 수 없습니다.");
            return;
        }

        foreach (var room in rooms)
        {
            var player = room.FindByToken(token);
            if (player == null || player.IsOnline) continue;

            sessionToPlayer[session] = player;
            sessionToRoom[session] = room;
            room.OnPlayerReconnected(player, session);
            return;
        }

        SendError(session, GomokuError.BadToken, "재접속할 수 있는 방을 찾지 못했습니다.");
    }

    private void HandleChat(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var text)) return;
        if (text.Length == 0 || text.Length > ProtocolLimits.MaxChatLength) return;

        if (sessionToRoom.TryGetValue(session, out var room) &&
            sessionToPlayer.TryGetValue(session, out var player))
        {
            room.BroadcastChat(player.Nick, text);
        }
        else if (spectatorRooms.TryGetValue(session, out var spectated))
        {
            spectated.BroadcastChat("관전자", text);
        }
    }

    private void HandleSpectate(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out _)) return;

        var room = rooms.Find(r => !r.IsFinished);
        if (room == null)
        {
            SendError(session, GomokuError.NoRoomToSpectate, "진행 중인 대국이 없습니다.");
            return;
        }

        spectatorRooms[session] = room;
        room.AddSpectator(session);
    }

    private bool TryGetRoomAndPlayer(KcpSession session, out GameRoom room, out Player player)
    {
        if (sessionToRoom.TryGetValue(session, out room!) &&
            sessionToPlayer.TryGetValue(session, out player!))
        {
            return true;
        }

        room = null!;
        player = null!;
        SendError(session, GomokuError.NotInRoom, "대국 중이 아닙니다.");
        return false;
    }

    private void SendError(KcpSession session, GomokuError code, string message)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.Error);
        writer.WriteByte((byte)code);
        writer.WriteString(message);
        session.Send(writer.Finish());
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>자원을 정리한다.</summary>
    public void Dispose() => server.Dispose();
}
