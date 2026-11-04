using System.Diagnostics;
using BenchmarkDotNet.Running;
using TreeSpitter.Benchmarks;

if (args.Length > 0 && args[0] == "quick")
{
    // A quick comparison without BenchmarkDotNet, to iterate on optimizations: checks the trees and times the parsers
    var sizes = args.Length > 1 ? args[1].Split(',').Select(int.Parse).ToArray() : [10_000, 100_000];
    var languages = args.Length > 2 ? args[2].Split(',') : Grammars.Names;
    foreach (var language in languages)
    {
        foreach (var size in sizes)
        {
            var benchmark = new NativeComparisonBenchmarks { Language = language, Size = size };
            benchmark.Setup();
            var native = Measure(benchmark.Native);
            var vm = Measure(() => benchmark.Vm());
            Console.WriteLine($"{language,-5} {size,7}: native {native,8:F3} ms   vm {vm,8:F3} ms   ratio {vm / native,5:F2}");
            benchmark.Cleanup();
        }
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

static double Measure(Action action)
{
    for (var i = 0; i < 20; i++)
    {
        action();
    }

    var best = double.MaxValue;
    for (var round = 0; round < 5; round++)
    {
        var count = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 200)
        {
            action();
            count++;
        }

        best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds / count);
    }

    return best;
}
