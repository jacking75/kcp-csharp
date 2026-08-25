using System;
using System.Net;
using System.Net.Sockets;

namespace KcpProject.Transport;

/// <summary>
/// 세션 테이블의 키로 쓰는 값 타입 주소(IP + 포트).
/// </summary>
/// <remarks>
/// <para>
/// 서버는 수신 패킷마다 "이 주소의 세션이 누구인가"를 조회한다. <see cref="IPEndPoint"/> 를 키로 쓰면
/// 패킷마다 객체가 생기므로, 주소를 16바이트 정수 2개로 눌러 담은 구조체를 키로 쓴다(할당 0).
/// IPv4 는 IPv4 매핑 IPv6 형태로 정규화해 담는다.
/// </para>
/// <para>사용자에게 보여 줄 <see cref="IPEndPoint"/> 는 세션 생성 시 한 번만 만든다.</para>
/// </remarks>
public readonly struct AddressKey : IEquatable<AddressKey>
{
    private readonly ulong hi;
    private readonly ulong lo;
    private readonly uint scopeId;
    private readonly ushort port;

    private AddressKey(ulong hi, ulong lo, uint scopeId, ushort port)
    {
        this.hi = hi;
        this.lo = lo;
        this.scopeId = scopeId;
        this.port = port;
    }

    /// <summary>포트 번호.</summary>
    public int Port => port;

    /// <summary><see cref="IPEndPoint"/> 로부터 키를 만든다.</summary>
    /// <param name="endPoint">주소.</param>
    /// <returns>주소 키.</returns>
    public static AddressKey FromEndPoint(IPEndPoint endPoint)
    {
        if (endPoint == null) throw new ArgumentNullException(nameof(endPoint));
        return FromAddress(endPoint.Address, endPoint.Port);
    }

    /// <summary>IP 주소와 포트로부터 키를 만든다.</summary>
    /// <param name="address">IP 주소.</param>
    /// <param name="port">포트.</param>
    /// <returns>주소 키.</returns>
    public static AddressKey FromAddress(IPAddress address, int port)
    {
        if (address == null) throw new ArgumentNullException(nameof(address));

        Span<byte> bytes = stackalloc byte[16];
        uint scope = 0;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPv4 매핑 IPv6(::ffff:a.b.c.d)로 정규화한다.
            Span<byte> v4 = stackalloc byte[4];
            if (!address.TryWriteBytes(v4, out _))
                throw new ArgumentException("IPv4 주소를 바이트로 변환하지 못했습니다.", nameof(address));

            bytes.Clear();
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
            v4.CopyTo(bytes.Slice(12));
        }
        else
        {
            if (!address.TryWriteBytes(bytes, out var written) || written != 16)
                throw new ArgumentException("IPv6 주소를 바이트로 변환하지 못했습니다.", nameof(address));
            scope = (uint)address.ScopeId;
        }

        var hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8));
        return new AddressKey(hi, lo, scope, (ushort)port);
    }

    /// <summary>키를 다시 <see cref="IPEndPoint"/> 로 만든다(세션 생성 시 1회만 호출한다).</summary>
    /// <returns>주소.</returns>
    public IPEndPoint ToEndPoint()
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, hi);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8), lo);

        // IPv4 매핑이면 IPv4 로 되돌린다.
        var isV4Mapped = hi == 0
                         && bytes[8] == 0 && bytes[9] == 0
                         && bytes[10] == 0xFF && bytes[11] == 0xFF;

        if (isV4Mapped)
        {
            return new IPEndPoint(new IPAddress(bytes.Slice(12, 4).ToArray()), port);
        }

        return new IPEndPoint(new IPAddress(bytes.ToArray(), scopeId), port);
    }

    /// <inheritdoc/>
    public bool Equals(AddressKey other) => hi == other.hi && lo == other.lo && port == other.port && scopeId == other.scopeId;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AddressKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = hi ^ lo;
        return (int)(h ^ (h >> 32)) * 397 ^ (port | (int)(scopeId << 16));
    }

    /// <inheritdoc/>
    public override string ToString() => ToEndPoint().ToString();

    /// <summary>두 키가 같은지 비교한다.</summary>
    /// <param name="left">왼쪽.</param>
    /// <param name="right">오른쪽.</param>
    /// <returns>같으면 true.</returns>
    public static bool operator ==(AddressKey left, AddressKey right) => left.Equals(right);

    /// <summary>두 키가 다른지 비교한다.</summary>
    /// <param name="left">왼쪽.</param>
    /// <param name="right">오른쪽.</param>
    /// <returns>다르면 true.</returns>
    public static bool operator !=(AddressKey left, AddressKey right) => !left.Equals(right);
}
