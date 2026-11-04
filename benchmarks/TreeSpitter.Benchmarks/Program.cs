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

if (args.Length > 0 && args[0] == "quick-incremental")
{
    // quick-incremental [languages]: times the incremental parses without BenchmarkDotNet
    foreach (var language in args.Length > 1 ? args[1].Split(',') : Grammars.Names)
    {
        var benchmark = new IncrementalBenchmarks { Language = language, Size = 100_000 };
        benchmark.Setup();
        var nativeFull = Measure(benchmark.NativeFull);
        var nativeIncremental = Measure(benchmark.NativeIncremental);
        var full = Measure(benchmark.Full);
        var incremental = Measure(benchmark.Incremental);
        Console.WriteLine($"{language,-5}: native full {nativeFull,8:F3} ms   incremental {nativeIncremental,8:F3} ms   treespitter full {full,8:F3} ms   incremental {incremental,8:F3} ms ({incremental / nativeIncremental:F2} of native)");
        benchmark.Cleanup();
    }

    return;
}

if (args.Length > 0 && args[0] == "incremental-trace")
{
    // incremental-trace <language> <corpus test> <trial>: logs the native parses of a trial of incremental-check
    IncrementalCheck.Trace(args[1], args[2], int.Parse(args[3]));
    return;
}

if (args.Length > 0 && args[0] == "incremental-check")
{
    // incremental-check [languages] [trials]: compares the incremental parses of random edits with the native ones
    var trials = args.Length > 2 ? int.Parse(args[2]) : 6;
    foreach (var language in args.Length > 1 ? args[1].Split(',') : Grammars.Names)
    {
        IncrementalCheck.Run(language, trials);
    }

    return;
}

if (args.Length > 0 && args[0] == "file")
{
    // file <language> <path> [noloops]: times the parsers on a file
    if (args.Length > 3 && args[3] == "noloops")
    {
        typeof(TreeSpitter.Parser).Assembly.GetType("TreeSpitter.LexAutomaton")!
            .GetProperty("MinimumSelfLoopSize", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, 1000);
    }

    var bytes = File.ReadAllBytes(args[2]);
    var text = System.Text.Encoding.UTF8.GetString(bytes);
    var parser = new TreeSpitter.Parser(Grammars.Language(args[1]));
    using var nativeLanguage = new TreeSitter.Language(args[1]);
    using var nativeParser = new TreeSitter.Parser(nativeLanguage);
    using var utf8Parser = new NativeUtf8Parser(args[1]);
    using (var nativeTree = nativeParser.Parse(text))
    using (var tree = parser.Parse(bytes))
    {
        var utf16 = nativeTree!.RootNode.ToString();
        var expected = utf8Parser.ToSExpression(bytes);
        Console.WriteLine(utf16 == expected ? "native UTF-16 and UTF-8 trees are the same" : "native UTF-16 and UTF-8 trees differ");
        var actual = tree.ToSExpression();
        if (expected == actual)
        {
            Console.WriteLine("same trees");
        }
        else
        {
            var index = 0;
            while (index < Math.Min(expected.Length, actual.Length) && expected[index] == actual[index])
            {
                index++;
            }

            var start = Math.Max(0, index - 300);
            Console.WriteLine($"DIFFERENT TREES at {index}:\n  native: ...{expected.Substring(start, Math.Min(500, expected.Length - start))}\n  ours:   ...{actual.Substring(start, Math.Min(500, actual.Length - start))}");
        }
    }

    var native = Measure(() => { using var tree = nativeParser.Parse(text); });
    var nativeUtf8 = Measure(() => utf8Parser.Parse(bytes));
    var vm = Measure(() => { using var tree = parser.Parse(bytes); });
    Console.WriteLine($"{args[1]} {bytes.Length} bytes: native {native:F3} ms   native UTF-8 {nativeUtf8:F3} ms   vm {vm:F3} ms ({vm / nativeUtf8:F2} of UTF-8)");
    return;
}

if (Environment.GetEnvironmentVariable("NO_KEYWORD_TABLE") is not null)
{
    typeof(TreeSpitter.Parser).Assembly.GetType("TreeSpitter.Language")!
        .GetProperty("DisableKeywordTable", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(null, true);
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
