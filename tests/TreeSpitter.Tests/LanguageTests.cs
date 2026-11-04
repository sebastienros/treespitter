using System.Text;
using TreeSpitter.CodeGen;
using Xunit;

namespace TreeSpitter.Tests;

public sealed class LanguageTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("go")]
    [InlineData("java")]
    [InlineData("c")]
    public void ALoadedLanguageParsesLikeTheCompiledOne(string grammar)
    {
        var language = Fixtures.Language(grammar);
        var loaded = Language.Load(language.Save());

        Assert.Equal(language.StateCount, loaded.StateCount);
        Assert.Equal(language.SymbolCount, loaded.SymbolCount);
        foreach (var test in Testing.Corpus.Load(Fixtures.Corpus(grammar)).Take(20))
        {
            Assert.Equal(new Parser(language).Parse(test.Input).ToSExpression(), new Parser(loaded).Parse(test.Input).ToSExpression());
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("go")]
    [InlineData("java")]
    [InlineData("c")]
    public void TheGeneratedLanguageHasTheTablesOfTheCompiledOne(string grammar)
    {
        var compiled = Fixtures.Language(grammar);
        var generated = Fixtures.GeneratedLanguage(grammar);

        Assert.Equal(compiled.StateCount, generated.StateCount);
        Assert.Equal(compiled.SymbolCount, generated.SymbolCount);
        Assert.Equal(compiled.LexStateCount, generated.LexStateCount);
        Assert.Equal(compiled.Save(), generated.Save());
    }

    [Fact]
    public void TheGeneratorEmitsAClassWithTheLanguage()
    {
        var source = CSharpGenerator.Generate(Fixtures.Language("json"), new CSharpGeneratorOptions { Namespace = "Demo", ClassName = "JsonParser" });

        Assert.Contains("namespace Demo", source);
        Assert.Contains("public static partial class JsonParser", source);
        Assert.Contains("private static bool LexMain(global::TreeSpitter.Lexer lexer, int state)", source);
        Assert.Contains("global::TreeSpitter.ILexFunction", source);
    }

    [Fact]
    public void TheQuickStartOfTheReadmeWorks()
    {
        var parser = Parser.FromGrammarFile(Fixtures.GrammarJs("json"));
        using var tree = parser.Parse("""{ "name": "treespitter", "tags": [1, 2] }""");

        Assert.Equal(
            "(document (object (pair key: (string (string_content)) value: (string (string_content))) (pair key: (string (string_content)) value: (array (number) (number)))))",
            tree.RootNode.ToSExpression());
        var pair = tree.RootNode.NamedChildren.First().NamedChildren.First();
        Assert.Equal("\"name\"", pair.ChildByFieldName("key").Text);
        Assert.Equal(new Point(0, 2), pair.StartPoint);
    }

    [Fact]
    public void GrammarsCanLogWithConsole()
    {
        var messages = new List<string>();
        var evaluator = new Dsl.GrammarEvaluator(new Dsl.GrammarEvaluatorOptions { Log = messages.Add });
        var grammar = evaluator.Evaluate("""
            console.log('evaluating', 1);
            module.exports = grammar({ name: 'logs', rules: { document: _ => 'x' } });
            """);

        Assert.Equal("logs", grammar.Name);
        Assert.Equal(["evaluating 1"], messages);
    }

    [Fact]
    public void ADisposedTreeCantBeUsed()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("json"));
        var tree = parser.Parse("[1]");
        tree.Dispose();

        Assert.Throws<ObjectDisposedException>(() => tree.RootNode.Type);
        Assert.Equal("(document (array (number)))", parser.Parse("[1]").ToSExpression());
    }

    [Fact]
    public void ParsesUtf8WithByteOffsets()
    {
        var parser = new Parser(Fixtures.GeneratedLanguage("json"));
        var input = Encoding.UTF8.GetBytes("{\"é\": [1, \"☃\"]}");
        var tree = parser.Parse(input);

        Assert.False(tree.HasError);
        var pair = tree.RootNode.NamedChildren.First().NamedChildren.First();
        Assert.Equal("pair", pair.Type);
        Assert.Equal("\"é\"", pair.ChildByFieldName("key").Text);
        Assert.Equal(1, pair.StartByte);
        Assert.Equal(input.Length - 1, pair.EndByte);
        Assert.Equal(new Point(0, 1), pair.StartPoint);
    }
}
