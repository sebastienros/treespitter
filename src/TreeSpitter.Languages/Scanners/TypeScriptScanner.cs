namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-typescript, shared by the TypeScript and TSX grammars: a port of its
/// <c>common/scanner.h</c> (MIT, Max Brunsfeld), which the <c>src/scanner.c</c> files of both grammars include. It has
/// no state.
/// </summary>
public sealed class TypeScriptScanner : IExternalScanner
{
    private const int AutomaticSemicolon = 0;
    private const int TemplateChars = 1;
    private const int TernaryQmark = 2;
    private const int HtmlComment = 3;
    private const int LogicalOr = 4;
    private const int EscapeSequence = 5;
    private const int RegexPattern = 6;
    private const int JsxText = 7;
    private const int FunctionSignatureAutomaticSemicolon = 8;
    private const int ErrorRecovery = 9;

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

    private static bool ScanWhitespaceAndComments(Lexer lexer, ref bool scannedComment)
    {
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
                    while (lexer.Lookahead != 0 && lexer.Lookahead != '\n')
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
                                break;
                            }
                        }
                        else
                        {
                            Skip(lexer);
                        }
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return true;
            }
        }
    }

    private static bool ScanAutomaticSemicolon(Lexer lexer, ReadOnlySpan<bool> validSymbols, ref bool scannedComment)
    {
        lexer.ResultSymbol = AutomaticSemicolon;
        lexer.MarkEnd();

        while (true)
        {
            if (lexer.Lookahead == 0)
            {
                return true;
            }

            if (lexer.Lookahead == '}')
            {
                // Automatic semicolon insertion breaks detection of object patterns
                // in a typed context:
                //   type F = ({a}: {a: number}) => number;
                // Therefore, disable automatic semicolons when followed by typing
                do
                {
                    Skip(lexer);
                }
                while (WideChar.IsSpace(lexer.Lookahead));

                if (lexer.Lookahead == ':')
                {
                    return validSymbols[LogicalOr]; // Don't return false if we're in a ternary by checking if || is valid
                }

                return true;
            }

            if (!WideChar.IsSpace(lexer.Lookahead))
            {
                return false;
            }

            if (lexer.Lookahead == '\n')
            {
                break;
            }

            Skip(lexer);
        }

        Skip(lexer);

        if (!ScanWhitespaceAndComments(lexer, ref scannedComment))
        {
            return false;
        }

        switch (lexer.Lookahead)
        {
            case '`':
            case ',':
            case '.':
            case ';':
            case '*':
            case '%':
            case '>':
            case '<':
            case '=':
            case '?':
            case '^':
            case '|':
            case '&':
            case '/':
            case ':':
                return false;

            case '{':
                if (validSymbols[FunctionSignatureAutomaticSemicolon])
                {
                    return false;
                }

                break;

            // Don't insert a semicolon before a '[' or '(', unless we're parsing
            // a type. Detect whether we're parsing a type or an expression using
            // the validity of a binary operator token.
            case '(':
            case '[':
                if (validSymbols[LogicalOr])
                {
                    return false;
                }

                break;

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

            /* Optional chaining. */
            if (lexer.Lookahead == '?' || lexer.Lookahead == '.')
            {
                return false;
            }

            lexer.MarkEnd();
            lexer.ResultSymbol = TernaryQmark;

            /* TypeScript optional arguments contain the ?: sequence, possibly
               with whitespace. */
            while (true)
            {
                if (!WideChar.IsSpace(lexer.Lookahead))
                {
                    break;
                }

                Advance(lexer);
            }

            if (lexer.Lookahead == ':' || lexer.Lookahead == ')' || lexer.Lookahead == ',')
            {
                return false;
            }

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

    private static bool ScanClosingComment(Lexer lexer)
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

        if (validSymbols[AutomaticSemicolon] || validSymbols[FunctionSignatureAutomaticSemicolon])
        {
            var scannedComment = false;
            var ret = ScanAutomaticSemicolon(lexer, validSymbols, ref scannedComment);
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
            return ScanClosingComment(lexer);
        }

        return false;
    }
}
