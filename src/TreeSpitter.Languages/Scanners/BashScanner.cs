using System.Buffers.Binary;

namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-bash: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld). Its state is the
/// stack of the heredocs, with their delimiters, and the depth of the parentheses of the last extglob pattern; it scans
/// the heredocs, the concatenations, the variable names, the test operators, the regular expressions, the extglob
/// patterns and the words of the expansions.
/// </summary>
public sealed class BashScanner : IExternalScanner
{
    // enum TokenType
    private const int HeredocStart = 0;
    private const int SimpleHeredocBody = 1;
    private const int HeredocBodyBeginning = 2;
    private const int HeredocContent = 3;
    private const int HeredocEnd = 4;
    private const int FileDescriptor = 5;
    private const int EmptyValue = 6;
    private const int Concat = 7;
    private const int VariableName = 8;
    private const int TestOperator = 9;
    private const int Regex = 10;
    private const int RegexNoSlash = 11;
    private const int RegexNoSpace = 12;
    private const int ExpansionWord = 13;
    private const int ExtglobPattern = 14;
    private const int BareDollar = 15;
    private const int BraceStart = 16;
    private const int ImmediateDoubleHash = 17;
    private const int ExternalExpansionSymHash = 18;
    private const int ExternalExpansionSymBang = 19;
    private const int ExternalExpansionSymEqual = 20;
    private const int ClosingBrace = 21;
    private const int ClosingBracket = 22;
    private const int HeredocArrow = 23;
    private const int HeredocArrowDash = 24;
    private const int Newline = 25;
    private const int OpeningParen = 26;
    private const int Esac = 27;
    private const int ErrorRecovery = 28;

    private byte _lastGlobParenDepth;
    private bool _extWasInDoubleQuote;
    private bool _extSawOutsideQuote;

    // Array(Heredoc) heredocs. Like in the C scanner, deserialize doesn't remove the heredocs after the ones of the
    // state (an empty state only resets them), and doesn't reset the other fields for an empty state.
    private readonly List<Heredoc> _heredocs = [];

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    private static bool InErrorRecovery(ReadOnlySpan<bool> validSymbols) => validSymbols[ErrorRecovery];

    private static void ResetString(List<byte> @string) => @string.Clear();

    private static void ResetHeredoc(Heredoc heredoc)
    {
        heredoc.IsRaw = false;
        heredoc.Started = false;
        heredoc.AllowsIndent = false;
        ResetString(heredoc.Delimiter);
    }

    private void Reset()
    {
        for (var i = 0; i < _heredocs.Count; i++)
        {
            ResetHeredoc(_heredocs[i]);
        }
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        var size = 0;

        buffer[size++] = _lastGlobParenDepth;
        buffer[size++] = (byte)(_extWasInDoubleQuote ? 1 : 0);
        buffer[size++] = (byte)(_extSawOutsideQuote ? 1 : 0);
        buffer[size++] = (byte)_heredocs.Count;

        for (var i = 0; i < _heredocs.Count; i++)
        {
            var heredoc = _heredocs[i];
            if (size + 3 + sizeof(uint) + heredoc.Delimiter.Count >= ExternalScanner.StateCapacity)
            {
                return 0;
            }

            buffer[size++] = (byte)(heredoc.IsRaw ? 1 : 0);
            buffer[size++] = (byte)(heredoc.Started ? 1 : 0);
            buffer[size++] = (byte)(heredoc.AllowsIndent ? 1 : 0);

            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(size), (uint)heredoc.Delimiter.Count);
            size += sizeof(uint);
            if (heredoc.Delimiter.Count > 0)
            {
                for (var j = 0; j < heredoc.Delimiter.Count; j++)
                {
                    buffer[size + j] = heredoc.Delimiter[j];
                }

                size += heredoc.Delimiter.Count;
            }
        }

        return size;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        if (state.Length == 0)
        {
            Reset();
        }
        else
        {
            var size = 0;
            _lastGlobParenDepth = state[size++];
            _extWasInDoubleQuote = state[size++] != 0;
            _extSawOutsideQuote = state[size++] != 0;
            int heredocCount = state[size++];
            for (var i = 0; i < heredocCount; i++)
            {
                Heredoc heredoc;
                if (i < _heredocs.Count)
                {
                    heredoc = _heredocs[i];
                }
                else
                {
                    heredoc = new Heredoc();
                    _heredocs.Add(heredoc);
                }

                heredoc.IsRaw = state[size++] != 0;
                heredoc.Started = state[size++] != 0;
                heredoc.AllowsIndent = state[size++] != 0;

                var delimiterSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(size));
                size += sizeof(uint);
                heredoc.Delimiter.Clear();

                if (delimiterSize > 0)
                {
                    for (var j = 0; j < delimiterSize; j++)
                    {
                        heredoc.Delimiter.Add(state[size + j]);
                    }

                    size += delimiterSize;
                }
            }
        }
    }

    // Consume a "word" in POSIX parlance, and returns it unquoted.
    //
    // This is an approximate implementation that doesn't deal with any
    // POSIX-mandated substitution, and assumes the default value for
    // IFS.
    private static bool AdvanceWord(Lexer lexer, List<byte> unquotedWord)
    {
        var empty = true;

        var quote = 0;
        if (lexer.Lookahead is '\'' or '"')
        {
            quote = lexer.Lookahead;
            Advance(lexer);
        }

        while (lexer.Lookahead != 0 &&
               !(quote != 0 ? lexer.Lookahead == quote || lexer.Lookahead == '\r' || lexer.Lookahead == '\n'
                   : WideChar.IsSpace(lexer.Lookahead)))
        {
            if (lexer.Lookahead == '\\')
            {
                Advance(lexer);
                if (lexer.Lookahead == 0)
                {
                    return false;
                }
            }

            empty = false;
            // String is an Array(char): the code point is truncated to its low byte
            unquotedWord.Add((byte)lexer.Lookahead);
            Advance(lexer);
        }

        unquotedWord.Add(0);

        if (quote != 0 && lexer.Lookahead == quote)
        {
            Advance(lexer);
        }

        return !empty;
    }

    private static bool ScanBareDollar(Lexer lexer)
    {
        while (WideChar.IsSpace(lexer.Lookahead) && lexer.Lookahead != '\n' && !lexer.IsAtEnd)
        {
            Skip(lexer);
        }

        if (lexer.Lookahead == '$')
        {
            Advance(lexer);
            lexer.ResultSymbol = BareDollar;
            lexer.MarkEnd();
            return WideChar.IsSpace(lexer.Lookahead) || lexer.IsAtEnd || lexer.Lookahead == '\"';
        }

        return false;
    }

    private static bool ScanHeredocStart(Heredoc heredoc, Lexer lexer)
    {
        while (WideChar.IsSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        lexer.ResultSymbol = HeredocStart;
        heredoc.IsRaw = lexer.Lookahead is '\'' or '"' or '\\';

        var foundDelimiter = AdvanceWord(lexer, heredoc.Delimiter);
        if (!foundDelimiter)
        {
            ResetString(heredoc.Delimiter);
            return false;
        }

        return foundDelimiter;
    }

    private static bool ScanHeredocEndIdentifier(Heredoc heredoc, Lexer lexer)
    {
        ResetString(heredoc.CurrentLeadingWord);
        // Scan the first 'n' characters on this line, to see if they match the
        // heredoc delimiter
        var size = 0;
        if (heredoc.Delimiter.Count > 0)
        {
            // The characters of the delimiter are signed chars. The delimiter ends with a '\0', which stops the loop
            // before its end (C asserts that the index is in the array)
            while (lexer.Lookahead != '\0' && lexer.Lookahead != '\n' && size < heredoc.Delimiter.Count &&
                   (sbyte)heredoc.Delimiter[size] == lexer.Lookahead &&
                   heredoc.CurrentLeadingWord.Count < heredoc.Delimiter.Count)
            {
                heredoc.CurrentLeadingWord.Add((byte)lexer.Lookahead);
                Advance(lexer);
                size++;
            }
        }

        heredoc.CurrentLeadingWord.Add(0);
        return heredoc.Delimiter.Count != 0 && StrCmp(heredoc.CurrentLeadingWord, heredoc.Delimiter) == 0;
    }

    // strcmp of the contents of two Strings, which end with a '\0'
    private static int StrCmp(List<byte> left, List<byte> right)
    {
        for (var i = 0; ; i++)
        {
            var l = i < left.Count ? left[i] : 0;
            var r = i < right.Count ? right[i] : 0;
            if (l != r)
            {
                return l - r;
            }

            if (l == 0)
            {
                return 0;
            }
        }
    }

    private bool ScanHeredocContent(Lexer lexer, int middleType, int endType)
    {
        var didAdvance = false;
        var heredoc = _heredocs[_heredocs.Count - 1];

        for (; ; )
        {
            switch (lexer.Lookahead)
            {
                case '\0':
                    if (lexer.IsAtEnd && didAdvance)
                    {
                        ResetHeredoc(heredoc);
                        lexer.ResultSymbol = (ushort)endType;
                        return true;
                    }

                    return false;

                case '\\':
                    didAdvance = true;
                    Advance(lexer);
                    Advance(lexer);
                    break;

                case '$':
                    if (heredoc.IsRaw)
                    {
                        didAdvance = true;
                        Advance(lexer);
                        break;
                    }

                    if (didAdvance)
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = (ushort)middleType;
                        heredoc.Started = true;
                        Advance(lexer);
                        if (WideChar.IsAlpha(lexer.Lookahead) || lexer.Lookahead == '{' || lexer.Lookahead == '(')
                        {
                            return true;
                        }

                        break;
                    }

                    if (middleType == HeredocBodyBeginning && lexer.GetColumn() == 0)
                    {
                        lexer.ResultSymbol = (ushort)middleType;
                        heredoc.Started = true;
                        return true;
                    }

                    return false;

                case '\n':
                    if (!didAdvance)
                    {
                        Skip(lexer);
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    didAdvance = true;
                    if (heredoc.AllowsIndent)
                    {
                        while (WideChar.IsSpace(lexer.Lookahead))
                        {
                            Advance(lexer);
                        }
                    }

                    lexer.ResultSymbol = (ushort)(heredoc.Started ? middleType : endType);
                    lexer.MarkEnd();
                    if (ScanHeredocEndIdentifier(heredoc, lexer))
                    {
                        if (lexer.ResultSymbol == HeredocEnd)
                        {
                            _heredocs.RemoveAt(_heredocs.Count - 1);
                        }

                        return true;
                    }

                    break;

                default:
                    if (lexer.GetColumn() == 0)
                    {
                        // an alternative is to check the starting column of the
                        // heredoc body and track that statefully
                        while (WideChar.IsSpace(lexer.Lookahead))
                        {
                            if (didAdvance)
                            {
                                Advance(lexer);
                            }
                            else
                            {
                                Skip(lexer);
                            }
                        }

                        if (endType != SimpleHeredocBody)
                        {
                            lexer.ResultSymbol = (ushort)middleType;
                            if (ScanHeredocEndIdentifier(heredoc, lexer))
                            {
                                return true;
                            }
                        }

                        if (endType == SimpleHeredocBody)
                        {
                            lexer.ResultSymbol = (ushort)endType;
                            lexer.MarkEnd();
                            if (ScanHeredocEndIdentifier(heredoc, lexer))
                            {
                                return true;
                            }
                        }
                    }

                    didAdvance = true;
                    Advance(lexer);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[Concat] && !InErrorRecovery(validSymbols))
        {
            if (!(lexer.Lookahead == 0 || WideChar.IsSpace(lexer.Lookahead) || lexer.Lookahead == '>' ||
                  lexer.Lookahead == '<' || lexer.Lookahead == ')' || lexer.Lookahead == '(' ||
                  lexer.Lookahead == ';' || lexer.Lookahead == '&' || lexer.Lookahead == '|' ||
                  (lexer.Lookahead == '}' && validSymbols[ClosingBrace]) ||
                  (lexer.Lookahead == ']' && validSymbols[ClosingBracket])))
            {
                lexer.ResultSymbol = Concat;
                // So for a`b`, we want to return a concat. We check if the
                // 2nd backtick has whitespace after it, and if it does we
                // return concat.
                if (lexer.Lookahead == '`')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    while (lexer.Lookahead != '`' && !lexer.IsAtEnd)
                    {
                        Advance(lexer);
                    }

                    if (lexer.IsAtEnd)
                    {
                        return false;
                    }

                    if (lexer.Lookahead == '`')
                    {
                        Advance(lexer);
                    }

                    return WideChar.IsSpace(lexer.Lookahead) || lexer.IsAtEnd;
                }

                // strings w/ expansions that contains escaped quotes or
                // backslashes need this to return a concat
                if (lexer.Lookahead == '\\')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead is '"' or '\'' or '\\')
                    {
                        return true;
                    }

                    if (lexer.IsAtEnd)
                    {
                        return false;
                    }
                }
                else
                {
                    return true;
                }
            }

            if (WideChar.IsSpace(lexer.Lookahead) && validSymbols[ClosingBrace] && !validSymbols[ExpansionWord])
            {
                lexer.ResultSymbol = Concat;
                return true;
            }
        }

        if (validSymbols[ImmediateDoubleHash] && !InErrorRecovery(validSymbols))
        {
            // advance two # and ensure not } after
            if (lexer.Lookahead == '#')
            {
                lexer.MarkEnd();
                Advance(lexer);
                if (lexer.Lookahead == '#')
                {
                    Advance(lexer);
                    if (lexer.Lookahead != '}')
                    {
                        lexer.ResultSymbol = ImmediateDoubleHash;
                        lexer.MarkEnd();
                        return true;
                    }
                }
            }
        }

        if (validSymbols[ExternalExpansionSymHash] && !InErrorRecovery(validSymbols))
        {
            if (lexer.Lookahead is '#' or '=' or '!')
            {
                lexer.ResultSymbol = lexer.Lookahead == '#' ? (ushort)ExternalExpansionSymHash
                    : lexer.Lookahead == '!' ? (ushort)ExternalExpansionSymBang
                    : (ushort)ExternalExpansionSymEqual;
                Advance(lexer);
                lexer.MarkEnd();
                while (lexer.Lookahead is '#' or '=' or '!')
                {
                    Advance(lexer);
                }

                while (WideChar.IsSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }

                if (lexer.Lookahead == '}')
                {
                    return true;
                }

                return false;
            }
        }

        if (validSymbols[EmptyValue])
        {
            if (WideChar.IsSpace(lexer.Lookahead) || lexer.IsAtEnd || lexer.Lookahead == ';' || lexer.Lookahead == '&')
            {
                lexer.ResultSymbol = EmptyValue;
                return true;
            }
        }

        if ((validSymbols[HeredocBodyBeginning] || validSymbols[SimpleHeredocBody]) && _heredocs.Count > 0 &&
            !_heredocs[_heredocs.Count - 1].Started && !InErrorRecovery(validSymbols))
        {
            return ScanHeredocContent(lexer, HeredocBodyBeginning, SimpleHeredocBody);
        }

        if (validSymbols[HeredocEnd] && _heredocs.Count > 0)
        {
            var heredoc = _heredocs[_heredocs.Count - 1];
            if (ScanHeredocEndIdentifier(heredoc, lexer))
            {
                _heredocs.RemoveAt(_heredocs.Count - 1);
                lexer.ResultSymbol = HeredocEnd;
                return true;
            }
        }

        if (validSymbols[HeredocContent] && _heredocs.Count > 0 && _heredocs[_heredocs.Count - 1].Started &&
            !InErrorRecovery(validSymbols))
        {
            return ScanHeredocContent(lexer, HeredocContent, HeredocEnd);
        }

        if (validSymbols[HeredocStart] && !InErrorRecovery(validSymbols) && _heredocs.Count > 0)
        {
            return ScanHeredocStart(_heredocs[_heredocs.Count - 1], lexer);
        }

        if (validSymbols[TestOperator] && !validSymbols[ExpansionWord])
        {
            while (WideChar.IsSpace(lexer.Lookahead) && lexer.Lookahead != '\n')
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '\\')
            {
                if (validSymbols[ExtglobPattern])
                {
                    goto extglob_pattern;
                }

                if (validSymbols[RegexNoSpace])
                {
                    goto regex;
                }

                Skip(lexer);

                if (lexer.IsAtEnd)
                {
                    return false;
                }

                if (lexer.Lookahead == '\r')
                {
                    Skip(lexer);
                    if (lexer.Lookahead == '\n')
                    {
                        Skip(lexer);
                    }
                }
                else if (lexer.Lookahead == '\n')
                {
                    Skip(lexer);
                }
                else
                {
                    return false;
                }

                while (WideChar.IsSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }
            }

            if (lexer.Lookahead == '\n' && !validSymbols[Newline])
            {
                Skip(lexer);

                while (WideChar.IsSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }
            }

            if (lexer.Lookahead == '-')
            {
                Advance(lexer);

                var advancedOnce = false;
                while (WideChar.IsAlpha(lexer.Lookahead))
                {
                    advancedOnce = true;
                    Advance(lexer);
                }

                if (WideChar.IsSpace(lexer.Lookahead) && advancedOnce)
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '}' && validSymbols[ClosingBrace])
                    {
                        if (validSymbols[ExpansionWord])
                        {
                            lexer.MarkEnd();
                            lexer.ResultSymbol = ExpansionWord;
                            return true;
                        }

                        return false;
                    }

                    lexer.ResultSymbol = TestOperator;
                    return true;
                }

                if (WideChar.IsSpace(lexer.Lookahead) && validSymbols[ExtglobPattern])
                {
                    lexer.ResultSymbol = ExtglobPattern;
                    return true;
                }
            }

            if (validSymbols[BareDollar] && !InErrorRecovery(validSymbols) && ScanBareDollar(lexer))
            {
                return true;
            }
        }

        if ((validSymbols[VariableName] || validSymbols[FileDescriptor] || validSymbols[HeredocArrow]) &&
            !validSymbols[RegexNoSlash] && !InErrorRecovery(validSymbols))
        {
            for (; ; )
            {
                if ((lexer.Lookahead == ' ' || lexer.Lookahead == '\t' || lexer.Lookahead == '\r' ||
                     (lexer.Lookahead == '\n' && !validSymbols[Newline])) &&
                    !validSymbols[ExpansionWord])
                {
                    Skip(lexer);
                }
                else if (lexer.Lookahead == '\\')
                {
                    Skip(lexer);

                    if (lexer.IsAtEnd)
                    {
                        lexer.MarkEnd();
                        lexer.ResultSymbol = VariableName;
                        return true;
                    }

                    if (lexer.Lookahead == '\r')
                    {
                        Skip(lexer);
                    }

                    if (lexer.Lookahead == '\n')
                    {
                        Skip(lexer);
                    }
                    else
                    {
                        if (lexer.Lookahead == '\\' && validSymbols[ExpansionWord])
                        {
                            goto expansion_word;
                        }

                        return false;
                    }
                }
                else
                {
                    break;
                }
            }

            // no '*', '@', '?', '-', '$', '0', '_'
            if (!validSymbols[ExpansionWord] &&
                (lexer.Lookahead == '*' || lexer.Lookahead == '@' || lexer.Lookahead == '?' || lexer.Lookahead == '-' ||
                 lexer.Lookahead == '0' || lexer.Lookahead == '_'))
            {
                lexer.MarkEnd();
                Advance(lexer);
                if (lexer.Lookahead == '=' || lexer.Lookahead == '[' || lexer.Lookahead == ':' ||
                    lexer.Lookahead == '-' || lexer.Lookahead == '%' || lexer.Lookahead == '#' ||
                    lexer.Lookahead == '/')
                {
                    return false;
                }

                if (validSymbols[ExtglobPattern] && WideChar.IsSpace(lexer.Lookahead))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = ExtglobPattern;
                    return true;
                }
            }

            if (validSymbols[HeredocArrow] && lexer.Lookahead == '<')
            {
                Advance(lexer);
                if (lexer.Lookahead == '<')
                {
                    Advance(lexer);
                    if (lexer.Lookahead == '-')
                    {
                        Advance(lexer);
                        var heredoc = new Heredoc { AllowsIndent = true };
                        _heredocs.Add(heredoc);
                        lexer.ResultSymbol = HeredocArrowDash;
                    }
                    else if (lexer.Lookahead is '<' or '=')
                    {
                        return false;
                    }
                    else
                    {
                        var heredoc = new Heredoc();
                        _heredocs.Add(heredoc);
                        lexer.ResultSymbol = HeredocArrow;
                    }

                    return true;
                }

                return false;
            }

            var isNumber = true;
            if (WideChar.IsDigit(lexer.Lookahead))
            {
                Advance(lexer);
            }
            else if (WideChar.IsAlpha(lexer.Lookahead) || lexer.Lookahead == '_')
            {
                isNumber = false;
                Advance(lexer);
            }
            else
            {
                if (lexer.Lookahead == '{')
                {
                    goto brace_start;
                }

                if (validSymbols[ExpansionWord])
                {
                    goto expansion_word;
                }

                if (validSymbols[ExtglobPattern])
                {
                    goto extglob_pattern;
                }

                return false;
            }

            for (; ; )
            {
                if (WideChar.IsDigit(lexer.Lookahead))
                {
                    Advance(lexer);
                }
                else if (WideChar.IsAlpha(lexer.Lookahead) || lexer.Lookahead == '_')
                {
                    isNumber = false;
                    Advance(lexer);
                }
                else
                {
                    break;
                }
            }

            if (isNumber && validSymbols[FileDescriptor] && (lexer.Lookahead == '>' || lexer.Lookahead == '<'))
            {
                lexer.ResultSymbol = FileDescriptor;
                return true;
            }

            if (validSymbols[VariableName])
            {
                if (lexer.Lookahead == '+')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '=' || lexer.Lookahead == ':' || validSymbols[ClosingBrace])
                    {
                        lexer.ResultSymbol = VariableName;
                        return true;
                    }

                    return false;
                }

                if (lexer.Lookahead == '/')
                {
                    return false;
                }

                // (On the ':' case:) TODO(amaanq): more cases for regular word chars but not variable
                // names for function words, only handling : for now? #235
                if (lexer.Lookahead == '=' || lexer.Lookahead == '[' ||
                    (lexer.Lookahead == ':' && !validSymbols[ClosingBrace] && !validSymbols[OpeningParen]) ||
                    lexer.Lookahead == '%' ||
                    (lexer.Lookahead == '#' && !isNumber) || lexer.Lookahead == '@' ||
                    (lexer.Lookahead == '-' && validSymbols[ClosingBrace]))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = VariableName;
                    return true;
                }

                if (lexer.Lookahead == '?')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    lexer.ResultSymbol = VariableName;
                    return WideChar.IsAlpha(lexer.Lookahead);
                }
            }

            return false;
        }

        if (validSymbols[BareDollar] && !InErrorRecovery(validSymbols) && ScanBareDollar(lexer))
        {
            return true;
        }

    regex:
        if ((validSymbols[Regex] || validSymbols[RegexNoSlash] || validSymbols[RegexNoSpace]) &&
            !InErrorRecovery(validSymbols))
        {
            if (validSymbols[Regex] || validSymbols[RegexNoSpace])
            {
                while (WideChar.IsSpace(lexer.Lookahead))
                {
                    Skip(lexer);
                }
            }

            if ((lexer.Lookahead != '"' && lexer.Lookahead != '\'') ||
                ((lexer.Lookahead == '$' || lexer.Lookahead == '\'') && validSymbols[RegexNoSlash]) ||
                (lexer.Lookahead == '\'' && validSymbols[RegexNoSpace]))
            {
                if (lexer.Lookahead == '$' && validSymbols[RegexNoSlash])
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '(')
                    {
                        return false;
                    }
                }

                lexer.MarkEnd();

                var state = new RegexState();
                while (!state.Done)
                {
                    if (state.InSingleQuote)
                    {
                        if (lexer.Lookahead == '\'')
                        {
                            state.InSingleQuote = false;
                            Advance(lexer);
                            lexer.MarkEnd();
                        }
                    }

                    switch (lexer.Lookahead)
                    {
                        case '\\':
                            state.LastWasEscape = true;
                            break;
                        case '\0':
                            return false;
                        case '(':
                            state.ParenDepth++;
                            state.LastWasEscape = false;
                            break;
                        case '[':
                            state.BracketDepth++;
                            state.LastWasEscape = false;
                            break;
                        case '{':
                            if (!state.LastWasEscape)
                            {
                                state.BraceDepth++;
                            }

                            state.LastWasEscape = false;
                            break;
                        case ')':
                            if (state.ParenDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.ParenDepth--;
                            state.LastWasEscape = false;
                            break;
                        case ']':
                            if (state.BracketDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BracketDepth--;
                            state.LastWasEscape = false;
                            break;
                        case '}':
                            if (state.BraceDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BraceDepth--;
                            state.LastWasEscape = false;
                            break;
                        case '\'':
                            // Enter or exit a single-quoted string.
                            state.InSingleQuote = !state.InSingleQuote;
                            Advance(lexer);
                            state.AdvancedOnce = true;
                            state.LastWasEscape = false;
                            continue;
                        default:
                            state.LastWasEscape = false;
                            break;
                    }

                    if (!state.Done)
                    {
                        if (validSymbols[Regex])
                        {
                            var wasSpace = !state.InSingleQuote && WideChar.IsSpace(lexer.Lookahead);
                            Advance(lexer);
                            state.AdvancedOnce = true;
                            if (!wasSpace || state.ParenDepth > 0)
                            {
                                lexer.MarkEnd();
                            }
                        }
                        else if (validSymbols[RegexNoSlash])
                        {
                            if (lexer.Lookahead == '/')
                            {
                                lexer.MarkEnd();
                                lexer.ResultSymbol = RegexNoSlash;
                                return state.AdvancedOnce;
                            }

                            if (lexer.Lookahead == '\\')
                            {
                                Advance(lexer);
                                state.AdvancedOnce = true;
                                if (!lexer.IsAtEnd && lexer.Lookahead != '[' && lexer.Lookahead != '/')
                                {
                                    Advance(lexer);
                                    lexer.MarkEnd();
                                }
                            }
                            else
                            {
                                var wasSpace = !state.InSingleQuote && WideChar.IsSpace(lexer.Lookahead);
                                Advance(lexer);
                                state.AdvancedOnce = true;
                                if (!wasSpace)
                                {
                                    lexer.MarkEnd();
                                }
                            }
                        }
                        else if (validSymbols[RegexNoSpace])
                        {
                            if (lexer.Lookahead == '\\')
                            {
                                state.FoundNonAlnumDollarUnderDash = true;
                                Advance(lexer);
                                if (!lexer.IsAtEnd)
                                {
                                    Advance(lexer);
                                }
                            }
                            else if (lexer.Lookahead == '$')
                            {
                                lexer.MarkEnd();
                                Advance(lexer);
                                // do not parse a command
                                // substitution
                                if (lexer.Lookahead == '(')
                                {
                                    return false;
                                }

                                // end $ always means regex, e.g.
                                // 99999999$
                                if (WideChar.IsSpace(lexer.Lookahead))
                                {
                                    lexer.ResultSymbol = RegexNoSpace;
                                    lexer.MarkEnd();
                                    return true;
                                }
                            }
                            else
                            {
                                var wasSpace = !state.InSingleQuote && WideChar.IsSpace(lexer.Lookahead);
                                if (wasSpace && state.ParenDepth == 0)
                                {
                                    lexer.MarkEnd();
                                    lexer.ResultSymbol = RegexNoSpace;
                                    return state.FoundNonAlnumDollarUnderDash;
                                }

                                if (!WideChar.IsAlnum(lexer.Lookahead) && lexer.Lookahead != '$' && lexer.Lookahead != '-' &&
                                    lexer.Lookahead != '_')
                                {
                                    state.FoundNonAlnumDollarUnderDash = true;
                                }

                                Advance(lexer);
                            }
                        }
                    }
                }

                lexer.ResultSymbol = validSymbols[RegexNoSlash] ? (ushort)RegexNoSlash
                    : validSymbols[RegexNoSpace] ? (ushort)RegexNoSpace
                    : (ushort)Regex;
                if (validSymbols[Regex] && !state.AdvancedOnce)
                {
                    return false;
                }

                return true;
            }
        }

    extglob_pattern:
        if (validSymbols[ExtglobPattern] && !InErrorRecovery(validSymbols))
        {
            // first skip ws, then check for ? * + @ !
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead == '?' || lexer.Lookahead == '*' || lexer.Lookahead == '+' || lexer.Lookahead == '@' ||
                lexer.Lookahead == '!' || lexer.Lookahead == '-' || lexer.Lookahead == ')' || lexer.Lookahead == '\\' ||
                lexer.Lookahead == '.' || lexer.Lookahead == '[' || WideChar.IsAlpha(lexer.Lookahead))
            {
                if (lexer.Lookahead == '\\')
                {
                    Advance(lexer);
                    if ((WideChar.IsSpace(lexer.Lookahead) || lexer.Lookahead == '"') && lexer.Lookahead != '\r' &&
                        lexer.Lookahead != '\n')
                    {
                        Advance(lexer);
                    }
                    else
                    {
                        return false;
                    }
                }

                if (lexer.Lookahead == ')' && _lastGlobParenDepth == 0)
                {
                    lexer.MarkEnd();
                    Advance(lexer);

                    if (WideChar.IsSpace(lexer.Lookahead))
                    {
                        return false;
                    }
                }

                lexer.MarkEnd();
                var wasNonAlpha = !WideChar.IsAlpha(lexer.Lookahead);
                if (lexer.Lookahead != '[')
                {
                    // no esac
                    if (lexer.Lookahead == 'e')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (lexer.Lookahead == 's')
                        {
                            Advance(lexer);
                            if (lexer.Lookahead == 'a')
                            {
                                Advance(lexer);
                                if (lexer.Lookahead == 'c')
                                {
                                    Advance(lexer);
                                    if (WideChar.IsSpace(lexer.Lookahead))
                                    {
                                        return false;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Advance(lexer);
                    }
                }

                // -\w is just a word, find something else special
                if (lexer.Lookahead == '-')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    while (WideChar.IsAlnum(lexer.Lookahead))
                    {
                        Advance(lexer);
                    }

                    if (lexer.Lookahead is ')' or '\\' or '.')
                    {
                        return false;
                    }

                    lexer.MarkEnd();
                }

                // case item -) or *)
                if (lexer.Lookahead == ')' && _lastGlobParenDepth == 0)
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (WideChar.IsSpace(lexer.Lookahead))
                    {
                        lexer.ResultSymbol = ExtglobPattern;
                        return wasNonAlpha;
                    }
                }

                if (WideChar.IsSpace(lexer.Lookahead))
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = ExtglobPattern;
                    _lastGlobParenDepth = 0;
                    return true;
                }

                if (lexer.Lookahead == '$')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead is '{' or '(')
                    {
                        lexer.ResultSymbol = ExtglobPattern;
                        return true;
                    }
                }

                if (lexer.Lookahead == '|')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    lexer.ResultSymbol = ExtglobPattern;
                    return true;
                }

                if (!WideChar.IsAlnum(lexer.Lookahead) && lexer.Lookahead != '(' && lexer.Lookahead != '"' &&
                    lexer.Lookahead != '[' && lexer.Lookahead != '?' && lexer.Lookahead != '/' &&
                    lexer.Lookahead != '\\' && lexer.Lookahead != '_' && lexer.Lookahead != '*')
                {
                    return false;
                }

                var state = new ExtglobState { SawNonAlphadot = wasNonAlpha, ParenDepth = _lastGlobParenDepth };
                while (!state.Done)
                {
                    switch (lexer.Lookahead)
                    {
                        case '\0':
                            return false;
                        case '(':
                            state.ParenDepth++;
                            break;
                        case '[':
                            state.BracketDepth++;
                            break;
                        case '{':
                            state.BraceDepth++;
                            break;
                        case ')':
                            if (state.ParenDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.ParenDepth--;
                            break;
                        case ']':
                            if (state.BracketDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BracketDepth--;
                            break;
                        case '}':
                            if (state.BraceDepth == 0)
                            {
                                state.Done = true;
                            }

                            state.BraceDepth--;
                            break;
                    }

                    if (lexer.Lookahead == '|')
                    {
                        lexer.MarkEnd();
                        Advance(lexer);
                        if (state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
                        {
                            lexer.ResultSymbol = ExtglobPattern;
                            return true;
                        }
                    }

                    if (!state.Done)
                    {
                        var wasSpace = WideChar.IsSpace(lexer.Lookahead);
                        if (lexer.Lookahead == '$')
                        {
                            lexer.MarkEnd();
                            if (!WideChar.IsAlpha(lexer.Lookahead) && lexer.Lookahead != '.' && lexer.Lookahead != '\\')
                            {
                                state.SawNonAlphadot = true;
                            }

                            Advance(lexer);
                            if (lexer.Lookahead is '(' or '{')
                            {
                                lexer.ResultSymbol = ExtglobPattern;
                                // uint8_t last_glob_paren_depth = uint32_t paren_depth
                                _lastGlobParenDepth = (byte)state.ParenDepth;
                                return state.SawNonAlphadot;
                            }
                        }

                        if (wasSpace)
                        {
                            lexer.MarkEnd();
                            lexer.ResultSymbol = ExtglobPattern;
                            _lastGlobParenDepth = 0;
                            return state.SawNonAlphadot;
                        }

                        if (lexer.Lookahead == '"')
                        {
                            lexer.MarkEnd();
                            lexer.ResultSymbol = ExtglobPattern;
                            _lastGlobParenDepth = 0;
                            return state.SawNonAlphadot;
                        }

                        if (lexer.Lookahead == '\\')
                        {
                            if (!WideChar.IsAlpha(lexer.Lookahead) && lexer.Lookahead != '.' && lexer.Lookahead != '\\')
                            {
                                state.SawNonAlphadot = true;
                            }

                            Advance(lexer);
                            if (WideChar.IsSpace(lexer.Lookahead) || lexer.Lookahead == '"')
                            {
                                Advance(lexer);
                            }
                        }
                        else
                        {
                            if (!WideChar.IsAlpha(lexer.Lookahead) && lexer.Lookahead != '.' && lexer.Lookahead != '\\')
                            {
                                state.SawNonAlphadot = true;
                            }

                            Advance(lexer);
                        }

                        if (!wasSpace)
                        {
                            lexer.MarkEnd();
                        }
                    }
                }

                lexer.ResultSymbol = ExtglobPattern;
                _lastGlobParenDepth = 0;
                return state.SawNonAlphadot;
            }

            _lastGlobParenDepth = 0;

            return false;
        }

    expansion_word:
        if (validSymbols[ExpansionWord])
        {
            var advancedOnce = false;
            var advanceOnceSpace = false;
            for (; ; )
            {
                if (lexer.Lookahead == '\"')
                {
                    return false;
                }

                if (lexer.Lookahead == '$')
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    if (lexer.Lookahead == '{' || lexer.Lookahead == '(' || lexer.Lookahead == '\'' ||
                        WideChar.IsAlnum(lexer.Lookahead))
                    {
                        lexer.ResultSymbol = ExpansionWord;
                        return advancedOnce;
                    }

                    advancedOnce = true;
                }

                if (lexer.Lookahead == '}')
                {
                    lexer.MarkEnd();
                    lexer.ResultSymbol = ExpansionWord;
                    return advancedOnce || advanceOnceSpace;
                }

                if (lexer.Lookahead == '(' && !(advancedOnce || advanceOnceSpace))
                {
                    lexer.MarkEnd();
                    Advance(lexer);
                    while (lexer.Lookahead != ')' && !lexer.IsAtEnd)
                    {
                        // if we find a $( or ${ assume this is valid and is
                        // a garbage concatenation of some weird word + an
                        // expansion
                        // I wonder where this can fail
                        if (lexer.Lookahead == '$')
                        {
                            lexer.MarkEnd();
                            Advance(lexer);
                            if (lexer.Lookahead == '{' || lexer.Lookahead == '(' || lexer.Lookahead == '\'' ||
                                WideChar.IsAlnum(lexer.Lookahead))
                            {
                                lexer.ResultSymbol = ExpansionWord;
                                return advancedOnce;
                            }

                            advancedOnce = true;
                        }
                        else
                        {
                            advancedOnce = advancedOnce || !WideChar.IsSpace(lexer.Lookahead);
                            advanceOnceSpace = advanceOnceSpace || WideChar.IsSpace(lexer.Lookahead);
                            Advance(lexer);
                        }
                    }

                    lexer.MarkEnd();
                    if (lexer.Lookahead == ')')
                    {
                        advancedOnce = true;
                        Advance(lexer);
                        lexer.MarkEnd();
                        if (lexer.Lookahead == '}')
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                if (lexer.Lookahead == '\'')
                {
                    return false;
                }

                if (lexer.IsAtEnd)
                {
                    return false;
                }

                advancedOnce = advancedOnce || !WideChar.IsSpace(lexer.Lookahead);
                advanceOnceSpace = advanceOnceSpace || WideChar.IsSpace(lexer.Lookahead);
                Advance(lexer);
            }
        }

    brace_start:
        if (validSymbols[BraceStart] && !InErrorRecovery(validSymbols))
        {
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                Skip(lexer);
            }

            if (lexer.Lookahead != '{')
            {
                return false;
            }

            Advance(lexer);
            lexer.MarkEnd();

            // isdigit: the ASCII digits
            while (lexer.Lookahead is >= '0' and <= '9')
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '.')
            {
                return false;
            }

            Advance(lexer);

            if (lexer.Lookahead != '.')
            {
                return false;
            }

            Advance(lexer);

            while (lexer.Lookahead is >= '0' and <= '9')
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '}')
            {
                return false;
            }

            lexer.ResultSymbol = BraceStart;
            return true;
        }

        return false;
    }

    private struct RegexState
    {
        public bool Done;
        public bool AdvancedOnce;
        public bool FoundNonAlnumDollarUnderDash;
        public bool LastWasEscape;
        public bool InSingleQuote;
        public uint ParenDepth;
        public uint BracketDepth;
        public uint BraceDepth;
    }

    private struct ExtglobState
    {
        public bool Done;
        public bool SawNonAlphadot;
        public uint ParenDepth;
        public uint BracketDepth;
        public uint BraceDepth;
    }

    private sealed class Heredoc
    {
        public bool IsRaw;
        public bool Started;
        public bool AllowsIndent;

        // String delimiter and current_leading_word: Arrays of chars, the code points truncated to their low byte
        public readonly List<byte> Delimiter = [];
        public readonly List<byte> CurrentLeadingWord = [];
    }
}
