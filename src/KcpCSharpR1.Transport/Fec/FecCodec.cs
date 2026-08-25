using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace KcpCSharpR1.Transport.Fec;

/// <summary>FEC 설정(P5-2).</summary>
public sealed class FecOptions
{
    /// <summary>한 그룹의 데이터 샤드 수. 기본 10.</summary>
    public int DataShards { get; set; } = 10;

    /// <summary>한 그룹의 패리티 샤드 수. 기본 3(≈ 23% 손실까지 재전송 없이 복구).</summary>
    public int ParityShards { get; set; } = 3;

    /// <summary>값이 유효한지 검사한다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">범위를 벗어난 값이 있는 경우.</exception>
    public void Validate()
    {
        if (DataShards <= 0 || DataShards > 128)
            throw new ArgumentOutOfRangeException(nameof(DataShards), DataShards, "1~128 이어야 합니다.");
        if (ParityShards <= 0 || ParityShards > 128)
            throw new ArgumentOutOfRangeException(nameof(ParityShards), ParityShards, "1~128 이어야 합니다.");
    }
}

/// <summary>
/// kcp-go 호환 FEC 프레이밍 상수.
/// </summary>
/// <remarks>
/// 헤더: <c>[seqid u32][flag u16]</c> (6바이트). 데이터 샤드는 그 뒤에 <c>[size u16]</c> 를 더 붙이고
/// 실제 페이로드가 온다. <c>size</c> 는 <b>size 필드 자신을 포함한</b> 길이다(kcp-go 와 동일).
/// 패리티 샤드는 <c>[size][payload]</c> 구간을 0 으로 채워 길이를 맞춘 뒤 계산한다.
/// </remarks>
public static class FecFrame
{
    /// <summary>FEC 헤더 크기(seqid 4 + flag 2).</summary>
    public const int HeaderSize = 6;

    /// <summary>데이터 샤드의 헤더 크기(FEC 헤더 + size 2).</summary>
    public const int DataHeaderSize = HeaderSize + 2;

    /// <summary>데이터 샤드 플래그.</summary>
    public const ushort TypeData = 0xF1;

    /// <summary>패리티 샤드 플래그.</summary>
    public const ushort TypeParity = 0xF2;
}

/// <summary>FEC 인코더/디코더가 패킷을 내보낼 때 쓰는 콜백.</summary>
/// <param name="packet">내보낼 패킷.</param>
public delegate void FecOutput(ReadOnlySpan<byte> packet);

/// <summary>
/// FEC 인코더. 데이터 샤드에 헤더를 채우고, 그룹이 차면 패리티 샤드를 만들어 낸다.
/// </summary>
internal sealed class FecEncoder
{
    private readonly ReedSolomon rs;
    private readonly int maxShardSize;
    private readonly byte[][] shards;      // (data + parity) x maxShardSize
    private readonly byte[] parityFrame;   // [flag 바이트 1][fec 헤더][패리티]
    private readonly int frameTypeOffset;

    private uint nextSeq;
    private int filled;
    private int groupMaxSize;

    /// <summary>인코더를 만든다.</summary>
    /// <param name="options">FEC 설정.</param>
    /// <param name="maxShardSize">샤드 하나의 최대 크기(size 필드 포함).</param>
    /// <param name="frameTypeOffset">패킷 앞에 붙는 프레임 타입 바이트 수(보통 1).</param>
    public FecEncoder(FecOptions options, int maxShardSize, int frameTypeOffset)
    {
        rs = new ReedSolomon(options.DataShards, options.ParityShards);
        this.maxShardSize = maxShardSize;
        this.frameTypeOffset = frameTypeOffset;

        shards = new byte[rs.TotalShards][];
        for (var i = 0; i < shards.Length; i++)
            shards[i] = new byte[maxShardSize];

        parityFrame = new byte[frameTypeOffset + FecFrame.HeaderSize + maxShardSize];
    }

    /// <summary>데이터 샤드 수.</summary>
    public int DataShards => rs.DataShards;

    /// <summary>패리티 샤드 수.</summary>
    public int ParityShards => rs.ParityShards;

    /// <summary>
    /// 데이터 패킷에 FEC 헤더를 채우고 그룹에 넣는다. 그룹이 차면 패리티 패킷을 <paramref name="output"/> 으로 낸다.
    /// </summary>
    /// <param name="frame">
    /// 전체 프레임. <c>[프레임타입][seqid][flag][size][KCP 패킷...]</c> 구조여야 하며,
    /// 앞의 <c>frameTypeOffset + 8</c> 바이트는 이 메서드가 채운다.
    /// </param>
    /// <param name="output">패킷 출력 콜백. 데이터 패킷을 먼저 내보내고, 그룹이 찼으면 이어서 패리티를 낸다.</param>
    public void Encode(Span<byte> frame, FecOutput output)
    {
        var body = frame.Slice(frameTypeOffset);              // [seqid][flag][size][payload]
        var dataRegion = body.Slice(FecFrame.HeaderSize);     // [size][payload]

        BinaryPrimitives.WriteUInt32LittleEndian(body, nextSeq);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(4), FecFrame.TypeData);
        BinaryPrimitives.WriteUInt16LittleEndian(dataRegion, (ushort)dataRegion.Length);

        // 그룹 캐시에 복사하고 나머지는 0 으로 채운다(패리티 계산 길이를 맞추기 위해).
        var slot = shards[filled];
        dataRegion.CopyTo(slot);
        if (dataRegion.Length < maxShardSize)
            slot.AsSpan(dataRegion.Length).Clear();

        if (dataRegion.Length > groupMaxSize)
            groupMaxSize = dataRegion.Length;

        nextSeq++;
        filled++;

        // 데이터 패킷을 먼저 내보낸다(패리티는 그룹이 찼을 때 뒤따른다).
        output(frame);

        if (filled < rs.DataShards)
            return;

        // 그룹 완성 → 패리티 계산
        rs.Encode(shards, groupMaxSize);

        parityFrame[0] = frame[0];   // 같은 프레임 타입으로 내보낸다.
        for (var p = 0; p < rs.ParityShards; p++)
        {
            var target = parityFrame.AsSpan(frameTypeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(target, nextSeq);
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(4), FecFrame.TypeParity);
            shards[rs.DataShards + p].AsSpan(0, groupMaxSize).CopyTo(target.Slice(FecFrame.HeaderSize));

            nextSeq++;
            output(parityFrame.AsSpan(0, frameTypeOffset + FecFrame.HeaderSize + groupMaxSize));
        }

        filled = 0;
        groupMaxSize = 0;
    }
}

/// <summary>
/// FEC 디코더. 도착한 샤드를 그룹별로 모아, 데이터 샤드가 빠졌으면 복원한다.
/// </summary>
/// <remarks>
/// <para>
/// 진단 G-4: 그룹의 샤드 버퍼를 <b>생성 시 전부 잡아 두고 재활용</b>한다. 도착 여부는 null 이 아니라
/// <c>Present</c> 배열로 따로 관리하므로, 샤드가 도착할 때마다 <c>new byte[]</c> 를 만들 필요가 없다.
/// </para>
/// <para>
/// 고정 메모리는 세션당 <c>MaxGroups × TotalShards × maxShardSize</c> 다
/// (기본 10+3 구성, MTU 1400 기준 약 285KB). 부담스러우면 <see cref="MaxGroups"/> 를 줄인다.
/// </para>
/// </remarks>
internal sealed class FecDecoder
{
    private const int MaxGroups = 16;

    private sealed class Group
    {
        public long Index = -1;
        public byte[][] Shards = Array.Empty<byte[]>();
        public bool[] Present = Array.Empty<bool>();
        public int Received;
        public bool Recovered;
        public int MaxSize;
    }

    private readonly ReedSolomon rs;
    private readonly int maxShardSize;
    private readonly List<Group> groups = new();

    // G-4: 복원 직전의 도착 여부 사본. 복원 후 무엇이 새로 채워졌는지 비교하는 데 쓴다.
    private readonly bool[] presentScratch;

    private long newestGroup = -1;

    /// <summary>디코더를 만든다.</summary>
    /// <param name="options">FEC 설정(인코더와 같아야 한다).</param>
    /// <param name="maxShardSize">샤드 하나의 최대 크기.</param>
    public FecDecoder(FecOptions options, int maxShardSize)
    {
        rs = new ReedSolomon(options.DataShards, options.ParityShards);
        this.maxShardSize = maxShardSize;
        presentScratch = new bool[rs.TotalShards];
    }

    /// <summary>복원한 데이터 샤드 수(통계).</summary>
    public long RecoveredShards { get; private set; }

    /// <summary>
    /// 샤드 하나를 처리한다.
    /// </summary>
    /// <param name="body">프레임 타입 바이트를 제외한 <c>[seqid][flag][...]</c> 구간.</param>
    /// <param name="payload">데이터 샤드였다면 원본 페이로드(KCP 패킷).</param>
    /// <param name="onRecovered">복원된 데이터 페이로드를 받을 콜백.</param>
    /// <returns>데이터 샤드라 <paramref name="payload"/> 가 유효하면 true.</returns>
    public bool Decode(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> payload, FecOutput onRecovered)
    {
        payload = default;
        if (body.Length < FecFrame.HeaderSize) return false;

        var seq = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var flag = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4));
        var shardData = body.Slice(FecFrame.HeaderSize);
        if (shardData.Length == 0 || shardData.Length > maxShardSize) return false;

        var isData = flag == FecFrame.TypeData;
        if (!isData && flag != FecFrame.TypeParity) return false;

        var groupIndex = seq / (uint)rs.TotalShards;
        var position = (int)(seq % (uint)rs.TotalShards);

        // 데이터 샤드 위치에 패리티가 오거나 그 반대면 규격 위반이다.
        if (isData != position < rs.DataShards) return false;

        if (isData)
        {
            // [size u16][payload...]
            var size = BinaryPrimitives.ReadUInt16LittleEndian(shardData);
            if (size < 2 || size > shardData.Length) return false;
            payload = shardData.Slice(2, size - 2);
        }

        var group = GetGroup(groupIndex);
        if (group == null) return isData;   // 너무 오래된 그룹이면 복원은 포기하고 데이터만 넘긴다.

        if (!group.Present[position])
        {
            // G-4: 미리 잡아 둔 슬롯에 복사한다(패킷당 할당 0).
            // 뒷부분은 반드시 0 으로 비운다 — 인코더가 그렇게 패딩해 패리티를 계산했기 때문이다.
            var slot = group.Shards[position];
            shardData.CopyTo(slot);
            if (shardData.Length < maxShardSize)
                slot.AsSpan(shardData.Length).Clear();

            group.Present[position] = true;
            group.Received++;
            if (shardData.Length > group.MaxSize) group.MaxSize = shardData.Length;
        }

        TryRecover(group, onRecovered);
        return isData;
    }

    private void TryRecover(Group group, FecOutput onRecovered)
    {
        if (group.Recovered || group.Received < rs.DataShards)
            return;

        var missing = false;
        for (var i = 0; i < rs.DataShards; i++)
        {
            if (!group.Present[i]) { missing = true; break; }
        }

        if (!missing)
        {
            group.Recovered = true;   // 전부 도착했으니 더 볼 것이 없다.
            return;
        }

        // G-4: 도착 여부 사본만 뜬다(스크래치 재사용). 복원 결과는 그룹 슬롯에 제자리로 채워진다.
        Array.Copy(group.Present, presentScratch, rs.TotalShards);

        if (!rs.Reconstruct(group.Shards, presentScratch, group.MaxSize))
            return;

        group.Recovered = true;

        for (var i = 0; i < rs.DataShards; i++)
        {
            if (group.Present[i]) continue;    // 원래 도착한 샤드는 이미 위로 올라갔다.
            if (!presentScratch[i]) continue;  // 복원되지 않았다.

            var shard = group.Shards[i];
            var size = BinaryPrimitives.ReadUInt16LittleEndian(shard);
            if (size < 2 || size > group.MaxSize) continue;

            RecoveredShards++;
            onRecovered(shard.AsSpan(2, size - 2));
        }
    }

    private Group? GetGroup(long index)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Index == index) return groups[i];
        }

        // 이미 지나간 그룹은 되살리지 않는다(늦게 온 샤드로 중복 복원하는 것을 막는다).
        if (index < newestGroup - MaxGroups) return null;

        if (index > newestGroup) newestGroup = index;

        Group group;
        if (groups.Count >= MaxGroups)
        {
            // 가장 오래된 그룹을 재활용한다(샤드 버퍼까지 그대로 재사용한다).
            var oldest = 0;
            for (var i = 1; i < groups.Count; i++)
                if (groups[i].Index < groups[oldest].Index) oldest = i;
            group = groups[oldest];
        }
        else
        {
            // G-4: 샤드 버퍼를 여기서 전부 만들어 두고 이후로는 재사용만 한다.
            var shards = new byte[rs.TotalShards][];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = new byte[maxShardSize];

            group = new Group
            {
                Shards = shards,
                Present = new bool[rs.TotalShards],
            };
            groups.Add(group);
        }

        group.Index = index;
        Array.Clear(group.Present, 0, group.Present.Length);
        group.Received = 0;
        group.Recovered = false;
        group.MaxSize = 0;
        return group;
    }
}
