using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace KcpCSharpR1.ProtocolComparison;

internal static class Program
{
    private static void Main() => BenchmarkRunner.Run<ProtocolBench>();
}

[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 12)]
public class ProtocolBench
{
    private Baseline.Kcp oldSender = null!, oldReceiver = null!;
    private Candidate.Kcp newSender = null!, newReceiver = null!;
    private byte[] payload = null!, receive = null!;
    private uint oldNow, newNow;

    [Params(1024, 32768)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        payload = new byte[Length];
        receive = new byte[Length];
        oldSender = new Baseline.Kcp(1, (data, size) => oldReceiver.Input(data.AsSpan(0, size), true, true), () => oldNow);
        oldReceiver = new Baseline.Kcp(1, (data, size) => oldSender.Input(data.AsSpan(0, size), true, true), () => oldNow);
        newSender = new Candidate.Kcp(1, (data, size) => newReceiver.Input(data.AsSpan(0, size), true, true), () => newNow);
        newReceiver = new Candidate.Kcp(1, (data, size) => newSender.Input(data.AsSpan(0, size), true, true), () => newNow);
        oldSender.NoDelay(1, 10, 2, 1); oldReceiver.NoDelay(1, 10, 2, 1);
        newSender.NoDelay(1, 10, 2, 1); newReceiver.NoDelay(1, 10, 2, 1);
        oldSender.WndSize(256, 256); oldReceiver.WndSize(256, 256);
        newSender.WndSize(256, 256); newReceiver.WndSize(256, 256);
    }

    [Benchmark(Baseline = true)]
    public int Before()
    {
        if (oldSender.Send(payload) != 0) throw new InvalidOperationException();
        oldNow += 10; oldSender.Update(); oldReceiver.Update();
        var received = oldReceiver.Recv(receive);
        if (received != Length || oldSender.WaitSnd != 0) throw new InvalidOperationException();
        return received;
    }

    [Benchmark]
    public int After()
    {
        if (newSender.Send(payload) != 0) throw new InvalidOperationException();
        newNow += 10; newSender.Update(); newReceiver.Update();
        var received = newReceiver.Recv(receive);
        if (received != Length || newSender.WaitSnd != 0) throw new InvalidOperationException();
        return received;
    }
}
