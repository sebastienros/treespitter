namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-c-sharp: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld, Damien Guard,
/// Amaan Qureshi). Its state is the quote count of the current raw string and the stack of the interpolated strings.
/// tree-sitter-razor has the same scanner (and the same external tokens) and uses this class.
/// </summary>
public sealed class CSharpScanner : IExternalScanner
{
    // enum TokenType
    private const int OptSemi = 0;
    private const int InterpolationRegularStart = 1;
    private const int InterpolationVerbatimStart = 2;
    private const int InterpolationRawStart = 3;
    private const int InterpolationStartQuote = 4;
    private const int InterpolationEndQuote = 5;
    private const int InterpolationOpenBrace = 6;
    private const int InterpolationCloseBrace = 7;
    private const int InterpolationStringContent = 8;
    private const int RawStringStart = 9;
    private const int RawStringEnd = 10;
    private const int RawStringContent = 11;

    // StringType
    private const int Regular = 1 << 0;
    private const int Verbatim = 1 << 1;
    private const int Raw = 1 << 2;

    private const int SerializationBufferSize = ExternalScanner.StateCapacity;

    private struct Interpolation
    {
        public byte DollarCount;
        public byte OpenBraceCount;
        public byte QuoteCount;

        // A StringType: an enum, an int in C
        public int StringType;
    }

    private static bool IsRegular(in Interpolation interpolation) => (interpolation.StringType & Regular) != 0;

    private static bool IsVerbatim(in Interpolation interpolation) => (interpolation.StringType & Verbatim) != 0;

    private static bool IsRaw(in Interpolation interpolation) => (interpolation.StringType & Raw) != 0;

    private byte _quoteCount;

    // Array(Interpolation): the size is the number of elements in use, the contents are kept when the size shrinks
    private Interpolation[] _interpolationStack = [];
    private int _interpolationStackSize;

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    private void ArrayPush(Interpolation interpolation)
    {
        ArrayReserve(_interpolationStackSize + 1);
        _interpolationStack[_interpolationStackSize++] = interpolation;
    }

    private void ArrayReserve(int capacity)
    {
        if (capacity > _interpolationStack.Length)
        {
            var contents = new Interpolation[Math.Max(capacity, Math.Max(_interpolationStack.Length * 2, 8))];
            Array.Copy(_interpolationStack, contents, _interpolationStack.Length);
            _interpolationStack = contents;
        }
    }

    private ref Interpolation ArrayBack() => ref _interpolationStack[_interpolationStackSize - 1];

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        if (_interpolationStackSize * 4 + 2 > SerializationBufferSize)
        {
            return 0;
        }

        var size = 0;

        buffer[size++] = _quoteCount;
        buffer[size++] = (byte)_interpolationStackSize;

        for (var i = 0; i < _interpolationStackSize; i++)
        {
            var interpolation = _interpolationStack[i];
            buffer[size++] = interpolation.DollarCount;
            buffer[size++] = interpolation.OpenBraceCount;
            buffer[size++] = interpolation.QuoteCount;
            buffer[size++] = (byte)interpolation.StringType;
        }

        return size;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _quoteCount = 0;
        _interpolationStackSize = 0;
        var size = 0;

        if (state.Length > 0)
        {
            _quoteCount = state[size++];
            _interpolationStackSize = state[size++];
            ArrayReserve(_interpolationStackSize);

            for (var i = 0; i < _interpolationStackSize; i++)
            {
                var interpolation = default(Interpolation);
                interpolation.DollarCount = state[size++];
                interpolation.OpenBraceCount = state[size++];
                interpolation.QuoteCount = state[size++];
                interpolation.StringType = state[size++];
                _interpolationStack[i] = interpolation;
            }
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        byte braceAdvanced = 0;
        byte quoteCount = 0;
        var didAdvance = false;

        // error recovery, gives better trees this way
        if (validSymbols[OptSemi] && validSymbols[InterpolationRegularStart])
        {
            return false;
        }

        if (validSymbols[OptSemi])
        {
            lexer.ResultSymbol = OptSemi;
            if (lexer.Lookahead == ';')
            {
                Advance(lexer);
            }

            return true;
        }

        if (validSymbols[RawStringStart])
        {
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '"')
            {
                while (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    quoteCount++;
                }

                if (quoteCount >= 3)
                {
                    lexer.ResultSymbol = RawStringStart;
                    _quoteCount = quoteCount;
                    return true;
                }
            }
        }

        if (validSymbols[RawStringEnd] && lexer.Lookahead == '"')
        {
            while (lexer.Lookahead == '"')
            {
                Advance(lexer);
                quoteCount++;
            }

            if (quoteCount == _quoteCount)
            {
                lexer.ResultSymbol = RawStringEnd;
                _quoteCount = 0;
                return true;
            }

            didAdvance = quoteCount > 0;
        }

        if (validSymbols[RawStringContent])
        {
            while (lexer.Lookahead != 0)
            {
                if (lexer.Lookahead == '"')
                {
                    lexer.MarkEnd();
                    quoteCount = 0;

                    while (lexer.Lookahead == '"')
                    {
                        Advance(lexer);
                        quoteCount++;
                    }

                    if (quoteCount == _quoteCount)
                    {
                        lexer.ResultSymbol = RawStringContent;
                        return true;
                    }
                }

                Advance(lexer);
                didAdvance = true;
            }

            lexer.MarkEnd();
            lexer.ResultSymbol = RawStringContent;
            return true;
        }

        if (validSymbols[InterpolationRegularStart] || validSymbols[InterpolationVerbatimStart] ||
            validSymbols[InterpolationRawStart])
        {
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            byte dollarAdvanced = 0;

            var isVerbatim = false;

            if (lexer.Lookahead == '@')
            {
                isVerbatim = true;
                Advance(lexer);
            }

            while (lexer.Lookahead == '$' && quoteCount == 0)
            {
                Advance(lexer);
                dollarAdvanced++;
            }

            if (dollarAdvanced > 0 && (lexer.Lookahead == '"' || lexer.Lookahead == '@'))
            {
                lexer.ResultSymbol = InterpolationRegularStart;
                var interpolation = new Interpolation
                {
                    DollarCount = dollarAdvanced,
                    OpenBraceCount = 0,
                    QuoteCount = 0,
                    StringType = 0,
                };

                if (isVerbatim || lexer.Lookahead == '@')
                {
                    if (lexer.Lookahead == '@')
                    {
                        Advance(lexer);
                        isVerbatim = true;
                    }

                    lexer.ResultSymbol = InterpolationVerbatimStart;
                    interpolation.StringType = Verbatim;
                }

                lexer.MarkEnd();
                Advance(lexer);

                if (lexer.Lookahead == '"' && !isVerbatim)
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '"')
                    {
                        lexer.ResultSymbol = InterpolationRawStart;
                        interpolation.StringType |= Raw;
                        ArrayPush(interpolation);
                    }

                    // If we find 1 or 3 quotes, we push an interpolation.
                    // If there's only two quotes, that's just an empty string
                }
                else
                {
                    interpolation.StringType |= Regular;
                    ArrayPush(interpolation);
                }

                return true;
            }
        }

        if (validSymbols[InterpolationStartQuote] && _interpolationStackSize > 0)
        {
            ref var currentInterpolation = ref ArrayBack();

            if (IsVerbatim(currentInterpolation) || IsRegular(currentInterpolation))
            {
                if (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    currentInterpolation.QuoteCount++;
                }
            }
            else
            {
                while (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    currentInterpolation.QuoteCount++;
                }
            }

            lexer.ResultSymbol = InterpolationStartQuote;
            return currentInterpolation.QuoteCount > 0;
        }

        if (validSymbols[InterpolationEndQuote] && _interpolationStackSize > 0)
        {
            ref var currentInterpolation = ref ArrayBack();

            while (lexer.Lookahead == '"')
            {
                Advance(lexer);
                quoteCount++;
            }

            if (quoteCount == currentInterpolation.QuoteCount)
            {
                lexer.ResultSymbol = InterpolationEndQuote;
                _interpolationStackSize--;
                return true;
            }

            didAdvance = quoteCount > 0;
        }

        if (validSymbols[InterpolationOpenBrace] && _interpolationStackSize > 0)
        {
            ref var currentInterpolation = ref ArrayBack();

            while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.DollarCount)
            {
                Advance(lexer);
                braceAdvanced++;
            }

            if (braceAdvanced > 0 && braceAdvanced == currentInterpolation.DollarCount &&
                (braceAdvanced == 0 || lexer.Lookahead != '{'))
            {
                currentInterpolation.OpenBraceCount = braceAdvanced;
                lexer.ResultSymbol = InterpolationOpenBrace;
                return true;
            }
        }

        if (validSymbols[InterpolationCloseBrace] && _interpolationStackSize > 0)
        {
            // brace_advanced in C, which shadows the outer one
            byte closeBraceAdvanced = 0;
            ref var currentInterpolation = ref ArrayBack();

            while (WideChar.IsSpace(lexer.Lookahead))
            {
                Advance(lexer);
            }

            while (lexer.Lookahead == '}')
            {
                Advance(lexer);
                closeBraceAdvanced++;

                if (closeBraceAdvanced == currentInterpolation.OpenBraceCount)
                {
                    currentInterpolation.OpenBraceCount = 0;
                    lexer.ResultSymbol = InterpolationCloseBrace;
                    return true;
                }
            }

            return false;
        }

        if (validSymbols[InterpolationStringContent] && _interpolationStackSize > 0)
        {
            lexer.ResultSymbol = InterpolationStringContent;
            ref var currentInterpolation = ref ArrayBack();

            while (lexer.Lookahead != 0)
            {
                // top-down approach, first see if it's raw
                if (IsRaw(currentInterpolation))
                {
                    if (lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == '"')
                        {
                            Advance(lexer);
                            byte quoteAdvanced = 2;
                            while (lexer.Lookahead == '"')
                            {
                                quoteAdvanced++;
                                Advance(lexer);
                            }

                            if (quoteAdvanced == currentInterpolation.QuoteCount)
                            {
                                return didAdvance;
                            }
                        }
                    }

                    if (lexer.Lookahead == '{')
                    {
                        lexer.MarkEnd();

                        while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.OpenBraceCount)
                        {
                            Advance(lexer);
                            braceAdvanced++;
                        }

                        if (braceAdvanced == currentInterpolation.OpenBraceCount &&
                            (braceAdvanced == 0 || lexer.Lookahead != '{'))
                        {
                            return didAdvance;
                        }
                    }
                }

                // then verbatim, since it could be verbatim + raw, but run the raw branch first
                else if (IsVerbatim(currentInterpolation))
                {
                    if (lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == '"')
                        {
                            Advance(lexer);
                            continue;
                        }

                        return didAdvance;
                    }

                    if (lexer.Lookahead == '{')
                    {
                        lexer.MarkEnd();

                        while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.OpenBraceCount)
                        {
                            Advance(lexer);
                            braceAdvanced++;
                        }

                        if (braceAdvanced == currentInterpolation.OpenBraceCount &&
                            (braceAdvanced == 0 || lexer.Lookahead != '{'))
                        {
                            return didAdvance;
                        }
                    }
                }

                // finally regular
                else if (IsRegular(currentInterpolation))
                {
                    if (lexer.Lookahead == '\\' || lexer.Lookahead == '\n' || lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        return didAdvance;
                    }

                    if (lexer.Lookahead == '{')
                    {
                        lexer.MarkEnd();

                        while (lexer.Lookahead == '{' && braceAdvanced < currentInterpolation.OpenBraceCount)
                        {
                            Advance(lexer);
                            braceAdvanced++;
                        }

                        if (braceAdvanced == currentInterpolation.OpenBraceCount &&
                            (braceAdvanced == 0 || lexer.Lookahead != '{'))
                        {
                            // if we're in a brace we're not allowed to
                            // collect more than the open_brace_count
                            return didAdvance;
                        }
                    }
                }

                if (lexer.Lookahead != '{')
                {
                    braceAdvanced = 0;
                }

                Advance(lexer);
                didAdvance = true;
            }

            lexer.MarkEnd();
            return didAdvance;
        }

        return false;
    }
}
