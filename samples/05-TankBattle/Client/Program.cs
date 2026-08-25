using System;

namespace TankBattle.Client;

/// <summary>샘플 ⑤ TankBattle 클라이언트(MonoGame) 진입점.</summary>
internal static class Program
{
    /// <summary>진입점.</summary>
    /// <param name="args">[서버IP] [닉네임]</param>
    [STAThread]
    private static void Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var nick = args.Length > 1 ? args[1] : $"t{Environment.ProcessId % 1000}";

        using var game = new TankBattleGame(host, nick);
        game.Run();
    }
}
