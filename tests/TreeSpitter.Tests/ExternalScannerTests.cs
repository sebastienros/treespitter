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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEditWithPointsInvalidatesTheNodesDependingOnTheirColumnUpToTheEndOfItsLine(bool withPoints)
    {
        var language = Language.FromGrammarSource("""
            module.exports = grammar({
              name: 'columns',
              externals: $ => [$.column],
              rules: {
                source: $ => repeat($.line),
                line: $ => seq($.column, $.word),
                word: $ => /[a-z]+/,
              },
            });
            """).WithExternalScanner(() => new ColumnScanner());
        var parser = new Parser(language);
        using var tree = parser.Parse("a\n  b\n  c\n");

        // "a" -> "xa": the columns of the first line are shifted, not the ones of the next lines. Like in tree-sitter, the
        // nodes depending on their column are invalidated until a line break: the second line starts before its
        // newline, at the end of the first one
        tree.Edit(withPoints ? new InputEdit(0, 0, 1, new Point(0, 0), new Point(0, 0), new Point(0, 1)) : new InputEdit(0, 0, 1));

        var lines = tree.RootNode.NamedChildren.ToList();
        Assert.True(lines[0].HasChanges);
        Assert.True(lines[1].HasChanges);
        Assert.Equal(!withPoints, lines[2].HasChanges);
        using var edited = parser.Parse("xa\n  b\n  c\n", tree);
        Assert.Equal("(source (line (column) (word)) (line (column) (word)) (line (column) (word)))", edited.ToSExpression());
        Assert.Equal(5, edited.RootNode.NamedChildren.ElementAt(1).StartByte);
    }

    /// <summary>A scanner whose empty token, before each word, reads its column.</summary>
    private sealed class ColumnScanner : IExternalScanner
    {
        public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
        {
            while (lexer.Lookahead is ' ' or '\n')
            {
                lexer.Advance(true);
            }

            if (!validSymbols[0] || lexer.IsAtEnd)
            {
                return false;
            }

            lexer.ResultSymbol = 0;
            return lexer.GetColumn() >= 0;
        }

        public int Serialize(Span<byte> buffer) => 0;

        public void Deserialize(ReadOnlySpan<byte> state)
        {
        }
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
