using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KcpCSharpR1.Transport;

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
/// net8.0 송신 경로도 <see cref="SocketAddress"/> 를 재사용한다. <c>SendTo(..., EndPoint)</c> 는 호출마다
/// <c>EndPoint.Serialize()</c> 로 <see cref="SocketAddress"/> 를 새로 만들기 때문이다(진단 G-1).
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
    private readonly AddressFamily family;
    private bool disposed;

#if NET8_0_OR_GREATER
    private readonly SocketAddress receiveAddress;

    /// <summary>
    /// 세션 상대 주소의 <see cref="SocketAddress"/> 캐시(진단 G-1).
    /// </summary>
    /// <remarks>
    /// 세션 하나는 같은 <see cref="IPEndPoint"/> 인스턴스를 계속 쓰므로 직렬화가 세션당 1회로 줄어든다.
    /// 약한 참조 테이블이라 세션이 사라지면 항목도 함께 사라져 별도의 수명 관리가 필요 없다.
    /// </remarks>
    private readonly ConditionalWeakTable<IPEndPoint, SocketAddress> sendAddressCache = new ConditionalWeakTable<IPEndPoint, SocketAddress>();

    /// <summary>주소 키로 보낼 때 재사용하는 버퍼(핸드셰이크 응답 등 일회성 송신용).</summary>
    private readonly SocketAddress keySendAddress;
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
        family = addressFamily;
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
        keySendAddress = new SocketAddress(addressFamily);
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
        if (remote == null) throw new ArgumentNullException(nameof(remote));

        try
        {
#if NET8_0_OR_GREATER
            // Socket.SendTo(..., EndPoint) 는 내부에서 remote.Serialize() 를 호출해 매번 SocketAddress 를 만든다.
            // 세션의 상대 주소는 불변이므로 인스턴스를 키로 캐시해 직렬화를 세션당 1회로 줄인다(진단 G-1).
            var address = sendAddressCache.GetValue(remote, static ep => ep.Serialize());
            return socket.SendTo(data, SocketFlags.None, address);
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
    public int SendTo(ReadOnlySpan<byte> data, AddressKey remote)
    {
        if (disposed) return 0;

#if NET8_0_OR_GREATER
        // 재사용 SocketAddress 에 직접 써서 IPEndPoint·IPAddress 할당을 모두 없앤다(진단 G-1).
        if (!TryWriteKey(remote, keySendAddress))
            return SendTo(data, remote.ToEndPoint());

        try
        {
            return socket.SendTo(data, SocketFlags.None, keySendAddress);
        }
        catch (SocketException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
#else
        return SendTo(data, remote.ToEndPoint());
#endif
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

    /// <inheritdoc/>
#if NET8_0_OR_GREATER
    public bool TryReceive(byte[] buffer, out int received, out AddressKey remote)
    {
        // net8.0 은 Span 오버로드가 이미 무복사·무할당이다.
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        return TryReceive(buffer.AsSpan(), out received, out remote);
    }
#else
    public bool TryReceive(byte[] buffer, out int received, out AddressKey remote)
    {
        // netstandard2.1 에는 Span 수신 오버로드가 없다. 배열을 그대로 넘겨 스크래치 복사 1회를 없앤다(진단 C-1).
        received = 0;
        remote = default;
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (disposed) return false;

        try
        {
            var n = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref receiveEndPoint);
            if (n <= 0) return false;
            received = n;
            remote = AddressKey.FromEndPoint((IPEndPoint)receiveEndPoint);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset ||
                                         ex.SocketErrorCode == SocketError.MessageSize)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
#endif

#if NET8_0_OR_GREATER
    /// <summary>주소 키를 재사용 <see cref="SocketAddress"/> 버퍼에 직접 기록한다.</summary>
    /// <returns>이 소켓의 주소 체계로 기록할 수 있으면 true.</returns>
    private bool TryWriteKey(AddressKey key, SocketAddress destination)
    {
        var span = destination.Buffer.Span;
        Span<byte> bytes = stackalloc byte[16];
        key.WriteAddressBytes(bytes);

        if (family == AddressFamily.InterNetwork)
        {
            // sockaddr_in: [family 2][port BE 2][addr 4][zero 8]
            if (!key.IsIPv4 || span.Length < 8) return false;
            span[2] = (byte)(key.Port >> 8);
            span[3] = (byte)key.Port;
            bytes.Slice(12, 4).CopyTo(span.Slice(4));
            return true;
        }

        if (family == AddressFamily.InterNetworkV6)
        {
            // sockaddr_in6: [family 2][port BE 2][flowinfo 4][addr 16][scope 4]
            if (span.Length < 28) return false;
            span[2] = (byte)(key.Port >> 8);
            span[3] = (byte)key.Port;
            span.Slice(4, 4).Clear();
            bytes.CopyTo(span.Slice(8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), key.ScopeId);
            return true;
        }

        return false;
    }

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
