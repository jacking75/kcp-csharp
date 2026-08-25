using System;

namespace MoveSync.Shared;

/// <summary>
/// 이동 동기화 메시지 타입.
/// </summary>
/// <remarks>
/// <para><b>어떤 메시지를 어느 채널로 보낼지가 이 샘플의 핵심이다.</b></para>
/// <list type="table">
/// <item>
///   <term>신뢰 채널(KCP)</term>
///   <description>
///   놓치면 게임이 망가지는 것 — 입장/퇴장, 내 ID 배정, 채팅.
///   한 번만 도착하면 되고 순서가 중요하다.
///   </description>
/// </item>
/// <item>
///   <term>비신뢰 채널(RAW)</term>
///   <description>
///   <b>다음 것이 곧 오는 것</b> — 입력(10Hz)과 스냅숏(20Hz).
///   50ms 뒤면 낡을 데이터를 재전송받아 봐야 지연만 는다.
///   </description>
/// </item>
/// </list>
/// </remarks>
public enum MessageType : byte
{
    /// <summary>C→S(신뢰) 입장. 페이로드: nick(string).</summary>
    Join = 0x01,

    /// <summary>C→S(비신뢰) 입력. 페이로드: seq(u32), dir(u8).</summary>
    Input = 0x02,

    /// <summary>C→S(신뢰) 채팅. 페이로드: text(string).</summary>
    Chat = 0x03,

    /// <summary>S→C(신뢰) 입장 수락. 페이로드: myId(u32), tickRate(u8), worldW(u16), worldH(u16).</summary>
    Welcome = 0x81,

    /// <summary>S→C(신뢰) 다른 플레이어 입장. 페이로드: id(u32), nick(string), colorIndex(u8).</summary>
    PlayerJoined = 0x82,

    /// <summary>S→C(신뢰) 플레이어 퇴장. 페이로드: id(u32).</summary>
    PlayerLeft = 0x83,

    /// <summary>
    /// S→C(비신뢰) 월드 스냅숏.
    /// 페이로드: tick(u32), count(u8), { id(u32), x(f32), y(f32), dir(u8) } × count.
    /// </summary>
    Snapshot = 0x84,

    /// <summary>S→C(신뢰) 채팅 전달. 페이로드: nick(string), text(string).</summary>
    ChatFrom = 0x85,
}

/// <summary>이동 방향 비트 플래그.</summary>
[Flags]
public enum MoveDirection : byte
{
    /// <summary>정지.</summary>
    None = 0,

    /// <summary>위.</summary>
    Up = 1,

    /// <summary>아래.</summary>
    Down = 2,

    /// <summary>왼쪽.</summary>
    Left = 4,

    /// <summary>오른쪽.</summary>
    Right = 8,
}

/// <summary>프로토콜·월드 상수.</summary>
public static class ProtocolLimits
{
    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ④ MoveSync = 27004.</summary>
    public const int Port = 27004;

    /// <summary>서버 틱레이트(Hz).</summary>
    public const int TickRate = 20;

    /// <summary>클라이언트 입력 전송 주기(Hz).</summary>
    public const int InputRate = 10;

    /// <summary>월드 가로 크기(픽셀).</summary>
    public const int WorldWidth = 800;

    /// <summary>월드 세로 크기(픽셀).</summary>
    public const int WorldHeight = 600;

    /// <summary>이동 속도(픽셀/초).</summary>
    public const float MoveSpeed = 180f;

    /// <summary>플레이어 반지름(픽셀).</summary>
    public const float PlayerRadius = 12f;

    /// <summary>최대 인원.</summary>
    public const int MaxPlayers = 16;

    /// <summary>닉네임 최대 길이.</summary>
    public const int MaxNickLength = 12;

    /// <summary>채팅 최대 길이.</summary>
    public const int MaxChatLength = 120;

    /// <summary>한 메시지의 최대 바이트 수.</summary>
    public const int MaxMessageBytes = 1024;

    /// <summary>
    /// 클라이언트 보간 지연(ms). 스냅숏을 이만큼 늦게 재생해 두 스냅숏 사이를 부드럽게 잇는다.
    /// 틱 간격(50ms)의 2배로 잡아 하나쯤 유실돼도 끊기지 않게 한다.
    /// </summary>
    public const int InterpolationDelayMs = 100;

    /// <summary>스냅숏 한 항목의 바이트 수: id(4) + x(4) + y(4) + dir(1).</summary>
    public const int SnapshotEntryBytes = 13;

    /// <summary>스냅숏 헤더 바이트 수: tick(4) + count(1).</summary>
    public const int SnapshotHeaderBytes = 5;
}
