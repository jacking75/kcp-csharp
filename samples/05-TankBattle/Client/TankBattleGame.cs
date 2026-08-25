using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using KcpCSharpR1.Transport;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using TankBattle.Shared;

namespace TankBattle.Client;

/// <summary>다른 플레이어(보간해서 그린다).</summary>
internal sealed class RemoteTank
{
    /// <summary>닉네임.</summary>
    public string Nick = "?";

    /// <summary>색 인덱스.</summary>
    public byte ColorIndex;

    /// <summary>이전 스냅숏 상태.</summary>
    public TankState From;

    /// <summary>최신 스냅숏 상태.</summary>
    public TankState To;

    /// <summary>보간 시작 시각(ms).</summary>
    public double FromTimeMs;

    /// <summary>보간 끝 시각(ms).</summary>
    public double ToTimeMs;

    /// <summary>킬 수.</summary>
    public ushort Kills;

    /// <summary>데스 수.</summary>
    public ushort Deaths;
}

/// <summary>화면에 잠깐 남는 발사 궤적.</summary>
internal struct ShotEffect
{
    /// <summary>시작 X.</summary>
    public float X;

    /// <summary>시작 Y.</summary>
    public float Y;

    /// <summary>각도.</summary>
    public float Angle;

    /// <summary>맞았는지.</summary>
    public bool Hit;

    /// <summary>사라질 시각(ms).</summary>
    public double ExpiresAtMs;
}

/// <summary>
/// MonoGame 클라이언트 — 예측·보정·보간·지연 보상의 종합.
/// </summary>
/// <remarks>
/// <b>F1</b> 로 예측을 껐다 켜 보는 것이 이 샘플의 핵심 실습이다.
/// 끄면 내 탱크가 RTT 만큼 늦게 반응한다(로컬에서는 티가 안 나므로 clumsy 로 지연을 걸어 보자).
/// </remarks>
internal sealed class TankBattleGame : Game
{
    private static readonly Color[] Palette =
    {
        new(232, 90, 90), new(90, 190, 232), new(150, 220, 120), new(240, 200, 90),
        new(200, 130, 230), new(120, 230, 210), new(240, 150, 180), new(180, 180, 190),
    };

    private readonly GraphicsDeviceManager graphics;
    private readonly KcpClient client = new();
    private readonly PredictionState prediction = new();
    private readonly Dictionary<uint, RemoteTank> remotes = new();
    private readonly List<ShotEffect> shots = new();
    private readonly List<TankInput> recentInputs = new();
    private readonly byte[] sendBuffer = new byte[GameLimits.MaxMessageBytes];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string host;
    private readonly string nick;

    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private TinyFont font = null!;
    private KcpSession? session;

    private uint myId;
    private uint serverTick;
    private double nextInputAtMs;
    private KeyboardState previousKeyboard;
    private string status = "CONNECTING...";
    private long snapshotsReceived;
    private long snapshotsDiscarded;

    /// <summary>게임을 만든다.</summary>
    /// <param name="host">서버 주소.</param>
    /// <param name="nick">닉네임.</param>
    public TankBattleGame(string host, string nick)
    {
        this.host = host;
        this.nick = nick;

        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = GameLimits.WorldWidth,
            PreferredBackBufferHeight = GameLimits.WorldHeight + 100,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 60.0);
    }

    /// <inheritdoc/>
    protected override void Initialize()
    {
        Window.Title = "KcpCSharpR1 샘플 5 - TankBattle";

        client.ReliableReceived += (_, data) => HandleReliable(data.Span);
        client.UnreliableReceived += (_, data) => HandleSnapshot(data.Span);
        client.Disconnected += (_, reason) => status = $"DISCONNECTED ({reason})";

        _ = ConnectAsync();
        base.Initialize();
    }

    private async Task ConnectAsync()
    {
        try
        {
            session = await client.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), GameLimits.Port));

            var writer = new MessageWriter(sendBuffer, MessageType.Join);
            writer.WriteString(nick);
            session.Send(writer.Finish());

            status = "JOINING...";
        }
        catch (Exception ex)
        {
            status = $"CONNECT FAILED: {ex.Message}";
        }
    }

    /// <inheritdoc/>
    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice);
        pixel = Primitives.CreatePixel(GraphicsDevice);
        font = new TinyFont(pixel);
    }

    /// <inheritdoc/>
    protected override void Update(GameTime gameTime)
    {
        client.Update();   // ★ 매 프레임 폴링

        var keyboard = Keyboard.GetState();

        if (keyboard.IsKeyDown(Keys.Escape) && !previousKeyboard.IsKeyDown(Keys.Escape))
            Exit();

        if (keyboard.IsKeyDown(Keys.F1) && !previousKeyboard.IsKeyDown(Keys.F1))
            prediction.Enabled = !prediction.Enabled;

        SendInput(keyboard);
        previousKeyboard = keyboard;

        var nowMs = clock.Elapsed.TotalMilliseconds;
        shots.RemoveAll(shot => shot.ExpiresAtMs < nowMs);

        base.Update(gameTime);
    }

    private void SendInput(KeyboardState keyboard)
    {
        if (session?.State != KcpSessionState.Connected || myId == 0) return;

        var nowMs = clock.Elapsed.TotalMilliseconds;
        if (nowMs < nextInputAtMs) return;
        nextInputAtMs = nowMs + 1000.0 / GameLimits.InputRate;

        sbyte throttle = 0;
        sbyte steer = 0;
        if (keyboard.IsKeyDown(Keys.W) || keyboard.IsKeyDown(Keys.Up)) throttle = 1;
        if (keyboard.IsKeyDown(Keys.S) || keyboard.IsKeyDown(Keys.Down)) throttle = -1;
        if (keyboard.IsKeyDown(Keys.A) || keyboard.IsKeyDown(Keys.Left)) steer = -1;
        if (keyboard.IsKeyDown(Keys.D) || keyboard.IsKeyDown(Keys.Right)) steer = 1;

        // 포탑은 마우스를 향한다.
        var mouse = Mouse.GetState();
        var turret = MathF.Atan2(mouse.Y - prediction.Predicted.Y, mouse.X - prediction.Predicted.X);

        var fire = keyboard.IsKeyDown(Keys.Space);

        var input = prediction.CreateInput(throttle, steer, turret, fire, serverTick);

        // ★ 최근 입력 3개를 한 패킷에 실어 보낸다.
        // 비신뢰 채널이라 하나쯤 유실돼도 다음 패킷에 다시 들어 있어 서버가 놓치지 않는다.
        recentInputs.Add(input);
        while (recentInputs.Count > GameLimits.InputRedundancy) recentInputs.RemoveAt(0);

        var writer = new MessageWriter(sendBuffer, MessageType.InputBatch);
        writer.WriteByte((byte)recentInputs.Count);
        foreach (var item in recentInputs)
        {
            writer.WriteUInt32(item.Sequence);
            writer.WriteByte((byte)item.Throttle);
            writer.WriteByte((byte)item.Steer);
            writer.WriteSingle(item.TurretAngle);
            writer.WriteByte(item.Fire ? (byte)1 : (byte)0);
        }

        session.SendUnreliable(writer.Finish());
    }

    private void HandleReliable(ReadOnlySpan<byte> packet)
    {
        if (!MessageReader.TryParse(packet, out var reader)) return;

        switch (reader.Type)
        {
            case MessageType.Welcome:
                if (reader.TryReadUInt32(out myId))
                    status = $"IN BATTLE AS #{myId}";
                break;

            case MessageType.PlayerJoined:
            {
                if (!reader.TryReadUInt32(out var id) ||
                    !reader.TryReadString(out var otherNick) ||
                    !reader.TryReadByte(out var color)) return;

                if (!remotes.TryGetValue(id, out var tank))
                    remotes[id] = tank = new RemoteTank();

                tank.Nick = otherNick;
                tank.ColorIndex = color;
                break;
            }

            case MessageType.PlayerLeft:
                if (reader.TryReadUInt32(out var leftId)) remotes.Remove(leftId);
                break;

            case MessageType.Shot:
            {
                if (!reader.TryReadUInt32(out _) ||
                    !reader.TryReadSingle(out var x) ||
                    !reader.TryReadSingle(out var y) ||
                    !reader.TryReadSingle(out var angle) ||
                    !reader.TryReadUInt32(out var hitId)) return;

                shots.Add(new ShotEffect
                {
                    X = x,
                    Y = y,
                    Angle = angle,
                    Hit = hitId != 0,
                    ExpiresAtMs = clock.Elapsed.TotalMilliseconds + 120,
                });
                break;
            }

            case MessageType.Respawned:
            {
                if (!reader.TryReadUInt32(out var id) ||
                    !reader.TryReadSingle(out var x) ||
                    !reader.TryReadSingle(out var y)) return;

                if (id == myId)
                {
                    // 서버가 위치를 통째로 정했으므로 예측을 초기화한다.
                    prediction.Reset(new TankState { X = x, Y = y, Alive = true });
                }
                break;
            }

            case MessageType.Scoreboard:
            {
                if (!reader.TryReadByte(out var count)) return;
                for (var i = 0; i < count; i++)
                {
                    if (!reader.TryReadUInt32(out var id) ||
                        !reader.TryReadUInt16(out var kills) ||
                        !reader.TryReadUInt16(out var deaths)) return;

                    if (!remotes.TryGetValue(id, out var tank))
                        remotes[id] = tank = new RemoteTank();

                    tank.Kills = kills;
                    tank.Deaths = deaths;
                }
                break;
            }
        }
    }

    private void HandleSnapshot(ReadOnlySpan<byte> packet)
    {
        if (!MessageReader.TryParse(packet, out var reader)) return;
        if (reader.Type != MessageType.Snapshot) return;

        if (!reader.TryReadUInt32(out var tick) ||
            !reader.TryReadUInt32(out var lastProcessedSeq) ||
            !reader.TryReadByte(out var count)) return;

        snapshotsReceived++;

        // 비신뢰 채널이라 순서가 뒤바뀔 수 있다. 낡은 스냅숏은 버린다.
        if (tick <= serverTick)
        {
            snapshotsDiscarded++;
            return;
        }

        serverTick = tick;
        var nowMs = clock.Elapsed.TotalMilliseconds;

        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt32(out var id) ||
                !reader.TryReadSingle(out var x) ||
                !reader.TryReadSingle(out var y) ||
                !reader.TryReadSingle(out var body) ||
                !reader.TryReadSingle(out var turret) ||
                !reader.TryReadByte(out var alive)) return;

            var state = new TankState
            {
                X = x,
                Y = y,
                BodyAngle = body,
                TurretAngle = turret,
                Alive = alive != 0,
            };

            if (id == myId)
            {
                // ★ 보정: 서버 상태 + 미확인 입력 재적용
                prediction.Reconcile(state, lastProcessedSeq, tick);
                continue;
            }

            if (!remotes.TryGetValue(id, out var tank))
                remotes[id] = tank = new RemoteTank { From = state, To = state };

            // 다른 탱크는 두 스냅숏 사이를 보간해 그린다.
            tank.From = tank.To;
            tank.FromTimeMs = tank.ToTimeMs;
            tank.To = state;
            tank.ToTimeMs = nowMs;
        }
    }

    /// <inheritdoc/>
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(22, 24, 28));

        batch.Begin();

        Primitives.DrawRect(batch, pixel,
            new Rectangle(0, 0, GameLimits.WorldWidth, GameLimits.WorldHeight), new Color(58, 62, 70), 2);

        var nowMs = clock.Elapsed.TotalMilliseconds;
        var renderTime = nowMs - GameLimits.InterpolationDelayMs;

        foreach (var pair in remotes)
        {
            var tank = pair.Value;
            if (tank.ToTimeMs <= 0) continue;

            var span = tank.ToTimeMs - tank.FromTimeMs;
            var t = span <= 0.0001 ? 1f : (float)Math.Clamp((renderTime - tank.FromTimeMs) / span, 0, 1);

            var state = tank.To;
            state.X = tank.From.X + (tank.To.X - tank.From.X) * t;
            state.Y = tank.From.Y + (tank.To.Y - tank.From.Y) * t;

            DrawTank(state, Palette[tank.ColorIndex % Palette.Length], tank.Nick, false);
        }

        if (myId != 0)
            DrawTank(prediction.Predicted, Color.White, nick, true);

        foreach (var shot in shots)
        {
            var end = new Vector2(
                shot.X + MathF.Cos(shot.Angle) * GameLimits.FireRange,
                shot.Y + MathF.Sin(shot.Angle) * GameLimits.FireRange);

            Primitives.DrawLine(batch, pixel, new Vector2(shot.X, shot.Y), end,
                shot.Hit ? Color.OrangeRed : new Color(200, 200, 120), 2f);
        }

        DrawHud();

        batch.End();
        base.Draw(gameTime);
    }

    private void DrawTank(in TankState state, Color color, string label, bool isMe)
    {
        if (!state.Alive)
        {
            font.Draw(batch, "X", (int)state.X - 3, (int)state.Y - 4, Color.DarkRed, 2);
            return;
        }

        var center = new Vector2(state.X, state.Y);
        Primitives.FillCircle(batch, pixel, center, GameLimits.TankRadius, color);

        // 차체 방향
        var nose = center + new Vector2(MathF.Cos(state.BodyAngle), MathF.Sin(state.BodyAngle)) * GameLimits.TankRadius;
        Primitives.DrawLine(batch, pixel, center, nose, Color.Black, 3f);

        // 포탑
        var muzzle = center + new Vector2(MathF.Cos(state.TurretAngle), MathF.Sin(state.TurretAngle)) * (GameLimits.TankRadius + 10);
        Primitives.DrawLine(batch, pixel, center, muzzle, isMe ? Color.White : Color.Gray, 2f);

        font.Draw(batch, label, (int)state.X - TinyFont.Measure(label) / 2, (int)state.Y - 30, Color.White);
    }

    private void DrawHud()
    {
        var y = GameLimits.WorldHeight + 6;

        font.Draw(batch, status, 8, y, Color.Yellow, 2);
        y += 18;

        var srtt = session?.SrttMs ?? 0;
        var stats = session?.Stats ?? default;
        var retransmit = stats.SegmentsSent == 0
            ? 0
            : (stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments)
              * 100.0 / stats.SegmentsSent;

        font.Draw(batch,
            $"RTT {srtt}MS  RETX {retransmit:F1}%  SNAP {snapshotsReceived} (DROP {snapshotsDiscarded})  TICK {serverTick}",
            8, y, Color.LightGreen);
        y += 12;

        font.Draw(batch,
            $"PREDICTION {(prediction.Enabled ? "ON " : "OFF")}  PENDING {prediction.PendingInputCount}  " +
            $"CORRECTIONS {prediction.CorrectionCount} (LAST {prediction.LastCorrectionDistance:F1}PX)",
            8, y, prediction.Enabled ? Color.Gray : Color.Orange);
        y += 12;

        font.Draw(batch, "WASD: MOVE   MOUSE: AIM   SPACE: FIRE   F1: PREDICTION   ESC: QUIT", 8, y, Color.DimGray);
        y += 14;

        // 점수판
        var column = 8;
        foreach (var pair in remotes)
        {
            var tank = pair.Value;
            font.Draw(batch, $"{tank.Nick} {tank.Kills}/{tank.Deaths}", column, y,
                Palette[tank.ColorIndex % Palette.Length]);
            column += 110;
            if (column > GameLimits.WorldWidth - 110) break;
        }
    }

    /// <inheritdoc/>
    protected override void UnloadContent()
    {
        client.Disconnect();
        client.Update();
        client.Dispose();
        pixel.Dispose();
        batch.Dispose();
        graphics.Dispose();
    }
}
