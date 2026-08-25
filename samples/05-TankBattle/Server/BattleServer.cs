using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KcpCSharpR1.Transport;
using TankBattle.Shared;

namespace TankBattle.Server;

/// <summary>서버가 들고 있는 플레이어.</summary>
internal sealed class BattlePlayer
{
    /// <summary>연결. 봇이면 null.</summary>
    public KcpSession? Session;

    /// <summary>플레이어 ID.</summary>
    public uint Id;

    /// <summary>닉네임.</summary>
    public string Nick = string.Empty;

    /// <summary>색 인덱스.</summary>
    public byte ColorIndex;

    /// <summary>현재 상태.</summary>
    public TankState State;

    /// <summary>아직 적용하지 않은 입력들(시퀀스 순).</summary>
    public readonly SortedDictionary<uint, TankInput> PendingInputs = new();

    /// <summary>마지막으로 처리한 입력 시퀀스. 클라이언트가 보정할 때 쓴다.</summary>
    public uint LastProcessedInputSeq;

    /// <summary>마지막으로 적용한 입력(입력이 안 오면 이것을 유지한다).</summary>
    public TankInput LastInput;

    /// <summary>죽은 뒤 되살아날 틱.</summary>
    public uint RespawnAtTick;

    /// <summary>킬 수.</summary>
    public ushort Kills;

    /// <summary>데스 수.</summary>
    public ushort Deaths;

    /// <summary>봇인지.</summary>
    public bool IsBot;

    /// <summary>봇의 다음 방향 전환 틱.</summary>
    public uint BotNextTurnTick;
}

/// <summary>
/// 탱크 배틀 서버 — 고정 틱 시뮬레이션 + 지연 보상.
/// </summary>
/// <remarks>
/// <para>서버가 하는 일은 세 가지다.</para>
/// <list type="number">
/// <item><description>받은 입력을 <b>시퀀스 순서대로</b> 적용하고, 어디까지 처리했는지 기억한다.</description></item>
/// <item><description>같은 물리 코드(<see cref="TankPhysics"/>)로 월드를 진행시킨다.</description></item>
/// <item><description>과거 1초의 스냅숏을 보관해 <b>지연 보상</b> 히트 판정에 쓴다.</description></item>
/// </list>
/// <para>
/// 클라이언트가 보낸 입력의 <b>내용</b>은 믿되(조작 의도이므로), 그 <b>결과</b>는 믿지 않는다.
/// 위치는 서버가 계산하고, 발사 쿨다운도 서버가 검증한다.
/// </para>
/// </remarks>
internal sealed class BattleServer : IDisposable
{
    private readonly KcpServer server;
    private readonly Action<string> log;
    private readonly Dictionary<KcpSession, BattlePlayer> bySession = new();
    private readonly List<BattlePlayer> players = new();
    private readonly LagCompensator history = new();
    private readonly byte[] sendBuffer = new byte[GameLimits.MaxMessageBytes];
    private readonly byte[] snapshotBuffer = new byte[GameLimits.MaxMessageBytes];
    private readonly Random random = new(20260825);
    private readonly bool lagCompensation;

    private uint nextPlayerId = 1;
    private uint tick;

    /// <summary>서버를 만든다.</summary>
    /// <param name="port">UDP 포트.</param>
    /// <param name="botCount">봇 수.</param>
    /// <param name="lagCompensation">지연 보상을 켤지(끄면 현재 위치로 판정한다).</param>
    /// <param name="log">로그 출력.</param>
    public BattleServer(int port, int botCount, bool lagCompensation, Action<string> log)
    {
        this.log = log;
        this.lagCompensation = lagCompensation;

        var options = new KcpServerOptions { MaxSessions = GameLimits.MaxPlayers * 2 };
        options.Session.SessionTimeoutMs = 8_000;

        server = new KcpServer(port, options);
        server.SessionDisconnected += OnDisconnected;
        server.ReliableReceived += OnReliable;
        server.UnreliableReceived += OnUnreliable;
        server.Error += ex => log($"오류: {ex.Message}");

        for (var i = 0; i < botCount; i++) AddBot();
    }

    /// <summary>현재 인원(봇 포함).</summary>
    public int PlayerCount => players.Count;

    /// <summary>현재 틱.</summary>
    public uint Tick => tick;

    /// <summary>마지막 틱 처리 시간(ms).</summary>
    public double LastTickMs { get; private set; }

    /// <summary>수신 루프를 시작한다.</summary>
    public void Start() => server.Start();

    /// <summary>수신 루프를 멈춘다.</summary>
    public void Stop() => server.Stop();

    /// <summary>틱 하나를 진행한다.</summary>
    public void Step()
    {
        var watch = Stopwatch.StartNew();
        tick++;

        lock (players)
        {
            foreach (var player in players)
            {
                if (player.IsBot) DriveBot(player);

                // 죽었으면 리스폰만 본다.
                if (!player.State.Alive)
                {
                    if (tick >= player.RespawnAtTick) Respawn(player);
                    continue;
                }

                ApplyInputs(player);
            }

            // 지연 보상을 위해 이번 틱의 위치를 보관한다.
            history.Record(tick, players);

            BroadcastSnapshot();
        }

        watch.Stop();
        LastTickMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>쌓인 입력을 시퀀스 순으로 적용한다.</summary>
    private void ApplyInputs(BattlePlayer player)
    {
        if (player.PendingInputs.Count == 0)
        {
            // 입력이 안 왔으면 마지막 입력을 유지한다(패킷 하나 빠졌다고 멈추지 않게).
            var held = player.LastInput;
            held.Fire = false;   // 발사만은 유지하지 않는다.
            player.State = TankPhysics.Step(player.State, held, tick);
            return;
        }

        foreach (var pair in player.PendingInputs.ToList())
        {
            var input = pair.Value;

            player.State = TankPhysics.Step(player.State, input, tick);
            player.LastProcessedInputSeq = input.Sequence;
            player.LastInput = input;

            if (input.Fire) TryFire(player, input);
        }

        player.PendingInputs.Clear();
    }

    /// <summary>발사를 처리한다(쿨다운 검증 + 지연 보상 히트 판정).</summary>
    private void TryFire(BattlePlayer shooter, in TankInput input)
    {
        // 서버가 쿨다운을 검증한다 — 클라이언트가 연사 패킷을 보내도 통하지 않는다.
        if (!TankPhysics.CanFire(shooter.State, tick)) return;

        shooter.State = TankPhysics.ApplyFireCooldown(shooter.State, tick);

        // 지연 보상: 사수가 화면에서 보고 있던 시점의 표적 위치로 되감는다.
        var rewindTicks = lagCompensation ? EstimateRewindTicks(shooter) : 0;
        var judgeTick = rewindTicks >= tick ? 0 : tick - rewindTicks;

        BattlePlayer? victim = null;
        var closest = float.MaxValue;

        foreach (var target in players)
        {
            if (ReferenceEquals(target, shooter)) continue;

            var targetState = lagCompensation
                ? history.StateAt(judgeTick, target.Id, target.State)
                : target.State;

            if (!TankPhysics.RayHitsTank(shooter.State.X, shooter.State.Y, input.TurretAngle,
                    GameLimits.FireRange, targetState, out var distance))
            {
                continue;
            }

            if (distance >= closest) continue;
            closest = distance;
            victim = target;
        }

        BroadcastShot(shooter, input.TurretAngle, victim?.Id ?? 0);

        if (victim == null) return;

        victim.State.Alive = false;
        victim.RespawnAtTick = tick + GameLimits.RespawnTicks;
        victim.Deaths++;
        shooter.Kills++;

        BroadcastKilled(victim.Id, shooter.Id);
        BroadcastScoreboard();

        log($"킬 {shooter.Nick} -> {victim.Nick} (되감기 {rewindTicks}틱)");
    }

    /// <summary>
    /// 사수의 화면 기준 시점까지 몇 틱을 되감을지 계산한다.
    /// </summary>
    /// <remarks>
    /// 사수가 보고 있던 것은 <c>지금 - (RTT/2 + 보간 지연)</c> 시점의 월드다.
    /// 그래서 그만큼 과거의 표적 위치로 판정해야 "내 화면에서 맞췄으면 맞은 것"이 된다.
    /// </remarks>
    private static uint EstimateRewindTicks(BattlePlayer shooter)
    {
        var halfRtt = (shooter.Session?.SrttMs ?? 0) / 2.0;
        var latencyMs = halfRtt + GameLimits.InterpolationDelayMs;
        var ticks = (uint)Math.Round(latencyMs / (1000.0 / GameLimits.TickRate));
        return Math.Min(ticks, GameLimits.HistoryTicks - 1);
    }

    private void Respawn(BattlePlayer player)
    {
        player.State = new TankState
        {
            X = random.Next(60, GameLimits.WorldWidth - 60),
            Y = random.Next(60, GameLimits.WorldHeight - 60),
            BodyAngle = (float)(random.NextDouble() * Math.PI * 2 - Math.PI),
            Alive = true,
        };

        var writer = new MessageWriter(sendBuffer, MessageType.Respawned);
        writer.WriteUInt32(player.Id);
        writer.WriteSingle(player.State.X);
        writer.WriteSingle(player.State.Y);
        Broadcast(writer.Finish());
    }

    private void BroadcastSnapshot()
    {
        var alive = players.Count;
        if (alive == 0) return;

        foreach (var receiver in players)
        {
            if (receiver.Session == null) continue;

            var writer = new MessageWriter(snapshotBuffer, MessageType.Snapshot);
            writer.WriteUInt32(tick);

            // ★ 이 값이 클라이언트 보정의 열쇠다: "너의 몇 번 입력까지 반영했다"
            writer.WriteUInt32(receiver.LastProcessedInputSeq);
            writer.WriteByte((byte)players.Count);

            foreach (var player in players)
            {
                writer.WriteUInt32(player.Id);
                writer.WriteSingle(player.State.X);
                writer.WriteSingle(player.State.Y);
                writer.WriteSingle(player.State.BodyAngle);
                writer.WriteSingle(player.State.TurretAngle);
                writer.WriteByte(player.State.Alive ? (byte)1 : (byte)0);
            }

            receiver.Session.SendUnreliable(writer.Finish());
        }
    }

    private void BroadcastShot(BattlePlayer shooter, float angle, uint hitId)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.Shot);
        writer.WriteUInt32(shooter.Id);
        writer.WriteSingle(shooter.State.X);
        writer.WriteSingle(shooter.State.Y);
        writer.WriteSingle(angle);
        writer.WriteUInt32(hitId);
        Broadcast(writer.Finish());
    }

    private void BroadcastKilled(uint victimId, uint killerId)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.Killed);
        writer.WriteUInt32(victimId);
        writer.WriteUInt32(killerId);
        Broadcast(writer.Finish());
    }

    private void BroadcastScoreboard()
    {
        var writer = new MessageWriter(sendBuffer, MessageType.Scoreboard);
        writer.WriteByte((byte)players.Count);
        foreach (var player in players)
        {
            writer.WriteUInt32(player.Id);
            writer.WriteUInt16(player.Kills);
            writer.WriteUInt16(player.Deaths);
        }
        Broadcast(writer.Finish());
    }

    private void Broadcast(ReadOnlySpan<byte> packet)
    {
        foreach (var player in players)
            player.Session?.Send(packet);
    }

    // ---------------- 수신 ----------------

    private void OnReliable(KcpSession session, ReadOnlyMemory<byte> data)
    {
        if (!MessageReader.TryParse(data.Span, out var reader)) return;
        if (reader.Type != MessageType.Join) return;
        if (!reader.TryReadString(out var nick)) return;

        nick = nick.Trim();
        if (nick.Length == 0) nick = "tank";
        if (nick.Length > GameLimits.MaxNickLength) nick = nick[..GameLimits.MaxNickLength];

        lock (players)
        {
            if (bySession.ContainsKey(session)) return;
            if (players.Count >= GameLimits.MaxPlayers) return;

            var player = new BattlePlayer
            {
                Session = session,
                Id = nextPlayerId++,
                Nick = nick,
                ColorIndex = (byte)(nextPlayerId % 8),
            };

            Respawn(player);
            bySession[session] = player;
            players.Add(player);

            var writer = new MessageWriter(sendBuffer, MessageType.Welcome);
            writer.WriteUInt32(player.Id);
            writer.WriteByte(GameLimits.TickRate);
            writer.WriteUInt16(GameLimits.WorldWidth);
            writer.WriteUInt16(GameLimits.WorldHeight);
            session.Send(writer.Finish());

            // 서로를 알린다.
            foreach (var other in players)
            {
                SendPlayerJoined(session, other);
                if (!ReferenceEquals(other, player)) other.Session?.Send(BuildPlayerJoined(player));
            }

            BroadcastScoreboard();
            log($"입장 {nick} (id={player.Id}, 총 {players.Count}명)");
        }
    }

    private ReadOnlySpan<byte> BuildPlayerJoined(BattlePlayer player)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.PlayerJoined);
        writer.WriteUInt32(player.Id);
        writer.WriteString(player.Nick);
        writer.WriteByte(player.ColorIndex);
        return writer.Finish();
    }

    private void SendPlayerJoined(KcpSession target, BattlePlayer player) =>
        target.Send(BuildPlayerJoined(player));

    private void OnUnreliable(KcpSession session, ReadOnlyMemory<byte> data)
    {
        if (!MessageReader.TryParse(data.Span, out var reader)) return;
        if (reader.Type != MessageType.InputBatch) return;
        if (!reader.TryReadByte(out var count)) return;

        lock (players)
        {
            if (!bySession.TryGetValue(session, out var player)) return;

            for (var i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out var seq) ||
                    !reader.TryReadByte(out var throttle) ||
                    !reader.TryReadByte(out var steer) ||
                    !reader.TryReadSingle(out var turret) ||
                    !reader.TryReadByte(out var fire))
                {
                    return;
                }

                // 이미 처리한 입력은 버린다(중복 탑재로 같은 입력이 여러 번 온다).
                if (seq <= player.LastProcessedInputSeq) continue;

                player.PendingInputs[seq] = new TankInput
                {
                    Sequence = seq,
                    Throttle = (sbyte)throttle,
                    Steer = (sbyte)steer,
                    TurretAngle = turret,
                    Fire = fire != 0,
                };
            }
        }
    }

    private void OnDisconnected(KcpSession session, KcpDisconnectReason reason)
    {
        lock (players)
        {
            if (!bySession.TryGetValue(session, out var player)) return;

            bySession.Remove(session);
            players.Remove(player);

            var writer = new MessageWriter(sendBuffer, MessageType.PlayerLeft);
            writer.WriteUInt32(player.Id);
            Broadcast(writer.Finish());

            log($"퇴장 {player.Nick} ({reason}, 남은 {players.Count}명)");
        }
    }

    // ---------------- 봇 ----------------

    private void AddBot()
    {
        var bot = new BattlePlayer
        {
            Id = nextPlayerId++,
            Nick = $"BOT{nextPlayerId - 1}",
            ColorIndex = (byte)(nextPlayerId % 8),
            IsBot = true,
        };

        Respawn(bot);
        players.Add(bot);
    }

    /// <summary>봇의 아주 단순한 행동: 가끔 방향을 바꾸고 계속 전진하며 주기적으로 쏜다.</summary>
    private void DriveBot(BattlePlayer bot)
    {
        if (!bot.State.Alive) return;

        if (tick >= bot.BotNextTurnTick)
        {
            bot.BotNextTurnTick = tick + (uint)random.Next(10, 40);
            bot.LastInput = new TankInput
            {
                Sequence = bot.LastInput.Sequence + 1,
                Throttle = 1,
                Steer = (sbyte)random.Next(-1, 2),
                TurretAngle = (float)(random.NextDouble() * Math.PI * 2 - Math.PI),
                Fire = random.Next(4) == 0,
            };
        }

        bot.PendingInputs[bot.LastInput.Sequence] = bot.LastInput;
        bot.LastInput.Sequence++;
    }

    /// <summary>자원을 정리한다.</summary>
    public void Dispose() => server.Dispose();
}
