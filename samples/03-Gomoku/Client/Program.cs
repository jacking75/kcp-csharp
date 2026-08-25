using System;

namespace Gomoku.Client;

/// <summary>샘플 ③ 오목 클라이언트(MonoGame) 진입점.</summary>
internal static class Program
{
    /// <summary>
    /// 진입점.
    /// </summary>
    /// <param name="args">[서버IP] [닉네임] [재접속토큰]</param>
    [STAThread]
    private static void Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var nick = args.Length > 1 ? args[1] : $"player{Environment.ProcessId % 1000}";
        var token = args.Length > 2 ? args[2] : null;

        using var game = new GomokuGame(host, nick, token);
        game.Run();
    }
}
