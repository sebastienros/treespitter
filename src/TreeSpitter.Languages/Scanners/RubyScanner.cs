namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-ruby: a port of its <c>src/scanner.c</c> (MIT, Rob Rix). Its state is the stack
/// of the open literals (strings, symbols, subshells, regular expressions and percent arrays, with their delimiters
/// and nesting depth) and the queue of the open heredocs, with their words; it scans the contents of the literals and
/// heredocs with their interpolations, and the whitespace-sensitive tokens (line breaks, unary and binary operators,
/// splats, element references, hash keys).
/// </summary>
public sealed class RubyScanner : IExternalScanner
{
    // enum TokenType
    private const int LineBreak = 0;
    private const int NoLineBreak = 1;

    // Delimited literals
    private const int SimpleSymbol = 2;
    private const int StringStart = 3;
    private const int SymbolStart = 4;
    private const int SubshellStart = 5;
    private const int RegexStart = 6;
    private const int StringArrayStart = 7;
    private const int SymbolArrayStart = 8;
    private const int HeredocBodyStart = 9;
    private const int StringContent = 10;
    private const int HeredocContent = 11;
    private const int StringEnd = 12;
    private const int HeredocBodyEnd = 13;
    private const int HeredocStart = 14;

    // Whitespace-sensitive tokens
    private const int ForwardSlash = 15;
    private const int BlockAmpersand = 16;
    private const int SplatStar = 17;
    private const int UnaryMinus = 18;
    private const int UnaryMinusNum = 19;
    private const int BinaryMinus = 20;
    private const int BinaryStar = 21;
    private const int SingletonClassLeftAngleLeftAngle = 22;
    private const int HashKeySymbol = 23;
    private const int IdentifierSuffix = 24;
    private const int ConstantSuffix = 25;
    private const int HashSplatStarStar = 26;
    private const int BinaryStarStar = 27;
    private const int ElementReferenceBracket = 28;
    private const int ShortInterpolation = 29;

    private const int None = 30;

    private bool _hasLeadingWhitespace;

    // Array(Literal) literal_stack
    private Literal[] _literalStack = new Literal[8];
    private int _literalStackSize;

    // Array(Heredoc) open_heredocs, and the heredocs that reset freed, reused by the next ones
    private readonly List<Heredoc> _openHeredocs = [];
    private readonly Stack<Heredoc> _freeHeredocs = new();

    // NON_IDENTIFIER_CHARS: is_iden_char takes a char, so the callers truncate the lookahead to its low byte, like the
    // C casts (memchr compares unsigned chars)
    private static bool IsIdenChar(byte c) => c switch
    {
        (byte)'\0' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)' ' or (byte)':' or (byte)';' or (byte)'`' or
        (byte)'"' or (byte)'\'' or (byte)'@' or (byte)'$' or (byte)'#' or (byte)'.' or (byte)',' or (byte)'|' or
        (byte)'^' or (byte)'&' or (byte)'<' or (byte)'=' or (byte)'>' or (byte)'+' or (byte)'-' or (byte)'*' or
        (byte)'/' or (byte)'\\' or (byte)'%' or (byte)'?' or (byte)'!' or (byte)'~' or (byte)'(' or (byte)')' or
        (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' => false,
        _ => true,
    };

    private void Skip(Lexer lexer)
    {
        _hasLeadingWhitespace = true;
        lexer.Advance(true);
    }

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private void Reset()
    {
        _literalStackSize = 0;
        for (var i = 0; i < _openHeredocs.Count; i++)
        {
            FreeHeredoc(_openHeredocs[i]);
        }

        _openHeredocs.Clear();
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        var size = 0;

        if (_literalStackSize * 5 + 2 >= ExternalScanner.StateCapacity)
        {
            return 0;
        }

        buffer[size++] = (byte)_literalStackSize;
        for (var i = 0; i < _literalStackSize; i++)
        {
            ref var literal = ref _literalStack[i];
            buffer[size++] = (byte)literal.Type;
            buffer[size++] = (byte)literal.OpenDelimiter;
            buffer[size++] = (byte)literal.CloseDelimiter;
            buffer[size++] = (byte)literal.NestingDepth;
            buffer[size++] = (byte)(literal.AllowsInterpolation ? 1 : 0);
        }

        buffer[size++] = (byte)_openHeredocs.Count;
        for (var i = 0; i < _openHeredocs.Count; i++)
        {
            var heredoc = _openHeredocs[i];
            if (size + 2 + heredoc.Word.Count >= ExternalScanner.StateCapacity)
            {
                return 0;
            }

            // The check of the C code leaves out 2 of the 4 bytes before the word: when the heredoc ends one byte past
            // the buffer, C writes past it (into the next field of the lexer), here the state is dropped
            if (size + 4 + heredoc.Word.Count > buffer.Length)
            {
                return 0;
            }

            buffer[size++] = (byte)(heredoc.EndWordIndentationAllowed ? 1 : 0);
            buffer[size++] = (byte)(heredoc.AllowsInterpolation ? 1 : 0);
            buffer[size++] = (byte)(heredoc.Started ? 1 : 0);
            buffer[size++] = (byte)heredoc.Word.Count;
            for (var j = 0; j < heredoc.Word.Count; j++)
            {
                buffer[size + j] = heredoc.Word[j];
            }

            size += heredoc.Word.Count;
        }

        return size;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Like the C code, the length of a word is read as a byte, so a state with a word of more than 255 bytes is read
    /// wrong; here the bytes past the end of the state read as 0 (C reads past the buffer).
    /// </remarks>
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        var size = 0;
        _hasLeadingWhitespace = false;
        Reset();

        if (state.Length == 0)
        {
            return;
        }

        var literalDepth = ReadByte(state, ref size);
        for (var j = 0; j < literalDepth; j++)
        {
            var literal = default(Literal);
            literal.Type = (sbyte)ReadByte(state, ref size);
            literal.OpenDelimiter = ReadByte(state, ref size);
            literal.CloseDelimiter = ReadByte(state, ref size);
            literal.NestingDepth = ReadByte(state, ref size);
            literal.AllowsInterpolation = ReadByte(state, ref size) != 0;
            PushLiteral(literal);
        }

        var openHeredocCount = ReadByte(state, ref size);
        for (var j = 0; j < openHeredocCount; j++)
        {
            var heredoc = NewHeredoc();
            heredoc.EndWordIndentationAllowed = ReadByte(state, ref size) != 0;
            heredoc.AllowsInterpolation = ReadByte(state, ref size) != 0;
            heredoc.Started = ReadByte(state, ref size) != 0;

            var wordLength = ReadByte(state, ref size);
            for (var k = 0; k < wordLength; k++)
            {
                heredoc.Word.Add(ReadByte(state, ref size));
            }

            _openHeredocs.Add(heredoc);
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> state, ref int size)
    {
        var value = size < state.Length ? state[size] : (byte)0;
        size++;
        return value;
    }

    private void PushLiteral(Literal literal)
    {
        if (_literalStackSize == _literalStack.Length)
        {
            Array.Resize(ref _literalStack, _literalStack.Length * 2);
        }

        _literalStack[_literalStackSize++] = literal;
    }

    private Heredoc NewHeredoc() => _freeHeredocs.Count > 0 ? _freeHeredocs.Pop() : new Heredoc();

    private void FreeHeredoc(Heredoc heredoc)
    {
        heredoc.Word.Clear();
        heredoc.EndWordIndentationAllowed = false;
        heredoc.AllowsInterpolation = false;
        heredoc.Started = false;
        _freeHeredocs.Push(heredoc);
    }

    private bool ScanWhitespace(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        var heredocBodyStartIsValid = _openHeredocs.Count > 0 && !_openHeredocs[0].Started &&
                                      validSymbols[HeredocBodyStart];
        var crossedNewline = false;

        for (; ; )
        {
            if (!validSymbols[NoLineBreak] && validSymbols[LineBreak] && lexer.IsAtIncludedRangeStart)
            {
                lexer.MarkEnd();
                lexer.ResultSymbol = LineBreak;
                return true;
            }

            switch (lexer.Lookahead)
            {
                case ' ':
                case '\t':
                    Skip(lexer);
                    break;
                case '\r':
                    if (heredocBodyStartIsValid)
                    {
                        lexer.ResultSymbol = HeredocBodyStart;
                        _openHeredocs[0].Started = true;
                        return true;
                    }
                    else
                    {
                        Skip(lexer);
                        break;
                    }

                case '\n':
                    if (heredocBodyStartIsValid)
                    {
                        lexer.ResultSymbol = HeredocBodyStart;
                        _openHeredocs[0].Started = true;
                        return true;
                    }
                    else if (!validSymbols[NoLineBreak] && validSymbols[LineBreak] && !crossedNewline)
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        crossedNewline = true;
                    }
                    else
                    {
                        Skip(lexer);
                    }

                    break;
                case '\\':
                    Advance(lexer);
                    if (lexer.Lookahead == '\r')
                    {
                        Skip(lexer);
                    }

                    if (WideChar.IsSpace(lexer.Lookahead))
                    {
                        Skip(lexer);
                    }
                    else
                    {
                        return false;
                    }

                    break;
                default:
                    if (crossedNewline)
                    {
                        if (lexer.Lookahead != '.' && lexer.Lookahead != '&' && lexer.Lookahead != '#')
                        {
                            lexer.ResultSymbol = LineBreak;
                        }
                        else if (lexer.Lookahead == '.')
                        {
                            // Don't return LINE_BREAK for the call operator (`.`) but do return one for range
                            // operators
                            // (`..` and `...`)
                            Advance(lexer);
                            if (!lexer.IsAtEnd && lexer.Lookahead == '.')
                            {
                                lexer.ResultSymbol = LineBreak;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }

                    return true;
            }
        }
    }

    private static bool ScanOperator(Lexer lexer)
    {
        switch (lexer.Lookahead)
        {
            // <, <=, <<, <=>
            case '<':
                Advance(lexer);
                if (lexer.Lookahead == '<')
                {
                    Advance(lexer);
                }
                else if (lexer.Lookahead == '=')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '>')
                    {
                        Advance(lexer);
                    }
                }

                return true;

            // >, >=, >>
            case '>':
                Advance(lexer);
                if (lexer.Lookahead is '>' or '=')
                {
                    Advance(lexer);
                }

                return true;

            // ==, ===, =~
            case '=':
                Advance(lexer);
                if (lexer.Lookahead == '~')
                {
                    Advance(lexer);
                    return true;
                }

                if (lexer.Lookahead == '=')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '=')
                    {
                        Advance(lexer);
                    }

                    return true;
                }

                return false;

            // +, -, ~, +@, -@, ~@
            case '+':
            case '-':
            case '~':
                Advance(lexer);
                if (lexer.Lookahead == '@')
                {
                    Advance(lexer);
                }

                return true;

            // ..
            case '.':
                Advance(lexer);
                if (lexer.Lookahead == '.')
                {
                    Advance(lexer);
                    return true;
                }

                return false;

            // &, ^, |, /, %`
            case '&':
            case '^':
            case '|':
            case '/':
            case '%':
            case '`':
                Advance(lexer);
                return true;

            // !, !=, !~
            case '!':
                Advance(lexer);
                if (lexer.Lookahead is '=' or '~')
                {
                    Advance(lexer);
                }

                return true;

            // *, **
            case '*':
                Advance(lexer);
                if (lexer.Lookahead == '*')
                {
                    Advance(lexer);
                }

                return true;

            // [], []=
            case '[':
                Advance(lexer);
                if (lexer.Lookahead == ']')
                {
                    Advance(lexer);
                }
                else
                {
                    return false;
                }

                if (lexer.Lookahead == '=')
                {
                    Advance(lexer);
                }

                return true;

            default:
                return false;
        }
    }

    private static bool ScanSymbolIdentifier(Lexer lexer)
    {
        if (lexer.Lookahead == '@')
        {
            Advance(lexer);
            if (lexer.Lookahead == '@')
            {
                Advance(lexer);
            }
        }
        else if (lexer.Lookahead == '$')
        {
            Advance(lexer);
        }

        if (IsIdenChar((byte)lexer.Lookahead))
        {
            Advance(lexer);
        }
        else if (!ScanOperator(lexer))
        {
            return false;
        }

        while (IsIdenChar((byte)lexer.Lookahead))
        {
            Advance(lexer);
        }

        if (lexer.Lookahead is '?' or '!')
        {
            Advance(lexer);
        }

        if (lexer.Lookahead == '=')
        {
            lexer.MarkEnd();
            Advance(lexer);
            if (lexer.Lookahead != '>')
            {
                lexer.MarkEnd();
            }
        }

        return true;
    }

    private bool ScanOpenDelimiter(Lexer lexer, ref Literal literal, ReadOnlySpan<bool> validSymbols)
    {
        switch (lexer.Lookahead)
        {
            case '"':
                literal.Type = StringStart;
                literal.OpenDelimiter = literal.CloseDelimiter = lexer.Lookahead;
                literal.AllowsInterpolation = true;
                Advance(lexer);
                return true;

            case '\'':
                literal.Type = StringStart;
                literal.OpenDelimiter = literal.CloseDelimiter = lexer.Lookahead;
                literal.AllowsInterpolation = false;
                Advance(lexer);
                return true;

            case '`':
                if (!validSymbols[SubshellStart])
                {
                    return false;
                }

                literal.Type = SubshellStart;
                literal.OpenDelimiter = literal.CloseDelimiter = lexer.Lookahead;
                literal.AllowsInterpolation = true;
                Advance(lexer);
                return true;

            case '/':
                if (!validSymbols[RegexStart])
                {
                    return false;
                }

                literal.Type = RegexStart;
                literal.OpenDelimiter = literal.CloseDelimiter = lexer.Lookahead;
                literal.AllowsInterpolation = true;
                Advance(lexer);
                if (validSymbols[ForwardSlash])
                {
                    if (!_hasLeadingWhitespace)
                    {
                        return false;
                    }

                    if (lexer.Lookahead is ' ' or '\t' or '\n' or '\r')
                    {
                        return false;
                    }

                    if (lexer.Lookahead == '=')
                    {
                        return false;
                    }
                }

                return true;

            case '%':
                Advance(lexer);

                switch (lexer.Lookahead)
                {
                    case 's':
                        if (!validSymbols[SimpleSymbol])
                        {
                            return false;
                        }

                        literal.Type = SymbolStart;
                        literal.AllowsInterpolation = false;
                        Advance(lexer);
                        break;

                    case 'r':
                        if (!validSymbols[RegexStart])
                        {
                            return false;
                        }

                        literal.Type = RegexStart;
                        literal.AllowsInterpolation = true;
                        Advance(lexer);
                        break;

                    case 'x':
                        if (!validSymbols[SubshellStart])
                        {
                            return false;
                        }

                        literal.Type = SubshellStart;
                        literal.AllowsInterpolation = true;
                        Advance(lexer);
                        break;

                    case 'q':
                        if (!validSymbols[StringStart])
                        {
                            return false;
                        }

                        literal.Type = StringStart;
                        literal.AllowsInterpolation = false;
                        Advance(lexer);
                        break;

                    case 'Q':
                        if (!validSymbols[StringStart])
                        {
                            return false;
                        }

                        literal.Type = StringStart;
                        literal.AllowsInterpolation = true;
                        Advance(lexer);
                        break;

                    case 'w':
                        if (!validSymbols[StringArrayStart])
                        {
                            return false;
                        }

                        literal.Type = StringArrayStart;
                        literal.AllowsInterpolation = false;
                        Advance(lexer);
                        break;

                    case 'i':
                        if (!validSymbols[SymbolArrayStart])
                        {
                            return false;
                        }

                        literal.Type = SymbolArrayStart;
                        literal.AllowsInterpolation = false;
                        Advance(lexer);
                        break;

                    case 'W':
                        if (!validSymbols[StringArrayStart])
                        {
                            return false;
                        }

                        literal.Type = StringArrayStart;
                        literal.AllowsInterpolation = true;
                        Advance(lexer);
                        break;

                    case 'I':
                        if (!validSymbols[SymbolArrayStart])
                        {
                            return false;
                        }

                        literal.Type = SymbolArrayStart;
                        literal.AllowsInterpolation = true;
                        Advance(lexer);
                        break;

                    default:
                        if (!validSymbols[StringStart])
                        {
                            return false;
                        }

                        literal.Type = StringStart;
                        literal.AllowsInterpolation = true;
                        break;
                }

                switch (lexer.Lookahead)
                {
                    case '(':
                        literal.OpenDelimiter = '(';
                        literal.CloseDelimiter = ')';
                        break;

                    case '[':
                        literal.OpenDelimiter = '[';
                        literal.CloseDelimiter = ']';
                        break;

                    case '{':
                        literal.OpenDelimiter = '{';
                        literal.CloseDelimiter = '}';
                        break;

                    case '<':
                        literal.OpenDelimiter = '<';
                        literal.CloseDelimiter = '>';
                        break;

                    case '\r':
                    case '\n':
                    case ' ':
                    case '\t':
                        // If the `/` operator is valid, then so is the `%` operator, which means
                        // that a `%` followed by whitespace should be considered an operator,
                        // not a percent string.
                        if (validSymbols[ForwardSlash])
                        {
                            return false;
                        }

                        break;

                    case '|':
                    case '!':
                    case '#':
                    case '/':
                    case '\\':
                    case '@':
                    case '$':
                    case '%':
                    case '^':
                    case '&':
                    case '*':
                    case ')':
                    case ']':
                    case '}':
                    case '>':
                    // TODO: Implement %= as external rule and re-enable = as a valid
                    // unbalanced delimiter. That will be necessary due to ambiguity
                    // between &= assignment operator and %=...= as string
                    // content delimiter.
                    // case '=':
                    case '+':
                    case '-':
                    case '~':
                    case '`':
                    case ',':
                    case '.':
                    case '?':
                    case ':':
                    case ';':
                    case '_':
                    case '"':
                    case '\'':
                        literal.OpenDelimiter = lexer.Lookahead;
                        literal.CloseDelimiter = lexer.Lookahead;
                        break;
                    default:
                        return false;
                }

                Advance(lexer);
                return true;

            default:
                return false;
        }
    }

    private static void ScanHeredocWord(Lexer lexer, Heredoc heredoc)
    {
        // String word: an Array(char), each code point is truncated to its low byte
        var word = heredoc.Word;
        var quote = 0;

        switch (lexer.Lookahead)
        {
            case '\'':
            case '"':
            case '`':
                quote = lexer.Lookahead;
                Advance(lexer);
                while (lexer.Lookahead != quote && !lexer.IsAtEnd)
                {
                    word.Add((byte)lexer.Lookahead);
                    Advance(lexer);
                }

                Advance(lexer);
                break;

            default:
                if (WideChar.IsAlnum(lexer.Lookahead) || lexer.Lookahead == '_')
                {
                    word.Add((byte)lexer.Lookahead);
                    Advance(lexer);
                    while (WideChar.IsAlnum(lexer.Lookahead) || lexer.Lookahead == '_')
                    {
                        word.Add((byte)lexer.Lookahead);
                        Advance(lexer);
                    }
                }

                break;
        }

        heredoc.AllowsInterpolation = quote != '\'';
    }

    // The characters of strchr("!@&`'+~=/\\,;.<>*$?:\"", lookahead), which converts the lookahead to a char and also
    // finds the terminating '\0'
    private static bool IsShortInterpolationVariableChar(byte c) => c switch
    {
        (byte)'!' or (byte)'@' or (byte)'&' or (byte)'`' or (byte)'\'' or (byte)'+' or (byte)'~' or (byte)'=' or
        (byte)'/' or (byte)'\\' or (byte)',' or (byte)';' or (byte)'.' or (byte)'<' or (byte)'>' or (byte)'*' or
        (byte)'$' or (byte)'?' or (byte)':' or (byte)'"' or 0 => true,
        _ => false,
    };

    private static bool ScanShortInterpolation(Lexer lexer, bool hasContent, int contentSymbol)
    {
        // char start = (char)lexer->lookahead: the low byte, signed
        var start = (sbyte)lexer.Lookahead;
        if (start is (sbyte)'@' or (sbyte)'$')
        {
            if (hasContent)
            {
                lexer.ResultSymbol = (ushort)contentSymbol;
                return true;
            }

            lexer.MarkEnd();
            Advance(lexer);
            var isShortInterpolation = false;
            if (start == '$')
            {
                if (IsShortInterpolationVariableChar((byte)lexer.Lookahead))
                {
                    isShortInterpolation = true;
                }
                else
                {
                    if (lexer.Lookahead == '-')
                    {
                        Advance(lexer);
                        isShortInterpolation = WideChar.IsAlpha(lexer.Lookahead) || lexer.Lookahead == '_';
                    }
                    else
                    {
                        isShortInterpolation = WideChar.IsAlnum(lexer.Lookahead) || lexer.Lookahead == '_';
                    }
                }
            }

            if (start == '@')
            {
                if (lexer.Lookahead == '@')
                {
                    Advance(lexer);
                }

                isShortInterpolation = IsIdenChar((byte)lexer.Lookahead) && !WideChar.IsDigit(lexer.Lookahead);
            }

            if (isShortInterpolation)
            {
                lexer.ResultSymbol = ShortInterpolation;
                return true;
            }
        }

        return false;
    }

    private bool ScanHeredocContent(Lexer lexer)
    {
        var heredoc = _openHeredocs[0];
        var positionInWord = 0;
        var lookForHeredocEnd = true;
        var hasContent = false;

        for (; ; )
        {
            if (positionInWord == heredoc.Word.Count)
            {
                if (!hasContent)
                {
                    lexer.MarkEnd();
                }

                while (lexer.Lookahead is ' ' or '\t')
                {
                    Advance(lexer);
                }

                if (lexer.Lookahead is '\n' or '\r')
                {
                    if (hasContent)
                    {
                        lexer.ResultSymbol = HeredocContent;
                    }
                    else
                    {
                        _openHeredocs.RemoveAt(0);
                        FreeHeredoc(heredoc);
                        lexer.ResultSymbol = HeredocBodyEnd;
                    }

                    return true;
                }

                hasContent = true;
                positionInWord = 0;
            }

            if (lexer.IsAtEnd)
            {
                lexer.MarkEnd();
                if (hasContent)
                {
                    lexer.ResultSymbol = HeredocContent;
                }
                else
                {
                    _openHeredocs.RemoveAt(0);
                    FreeHeredoc(heredoc);
                    lexer.ResultSymbol = HeredocBodyEnd;
                }

                return true;
            }

            // The characters of the word are signed chars
            if (lexer.Lookahead == (sbyte)heredoc.Word[positionInWord] && lookForHeredocEnd)
            {
                Advance(lexer);
                positionInWord++;
            }
            else
            {
                positionInWord = 0;
                lookForHeredocEnd = false;

                if (heredoc.AllowsInterpolation && lexer.Lookahead == '\\')
                {
                    if (hasContent)
                    {
                        lexer.ResultSymbol = HeredocContent;
                        return true;
                    }

                    return false;
                }

                if (heredoc.AllowsInterpolation && lexer.Lookahead == '#')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '{')
                    {
                        if (hasContent)
                        {
                            lexer.ResultSymbol = HeredocContent;
                            return true;
                        }

                        return false;
                    }

                    if (ScanShortInterpolation(lexer, hasContent, HeredocContent))
                    {
                        return true;
                    }
                }
                else if (lexer.Lookahead is '\r' or '\n')
                {
                    if (lexer.Lookahead == '\r')
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == '\n')
                        {
                            Advance(lexer);
                        }
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    hasContent = true;
                    lookForHeredocEnd = true;
                    while (lexer.Lookahead is ' ' or '\t')
                    {
                        Advance(lexer);
                        if (!heredoc.EndWordIndentationAllowed)
                        {
                            lookForHeredocEnd = false;
                        }
                    }

                    lexer.MarkEnd();
                }
                else
                {
                    hasContent = true;
                    Advance(lexer);
                    lexer.MarkEnd();
                }
            }
        }
    }

    private bool ScanLiteralContent(Lexer lexer)
    {
        ref var literal = ref _literalStack[_literalStackSize - 1];
        var hasContent = false;
        var stopOnSpace = literal.Type is SymbolArrayStart or StringArrayStart;

        for (; ; )
        {
            if (stopOnSpace && WideChar.IsSpace(lexer.Lookahead))
            {
                if (hasContent)
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = StringContent;
                    return true;
                }

                return false;
            }

            if (lexer.Lookahead == literal.CloseDelimiter)
            {
                lexer.MarkEnd();
                if (literal.NestingDepth == 1)
                {
                    if (hasContent)
                    {
                        lexer.ResultSymbol = StringContent;
                    }
                    else
                    {
                        Advance(lexer);
                        if (literal.Type == RegexStart)
                        {
                            while (WideChar.IsLower(lexer.Lookahead))
                            {
                                Advance(lexer);
                            }
                        }

                        _literalStackSize--;
                        lexer.ResultSymbol = StringEnd;
                        lexer.MarkEnd();
                    }

                    return true;
                }

                literal.NestingDepth--;
                Advance(lexer);
            }
            else if (lexer.Lookahead == literal.OpenDelimiter)
            {
                literal.NestingDepth++;
                Advance(lexer);
            }
            else if (literal.AllowsInterpolation && lexer.Lookahead == '#')
            {
                lexer.MarkEnd();
                Advance(lexer);
                if (lexer.Lookahead == '{')
                {
                    if (hasContent)
                    {
                        lexer.ResultSymbol = StringContent;
                        return true;
                    }

                    return false;
                }

                if (ScanShortInterpolation(lexer, hasContent, StringContent))
                {
                    return true;
                }
            }
            else if (lexer.Lookahead == '\\')
            {
                if (literal.AllowsInterpolation)
                {
                    if (hasContent)
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = StringContent;
                        return true;
                    }

                    return false;
                }

                Advance(lexer);
                Advance(lexer);
            }
            else if (lexer.IsAtEnd)
            {
                Advance(lexer);
                lexer.MarkEnd();
                return false;
            }
            else
            {
                Advance(lexer);
            }

            hasContent = true;
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        _hasLeadingWhitespace = false;

        // Contents of literals, which match any character except for some close delimiter
        if (!validSymbols[StringStart])
        {
            if ((validSymbols[StringContent] || validSymbols[StringEnd]) && _literalStackSize > 0)
            {
                return ScanLiteralContent(lexer);
            }

            if ((validSymbols[HeredocContent] || validSymbols[HeredocBodyEnd]) && _openHeredocs.Count > 0)
            {
                return ScanHeredocContent(lexer);
            }
        }

        // Whitespace
        lexer.ResultSymbol = None;
        if (!ScanWhitespace(lexer, validSymbols))
        {
            return false;
        }

        if (lexer.ResultSymbol != None)
        {
            return true;
        }

        switch (lexer.Lookahead)
        {
            case '&':
                if (validSymbols[BlockAmpersand])
                {
                    Advance(lexer);
                    if (lexer.Lookahead != '&' && lexer.Lookahead != '.' && lexer.Lookahead != '=' &&
                        !WideChar.IsSpace(lexer.Lookahead))
                    {
                        lexer.ResultSymbol = BlockAmpersand;
                        return true;
                    }

                    return false;
                }

                break;

            case '<':
                if (validSymbols[SingletonClassLeftAngleLeftAngle])
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '<')
                    {
                        Advance(lexer);
                        lexer.ResultSymbol = SingletonClassLeftAngleLeftAngle;
                        return true;
                    }

                    return false;
                }

                break;

            case '*':
                if (validSymbols[SplatStar] || validSymbols[BinaryStar] || validSymbols[HashSplatStarStar] ||
                    validSymbols[BinaryStarStar])
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '=')
                    {
                        return false;
                    }

                    if (lexer.Lookahead == '*')
                    {
                        if (validSymbols[HashSplatStarStar] || validSymbols[BinaryStarStar])
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == '=')
                            {
                                return false;
                            }

                            if (validSymbols[BinaryStarStar] && !_hasLeadingWhitespace)
                            {
                                lexer.ResultSymbol = BinaryStarStar;
                                return true;
                            }

                            if (validSymbols[HashSplatStarStar] && !WideChar.IsSpace(lexer.Lookahead))
                            {
                                lexer.ResultSymbol = HashSplatStarStar;
                                return true;
                            }

                            if (validSymbols[BinaryStarStar])
                            {
                                lexer.ResultSymbol = BinaryStarStar;
                                return true;
                            }

                            if (validSymbols[HashSplatStarStar])
                            {
                                lexer.ResultSymbol = HashSplatStarStar;
                                return true;
                            }

                            return false;
                        }

                        return false;
                    }

                    if (validSymbols[BinaryStar] && !_hasLeadingWhitespace)
                    {
                        lexer.ResultSymbol = BinaryStar;
                        return true;
                    }

                    if (validSymbols[SplatStar] && !WideChar.IsSpace(lexer.Lookahead))
                    {
                        lexer.ResultSymbol = SplatStar;
                        return true;
                    }

                    if (validSymbols[BinaryStar])
                    {
                        lexer.ResultSymbol = BinaryStar;
                        return true;
                    }

                    if (validSymbols[SplatStar])
                    {
                        lexer.ResultSymbol = SplatStar;
                        return true;
                    }

                    return false;
                }

                break;

            case '-':
                if (validSymbols[UnaryMinus] || validSymbols[UnaryMinusNum] || validSymbols[BinaryMinus])
                {
                    Advance(lexer);
                    if (lexer.Lookahead != '=' && lexer.Lookahead != '>')
                    {
                        if (validSymbols[UnaryMinusNum] &&
                            (!validSymbols[BinaryStar] || _hasLeadingWhitespace) &&
                            WideChar.IsDigit(lexer.Lookahead))
                        {
                            lexer.ResultSymbol = UnaryMinusNum;
                            return true;
                        }

                        if (validSymbols[UnaryMinus] && _hasLeadingWhitespace && !WideChar.IsSpace(lexer.Lookahead))
                        {
                            lexer.ResultSymbol = UnaryMinus;
                        }
                        else if (validSymbols[BinaryMinus])
                        {
                            lexer.ResultSymbol = BinaryMinus;
                        }
                        else
                        {
                            lexer.ResultSymbol = UnaryMinus;
                        }

                        return true;
                    }

                    return false;
                }

                break;

            case ':':
                if (validSymbols[SymbolStart])
                {
                    var literal = default(Literal);
                    literal.Type = SymbolStart;
                    literal.NestingDepth = 1;
                    Advance(lexer);

                    switch (lexer.Lookahead)
                    {
                        case '"':
                            Advance(lexer);
                            literal.OpenDelimiter = '"';
                            literal.CloseDelimiter = '"';
                            literal.AllowsInterpolation = true;
                            PushLiteral(literal);
                            lexer.ResultSymbol = SymbolStart;
                            return true;

                        case '\'':
                            Advance(lexer);
                            literal.OpenDelimiter = '\'';
                            literal.CloseDelimiter = '\'';
                            literal.AllowsInterpolation = false;
                            PushLiteral(literal);
                            lexer.ResultSymbol = SymbolStart;
                            return true;

                        default:
                            if (ScanSymbolIdentifier(lexer))
                            {
                                lexer.ResultSymbol = SimpleSymbol;
                                return true;
                            }

                            break;
                    }

                    return false;
                }

                break;

            case '[':
                // Treat a square bracket as an element reference if either:
                // * the bracket is not preceded by any whitespace
                // * an arbitrary expression is not valid at the current position.
                if (validSymbols[ElementReferenceBracket] &&
                    (!_hasLeadingWhitespace || !validSymbols[StringStart]))
                {
                    Advance(lexer);
                    lexer.ResultSymbol = ElementReferenceBracket;
                    return true;
                }

                break;

            default:
                break;
        }

        // Open delimiters for literals
        if (((validSymbols[HashKeySymbol] || validSymbols[IdentifierSuffix]) &&
             (WideChar.IsAlpha(lexer.Lookahead) || lexer.Lookahead == '_')) ||
            (validSymbols[ConstantSuffix] && WideChar.IsUpper(lexer.Lookahead)))
        {
            var validIdentifierSymbol = WideChar.IsUpper(lexer.Lookahead) ? ConstantSuffix : IdentifierSuffix;
            while (WideChar.IsAlnum(lexer.Lookahead) || lexer.Lookahead == '_')
            {
                Advance(lexer);
            }

            if (validSymbols[HashKeySymbol] && lexer.Lookahead == ':')
            {
                lexer.MarkEnd();
                Advance(lexer);
                if (lexer.Lookahead != ':')
                {
                    lexer.ResultSymbol = HashKeySymbol;
                    return true;
                }
            }
            else if (validSymbols[validIdentifierSymbol] && lexer.Lookahead == '!')
            {
                Advance(lexer);
                if (lexer.Lookahead != '=')
                {
                    lexer.ResultSymbol = (ushort)validIdentifierSymbol;
                    return true;
                }
            }

            return false;
        }

        // Open delimiters for literals
        if (validSymbols[StringStart])
        {
            var literal = default(Literal);
            literal.NestingDepth = 1;

            if (lexer.Lookahead == '<')
            {
                Advance(lexer);
                if (lexer.Lookahead != '<')
                {
                    return false;
                }

                Advance(lexer);

                var heredoc = NewHeredoc();
                if (lexer.Lookahead is '-' or '~')
                {
                    Advance(lexer);
                    heredoc.EndWordIndentationAllowed = true;
                }

                ScanHeredocWord(lexer, heredoc);
                if (heredoc.Word.Count == 0)
                {
                    FreeHeredoc(heredoc);
                    return false;
                }

                _openHeredocs.Add(heredoc);
                lexer.ResultSymbol = HeredocStart;
                return true;
            }

            if (ScanOpenDelimiter(lexer, ref literal, validSymbols))
            {
                PushLiteral(literal);
                lexer.ResultSymbol = (ushort)literal.Type;
                return true;
            }

            return false;
        }

        return false;
    }

    private struct Literal
    {
        public int Type;
        public int OpenDelimiter;
        public int CloseDelimiter;
        public int NestingDepth;
        public bool AllowsInterpolation;
    }

    private sealed class Heredoc
    {
        // String word: the characters of the word, truncated to char
        public readonly List<byte> Word = [];
        public bool EndWordIndentationAllowed;
        public bool AllowsInterpolation;
        public bool Started;
    }
}
