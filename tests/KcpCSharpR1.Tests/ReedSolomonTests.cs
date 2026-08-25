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
        var damaged = new byte[]?[rs.TotalShards];
        Array.Copy(shards, damaged, rs.TotalShards);
        for (var i = 0; i < parityShards && i < dataShards; i++)
            damaged[i] = null;

        Assert.True(rs.Reconstruct(damaged, shardSize));

        for (var i = 0; i < dataShards; i++)
            Assert.Equal(original[i], damaged[i]);
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

        var damaged = new byte[]?[rs.TotalShards];
        Array.Copy(shards, damaged, rs.TotalShards);
        damaged[0] = null;    // 데이터
        damaged[7] = null;    // 데이터
        damaged[11] = null;   // 패리티

        Assert.True(rs.Reconstruct(damaged, shardSize));
        Assert.Equal(original[0], damaged[0]);
        Assert.Equal(original[7], damaged[7]);
    }

    [Fact]
    public void 남은_샤드가_부족하면_복원하지_못한다()
    {
        const int dataShards = 10, parityShards = 3, shardSize = 32;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 31);
        rs.Encode(shards, shardSize);

        var damaged = new byte[]?[rs.TotalShards];
        Array.Copy(shards, damaged, rs.TotalShards);
        for (var i = 0; i < parityShards + 1; i++)
            damaged[i] = null;   // 패리티보다 하나 더 잃었다.

        Assert.False(rs.Reconstruct(damaged, shardSize));
    }

    [Fact]
    public void 잃은_것이_없으면_그대로_둔다()
    {
        const int dataShards = 4, parityShards = 2, shardSize = 16;
        var rs = new ReedSolomon(dataShards, parityShards);
        var shards = MakeShards(dataShards, parityShards, shardSize, seed: 5);
        rs.Encode(shards, shardSize);

        var intact = new byte[]?[rs.TotalShards];
        Array.Copy(shards, intact, rs.TotalShards);

        Assert.True(rs.Reconstruct(intact, shardSize));
        for (var i = 0; i < dataShards; i++)
            Assert.Same(shards[i], intact[i]);
    }

    [Fact]
    public void 잘못된_설정은_거부한다()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(2, 0));
        Assert.Throws<ArgumentException>(() => new ReedSolomon(200, 100));
    }
}
