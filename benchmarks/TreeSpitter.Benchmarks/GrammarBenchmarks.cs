using BenchmarkDotNet.Attributes;
using TreeSpitter.Dsl;
using TreeSpitter.Generation;
using TreeSpitter.Grammars;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// The preparation of a language, which tree-sitter does ahead of time with <c>tree-sitter generate</c> and a C
/// compiler: evaluating the grammar.js file, compiling the grammar to tables, and loading precompiled tables (what a
/// source-generated parser does at startup).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class GrammarBenchmarks
{
    private Grammar _grammar = null!;
    private byte[] _tables = [];

    /// <summary>The grammar of the benchmark.</summary>
    [Params("json", "go", "c")]
    public string Grammar { get; set; } = "";

    /// <summary>Evaluates and compiles the grammar once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _grammar = new GrammarEvaluator().EvaluateFile(Grammars.Path(Grammar));
        _tables = LanguageCompiler.Compile(_grammar).Save();
    }

    /// <summary>Evaluates the grammar.js file with Jint.</summary>
    [Benchmark]
    public Grammar Evaluate() => new GrammarEvaluator().EvaluateFile(Grammars.Path(Grammar));

    /// <summary>Compiles the grammar to parse tables and lexer automata.</summary>
    [Benchmark]
    public Language Compile() => LanguageCompiler.Compile(_grammar);

    /// <summary>Loads the compiled tables.</summary>
    [Benchmark]
    public Language Load() => Language.Load(_tables);
}
