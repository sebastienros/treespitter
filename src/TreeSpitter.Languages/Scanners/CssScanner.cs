namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-css: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld). It has no state.
/// </summary>
public sealed class CssScanner : IExternalScanner
{
    private const int DescendantOp = 0;
    private const int PseudoClassSelectorColon = 1;
    private const int ErrorRecovery = 2;

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer) => 0;

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[ErrorRecovery])
        {
            return false;
        }

        if (WideChar.IsSpace(lexer.Lookahead) && validSymbols[DescendantOp])
        {
            lexer.ResultSymbol = DescendantOp;

            lexer.Advance(true);
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                lexer.Advance(true);
            }

            lexer.MarkEnd();

            if (lexer.Lookahead is '#' or '.' or '[' or '-' or '*' || WideChar.IsAlnum(lexer.Lookahead))
            {
                return true;
            }

            if (lexer.Lookahead == ':')
            {
                lexer.Advance(false);
                if (WideChar.IsSpace(lexer.Lookahead))
                {
                    return false;
                }

                while (true)
                {
                    if (lexer.Lookahead is ';' or '}' || lexer.IsAtEnd)
                    {
                        return false;
                    }

                    if (lexer.Lookahead == '{')
                    {
                        return true;
                    }

                    lexer.Advance(false);
                }
            }
        }

        if (validSymbols[PseudoClassSelectorColon])
        {
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                lexer.Advance(true);
            }

            if (lexer.Lookahead == ':')
            {
                lexer.Advance(false);
                if (lexer.Lookahead == ':')
                {
                    return false;
                }

                lexer.MarkEnd();
                lexer.ResultSymbol = PseudoClassSelectorColon;

                // A pseudo class selector is followed by a `{`, a `;` is a property, except in a comment
                var inComment = false;
                while (lexer.Lookahead is not (';' or '}') && !lexer.IsAtEnd)
                {
                    lexer.Advance(false);
                    if (lexer.Lookahead == '{' && !inComment)
                    {
                        return true;
                    }

                    if (lexer.Lookahead == '/' && !inComment)
                    {
                        lexer.Advance(false);
                        if (lexer.Lookahead == '*')
                        {
                            inComment = true;
                        }
                    }
                    else if (lexer.Lookahead == '*' && inComment)
                    {
                        lexer.Advance(false);
                        if (lexer.Lookahead == '/')
                        {
                            inComment = false;
                        }
                    }
                }

                // At the end of the input, a pseudo class selector improves the error recovery
                return lexer.IsAtEnd;
            }
        }

        return false;
    }
}
