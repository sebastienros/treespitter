using System.Buffers.Binary;

namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-scala: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld and GitHub). Its
/// state is the stack of indentations, and the indentation, the new lines and the column of the last outdent.
/// </summary>
public sealed class ScalaScanner : IExternalScanner
{
    // enum TokenType
    private const ushort AutomaticSemicolon = 0;
    private const ushort Indent = 1;
    private const ushort Outdent = 2;
    private const ushort SimpleStringStart = 3;
    private const ushort SimpleStringMiddle = 4;
    private const ushort SimpleMultilineStringStart = 5;
    private const ushort InterpolatedStringMiddle = 6;
    private const ushort InterpolatedMultilineStringMiddle = 7;
    private const ushort RawStringStart = 8;
    private const ushort RawStringMiddle = 9;
    private const ushort RawStringMultilineMiddle = 10;
    private const ushort SingleLineStringEnd = 11;
    private const ushort MultilineStringEnd = 12;
    private const ushort Else = 13;
    private const ushort Catch = 14;
    private const ushort Finally = 15;
    private const ushort Extends = 16;
    private const ushort Derives = 17;
    private const ushort With = 18;
    private const ushort ErrorSentinel = 19;

    private readonly List<short> _indents = [];
    private short _lastIndentationSize = -1;
    private short _lastNewlineCount;
    private short _lastColumn = -1;

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        if ((_indents.Count + 3) * sizeof(short) > ExternalScanner.StateCapacity)
        {
            return 0;
        }

        var size = 0;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(size), _lastIndentationSize);
        size += sizeof(short);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(size), _lastNewlineCount);
        size += sizeof(short);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(size), _lastColumn);
        size += sizeof(short);

        for (var i = 0; i < _indents.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(size), _indents[i]);
            size += sizeof(short);
        }

        return size;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _indents.Clear();
        _lastIndentationSize = -1;
        _lastColumn = -1;
        _lastNewlineCount = 0;

        if (state.Length == 0)
        {
            return;
        }

        var size = 0;

        _lastIndentationSize = BinaryPrimitives.ReadInt16LittleEndian(state.Slice(size));
        size += sizeof(short);
        _lastNewlineCount = BinaryPrimitives.ReadInt16LittleEndian(state.Slice(size));
        size += sizeof(short);
        _lastColumn = BinaryPrimitives.ReadInt16LittleEndian(state.Slice(size));
        size += sizeof(short);

        while (size < state.Length)
        {
            _indents.Add(BinaryPrimitives.ReadInt16LittleEndian(state.Slice(size)));
            size += sizeof(short);
        }
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    // We enumerate 3 types of strings that we need to handle differently:
    // 1. Simple strings, `"..."` or `"""..."""`
    // 2. Interpolated strings, `s"..."` or `f"..."` or `foo"..."` or foo"""...""".
    // 3. Raw strings, `raw"..."`
    private enum StringMode
    {
        Simple,
        Interpolated,
        Raw,
    }

    private static bool ScanStringContent(Lexer lexer, bool isMultiline, StringMode stringMode)
    {
        var closingQuoteCount = 0u;
        for (; ; )
        {
            if (lexer.Lookahead == '"')
            {
                Advance(lexer);
                closingQuoteCount++;
                if (!isMultiline)
                {
                    lexer.ResultSymbol = SingleLineStringEnd;
                    lexer.MarkEnd();
                    return true;
                }

                if (closingQuoteCount >= 3 && lexer.Lookahead != '"')
                {
                    lexer.ResultSymbol = MultilineStringEnd;
                    lexer.MarkEnd();
                    return true;
                }
            }
            else if (lexer.Lookahead == '$' && stringMode != StringMode.Simple)
            {
                switch (stringMode)
                {
                    case StringMode.Interpolated:
                        lexer.ResultSymbol = isMultiline ? InterpolatedMultilineStringMiddle : InterpolatedStringMiddle;
                        break;
                    case StringMode.Raw:
                        lexer.ResultSymbol = isMultiline ? RawStringMultilineMiddle : RawStringMiddle;
                        break;
                }

                lexer.MarkEnd();
                return true;
            }
            else
            {
                closingQuoteCount = 0;
                if (lexer.Lookahead == '\\')
                {
                    // Multiline strings ignore escape sequences
                    if (isMultiline || stringMode == StringMode.Raw)
                    {
                        // FIXME: In raw string mode, we have to jump over escaped quotes.
                        Advance(lexer);
                        // In single-line raw strings, `\"` is not translated to `"`, but it also does
                        // not close the string. Likewise, `\\` is not translated to `\`, but it does
                        // stop the second `\` from stopping a double-quote from closing the string.
                        if (!isMultiline && stringMode == StringMode.Raw &&
                            (lexer.Lookahead == '"' || lexer.Lookahead == '\\'))
                        {
                            Advance(lexer);
                        }
                    }
                    else
                    {
                        lexer.ResultSymbol = stringMode == StringMode.Simple ? SimpleStringMiddle : InterpolatedStringMiddle;
                        lexer.MarkEnd();
                        return true;
                    }
                }
                // During error recovery and dynamic precedence resolution, the external
                // scanner will be invoked with all valid_symbols set to true, which means
                // we will be asked to scan a string token when we are not actually in a
                // string context. Here we detect these cases and return false.
                else if (lexer.Lookahead == '\n' && !isMultiline)
                {
                    return false;
                }
                else if (lexer.IsAtEnd)
                {
                    return false;
                }
                else
                {
                    Advance(lexer);
                }
            }
        }
    }

    private static bool DetectCommentStart(Lexer lexer)
    {
        lexer.MarkEnd();
        // Comments should not affect indentation
        if (lexer.Lookahead == '/')
        {
            Advance(lexer);
            if (lexer.Lookahead == '/' || lexer.Lookahead == '*')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ScanWord(Lexer lexer, string word)
    {
        for (var i = 0; i < word.Length; i++)
        {
            if (lexer.Lookahead != word[i])
            {
                return false;
            }

            Advance(lexer);
        }

        return !WideChar.IsAlnum(lexer.Lookahead);
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        var prev = _indents.Count > 0 ? _indents[_indents.Count - 1] : (short)-1;
        short newlineCount = 0;
        short indentationSize = 0;

        while (WideChar.IsSpace(lexer.Lookahead))
        {
            if (lexer.Lookahead == '\n')
            {
                newlineCount++;
                indentationSize = 0;
            }
            else
            {
                indentationSize++;
            }

            Skip(lexer);
        }

        // Before advancing the lexer, check if we can double outdent
        if (
            validSymbols[Outdent] &&
            (
              lexer.Lookahead == 0 ||
              (
                prev != -1 &&
                (
                  lexer.Lookahead == ')' ||
                  lexer.Lookahead == ']' ||
                  lexer.Lookahead == '}'
                )
              ) ||
              (
                _lastIndentationSize != -1 &&
                prev != -1 &&
                _lastIndentationSize < prev
              )
            )
        )
        {
            if (_indents.Count > 0)
            {
                _indents.RemoveAt(_indents.Count - 1);
            }

            lexer.ResultSymbol = Outdent;
            return true;
        }

        _lastIndentationSize = -1;

        if (
            validSymbols[Indent] &&
            newlineCount > 0 &&
            (
              _indents.Count == 0 ||
              indentationSize > _indents[_indents.Count - 1]
            )
        )
        {
            if (DetectCommentStart(lexer))
            {
                return false;
            }

            _indents.Add(indentationSize);
            lexer.ResultSymbol = Indent;
            return true;
        }

        // This saves the indentation_size and newline_count so it can be used
        // in subsequent calls for multiple outdent or auto-semicolon.
        if (validSymbols[Outdent] &&
            (lexer.Lookahead == 0 ||
            (
              newlineCount > 0 &&
              prev != -1 &&
              indentationSize < prev
            )
            )
        )
        {
            if (_indents.Count > 0)
            {
                _indents.RemoveAt(_indents.Count - 1);
            }

            lexer.ResultSymbol = Outdent;
            lexer.MarkEnd();
            if (DetectCommentStart(lexer))
            {
                return false;
            }

            _lastIndentationSize = indentationSize;
            _lastNewlineCount = newlineCount;
            if (lexer.IsAtEnd)
            {
                _lastColumn = -1;
            }
            else
            {
                _lastColumn = (short)lexer.GetColumn();
            }

            return true;
        }

        // Recover newline_count from the outdent reset
        var isEof = lexer.IsAtEnd;
        if (
            (
              _lastNewlineCount > 0 &&
              (isEof && _lastColumn == -1)
            ) ||
            (!isEof && (uint)lexer.GetColumn() == (uint)_lastColumn)
        )
        {
            newlineCount += _lastNewlineCount;
        }

        _lastNewlineCount = 0;

        if (validSymbols[AutomaticSemicolon] && newlineCount > 0)
        {
            // AUTOMATIC_SEMICOLON should not be issued in the middle of expressions
            // Thus, we exit this branch when encountering comments, else/catch clauses, etc.

            lexer.MarkEnd();
            lexer.ResultSymbol = AutomaticSemicolon;

            // Probably, a multi-line field expression, e.g.
            // a
            //  .b
            //  .c
            if (lexer.Lookahead == '.')
            {
                return false;
            }

            // Single-line and multi-line comments
            if (lexer.Lookahead == '/')
            {
                Advance(lexer);
                if (lexer.Lookahead == '/')
                {
                    return false;
                }

                if (lexer.Lookahead == '*')
                {
                    Advance(lexer);
                    while (!lexer.IsAtEnd)
                    {
                        if (lexer.Lookahead == '*')
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == '/')
                            {
                                Advance(lexer);
                                break;
                            }
                        }
                        else
                        {
                            Advance(lexer);
                        }
                    }

                    while (WideChar.IsSpace(lexer.Lookahead))
                    {
                        if (lexer.Lookahead == '\n' || lexer.Lookahead == '\r')
                        {
                            return false;
                        }

                        Skip(lexer);
                    }

                    // If some code is present at the same line after comment end,
                    // we should still produce AUTOMATIC_SEMICOLON, e.g. in
                    // val a = 1
                    // /* comment */ val b = 2
                    return true;
                }
            }

            if (validSymbols[Else])
            {
                return !ScanWord(lexer, "else");
            }

            if (validSymbols[Catch])
            {
                if (ScanWord(lexer, "catch"))
                {
                    return false;
                }
            }

            if (validSymbols[Finally])
            {
                if (ScanWord(lexer, "finally"))
                {
                    return false;
                }
            }

            if (validSymbols[Extends])
            {
                if (ScanWord(lexer, "extends"))
                {
                    return false;
                }
            }

            if (validSymbols[With])
            {
                if (ScanWord(lexer, "with"))
                {
                    return false;
                }
            }

            if (validSymbols[Derives])
            {
                if (ScanWord(lexer, "derives"))
                {
                    return false;
                }
            }

            if (newlineCount > 1)
            {
                return true;
            }

            return true;
        }

        while (WideChar.IsSpace(lexer.Lookahead))
        {
            if (lexer.Lookahead == '\n')
            {
                newlineCount++;
            }

            Skip(lexer);
        }

        if (validSymbols[SimpleStringStart] && lexer.Lookahead == '"')
        {
            Advance(lexer);
            lexer.MarkEnd();

            if (lexer.Lookahead == '"')
            {
                Advance(lexer);
                if (lexer.Lookahead == '"')
                {
                    Advance(lexer);
                    lexer.ResultSymbol = SimpleMultilineStringStart;
                    lexer.MarkEnd();
                    return true;
                }
            }

            lexer.ResultSymbol = SimpleStringStart;
            return true;
        }

        // We need two tokens of lookahead to determine if we are parsing a raw string,
        // the `raw` and the `"`, which is why we need to do it in the external scanner.
        if (validSymbols[RawStringStart] && lexer.Lookahead == 'r')
        {
            Advance(lexer);
            if (lexer.Lookahead == 'a')
            {
                Advance(lexer);
                if (lexer.Lookahead == 'w')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '"')
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = RawStringStart;
                        return true;
                    }
                }
            }
        }

        if (validSymbols[SimpleStringMiddle])
        {
            return ScanStringContent(lexer, false, StringMode.Simple);
        }

        if (validSymbols[InterpolatedStringMiddle])
        {
            return ScanStringContent(lexer, false, StringMode.Interpolated);
        }

        if (validSymbols[RawStringMiddle])
        {
            return ScanStringContent(lexer, false, StringMode.Raw);
        }

        if (validSymbols[RawStringMultilineMiddle])
        {
            return ScanStringContent(lexer, true, StringMode.Raw);
        }

        if (validSymbols[InterpolatedMultilineStringMiddle])
        {
            return ScanStringContent(lexer, true, StringMode.Interpolated);
        }

        // We still need to handle the simple multiline string case, but there is
        // no `MULTILINE_STRING_MIDDLE` token, and `MULTILINE_STRING_END` is used
        // by all three of simple raw, and interpolated multiline strings. So this
        // check needs to come after the `INTERPOLATED_MULTILINE_STRING_MIDDLE` and
        // `RAW_STRING_MULTILINE_MIDDLE` check, so that we can be sure we are in a
        // simple multiline string context.
        if (validSymbols[MultilineStringEnd])
        {
            return ScanStringContent(lexer, true, StringMode.Simple);
        }

        return false;
    }
}
