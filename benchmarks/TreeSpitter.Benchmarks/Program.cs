using System.Diagnostics;
using BenchmarkDotNet.Running;
using TreeSpitter.Benchmarks;

if (args.Length > 0 && args[0] == "profile")
{
    // Parses in a loop, to profile with dotnet-trace: profile <language> <size> <vm|generated> <seconds>
    var benchmark = new NativeComparisonBenchmarks { Language = args[1], Size = int.Parse(args[2]) };
    benchmark.Setup();
    var stopwatch = Stopwatch.StartNew();
    var count = 0;
    while (stopwatch.Elapsed.TotalSeconds < double.Parse(args[4]))
    {
        if (args[3] == "vm")
        {
            benchmark.Vm();
        }
        else
        {
            benchmark.Generated();
        }
        count++;
    }

    Console.WriteLine($"{count} parses, {stopwatch.Elapsed.TotalMilliseconds / count:F3} ms per parse");
    return;
}

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
            var vm = Measure(benchmark.Vm);
            var generated = Measure(benchmark.Generated);
            Console.WriteLine($"{language,-5} {size,7}: native {native,8:F3} ms   vm {vm,8:F3} ms ({vm / native,4:F2})   generated {generated,8:F3} ms ({generated / native,4:F2})");
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
