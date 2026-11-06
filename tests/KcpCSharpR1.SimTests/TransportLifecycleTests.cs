using System.Net;
using KcpCSharpR1.Transport;

namespace KcpCSharpR1.SimTests;

public class TransportLifecycleTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 27180);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 콜백에서_정지는_즉시_반환하고_콜백_반환_뒤에_정리한다(bool dispose)
    {
        using var server = new KcpServer(Remote.Port);
        var transport = new TrackingTransport();
        var callback = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MetricsSampled += _ =>
        {
            if (dispose) server.Dispose();
            else server.Stop();
            callback.TrySetResult(transport.DisposeCount);
        };
        server.Start(transport);
        Assert.Equal(0, await callback.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        server.Stop();
        Assert.Equal(1, transport.DisposeCount);
        if (!dispose)
        {
            server.Start(new TrackingTransport(), runLoop: false);
            server.Pump();
        }
    }

    [Fact]
    public async Task 외부_Stop은_오래_실행되는_콜백이_끝나기_전에_자원을_반환하지_않는다()
    {
        using var server = new KcpServer(Remote.Port);
        var transport = new TrackingTransport();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MetricsSampled += _ => { entered.TrySetResult(true); release.Wait(); };
        server.Start(transport);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = Task.Run(server.Stop);
        try
        {
            await Task.Delay(3_200);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, transport.DisposeCount);
        }
        finally
        {
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public void 수동_Pump_콜백에서_Dispose도_콜백_반환까지_정리를_미룬다()
    {
        using var server = new KcpServer(Remote.Port);
        var transport = new TrackingTransport();
        server.MetricsSampled += _ =>
        {
            server.Dispose();
            Assert.Equal(0, transport.DisposeCount);
        };
        server.Start(transport, runLoop: false);
        server.Pump();
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task 클라이언트_생성_뒤의_원본_설정_변경은_연결에_영향을_주지_않는다()
    {
        var options = new KcpSessionOptions { RawMode = true };
        using var client = new KcpClient(options);
        options.Mtu = 1;
        options.RawMode = false;
        await client.ConnectAsync(new TrackingTransport(), Remote);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void 서버_생성_뒤의_원본_설정_변경은_구동에_영향을_주지_않는다()
    {
        var options = new KcpServerOptions();
        using var server = new KcpServer(Remote.Port, options);
        options.MetricsIntervalMs = 0;
        options.Session = null!;
        var sampled = false;
        server.MetricsSampled += _ => sampled = true;
        server.Start(new TrackingTransport(), runLoop: false);
        server.Pump();
        Assert.True(sampled);
    }

    [Fact]
    public async Task 이미_취소된_RawMode_연결도_연결하지_않고_전송을_정리한다()
    {
        using var client = new KcpClient(new KcpSessionOptions { RawMode = true });
        var transport = new TrackingTransport();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConnectAsync(transport, Remote, new CancellationToken(true)));
        Assert.Equal(1, transport.DisposeCount);
        Assert.Null(client.Session);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task 핸드셰이크_시간초과는_소유한_전송을_정리한다()
    {
        uint now = 0;
        using var client = new KcpClient(clock: () => now += 10_000);
        var transport = new TrackingTransport();
        await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(transport, Remote));
        Assert.Equal(1, transport.DisposeCount);
        Assert.Null(client.Session);
        Assert.Null(client.LocalEndPoint);
    }

    [Fact]
    public async Task 연결_취소는_소유한_전송을_정리하고_재시도를_허용한다()
    {
        using var client = new KcpClient();
        using var cancellation = new CancellationTokenSource();
        var transport = new TrackingTransport { OnSend = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConnectAsync(transport, Remote, cancellation.Token));
        Assert.Equal(1, transport.DisposeCount);
        Assert.Null(client.Session);
        Assert.Null(client.LocalEndPoint);

        var retry = new TrackingTransport { OnSend = () => throw new IOException("retry") };
        await Assert.ThrowsAsync<IOException>(() => client.ConnectAsync(retry, Remote));
        Assert.Equal(1, retry.DisposeCount);
        Assert.Null(client.Session);
    }

    [Fact]
    public async Task 연결_송신_예외도_소유한_전송을_정리한다()
    {
        using var client = new KcpClient();
        var transport = new TrackingTransport { OnSend = () => throw new IOException("send") };
        await Assert.ThrowsAsync<IOException>(() => client.ConnectAsync(transport, Remote));
        Assert.Equal(1, transport.DisposeCount);
        Assert.Null(client.Session);
        Assert.Null(client.LocalEndPoint);
    }

    [Fact]
    public async Task 연결된_클라이언트의_중복_연결은_기존_세션을_보존한다()
    {
        using var client = new KcpClient(new KcpSessionOptions { RawMode = true });
        var first = new TrackingTransport();
        var session = await client.ConnectAsync(first, Remote);
        using var second = new TrackingTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(second, Remote));
        Assert.Same(session, client.Session);
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
    }

    [Fact]
    public async Task 종료된_클라이언트의_재연결은_이전_전송을_정리한다()
    {
        using var client = new KcpClient(new KcpSessionOptions { RawMode = true });
        var first = new TrackingTransport();
        await client.ConnectAsync(first, Remote);
        client.Disconnect();
        var second = new TrackingTransport();
        await client.ConnectAsync(second, Remote);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task 핸드셰이크_중에는_다른_구동기를_시작하지_못한다()
    {
        using var client = new KcpClient();
        using var cancellation = new CancellationTokenSource();
        var transport = new TrackingTransport();
        var connecting = client.ConnectAsync(transport, Remote, cancellation.Token);
        try
        {
            Assert.Throws<InvalidOperationException>(client.Update);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunAsync(new CancellationToken(true)));
            using var other = new TrackingTransport();
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(other, Remote));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        }
    }

    [Fact]
    public void 수동_구동_서버도_중복_시작을_거부하고_정지_후에는_재시작한다()
    {
        using var server = new KcpServer(Remote.Port);
        var first = new TrackingTransport();
        using var second = new TrackingTransport();
        server.Start(first, runLoop: false);
        Assert.Throws<InvalidOperationException>(() => server.Start(second, runLoop: false));
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
        server.Stop();
        Assert.Equal(1, first.DisposeCount);
        server.Start(second, runLoop: false);
        server.Pump();
    }

    [Fact]
    public void 자체_루프_서버에서는_수동_Pump를_거부한다()
    {
        using var server = new KcpServer(Remote.Port);
        server.Start(new TrackingTransport());
        Assert.Throws<InvalidOperationException>(server.Pump);
    }

    private sealed class TrackingTransport : IUdpTransport
    {
        public int DisposeCount { get; private set; }
        public Action? OnSend { get; init; }
        public IPEndPoint? LocalEndPoint => new(IPAddress.Loopback, 27181);
        public void Bind(IPEndPoint localEndPoint) { }
        public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
        {
            OnSend?.Invoke();
            return data.Length;
        }
        public bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)
        {
            received = 0;
            remote = default;
            return false;
        }
        public bool WaitForData(int microseconds)
        {
            Thread.Sleep(1);
            return false;
        }
        public void Dispose() => DisposeCount++;
    }
}
