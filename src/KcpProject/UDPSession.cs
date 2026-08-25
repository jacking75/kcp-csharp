using System;
using System.Net;
using System.Net.Sockets;

namespace KcpProject;

/// <summary>
/// 단일 서버에 연결하는 최소 클라이언트 세션(레거시).
/// </summary>
/// <remarks>
/// <para>
/// 핸드셰이크가 없어 conv 를 클라이언트가 임의로 정하므로 서버와 합의할 방법이 없다(진단 B-11).
/// 새 코드는 <c>KcpProject.Transport.KcpClient</c> 를 사용한다. 이 타입은 기존 사용자 호환을 위해
/// 한 메이저 버전 동안만 유지한다.
/// </para>
/// <para><b>스레딩 규약</b>: 인스턴스 1개는 소유 스레드 1개에서만 사용한다.</para>
/// </remarks>
[Obsolete("KcpProject.Transport.KcpClient 로 대체되었습니다. 다음 메이저 버전에서 제거됩니다.")]
public class UDPSession
{
    private Socket? mSocket;
    private Kcp? mKCP;

    private readonly ByteBuffer mRecvBuffer = ByteBuffer.Allocate(1024 * 32);
    private uint mNextUpdateTime;

    /// <summary>소켓이 열려 있고 연결된 상태인지 여부.</summary>
    public bool IsConnected => mSocket != null && mSocket.Connected;

    /// <summary>true 면 <see cref="Send"/> 에서 즉시 Flush 하지 않고 다음 <see cref="Update"/> 로 미룬다.</summary>
    public bool WriteDelay { get; set; }

    /// <summary>true 면 수신 즉시 ACK 를 내보낸다. 지연은 줄지만 대역폭 효율은 떨어진다.</summary>
    public bool AckNoDelay { get; set; }

    /// <summary>수신 루프에서 발생한 소켓 예외를 전달받는 콜백. 지정하지 않으면 예외를 그대로 던진다(진단 B-13).</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>연결된 상대 주소.</summary>
    public IPEndPoint? RemoteAddress { get; private set; }

    /// <summary>로컬 주소.</summary>
    public IPEndPoint? LocalAddress { get; private set; }

    /// <summary>내부 <see cref="Kcp"/> 인스턴스. 통계·RTT 조회용.</summary>
    public Kcp? Kcp => mKCP;

    /// <summary>서버에 연결한다.</summary>
    /// <param name="host">호스트 이름 또는 IP 리터럴.</param>
    /// <param name="port">포트.</param>
    /// <exception cref="SocketException">이름을 주소로 풀 수 없는 경우.</exception>
    public void Connect(string host, int port)
    {
        // 진단 B-10: GetHostEntry 대신 GetHostAddresses 를 쓰고, IPv4 를 우선한다.
        // (IPv6 가 목록 앞에 오면 IPv4 전용 서버에 연결하지 못한다.)
        if (!IPAddress.TryParse(host, out var addr))
        {
            var list = Dns.GetHostAddresses(host);
            addr = Array.Find(list, a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? (list.Length > 0 ? list[0] : throw new SocketException((int)SocketError.HostNotFound));
        }

        mSocket = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        mSocket.Connect(addr, port);
        RemoteAddress = (IPEndPoint?)mSocket.RemoteEndPoint;
        LocalAddress = (IPEndPoint?)mSocket.LocalEndPoint;

        mKCP = new Kcp((uint)new Random().Next(1, int.MaxValue), rawSend);
        // normal:  0, 40, 2, 1 / fast: 0, 30, 2, 1 / fast2: 1, 20, 2, 1 / fast3: 1, 10, 2, 1
        mKCP.NoDelay(0, 30, 2, 1);
        mKCP.SetStreamMode(true);

        mRecvBuffer.Clear();
        mNextUpdateTime = 0;
    }

    /// <summary>소켓을 닫는다.</summary>
    public void Close()
    {
        if (mSocket != null)
        {
            mSocket.Close();
            mSocket = null;
            mRecvBuffer.Clear();
        }
    }

    private void rawSend(byte[] data, int length)
    {
        mSocket?.Send(data, length, SocketFlags.None);
    }

    /// <summary>데이터를 보낸다.</summary>
    /// <param name="data">보낼 데이터.</param>
    /// <param name="index">시작 위치.</param>
    /// <param name="length">길이.</param>
    /// <returns>
    /// 보낸 바이트 수. 소켓이 닫혀 있으면 -1, 송신 윈도가 가득 차 이번에 보내지 못했으면 0,
    /// KCP 가 오류를 반환하면 그 오류 코드(음수)를 그대로 돌려준다(진단 B-12).
    /// </returns>
    public int Send(byte[] data, int index, int length)
    {
        if (mSocket == null || mKCP == null)
            return -1;

        var waitsnd = mKCP.WaitSnd;
        if (waitsnd < mKCP.SndWnd && waitsnd < mKCP.RmtWnd)
        {
            var sendBytes = 0;
            do
            {
                var n = Math.Min((int)mKCP.Mss, length - sendBytes);
                // 진단 B-12: 반환값(오류 코드)을 무시하지 않는다.
                var ret = mKCP.Send(data, index + sendBytes, n);
                if (ret < 0)
                    return ret;
                sendBytes += n;
            } while (sendBytes < length);

            waitsnd = mKCP.WaitSnd;
            if (waitsnd >= mKCP.SndWnd || waitsnd >= mKCP.RmtWnd || !WriteDelay)
            {
                mKCP.Flush(false);
            }

            return length;
        }

        return 0;
    }

    /// <summary>도착한 데이터를 읽는다.</summary>
    /// <param name="data">대상 버퍼.</param>
    /// <param name="index">기록 시작 위치.</param>
    /// <param name="length">최대 길이.</param>
    /// <returns>읽은 바이트 수. 읽을 것이 없으면 0, 오류면 음수.</returns>
    public int Recv(byte[] data, int index, int length)
    {
        // 지난 호출에서 남겨 둔 데이터부터 넘긴다.
        if (mRecvBuffer.ReadableBytes > 0)
        {
            var recvBytes = Math.Min(mRecvBuffer.ReadableBytes, length);
            Buffer.BlockCopy(mRecvBuffer.RawBuffer, mRecvBuffer.ReaderIndex, data, index, recvBytes);
            mRecvBuffer.ReaderIndex += recvBytes;
            if (mRecvBuffer.ReaderIndex == mRecvBuffer.WriterIndex)
            {
                mRecvBuffer.Clear();
            }
            return recvBytes;
        }

        if (mSocket == null || mKCP == null)
            return -1;

        if (!mSocket.Poll(0, SelectMode.SelectRead))
        {
            return 0;
        }

        int rn;
        try
        {
            rn = mSocket.Receive(mRecvBuffer.RawBuffer, mRecvBuffer.WriterIndex, mRecvBuffer.WritableBytes, SocketFlags.None);
        }
        catch (SocketException ex)
        {
            // 진단 B-13: 라이브러리가 콘솔에 직접 쓰지 않는다. 콜백이 없으면 그대로 던진다.
            if (OnError == null) throw;
            OnError(ex);
            return -1;
        }

        if (rn <= 0)
        {
            return rn;
        }
        mRecvBuffer.WriterIndex += rn;

        var inputN = mKCP.Input(mRecvBuffer.RawBuffer, mRecvBuffer.ReaderIndex, mRecvBuffer.ReadableBytes, true, AckNoDelay);
        if (inputN < 0)
        {
            mRecvBuffer.Clear();
            return inputN;
        }
        mRecvBuffer.Clear();

        // 완성된 메시지를 전부 꺼낸다.
        for (; ; )
        {
            var size = mKCP.PeekSize();
            if (size < 0) break;

            mRecvBuffer.EnsureWritableBytes(size);

            var n = mKCP.Recv(mRecvBuffer.RawBuffer, mRecvBuffer.WriterIndex, size);
            if (n > 0) mRecvBuffer.WriterIndex += n;
        }

        // 받을 데이터가 있으면 재귀로 넘긴다.
        if (mRecvBuffer.ReadableBytes > 0)
        {
            return Recv(data, index, length);
        }

        return 0;
    }

    /// <summary>KCP 상태를 갱신한다. 게임 루프에서 매 틱 호출한다.</summary>
    public void Update()
    {
        if (mSocket == null || mKCP == null)
            return;

        var current = mKCP.CurrentMS;

        // 진단 B-02: 부호 없는 직접 비교(current >= next)는 약 49.7일 뒤 랩어라운드에서
        // 영원히 참이 되지 않아 Update 가 멈춘다. 반드시 부호 있는 차이로 비교한다.
        if (0 == mNextUpdateTime || (int)(current - mNextUpdateTime) >= 0)
        {
            mKCP.Update();
            mNextUpdateTime = mKCP.Check();
        }
    }
}
