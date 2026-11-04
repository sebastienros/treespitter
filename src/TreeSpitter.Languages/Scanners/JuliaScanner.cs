namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-julia: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld, GitHub). It has
/// no state.
/// </summary>
/// <remarks>
/// Block comments and immediate parentheses are easy to parse, but strings
/// require extra-attention.
///
/// The main problems that arise when parsing strings are:
/// 1. Triple quoted strings allow single quotes inside. e.g. """ "foo" """.
/// 2. Strings can have arbitrary interpolations, including other strings.
///    e.g. "echo $("foo")"
/// 3. Non-standard string literals don't allow interpolations or escape
///    sequences, but you can always write \" and \`.
/// All of the above also applies to command literals.
/// </remarks>
public sealed class JuliaScanner : IExternalScanner
{
    // enum TokenType
    private const ushort BlockCommentRest = 0;
    private const ushort ImmediateParen = 1;
    private const ushort ImmediateBracket = 2;
    private const ushort ImmediateBrace = 3;
    private const ushort ImmediateStringStart = 4;
    private const ushort ImmediateCommandStart = 5;
    private const ushort ContentCmd1 = 6;
    private const ushort ContentCmd1Raw = 7;
    private const ushort ContentCmd3 = 8;
    private const ushort ContentCmd3Raw = 9;
    private const ushort ContentStr1 = 10;
    private const ushort ContentStr1Raw = 11;
    private const ushort ContentStr3 = 12;
    private const ushort ContentStr3Raw = 13;
    private const ushort EndCmd = 14;
    private const ushort EndStr = 15;

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer) => 0;

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
    }

    // Scanner functions

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void MarkEnd(Lexer lexer) => lexer.MarkEnd();

    private static bool ScanContent(Lexer lexer, ushort contentSymbol, char endChar, uint nDelim, bool interp)
    {
        var endSymbol = (endChar == '"') ? EndStr : EndCmd;
        var hasContent = false;
        int next;
        while ((next = lexer.Lookahead) != 0)
        {
            MarkEnd(lexer);
            if (interp && (next == '$' || next == '\\'))
            {
                lexer.ResultSymbol = contentSymbol;
                return hasContent;
            }
            else if (next == '\\')
            {
                // Parse backslash in raw strings (check escaped delimiters and '\\')
                Advance(lexer);
                next = lexer.Lookahead;
                if (next == endChar || next == '\\')
                {
                    lexer.ResultSymbol = contentSymbol;
                    return hasContent;
                }
            }
            else
            {
                var isEndDelimiter = true;
                for (var i = 1u; i <= nDelim; i++)
                {
                    if (lexer.Lookahead == endChar)
                    {
                        Advance(lexer);
                    }
                    else
                    {
                        isEndDelimiter = false;
                        break;
                    }
                }

                if (isEndDelimiter)
                {
                    if (hasContent)
                    {
                        lexer.ResultSymbol = contentSymbol;
                        return true;
                    }
                    else
                    {
                        MarkEnd(lexer);
                        lexer.ResultSymbol = endSymbol;
                        return true;
                    }
                }
            }

            Advance(lexer);
            hasContent = true;
        }

        return false;
    }

    private static bool ScanBlockComment(Lexer lexer)
    {
        // NOTE: The first `#=` is scanned by tree-sitter
        var afterEq = false;
        var nestingDepth = 1u;
        for (; ; )
        {
            switch (lexer.Lookahead)
            {
                case '=':
                    Advance(lexer);
                    afterEq = true;
                    break;
                case '#':
                    Advance(lexer);
                    if (afterEq)
                    {
                        afterEq = false;
                        nestingDepth--;
                        if (nestingDepth == 0)
                        {
                            lexer.ResultSymbol = BlockCommentRest;
                            return true;
                        }
                    }
                    else
                    {
                        afterEq = false;
                        if (lexer.Lookahead == '=')
                        {
                            Advance(lexer);
                            nestingDepth++;
                        }
                    }

                    break;
                case '\0':
                    return false;
                default:
                    Advance(lexer);
                    afterEq = false;
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[ImmediateParen] && lexer.Lookahead == '(')
        {
            lexer.ResultSymbol = ImmediateParen;
            return true;
        }
        else if (validSymbols[ImmediateBracket] && lexer.Lookahead == '[')
        {
            lexer.ResultSymbol = ImmediateBracket;
            return true;
        }
        else if (validSymbols[ImmediateBrace] && lexer.Lookahead == '{')
        {
            lexer.ResultSymbol = ImmediateBrace;
            return true;
        }
        else if (validSymbols[ImmediateStringStart] && lexer.Lookahead == '"')
        {
            lexer.ResultSymbol = ImmediateStringStart;
            return true;
        }
        else if (validSymbols[ImmediateCommandStart] && lexer.Lookahead == '`')
        {
            lexer.ResultSymbol = ImmediateCommandStart;
            return true;
        }

        if (validSymbols[BlockCommentRest] && ScanBlockComment(lexer))
        {
            return true;
        }

        if (validSymbols[ContentStr1] && ScanContent(lexer, ContentStr1, '"', 1, true))
        {
            return true;
        }

        if (validSymbols[ContentStr3] && ScanContent(lexer, ContentStr3, '"', 3, true))
        {
            return true;
        }

        if (validSymbols[ContentCmd1] && ScanContent(lexer, ContentCmd1, '`', 1, true))
        {
            return true;
        }

        if (validSymbols[ContentCmd3] && ScanContent(lexer, ContentCmd3, '`', 3, true))
        {
            return true;
        }

        if (validSymbols[ContentStr1Raw] && ScanContent(lexer, ContentStr1Raw, '"', 1, false))
        {
            return true;
        }

        if (validSymbols[ContentStr3Raw] && ScanContent(lexer, ContentStr3Raw, '"', 3, false))
        {
            return true;
        }

        if (validSymbols[ContentCmd1Raw] && ScanContent(lexer, ContentCmd1Raw, '`', 1, false))
        {
            return true;
        }

        if (validSymbols[ContentCmd3Raw] && ScanContent(lexer, ContentCmd3Raw, '`', 3, false))
        {
            return true;
        }

        return false;
    }
}
