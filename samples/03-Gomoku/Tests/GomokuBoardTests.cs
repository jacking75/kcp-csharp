using System;
using Gomoku.Shared;

namespace Gomoku.Tests;

/// <summary>
/// P7-3 ①: 오목 판정 단위 테스트(가로/세로/대각/육목 무효).
/// </summary>
/// <remarks>
/// 이 판정 코드는 <b>서버와 클라이언트가 함께 쓴다</b>. 여기서 통과한 규칙이 양쪽에서 똑같이 돈다.
/// </remarks>
public class GomokuBoardTests
{
    /// <summary>
    /// 상대 차례를 흘려보내기 위한 더미 좌표. 가로·세로 모두 두 칸씩 떨어뜨려
    /// <b>어떤 방향으로도 이어지지 않게</b> 한다.
    /// </summary>
    /// <remarks>
    /// 처음엔 대각선으로 (14,14), (13,13) … 을 뒀다가 더미가 5목을 완성해 버려 테스트가 깨졌다.
    /// 판정 테스트에서 더미 수는 절대 승부에 관여하면 안 된다.
    /// </remarks>
    private static (int X, int Y) DummySpot(int index) => (index % 8 * 2, 14 - index / 8 * 2);

    /// <summary>번갈아 두면서 지정한 좌표에만 색을 놓는다(상대는 더미 자리에 둔다).</summary>
    private static GomokuBoard PlaceSequence(StoneColor color, params (int X, int Y)[] moves)
    {
        var board = new GomokuBoard();
        var dummyIndex = 0;

        foreach (var (x, y) in moves)
        {
            if (board.Turn != color)
            {
                var (dx, dy) = DummySpot(dummyIndex++);
                board.Place(dx, dy, board.Turn);
            }

            board.Place(x, y, color);
        }

        return board;
    }

    [Fact]
    public void 초기_상태()
    {
        var board = new GomokuBoard();

        Assert.Equal(StoneColor.Black, board.Turn);
        Assert.Equal(StoneColor.None, board.Winner);
        Assert.False(board.IsFinished);
        Assert.Equal(0, board.StoneCount);
        Assert.Equal(StoneColor.None, board[7, 7]);
    }

    [Fact]
    public void 흑이_먼저_두고_차례가_넘어간다()
    {
        var board = new GomokuBoard();

        Assert.Equal(PlaceResult.NotYourTurn, board.Place(7, 7, StoneColor.White));
        Assert.Equal(PlaceResult.Ok, board.Place(7, 7, StoneColor.Black));
        Assert.Equal(StoneColor.Black, board[7, 7]);
        Assert.Equal(StoneColor.White, board.Turn);
        Assert.Equal(1, board.StoneCount);
    }

    [Fact]
    public void 같은_자리에_두면_거부한다()
    {
        var board = new GomokuBoard();
        board.Place(7, 7, StoneColor.Black);

        Assert.Equal(PlaceResult.Occupied, board.Place(7, 7, StoneColor.White));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(15, 0)]
    [InlineData(0, 15)]
    public void 판_밖은_거부한다(int x, int y)
    {
        var board = new GomokuBoard();

        Assert.Equal(PlaceResult.OutOfBounds, board.Place(x, y, StoneColor.Black));
    }

    [Fact]
    public void 가로_5목이면_승리()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 5), (3, 5), (4, 5), (5, 5), (6, 5));

        Assert.True(board.IsFinished);
        Assert.Equal(StoneColor.Black, board.Winner);
    }

    [Fact]
    public void 세로_5목이면_승리()
    {
        var board = PlaceSequence(StoneColor.Black, (5, 2), (5, 3), (5, 4), (5, 5), (5, 6));

        Assert.True(board.IsFinished);
        Assert.Equal(StoneColor.Black, board.Winner);
    }

    [Fact]
    public void 우하향_대각_5목이면_승리()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 2), (3, 3), (4, 4), (5, 5), (6, 6));

        Assert.True(board.IsFinished);
        Assert.Equal(StoneColor.Black, board.Winner);
    }

    [Fact]
    public void 우상향_대각_5목이면_승리()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 6), (3, 5), (4, 4), (5, 3), (6, 2));

        Assert.True(board.IsFinished);
        Assert.Equal(StoneColor.Black, board.Winner);
    }

    [Fact]
    public void 백도_이길_수_있다()
    {
        var board = new GomokuBoard();

        // 흑은 이어지지 않는 더미 자리에 두고, 백이 한 줄을 만든다.
        for (var i = 0; i < 5; i++)
        {
            var (dx, dy) = DummySpot(i);
            board.Place(dx, dy, StoneColor.Black);
            board.Place(5 + i, 8, StoneColor.White);
        }

        Assert.True(board.IsFinished);
        Assert.Equal(StoneColor.White, board.Winner);
    }

    [Fact]
    public void 사목은_승리가_아니다()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 5), (3, 5), (4, 5), (5, 5));

        Assert.False(board.IsFinished);
        Assert.Equal(StoneColor.None, board.Winner);
    }

    /// <summary>육목(6개 이상)은 승리가 아니다 — 이 샘플이 채택한 규칙.</summary>
    [Fact]
    public void 육목은_승리가_아니다()
    {
        var board = new GomokuBoard();

        // 흑이 3,5,6,7,8 을 먼저 채우고(4목), 마지막에 4를 메워 6목을 만든다.
        var blackMoves = new[] { (3, 5), (5, 5), (6, 5), (7, 5), (8, 5) };
        var dummyIndex = 0;

        foreach (var (x, y) in blackMoves)
        {
            board.Place(x, y, StoneColor.Black);
            var (dx, dy) = DummySpot(dummyIndex++);
            board.Place(dx, dy, StoneColor.White);
        }

        Assert.False(board.IsFinished);   // 아직 3,5,6,7,8 — 이어진 것은 4개

        // 가운데 4를 메우면 3~8 로 6개가 이어진다. 정확히 5가 아니므로 승리가 아니다.
        Assert.Equal(PlaceResult.Ok, board.Place(4, 5, StoneColor.Black));
        Assert.False(board.IsFinished);
        Assert.Equal(StoneColor.None, board.Winner);
    }

    [Fact]
    public void 판이_끝나면_더_둘_수_없다()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 5), (3, 5), (4, 5), (5, 5), (6, 5));

        Assert.Equal(PlaceResult.GameOver, board.Place(0, 0, StoneColor.White));
    }

    [Fact]
    public void 직렬화_왕복()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 5), (3, 5), (4, 5));

        var bytes = new byte[GomokuBoard.CellCount];
        board.WriteTo(bytes);

        var restored = new GomokuBoard();
        restored.ReadFrom(bytes, board.Turn, board.Winner, board.IsFinished);

        for (var y = 0; y < GomokuBoard.Size; y++)
        {
            for (var x = 0; x < GomokuBoard.Size; x++)
                Assert.Equal(board[x, y], restored[x, y]);
        }

        Assert.Equal(board.Turn, restored.Turn);
        Assert.Equal(board.StoneCount, restored.StoneCount);
    }

    [Fact]
    public void 깨진_바이트로_복원해도_예외가_없다()
    {
        var rng = new Random(7);
        var bytes = new byte[GomokuBoard.CellCount];
        rng.NextBytes(bytes);

        var board = new GomokuBoard();
        board.ReadFrom(bytes, StoneColor.Black, StoneColor.None, false);

        // 0~2 밖의 값은 빈 칸으로 접힌다.
        for (var y = 0; y < GomokuBoard.Size; y++)
        {
            for (var x = 0; x < GomokuBoard.Size; x++)
                Assert.True(board[x, y] is StoneColor.None or StoneColor.Black or StoneColor.White);
        }
    }

    [Fact]
    public void Reset은_초기_상태로_되돌린다()
    {
        var board = PlaceSequence(StoneColor.Black, (2, 5), (3, 5), (4, 5), (5, 5), (6, 5));
        Assert.True(board.IsFinished);

        board.Reset();

        Assert.False(board.IsFinished);
        Assert.Equal(StoneColor.Black, board.Turn);
        Assert.Equal(0, board.StoneCount);
        Assert.Equal(StoneColor.None, board[2, 5]);
    }
}
