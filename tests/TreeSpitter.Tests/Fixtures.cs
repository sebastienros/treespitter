using System.Collections.Concurrent;
using TreeSpitter.Dsl;
using Xunit;

namespace TreeSpitter.Tests;

/// <summary>The grammars vendored in the grammars folder, and their languages, shared by the tests.</summary>
internal static class Fixtures
{
    private static readonly ConcurrentDictionary<string, Lazy<Language>> CompiledLanguages = new();

    /// <summary>The vendored grammars: the directories of the grammars folder with a grammar.json and a corpus.</summary>
    public static readonly string[] GrammarNames = Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "Grammars"))
        .Where(d => File.Exists(Path.Combine(d, "grammar.json")) && Directory.Exists(Path.Combine(d, "corpus")))
        .Select(Path.GetFileName)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray()!;

    /// <summary>The grammars, for the theories over all of them.</summary>
    public static TheoryData<string> Grammars => new(GrammarNames);

    public static string GrammarDirectory(string name) => Path.Combine(AppContext.BaseDirectory, "Grammars", name);

    /// <summary>
    /// The grammar.js of a grammar: <c>name/grammar.js</c>, or <c>name/name/grammar.js</c> for the grammars vendored with
    /// the layout of their repository, because they require files of sibling directories (<c>../common</c>).
    /// </summary>
    public static string GrammarJs(string name)
    {
        var path = Path.Combine(GrammarDirectory(name), "grammar.js");
        return File.Exists(path) ? path : Path.Combine(GrammarDirectory(name), name, "grammar.js");
    }

    public static string GrammarJson(string name) => Path.Combine(GrammarDirectory(name), "grammar.json");

    public static string Corpus(string name) => Path.Combine(GrammarDirectory(name), "corpus");

    public static Language Language(string name)
        => CompiledLanguages.GetOrAdd(name, n => new Lazy<Language>(() =>
        {
            var language = TreeSpitter.Language.FromGrammarFile(GrammarJs(n));
            return ExternalScanner(n) is { } scanner ? language.WithExternalScanner(scanner) : language;
        })).Value;

    public static Parser Parser(string name) => new(Language(name));

    /// <summary>The external scanner of a grammar: the one of its generated language.</summary>
    public static Func<IExternalScanner>? ExternalScanner(string name) => GeneratedLanguage(name).ExternalScannerFactory;

    /// <summary>
    /// The source-generated language of a grammar, from TreeSpitter.Languages: the class marked with
    /// <c>[GeneratedLanguage("name/grammar.json")]</c>.
    /// </summary>
    public static Language GeneratedLanguage(string name)
    {
        var type = typeof(Languages.Json).Assembly.GetTypes().FirstOrDefault(t =>
            t.GetCustomAttributes(typeof(GeneratedLanguageAttribute), false).Cast<GeneratedLanguageAttribute>().Any(a => a.GrammarPath == name + "/grammar.json"))
            ?? throw new ArgumentException("No generated language " + name, nameof(name));
        return (Language)type.GetProperty("Language")!.GetValue(null)!;
    }

    public static Parser FromSource(string source) => new(TreeSpitter.Language.FromGrammarSource(source));

    public static GrammarEvaluator Evaluator() => new();
}
