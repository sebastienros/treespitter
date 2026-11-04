using System.Text;
using BenchmarkDotNet.Attributes;
using NativeLanguage = TreeSitter.Language;
using NativeParser = TreeSitter.Parser;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// TreeSpitter against the native tree-sitter parsers of the TreeSitter.DotNet package, on the same inputs: through the
/// binding (which parses strings as UTF-16) and directly on the UTF-8 input. The setup checks that the trees are the same. The memory columns only count managed allocations: the native trees are
/// allocated by tree-sitter.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class NativeComparisonBenchmarks
{
    private Parser _parser = null!;
    private Parser _generatedParser = null!;
    private NativeLanguage _nativeLanguage = null!;
    private NativeParser _nativeParser = null!;
    private NativeUtf8Parser _nativeUtf8Parser = null!;
    private string _text = "";
    private byte[] _utf8 = [];

    /// <summary>The languages compared.</summary>
    public static IEnumerable<string> Languages => Grammars.Names;

    /// <summary>The language of the benchmark.</summary>
    [ParamsSource(nameof(Languages))]
    public string Language { get; set; } = "";

    /// <summary>The approximate size of the input, in chars.</summary>
    [Params(10_000, 100_000)]
    public int Size { get; set; }

    /// <summary>Builds both parsers and checks that they agree on the input.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _text = Grammars.Input(Language, Size);
        _utf8 = Encoding.UTF8.GetBytes(_text);
        _parser = new Parser(Grammars.Language(Language));
        _generatedParser = new Parser(Grammars.GeneratedLanguage(Language));
        _nativeLanguage = new NativeLanguage(Language);
        _nativeParser = new NativeParser(_nativeLanguage);
        _nativeUtf8Parser = new NativeUtf8Parser(Language);

        // tree-sitter counts the skipped bytes in its error costs: the trees match the native parser on the same UTF-8 input
        var expected = _nativeUtf8Parser.ToSExpression(_utf8);
        foreach (var actual in Grammars.As026(() => new[] { _parser.Parse(_utf8).ToSExpression(), _generatedParser.Parse(_utf8).ToSExpression() }))
        {
            Check(expected, actual);
        }
    }

    private void Check(string expected, string actual)
    {
        if (expected != actual)
        {
            var index = 0;
            while (index < Math.Min(expected.Length, actual.Length) && expected[index] == actual[index])
            {
                index++;
            }

            var start = Math.Max(0, index - 80);
            throw new InvalidOperationException(
                $"The {Language} trees differ at {index}:\n  native:      ...{expected.Substring(start, Math.Min(160, expected.Length - start))}\n  treespitter: ...{actual.Substring(start, Math.Min(160, actual.Length - start))}");
        }
    }

    /// <summary>Releases the native parser.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _nativeParser.Dispose();
        _nativeUtf8Parser.Dispose();
        _nativeLanguage.Dispose();
    }

    /// <summary>Parses the input with the native tree-sitter parser.</summary>
    [Benchmark(Baseline = true)]
    public void Native()
    {
        using var tree = _nativeParser.Parse(_text);
    }

    /// <summary>Parses the UTF-8 input with the native tree-sitter parser.</summary>
    [Benchmark]
    public void NativeUtf8() => _nativeUtf8Parser.Parse(_utf8);

    /// <summary>Parses the UTF-8 input with the TreeSpitter virtual machine, and disposes the tree like the native benchmark.</summary>
    [Benchmark]
    public void Vm()
    {
        using var tree = _parser.Parse(_utf8);
    }

    /// <summary>Parses the UTF-8 input with the source-generated TreeSpitter parser, and disposes the tree.</summary>
    [Benchmark]
    public void Generated()
    {
        using var tree = _generatedParser.Parse(_utf8);
    }

    /// <summary>Parses the UTF-8 input with the TreeSpitter virtual machine, without disposing the tree.</summary>
    [Benchmark]
    public SyntaxTree VmUndisposed() => _parser.Parse(_utf8);

    /// <summary>Parses the string with the TreeSpitter virtual machine, the UTF-8 encoding included.</summary>
    [Benchmark]
    public SyntaxTree VmString() => _parser.Parse(_text);
}
