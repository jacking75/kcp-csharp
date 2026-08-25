using System;

namespace Gomoku.Shared;

/// <summary>돌의 색.</summary>
public enum StoneColor : byte
{
    /// <summary>빈 칸.</summary>
    None = 0,

    /// <summary>흑(선공).</summary>
    Black = 1,

    /// <summary>백(후공).</summary>
    White = 2,
}

/// <summary>돌을 놓으려 할 때의 결과.</summary>
public enum PlaceResult
{
    /// <summary>정상적으로 놓였다.</summary>
    Ok,

    /// <summary>보드 밖이다.</summary>
    OutOfBounds,

    /// <summary>이미 돌이 있다.</summary>
    Occupied,

    /// <summary>내 차례가 아니다.</summary>
    NotYourTurn,

    /// <summary>이미 끝난 판이다.</summary>
    GameOver,
}

/// <summary>
/// 오목 판과 규칙.
/// </summary>
/// <remarks>
/// <para>
/// <b>이 클래스를 서버와 클라이언트가 함께 쓴다.</b> 같은 코드로 판정하므로 "내 화면에서는 이겼는데
/// 서버는 아니라더라" 같은 어긋남이 생기지 않는다. 다만 <b>권위는 서버에 있다</b>. 클라이언트의 판정은
/// 화면을 미리 갱신하기 위한 것일 뿐, 최종 결과는 언제나 서버가 보낸 것을 따른다.
/// </para>
/// <para>
/// 규칙은 단순 오목 + <b>육목 무효</b>다. 정확히 5개가 이어져야 승리이며, 6개 이상 이어지면 승리가 아니다.
/// (렌주의 금수 규칙은 넣지 않았다 — 연습문제로 남겨 둔다.)
/// </para>
/// </remarks>
public sealed class GomokuBoard
{
    /// <summary>한 변의 칸 수.</summary>
    public const int Size = 15;

    /// <summary>보드를 직렬화했을 때의 바이트 수.</summary>
    public const int CellCount = Size * Size;

    private readonly StoneColor[] cells = new StoneColor[CellCount];

    /// <summary>지금 둘 차례인 색.</summary>
    public StoneColor Turn { get; private set; } = StoneColor.Black;

    /// <summary>승자. 아직 안 끝났으면 <see cref="StoneColor.None"/>.</summary>
    public StoneColor Winner { get; private set; }

    /// <summary>판이 끝났는지 여부(승자가 있거나 판이 가득 찼다).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>놓인 돌의 수.</summary>
    public int StoneCount { get; private set; }

    /// <summary>칸을 읽는다.</summary>
    /// <param name="x">0~14.</param>
    /// <param name="y">0~14.</param>
    /// <returns>돌의 색.</returns>
    public StoneColor this[int x, int y] => InBounds(x, y) ? cells[y * Size + x] : StoneColor.None;

    /// <summary>좌표가 판 안인지 확인한다.</summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <returns>판 안이면 true.</returns>
    public static bool InBounds(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size;

    /// <summary>판을 초기 상태로 되돌린다.</summary>
    public void Reset()
    {
        Array.Clear(cells, 0, cells.Length);
        Turn = StoneColor.Black;
        Winner = StoneColor.None;
        IsFinished = false;
        StoneCount = 0;
    }

    /// <summary>돌을 놓는다.</summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="color">놓는 색.</param>
    /// <returns>결과.</returns>
    public PlaceResult Place(int x, int y, StoneColor color)
    {
        if (IsFinished) return PlaceResult.GameOver;
        if (!InBounds(x, y)) return PlaceResult.OutOfBounds;
        if (color != Turn) return PlaceResult.NotYourTurn;
        if (cells[y * Size + x] != StoneColor.None) return PlaceResult.Occupied;

        cells[y * Size + x] = color;
        StoneCount++;

        if (IsWinningMove(x, y, color))
        {
            Winner = color;
            IsFinished = true;
        }
        else if (StoneCount >= CellCount)
        {
            IsFinished = true;   // 무승부
        }
        else
        {
            Turn = color == StoneColor.Black ? StoneColor.White : StoneColor.Black;
        }

        return PlaceResult.Ok;
    }

    /// <summary>방금 놓은 수가 승리인지 판정한다(정확히 5목, 육목은 무효).</summary>
    /// <param name="x">방금 놓은 X.</param>
    /// <param name="y">방금 놓은 Y.</param>
    /// <param name="color">색.</param>
    /// <returns>승리면 true.</returns>
    public bool IsWinningMove(int x, int y, StoneColor color)
    {
        // 가로, 세로, ↘, ↗ 네 방향
        return CountLine(x, y, 1, 0, color) == 5
            || CountLine(x, y, 0, 1, color) == 5
            || CountLine(x, y, 1, 1, color) == 5
            || CountLine(x, y, 1, -1, color) == 5;
    }

    /// <summary>지정 방향으로 이어진 같은 색 돌의 개수를 센다(양쪽 합).</summary>
    private int CountLine(int x, int y, int dx, int dy, StoneColor color)
    {
        var count = 1;

        for (var step = 1; ; step++)
        {
            var nx = x + dx * step;
            var ny = y + dy * step;
            if (this[nx, ny] != color) break;
            count++;
        }

        for (var step = 1; ; step++)
        {
            var nx = x - dx * step;
            var ny = y - dy * step;
            if (this[nx, ny] != color) break;
            count++;
        }

        return count;
    }

    /// <summary>보드를 225바이트로 직렬화한다(StateSync 용).</summary>
    /// <param name="destination">대상(<see cref="CellCount"/> 바이트 이상).</param>
    public void WriteTo(Span<byte> destination)
    {
        for (var i = 0; i < CellCount; i++)
            destination[i] = (byte)cells[i];
    }

    /// <summary>225바이트에서 보드를 복원한다.</summary>
    /// <param name="source">원본.</param>
    /// <param name="turn">복원할 차례.</param>
    /// <param name="winner">복원할 승자.</param>
    /// <param name="finished">복원할 종료 여부.</param>
    public void ReadFrom(ReadOnlySpan<byte> source, StoneColor turn, StoneColor winner, bool finished)
    {
        StoneCount = 0;
        for (var i = 0; i < CellCount; i++)
        {
            var value = source[i];
            cells[i] = value <= 2 ? (StoneColor)value : StoneColor.None;
            if (cells[i] != StoneColor.None) StoneCount++;
        }

        Turn = turn;
        Winner = winner;
        IsFinished = finished;
    }
}
