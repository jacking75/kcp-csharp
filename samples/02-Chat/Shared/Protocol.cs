using System;

namespace Chat.Shared;

/// <summary>
/// 채팅 메시지 타입.
/// </summary>
/// <remarks>
/// 최상위 비트로 방향을 구분한다. <c>0x00~0x7F</c> 는 클라 → 서버, <c>0x80~0xFF</c> 는 서버 → 클라.
/// 로그만 봐도 방향을 알 수 있어 디버깅이 쉬워진다.
/// </remarks>
public enum MessageType : byte
{
    /// <summary>C→S 닉네임 설정. 페이로드: nick(string).</summary>
    SetNick = 0x01,

    /// <summary>C→S 전체 발언. 페이로드: text(string).</summary>
    Say = 0x02,

    /// <summary>C→S 귓속말. 페이로드: targetNick(string), text(string).</summary>
    Whisper = 0x03,

    /// <summary>S→C 입장 환영. 페이로드: motd(string), 접속자 수(u16), nick(string) × n.</summary>
    Welcome = 0x81,

    /// <summary>S→C 누군가 들어옴. 페이로드: nick(string).</summary>
    UserJoined = 0x82,

    /// <summary>S→C 누군가 나감. 페이로드: nick(string).</summary>
    UserLeft = 0x83,

    /// <summary>S→C 발언 전달. 페이로드: nick(string), text(string).</summary>
    Chat = 0x84,

    /// <summary>S→C 귓속말 전달. 페이로드: fromNick(string), text(string).</summary>
    WhisperFrom = 0x85,

    /// <summary>S→C 오류. 페이로드: code(u8), message(string).</summary>
    Error = 0x8F,
}

/// <summary>서버가 돌려주는 오류 코드.</summary>
public enum ChatError : byte
{
    /// <summary>이미 쓰고 있는 닉네임.</summary>
    NickTaken = 1,

    /// <summary>빈 닉네임이거나 길이·문자가 규칙에 어긋남.</summary>
    NickInvalid = 2,

    /// <summary>닉네임을 정하기 전에 발언했다.</summary>
    NotJoined = 3,

    /// <summary>귓속말 대상이 없다.</summary>
    TargetNotFound = 4,

    /// <summary>메시지가 너무 길다.</summary>
    MessageTooLong = 5,

    /// <summary>해석할 수 없는 패킷.</summary>
    MalformedPacket = 6,
}

/// <summary>프로토콜 상수.</summary>
public static class ChatLimits
{
    /// <summary>닉네임 최대 길이(문자 수).</summary>
    public const int MaxNickLength = 16;

    /// <summary>발언 최대 길이(문자 수).</summary>
    public const int MaxTextLength = 500;

    /// <summary>한 메시지의 최대 바이트 수(KCP 한 번에 보낼 수 있는 크기 안쪽으로 잡는다).</summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>서버 접속 환영 문구.</summary>
    public const string Motd = "KcpCSharpR1 채팅 샘플에 오신 것을 환영합니다. /help 로 명령을 볼 수 있습니다.";
}
