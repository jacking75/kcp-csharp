using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KcpProject.Transport;

/// <summary>
/// 실제 UDP 소켓 기반 <see cref="IUdpTransport"/> 구현(P3-7).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// net8.0 에서는 <c>ReceiveFrom(Span, SocketFlags, SocketAddress)</c> 로 <see cref="SocketAddress"/> 를
/// 재사용해 수신 경로 할당을 없앤다. netstandard2.1 은 <c>ReceiveFrom(byte[], ..., ref EndPoint)</c> 로 폴백한다.
/// </description></item>
/// <item><description>
/// 송수신 버퍼를 기본 1MB 로 키운다. OS 기본값(보통 64KB)에서는 버스트 시 커널이 조용히 패킷을 버린다(진단 S-05).
/// </description></item>
/// <item><description>
/// Windows 서버에서는 <c>SIO_UDP_CONNRESET</c> 을 꺼야 한다. 끄지 않으면 상대가 사라져 ICMP
/// Port Unreachable 이 오는 순간 다음 수신 호출이 <see cref="SocketException"/> 으로 터진다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class UdpSocketTransport : IUdpTransport
{
    private const int SIO_UDP_CONNRESET = -1744830452;

    private readonly Socket socket;
    private bool disposed;

#if NET8_0_OR_GREATER
    private readonly SocketAddress receiveAddress;
#else
    private EndPoint receiveEndPoint;
    private byte[] receiveScratch = new byte[2048];
#endif

    /// <summary>소켓을 만든다.</summary>
    /// <param name="addressFamily">주소 체계(기본 IPv4).</param>
    /// <param name="receiveBufferSize">수신 버퍼 크기(바이트).</param>
    /// <param name="sendBufferSize">송신 버퍼 크기(바이트).</param>
    public UdpSocketTransport(
        AddressFamily addressFamily = AddressFamily.InterNetwork,
        int receiveBufferSize = 1 << 20,
        int sendBufferSize = 1 << 20)
    {
        socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = receiveBufferSize,
            SendBufferSize = sendBufferSize,
            Blocking = false,
        };

        // Windows: ICMP Port Unreachable 로 인한 수신 예외를 막는다.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch (SocketException)
            {
                // 일부 가상 어댑터는 지원하지 않는다. 치명적이지 않으므로 넘어간다.
            }
        }

#if NET8_0_OR_GREATER
        receiveAddress = new SocketAddress(addressFamily);
#else
        receiveEndPoint = new IPEndPoint(
            addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
#endif
    }

    /// <inheritdoc/>
    public IPEndPoint? LocalEndPoint => socket.LocalEndPoint as IPEndPoint;

    /// <summary>내부 소켓. 고급 옵션 조정용.</summary>
    public Socket Socket => socket;

    /// <inheritdoc/>
    public void Bind(IPEndPoint localEndPoint)
    {
        socket.Bind(localEndPoint);
    }

    /// <inheritdoc/>
    public int SendTo(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        if (disposed) return 0;

        try
        {
#if NET8_0_OR_GREATER
            return socket.SendTo(data, SocketFlags.None, remote);
#else
            var array = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(array);
                return socket.SendTo(array, 0, data.Length, SocketFlags.None, remote);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
            }
#endif
        }
        catch (SocketException)
        {
            // UDP 는 비신뢰다. 개별 송신 실패는 KCP 의 재전송이 흡수한다.
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    /// <inheritdoc/>
    public bool TryReceive(Span<byte> buffer, out int received, out AddressKey remote)
    {
        received = 0;
        remote = default;
        if (disposed) return false;

        try
        {
#if NET8_0_OR_GREATER
            var n = socket.ReceiveFrom(buffer, SocketFlags.None, receiveAddress);
            if (n <= 0) return false;
            received = n;
            remote = ToKey(receiveAddress);
            return true;
#else
            if (receiveScratch.Length < buffer.Length)
                receiveScratch = new byte[buffer.Length];

            var n = socket.ReceiveFrom(receiveScratch, 0, buffer.Length, SocketFlags.None, ref receiveEndPoint);
            if (n <= 0) return false;
            receiveScratch.AsSpan(0, n).CopyTo(buffer);
            received = n;
            remote = AddressKey.FromEndPoint((IPEndPoint)receiveEndPoint);
            return true;
#endif
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset ||
                                         ex.SocketErrorCode == SocketError.MessageSize)
        {
            // 상대가 사라졌거나(ICMP) 데이터그램이 버퍼보다 큰 경우. 그 패킷만 버리고 계속한다.
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public bool WaitForData(int microseconds)
    {
        if (disposed) return false;
        try
        {
            return socket.Poll(microseconds, SelectMode.SelectRead);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

#if NET8_0_OR_GREATER
    private static AddressKey ToKey(SocketAddress address)
    {
        // SocketAddress -> IPEndPoint 변환은 할당을 만든다. 버퍼에서 직접 읽어 키를 만든다.
        var span = address.Buffer.Span;
        var port = (ushort)((span[2] << 8) | span[3]);

        if (address.Family == AddressFamily.InterNetwork)
        {
            return AddressKey.FromIPv4(span.Slice(4, 4), port);
        }

        var scope = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        return AddressKey.FromIPv6(span.Slice(8, 16), scope, port);
    }
#endif

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            socket.Dispose();
        }
        catch (SocketException)
        {
            // 종료 중 예외는 무시한다.
        }
    }
}
