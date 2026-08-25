using System;

namespace TankBattle.Shared;

/// <summary>탱크 배틀 메시지 타입.</summary>
/// <remarks>
/// 입력과 스냅숏은 <b>비신뢰</b>(곧 다음 것이 온다), 발사 결과·점수·입퇴장은 <b>신뢰</b>(놓치면 안 된다).
/// </remarks>
public enum MessageType : byte
{
    /// <summary>C→S(신뢰) 입장. 페이로드: nick(string).</summary>
    Join = 0x01,

    /// <summary>
    /// C→S(비신뢰) 입력 묶음. 페이로드: count(u8), { seq(u32), throttle(i8), steer(i8), turret(f32), fire(u8) } × count.
    /// <b>최근 3개를 한 패킷에 실어</b> 손실 내성을 얻는다.
    /// </summary>
    InputBatch = 0x02,

    /// <summary>S→C(신뢰) 입장 수락. 페이로드: myId(u32), tickRate(u8), worldW(u16), worldH(u16).</summary>
    Welcome = 0x81,

    /// <summary>S→C(신뢰) 플레이어 입장. 페이로드: id(u32), nick(string), colorIndex(u8).</summary>
    PlayerJoined = 0x82,

    /// <summary>S→C(신뢰) 플레이어 퇴장. 페이로드: id(u32).</summary>
    PlayerLeft = 0x83,

    /// <summary>
    /// S→C(비신뢰) 스냅숏.
    /// 페이로드: tick(u32), lastProcessedInputSeq(u32), count(u8),
    /// { id(u32), x(f32), y(f32), body(f32), turret(f32), alive(u8) } × count.
    /// <b>lastProcessedInputSeq 가 클라이언트 보정(reconciliation)의 열쇠다.</b>
    /// </summary>
    Snapshot = 0x84,

    /// <summary>S→C(신뢰) 발사 발생. 페이로드: shooterId(u32), x(f32), y(f32), angle(f32), hitId(u32, 0이면 빗나감).</summary>
    Shot = 0x85,

    /// <summary>S→C(신뢰) 사망. 페이로드: victimId(u32), killerId(u32).</summary>
    Killed = 0x86,

    /// <summary>S→C(신뢰) 리스폰. 페이로드: id(u32), x(f32), y(f32).</summary>
    Respawned = 0x87,

    /// <summary>S→C(신뢰) 점수판. 페이로드: count(u8), { id(u32), kills(u16), deaths(u16) } × count.</summary>
    Scoreboard = 0x88,
}

/// <summary>게임 상수. 서버와 클라이언트가 반드시 같은 값을 써야 한다.</summary>
public static class GameLimits
{
    /// <summary>로드맵 12.4절 포트 배정표: 샘플 ⑤ TankBattle = 27005.</summary>
    public const int Port = 27005;

    /// <summary>서버 틱레이트(Hz).</summary>
    public const int TickRate = 20;

    /// <summary>클라이언트 입력 전송 주기(Hz).</summary>
    public const int InputRate = 30;

    /// <summary>한 입력 패킷에 싣는 최근 입력 개수(손실 내성).</summary>
    public const int InputRedundancy = 3;

    /// <summary>월드 가로.</summary>
    public const int WorldWidth = 900;

    /// <summary>월드 세로.</summary>
    public const int WorldHeight = 640;

    /// <summary>이동 속도(픽셀/초).</summary>
    public const float MoveSpeed = 160f;

    /// <summary>회전 속도(라디안/초).</summary>
    public const float TurnSpeed = 2.6f;

    /// <summary>탱크 반지름.</summary>
    public const float TankRadius = 16f;

    /// <summary>포탄 사거리.</summary>
    public const float FireRange = 460f;

    /// <summary>발사 쿨다운(틱). 20Hz 기준 0.6초.</summary>
    public const uint FireCooldownTicks = 12;

    /// <summary>사망 후 리스폰까지(틱). 3초.</summary>
    public const uint RespawnTicks = 60;

    /// <summary>최대 인원.</summary>
    public const int MaxPlayers = 8;

    /// <summary>지연 보상을 위해 서버가 보관하는 과거 스냅숏 수(1초).</summary>
    public const int HistoryTicks = TickRate;

    /// <summary>클라이언트 보간 지연(ms).</summary>
    public const int InterpolationDelayMs = 100;

    /// <summary>닉네임 최대 길이.</summary>
    public const int MaxNickLength = 12;

    /// <summary>한 메시지의 최대 바이트 수.</summary>
    public const int MaxMessageBytes = 1024;
}
