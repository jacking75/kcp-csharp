using System;
using Gomoku.Shared;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Gomoku.Client;

/// <summary>
/// MonoGame 클라이언트.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 샘플이 보여 주는 MonoGame ↔ KCP 연결법은 한 줄이다</b>:
/// <c>Update()</c> 안에서 <see cref="NetworkClient.Poll"/> 을 부른다. 그게 전부다.
/// 수신 콜백이 게임 스레드에서 발화하므로 렌더링 상태를 직접 고쳐도 안전하다.
/// </para>
/// <para>
/// 콘텐츠 파이프라인(.mgcb)을 쓰지 않는다. 보드·돌은 도형으로, 글자는 <see cref="TinyFont"/> 로 그린다.
/// 덕분에 에셋 없이 <c>dotnet run</c> 만으로 돌아간다.
/// </para>
/// </remarks>
internal sealed class GomokuGame : Game
{
    private const int CellSize = 36;
    private const int Margin = 40;
    private const int BoardPixels = (GomokuBoard.Size - 1) * CellSize;
    private const int PanelWidth = 260;

    private readonly GraphicsDeviceManager graphics;
    private readonly NetworkClient network;
    private readonly string host;
    private readonly string? reconnectToken;

    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private TinyFont font = null!;

    private MouseState previousMouse;
    private KeyboardState previousKeyboard;
    private (int X, int Y) hover = (-1, -1);

    /// <summary>게임을 만든다.</summary>
    /// <param name="host">서버 주소.</param>
    /// <param name="nick">내 닉네임.</param>
    /// <param name="reconnectToken">재접속 토큰(없으면 null).</param>
    public GomokuGame(string host, string nick, string? reconnectToken)
    {
        this.host = host;
        this.reconnectToken = reconnectToken;
        network = new NetworkClient(nick);

        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Margin * 2 + BoardPixels + PanelWidth,
            PreferredBackBufferHeight = Margin * 2 + BoardPixels,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;

        // 60Hz 고정 틱. 네트워크 폴링 주기도 이 값을 따라간다.
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 60.0);
    }

    /// <inheritdoc/>
    protected override void Initialize()
    {
        Window.Title = "KcpCSharpR1 샘플 3 - Gomoku";
        _ = network.ConnectAsync(host, reconnectToken);
        base.Initialize();
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
        // ★ 이 한 줄이 MonoGame 과 KCP 를 잇는 전부다.
        network.Poll();

        var keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Escape) && !previousKeyboard.IsKeyDown(Keys.Escape))
            Exit();
        if (keyboard.IsKeyDown(Keys.R) && !previousKeyboard.IsKeyDown(Keys.R))
            network.Surrender();
        previousKeyboard = keyboard;

        var mouse = Mouse.GetState();
        hover = ToBoardCoordinate(mouse.X, mouse.Y);

        if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released)
        {
            if (hover.X >= 0) network.RequestPlace(hover.X, hover.Y);
        }
        previousMouse = mouse;

        base.Update(gameTime);
    }

    /// <summary>화면 좌표를 보드 칸으로 바꾼다. 판 밖이면 (-1,-1).</summary>
    private static (int X, int Y) ToBoardCoordinate(int screenX, int screenY)
    {
        var x = (int)MathF.Round((screenX - Margin) / (float)CellSize);
        var y = (int)MathF.Round((screenY - Margin) / (float)CellSize);

        if (!GomokuBoard.InBounds(x, y)) return (-1, -1);

        // 교점에서 너무 멀면 무시한다.
        var dx = MathF.Abs(screenX - (Margin + x * CellSize));
        var dy = MathF.Abs(screenY - (Margin + y * CellSize));
        return dx > CellSize * 0.45f || dy > CellSize * 0.45f ? (-1, -1) : (x, y);
    }

    /// <inheritdoc/>
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(28, 28, 32));

        batch.Begin();
        DrawBoard();
        DrawStones();
        DrawPanel();
        batch.End();

        base.Draw(gameTime);
    }

    private void DrawBoard()
    {
        var boardRect = new Rectangle(Margin - 18, Margin - 18, BoardPixels + 36, BoardPixels + 36);
        Primitives.FillRect(batch, pixel, boardRect, new Color(196, 160, 96));

        for (var i = 0; i < GomokuBoard.Size; i++)
        {
            var offset = i * CellSize;
            Primitives.FillRect(batch, pixel,
                new Rectangle(Margin, Margin + offset, BoardPixels + 1, 1), new Color(60, 40, 20));
            Primitives.FillRect(batch, pixel,
                new Rectangle(Margin + offset, Margin, 1, BoardPixels + 1), new Color(60, 40, 20));
        }

        // 화점(3, 7, 11)
        foreach (var y in new[] { 3, 7, 11 })
        {
            foreach (var x in new[] { 3, 7, 11 })
            {
                Primitives.FillCircle(batch, pixel,
                    new Vector2(Margin + x * CellSize, Margin + y * CellSize), 3f, new Color(60, 40, 20));
            }
        }

        // 마우스가 올라간 교점 미리보기
        if (hover.X >= 0 && network.Phase == ClientPhase.Playing &&
            network.Board.Turn == network.MyColor &&
            network.Board[hover.X, hover.Y] == StoneColor.None)
        {
            var center = new Vector2(Margin + hover.X * CellSize, Margin + hover.Y * CellSize);
            var color = network.MyColor == StoneColor.Black ? Color.Black : Color.White;
            Primitives.FillCircle(batch, pixel, center, CellSize * 0.42f, color * 0.35f);
        }
    }

    private void DrawStones()
    {
        for (var y = 0; y < GomokuBoard.Size; y++)
        {
            for (var x = 0; x < GomokuBoard.Size; x++)
            {
                var stone = network.Board[x, y];
                if (stone == StoneColor.None) continue;

                var center = new Vector2(Margin + x * CellSize, Margin + y * CellSize);
                var color = stone == StoneColor.Black ? new Color(20, 20, 24) : new Color(240, 240, 235);

                Primitives.FillCircle(batch, pixel, center, CellSize * 0.44f, color);

                if (network.LastMove == (x, y))
                {
                    Primitives.DrawRect(batch, pixel,
                        new Rectangle((int)center.X - 4, (int)center.Y - 4, 8, 8), Color.Red, 2);
                }
            }
        }
    }

    private void DrawPanel()
    {
        var left = Margin + BoardPixels + 40;
        var y = Margin - 10;

        font.Draw(batch, "GOMOKU", left, y, Color.White, 3);
        y += 34;

        font.Draw(batch, network.StatusText, left, y, Color.Yellow, 2);
        y += 26;

        var myColorText = network.MyColor switch
        {
            StoneColor.Black => "YOU: BLACK",
            StoneColor.White => "YOU: WHITE",
            _ => "YOU: -",
        };
        font.Draw(batch, myColorText, left, y, Color.LightGray, 2);
        y += 20;

        font.Draw(batch, $"VS {network.OpponentNick}", left, y, Color.LightGray, 2);
        y += 30;

        // HUD — 회선 상태
        font.Draw(batch, $"RTT {network.SrttMs}MS", left, y, Color.LightGreen, 2);
        y += 18;
        font.Draw(batch, $"PING {network.PingMs}MS", left, y, Color.LightGreen, 2);
        y += 30;

        font.Draw(batch, "CLICK: PLACE", left, y, Color.Gray, 1);
        y += 12;
        font.Draw(batch, "R: SURRENDER   ESC: QUIT", left, y, Color.Gray, 1);
        y += 24;

        font.Draw(batch, "LOG", left, y, Color.White, 2);
        y += 18;

        foreach (var line in network.ChatLines)
        {
            font.Draw(batch, line.Length > 30 ? line[..30] : line, left, y, Color.DarkGray, 1);
            y += 12;
        }

        if (!string.IsNullOrEmpty(network.ReconnectToken))
        {
            font.Draw(batch, "TOKEN (FOR RECONNECT):", left, Margin + BoardPixels - 30, Color.DarkGray, 1);
            font.Draw(batch, network.ReconnectToken, left, Margin + BoardPixels - 16, Color.DarkGray, 1);
        }
    }

    /// <inheritdoc/>
    protected override void UnloadContent()
    {
        network.Dispose();
        pixel.Dispose();
        batch.Dispose();
        graphics.Dispose();
    }
}
