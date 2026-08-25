using System.Reflection;
using BenchmarkDotNet.Running;

namespace KcpProject.Benchmarks;

/// <summary>벤치마크 진입점. 예: dotnet run -c Release --project benchmarks/KcpProject.Benchmarks -- --filter '*Roundtrip*'</summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
    }
}
