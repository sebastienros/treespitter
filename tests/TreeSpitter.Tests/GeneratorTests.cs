using TreeSpitter.Generation;
using TreeSpitter.Grammars;
using Xunit;

namespace TreeSpitter.Tests;

/// <summary>The behaviors of <c>tree-sitter generate</c> 0.27 that the corpus tests of the grammars don't cover.</summary>
public sealed class GeneratorTests
{
    private static Language Compile(string rules, string options = "", List<string>? warnings = null)
    {
        var grammar = Fixtures.Evaluator().Evaluate($$"""
            module.exports = grammar({
              name: 'test',
              {{options}}
              rules: {
                {{rules}}
              }
            });
            """);
        return LanguageCompiler.Compile(grammar, warnings);
    }

    private static string Parse(Language language, string input)
    {
        using var tree = new Parser(language).Parse(input);
        return tree.ToSExpression();
    }

    [Fact]
    public void IdenticalReductionsAreNotAConflict()
    {
        var language = Compile("""
            document: $ => repeat($.item),
            item: $ => choice('a', prec.left('a')),
            """);

        Assert.Equal("(document (item) (item))", Parse(language, "a a"));
    }

    [Fact]
    public void UnnecessaryConflictsAreReported()
    {
        var warnings = new List<string>();
        Compile("""
            document: $ => repeat(choice($.a, $.b)),
            a: $ => 'a',
            b: $ => 'b',
            """, "conflicts: $ => [[$.a, $.b]],", warnings);

        Assert.Contains("unnecessary conflicts:\n  `a`, `b`", warnings);
    }

    [Fact]
    public void IndirectRecursionIsDetectedInTheFlattenedProductions()
    {
        // 0.25 only followed choices: the optional semicolon hid the cycle
        var exception = Assert.Throws<GrammarException>(() => Compile("""
            document: $ => repeat($.a),
            a: $ => seq($.b, optional(';')),
            b: $ => choice($.a, 'x'),
            """));

        Assert.Equal("Grammar contains an indirectly recursive rule: a -> b -> a", exception.Message);
    }

    [Fact]
    public void RulesUsedOnlyByReservedWordsAreKept()
    {
        var language = Compile("""
            document: $ => repeat($.identifier),
            identifier: $ => /[a-z]+/,
            kw_if: $ => 'if',
            """, "word: $ => $.identifier, reserved: { global: $ => [$.kw_if] },");

        Assert.Contains("kw_if", Enumerable.Range(0, language.SymbolCount).Select(i => language.SymbolName((ushort)i)));
    }

    [Fact]
    public void ImmediateTokensCantReferenceRules()
    {
        var exception = Assert.Throws<GrammarException>(() => Compile("""
            document: $ => repeat(choice($.word, $.suffix)),
            word: $ => /[a-z]+/,
            suffix: $ => token.immediate($.word),
            """));

        Assert.Equal("Grammar Error: Unexpected rule `word` in `token()` call", exception.Message);
    }

    [Fact]
    public void InlinedSupertypesAreIgnored()
    {
        var warnings = new List<string>();
        var language = Compile("""
            document: $ => repeat($._value),
            _value: $ => choice($.number, $.word),
            number: $ => /\d+/,
            word: $ => /[a-z]+/,
            """, "supertypes: $ => [$._value], inline: $ => [$._value],", warnings);

        Assert.Contains("rule `_value` is both a supertype and inlined. the supertype is ignored.", warnings);
        Assert.Equal("(document (number) (word))", Parse(language, "1 a"));
    }

    [Fact]
    public void TerminalSupertypesAreRejected()
    {
        var exception = Assert.Throws<GrammarException>(() => Compile("""
            document: $ => repeat($._literal),
            _literal: $ => /\d+/,
            """, "supertypes: $ => [$._literal],"));

        Assert.Equal("Terminal rule '_literal' cannot be used as a supertype", exception.Message);
    }

    [Fact]
    public void AGrammarJsonWithoutExtrasHasNone()
    {
        var grammar = GrammarJsonReader.Read("""
            { "name": "test", "rules": { "document": { "type": "REPEAT", "content": { "type": "STRING", "value": "a" } } } }
            """);

        Assert.Empty(grammar.Extras);
    }

    [Fact]
    public void CaseInsensitivePatternsUseSimpleCaseFolding()
    {
        // σ, ς and Σ are a case folding class, which upper and lower casing don't connect
        var language = Compile("""
            document: $ => repeat($.word),
            word: $ => /σ+/i,
            """);

        Assert.Equal("(document (word))", Parse(language, "σςΣ"));
    }

    [Fact]
    public void CaseInsensitivePatternsDontFoldAsciiLettersOntoOtherLetters()
    {
        // The Kelvin sign folds with k and K, but not in tree-sitter's patterns
        var language = Compile("""
            document: $ => repeat(choice($.word, $.other)),
            word: $ => /k+/i,
            other: $ => /[^kK\s]/,
            """);

        Assert.Equal("(document (word) (other))", Parse(language, "kKK"));
    }

    [Fact]
    public void PropertiesCanBeNegatedWithNotEqual()
    {
        var language = Compile("""
            document: $ => repeat(choice($.upper, $.other)),
            upper: $ => /\p{gc=Lu}+/,
            other: $ => /\p{gc!=Lu}+/,
            """);

        Assert.Equal("(document (upper) (other))", Parse(language, "ABcd"));
    }

    [Fact]
    public void NonAsciiByteClassesAreRejected()
    {
        var exception = Assert.Throws<GrammarException>(() => Compile("""
            document: $ => repeat($.byte),
            byte: $ => new RustRegex('(?-u:[\\x80-\\xff])'),
            """));

        Assert.Contains("The non-ASCII byte class \\x80-\\xff via (?-u:...) is not supported. Remove the `-u` flag.", exception.Message);
    }
}
