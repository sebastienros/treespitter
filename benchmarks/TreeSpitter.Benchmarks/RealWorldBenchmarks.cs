using System.Text;
using BenchmarkDotNet.Attributes;
using NativeLanguage = TreeSitter.Language;
using NativeParser = TreeSitter.Parser;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// TreeSpitter against the native tree-sitter parsers on real-world files: tree-sitter's own <c>parser.c</c> and
/// <c>query.c</c>, and the <c>grammar.json</c> of tree-sitter-c (pretty-printed JSON). The setup checks that the trees are
/// the same as the native parser's on the same UTF-8 input.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RealWorldBenchmarks
{
    private Parser _parser = null!;
    private NativeLanguage _nativeLanguage = null!;
    private NativeParser _nativeParser = null!;
    private NativeUtf8Parser _nativeUtf8Parser = null!;
    private string _text = "";
    private byte[] _utf8 = [];

    /// <summary>The files.</summary>
    public static IEnumerable<string> Files => ["parser.c", "query.c", "grammar.json"];

    /// <summary>The file of the benchmark.</summary>
    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    /// <summary>Builds the parsers and checks that they agree on the file.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var (language, path) = File switch
        {
            "grammar.json" => ("json", Path.Combine(Grammars.Root(), "grammars", "c", "grammar.json")),
            _ => ("c", Path.Combine(Grammars.Root(), "benchmarks", "TreeSpitter.Benchmarks", "Inputs", File)),
        };

        _utf8 = System.IO.File.ReadAllBytes(path);
        _text = Encoding.UTF8.GetString(_utf8);
        _parser = new Parser(Grammars.GeneratedLanguage(language));
        _nativeLanguage = new NativeLanguage(language);
        _nativeParser = new NativeParser(_nativeLanguage);
        _nativeUtf8Parser = new NativeUtf8Parser(language);
        if (Grammars.As026(() => { using var tree = _parser.Parse(_utf8); return tree.ToSExpression(); }) != _nativeUtf8Parser.ToSExpression(_utf8))
        {
            throw new InvalidOperationException($"The trees of {File} differ.");
        }
    }

    /// <summary>Releases the native parsers.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _nativeParser.Dispose();
        _nativeUtf8Parser.Dispose();
        _nativeLanguage.Dispose();
    }

    /// <summary>The native parser, through the binding (UTF-16).</summary>
    [Benchmark(Baseline = true)]
    public void Native()
    {
        using var tree = _nativeParser.Parse(_text);
    }

    /// <summary>The native parser on the UTF-8 input.</summary>
    [Benchmark]
    public void NativeUtf8() => _nativeUtf8Parser.Parse(_utf8);

    /// <summary>The source-generated TreeSpitter parser on the UTF-8 input.</summary>
    [Benchmark]
    public void Generated()
    {
        using var tree = _parser.Parse(_utf8);
    }
}
