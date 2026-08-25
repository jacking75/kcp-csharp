using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KcpProject.Transport;
using MoveSync.Shared;

namespace MoveSync.Server;

/// <summary>서버가 들고 있는 플레이어 하나.</summary>
internal sealed class PlayerState
{
    /// <summary>연결.</summary>
    public KcpSession Session = null!;

    /// <summary>서버가 부여한 ID.</summary>
    public uint Id;

    /// <summary>닉네임.</summary>
    public string Nick = string.Empty;

    /// <summary>색 인덱스(클라이언트가 색을 고를 때 쓴다).</summary>
    public byte ColorIndex;

    /// <summary>현재 위치.</summary>
    public float X;

    /// <summary>현재 위치.</summary>
    public float Y;

    /// <summary>마지막으로 받은 입력.</summary>
    public MoveDirection Direction;

    /// <summary>마지막으로 처리한 입력 시퀀스(오래된 입력을 버리는 데 쓴다).</summary>
    public uint LastInputSeq;

    /// <summary>입장 절차를 마쳤는지.</summary>
    public bool Joined;
}

/// <summary>
/// 20Hz 고정 틱 서버.
/// </summary>
/// <remarks>
/// <para>
/// <b>고정 틱 루프</b>가 이 샘플의 뼈대다. 서버는 50ms마다 한 번 깨어나
/// ① 그동안 쌓인 입력을 적용하고 ② 월드를 진행시킨 뒤 ③ 전체 스냅숏을 뿌린다.
/// 클라이언트의 프레임레이트(60Hz, 144Hz…)와 무관하게 서버는 항상 같은 속도로 돈다.
/// </para>
/// <para>
/// 스냅숏은 <b>비신뢰 채널</b>로 보낸다. 50ms 뒤면 새 스냅숏이 오는데, 낡은 것을 재전송받아
/// 순서대로 기다릴 이유가 없다. 이것이 KCP 를 쓰면서도 "모든 것을 신뢰로 보내지 않는" 이유다.
/// </para>
/// </remarks>
internal sealed class WorldServer : IDisposable
{
    private readonly KcpServer server;
    private readonly Action<string> log;
    private readonly Dictionary<KcpSession, PlayerState> players = new();
    private readonly byte[] sendBuffer = new byte[ProtocolLimits.MaxMessageBytes];
    private readonly byte[] snapshotBuffer = new byte[ProtocolLimits.MaxMessageBytes];
    private readonly Random random = new(20260825);

    private uint nextPlayerId = 1;
    private uint tick;

    /// <summary>서버를 만든다.</summary>
    /// <param name="port">UDP 포트.</param>
    /// <param name="log">로그 출력.</param>
    public WorldServer(int port, Action<string> log)
    {
        this.log = log;

        var options = new KcpServerOptions { MaxSessions = ProtocolLimits.MaxPlayers * 2 };
        options.Session.SessionTimeoutMs = 8_000;

        server = new KcpServer(port, options);
        server.SessionDisconnected += OnDisconnected;
        server.ReliableReceived += OnReliable;
        server.UnreliableReceived += OnUnreliable;
        server.Error += ex => log($"오류: {ex.Message}");
    }

    /// <summary>현재 접속 인원.</summary>
    public int PlayerCount => players.Count;

    /// <summary>지금까지 진행한 틱 수.</summary>
    public uint Tick => tick;

    /// <summary>마지막 틱 처리에 걸린 시간(ms).</summary>
    public double LastTickMs { get; private set; }

    /// <summary>수신 루프를 시작한다.</summary>
    public void Start() => server.Start();

    /// <summary>수신 루프를 멈춘다.</summary>
    public void Stop() => server.Stop();

    /// <summary>
    /// 틱 한 번을 진행한다. <see cref="ProtocolLimits.TickRate"/> Hz 로 호출한다.
    /// </summary>
    public void Step()
    {
        var watch = Stopwatch.StartNew();

        tick++;
        const float dt = 1f / ProtocolLimits.TickRate;

        // ① 입력 적용 + ② 월드 진행
        lock (players)
        {
            foreach (var player in players.Values)
            {
                if (!player.Joined) continue;
                Move(player, dt);
            }

            // ③ 스냅숏 브로드캐스트(비신뢰)
            BroadcastSnapshot();
        }

        watch.Stop();
        LastTickMs = watch.Elapsed.TotalMilliseconds;
    }

    private static void Move(PlayerState player, float dt)
    {
        var dx = 0f;
        var dy = 0f;

        if ((player.Direction & MoveDirection.Left) != 0) dx -= 1f;
        if ((player.Direction & MoveDirection.Right) != 0) dx += 1f;
        if ((player.Direction & MoveDirection.Up) != 0) dy -= 1f;
        if ((player.Direction & MoveDirection.Down) != 0) dy += 1f;

        if (dx != 0f && dy != 0f)
        {
            // 대각선이 빨라지지 않도록 정규화한다.
            var inv = 1f / MathF.Sqrt(2f);
            dx *= inv;
            dy *= inv;
        }

        player.X += dx * ProtocolLimits.MoveSpeed * dt;
        player.Y += dy * ProtocolLimits.MoveSpeed * dt;

        // 서버가 경계를 강제한다 — 클라이언트가 무엇을 보내든 월드 밖으로 못 나간다.
        player.X = Math.Clamp(player.X, ProtocolLimits.PlayerRadius, ProtocolLimits.WorldWidth - ProtocolLimits.PlayerRadius);
        player.Y = Math.Clamp(player.Y, ProtocolLimits.PlayerRadius, ProtocolLimits.WorldHeight - ProtocolLimits.PlayerRadius);
    }

    private void BroadcastSnapshot()
    {
        var joined = players.Values.Where(p => p.Joined).ToList();
        if (joined.Count == 0) return;

        var writer = new MessageWriter(snapshotBuffer, MessageType.Snapshot);
        writer.WriteUInt32(tick);
        writer.WriteByte((byte)joined.Count);

        foreach (var player in joined)
        {
            writer.WriteUInt32(player.Id);
            writer.WriteSingle(player.X);
            writer.WriteSingle(player.Y);
            writer.WriteByte((byte)player.Direction);
        }

        var packet = writer.Finish();
        foreach (var player in joined)
            player.Session.SendUnreliable(packet);
    }

    private void OnReliable(KcpSession session, ReadOnlyMemory<byte> data)
    {
        if (!MessageReader.TryParse(data.Span, out var reader)) return;

        switch (reader.Type)
        {
            case MessageType.Join: HandleJoin(session, ref reader); break;
            case MessageType.Chat: HandleChat(session, ref reader); break;
        }
    }

    private void OnUnreliable(KcpSession session, ReadOnlyMemory<byte> data)
    {
        if (!MessageReader.TryParse(data.Span, out var reader)) return;
        if (reader.Type != MessageType.Input) return;

        if (!reader.TryReadUInt32(out var seq) || !reader.TryReadByte(out var dir)) return;

        lock (players)
        {
            if (!players.TryGetValue(session, out var player) || !player.Joined) return;

            // 비신뢰 채널이라 순서가 뒤바뀔 수 있다. 낡은 입력은 버린다.
            if (seq <= player.LastInputSeq) return;

            player.LastInputSeq = seq;
            player.Direction = (MoveDirection)(dir & 0x0F);
        }
    }

    private void HandleJoin(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var nick)) return;

        nick = nick.Trim();
        if (nick.Length == 0) nick = "player";
        if (nick.Length > ProtocolLimits.MaxNickLength) nick = nick[..ProtocolLimits.MaxNickLength];

        lock (players)
        {
            if (players.ContainsKey(session)) return;
            if (players.Count >= ProtocolLimits.MaxPlayers) return;

            var player = new PlayerState
            {
                Session = session,
                Id = nextPlayerId++,
                Nick = nick,
                ColorIndex = (byte)(nextPlayerId % 8),
                X = random.Next(50, ProtocolLimits.WorldWidth - 50),
                Y = random.Next(50, ProtocolLimits.WorldHeight - 50),
                Joined = true,
            };

            players[session] = player;

            // 새 플레이어에게: 내 ID + 월드 정보
            var writer = new MessageWriter(sendBuffer, MessageType.Welcome);
            writer.WriteUInt32(player.Id);
            writer.WriteByte(ProtocolLimits.TickRate);
            writer.WriteUInt16(ProtocolLimits.WorldWidth);
            writer.WriteUInt16(ProtocolLimits.WorldHeight);
            session.Send(writer.Finish());

            // 새 플레이어에게 기존 인원을, 기존 인원에게 새 플레이어를 알린다(둘 다 신뢰 채널).
            foreach (var other in players.Values)
            {
                if (ReferenceEquals(other, player)) continue;
                SendPlayerJoined(session, other);
                SendPlayerJoined(other.Session, player);
            }

            log($"입장 {nick} (id={player.Id}, 총 {players.Count}명)");
        }
    }

    private void SendPlayerJoined(KcpSession target, PlayerState player)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.PlayerJoined);
        writer.WriteUInt32(player.Id);
        writer.WriteString(player.Nick);
        writer.WriteByte(player.ColorIndex);
        target.Send(writer.Finish());
    }

    private void HandleChat(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var text)) return;
        if (text.Length == 0 || text.Length > ProtocolLimits.MaxChatLength) return;

        lock (players)
        {
            if (!players.TryGetValue(session, out var sender)) return;

            var writer = new MessageWriter(sendBuffer, MessageType.ChatFrom);
            writer.WriteString(sender.Nick);
            writer.WriteString(text);
            var packet = writer.Finish();

            foreach (var player in players.Values)
                player.Session.Send(packet);
        }
    }

    private void OnDisconnected(KcpSession session, KcpDisconnectReason reason)
    {
        lock (players)
        {
            if (!players.TryGetValue(session, out var player)) return;
            players.Remove(session);

            var writer = new MessageWriter(sendBuffer, MessageType.PlayerLeft);
            writer.WriteUInt32(player.Id);
            var packet = writer.Finish();

            foreach (var other in players.Values)
                other.Session.Send(packet);

            log($"퇴장 {player.Nick} ({reason}, 남은 {players.Count}명)");
        }
    }

    /// <summary>자원을 정리한다.</summary>
    public void Dispose() => server.Dispose();
}
