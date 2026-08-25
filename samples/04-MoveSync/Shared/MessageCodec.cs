using System;
using System.Buffers.Binary;
using System.Text;

namespace MoveSync.Shared;

/// <summary>
/// 메시지를 바이트로 쓰는 쪽.
/// </summary>
/// <remarks>
/// <para>와이어 포맷: <c>[u16 length][u8 type][payload...]</c>. length 는 <b>자기 자신을 제외한</b>
/// 뒤쪽 전체 길이(타입 1바이트 + 페이로드)다. 문자열은 <c>[u16 바이트수][UTF-8]</c>.</para>
/// <para>
/// <b>왜 길이 접두사를 넣나?</b> KCP 를 메시지 모드로 쓰면 경계가 보존되므로 사실 없어도 된다.
/// 그래도 넣는 이유는 두 가지다. ① 스트림 모드로 바꾸거나 TCP 로 옮겨도 코드가 그대로 산다.
/// ② 한 패킷에 여러 메시지를 묶어 보내는 최적화를 나중에 넣을 수 있다.
/// 실무 프로토콜은 대부분 이 형태이므로 습관을 들여 두는 편이 낫다.
/// </para>
/// <para>직렬화 라이브러리를 쓰지 않는다. 바이트가 어떻게 놓이는지 직접 보는 것이 이 샘플의 목적이다.</para>
/// </remarks>
public ref struct MessageWriter
{
    private readonly Span<byte> buffer;
    private int position;

    /// <summary>대상 버퍼를 감싸는 작성기를 만든다.</summary>
    /// <param name="destination">대상 버퍼.</param>
    /// <param name="type">메시지 타입.</param>
    public MessageWriter(Span<byte> destination, MessageType type)
    {
        buffer = destination;
        position = sizeof(ushort);       // 길이 자리를 비워 두고 시작한다.
        buffer[position++] = (byte)type;
    }

    /// <summary>지금까지 기록한 바이트 수(길이 접두사 포함).</summary>
    public int Length => position;

    /// <summary>부호 없는 8비트 정수를 쓴다.</summary>
    /// <param name="value">값.</param>
    public void WriteByte(byte value)
    {
        buffer[position++] = value;
    }

    /// <summary>부호 없는 16비트 정수를 쓴다(리틀엔디언).</summary>
    /// <param name="value">값.</param>
    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(position), value);
        position += sizeof(ushort);
    }

    /// <summary>부호 없는 32비트 정수를 쓴다(리틀엔디언).</summary>
    /// <param name="value">값.</param>
    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(position), value);
        position += sizeof(uint);
    }

    /// <summary>32비트 부동소수를 쓴다(IEEE 754 리틀엔디언).</summary>
    /// <param name="value">값.</param>
    public void WriteSingle(float value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(position), BitConverter.SingleToUInt32Bits(value));
        position += sizeof(float);
    }

    /// <summary>바이트 배열을 길이 없이 그대로 쓴다(길이를 앞서 적었을 때 쓴다).</summary>
    /// <param name="source">원본.</param>
    public void WriteBytes(scoped ReadOnlySpan<byte> source)
    {
        source.CopyTo(buffer.Slice(position));
        position += source.Length;
    }

    /// <summary>문자열을 <c>[u16 바이트수][UTF-8]</c> 로 쓴다.</summary>
    /// <param name="value">문자열. null 은 빈 문자열로 취급한다.</param>
    /// <exception cref="ArgumentException">UTF-8 인코딩 결과가 65535바이트를 넘는 경우.</exception>
    public void WriteString(string? value)
    {
        value ??= string.Empty;

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ArgumentException("문자열이 너무 깁니다.", nameof(value));

        WriteUInt16((ushort)byteCount);
        Encoding.UTF8.GetBytes(value, buffer.Slice(position));
        position += byteCount;
    }

    /// <summary>길이 접두사를 채우고 완성된 메시지 구간을 돌려준다.</summary>
    /// <returns>보낼 바이트 구간.</returns>
    public ReadOnlySpan<byte> Finish()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)(position - sizeof(ushort)));
        return buffer.Slice(0, position);
    }
}

/// <summary>
/// 메시지를 바이트에서 읽는 쪽.
/// </summary>
/// <remarks>
/// 모든 읽기는 <c>Try</c> 형태다. <b>네트워크에서 온 바이트는 언제나 거짓말일 수 있으므로</b>
/// 예외 대신 false 를 돌려주고 호출자가 세션을 끊거나 오류를 회신하게 한다.
/// </remarks>
public ref struct MessageReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private int position;

    private MessageReader(ReadOnlySpan<byte> payload, MessageType type)
    {
        buffer = payload;
        position = 0;
        Type = type;
        IsValid = true;
    }

    /// <summary>메시지 타입.</summary>
    public MessageType Type { get; }

    /// <summary>지금까지 읽기가 모두 성공했는지 여부.</summary>
    public bool IsValid { get; private set; }

    /// <summary>남은 바이트 수.</summary>
    public int Remaining => buffer.Length - position;

    /// <summary>패킷 하나를 해석한다.</summary>
    /// <param name="packet">받은 바이트 전체.</param>
    /// <param name="reader">해석 결과.</param>
    /// <returns>형식이 맞으면 true.</returns>
    public static bool TryParse(ReadOnlySpan<byte> packet, out MessageReader reader)
    {
        reader = default;

        if (packet.Length < sizeof(ushort) + 1) return false;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if (length < 1 || sizeof(ushort) + length > packet.Length) return false;

        var type = (MessageType)packet[sizeof(ushort)];
        var payload = packet.Slice(sizeof(ushort) + 1, length - 1);

        reader = new MessageReader(payload, type);
        return true;
    }

    /// <summary>부호 없는 8비트 정수를 읽는다.</summary>
    /// <param name="value">읽은 값.</param>
    /// <returns>성공하면 true.</returns>
    public bool TryReadByte(out byte value)
    {
        value = 0;
        if (!IsValid || Remaining < 1) { IsValid = false; return false; }

        value = buffer[position++];
        return true;
    }

    /// <summary>부호 없는 16비트 정수를 읽는다.</summary>
    /// <param name="value">읽은 값.</param>
    /// <returns>성공하면 true.</returns>
    public bool TryReadUInt16(out ushort value)
    {
        value = 0;
        if (!IsValid || Remaining < sizeof(ushort)) { IsValid = false; return false; }

        value = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position));
        position += sizeof(ushort);
        return true;
    }

    /// <summary>부호 없는 32비트 정수를 읽는다.</summary>
    /// <param name="value">읽은 값.</param>
    /// <returns>성공하면 true.</returns>
    public bool TryReadUInt32(out uint value)
    {
        value = 0;
        if (!IsValid || Remaining < sizeof(uint)) { IsValid = false; return false; }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position));
        position += sizeof(uint);
        return true;
    }

    /// <summary>32비트 부동소수를 읽는다.</summary>
    /// <param name="value">읽은 값.</param>
    /// <returns>성공하면 true.</returns>
    public bool TryReadSingle(out float value)
    {
        value = 0;
        if (!TryReadUInt32(out var bits)) return false;

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }

    /// <summary>바이트를 지정한 개수만큼 읽는다.</summary>
    /// <param name="count">개수.</param>
    /// <param name="value">읽은 구간.</param>
    /// <returns>성공하면 true.</returns>
    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!IsValid || count < 0 || Remaining < count) { IsValid = false; return false; }

        value = buffer.Slice(position, count);
        position += count;
        return true;
    }

    /// <summary>문자열을 읽는다.</summary>
    /// <param name="value">읽은 문자열.</param>
    /// <param name="maxBytes">허용할 최대 바이트 수(폭탄 패킷 방어).</param>
    /// <returns>성공하면 true.</returns>
    public bool TryReadString(out string value, int maxBytes = ProtocolLimits.MaxMessageBytes)
    {
        value = string.Empty;
        if (!TryReadUInt16(out var byteCount)) return false;

        if (byteCount > maxBytes || Remaining < byteCount)
        {
            IsValid = false;
            return false;
        }

        value = Encoding.UTF8.GetString(buffer.Slice(position, byteCount));
        position += byteCount;
        return true;
    }
}
