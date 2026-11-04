namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-rust: a port of its <c>src/scanner.c</c> (MIT, Maxim Sokolov). Its state is the
/// number of hashes that opened the current raw string literal.
/// </summary>
public sealed class RustScanner : IExternalScanner
{
    // enum TokenType
    private const int StringContent = 0;
    private const int RawStringLiteralStart = 1;
    private const int RawStringLiteralContent = 2;
    private const int RawStringLiteralEnd = 3;
    private const int FloatLiteral = 4;
    private const int BlockOuterDocMarker = 5;
    private const int BlockInnerDocMarker = 6;
    private const int BlockCommentContent = 7;
    private const int LineDocContent = 8;
    private const int ErrorSentinel = 9;

    private byte _openingHashCount;

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        buffer[0] = _openingHashCount;
        return 1;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _openingHashCount = 0;
        if (state.Length == 1)
        {
            _openingHashCount = state[0];
        }
    }

    private static bool IsNumChar(int c) => c == '_' || WideChar.IsDigit(c);

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    private static bool ProcessString(Lexer lexer)
    {
        var hasContent = false;
        while (true)
        {
            if (lexer.Lookahead == '"' || lexer.Lookahead == '\\')
            {
                break;
            }

            if (lexer.IsAtEnd)
            {
                return false;
            }

            hasContent = true;
            Advance(lexer);
        }

        lexer.ResultSymbol = StringContent;
        lexer.MarkEnd();
        return hasContent;
    }

    private bool ScanRawStringStart(Lexer lexer)
    {
        if (lexer.Lookahead == 'b' || lexer.Lookahead == 'c')
        {
            Advance(lexer);
        }

        if (lexer.Lookahead != 'r')
        {
            return false;
        }

        Advance(lexer);

        byte openingHashCount = 0;
        while (lexer.Lookahead == '#')
        {
            Advance(lexer);
            openingHashCount++;
        }

        if (lexer.Lookahead != '"')
        {
            return false;
        }

        Advance(lexer);
        _openingHashCount = openingHashCount;

        lexer.ResultSymbol = RawStringLiteralStart;
        return true;
    }

    private bool ScanRawStringContent(Lexer lexer)
    {
        while (true)
        {
            if (lexer.IsAtEnd)
            {
                return false;
            }

            if (lexer.Lookahead == '"')
            {
                lexer.MarkEnd();
                Advance(lexer);
                uint hashCount = 0;
                while (lexer.Lookahead == '#' && hashCount < _openingHashCount)
                {
                    Advance(lexer);
                    hashCount++;
                }

                if (hashCount == _openingHashCount)
                {
                    lexer.ResultSymbol = RawStringLiteralContent;
                    return true;
                }
            }
            else
            {
                Advance(lexer);
            }
        }
    }

    private bool ScanRawStringEnd(Lexer lexer)
    {
        Advance(lexer);
        for (uint i = 0; i < _openingHashCount; i++)
        {
            Advance(lexer);
        }

        lexer.ResultSymbol = RawStringLiteralEnd;
        return true;
    }

    private static bool ProcessFloatLiteral(Lexer lexer)
    {
        lexer.ResultSymbol = FloatLiteral;

        Advance(lexer);
        while (IsNumChar(lexer.Lookahead))
        {
            Advance(lexer);
        }

        var hasFraction = false;
        var hasExponent = false;

        if (lexer.Lookahead == '.')
        {
            hasFraction = true;
            Advance(lexer);
            if (WideChar.IsAlpha(lexer.Lookahead))
            {
                // The dot is followed by a letter: 1.max(2) => not a float but an integer
                return false;
            }

            if (lexer.Lookahead == '.')
            {
                return false;
            }

            while (IsNumChar(lexer.Lookahead))
            {
                Advance(lexer);
            }
        }

        lexer.MarkEnd();

        if (lexer.Lookahead == 'e' || lexer.Lookahead == 'E')
        {
            hasExponent = true;
            Advance(lexer);
            if (lexer.Lookahead == '+' || lexer.Lookahead == '-')
            {
                Advance(lexer);
            }

            if (!IsNumChar(lexer.Lookahead))
            {
                return true;
            }

            Advance(lexer);
            while (IsNumChar(lexer.Lookahead))
            {
                Advance(lexer);
            }

            lexer.MarkEnd();
        }

        if (!hasExponent && !hasFraction)
        {
            return false;
        }

        if (lexer.Lookahead != 'u' && lexer.Lookahead != 'i' && lexer.Lookahead != 'f')
        {
            return true;
        }

        Advance(lexer);
        if (!WideChar.IsDigit(lexer.Lookahead))
        {
            return true;
        }

        while (WideChar.IsDigit(lexer.Lookahead))
        {
            Advance(lexer);
        }

        lexer.MarkEnd();
        return true;
    }

    private static bool ProcessLineDocContent(Lexer lexer)
    {
        lexer.ResultSymbol = LineDocContent;
        while (true)
        {
            if (lexer.IsAtEnd)
            {
                return true;
            }

            if (lexer.Lookahead == '\n')
            {
                // Include the newline in the doc content node.
                // Line endings are useful for markdown injection.
                Advance(lexer);
                return true;
            }

            Advance(lexer);
        }
    }

    // BlockCommentState
    private const int LeftForwardSlash = 0;
    private const int LeftAsterisk = 1;
    private const int Continuing = 2;

    private struct BlockCommentProcessing
    {
        public int State;
        public uint NestingDepth;
    }

    // The characters are C chars: the code points truncated to a signed byte
    private static void ProcessLeftForwardSlash(ref BlockCommentProcessing processing, sbyte current)
    {
        if (current == '*')
        {
            processing.NestingDepth += 1;
        }

        processing.State = Continuing;
    }

    private static void ProcessLeftAsterisk(ref BlockCommentProcessing processing, sbyte current, Lexer lexer)
    {
        if (current == '*')
        {
            lexer.MarkEnd();
            processing.State = LeftAsterisk;
            return;
        }

        if (current == '/')
        {
            processing.NestingDepth -= 1;
        }

        processing.State = Continuing;
    }

    private static void ProcessContinuing(ref BlockCommentProcessing processing, sbyte current)
    {
        switch (current)
        {
            case (sbyte)'/':
                processing.State = LeftForwardSlash;
                break;
            case (sbyte)'*':
                processing.State = LeftAsterisk;
                break;
        }
    }

    private static bool ProcessBlockComment(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        var first = unchecked((sbyte)lexer.Lookahead);
        // The first character is stored so we can safely advance inside
        // these if blocks. However, because we only store one, we can only
        // safely advance 1 time. Since there's a chance that an advance could
        // happen in one state, we must advance in all states to ensure that
        // the program ends up in a sane state prior to processing the block
        // comment if need be.
        if (validSymbols[BlockInnerDocMarker] && first == '!')
        {
            lexer.ResultSymbol = BlockInnerDocMarker;
            Advance(lexer);
            return true;
        }

        if (validSymbols[BlockOuterDocMarker] && first == '*')
        {
            Advance(lexer);
            lexer.MarkEnd();
            // If the next token is a / that means that it's an empty block comment.
            if (lexer.Lookahead == '/')
            {
                return false;
            }

            // If the next token is a * that means that this isn't a BLOCK_OUTER_DOC_MARKER
            // as BLOCK_OUTER_DOC_MARKER's only have 2 * not 3 or more.
            if (lexer.Lookahead != '*')
            {
                lexer.ResultSymbol = BlockOuterDocMarker;
                return true;
            }
        }
        else
        {
            Advance(lexer);
        }

        if (validSymbols[BlockCommentContent])
        {
            var processing = new BlockCommentProcessing { State = Continuing, NestingDepth = 1 };
            // Manually set the current state based on the first character
            switch (first)
            {
                case (sbyte)'*':
                    processing.State = LeftAsterisk;
                    if (lexer.Lookahead == '/')
                    {
                        // This case can happen in an empty doc block comment
                        // like /*!*/. The comment has no contents, so bail.
                        return false;
                    }

                    break;
                case (sbyte)'/':
                    processing.State = LeftForwardSlash;
                    break;
                default:
                    processing.State = Continuing;
                    break;
            }

            // For the purposes of actually parsing rust code, this
            // is incorrect as it considers an unterminated block comment
            // to be an error. However, for the purposes of syntax highlighting
            // this should be considered successful as otherwise you are not able
            // to syntax highlight a block of code prior to closing the
            // block comment
            while (!lexer.IsAtEnd && processing.NestingDepth != 0)
            {
                // Set first to the current lookahead as that is the second character
                // as we force an advance in the above code when we are checking if we
                // need to handle a block comment inner or outer doc comment signifier
                // node
                first = unchecked((sbyte)lexer.Lookahead);
                switch (processing.State)
                {
                    case LeftForwardSlash:
                        ProcessLeftForwardSlash(ref processing, first);
                        break;
                    case LeftAsterisk:
                        ProcessLeftAsterisk(ref processing, first, lexer);
                        break;
                    case Continuing:
                        lexer.MarkEnd();
                        ProcessContinuing(ref processing, first);
                        break;
                    default:
                        break;
                }

                Advance(lexer);
                if (first == '/' && processing.NestingDepth != 0)
                {
                    lexer.MarkEnd();
                }
            }

            lexer.ResultSymbol = BlockCommentContent;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        // The documentation states that if the lexical analysis fails for some reason
        // they will mark every state as valid and pass it to the external scanner
        // However, we can't do anything to help them recover in that case so we
        // should just fail.
        /*
          link: https://tree-sitter.github.io/tree-sitter/creating-parsers#external-scanners
          If a syntax error is encountered during regular parsing, Tree-sitter’s
          first action during error recovery will be to call the external scanner’s
          scan function with all tokens marked valid. The scanner should detect this
          case and handle it appropriately. One simple method of detection is to add
          an unused token to the end of the externals array, for example

          externals: $ => [$.token1, $.token2, $.error_sentinel],

          then check whether that token is marked valid to determine whether
          Tree-sitter is in error correction mode.
        */
        if (validSymbols[ErrorSentinel])
        {
            return false;
        }

        if (validSymbols[BlockCommentContent] || validSymbols[BlockInnerDocMarker] ||
            validSymbols[BlockOuterDocMarker])
        {
            return ProcessBlockComment(lexer, validSymbols);
        }

        if (validSymbols[StringContent] && !validSymbols[FloatLiteral])
        {
            return ProcessString(lexer);
        }

        if (validSymbols[LineDocContent])
        {
            return ProcessLineDocContent(lexer);
        }

        while (WideChar.IsSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        if (validSymbols[RawStringLiteralStart] &&
            (lexer.Lookahead == 'r' || lexer.Lookahead == 'b' || lexer.Lookahead == 'c'))
        {
            return ScanRawStringStart(lexer);
        }

        if (validSymbols[RawStringLiteralContent])
        {
            return ScanRawStringContent(lexer);
        }

        if (validSymbols[RawStringLiteralEnd] && lexer.Lookahead == '"')
        {
            return ScanRawStringEnd(lexer);
        }

        if (validSymbols[FloatLiteral] && WideChar.IsDigit(lexer.Lookahead))
        {
            return ProcessFloatLiteral(lexer);
        }

        return false;
    }
}
