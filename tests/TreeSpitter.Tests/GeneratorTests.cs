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

public sealed class WideCharTests
{
    [Fact]
    public void TheWideCharacterFunctionsAreTheOnesOfTreeSitterWasmStandardLibrary()
    {
        Assert.True(WideChar.IsSpace(' ') && WideChar.IsSpace('\t') && WideChar.IsSpace(0x2028) && WideChar.IsSpace(0x3000));
        Assert.False(WideChar.IsSpace(0xA0) || WideChar.IsSpace(0x2007) || WideChar.IsSpace(0x1680) || WideChar.IsSpace('a'));
        Assert.True(WideChar.IsAlpha('a') && WideChar.IsAlpha('é') && WideChar.IsAlpha('Σ') && WideChar.IsAlpha(0x4E00));
        Assert.False(WideChar.IsAlpha('1') || WideChar.IsAlpha('_') || WideChar.IsAlpha(' '));
        Assert.True(WideChar.IsAlnum('7') && WideChar.IsDigit('0') && !WideChar.IsDigit('٣'));
        Assert.True(WideChar.IsPunct('!') && WideChar.IsPunct('§') && !WideChar.IsPunct('a'));
        Assert.Equal('A', WideChar.ToUpper('a'));
        Assert.Equal('σ', WideChar.ToLower('Σ'));
        Assert.Equal('Σ', WideChar.ToUpper('ς'));
        Assert.Equal('k', WideChar.ToLower(0x212A));
        Assert.Equal(0x1C4, WideChar.ToUpper(0x1C5));
        Assert.Equal(0x1C6, WideChar.ToLower(0x1C5));
        Assert.True(WideChar.IsUpper('Q') && WideChar.IsLower('q') && !WideChar.IsUpper('1'));
        Assert.True(WideChar.IsXDigit('F') && WideChar.IsXDigit('a') && !WideChar.IsXDigit('g'));
    }
}
