using System;
using System.Buffers.Binary;

namespace KcpCSharpR1.Transport;

/// <summary>UDP 페이로드 첫 바이트에 오는 프레임 타입.</summary>
public enum FrameType : byte
{
    /// <summary>없음/알 수 없음.</summary>
    None = 0x00,

    /// <summary>클라 → 서버 연결 요청.</summary>
    Syn = 0x01,

    /// <summary>서버 → 클라 쿠키 응답(무상태).</summary>
    SynCookie = 0x02,

    /// <summary>서버 → 클라 연결 수락(conv 할당).</summary>
    SynAck = 0x03,

    /// <summary>신뢰 데이터(KCP 원본 패킷).</summary>
    Kcp = 0x04,

    /// <summary>비신뢰 데이터(재전송·순서 보장 없음).</summary>
    Raw = 0x05,

    /// <summary>keepalive 요청.</summary>
    Ping = 0x06,

    /// <summary>keepalive 응답.</summary>
    Pong = 0x07,

    /// <summary>정상 종료 통지.</summary>
    Fin = 0x08,
}

/// <summary>
/// KcpCSharpR1 전송 계층의 프레이밍 코덱.
/// </summary>
/// <remarks>
/// <para>
/// 모든 UDP 페이로드는 첫 1바이트가 <see cref="FrameType"/> 이다. 이 계층은 소켓을 알지 못하는
/// <b>순수 정적 함수</b>로만 이루어져 있어 단위 테스트로 왕복을 못 박을 수 있다.
/// </para>
/// <para>와이어 포맷 (모든 정수는 리틀엔디언):</para>
/// <code>
/// 0x01 SYN        [0x01][protocolVer u8][clientNonce u32][cookie u32]   10 bytes
/// 0x02 SYN_COOKIE [0x02][protocolVer u8][clientNonce u32][cookie u32]   10 bytes
/// 0x03 SYN_ACK    [0x03][conv u32][serverNonce u32]                      9 bytes
/// 0x04 KCP        [0x04][KCP 원본 패킷(conv 포함) ...]                   1 + n
/// 0x05 RAW        [0x05][conv u32][페이로드 ...]                          5 + n
/// 0x06 PING       [0x06][conv u32][timestamp u32]                        9 bytes
/// 0x07 PONG       [0x07][conv u32][timestamp u32(에코)]                  9 bytes
/// 0x08 FIN        [0x08][conv u32][reason u8]                            6 bytes
/// </code>
/// <para>
/// 코어 KCP 헤더(24바이트)는 건드리지 않는다. <c>RawMode</c> 를 켜면 이 프레이밍 없이
/// 순수 KCP 패킷만 주고받아 kcp-go 등 기존 서버와 상호운용할 수 있다.
/// </para>
/// </remarks>
public static class Framing
{
    /// <summary>현재 프로토콜 버전.</summary>
    public const byte ProtocolVersion = 1;

    /// <summary>SYN / SYN_COOKIE 프레임 길이.</summary>
    public const int SynSize = 10;

    /// <summary>SYN_ACK 프레임 길이.</summary>
    public const int SynAckSize = 9;

    /// <summary>PING / PONG 프레임 길이.</summary>
    public const int PingSize = 9;

    /// <summary>FIN 프레임 길이.</summary>
    public const int FinSize = 6;

    /// <summary>KCP 프레임의 헤더 길이(타입 1바이트).</summary>
    public const int KcpHeaderSize = 1;

    /// <summary>RAW 프레임의 헤더 길이(타입 1 + conv 4).</summary>
    public const int RawHeaderSize = 5;

    /// <summary>프레임의 타입을 읽는다.</summary>
    /// <param name="frame">프레임 바이트.</param>
    /// <returns>타입. 비어 있으면 <see cref="FrameType.None"/>.</returns>
    public static FrameType PeekType(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1) return FrameType.None;
        var value = frame[0];
        return value >= (byte)FrameType.Syn && value <= (byte)FrameType.Fin
            ? (FrameType)value
            : FrameType.None;
    }

    // ---- SYN / SYN_COOKIE ----

    /// <summary>SYN 또는 SYN_COOKIE 프레임을 기록한다.</summary>
    /// <param name="destination">대상(<see cref="SynSize"/> 바이트 이상).</param>
    /// <param name="type"><see cref="FrameType.Syn"/> 또는 <see cref="FrameType.SynCookie"/>.</param>
    /// <param name="clientNonce">클라이언트가 만든 난수(응답 대조용).</param>
    /// <param name="cookie">서버가 준 쿠키. 최초 SYN 은 0.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int WriteSyn(Span<byte> destination, FrameType type, uint clientNonce, uint cookie)
    {
        destination[0] = (byte)type;
        destination[1] = ProtocolVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(2), clientNonce);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(6), cookie);
        return SynSize;
    }

    /// <summary>SYN 또는 SYN_COOKIE 프레임을 읽는다.</summary>
    /// <param name="frame">프레임.</param>
    /// <param name="version">프로토콜 버전.</param>
    /// <param name="clientNonce">클라이언트 난수.</param>
    /// <param name="cookie">쿠키.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryReadSyn(ReadOnlySpan<byte> frame, out byte version, out uint clientNonce, out uint cookie)
    {
        version = 0;
        clientNonce = 0;
        cookie = 0;
        if (frame.Length < SynSize) return false;

        version = frame[1];
        clientNonce = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(2));
        cookie = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(6));
        return true;
    }

    // ---- SYN_ACK ----

    /// <summary>SYN_ACK 프레임을 기록한다.</summary>
    /// <param name="destination">대상(<see cref="SynAckSize"/> 바이트 이상).</param>
    /// <param name="conv">서버가 할당한 대화 번호.</param>
    /// <param name="serverNonce">서버 난수.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int WriteSynAck(Span<byte> destination, uint conv, uint serverNonce)
    {
        destination[0] = (byte)FrameType.SynAck;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), conv);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(5), serverNonce);
        return SynAckSize;
    }

    /// <summary>SYN_ACK 프레임을 읽는다.</summary>
    /// <param name="frame">프레임.</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="serverNonce">서버 난수.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryReadSynAck(ReadOnlySpan<byte> frame, out uint conv, out uint serverNonce)
    {
        conv = 0;
        serverNonce = 0;
        if (frame.Length < SynAckSize) return false;

        conv = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        serverNonce = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(5));
        return true;
    }

    // ---- KCP ----

    /// <summary>KCP 프레임 헤더를 기록한다(뒤에 KCP 원본 패킷이 이어진다).</summary>
    /// <param name="destination">대상.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int WriteKcpHeader(Span<byte> destination)
    {
        destination[0] = (byte)FrameType.Kcp;
        return KcpHeaderSize;
    }

    /// <summary>KCP 프레임의 페이로드(원본 KCP 패킷)를 꺼낸다.</summary>
    /// <param name="frame">프레임.</param>
    /// <param name="payload">KCP 패킷.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryReadKcp(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> payload)
    {
        if (frame.Length <= KcpHeaderSize)
        {
            payload = default;
            return false;
        }
        payload = frame.Slice(KcpHeaderSize);
        return true;
    }

    // ---- RAW ----

    /// <summary>RAW(비신뢰) 프레임을 기록한다.</summary>
    /// <param name="destination">대상(<see cref="RawHeaderSize"/> + 페이로드 크기 이상).</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="payload">페이로드.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int WriteRaw(Span<byte> destination, uint conv, ReadOnlySpan<byte> payload)
    {
        destination[0] = (byte)FrameType.Raw;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), conv);
        payload.CopyTo(destination.Slice(RawHeaderSize));
        return RawHeaderSize + payload.Length;
    }

    /// <summary>RAW 프레임을 읽는다.</summary>
    /// <param name="frame">프레임.</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="payload">페이로드.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryReadRaw(ReadOnlySpan<byte> frame, out uint conv, out ReadOnlySpan<byte> payload)
    {
        conv = 0;
        payload = default;
        if (frame.Length < RawHeaderSize) return false;

        conv = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        payload = frame.Slice(RawHeaderSize);
        return true;
    }

    // ---- PING / PONG ----

    /// <summary>PING 또는 PONG 프레임을 기록한다.</summary>
    /// <param name="destination">대상(<see cref="PingSize"/> 바이트 이상).</param>
    /// <param name="type"><see cref="FrameType.Ping"/> 또는 <see cref="FrameType.Pong"/>.</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="timestamp">보낸 시각(ms). PONG 은 받은 값을 그대로 되돌린다.</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int WritePing(Span<byte> destination, FrameType type, uint conv, uint timestamp)
    {
        destination[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), conv);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(5), timestamp);
        return PingSize;
    }

    /// <summary>PING 또는 PONG 프레임을 읽는다.</summary>
    /// <param name="frame">프레임.</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="timestamp">타임스탬프.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryReadPing(ReadOnlySpan<byte> frame, out uint conv, out uint timestamp)
    {
        conv = 0;
        timestamp = 0;
        if (frame.Length < PingSize) return false;

        conv = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        timestamp = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(5));
        return true;
    }

    // ---- FIN ----

    /// <summary>FIN 프레임을 기록한다.</summary>
    /// <param name="destination">대상(<see cref="FinSize"/> 바이트 이상).</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="reason">종료 사유(애플리케이션 정의).</param>
    /// <returns>기록한 바이트 수.</returns>
    public static int WriteFin(Span<byte> destination, uint conv, byte reason)
    {
        destination[0] = (byte)FrameType.Fin;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), conv);
        destination[5] = reason;
        return FinSize;
    }

    /// <summary>FIN 프레임을 읽는다.</summary>
    /// <param name="frame">프레임.</param>
    /// <param name="conv">대화 번호.</param>
    /// <param name="reason">종료 사유.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryReadFin(ReadOnlySpan<byte> frame, out uint conv, out byte reason)
    {
        conv = 0;
        reason = 0;
        if (frame.Length < FinSize) return false;

        conv = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        reason = frame[5];
        return true;
    }
}
