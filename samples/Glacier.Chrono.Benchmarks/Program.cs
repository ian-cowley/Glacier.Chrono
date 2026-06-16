using BenchmarkDotNet.Running;

namespace Glacier.Chrono.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<ChronoBenchmarks>();
    }
}
