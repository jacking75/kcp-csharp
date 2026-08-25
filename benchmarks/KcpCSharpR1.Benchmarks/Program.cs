using System.Reflection;
using BenchmarkDotNet.Running;

namespace KcpCSharpR1.Benchmarks;

/// <summary>벤치마크 진입점. 예: dotnet run -c Release --project benchmarks/KcpCSharpR1.Benchmarks -- --filter '*Roundtrip*'</summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
    }
}
