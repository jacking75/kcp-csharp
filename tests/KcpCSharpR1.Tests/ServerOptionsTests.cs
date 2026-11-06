using KcpCSharpR1.Transport;

namespace KcpCSharpR1.Tests;

public class ServerOptionsTests
{
    [Theory]
    [InlineData(nameof(KcpServerOptions.MaxSessions), 0)]
    [InlineData(nameof(KcpServerOptions.MaxSessions), -1)]
    [InlineData(nameof(KcpServerOptions.PollMicroseconds), 0)]
    [InlineData(nameof(KcpServerOptions.PollMicroseconds), -1)]
    [InlineData(nameof(KcpServerOptions.MaxReceiveBatch), 0)]
    [InlineData(nameof(KcpServerOptions.MaxReceiveBatch), -1)]
    [InlineData(nameof(KcpServerOptions.SocketReceiveBufferSize), 0)]
    [InlineData(nameof(KcpServerOptions.SocketReceiveBufferSize), -1)]
    [InlineData(nameof(KcpServerOptions.SocketSendBufferSize), 0)]
    [InlineData(nameof(KcpServerOptions.SocketSendBufferSize), -1)]
    public void InvalidServerOptionsFailBeforeOpeningSocket(string property, int value)
    {
        var options = new KcpServerOptions();
        typeof(KcpServerOptions).GetProperty(property)!.SetValue(options, value);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var server = new KcpServer(27185, options);
        });

        Assert.Equal(property, error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveMetricsIntervalStillDisablesSampling(int interval)
    {
        using var server = new KcpServer(27185, new KcpServerOptions { MetricsIntervalMs = interval });
        Assert.Null(server.LocalEndPoint);
    }
}
