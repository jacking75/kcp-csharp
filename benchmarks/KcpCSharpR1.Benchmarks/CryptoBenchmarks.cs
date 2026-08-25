using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using KcpCSharpR1.Transport;
using KcpCSharpR1.Transport.Fec;

namespace KcpCSharpR1.Benchmarks;

/// <summary>P5-1: 패킷 암호화 on/off 비용 비교.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class PacketCrypto
{
    private const int BatchSize = 256;

    private AesGcmPacketCipher sender = null!;
    private AesGcmPacketCipher receiver = null!;
    private byte[] key = null!;
    private byte[] plaintext = null!;
    private byte[] ciphertext = null!;
    private byte[] output = null!;
    private byte[][] prepared = null!;
    private int preparedLength;

    /// <summary>패킷 크기(바이트).</summary>
    [Params(1376)]
    public int PacketSize;

    /// <summary>준비.</summary>
    [GlobalSetup]
    public void Setup()
    {
        key = new byte[AesGcmPacketCipher.KeySize];
        new Random(3).NextBytes(key);

        sender = new AesGcmPacketCipher(key, 0x1234, serverDirection: false);

        plaintext = new byte[PacketSize];
        new Random(7).NextBytes(plaintext);

        ciphertext = new byte[PacketSize + AesGcmPacketCipher.OverheadBytes];
        output = new byte[PacketSize];

        // 복호화는 리플레이 윈도 때문에 같은 패킷을 두 번 넣을 수 없다.
        // 카운터가 서로 다른 패킷 묶음을 미리 만들어 두고, 반복마다 새 수신기로 한 묶음씩 처리한다.
        using var batchSender = new AesGcmPacketCipher(key, 0x1234, serverDirection: false);
        prepared = new byte[BatchSize][];
        for (var i = 0; i < BatchSize; i++)
        {
            prepared[i] = new byte[PacketSize + AesGcmPacketCipher.OverheadBytes];
            preparedLength = batchSender.Encrypt(plaintext, prepared[i]);
        }

        receiver = new AesGcmPacketCipher(key, 0x1234, serverDirection: true);
    }

    /// <summary>반복마다 리플레이 윈도가 비워진 새 수신기를 준비한다(측정에는 포함되지 않는다).</summary>
    [IterationSetup(Target = nameof(Decrypt))]
    public void ResetReceiver()
    {
        receiver.Dispose();
        receiver = new AesGcmPacketCipher(key, 0x1234, serverDirection: true);
    }

    /// <summary>기준선: 암호화 없이 그냥 복사만 한다.</summary>
    [Benchmark(Baseline = true)]
    public int PlainCopy()
    {
        plaintext.AsSpan().CopyTo(ciphertext);
        return plaintext.Length;
    }

    /// <summary>AES-256-GCM 암호화 1패킷.</summary>
    [Benchmark]
    public int Encrypt() => sender.Encrypt(plaintext, ciphertext);

    /// <summary>AES-256-GCM 복호화 1패킷(인증 + 리플레이 검사 포함).</summary>
    [Benchmark(OperationsPerInvoke = BatchSize)]
    [InvocationCount(1, unrollFactor: 1)]
    public int Decrypt()
    {
        var ok = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            if (receiver.TryDecrypt(prepared[i].AsSpan(0, preparedLength), output, out _))
                ok++;
        }
        return ok;
    }

    /// <summary>정리.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        sender.Dispose();
        receiver.Dispose();
    }
}

/// <summary>P5-2: 리드-솔로몬 부호화/복원 비용.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class FecCoding
{
    private ReedSolomon rs = null!;
    private byte[][] shards = null!;
    private byte[]?[] damaged = null!;

    /// <summary>샤드 크기(바이트).</summary>
    [Params(1376)]
    public int ShardSize;

    /// <summary>데이터 샤드 수.</summary>
    [Params(10)]
    public int DataShards;

    /// <summary>패리티 샤드 수.</summary>
    [Params(3)]
    public int ParityShards;

    /// <summary>준비.</summary>
    [GlobalSetup]
    public void Setup()
    {
        rs = new ReedSolomon(DataShards, ParityShards);

        var rng = new Random(11);
        shards = new byte[DataShards + ParityShards][];
        for (var i = 0; i < shards.Length; i++)
        {
            shards[i] = new byte[ShardSize];
            if (i < DataShards) rng.NextBytes(shards[i]);
        }

        rs.Encode(shards, ShardSize);

        damaged = new byte[]?[shards.Length];
    }

    /// <summary>한 그룹(10+3)의 패리티 계산.</summary>
    [Benchmark]
    public void Encode() => rs.Encode(shards, ShardSize);

    /// <summary>데이터 샤드 3개가 사라진 그룹의 복원.</summary>
    [Benchmark]
    public bool Reconstruct()
    {
        Array.Copy(shards, damaged, shards.Length);
        damaged[0] = null;
        damaged[1] = null;
        damaged[2] = null;
        return rs.Reconstruct(damaged, ShardSize);
    }
}
