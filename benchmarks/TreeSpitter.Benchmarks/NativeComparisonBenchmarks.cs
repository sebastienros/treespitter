using System.Text;
using BenchmarkDotNet.Attributes;
using NativeLanguage = TreeSitter.Language;
using NativeParser = TreeSitter.Parser;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// TreeSpitter against the native tree-sitter parsers of the TreeSitter.DotNet package, on the same inputs. The setup
/// checks that both produce the same tree. The memory columns only count managed allocations: the native trees are
/// allocated by tree-sitter.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class NativeComparisonBenchmarks
{
    private Parser _parser = null!;
    private NativeLanguage _nativeLanguage = null!;
    private NativeParser _nativeParser = null!;
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
        _nativeLanguage = new NativeLanguage(Language);
        _nativeParser = new NativeParser(_nativeLanguage);

        using var nativeTree = _nativeParser.Parse(_text) ?? throw new InvalidOperationException("The native parser failed.");
        var expected = nativeTree.RootNode.ToString();
        var actual = _parser.Parse(_utf8).RootNode.ToSExpression();
        if (expected != actual)
        {
            var index = 0;
            while (index < Math.Min(expected.Length, actual.Length) && expected[index] == actual[index])
            {
                index++;
            }

            var start = Math.Max(0, index - 80);
            throw new InvalidOperationException(
                $"The {Language} trees differ at {index}:\n  native:      ...{expected.Substring(start, Math.Min(160, expected.Length - start))}\n  tree-spitter: ...{actual.Substring(start, Math.Min(160, actual.Length - start))}");
        }
    }

    /// <summary>Releases the native parser.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _nativeParser.Dispose();
        _nativeLanguage.Dispose();
    }

    /// <summary>Parses the input with the native tree-sitter parser.</summary>
    [Benchmark(Baseline = true)]
    public void Native()
    {
        using var tree = _nativeParser.Parse(_text);
    }

    /// <summary>Parses the UTF-8 input with the TreeSpitter virtual machine.</summary>
    [Benchmark]
    public SyntaxTree Vm() => _parser.Parse(_utf8);

    /// <summary>Parses the string with the TreeSpitter virtual machine, the UTF-8 encoding included.</summary>
    [Benchmark]
    public SyntaxTree VmString() => _parser.Parse(_text);
}
