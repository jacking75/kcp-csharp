using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TankBattle.Client;

/// <summary>
/// 콘텐츠 파이프라인 없이 쓰는 5×7 비트맵 폰트.
/// </summary>
/// <remarks>
/// <para>
/// MonoGame 에서 글자를 찍으려면 보통 <c>SpriteFont</c>(.spritefont → MGCB 빌드)가 필요하다.
/// 이 샘플들은 <b>외부 에셋 없이 복붙만으로 돌아가는 것</b>을 우선해, 글리프를 코드에 넣고
/// 1×1 흰 텍스처로 점을 찍어 그린다. 실무에서는 SpriteFont 를 쓰는 편이 낫다(부록 D).
/// </para>
/// <para>
/// ASCII 32(공백)~95(_)만 지원한다. 소문자는 대문자로 접어 그리고, 한글은 '?' 로 대체한다.
/// HUD 숫자와 짧은 영문 라벨을 찍는 용도다.
/// </para>
/// </remarks>
internal sealed class TinyFont
{
    /// <summary>글리프 하나의 가로 픽셀 수.</summary>
    public const int GlyphWidth = 5;

    /// <summary>글리프 하나의 세로 픽셀 수.</summary>
    public const int GlyphHeight = 7;

    private const char FirstChar = ' ';
    private const char LastChar = '_';

    /// <summary>
    /// 글리프 데이터. 문자당 5바이트이며 각 바이트가 세로 한 줄(열)이다.
    /// 비트 0이 맨 위 픽셀이다.
    /// </summary>
    private static readonly byte[] Glyphs = ParseHex(
        "000000000000005F00000007000700147F147F14242A7F2A12231308646236495522500005030000" +
        "001C2241000041221C0014083E081408083E08080050300000080808080800606000002010080402" +
        "3E5149453E00427F400042615149462141454B311814127F1027454545393C4A4949300171090503" +
        "3649494936064949291E003636000000563600000814224100141414141400412214080201510906" +
        "324979413E7E1111117E7F494949363E414141227F4141221C7F494949417F090909013E4149497A" +
        "7F0808087F00417F41002040413F017F081422417F404040407F020C027F7F0408107F3E4141413E" +
        "7F090909063E4151215E7F09192946464949493101017F01013F4040403F1F2040201F3F4038403F" +
        "631408146307087008076151494543007F41410002040810200041417F0004020102044040404040");

    private readonly Texture2D pixel;

    /// <summary>폰트를 만든다.</summary>
    /// <param name="pixel">1×1 흰색 텍스처.</param>
    public TinyFont(Texture2D pixel)
    {
        this.pixel = pixel;
    }

    /// <summary>글자 크기 배율을 적용한 문자열의 픽셀 폭을 잰다.</summary>
    /// <param name="text">문자열.</param>
    /// <param name="scale">배율.</param>
    /// <returns>픽셀 폭.</returns>
    public static int Measure(string text, int scale = 1) =>
        text.Length * (GlyphWidth + 1) * scale;

    /// <summary>문자열을 그린다.</summary>
    /// <param name="batch">스프라이트 배치(Begin 상태여야 한다).</param>
    /// <param name="text">문자열.</param>
    /// <param name="x">왼쪽 위 X.</param>
    /// <param name="y">왼쪽 위 Y.</param>
    /// <param name="color">색.</param>
    /// <param name="scale">배율.</param>
    public void Draw(SpriteBatch batch, string text, int x, int y, Color color, int scale = 1)
    {
        if (string.IsNullOrEmpty(text)) return;

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

                    batch.Draw(pixel,
                        new Rectangle(cursor + col * scale, y + row * scale, scale, scale),
                        color);
                }
            }

            cursor += (GlyphWidth + 1) * scale;
        }
    }

    private static byte[] ParseHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}

/// <summary>1×1 텍스처로 도형을 그리는 최소 유틸.</summary>
internal static class Primitives
{
    /// <summary>1×1 흰색 텍스처를 만든다.</summary>
    /// <param name="device">그래픽 디바이스.</param>
    /// <returns>텍스처.</returns>
    public static Texture2D CreatePixel(GraphicsDevice device)
    {
        var texture = new Texture2D(device, 1, 1);
        texture.SetData(new[] { Color.White });
        return texture;
    }

    /// <summary>채워진 사각형을 그린다.</summary>
    /// <param name="batch">스프라이트 배치.</param>
    /// <param name="pixel">1×1 텍스처.</param>
    /// <param name="rect">사각형.</param>
    /// <param name="color">색.</param>
    public static void FillRect(SpriteBatch batch, Texture2D pixel, Rectangle rect, Color color) =>
        batch.Draw(pixel, rect, color);

    /// <summary>테두리만 그린다.</summary>
    /// <param name="batch">스프라이트 배치.</param>
    /// <param name="pixel">1×1 텍스처.</param>
    /// <param name="rect">사각형.</param>
    /// <param name="color">색.</param>
    /// <param name="thickness">두께.</param>
    public static void DrawRect(SpriteBatch batch, Texture2D pixel, Rectangle rect, Color color, int thickness = 1)
    {
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        batch.Draw(pixel, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        batch.Draw(pixel, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    /// <summary>임의 방향의 선을 그린다.</summary>
    /// <param name="batch">스프라이트 배치.</param>
    /// <param name="pixel">1×1 텍스처.</param>
    /// <param name="from">시작점.</param>
    /// <param name="to">끝점.</param>
    /// <param name="color">색.</param>
    /// <param name="thickness">두께.</param>
    public static void DrawLine(SpriteBatch batch, Texture2D pixel, Vector2 from, Vector2 to, Color color, float thickness = 1f)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length < 0.01f) return;

        batch.Draw(pixel,
            new Rectangle((int)from.X, (int)from.Y, (int)length, (int)MathF.Max(1, thickness)),
            null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero, SpriteEffects.None, 0f);
    }

    /// <summary>채워진 원을 근사해 그린다(가로줄을 쌓는 방식).</summary>
    /// <param name="batch">스프라이트 배치.</param>
    /// <param name="pixel">1×1 텍스처.</param>
    /// <param name="center">중심.</param>
    /// <param name="radius">반지름.</param>
    /// <param name="color">색.</param>
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
}
