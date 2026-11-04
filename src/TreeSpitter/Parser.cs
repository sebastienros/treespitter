using System.Text;
using TreeSpitter.Dsl;
using TreeSpitter.Generation;
using TreeSpitter.Grammars;

namespace TreeSpitter;

/// <summary>
/// Parses UTF-8 input with a <see cref="TreeSpitter.Language"/>, with the GLR algorithm and the error recovery of
/// tree-sitter. A parser isn't thread-safe: use one parser per thread. Languages can be shared.
/// </summary>
public sealed class Parser
{
    private readonly IParserEngine _engine;

    /// <summary>Creates a parser for a language.</summary>
    public Parser(Language language)
    {
        Language = language ?? throw new ArgumentNullException(nameof(language));
        _engine = language.CreateEngine();
    }

    /// <summary>Gets the language of the parser.</summary>
    public Language Language { get; }

    /// <summary>
    /// Parses UTF-8 input. With an old tree, the tree of the previous version of the input edited with
    /// <see cref="SyntaxTree.Edit"/>, the parse is incremental: the unchanged nodes of the old tree are reused, and
    /// the new tree shares them. The old tree isn't changed.
    /// </summary>
    public SyntaxTree Parse(byte[] utf8, SyntaxTree? oldTree = null)
    {
        if (utf8 is null)
        {
            throw new ArgumentNullException(nameof(utf8));
        }

        return _engine.Parse(utf8, utf8.Length, oldTree);
    }

    /// <summary>Parses UTF-8 input, incrementally with an old tree, see <see cref="Parse(byte[], SyntaxTree?)"/>.</summary>
    public SyntaxTree Parse(ReadOnlyMemory<byte> utf8, SyntaxTree? oldTree = null)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(utf8, out var segment) && segment.Offset == 0 && segment.Array is not null)
        {
            return _engine.Parse(segment.Array, segment.Count, oldTree);
        }

        return Parse(utf8.ToArray(), oldTree);
    }

    /// <summary>
    /// Parses a string, encoded to UTF-8 first: positions are byte offsets in the UTF-8 encoding. The parse is
    /// incremental with an old tree, see <see cref="Parse(byte[], SyntaxTree?)"/>.
    /// </summary>
    public SyntaxTree Parse(string text, SyntaxTree? oldTree = null)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return Parse(Encoding.UTF8.GetBytes(text), oldTree);
    }

    /// <summary>Parses a file, read as UTF-8.</summary>
    public SyntaxTree ParseFile(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Creates a parser from a <c>grammar.js</c> or <c>grammar.json</c> file: the grammar is evaluated with Jint (or
    /// read) and compiled to parse tables and lexer automata.
    /// </summary>
    public static Parser FromGrammarFile(string path) => new(Language.FromGrammarFile(path));
}

public sealed partial class Language
{
    private Func<Language, IParserEngine>? _engineFactory;

    /// <summary>Gets the factory of the external scanner of the language, set by <see cref="WithExternalScanner"/>.</summary>
    public Func<IExternalScanner>? ExternalScannerFactory { get; private set; }

    /// <summary>
    /// Uses an external scanner for the grammar's <c>externals</c>: each parser creates one with the factory. Without
    /// a scanner, the external tokens never match.
    /// </summary>
    public Language WithExternalScanner(Func<IExternalScanner> factory)
    {
        ExternalScannerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    internal IParserEngine CreateEngine() =>
        _engineFactory is { } factory ? factory(this) : CreateEngine(TableLexFunction);

    /// <summary>Creates the parse loop, specialized for the lex function and the kind of parse table.</summary>
    private IParserEngine CreateEngine<TLexer>(TLexer lexFunction)
        where TLexer : struct, ILexFunction =>
        ParseTable.Length > 0
            ? new ParserEngine<TLexer, DenseParseTable>(this, lexFunction, DenseTable)
            : new ParserEngine<TLexer, ChunkedParseTable>(this, lexFunction, ChunkedTable);

    /// <summary>Gets the table-driven lex function of the language, which runs its lex automata.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public TableLexFunction TableLexFunction => new(MainLexTable, KeywordLexTable, Keywords);

    /// <summary>
    /// Uses a lex function instead of the lex automata of the language, typically a generated lexer. The parse
    /// loop is specialized for the lex function.
    /// </summary>
    public Language WithLexFunction<TLexer>(TLexer lexFunction)
        where TLexer : struct, ILexFunction
    {
        _engineFactory = language => language.CreateEngine(lexFunction);
        return this;
    }

    /// <summary>Compiles a grammar to a language.</summary>
    public static Language FromGrammar(Grammar grammar) => LanguageCompiler.Compile(grammar);

    /// <summary>Evaluates or reads a <c>grammar.js</c> or <c>grammar.json</c> file, and compiles the grammar.</summary>
    public static Language FromGrammarFile(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        var grammar = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? GrammarJsonReader.Read(File.ReadAllText(path))
            : new GrammarEvaluator().EvaluateFile(path);
        return LanguageCompiler.Compile(grammar);
    }

    /// <summary>Evaluates a <c>grammar.js</c> source, and compiles the grammar.</summary>
    public static Language FromGrammarSource(string source) => LanguageCompiler.Compile(new GrammarEvaluator().Evaluate(source));
}
