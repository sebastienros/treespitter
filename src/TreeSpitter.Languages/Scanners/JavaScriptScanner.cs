namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-javascript: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld). It has no
/// state.
/// </summary>
public sealed class JavaScriptScanner : IExternalScanner
{
    private const int AutomaticSemicolon = 0;
    private const int TemplateChars = 1;
    private const int TernaryQmark = 2;
    private const int HtmlComment = 3;
    private const int LogicalOr = 4;
    private const int EscapeSequence = 5;
    private const int RegexPattern = 6;
    private const int JsxText = 7;

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer) => 0;

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    private static bool ScanTemplateChars(Lexer lexer)
    {
        lexer.ResultSymbol = TemplateChars;
        for (var hasContent = false; ; hasContent = true)
        {
            lexer.MarkEnd();
            switch (lexer.Lookahead)
            {
                case '`':
                    return hasContent;
                case '\0':
                    return false;
                case '$':
                    Advance(lexer);
                    if (lexer.Lookahead == '{')
                    {
                        return hasContent;
                    }

                    break;
                case '\\':
                    return hasContent;
                default:
                    Advance(lexer);
                    break;
            }
        }
    }

    private enum WhitespaceResult
    {
        Reject, // Semicolon is illegal, ie a syntax error occurred
        NoNewline, // Unclear if semicolon will be legal, continue
        Accept, // Semicolon is legal, assuming a comment was encountered
    }

    /// <param name="lexer">The lexer.</param>
    /// <param name="scannedComment">Set when a comment is scanned.</param>
    /// <param name="consume">If false, only consume enough to check if comment indicates semicolon-legality.</param>
    private static WhitespaceResult ScanWhitespaceAndComments(Lexer lexer, ref bool scannedComment, bool consume)
    {
        var sawBlockNewline = false;

        while (true)
        {
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '/')
            {
                Skip(lexer);

                if (lexer.Lookahead == '/')
                {
                    Skip(lexer);
                    while (lexer.Lookahead != 0 && lexer.Lookahead != '\n' && lexer.Lookahead != 0x2028 &&
                           lexer.Lookahead != 0x2029)
                    {
                        Skip(lexer);
                    }

                    scannedComment = true;
                }
                else if (lexer.Lookahead == '*')
                {
                    Skip(lexer);
                    while (lexer.Lookahead != 0)
                    {
                        if (lexer.Lookahead == '*')
                        {
                            Skip(lexer);
                            if (lexer.Lookahead == '/')
                            {
                                Skip(lexer);
                                scannedComment = true;

                                if (lexer.Lookahead != '/' && !consume)
                                {
                                    return sawBlockNewline ? WhitespaceResult.Accept : WhitespaceResult.NoNewline;
                                }

                                break;
                            }
                        }
                        else if (lexer.Lookahead == '\n' || lexer.Lookahead == 0x2028 || lexer.Lookahead == 0x2029)
                        {
                            sawBlockNewline = true;
                            Skip(lexer);
                        }
                        else
                        {
                            Skip(lexer);
                        }
                    }
                }
                else
                {
                    return WhitespaceResult.Reject;
                }
            }
            else
            {
                return WhitespaceResult.Accept;
            }
        }
    }

    private static bool ScanAutomaticSemicolon(Lexer lexer, bool commentCondition, ref bool scannedComment)
    {
        lexer.ResultSymbol = AutomaticSemicolon;
        lexer.MarkEnd();

        while (true)
        {
            if (lexer.Lookahead == 0)
            {
                return true;
            }

            if (lexer.Lookahead == '/')
            {
                var result = ScanWhitespaceAndComments(lexer, ref scannedComment, false);
                if (result == WhitespaceResult.Reject)
                {
                    return false;
                }

                if (result == WhitespaceResult.Accept && commentCondition && lexer.Lookahead != ',' && lexer.Lookahead != '=')
                {
                    return true;
                }
            }

            if (lexer.Lookahead == '}')
            {
                return true;
            }

            if (lexer.IsAtIncludedRangeStart)
            {
                return true;
            }

            if (lexer.Lookahead == '\n' || lexer.Lookahead == 0x2028 || lexer.Lookahead == 0x2029)
            {
                break;
            }

            if (!WideChar.IsSpace(lexer.Lookahead))
            {
                return false;
            }

            Skip(lexer);
        }

        Skip(lexer);

        if (ScanWhitespaceAndComments(lexer, ref scannedComment, true) == WhitespaceResult.Reject)
        {
            return false;
        }

        switch (lexer.Lookahead)
        {
            case '`':
            case ',':
            case ':':
            case ';':
            case '*':
            case '%':
            case '>':
            case '<':
            case '=':
            case '[':
            case '(':
            case '?':
            case '^':
            case '|':
            case '&':
            case '/':
                return false;

            // Insert a semicolon before decimals literals but not otherwise.
            case '.':
                Skip(lexer);
                return WideChar.IsDigit(lexer.Lookahead);

            // Insert a semicolon before `--` and `++`, but not before binary `+` or `-`.
            case '+':
                Skip(lexer);
                return lexer.Lookahead == '+';
            case '-':
                Skip(lexer);
                return lexer.Lookahead == '-';

            // Don't insert a semicolon before `!=`, but do insert one before a unary `!`.
            case '!':
                Skip(lexer);
                return lexer.Lookahead != '=';

            // Don't insert a semicolon before `in` or `instanceof`, but do insert one
            // before an identifier.
            case 'i':
                Skip(lexer);

                if (lexer.Lookahead != 'n')
                {
                    return true;
                }

                Skip(lexer);

                if (!WideChar.IsAlpha(lexer.Lookahead))
                {
                    return false;
                }

                for (var i = 0; i < 8; i++)
                {
                    if (lexer.Lookahead != "stanceof"[i])
                    {
                        return true;
                    }

                    Skip(lexer);
                }

                if (!WideChar.IsAlpha(lexer.Lookahead))
                {
                    return false;
                }

                break;

            default:
                break;
        }

        return true;
    }

    private static bool ScanTernaryQmark(Lexer lexer)
    {
        while (true)
        {
            if (!WideChar.IsSpace(lexer.Lookahead))
            {
                break;
            }

            Skip(lexer);
        }

        if (lexer.Lookahead == '?')
        {
            Advance(lexer);

            if (lexer.Lookahead == '?')
            {
                return false;
            }

            lexer.MarkEnd();
            lexer.ResultSymbol = TernaryQmark;

            if (lexer.Lookahead == '.')
            {
                Advance(lexer);
                if (WideChar.IsDigit(lexer.Lookahead))
                {
                    return true;
                }

                return false;
            }

            return true;
        }

        return false;
    }

    private static bool ScanHtmlComment(Lexer lexer)
    {
        while (WideChar.IsSpace(lexer.Lookahead) || lexer.Lookahead == 0x2028 || lexer.Lookahead == 0x2029)
        {
            Skip(lexer);
        }

        const string commentStart = "<!--";
        const string commentEnd = "-->";

        if (lexer.Lookahead == '<')
        {
            for (var i = 0; i < 4; i++)
            {
                if (lexer.Lookahead != commentStart[i])
                {
                    return false;
                }

                Advance(lexer);
            }
        }
        else if (lexer.Lookahead == '-')
        {
            for (var i = 0; i < 3; i++)
            {
                if (lexer.Lookahead != commentEnd[i])
                {
                    return false;
                }

                Advance(lexer);
            }
        }
        else
        {
            return false;
        }

        while (lexer.Lookahead != 0 && lexer.Lookahead != '\n' && lexer.Lookahead != 0x2028 &&
               lexer.Lookahead != 0x2029)
        {
            Advance(lexer);
        }

        lexer.ResultSymbol = HtmlComment;
        lexer.MarkEnd();

        return true;
    }

    private static bool ScanJsxText(Lexer lexer)
    {
        // saw_text will be true if we see any non-whitespace content, or any whitespace content that is not a newline and
        // does not immediately follow a newline.
        var sawText = false;
        // at_newline will be true if we are currently at a newline, or if we are at whitespace that is not a newline but
        // immediately follows a newline.
        var atNewline = false;

        while (lexer.Lookahead != 0 && lexer.Lookahead != '<' && lexer.Lookahead != '>' && lexer.Lookahead != '{' &&
               lexer.Lookahead != '}' && lexer.Lookahead != '&')
        {
            var isWspace = WideChar.IsSpace(lexer.Lookahead);
            if (lexer.Lookahead == '\n')
            {
                atNewline = true;
            }
            else
            {
                // If at_newline is already true, and we see some whitespace, then it must stay true.
                // Otherwise, it should be false.
                //
                // See the table below to determine the logic for computing `saw_text`.
                //
                // |------------------------------------|
                // | at_newline | is_wspace | saw_text  |
                // |------------|-----------|-----------|
                // | false (0)  | false (0) | true  (1) |
                // | false (0)  | true  (1) | true  (1) |
                // | true  (1)  | false (0) | true  (1) |
                // | true  (1)  | true  (1) | false (0) |
                // |------------------------------------|

                atNewline &= isWspace;
                if (!atNewline)
                {
                    sawText = true;
                }
            }

            Advance(lexer);
        }

        lexer.ResultSymbol = JsxText;
        return sawText;
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[TemplateChars])
        {
            if (validSymbols[AutomaticSemicolon])
            {
                return false;
            }

            return ScanTemplateChars(lexer);
        }

        if (validSymbols[JsxText] && ScanJsxText(lexer))
        {
            return true;
        }

        if (validSymbols[AutomaticSemicolon])
        {
            var scannedComment = false;
            var ret = ScanAutomaticSemicolon(lexer, !validSymbols[LogicalOr], ref scannedComment);
            if (!ret && !scannedComment && validSymbols[TernaryQmark] && lexer.Lookahead == '?')
            {
                return ScanTernaryQmark(lexer);
            }

            return ret;
        }

        if (validSymbols[TernaryQmark])
        {
            return ScanTernaryQmark(lexer);
        }

        if (validSymbols[HtmlComment] && !validSymbols[LogicalOr] && !validSymbols[EscapeSequence] &&
            !validSymbols[RegexPattern])
        {
            return ScanHtmlComment(lexer);
        }

        return false;
    }
}
