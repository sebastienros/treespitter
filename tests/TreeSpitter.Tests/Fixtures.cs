using System.Collections.Concurrent;
using TreeSpitter.Dsl;

namespace TreeSpitter.Tests;

/// <summary>The grammars vendored in the grammars folder, and their languages, shared by the tests.</summary>
internal static class Fixtures
{
    private static readonly ConcurrentDictionary<string, Lazy<Language>> Languages = new();

    public static readonly string[] GrammarNames = ["json", "go", "java", "c"];

    public static string GrammarDirectory(string name) => Path.Combine(AppContext.BaseDirectory, "Grammars", name);

    public static string GrammarJs(string name) => Path.Combine(GrammarDirectory(name), "grammar.js");

    public static string GrammarJson(string name) => Path.Combine(GrammarDirectory(name), "grammar.json");

    public static string Corpus(string name) => Path.Combine(GrammarDirectory(name), "corpus");

    public static Language Language(string name)
        => Languages.GetOrAdd(name, n => new Lazy<Language>(() => TreeSpitter.Language.FromGrammarFile(GrammarJs(n)))).Value;

    public static Parser Parser(string name) => new(Language(name));

    public static Parser FromSource(string source) => new(TreeSpitter.Language.FromGrammarSource(source));

    public static GrammarEvaluator Evaluator() => new();
}
