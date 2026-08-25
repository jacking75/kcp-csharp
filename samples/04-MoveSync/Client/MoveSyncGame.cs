using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using KcpCSharpR1.Transport;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MoveSync.Shared;

namespace MoveSync.Client;

/// <summary>
/// MonoGame 클라이언트 — 입력 송신(비신뢰) + 스냅숏 보간 렌더링.
/// </summary>
/// <remarks>
/// <para>
/// <b>F1 로 보간을 껐다 켜 보는 것</b>이 이 샘플의 핵심 실습이다. 끄면 20Hz 로 뚝뚝 끊기고,
/// 켜면 60Hz 로 부드럽다. 두 상태의 차이가 곧 "보간이 하는 일"이다.
/// </para>
/// </remarks>
internal sealed class MoveSyncGame : Game
{
    private static readonly Color[] Palette =
    {
        new(232, 90, 90), new(90, 190, 232), new(150, 220, 120), new(240, 200, 90),
        new(200, 130, 230), new(120, 230, 210), new(240, 150, 180), new(180, 180, 190),
    };

    private readonly GraphicsDeviceManager graphics;
    private readonly KcpClient client = new();
    private readonly SnapshotBuffer buffer = new();
    private readonly Dictionary<uint, EntitySnapshot> rendered = new();
    private readonly Dictionary<uint, (string Nick, byte Color)> knownPlayers = new();
    private readonly List<string> chatLines = new();
    private readonly byte[] sendBuffer = new byte[ProtocolLimits.MaxMessageBytes];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string host;
    private readonly string nick;

    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private TinyFont font = null!;
    private KcpSession? session;

    private uint myId;
    private uint inputSeq;
    private double nextInputAtMs;
    private MoveDirection lastSentDirection = (MoveDirection)0xFF;
    private bool interpolate = true;
    private KeyboardState previousKeyboard;
    private string status = "CONNECTING...";

    // HUD 용 계측
    private double snapshotRateWindowStartMs;
    private long snapshotsAtWindowStart;
    private double snapshotRate;

    /// <summary>게임을 만든다.</summary>
    /// <param name="host">서버 주소.</param>
    /// <param name="nick">닉네임.</param>
    public MoveSyncGame(string host, string nick)
    {
        this.host = host;
        this.nick = nick;

        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = ProtocolLimits.WorldWidth,
            PreferredBackBufferHeight = ProtocolLimits.WorldHeight + 90,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 60.0);
    }

    /// <inheritdoc/>
    protected override void Initialize()
    {
        Window.Title = "KcpCSharpR1 샘플 4 - MoveSync";

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
            session = await client.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), ProtocolLimits.Port));

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
            interpolate = !interpolate;

        SendInput(keyboard);
        previousKeyboard = keyboard;

        UpdateSnapshotRate();
        base.Update(gameTime);
    }

    /// <summary>입력을 <see cref="ProtocolLimits.InputRate"/> Hz 로, 비신뢰 채널로 보낸다.</summary>
    private void SendInput(KeyboardState keyboard)
    {
        if (session?.State != KcpSessionState.Connected) return;

        var direction = MoveDirection.None;
        if (keyboard.IsKeyDown(Keys.W) || keyboard.IsKeyDown(Keys.Up)) direction |= MoveDirection.Up;
        if (keyboard.IsKeyDown(Keys.S) || keyboard.IsKeyDown(Keys.Down)) direction |= MoveDirection.Down;
        if (keyboard.IsKeyDown(Keys.A) || keyboard.IsKeyDown(Keys.Left)) direction |= MoveDirection.Left;
        if (keyboard.IsKeyDown(Keys.D) || keyboard.IsKeyDown(Keys.Right)) direction |= MoveDirection.Right;

        var nowMs = clock.Elapsed.TotalMilliseconds;

        // 방향이 바뀌면 즉시, 아니면 정해진 주기로 보낸다.
        var changed = direction != lastSentDirection;
        if (!changed && nowMs < nextInputAtMs) return;

        nextInputAtMs = nowMs + 1000.0 / ProtocolLimits.InputRate;
        lastSentDirection = direction;

        var writer = new MessageWriter(sendBuffer, MessageType.Input);
        writer.WriteUInt32(++inputSeq);
        writer.WriteByte((byte)direction);

        // 입력은 비신뢰다. 하나 놓쳐도 100ms 뒤 다음 입력이 덮어쓴다.
        session.SendUnreliable(writer.Finish());
    }

    private void UpdateSnapshotRate()
    {
        var nowMs = clock.Elapsed.TotalMilliseconds;
        if (nowMs - snapshotRateWindowStartMs < 1000) return;

        snapshotRate = (buffer.ReceivedCount - snapshotsAtWindowStart) * 1000.0 / (nowMs - snapshotRateWindowStartMs);
        snapshotsAtWindowStart = buffer.ReceivedCount;
        snapshotRateWindowStartMs = nowMs;
    }

    private void HandleReliable(ReadOnlySpan<byte> packet)
    {
        if (!MessageReader.TryParse(packet, out var reader)) return;

        switch (reader.Type)
        {
            case MessageType.Welcome:
                if (reader.TryReadUInt32(out myId))
                    status = $"CONNECTED AS #{myId}";
                break;

            case MessageType.PlayerJoined:
            {
                if (!reader.TryReadUInt32(out var id) ||
                    !reader.TryReadString(out var otherNick) ||
                    !reader.TryReadByte(out var color)) return;

                knownPlayers[id] = (otherNick, color);
                AddChat($"+ {otherNick} JOINED");
                break;
            }

            case MessageType.PlayerLeft:
                if (reader.TryReadUInt32(out var leftId))
                {
                    if (knownPlayers.TryGetValue(leftId, out var info)) AddChat($"- {info.Nick} LEFT");
                    knownPlayers.Remove(leftId);
                }
                break;

            case MessageType.ChatFrom:
                if (reader.TryReadString(out var from) && reader.TryReadString(out var text))
                    AddChat($"{from}: {text}");
                break;
        }
    }

    private void HandleSnapshot(ReadOnlySpan<byte> packet)
    {
        if (!MessageReader.TryParse(packet, out var reader)) return;
        if (reader.Type != MessageType.Snapshot) return;

        if (!reader.TryReadUInt32(out var tick) || !reader.TryReadByte(out var count)) return;

        var snapshot = new WorldSnapshot { Tick = tick, ReceivedAtMs = clock.Elapsed.TotalMilliseconds };

        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt32(out var id) ||
                !reader.TryReadSingle(out var x) ||
                !reader.TryReadSingle(out var y) ||
                !reader.TryReadByte(out var dir)) return;

            snapshot.Entities.Add(new EntitySnapshot
            {
                Id = id,
                X = x,
                Y = y,
                Direction = (MoveDirection)dir,
            });
        }

        buffer.Add(snapshot);
    }

    private void AddChat(string line)
    {
        chatLines.Add(line);
        if (chatLines.Count > 4) chatLines.RemoveAt(0);
    }

    /// <inheritdoc/>
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(24, 26, 30));

        batch.Begin();

        // 월드 경계
        Primitives.DrawRect(batch, pixel,
            new Rectangle(0, 0, ProtocolLimits.WorldWidth, ProtocolLimits.WorldHeight),
            new Color(60, 64, 72), 2);

        buffer.Sample(clock.Elapsed.TotalMilliseconds, rendered, interpolate);

        foreach (var entity in rendered.Values)
        {
            var color = knownPlayers.TryGetValue(entity.Id, out var info)
                ? Palette[info.Color % Palette.Length]
                : Palette[entity.Id % (uint)Palette.Length];

            var center = new Vector2(entity.X, entity.Y);
            Primitives.FillCircle(batch, pixel, center, ProtocolLimits.PlayerRadius, color);

            if (entity.Id == myId)
            {
                Primitives.DrawRect(batch, pixel,
                    new Rectangle((int)(entity.X - 18), (int)(entity.Y - 18), 36, 36), Color.White, 1);
            }

            var label = knownPlayers.TryGetValue(entity.Id, out var known) ? known.Nick : $"#{entity.Id}";
            font.Draw(batch, label, (int)entity.X - TinyFont.Measure(label) / 2, (int)entity.Y - 26, Color.White);
        }

        DrawHud();

        batch.End();
        base.Draw(gameTime);
    }

    private void DrawHud()
    {
        var y = ProtocolLimits.WorldHeight + 6;

        font.Draw(batch, status, 8, y, Color.Yellow, 2);
        y += 18;

        var srtt = session?.SrttMs ?? 0;
        var stats = session?.Stats ?? default;
        var retransmit = stats.SegmentsSent == 0
            ? 0
            : (stats.RetransmitSegments + stats.FastRetransmitSegments + stats.EarlyRetransmitSegments)
              * 100.0 / stats.SegmentsSent;

        font.Draw(batch,
            $"RTT {srtt}MS  RETX {retransmit:F1}%  SNAP {snapshotRate:F0}/S  DROPPED {buffer.DiscardedCount}",
            8, y, Color.LightGreen);
        y += 12;

        font.Draw(batch,
            $"INTERP {(interpolate ? "ON " : "OFF")} ({ProtocolLimits.InterpolationDelayMs}MS)  F1: TOGGLE   WASD: MOVE   ESC: QUIT",
            8, y, interpolate ? Color.Gray : Color.Orange);
        y += 14;

        foreach (var line in chatLines)
        {
            font.Draw(batch, line, 8, y, Color.DarkGray);
            y += 10;
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
