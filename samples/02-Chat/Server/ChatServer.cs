using System;
using System.Collections.Generic;
using System.Linq;
using Chat.Shared;
using KcpCSharpR1.Transport;

namespace Chat.Server;

/// <summary>
/// 채팅 서버 본체.
/// </summary>
/// <remarks>
/// <para>
/// <b>스레딩</b>: 모든 콜백은 <see cref="KcpServer"/> 의 수신 루프 스레드 하나에서만 발화한다.
/// 그래서 이 클래스는 lock 없이 <see cref="Dictionary{TKey,TValue}"/> 를 그대로 쓴다.
/// 실제 게임 서버라면 여기서 게임 로직 스레드의 큐로 넘기는 것이 정석이다(책 6장).
/// </para>
/// <para>
/// <b>원칙</b>: 서버는 클라이언트를 믿지 않는다. 닉네임 길이·중복·빈 값, 메시지 길이, 순서(닉 설정 전 발언)를
/// 전부 서버에서 검사한다.
/// </para>
/// </remarks>
internal sealed class ChatServer : IDisposable
{
    private readonly KcpServer server;
    private readonly Dictionary<KcpSession, string> nicknames = new();
    private readonly Dictionary<string, KcpSession> byNickname = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] sendBuffer = new byte[ChatLimits.MaxMessageBytes];
    private readonly Action<string> log;

    /// <summary>서버를 만든다.</summary>
    /// <param name="port">UDP 포트.</param>
    /// <param name="log">로그 출력.</param>
    public ChatServer(int port, Action<string> log)
    {
        this.log = log;

        server = new KcpServer(port);
        server.SessionConnected += OnConnected;
        server.SessionDisconnected += OnDisconnected;
        server.ReliableReceived += OnReceived;
        server.Error += ex => log($"오류: {ex.Message}");
    }

    /// <summary>현재 접속자 수.</summary>
    public int UserCount => nicknames.Count;

    /// <summary>수신 루프를 시작한다.</summary>
    public void Start() => server.Start();

    /// <summary>수신 루프를 멈춘다.</summary>
    public void Stop() => server.Stop();

    private void OnConnected(KcpSession session)
    {
        log($"접속 {session.RemoteEndPoint} (conv={session.Conv}) — 닉네임 대기 중");
    }

    private void OnDisconnected(KcpSession session, KcpDisconnectReason reason)
    {
        if (!nicknames.TryGetValue(session, out var nick))
        {
            log($"종료 {session.RemoteEndPoint} ({reason}) — 닉네임 없이 나감");
            return;
        }

        nicknames.Remove(session);
        byNickname.Remove(nick);

        log($"퇴장 {nick} ({reason}) — 남은 인원 {nicknames.Count}");
        BroadcastNick(MessageType.UserLeft, nick, except: session);
    }

    private void OnReceived(KcpSession session, ReadOnlyMemory<byte> data)
    {
        if (!MessageReader.TryParse(data.Span, out var reader))
        {
            SendError(session, ChatError.MalformedPacket, "패킷을 해석할 수 없습니다.");
            return;
        }

        switch (reader.Type)
        {
            case MessageType.SetNick:
                HandleSetNick(session, ref reader);
                break;

            case MessageType.Say:
                HandleSay(session, ref reader);
                break;

            case MessageType.Whisper:
                HandleWhisper(session, ref reader);
                break;

            default:
                SendError(session, ChatError.MalformedPacket, $"알 수 없는 타입 0x{(byte)reader.Type:X2}");
                break;
        }
    }

    private void HandleSetNick(KcpSession session, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var nick))
        {
            SendError(session, ChatError.MalformedPacket, "닉네임을 읽을 수 없습니다.");
            return;
        }

        nick = nick.Trim();

        // 서버 측 검증 — 클라이언트가 이미 검사했더라도 다시 한다.
        if (nick.Length == 0 || nick.Length > ChatLimits.MaxNickLength || nick.Any(char.IsControl))
        {
            SendError(session, ChatError.NickInvalid,
                $"닉네임은 1~{ChatLimits.MaxNickLength}자여야 하고 제어문자를 넣을 수 없습니다.");
            return;
        }

        if (byNickname.ContainsKey(nick))
        {
            SendError(session, ChatError.NickTaken, $"'{nick}' 은(는) 이미 사용 중입니다.");
            return;
        }

        // 닉 변경도 허용한다(이미 있던 이름은 지운다).
        if (nicknames.TryGetValue(session, out var oldNick))
        {
            byNickname.Remove(oldNick);
            BroadcastNick(MessageType.UserLeft, oldNick, except: session);
        }

        nicknames[session] = nick;
        byNickname[nick] = session;

        SendWelcome(session);
        BroadcastNick(MessageType.UserJoined, nick, except: session);

        log($"입장 {nick} (conv={session.Conv}) — 현재 {nicknames.Count}명");
    }

    private void HandleSay(KcpSession session, ref MessageReader reader)
    {
        if (!nicknames.TryGetValue(session, out var nick))
        {
            SendError(session, ChatError.NotJoined, "먼저 닉네임을 정하세요.");
            return;
        }

        if (!reader.TryReadString(out var text))
        {
            SendError(session, ChatError.MalformedPacket, "본문을 읽을 수 없습니다.");
            return;
        }

        if (text.Length == 0) return;
        if (text.Length > ChatLimits.MaxTextLength)
        {
            SendError(session, ChatError.MessageTooLong, $"{ChatLimits.MaxTextLength}자를 넘을 수 없습니다.");
            return;
        }

        log($"[전체] {nick}: {text}");

        var writer = new MessageWriter(sendBuffer, MessageType.Chat);
        writer.WriteString(nick);
        writer.WriteString(text);
        var packet = writer.Finish();

        foreach (var target in nicknames.Keys)
            target.Send(packet);
    }

    private void HandleWhisper(KcpSession session, ref MessageReader reader)
    {
        if (!nicknames.TryGetValue(session, out var nick))
        {
            SendError(session, ChatError.NotJoined, "먼저 닉네임을 정하세요.");
            return;
        }

        if (!reader.TryReadString(out var targetNick) || !reader.TryReadString(out var text))
        {
            SendError(session, ChatError.MalformedPacket, "귓속말을 읽을 수 없습니다.");
            return;
        }

        if (text.Length > ChatLimits.MaxTextLength)
        {
            SendError(session, ChatError.MessageTooLong, $"{ChatLimits.MaxTextLength}자를 넘을 수 없습니다.");
            return;
        }

        if (!byNickname.TryGetValue(targetNick, out var target))
        {
            SendError(session, ChatError.TargetNotFound, $"'{targetNick}' 은(는) 접속해 있지 않습니다.");
            return;
        }

        log($"[귓속말] {nick} → {targetNick}");

        var writer = new MessageWriter(sendBuffer, MessageType.WhisperFrom);
        writer.WriteString(nick);
        writer.WriteString(text);
        target.Send(writer.Finish());
    }

    private void SendWelcome(KcpSession session)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.Welcome);
        writer.WriteString(ChatLimits.Motd);
        writer.WriteUInt16((ushort)nicknames.Count);
        foreach (var other in nicknames.Values)
            writer.WriteString(other);

        session.Send(writer.Finish());
    }

    private void BroadcastNick(MessageType type, string nick, KcpSession? except)
    {
        var writer = new MessageWriter(sendBuffer, type);
        writer.WriteString(nick);
        var packet = writer.Finish();

        foreach (var target in nicknames.Keys)
        {
            if (ReferenceEquals(target, except)) continue;
            target.Send(packet);
        }
    }

    private void SendError(KcpSession session, ChatError code, string message)
    {
        var writer = new MessageWriter(sendBuffer, MessageType.Error);
        writer.WriteByte((byte)code);
        writer.WriteString(message);
        session.Send(writer.Finish());
    }

    /// <summary>자원을 정리한다.</summary>
    public void Dispose() => server.Dispose();
}
