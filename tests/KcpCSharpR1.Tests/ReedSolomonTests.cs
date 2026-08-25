using System;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.Tests;

/// <summary>P5-2 ①: 리드-솔로몬 부호기 단품 검증.</summary>
public class ReedSolomonTests
{
    [Fact]
    public void 갈루아체_기본_성질()
    {
        // 항등원
        for (var i = 1; i < 256; i++)
        {
            Assert.Equal((byte)i, GaloisField.Multiply((byte)i, 1));
            Assert.Equal((byte)i, GaloisField.Divide((byte)i, 1));
        }

        // 0 곱은 0
        Assert.Equal(0, GaloisField.Multiply(0, 123));
        Assert.Equal(0, GaloisField.Multiply(123, 0));

        // 곱하고 나누면 제자리
        for (var a = 1; a < 256; a += 7)
        {
            for (var b = 1; b < 256; b += 11)
            {
                var product = GaloisField.Multiply((byte)a, (byte)b);
                Assert.Equal((byte)a, GaloisField.Divide(product, (byte)b));
            }
        }

        // 교환법칙
        Assert.Equal(GaloisField.Multiply(37, 91), GaloisField.Multiply(91, 37));

        // 0 으로 나누면 예외
        Assert.Throws<DivideByZeroException>(() => GaloisField.Divide(1, 0));
    }

    [Fact]
    public void Exp는_반복_곱셈과_같다()
    {
        for (byte a = 1; a < 20; a++)
        {
            byte expected = 1;
            for (var n = 0; n < 10; n++)
            {
                Assert.Equal(expected, GaloisField.Exp(a, n));
                expected = GaloisField.Multiply(expected, a);
            }
        }
    }

    private static byte[][] MakeShards(int dataShards, int parityShards, int shardSize, int seed)
    {
        var rng = new Random(seed);
        var shards = new byte[dataShards + parityShards][];
        for (var i = 0; i < shards.Length; i++)
        {
            shards[i] = new byte[shardSize];
            if (i < dataShards) rng.NextBytes(shards[i]);
        }
        return shards;
    }

    /// <summary>모두 도착한 것으로 표시한 present 배열을 만든다(G-4 API).</summary>
    private static bool[] AllPresent(int total)
    {
        var present = new bool[total];
        for (var i = 0; i < total; i++) present[i] = true;
        return present;
    }

    /// <summary>샤드를 "잃어버린" 상태로 만든다. 버퍼는 그대로 두고 도착 표시만 지운다.</summary>
    private static void Lose(byte[][] shards, bool[] present, int index)
    {
        present[index] = false;
        Array.Clear(shards[index], 0, shards[index].Length);   // 남은 값으로 우연히 맞는 일이 없도록 지운다.
    }

    [Theory]
    [InlineData(10, 3)]
    [InlineData(4, 2)]
    [InlineData(1, 1)]
    [InlineData(16, 8)]
    public void 패리티_개수만큼_잃어도_복원한다(int dataShards, int parityShards)
    {
        const int shardSize = 64;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 11);

        var original = new byte[dataShards][];
        for (var i = 0; i < dataShards; i++)
            original[i] = (byte[])shards[i].Clone();

        rs.Encode(shards, shardSize);

        // 데이터 샤드 중 parityShards 개를 지운다.
        var present = AllPresent(rs.TotalShards);
        for (var i = 0; i < parityShards && i < dataShards; i++)
            Lose(shards, present, i);

        Assert.True(rs.Reconstruct(shards, present, shardSize));

        for (var i = 0; i < dataShards; i++)
        {
            Assert.True(present[i]);
            Assert.Equal(original[i], shards[i]);
        }
    }

    [Fact]
    public void 데이터와_패리티가_섞여_사라져도_복원한다()
    {
        const int dataShards = 10, parityShards = 3, shardSize = 128;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 23);

        var original = new byte[dataShards][];
        for (var i = 0; i < dataShards; i++)
            original[i] = (byte[])shards[i].Clone();

        rs.Encode(shards, shardSize);

        var present = AllPresent(rs.TotalShards);
        Lose(shards, present, 0);    // 데이터
        Lose(shards, present, 7);    // 데이터
        Lose(shards, present, 11);   // 패리티

        Assert.True(rs.Reconstruct(shards, present, shardSize));
        Assert.Equal(original[0], shards[0]);
        Assert.Equal(original[7], shards[7]);
    }

    [Fact]
    public void 남은_샤드가_부족하면_복원하지_못한다()
    {
        const int dataShards = 10, parityShards = 3, shardSize = 32;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 31);
        rs.Encode(shards, shardSize);

        var present = AllPresent(rs.TotalShards);
        for (var i = 0; i < parityShards + 1; i++)
            Lose(shards, present, i);   // 패리티보다 하나 더 잃었다.

        Assert.False(rs.Reconstruct(shards, present, shardSize));
    }

    [Fact]
    public void 잃은_것이_없으면_그대로_둔다()
    {
        const int dataShards = 4, parityShards = 2, shardSize = 16;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 5);
        rs.Encode(shards, shardSize);

        var snapshot = new byte[dataShards][];
        for (var i = 0; i < dataShards; i++)
            snapshot[i] = (byte[])shards[i].Clone();

        var present = AllPresent(rs.TotalShards);
        Assert.True(rs.Reconstruct(shards, present, shardSize));

        // 손댈 것이 없으면 버퍼도 그대로여야 한다.
        for (var i = 0; i < dataShards; i++)
            Assert.Equal(snapshot[i], shards[i]);
    }

    [Fact]
    public void 같은_인스턴스로_반복_복원해도_결과가_같다()
    {
        // G-4: Reconstruct 가 스크래치를 재사용하므로, 이전 호출의 잔재가 다음 결과를 오염시키지 않는지 본다.
        const int dataShards = 10, parityShards = 3, shardSize = 96;
        var rs = new ReedSolomon(dataShards, parityShards);

        for (var round = 0; round < 20; round++)
        {
            var shards = MakeShards(dataShards, parityShards, shardSize, seed: 100 + round);
            var original = new byte[dataShards][];
            for (var i = 0; i < dataShards; i++)
                original[i] = (byte[])shards[i].Clone();

            rs.Encode(shards, shardSize);

            // 라운드마다 다른 자리를 잃어버린다(부분 행렬·역행렬이 매번 달라진다).
            var present = AllPresent(rs.TotalShards);
            Lose(shards, present, round % dataShards);
            Lose(shards, present, (round + 3) % dataShards);
            Lose(shards, present, dataShards + (round % parityShards));

            Assert.True(rs.Reconstruct(shards, present, shardSize));

            for (var i = 0; i < dataShards; i++)
                Assert.Equal(original[i], shards[i]);
        }
    }

    [Fact]
    public void 복원은_힙_할당을_남기지_않는다()
    {
        // G-4: 스크래치 재사용이 실제로 동작하는지 GC 할당량으로 확인한다.
        const int dataShards = 10, parityShards = 3, shardSize = 128;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 77);
        rs.Encode(shards, shardSize);

        var present = AllPresent(rs.TotalShards);

        // 워밍업(첫 호출에서 JIT·정적 초기화가 섞이지 않도록).
        Lose(shards, present, 0);
        Assert.True(rs.Reconstruct(shards, present, shardSize));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            present[i % dataShards] = false;
            Assert.True(rs.Reconstruct(shards, present, shardSize));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"복원 100회에서 {allocated}바이트를 할당했습니다(0 이어야 합니다).");
    }

    [Fact]
    public void 잘못된_설정은_거부한다()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(2, 0));
        Assert.Throws<ArgumentException>(() => new ReedSolomon(200, 100));
    }
}
