using Xunit;

namespace TreeSpitter.Tests;

public sealed class ExternalScannerTests
{
    [Fact]
    public void EachParseStartsWithANewScanner()
    {
        var language = Language.FromGrammarSource("""
            module.exports = grammar({
              name: 'once',
              externals: $ => [$.bang],
              rules: {
                source: $ => repeat(choice($.bang, $.word)),
                word: $ => /[a-z]+/,
              },
            });
            """).WithExternalScanner(() => new OnceScanner());
        var parser = new Parser(language);

        using var first = parser.Parse("!");
        using var second = parser.Parse("!");

        Assert.Equal("(source (bang))", first.ToSExpression());
        Assert.Equal("(source (bang))", second.ToSExpression());
    }

    /// <summary>
    /// A scanner which finds its token once: an empty state doesn't reset it, like the <c>inside_interpolated_string</c>
    /// of tree-sitter-python's scanner, so the state of a parse would leak to the next one with the same scanner.
    /// </summary>
    private sealed class OnceScanner : IExternalScanner
    {
        private bool _found;

        public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
        {
            if (_found || !validSymbols[0] || lexer.Lookahead != '!')
            {
                return false;
            }

            lexer.Advance(false);
            lexer.ResultSymbol = 0;
            _found = true;
            return true;
        }

        public int Serialize(Span<byte> buffer) => 0;

        public void Deserialize(ReadOnlySpan<byte> state)
        {
        }
    }
}
