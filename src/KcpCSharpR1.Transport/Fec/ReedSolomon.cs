using System;

namespace KcpCSharpR1.Transport.Fec;

/// <summary>
/// 리드-솔로몬 소거 부호(erasure code) 인코더/디코더(P5-2).
/// </summary>
/// <remarks>
/// <para>
/// 데이터 샤드 <c>k</c> 개에 패리티 샤드 <c>m</c> 개를 덧붙여, <b>어떤 조합이든 k 개만 남으면</b>
/// 원본을 복원한다. UDP 에서 손실된 패킷을 재전송 없이 복구할 수 있어 왕복 지연을 아낀다.
/// </para>
/// <para>
/// 부호화 행렬은 반데르몬드 행렬을 상단 정사각 부분의 역행렬로 곱해 조직 부호(systematic code)로 만든 것이다.
/// 즉 상단 k×k 가 단위행렬이라 데이터 샤드는 그대로 나가고 패리티만 계산된다.
/// klauspost/reedsolomon(kcp-go 가 쓰는 구현)과 같은 방식이다.
/// </para>
/// </remarks>
internal sealed class ReedSolomon
{
    private readonly byte[][] parityRows;   // m x k

    /// <summary>데이터 샤드 수.</summary>
    public int DataShards { get; }

    /// <summary>패리티 샤드 수.</summary>
    public int ParityShards { get; }

    /// <summary>전체 샤드 수.</summary>
    public int TotalShards => DataShards + ParityShards;

    private readonly byte[][] encodeMatrix;  // (k+m) x k

    /// <summary>부호기를 만든다.</summary>
    /// <param name="dataShards">데이터 샤드 수(1 이상).</param>
    /// <param name="parityShards">패리티 샤드 수(1 이상).</param>
    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards <= 0) throw new ArgumentOutOfRangeException(nameof(dataShards), "1 이상이어야 합니다.");
        if (parityShards <= 0) throw new ArgumentOutOfRangeException(nameof(parityShards), "1 이상이어야 합니다.");
        if (dataShards + parityShards > 256)
            throw new ArgumentException("데이터 + 패리티 샤드는 256 을 넘을 수 없습니다(GF(2^8) 제약).");

        DataShards = dataShards;
        ParityShards = parityShards;

        encodeMatrix = BuildEncodeMatrix(dataShards, parityShards);

        parityRows = new byte[parityShards][];
        for (var i = 0; i < parityShards; i++)
            parityRows[i] = encodeMatrix[dataShards + i];
    }

    /// <summary>데이터 샤드로부터 패리티 샤드를 계산한다.</summary>
    /// <param name="shards">
    /// 길이가 <see cref="TotalShards"/> 인 배열. 앞 <see cref="DataShards"/> 개는 입력,
    /// 뒤 <see cref="ParityShards"/> 개는 결과가 채워진다. 모든 샤드의 길이는 같아야 한다.
    /// </param>
    /// <param name="shardSize">각 샤드의 유효 길이.</param>
    public void Encode(byte[][] shards, int shardSize)
    {
        if (shards == null) throw new ArgumentNullException(nameof(shards));
        if (shards.Length != TotalShards)
            throw new ArgumentException($"샤드 배열 길이는 {TotalShards} 여야 합니다.", nameof(shards));

        for (var p = 0; p < ParityShards; p++)
        {
            var target = shards[DataShards + p].AsSpan(0, shardSize);
            target.Clear();

            var row = parityRows[p];
            for (var d = 0; d < DataShards; d++)
                GaloisField.MultiplyAddInto(row[d], shards[d].AsSpan(0, shardSize), target);
        }
    }

    /// <summary>빠진 데이터 샤드를 복원한다.</summary>
    /// <param name="shards">
    /// 길이가 <see cref="TotalShards"/> 인 배열. 없는 샤드는 null 로 둔다.
    /// 복원에 성공하면 빠져 있던 데이터 샤드 자리에 새 배열이 채워진다.
    /// </param>
    /// <param name="shardSize">각 샤드의 유효 길이.</param>
    /// <returns>복원했으면 true. 남은 샤드가 <see cref="DataShards"/> 개 미만이면 false.</returns>
    public bool Reconstruct(byte[]?[] shards, int shardSize)
    {
        if (shards == null) throw new ArgumentNullException(nameof(shards));
        if (shards.Length != TotalShards)
            throw new ArgumentException($"샤드 배열 길이는 {TotalShards} 여야 합니다.", nameof(shards));

        var present = 0;
        for (var i = 0; i < TotalShards; i++)
            if (shards[i] != null) present++;

        if (present < DataShards) return false;

        var missingData = false;
        for (var i = 0; i < DataShards; i++)
            if (shards[i] == null) { missingData = true; break; }

        if (!missingData) return true;   // 복원할 것이 없다.

        // 살아 있는 샤드 k 개를 골라 부분 행렬을 만든다.
        var subMatrix = new byte[DataShards][];
        var subShards = new byte[DataShards][];
        var used = 0;
        for (var i = 0; i < TotalShards && used < DataShards; i++)
        {
            if (shards[i] == null) continue;
            subMatrix[used] = (byte[])encodeMatrix[i].Clone();
            subShards[used] = shards[i]!;
            used++;
        }

        var inverted = Invert(subMatrix);
        if (inverted == null) return false;

        // 빠진 데이터 샤드 = 역행렬의 해당 행 × 살아 있는 샤드들
        for (var i = 0; i < DataShards; i++)
        {
            if (shards[i] != null) continue;

            var recovered = new byte[shardSize];
            var row = inverted[i];
            for (var c = 0; c < DataShards; c++)
                GaloisField.MultiplyAddInto(row[c], subShards[c].AsSpan(0, shardSize), recovered);

            shards[i] = recovered;
        }

        return true;
    }

    /// <summary>조직 부호용 부호화 행렬 (k+m) x k 를 만든다.</summary>
    private static byte[][] BuildEncodeMatrix(int dataShards, int parityShards)
    {
        var total = dataShards + parityShards;

        // 반데르몬드 행렬 (total x k)
        var vandermonde = new byte[total][];
        for (var r = 0; r < total; r++)
        {
            vandermonde[r] = new byte[dataShards];
            for (var c = 0; c < dataShards; c++)
                vandermonde[r][c] = GaloisField.Exp((byte)r, c);
        }

        // 상단 k x k 의 역행렬을 곱해 상단을 단위행렬로 만든다.
        var top = new byte[dataShards][];
        for (var r = 0; r < dataShards; r++)
            top[r] = (byte[])vandermonde[r].Clone();

        var topInverse = Invert(top)
            ?? throw new InvalidOperationException("반데르몬드 행렬의 역행렬을 구하지 못했습니다.");

        return Multiply(vandermonde, topInverse);
    }

    private static byte[][] Multiply(byte[][] left, byte[][] right)
    {
        var rows = left.Length;
        var cols = right[0].Length;
        var inner = right.Length;

        var result = new byte[rows][];
        for (var r = 0; r < rows; r++)
        {
            result[r] = new byte[cols];
            for (var c = 0; c < cols; c++)
            {
                byte sum = 0;
                for (var i = 0; i < inner; i++)
                    sum ^= GaloisField.Multiply(left[r][i], right[i][c]);
                result[r][c] = sum;
            }
        }
        return result;
    }

    /// <summary>가우스 소거법으로 정사각 행렬의 역행렬을 구한다. 특이행렬이면 null.</summary>
    private static byte[][]? Invert(byte[][] matrix)
    {
        var n = matrix.Length;

        // [matrix | I] 확대 행렬
        var work = new byte[n][];
        for (var r = 0; r < n; r++)
        {
            work[r] = new byte[n * 2];
            Array.Copy(matrix[r], work[r], n);
            work[r][n + r] = 1;
        }

        for (var col = 0; col < n; col++)
        {
            // 피벗 찾기
            if (work[col][col] == 0)
            {
                var swapRow = -1;
                for (var r = col + 1; r < n; r++)
                {
                    if (work[r][col] != 0) { swapRow = r; break; }
                }
                if (swapRow < 0) return null;   // 특이행렬

                (work[col], work[swapRow]) = (work[swapRow], work[col]);
            }

            // 피벗을 1 로
            var pivot = work[col][col];
            if (pivot != 1)
            {
                for (var c = 0; c < n * 2; c++)
                    work[col][c] = GaloisField.Divide(work[col][c], pivot);
            }

            // 다른 행에서 이 열을 제거
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var factor = work[r][col];
                if (factor == 0) continue;

                for (var c = 0; c < n * 2; c++)
                    work[r][c] ^= GaloisField.Multiply(factor, work[col][c]);
            }
        }

        var inverse = new byte[n][];
        for (var r = 0; r < n; r++)
        {
            inverse[r] = new byte[n];
            Array.Copy(work[r], n, inverse[r], 0, n);
        }
        return inverse;
    }
}
