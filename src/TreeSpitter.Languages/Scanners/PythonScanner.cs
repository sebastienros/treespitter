namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-python: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld). Its state is
/// the stack of indentations, the stack of string delimiters and whether the scanner is inside an interpolated string.
/// </summary>
public sealed class PythonScanner : IExternalScanner
{
    // enum TokenType
    private const int Newline = 0;
    private const int Indent = 1;
    private const int Dedent = 2;
    private const int StringStart = 3;
    private const int StringContent = 4;
    private const int EscapeInterpolation = 5;
    private const int StringEnd = 6;
    private const int Comment = 7;
    private const int CloseParen = 8;
    private const int CloseBracket = 9;
    private const int CloseBrace = 10;
    private const int Except = 11;

    // Flags
    private const byte SingleQuote = 1 << 0;
    private const byte DoubleQuote = 1 << 1;
    private const byte BackQuote = 1 << 2;
    private const byte Raw = 1 << 3;
    private const byte Format = 1 << 4;
    private const byte Triple = 1 << 5;
    private const byte Bytes = 1 << 6;

    private readonly List<ushort> _indents = [];

    // The delimiters are the flags of the C struct Delimiter (a char)
    private readonly List<byte> _delimiters = [];
    private bool _insideInterpolatedString;

    /// <summary>Creates a scanner in its initial state, like <c>tree_sitter_python_external_scanner_create</c>.</summary>
    public PythonScanner()
    {
        Deserialize([]);
    }

    private static bool IsFormat(byte delimiter) => (delimiter & Format) != 0;

    private static bool IsRaw(byte delimiter) => (delimiter & Raw) != 0;

    private static bool IsTriple(byte delimiter) => (delimiter & Triple) != 0;

    private static bool IsBytes(byte delimiter) => (delimiter & Bytes) != 0;

    private static int EndCharacter(byte delimiter)
    {
        if ((delimiter & SingleQuote) != 0)
        {
            return '\'';
        }

        if ((delimiter & DoubleQuote) != 0)
        {
            return '"';
        }

        if ((delimiter & BackQuote) != 0)
        {
            return '`';
        }

        return 0;
    }

    private static void SetFormat(ref byte delimiter) => delimiter |= Format;

    private static void SetRaw(ref byte delimiter) => delimiter |= Raw;

    private static void SetTriple(ref byte delimiter) => delimiter |= Triple;

    private static void SetBytes(ref byte delimiter) => delimiter |= Bytes;

    private static void SetEndCharacter(ref byte delimiter, int character)
    {
        switch (character)
        {
            case '\'':
                delimiter |= SingleQuote;
                break;
            case '"':
                delimiter |= DoubleQuote;
                break;
            case '`':
                delimiter |= BackQuote;
                break;
        }
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        var errorRecoveryMode = validSymbols[StringContent] && validSymbols[Indent];
        var withinBrackets = validSymbols[CloseBrace] || validSymbols[CloseParen] || validSymbols[CloseBracket];

        var advancedOnce = false;
        if (validSymbols[EscapeInterpolation] && _delimiters.Count > 0 &&
            (lexer.Lookahead == '{' || lexer.Lookahead == '}') && !errorRecoveryMode)
        {
            var delimiter = _delimiters[_delimiters.Count - 1];
            if (IsFormat(delimiter))
            {
                lexer.MarkEnd();
                var isLeftBrace = lexer.Lookahead == '{';
                Advance(lexer);
                advancedOnce = true;
                if ((lexer.Lookahead == '{' && isLeftBrace) || (lexer.Lookahead == '}' && !isLeftBrace))
                {
                    Advance(lexer);
                    lexer.MarkEnd();
                    lexer.ResultSymbol = EscapeInterpolation;
                    return true;
                }

                return false;
            }
        }

        if (validSymbols[StringContent] && _delimiters.Count > 0 && !errorRecoveryMode)
        {
            var delimiter = _delimiters[_delimiters.Count - 1];
            var endChar = EndCharacter(delimiter);
            var hasContent = advancedOnce;
            while (lexer.Lookahead != 0)
            {
                if ((advancedOnce || lexer.Lookahead == '{' || lexer.Lookahead == '}') && IsFormat(delimiter))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = StringContent;
                    return hasContent;
                }

                if (lexer.Lookahead == '\\')
                {
                    if (IsRaw(delimiter))
                    {
                        // Step over the backslash.
                        Advance(lexer);
                        // Step over any escaped quotes.
                        if (lexer.Lookahead == EndCharacter(delimiter) || lexer.Lookahead == '\\')
                        {
                            Advance(lexer);
                        }

                        // Step over newlines
                        if (lexer.Lookahead == '\r')
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == '\n')
                            {
                                Advance(lexer);
                            }
                        }
                        else if (lexer.Lookahead == '\n')
                        {
                            Advance(lexer);
                        }

                        continue;
                    }

                    if (IsBytes(delimiter))
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == 'N' || lexer.Lookahead == 'u' || lexer.Lookahead == 'U')
                        {
                            // In bytes string, \N{...}, \uXXXX and \UXXXXXXXX are
                            // not escape sequences
                            // https://docs.python.org/3/reference/lexical_analysis.html#string-and-bytes-literals
                            Advance(lexer);
                        }
                        else
                        {
                            lexer.ResultSymbol = StringContent;
                            return hasContent;
                        }
                    }
                    else
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = StringContent;
                        return hasContent;
                    }
                }
                else if (lexer.Lookahead == endChar)
                {
                    if (IsTriple(delimiter))
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == endChar)
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == endChar)
                            {
                                if (hasContent)
                                {
                                    lexer.ResultSymbol = StringContent;
                                }
                                else
                                {
                                    Advance(lexer);
                                    lexer.MarkEnd();
                                    _delimiters.RemoveAt(_delimiters.Count - 1);
                                    lexer.ResultSymbol = StringEnd;
                                    _insideInterpolatedString = false;
                                }

                                return true;
                            }

                            lexer.MarkEnd();
                            lexer.ResultSymbol = StringContent;
                            return true;
                        }

                        lexer.MarkEnd();
                        lexer.ResultSymbol = StringContent;
                        return true;
                    }

                    if (hasContent)
                    {
                        lexer.ResultSymbol = StringContent;
                    }
                    else
                    {
                        Advance(lexer);
                        _delimiters.RemoveAt(_delimiters.Count - 1);
                        lexer.ResultSymbol = StringEnd;
                        _insideInterpolatedString = false;
                    }

                    lexer.MarkEnd();
                    return true;
                }
                else if (lexer.Lookahead == '\n' && hasContent && !IsTriple(delimiter))
                {
                    return false;
                }

                Advance(lexer);
                hasContent = true;
            }
        }

        lexer.MarkEnd();

        var foundEndOfLine = false;
        ushort indentLength = 0;
        var firstCommentIndentLength = -1;
        while (true)
        {
            if (lexer.Lookahead == '\n')
            {
                foundEndOfLine = true;
                indentLength = 0;
                Skip(lexer);
            }
            else if (lexer.Lookahead == ' ')
            {
                indentLength++;
                Skip(lexer);
            }
            else if (lexer.Lookahead == '\r' || lexer.Lookahead == '\f')
            {
                indentLength = 0;
                Skip(lexer);
            }
            else if (lexer.Lookahead == '\t')
            {
                indentLength += 8;
                Skip(lexer);
            }
            else if (lexer.Lookahead == '#' && (validSymbols[Indent] || validSymbols[Dedent] ||
                                                validSymbols[Newline] || validSymbols[Except]))
            {
                // If we haven't found an EOL yet,
                // then this is a comment after an expression:
                //   foo = bar # comment
                // Just return, since we don't want to generate an indent/dedent
                // token.
                if (!foundEndOfLine)
                {
                    return false;
                }

                if (firstCommentIndentLength == -1)
                {
                    firstCommentIndentLength = indentLength;
                }

                while (lexer.Lookahead != 0 && lexer.Lookahead != '\n')
                {
                    Skip(lexer);
                }

                Skip(lexer);
                indentLength = 0;
            }
            else if (lexer.Lookahead == '\\')
            {
                Skip(lexer);
                if (lexer.Lookahead == '\r')
                {
                    Skip(lexer);
                }

                if (lexer.Lookahead == '\n' || lexer.IsAtEnd)
                {
                    Skip(lexer);
                }
                else
                {
                    return false;
                }
            }
            else if (lexer.IsAtEnd)
            {
                indentLength = 0;
                foundEndOfLine = true;
                break;
            }
            else
            {
                break;
            }
        }

        if (foundEndOfLine)
        {
            if (_indents.Count > 0)
            {
                var currentIndentLength = _indents[_indents.Count - 1];

                if (validSymbols[Indent] && indentLength > currentIndentLength)
                {
                    _indents.Add(indentLength);
                    lexer.ResultSymbol = Indent;
                    return true;
                }

                var nextTokIsStringStart =
                    lexer.Lookahead == '"' || lexer.Lookahead == '\'' || lexer.Lookahead == '`';

                if ((validSymbols[Dedent] ||
                     (!validSymbols[Newline] && !(validSymbols[StringStart] && nextTokIsStringStart) &&
                      !withinBrackets)) &&
                    indentLength < currentIndentLength && !_insideInterpolatedString &&

                    // Wait to create a dedent token until we've consumed any
                    // comments
                    // whose indentation matches the current block.
                    firstCommentIndentLength < currentIndentLength)
                {
                    _indents.RemoveAt(_indents.Count - 1);
                    lexer.ResultSymbol = Dedent;
                    return true;
                }
            }

            if (validSymbols[Newline] && !errorRecoveryMode)
            {
                lexer.ResultSymbol = Newline;
                return true;
            }
        }

        if (firstCommentIndentLength == -1 && validSymbols[StringStart])
        {
            byte delimiter = 0;

            var hasFlags = false;
            while (lexer.Lookahead != 0)
            {
                if (lexer.Lookahead == 'f' || lexer.Lookahead == 'F' || lexer.Lookahead == 't' ||
                    lexer.Lookahead == 'T')
                {
                    SetFormat(ref delimiter);
                }
                else if (lexer.Lookahead == 'r' || lexer.Lookahead == 'R')
                {
                    SetRaw(ref delimiter);
                }
                else if (lexer.Lookahead == 'b' || lexer.Lookahead == 'B')
                {
                    SetBytes(ref delimiter);
                }
                else if (lexer.Lookahead != 'u' && lexer.Lookahead != 'U')
                {
                    break;
                }

                hasFlags = true;
                Advance(lexer);
            }

            if (lexer.Lookahead == '`')
            {
                SetEndCharacter(ref delimiter, '`');
                Advance(lexer);
                lexer.MarkEnd();
            }
            else if (lexer.Lookahead == '\'')
            {
                SetEndCharacter(ref delimiter, '\'');
                Advance(lexer);
                lexer.MarkEnd();
                if (lexer.Lookahead == '\'')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '\'')
                    {
                        Advance(lexer);
                        lexer.MarkEnd();
                        SetTriple(ref delimiter);
                    }
                }
            }
            else if (lexer.Lookahead == '"')
            {
                SetEndCharacter(ref delimiter, '"');
                Advance(lexer);
                lexer.MarkEnd();
                if (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '"')
                    {
                        Advance(lexer);
                        lexer.MarkEnd();
                        SetTriple(ref delimiter);
                    }
                }
            }

            if (EndCharacter(delimiter) != 0)
            {
                _delimiters.Add(delimiter);
                lexer.ResultSymbol = StringStart;
                _insideInterpolatedString = IsFormat(delimiter);
                return true;
            }

            if (hasFlags)
            {
                return false;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        var size = 0;

        buffer[size++] = _insideInterpolatedString ? (byte)1 : (byte)0;

        var delimiterCount = _delimiters.Count;
        if (delimiterCount > byte.MaxValue)
        {
            delimiterCount = byte.MaxValue;
        }

        buffer[size++] = (byte)delimiterCount;

        for (var i = 0; i < delimiterCount; i++)
        {
            buffer[size + i] = _delimiters[i];
        }

        size += delimiterCount;

        // The C code writes the pair starting at the last byte of the buffer past its end: the pair is left out
        for (var iter = 1; iter < _indents.Count && size + 1 < buffer.Length; ++iter)
        {
            var indentValue = _indents[iter];
            buffer[size++] = (byte)(indentValue & 0xFF);
            buffer[size++] = (byte)((indentValue >> 8) & 0xFF);
        }

        return size;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Like the C code, an empty state resets the indentations and the delimiters but not whether the scanner is inside
    /// an interpolated string, which is serialized with the other fields.
    /// </remarks>
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _delimiters.Clear();
        _indents.Clear();
        _indents.Add(0);

        if (state.Length > 0)
        {
            var size = 0;

            _insideInterpolatedString = state[size++] != 0;

            int delimiterCount = state[size++];
            for (var i = 0; i < delimiterCount; i++)
            {
                _delimiters.Add(state[size + i]);
            }

            size += delimiterCount;

            for (; size + 1 < state.Length; size += 2)
            {
                var indentValue = (ushort)(state[size] | (state[size + 1] << 8));
                _indents.Add(indentValue);
            }
        }
    }
}
