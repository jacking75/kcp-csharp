using System;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var port) || port < 27001 || port > 27198)
        {
            Console.Error.WriteLine("Usage: PackageConsumer <expected framework> <port: 27001..27198>");
            return 2;
        }

        // Dispose/Stop 자체가 멈추더라도 실행 프로세스는 제한 시간 뒤 실패로 종료한다.
        var probe = Task.Run(() => PackageProbe.RunAsync(args[0], port));
        if (await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false) != probe)
        {
            Console.Error.WriteLine("Package consumer exceeded its 20-second process deadline.");
            return 1;
        }

        try
        {
            await probe.ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
