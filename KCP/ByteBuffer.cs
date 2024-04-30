using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KcpProject;

class ByteBuffer : ICloneable
{
    //바이트버퍼
    private byte[] buf;
    //읽기 인덱스
    private int readIndex = 0;
    //쓰기 인덱스
    private int writeIndex = 0;
    //색인 마크 읽기
    private int markReadIndex = 0;
    //색인 마크 쓰기
    private int markWirteIndex = 0;
    //바이트 배열의 길이
    private int capacity;

    //객체 풀
    private static List<ByteBuffer> pool = new List<ByteBuffer>();
    private static int poolMaxCount = 200;

    //이 개체가 풀링 되어 있는지 여부
    private bool isPool = false;

    /// <param name="capacity">초기 용량</param>
    private ByteBuffer(int capacity)
    {
        this.buf = new byte[capacity];
        this.capacity = capacity;
        this.readIndex = 0;
        this.writeIndex = 0;
    }

    /// <param name="bytes">초기 바이트 배열</param>
    private ByteBuffer(byte[] bytes)
    {
        this.buf = new byte[bytes.Length];
        Array.Copy(bytes, 0, buf, 0, buf.Length);
        this.capacity = buf.Length;
        this.readIndex = 0;
        this.writeIndex = bytes.Length + 1;
    }

    /// <summary>
    /// 용량 길이의 ByteBuffer 객체를 만든다
    /// </summary>
    /// <param name="capacity">초기 용량</param>
    /// <param name="fromPool">
    /// true는 풀링된 바이트버퍼 객체를 가져오는 것을 의미하며, 풀링된 객체는 풀에 푸시되기 전에 Dispose를 호출해야 하며 이 메서드는 스레드 안전하다.
    /// true이면 풀에서 가져온 오브젝트의 실제 용량 값이다.
    /// </param>
    /// <returns>ByteBuffer 객체</returns>
    public static ByteBuffer Allocate(int capacity, bool fromPool = false)
    {
        if (!fromPool)
        {
            return new ByteBuffer(capacity);
        }
        lock (pool)
        {
            ByteBuffer bbuf;
            if (pool.Count == 0)
            {
                bbuf = new ByteBuffer(capacity)
                {
                    isPool = true
                };
                return bbuf;
            }
            int lastIndex = pool.Count - 1;
            bbuf = pool[lastIndex];
            pool.RemoveAt(lastIndex);
            if (!bbuf.isPool)
            {
                bbuf.isPool = true;
            }
            return bbuf;
        }
    }

    /// <summary>
    /// 바이트 버퍼를 바이트 버퍼로 사용하는 ByteBuffer 객체를 생성하는 것은 일반적으로 권장되지 않는다
    /// </summary>
    /// <param name="bytes">초기 바이트 배열</param>
    /// <param name="fromPool">
    /// true는 풀링된 바이트버퍼 객체를 가져오는 것을 의미하며 풀링된 객체는 Dispose가 호출된 후 풀로 푸시되어야 하며 이 메서드는 스레드 안전하다
    /// </param>
    /// <returns>ByteBuffer 객체</returns>
    public static ByteBuffer Allocate(byte[] bytes, bool fromPool = false)
    {
        if (!fromPool)
        {
            return new ByteBuffer(bytes);
        }
        lock (pool)
        {
            ByteBuffer bbuf;
            if (pool.Count == 0)
            {
                bbuf = new ByteBuffer(bytes)
                {
                    isPool = true
                };
                return bbuf;
            }
            int lastIndex = pool.Count - 1;
            bbuf = pool[lastIndex];
            bbuf.WriteBytes(bytes);
            pool.RemoveAt(lastIndex);
            if (!bbuf.isPool)
            {
                bbuf.isPool = true;
            }
            return bbuf;
        }
    }

    /// <summary>
    /// 이 값에 따라 길이 = 7, 반환값 8, 길이 = 12, 반환값 16과 같이 이 길이보다 가장 가까운 두 배의 수를 결정한다
    /// </summary>
    /// <param name="value">참고 용량</param>
    /// <returns>기준 용량에 가장 가까운 2승의 수</returns>
    private int FixLength(int value)
    {
        if (value == 0)
        {
            return 1;
        }
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
        //int n = 2;
        //int b = 2;
        //while (b < length)
        //{
        //    b = 2 << n;
        //    n++;
        //}
        //return b;
    }

    /// <summary>
    /// 바이트 배열을 뒤집는다. 로컬 바이트 시퀀스가 빅엔디언이라면 리틀엔디언 시퀀스로 뒤집는다.
    /// </summary>
    /// <param name="bytes">빅엔디언 바이트 순서로 반전할 바이트 배열</param>
    /// <returns>낮은 바이트 시퀀스의 바이트</returns>
    private byte[] Flip(byte[] bytes)
    {
        //if (BitConverter.IsLittleEndian)
        //{
        //    Array.Reverse(bytes);
        //}
        return bytes;
    }

    /// <summary>
    /// 내부 바이트 캐시 배열의 크기를 결정한다
    /// </summary>
    /// <param name="currLen">현재 용량</param>
    /// <param name="futureLen">향후 용량</param>
    /// <returns>현재 버퍼의 최대 용량</returns>
    private int FixSizeAndReset(int currLen, int futureLen)
    {
        if (futureLen > currLen)
        {
            //내부 바이트 버퍼 크기를 원래 크기의 거듭제곱의 2배로 결정한다
            int size = FixLength(currLen) * 2;
            if (futureLen > size)
            {
                //내부 바이트 버퍼의 크기를 미래 크기의 2배를 2의 거듭제곱으로 결정한다
                size = FixLength(futureLen) * 2;
            }
            byte[] newbuf = new byte[size];
            Array.Copy(buf, 0, newbuf, 0, currLen);
            buf = newbuf;
            capacity = size;
        }
        return futureLen;
    }

    /// <summary>
    /// 쓰기 가능한 바이트가 이 정도인지 확인한다
    /// </summary>
    /// <param name="minBytes"></param>
    public void EnsureWritableBytes(int minBytes)
    {
        // 쓸 수 있는 공간이 충분하지 않으면
        if (WritableBytes < minBytes)
        {

            // 공간 우선 순위 지정
            if (ReaderIndex >= minBytes)
            {
                // 사용 가능한 공간 정리
                TrimReadedBytes();
            }
            else
            {
                // 공간이 부족하면 메모리 재할당
                FixSizeAndReset(buf.Length, buf.Length + minBytes);
            }
        }
    }

    public void TrimReadedBytes()
    {
        Buffer.BlockCopy(buf, readIndex, buf, 0, writeIndex - readIndex);
        writeIndex -= readIndex;
        readIndex = 0;
    }

    /// <summary>
    /// startIndex에서 시작하여 길이가 끝나는 바이트 배열을 이 캐시에 쓴다.
    /// </summary>
    /// <param name="bytes">기록할 바이트</param>
    /// <param name="startIndex">쓰기를 위한 시작 위치</param>
    /// <param name="length">쓰기 길이</param>
    public void WriteBytes(byte[] bytes, int startIndex, int length)
    {
        if (length <= 0 || startIndex < 0) return;

        int total = length + writeIndex;
        int len = buf.Length;
        FixSizeAndReset(len, total);
        Array.Copy(bytes, startIndex, buf, writeIndex, length);
        writeIndex = total;
    }

    /// <summary>
    /// 0부터 길이까지 바이트 배열의 요소를 버퍼에 쓴다
    /// </summary>
    public void WriteBytes(byte[] bytes, int length)
    {
        WriteBytes(bytes, 0, length);
    }

    /// <summary>
    /// 전체 바이트 배열을 버퍼에 쓴다
    /// </summary>
    public void WriteBytes(byte[] bytes)
    {
        WriteBytes(bytes, bytes.Length);
    }

    /// <summary>
    /// 바이트버퍼의 유효한 바이트 영역을 이 버퍼에 쓴다
    /// </summary>
    public void Write(ByteBuffer buffer)
    {
        if (buffer == null) return;
        if (buffer.ReadableBytes <= 0) return;
        WriteBytes(buffer.ToArray());
    }

    public void WriteShort(short value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteUshort(ushort value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteInt(int value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteUint(uint value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteLong(long value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteUlong(ulong value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteFloat(float value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteByte(byte value)
    {
        int afterLen = writeIndex + 1;
        int len = buf.Length;
        FixSizeAndReset(len, afterLen);
        buf[writeIndex] = value;
        writeIndex = afterLen;
    }

    public void WriteByte(int value)
    {
        byte b = (byte)value;
        WriteByte(b);
    }

    public void WriteDouble(double value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteChar(char value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public void WriteBoolean(bool value)
    {
        WriteBytes(Flip(BitConverter.GetBytes(value)));
    }

    public byte ReadByte()
    {
        byte b = buf[readIndex];
        readIndex++;
        return b;
    }

    /// <summary>
    /// 인덱스에서 len 길이의 바이트 가져오기
    /// </summary>
    /// <param name="index"></param>
    /// <param name="len"></param>
    /// <returns></returns>
    private byte[] Get(int index, int len)
    {
        byte[] bytes = new byte[len];
        Array.Copy(buf, index, bytes, 0, len);
        return Flip(bytes);
    }

    /// <summary>
    /// 읽기 인덱스 위치에서 시작하여 길이가 len인 바이트 배열을 읽는다
    /// </summary>
    /// <param name="len">읽을 바이트의 길이</param>
    /// <returns>바이트 배열</returns>
    private byte[] Read(int len)
    {
        byte[] bytes = Get(readIndex, len);
        readIndex += len;
        return bytes;
    }

    public ushort ReadUshort()
    {
        return BitConverter.ToUInt16(Read(2), 0);
    }

    public short ReadShort()
    {
        return BitConverter.ToInt16(Read(2), 0);
    }

    public uint ReadUint()
    {
        return BitConverter.ToUInt32(Read(4), 0);
    }

    public int ReadInt()
    {
        return BitConverter.ToInt32(Read(4), 0);
    }

    public ulong ReadUlong()
    {
        return BitConverter.ToUInt64(Read(8), 0);
    }

    public long ReadLong()
    {
        return BitConverter.ToInt64(Read(8), 0);
    }

    public float ReadFloat()
    {
        return BitConverter.ToSingle(Read(4), 0);
    }

    public double ReadDouble()
    {
        return BitConverter.ToDouble(Read(8), 0);
    }

    public char ReadChar()
    {
        return BitConverter.ToChar(Read(2), 0);
    }

    public bool ReadBoolean()
    {
        return BitConverter.ToBoolean(Read(1), 0);
    }

    /// <summary>
    /// 읽기 인덱스 위치에서 disbytes 대상 바이트 배열로 len 길이의 바이트를 읽는다
    /// </summary>
    /// <param name="disbytes">읽은 바이트는 이 바이트 배열에 저장된다</param>
    /// <param name="disstart">대상 바이트 배열의 쓰기 인덱스</param>
    /// <param name="len">읽은 바이트의 길이</param>
    public void ReadBytes(byte[] disbytes, int disstart, int len)
    {
        int size = disstart + len;
        for (int i = disstart; i < size; i++)
        {
            disbytes[i] = this.ReadByte();
        }
    }

    public byte[] ReadBytes(int len)
    {
        return ReadBytes(readIndex, len);
    }

    public byte[] ReadBytes(int index, int len)
    {
        if (ReadableBytes < len)
            throw new Exception("no more readable bytes");

        var buffer = new byte[len];
        Array.Copy(buf, index, buffer, 0, len);
        readIndex += len;
        return buffer;
    }

    public byte GetByte(int index)
    {
        return buf[index];
    }

    public byte GetByte()
    {
        return GetByte(readIndex);
    }

    /// <summary>
    /// 데이터 내용을 변경하지 않고 배정밀도 부동 소수점 데이터 가져오기
    /// </summary>
    public double GetDouble(int index)
    {
        return BitConverter.ToDouble(Get(index, 8), 0);
    }

    /// <summary>
    /// 데이터 내용을 변경하지 않고 배정밀도 부동 소수점 데이터 가져오기
    /// </summary>
    /// <returns></returns>
    public double GetDouble()
    {
        return GetDouble(readIndex);
    }

    public float GetFloat(int index)
    {
        return BitConverter.ToSingle(Get(index, 4), 0);
    }

    public float GetFloat()
    {
        return GetFloat(readIndex);
    }

    public long GetLong(int index)
    {
        return BitConverter.ToInt64(Get(index, 8), 0);
    }

    public long GetLong()
    {
        return GetLong(readIndex);
    }

    public ulong GetUlong(int index)
    {
        return BitConverter.ToUInt64(Get(index, 8), 0);
    }

    public ulong GetUlong()
    {
        return GetUlong(readIndex);
    }

    public int GetInt(int index)
    {
        return BitConverter.ToInt32(Get(index, 4), 0);
    }

    public int GetInt()
    {
        return GetInt(readIndex);
    }

    public uint GetUint(int index)
    {
        return BitConverter.ToUInt32(Get(index, 4), 0);
    }

    public uint GetUint()
    {
        return GetUint(readIndex);
    }

    public int GetShort(int index)
    {
        return BitConverter.ToInt16(Get(index, 2), 0);
    }

    public int GetShort()
    {
        return GetShort(readIndex);
    }

    public int GetUshort(int index)
    {
        return BitConverter.ToUInt16(Get(index, 2), 0);
    }

    public int GetUshort()
    {
        return GetUshort(readIndex);
    }

    public char GetChar(int index)
    {
        return BitConverter.ToChar(Get(index, 2), 0);
    }

    public char GetChar()
    {
        return GetChar(readIndex);
    }

    public bool GetBoolean(int index)
    {
        return BitConverter.ToBoolean(Get(index, 1), 0);
    }

    public bool GetBoolean()
    {
        return GetBoolean(readIndex);
    }

    /// <summary>
    /// 읽은 바이트를 지우고 버퍼를 다시 작성한다
    /// </summary>
    public void DiscardReadBytes()
    {
        if (readIndex <= 0) return;
        int len = buf.Length - readIndex;
        byte[] newbuf = new byte[len];
        Array.Copy(buf, readIndex, newbuf, 0, len);
        buf = newbuf;
        writeIndex -= readIndex;
        markReadIndex -= readIndex;
        if (markReadIndex < 0)
        {
            //markReadIndex = readIndex;
            markReadIndex = 0;
        }
        markWirteIndex -= readIndex;
        if (markWirteIndex < 0 || markWirteIndex < readIndex || markWirteIndex < markReadIndex)
        {
            markWirteIndex = writeIndex;
        }
        readIndex = 0;
    }

    /// <summary>
    /// 읽기 포인터 위치 설정/조회
    /// </summary>
    public int ReaderIndex
    {
        get
        {
            return readIndex;
        }
        set
        {
            if (value < 0) return;
            readIndex = value;
        }
    }

    /// <summary>
    /// 쓰기 포인터 위치 설정/조회
    /// </summary>
    public int WriterIndex
    {
        get
        {
            return writeIndex;
        }
        set
        {
            if (value < 0) return;
            writeIndex = value;
        }
    }

    /// <summary>
    /// 읽기 인덱스 위치를 표시한다
    /// </summary>
    public void MarkReaderIndex()
    {
        markReadIndex = readIndex;
    }

    /// <summary>
    /// 쓰기 인덱스 위치를 표시한다
    /// </summary>
    public void MarkWriterIndex()
    {
        markWirteIndex = writeIndex;
    }

    /// <summary>
    /// 읽기 인덱스 위치를 표시된 읽기 인덱스 위치로 재설정한다
    /// </summary>
    public void ResetReaderIndex()
    {
        readIndex = markReadIndex;
    }

    /// <summary>
    /// 쓰기 인덱스 위치를 표시된 쓰기 인덱스 위치로 재설정한다
    /// </summary>
    public void ResetWriterIndex()
    {
        writeIndex = markWirteIndex;
    }

    /// <summary>
    /// 읽을 수 있는 유효 바이트 수
    /// </summary>
    public int ReadableBytes
    {
        get
        {
            return writeIndex - readIndex;
        }
    }

    /// <summary>
    /// 쓸 수 있는 유효 바이트 수
    /// </summary>
    public int WritableBytes
    {
        get
        {
            return capacity - writeIndex;
        }
    }

    public int Capacity
    {
        get
        {
            return this.capacity;
        }
    }

    public byte[] RawBuffer
    {
        get
        {
            return buf;
        }
    }

    /// <summary>
    /// 읽기 가능한 바이트 배열 가져오기
    /// </summary>
    /// <returns>바이트 데이터</returns>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[writeIndex - readIndex];
        Array.Copy(buf, readIndex, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// 간단한 데이터 유형
    /// </summary>
    public enum DataType
    {
        BYTE = 1,
        SHORT = 2,
        INT = 3,
        LONG = 4
    }

    /// <summary>
    /// 데이터 쓰기
    /// </summary>
    private void WriteValue(int value, DataType type)
    {
        switch (type)
        {
            case DataType.BYTE:
                this.WriteByte(value);
                break;
            case DataType.SHORT:
                this.WriteShort((short)value);
                break;
            case DataType.LONG:
                this.WriteLong((long)value);
                break;
            default:
                this.WriteInt(value);
                break;
        }
    }

    private int ReadValue(DataType type)
    {
        switch (type)
        {
            case DataType.BYTE:
                return (int)ReadByte();
            case DataType.SHORT:
                return (int)ReadShort();
            case DataType.INT:
                return (int)ReadInt();
            case DataType.LONG:
                return (int)ReadLong();
            default:
                return -1;
        }
    }

    /// <summary>
    ///  UTF-8 문자열 쓰기, UTF-8 문자열에는 상위 또는 하위 바이트 순서 문제가 없다
    /// <para>쓰기 버퍼의 구조는 문자열 바이트의 길이(lenType으로 지정된 유형) + 문자열 바이트의 배열 이다</para>
    /// </summary>
    /// <param name="content">기록할 문자열</param>
    /// <param name="lenType">기록할 문자열의 길이 타입</param>
    public void WriteUTF8String(string content, DataType lenType)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        int max;
        if (lenType == DataType.BYTE)
        {
            WriteByte(bytes.Length);
            max = byte.MaxValue;
        }
        else if (lenType == DataType.SHORT)
        {
            WriteShort((short)bytes.Length);
            max = short.MaxValue;
        }
        else
        {
            WriteInt(bytes.Length);
            max = int.MaxValue;
        }
        if (bytes.Length > max)
        {
            WriteBytes(bytes, 0, max);
        }
        else
        {
            WriteBytes(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// 문자열 바이트 길이와 문자열 바이트 데이터를 짧게 쓴다.
    /// </summary>
    /// <param name="content"></param>
    public void WriteUTF(string content)
    {
        this.WriteUTF8String(content, DataType.SHORT);
    }

    /// <summary>
    /// 상위 및 하위 바이트 순서 문제 없이 UTF-8 문자열, UTF-8 문자열 읽기
    /// </summary>
    /// <param name="len">읽을 문자열의 길이</param>
    /// <returns>문자열</returns>
    public string ReadUTF8String(int len)
    {
        byte[] bytes = new byte[len];
        this.ReadBytes(bytes, 0, len);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 상위 및 하위 바이트 순서 문제 없이 UTF-8 문자열, UTF-8 문자열 읽기
    /// </summary>
    /// <param name="lenType">字符串长度类型</param>
    /// <returns>문자열</returns>
    public string ReadUTF8String(DataType lenType)
    {
        int len = ReadValue(lenType);
        return ReadUTF8String(len);
    }

    /// <summary>
    /// short 타입의 문자열의 바이트 길이를 읽은 다음 이 길이에 따라 해당 바이트 수의 데이터를 읽고 문자열로 변환한다
    /// </summary>
    /// <returns>UTF-8 문자열</returns>
    public string ReadUTF()
    {
        return this.ReadUTF8String(DataType.SHORT);
    }

    /// <summary>
    /// 읽기 데이터를 제외한 원본 개체의 데이터를 변경하지 않고 원본 개체와 동일한 데이터로 개체를 복제합니다.
    /// </summary>
    /// <returns></returns>
    public ByteBuffer Copy()
    {
        if (buf == null)
        {
            return new ByteBuffer(16);
        }
        if (readIndex < writeIndex)
        {
            byte[] newbytes = new byte[writeIndex - readIndex];
            Array.Copy(buf, readIndex, newbytes, 0, newbytes.Length);
            ByteBuffer buffer = new ByteBuffer(newbytes.Length);
            buffer.WriteBytes(newbytes);
            buffer.isPool = this.isPool;
            return buffer;
        }
        return new ByteBuffer(16);
    }

    /// <summary>
    /// 읽기 데이터를 포함하여 원본 개체의 데이터를 변경하지 않고 원본 개체와 동일한 데이터로 딥 카피합니다.
    /// </summary>
    /// <returns></returns>
    public object Clone()
    {
        if (buf == null)
        {
            return new ByteBuffer(16);
        }
        ByteBuffer newBuf = new ByteBuffer(buf)
        {
            capacity = this.capacity,
            readIndex = this.readIndex,
            writeIndex = this.writeIndex,
            markReadIndex = this.markReadIndex,
            markWirteIndex = this.markWirteIndex,
            isPool = this.isPool
        };
        return newBuf;
    }

    /// <summary>
    /// 모든 바이트의 데이터 반복
    /// </summary>
    /// <param name="action"></param>
    public void ForEach(Action<byte> action)
    {
        for (int i = 0; i < this.ReadableBytes; i++)
        {
            action.Invoke(this.buf[i]);
        }
    }

    public void Clear()
    {
        readIndex = 0;
        writeIndex = 0;
        markReadIndex = 0;
        markWirteIndex = 0;
        capacity = buf.Length;
    }

    /// <summary>
    /// 객체를 해제하고 바이트 캐시 배열을 지우며, 객체가 풀링 가능한 경우 이 메서드를 호출하면 다음 호출을 위해 객체를 풀로 푸시한다
    /// </summary>
    public void Dispose()
    {
        if (isPool) {
            lock (pool) {
                if (pool.Count < poolMaxCount) {
                    this.Clear();
                    pool.Add(this);
                    return;
                }
            }
        }

        readIndex = 0;
        writeIndex = 0;
        markReadIndex = 0;
        markWirteIndex = 0;
        capacity = 0;
        buf = null;
    }
}