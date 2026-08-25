using System;

namespace Gomoku.Shared;

/// <summary>
/// 오목 메시지 타입.
/// </summary>
/// <remarks>최상위 비트로 방향을 구분한다(<c>0x00~0x7F</c> C→S, <c>0x80~0xFF</c> S→C).</remarks>
public enum MessageType : byte
{
    /// <summary>C→S 대기열 참가. 페이로드: nick(string).</summary>
    JoinQueue = 0x01,

    /// <summary>C→S 착수. 페이로드: x(u8), y(u8).</summary>
    PlaceStone = 0x02,

    /// <summary>C→S 항복.</summary>
    Surrender = 0x03,

    /// <summary>C→S 재접속. 페이로드: token(string).</summary>
    Reconnect = 0x04,

    /// <summary>C→S 채팅. 페이로드: text(string).</summary>
    Chat = 0x05,

    /// <summary>C→S 관전 요청. 페이로드: nick(string).</summary>
    Spectate = 0x06,

    /// <summary>S→C 매칭 성사. 페이로드: roomId(u32), myColor(u8), oppNick(string), token(string).</summary>
    MatchFound = 0x81,

    /// <summary>S→C 착수 알림. 페이로드: x(u8), y(u8), color(u8).</summary>
    StonePlaced = 0x82,

    /// <summary>S→C 차례 알림. 페이로드: turnColor(u8).</summary>
    TurnChanged = 0x83,

    /// <summary>S→C 게임 종료. 페이로드: result(u8), winnerColor(u8).</summary>
    GameOver = 0x84,

    /// <summary>S→C 전체 상태 동기화. 페이로드: board(225바이트), turn(u8), myColor(u8), finished(u8), winner(u8).</summary>
    StateSync = 0x85,

    /// <summary>S→C 채팅 전달. 페이로드: nick(string), text(string).</summary>
    ChatFrom = 0x86,

    /// <summary>S→C 대기열 상태. 페이로드: 대기 인원(u16).</summary>
    QueueStatus = 0x87,

    /// <summary>S→C 상대 접속 끊김(재접속 대기). 페이로드: 남은 초(u8).</summary>
    OpponentDisconnected = 0x88,

    /// <summary>S→C 상대 복귀.</summary>
    OpponentReconnected = 0x89,

    /// <summary>S→C 오류. 페이로드: code(u8), message(string).</summary>
    Error = 0x8F,
}

/// <summary>게임 종료 사유.</summary>
public enum GameResult : byte
{
    /// <summary>오목을 완성했다.</summary>
    Win = 0,

    /// <summary>상대가 오목을 완성했다.</summary>
    Lose = 1,

    /// <summary>판이 가득 찼다.</summary>
    Draw = 2,

    /// <summary>상대가 항복했다.</summary>
    OpponentSurrendered = 3,

    /// <summary>상대가 재접속 시간 안에 돌아오지 않았다.</summary>
    OpponentLeft = 4,
}

/// <summary>서버가 돌려주는 오류 코드.</summary>
public enum GomokuError : byte
{
    /// <summary>닉네임이 비었거나 너무 길다.</summary>
    NickInvalid = 1,

    /// <summary>아직 방에 들어가지 않았다.</summary>
    NotInRoom = 2,

    /// <summary>내 차례가 아니다.</summary>
    NotYourTurn = 3,

    /// <summary>보드 밖이거나 이미 돌이 있다.</summary>
    InvalidMove = 4,

    /// <summary>재접속 토큰이 없거나 만료됐다.</summary>
    BadToken = 5,

    /// <summary>관전할 방이 없다.</summary>
    NoRoomToSpectate = 6,

    /// <summary>해석할 수 없는 패킷.</summary>
    MalformedPacket = 7,
}

/// <summary>프로토콜 상수.</summary>
public static class ProtocolLimits
{
    /// <summary>한 메시지의 최대 바이트 수.</summary>
    public const int MaxMessageBytes = 1024;

    /// <summary>닉네임 최대 길이.</summary>
    public const int MaxNickLength = 16;

    /// <summary>채팅 최대 길이.</summary>
    public const int MaxChatLength = 200;

    /// <summary>재접속을 기다려 주는 시간(초).</summary>
    public const int ReconnectGraceSeconds = 30;

    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ③ Gomoku = 27003.</summary>
    public const int Port = 27003;
}
