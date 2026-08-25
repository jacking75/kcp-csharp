using System;
using System.Globalization;
using KcpProject.Transport;

namespace LoadTest;

/// <summary>실행 모드.</summary>
internal enum RunMode
{
    /// <summary>부하를 받아 에코하는 쪽.</summary>
    Server,

    /// <summary>세션을 잔뜩 만들어 부하를 거는 쪽.</summary>
    Client,
}

/// <summary>
/// 명령줄 옵션.
/// </summary>
/// <remarks>
/// 외부 파서 패키지를 쓰지 않는다(로드맵: 샘플은 MonoGame 외 외부 패키지 금지).
/// </remarks>
internal sealed class Options
{
    /// <summary>로드맵 12.4절 포트 배정표: LoadTest = 27010.</summary>
    public const int DefaultPort = 27010;

    /// <summary>실행 모드.</summary>
    public RunMode Mode { get; private set; }

    /// <summary>서버 주소(클라 모드).</summary>
    public string Host { get; private set; } = "127.0.0.1";

    /// <summary>UDP 포트.</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>동시 세션 수.</summary>
    public int Sessions { get; private set; } = 100;

    /// <summary>세션당 초당 메시지 수.</summary>
    public int Rate { get; private set; } = 20;

    /// <summary>메시지 크기(바이트).</summary>
    public int Size { get; private set; } = 256;

    /// <summary>측정 시간(초).</summary>
    public int DurationSeconds { get; private set; } = 60;

    /// <summary>true 면 비신뢰 채널로 보낸다.</summary>
    public bool Unreliable { get; private set; }

    /// <summary>NoDelay 프리셋 이름.</summary>
    public string Preset { get; private set; } = "fast2";

    /// <summary>CSV 결과 파일 경로. null 이면 저장하지 않는다.</summary>
    public string? CsvPath { get; private set; }

    /// <summary>세션당 소켓 버퍼 크기(바이트). 세션이 많을 때 메모리를 아끼려고 낮춘다.</summary>
    public int SocketBufferSize { get; private set; } = 64 * 1024;

    /// <summary>사용법.</summary>
    public static string Usage => """
        사용법:
          LoadTest server [--port 27010] [--preset fast2]
          LoadTest client [--host 127.0.0.1] [--port 27010] [--sessions 1000] [--rate 20]
                          [--size 256] [--duration 60] [--unreliable] [--preset fast2]
                          [--csv result.csv] [--sockbuf 65536]

        프리셋: normal(0,40,2,1) / fast(0,30,2,1) / fast2(1,20,2,1) / fast3(1,10,2,1)
        """;

    /// <summary>명령줄을 해석한다.</summary>
    /// <param name="args">인자.</param>
    /// <param name="options">결과.</param>
    /// <param name="error">실패 사유.</param>
    /// <returns>성공하면 true.</returns>
    public static bool TryParse(string[] args, out Options options, out string error)
    {
        options = new Options();
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "모드를 지정하세요(server 또는 client).";
            return false;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "server": options.Mode = RunMode.Server; break;
            case "client": options.Mode = RunMode.Client; break;
            default:
                error = $"알 수 없는 모드: {args[0]}";
                return false;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i].ToLowerInvariant();

            if (key == "--unreliable")
            {
                options.Unreliable = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"{args[i]} 에 값이 없습니다.";
                return false;
            }

            var value = args[++i];

            switch (key)
            {
                case "--host": options.Host = value; break;
                case "--preset": options.Preset = value.ToLowerInvariant(); break;
                case "--csv": options.CsvPath = value; break;

                case "--port": if (!TryInt(value, out var p, out error)) return false; options.Port = p; break;
                case "--sessions": if (!TryInt(value, out var s, out error)) return false; options.Sessions = s; break;
                case "--rate": if (!TryInt(value, out var r, out error)) return false; options.Rate = r; break;
                case "--size": if (!TryInt(value, out var z, out error)) return false; options.Size = z; break;
                case "--duration": if (!TryInt(value, out var d, out error)) return false; options.DurationSeconds = d; break;
                case "--sockbuf": if (!TryInt(value, out var b, out error)) return false; options.SocketBufferSize = b; break;

                default:
                    error = $"알 수 없는 옵션: {args[i - 1]}";
                    return false;
            }
        }

        if (options.Sessions <= 0) { error = "--sessions 는 1 이상이어야 합니다."; return false; }
        if (options.Rate <= 0) { error = "--rate 는 1 이상이어야 합니다."; return false; }
        if (options.Size < 8) { error = "--size 는 8 이상이어야 합니다(타임스탬프 8바이트를 담습니다)."; return false; }
        if (options.DurationSeconds <= 0) { error = "--duration 은 1 이상이어야 합니다."; return false; }

        return true;
    }

    private static bool TryInt(string text, out int value, out string error)
    {
        error = string.Empty;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        error = $"숫자가 아닙니다: {text}";
        return false;
    }

    /// <summary>프리셋 이름을 KCP NoDelay 인자로 바꿔 세션 옵션에 적용한다.</summary>
    /// <param name="target">적용 대상.</param>
    /// <exception cref="ArgumentException">알 수 없는 프리셋인 경우.</exception>
    public void ApplyPreset(KcpSessionOptions target)
    {
        (int nodelay, int interval, int resend, int nc) preset = Preset switch
        {
            "normal" => (0, 40, 2, 1),
            "fast" => (0, 30, 2, 1),
            "fast2" => (1, 20, 2, 1),
            "fast3" => (1, 10, 2, 1),
            _ => throw new ArgumentException($"알 수 없는 프리셋: {Preset}"),
        };

        target.NoDelay = preset.nodelay;
        target.IntervalMs = preset.interval;
        target.FastResend = preset.resend;
        target.NoCongestionControl = preset.nc;
    }
}
