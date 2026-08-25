# 부록 D. MonoGame 연동 가이드

샘플 3~5의 클라이언트가 MonoGame으로 되어 있습니다. 이 부록은 그 공통 구조를 정리합니다.

## 프로젝트 만들기

### 템플릿으로

```bash
dotnet new install MonoGame.Templates.CSharp
dotnet new mgdesktopgl -o MyGame
```

### 손으로 (이 저장소의 방식)

템플릿은 콘텐츠 파이프라인(`.mgcb`)과 에셋 폴더를 함께 만듭니다.
이 저장소의 샘플은 **에셋 없이 돌아가는 것**을 우선해 csproj를 직접 씁니다.

```xml
<!-- samples/03-Gomoku/Client/Gomoku.Client.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>WinExe</OutputType>
    <RootNamespace>Gomoku.Client</RootNamespace>
    <AssemblyName>Gomoku.Client</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <RollForward>Major</RollForward>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.5.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Shared\Gomoku.Shared.csproj" />
    <ProjectReference Include="..\..\..\src\KcpProject\KcpProject.csproj" />
    <ProjectReference Include="..\..\..\src\KcpProject.Transport\KcpProject.Transport.csproj" />
  </ItemGroup>

</Project>
```

| 설정 | 이유 |
|---|---|
| `OutputType: WinExe` | 콘솔 창이 함께 뜨지 않게 |
| `ApplicationManifest` | 고해상도 화면에서 흐릿해지지 않게(DPI 인식) |
| `RollForward: Major` | 상위 런타임에서도 실행 |

DPI 매니페스트:

```xml
<!-- samples/03-Gomoku/Client/app.manifest -->
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <!-- 고해상도 화면에서 흐릿하게 늘어나지 않도록 DPI 인식을 켠다. -->
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">permonitorv2,permonitor</dpiAwareness>
  </windowsSettings>
</application>
```

## KCP를 게임 루프에 붙이기

**핵심은 한 줄입니다.**

```csharp
// samples/03-Gomoku/Client/GomokuGame.cs
protected override void Update(GameTime gameTime)
{
    // ★ 이 한 줄이 MonoGame 과 KCP 를 잇는 전부다.
    network.Poll();
    ...
}
```

`network.Poll()` 안에서 `KcpClient.Update()` 를 부릅니다.

```csharp
// samples/03-Gomoku/Client/NetworkClient.cs
/// <summary>매 프레임 호출한다.</summary>
public void Poll() => client.Update();
```

### 왜 이것으로 충분한가

`Update()` 안에서 수신 콜백이 발화하므로 **콜백이 게임 스레드에서 실행**됩니다.
따라서 렌더링 상태를 lock 없이 직접 고쳐도 안전합니다.

```csharp
// samples/03-Gomoku/Client/NetworkClient.cs
/// <para>
/// <b>MonoGame 과 엮는 방법의 핵심</b>: <see cref="Poll"/> 을 <c>Game.Update()</c> 에서 매 프레임 부른다.
/// 그러면 수신 콜백도 게임 스레드에서 발화하므로, 렌더링 상태를 lock 없이 그대로 고쳐도 안전하다.
/// (서버는 반대로 전용 수신 스레드를 쓴다 — 책 6장에서 두 모델을 비교한다.)
/// </para>
```

### 연결은 비동기로

`ConnectAsync` 는 비동기입니다. `Initialize()` 에서 기다리지 말고 던져 두세요.

```csharp
// samples/03-Gomoku/Client/GomokuGame.cs
protected override void Initialize()
{
    Window.Title = "kcp-csharp 샘플 3 - Gomoku";
    _ = network.ConnectAsync(host, reconnectToken);   // await 하지 않는다
    base.Initialize();
}
```

연결이 끝날 때까지 화면에는 "CONNECTING…"을 그립니다.
`Initialize()` 에서 `.Wait()` 하면 **창이 뜨기 전에 멈춥니다.**

## 고정 틱과 `IsFixedTimeStep`

```csharp
// samples/03-Gomoku/Client/GomokuGame.cs
// 60Hz 고정 틱. 네트워크 폴링 주기도 이 값을 따라간다.
IsFixedTimeStep = true;
TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 60.0);
```

| 설정 | 효과 |
|---|---|
| `IsFixedTimeStep = true` | `Update()` 가 초당 60번 고정 호출 |
| `false` | 프레임레이트만큼 호출(가변) |

**네트워크 폴링에는 고정이 편합니다.** 입력 전송 주기를 프레임과 무관하게 잡기 쉽고,
고사양 PC에서 초당 500번씩 폴링하는 낭비도 없습니다.

단, `Update()` 가 무거워 60Hz를 못 지키면 MonoGame이 `Draw()` 를 건너뛰며 따라잡으려 합니다.
`gameTime.IsRunningSlowly` 로 감지할 수 있습니다.

### 서버의 틱과는 다릅니다

| | 클라이언트 | 서버 |
|---|---|---|
| 틱레이트 | 60Hz(렌더링) | 20Hz(시뮬레이션) |
| 하는 일 | 폴링·입력·보간·그리기 | 입력 적용·물리·스냅숏 |

서로 다른 속도로 도는 것이 정상입니다. 그 사이를 메우는 것이 보간입니다(12장).

## 콘텐츠 파이프라인 없이 그리기

이 저장소의 샘플은 `.mgcb` 를 쓰지 않습니다. **이유는 자기완결성**입니다 —
`git clone` 하고 `dotnet run` 하면 바로 돌아야 합니다.

### 1×1 흰 텍스처

모든 도형의 재료입니다.

```csharp
// samples/03-Gomoku/Client/TinyFont.cs
public static Texture2D CreatePixel(GraphicsDevice device)
{
    var texture = new Texture2D(device, 1, 1);
    texture.SetData(new[] { Color.White });
    return texture;
}
```

이것 하나로 사각형·선·원을 다 그립니다.

```csharp
// samples/03-Gomoku/Client/TinyFont.cs
public static void DrawLine(SpriteBatch batch, Texture2D pixel, Vector2 from, Vector2 to, Color color, float thickness = 1f)
{
    var delta = to - from;
    var length = delta.Length();
    if (length < 0.01f) return;

    batch.Draw(pixel,
        new Rectangle((int)from.X, (int)from.Y, (int)length, (int)MathF.Max(1, thickness)),
        null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero, SpriteEffects.None, 0f);
}
```

선은 **1×1 텍스처를 늘리고 회전**시켜 그립니다.

원은 가로줄을 쌓습니다.

```csharp
public static void FillCircle(SpriteBatch batch, Texture2D pixel, Vector2 center, float radius, Color color)
{
    var r = (int)MathF.Ceiling(radius);
    for (var dy = -r; dy <= r; dy++)
    {
        var half = (int)MathF.Sqrt(MathF.Max(0, radius * radius - dy * dy));
        if (half <= 0) continue;

        batch.Draw(pixel,
            new Rectangle((int)center.X - half, (int)center.Y + dy, half * 2, 1),
            color);
    }
}
```

### 글자 — 5×7 비트맵 폰트

`SpriteFont` 는 `.spritefont` 파일과 MGCB 빌드가 필요합니다.
샘플은 **글리프를 코드에 넣어** 그것을 피합니다.

```csharp
// samples/03-Gomoku/Client/TinyFont.cs
/// <summary>
/// 글리프 데이터. 문자당 5바이트이며 각 바이트가 세로 한 줄(열)이다.
/// 비트 0이 맨 위 픽셀이다.
/// </summary>
private static readonly byte[] Glyphs = ParseHex(
    "000000000000005F00000007000700147F147F14242A7F2A12231308646236495522500005030000" +
    ...);
```

ASCII 32~95(공백~`_`)만 지원합니다. 소문자는 대문자로 접고, **한글은 `?` 로 대체**합니다.
HUD 숫자와 짧은 영문 라벨용입니다.

```csharp
public void Draw(SpriteBatch batch, string text, int x, int y, Color color, int scale = 1)
{
    var cursor = x;
    foreach (var raw in text)
    {
        var ch = char.ToUpperInvariant(raw);
        if (ch < FirstChar || ch > LastChar) ch = '?';

        var offset = (ch - FirstChar) * GlyphWidth;
        for (var col = 0; col < GlyphWidth; col++)
        {
            var bits = Glyphs[offset + col];
            for (var row = 0; row < GlyphHeight; row++)
            {
                if ((bits & (1 << row)) == 0) continue;
                batch.Draw(pixel, new Rectangle(cursor + col * scale, y + row * scale, scale, scale), color);
            }
        }
        cursor += (GlyphWidth + 1) * scale;
    }
}
```

> **실무에서는 `SpriteFont` 를 쓰세요.** 한글이 필요하고, 성능도 훨씬 낫습니다.
> 이 방식은 "에셋 없이 돌아가는 샘플"을 위한 타협입니다.

### SpriteFont 를 쓰려면

```bash
dotnet tool install -g dotnet-mgcb-editor
mgcb-editor
```

`Content.mgcb` 에 `.spritefont` 를 추가하고 csproj에 넣습니다.

```xml
<ItemGroup>
  <MonoGameContentReference Include="Content\Content.mgcb" />
</ItemGroup>
```

한글을 쓰려면 `.spritefont` 의 `CharacterRegions` 에 한글 범위를 추가해야 합니다.

```xml
<CharacterRegions>
  <CharacterRegion>
    <Start>&#32;</Start>
    <End>&#126;</End>
  </CharacterRegion>
  <CharacterRegion>
    <Start>&#44032;</Start>   <!-- 가 -->
    <End>&#55203;</End>       <!-- 힣 -->
  </CharacterRegion>
</CharacterRegions>
```

> 한글 전체(11,172자)를 넣으면 텍스처 아틀라스가 매우 커집니다.
> 실제로 쓰는 글자만 넣거나, 동적 폰트 라이브러리를 쓰세요.

## 샘플 3~5의 공통 구조

```
Client/
  Program.cs          진입점. [STAThread] + game.Run()
  XxxGame.cs          Game 상속. Update(폴링·입력) / Draw(렌더)
  NetworkClient.cs    (샘플 3) 네트워크 상태를 게임이 읽기 좋은 형태로
  TinyFont.cs         폰트 + 도형 유틸
  app.manifest        DPI 인식
```

### 진입점

```csharp
// samples/03-Gomoku/Client/Program.cs
[STAThread]
private static void Main(string[] args)
{
    var host = args.Length > 0 ? args[0] : "127.0.0.1";
    var nick = args.Length > 1 ? args[1] : $"player{Environment.ProcessId % 1000}";
    var token = args.Length > 2 ? args[2] : null;

    using var game = new GomokuGame(host, nick, token);
    game.Run();
}
```

`[STAThread]` 는 Windows에서 일부 대화상자·클립보드 기능에 필요합니다.

### 정리

```csharp
// samples/03-Gomoku/Client/GomokuGame.cs
protected override void UnloadContent()
{
    network.Dispose();
    pixel.Dispose();
    batch.Dispose();
    graphics.Dispose();
}
```

`network.Dispose()` 안에서 FIN을 보내고 한 번 더 펌프합니다.

```csharp
// samples/03-Gomoku/Client/NetworkClient.cs
public void Dispose()
{
    client.Disconnect();
    client.Update();     // FIN 이 실제로 나가도록
    client.Dispose();
}
```

**`Update()` 를 한 번 더 부르는 것**을 잊지 마세요. `Disconnect()` 는 큐에 넣기만 합니다.

## 입력 처리 패턴

MonoGame의 `Keyboard.GetState()` 는 **현재 상태**만 줍니다.
"방금 눌렸다"를 알려면 이전 상태와 비교해야 합니다.

```csharp
// samples/04-MoveSync/Client/MoveSyncGame.cs
var keyboard = Keyboard.GetState();

if (keyboard.IsKeyDown(Keys.F1) && !previousKeyboard.IsKeyDown(Keys.F1))
    interpolate = !interpolate;      // 눌린 순간에만 토글

previousKeyboard = keyboard;
```

마우스도 같습니다.

```csharp
// samples/03-Gomoku/Client/GomokuGame.cs
if (mouse.LeftButton == ButtonState.Pressed && previousMouse.LeftButton == ButtonState.Released)
{
    if (hover.X >= 0) network.RequestPlace(hover.X, hover.Y);
}
previousMouse = mouse;
```

### 입력 전송 주기는 프레임과 분리

60fps에서 매 프레임 입력을 보내면 60Hz입니다 — 대개 과합니다.

```csharp
// samples/04-MoveSync/Client/MoveSyncGame.cs
var nowMs = clock.Elapsed.TotalMilliseconds;

// 방향이 바뀌면 즉시, 아니면 정해진 주기로 보낸다.
var changed = direction != lastSentDirection;
if (!changed && nowMs < nextInputAtMs) return;

nextInputAtMs = nowMs + 1000.0 / ProtocolLimits.InputRate;
```

**"바뀌면 즉시, 아니면 주기적으로"** 가 좋은 절충입니다.
반응성은 유지하면서 대역폭을 아낍니다.

## 실행 확인

```bash
dotnet run --project samples/03-Gomoku/Server
dotnet run --project samples/03-Gomoku/Client -- 127.0.0.1 alice
dotnet run --project samples/03-Gomoku/Client -- 127.0.0.1 bob
```

창 두 개가 뜨고 서버 로그에 매칭이 찍히면 성공입니다.

```
[12:12:04] [방 1] 시작 — 흑 alice vs 백 bob
```

## 흔한 문제

| 증상 | 원인 |
|---|---|
| 창이 안 뜨고 바로 종료 | `Initialize()` 에서 `.Wait()` 하고 있지 않은지 |
| 글자가 `?` 로 나옴 | TinyFont는 ASCII만 지원(한글은 SpriteFont 필요) |
| 화면이 흐릿함 | `app.manifest` 의 DPI 설정 확인 |
| 네트워크가 안 됨 | `Update()`/`Poll()` 을 부르고 있는지 |
| 종료 시 상대가 타임아웃으로 알게 됨 | `Disconnect()` 후 `Update()` 를 한 번 더 |
