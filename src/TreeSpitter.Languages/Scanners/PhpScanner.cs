using System.Buffers.Binary;
using System.Diagnostics;

namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-php: a port of its <c>common/scanner.h</c>, which <c>php/src/scanner.c</c>
/// includes (MIT, Josh Vera, Max Brunsfeld, Amaan Qureshi, Christian Frøystad, Caleb White). Its state is the stack
/// of the open heredocs and nowdocs, with their words; it scans the contents of the strings with interpolations, and
/// the automatic semicolon before <c>?&gt;</c>.
/// </summary>
public sealed class PhpScanner : IExternalScanner
{
    // enum TokenType
    private const int AutomaticSemicolon = 0;
    private const int EncapsedStringChars = 1;
    private const int EncapsedStringCharsAfterVariable = 2;
    private const int ExecutionStringChars = 3;
    private const int ExecutionStringCharsAfterVariable = 4;
    private const int EncapsedStringCharsHeredoc = 5;
    private const int EncapsedStringCharsAfterVariableHeredoc = 6;
    private const int EofToken = 7;
    private const int HeredocStart = 8;
    private const int HeredocEnd = 9;
    private const int NowdocString = 10;
    private const int SentinelError = 11; // Unused token used to indicate error recovery mode

    // Array(Heredoc) heredocs. Like in the C scanner, deserialize doesn't remove the heredocs after the ones of the
    // state: their words are only emptied. (has_leading_whitespace is never read: it isn't ported.)
    private readonly List<Heredoc> _heredocs = [];

    // The String of scan_heredoc_word for heredoc_end, reused
    private readonly List<int> _word = [];

    private static bool StringEq(List<int> self, List<int> other)
    {
        if (self.Count != other.Count)
        {
            return false;
        }

        for (var i = 0; i < self.Count; i++)
        {
            if (self[i] != other[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void ResetHeredoc(Heredoc heredoc)
    {
        heredoc.Word.Clear();
        heredoc.EndWordIndentationAllowed = false;
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        var size = 0;

        buffer[size++] = (byte)_heredocs.Count;
        for (var j = 0; j < _heredocs.Count; j++)
        {
            var heredoc = _heredocs[j];
            var wordSize = heredoc.Word.Count * sizeof(int);
            if (size + 5 + wordSize >= ExternalScanner.StateCapacity)
            {
                return 0;
            }

            buffer[size++] = (byte)(heredoc.EndWordIndentationAllowed ? 1 : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(size), (uint)heredoc.Word.Count);
            size += sizeof(uint);
            if (heredoc.Word.Count > 0)
            {
                foreach (var c in heredoc.Word)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(size), c);
                    size += sizeof(int);
                }
            }
        }

        return size;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        var size = 0;

        for (var i = 0; i < _heredocs.Count; i++)
        {
            ResetHeredoc(_heredocs[i]);
        }

        if (state.Length == 0)
        {
            return;
        }

        var openHeredocCount = state[size++];
        for (var i = 0; i < openHeredocCount; i++)
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

            heredoc.EndWordIndentationAllowed = state[size++] != 0;
            var wordLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(size));
            size += sizeof(uint);
            var wordSize = wordLength * sizeof(int);
            if (wordSize > 0)
            {
                for (var k = 0; k < wordLength; k++)
                {
                    heredoc.Word.Add(BinaryPrimitives.ReadInt32LittleEndian(state.Slice(size + k * sizeof(int))));
                }

                size += wordSize;
            }
        }

        Debug.Assert(size == state.Length, "The state has the length of its heredocs.");
    }

    private static bool ScanWhitespace(Lexer lexer)
    {
        for (; ; )
        {
            while (WideChar.IsSpace(lexer.Lookahead))
            {
                lexer.Advance(false);
            }

            if (lexer.Lookahead == '/')
            {
                lexer.Advance(false);

                if (lexer.Lookahead == '/')
                {
                    lexer.Advance(false);
                    while (lexer.Lookahead != 0 && lexer.Lookahead != '\n')
                    {
                        lexer.Advance(false);
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

    private static bool IsValidNameChar(Lexer lexer) => WideChar.IsAlnum(lexer.Lookahead) || lexer.Lookahead == '_' || lexer.Lookahead >= 0x80;

    private static bool IsEscapableSequence(Lexer lexer)
    {
        // Note: remember to also update the escape_sequence rule in the
        // main grammar whenever changing this method
        var letter = lexer.Lookahead;

        if (letter is 'n' or 'r' or 't' or 'v' or 'e' or 'f' or '\\' or '$' or '"')
        {
            return true;
        }

        // Hex
        if (letter == 'x')
        {
            lexer.Advance(false);
            return WideChar.IsXDigit(lexer.Lookahead);
        }

        // Unicode
        if (letter == 'u')
        {
            return true; // We handle the case where this is not really an escape
                         // sequence in grammar.js - this is needed to support the
                         // edge case "\u{$a}" in which case "\u" is to be
                         // interpreted as characters and {$a} as a variable
        }

        // Octal
        return WideChar.IsDigit(lexer.Lookahead) && lexer.Lookahead >= '0' && lexer.Lookahead <= '7';
    }

    private static List<int> ScanHeredocWord(Lexer lexer, List<int> result)
    {
        result.Clear();

        while (IsValidNameChar(lexer))
        {
            result.Add(lexer.Lookahead);
            lexer.Advance(false);
        }

        return result;
    }

    private bool ScanNowdocString(Lexer lexer)
    {
        var hasConsumedContent = false;
        if (_heredocs.Count == 0)
        {
            return false;
        }

        // While PHP requires the nowdoc end tag to be the very first on a new line,
        // there may be an arbitrary amount of whitespace before the closing token
        while (WideChar.IsSpace(lexer.Lookahead))
        {
            lexer.Advance(false);
            hasConsumedContent = true;
        }

        var endTagMatched = false;
        var heredocTag = _heredocs[_heredocs.Count - 1].Word;

        for (var i = 0; i < heredocTag.Count; i++)
        {
            if (lexer.Lookahead != heredocTag[i])
            {
                break;
            }

            lexer.Advance(false);
            hasConsumedContent = true;

            endTagMatched = i == heredocTag.Count - 1 && (WideChar.IsSpace(lexer.Lookahead) || lexer.Lookahead == ';' ||
                                                          lexer.Lookahead == ',' || lexer.Lookahead == ')');
        }

        if (endTagMatched)
        {
            // There may be an arbitrary amount of white space after the end tag
            while (WideChar.IsSpace(lexer.Lookahead) && lexer.Lookahead != '\r' && lexer.Lookahead != '\n')
            {
                lexer.Advance(false);
                hasConsumedContent = true;
            }

            // Return to allow the end tag parsing if we've encountered an end tag
            // at a valid position
            if (lexer.Lookahead is ';' or ',' or ')' or '\n' or '\r')
            {
                // , and ) is needed to support heredoc in function arguments
                return false;
            }
        }

        for (var hasContent = hasConsumedContent; ; hasContent = true)
        {
            lexer.MarkEnd();

            switch (lexer.Lookahead)
            {
                case '\n':
                case '\r':
                    return hasContent;
                default:
                    if (lexer.IsAtEnd)
                    {
                        return false;
                    }

                    lexer.Advance(false);
                    break;
            }
        }
    }

    private bool ScanEncapsedPartString(Lexer lexer, bool isAfterVariable, bool isHeredoc, bool isExecutionString)
    {
        var hasConsumedContent = false;

        if (isHeredoc && _heredocs.Count > 0)
        {
            // While PHP requires the heredoc end tag to be the very first on a new
            // line, there may be an arbitrary amount of whitespace before the
            // closing token However, we should not consume \r or \n
            while (WideChar.IsSpace(lexer.Lookahead) && lexer.Lookahead != '\r' && lexer.Lookahead != '\n')
            {
                lexer.Advance(false);
                hasConsumedContent = true;
            }

            var heredocTag = _heredocs[_heredocs.Count - 1].Word;

            var endTagMatched = false;

            for (var i = 0; i < heredocTag.Count; i++)
            {
                if (lexer.Lookahead != heredocTag[i])
                {
                    break;
                }

                hasConsumedContent = true;
                lexer.Advance(false);

                endTagMatched = i == heredocTag.Count - 1 && (WideChar.IsSpace(lexer.Lookahead) || lexer.Lookahead == ';' ||
                                                              lexer.Lookahead == ',' || lexer.Lookahead == ')');
            }

            if (endTagMatched)
            {
                // There may be an arbitrary amount of white space after the end tag
                // However, we should not consume \r or \n
                while (WideChar.IsSpace(lexer.Lookahead) && lexer.Lookahead != '\r' && lexer.Lookahead != '\n')
                {
                    lexer.Advance(false);
                    hasConsumedContent = true;
                }

                // Return to allow the end tag parsing if we've encountered an end
                // tag at a valid position
                if (lexer.Lookahead is ';' or ',' or ')' or '\n' or '\r')
                {
                    // , and ) is needed to support heredoc in function arguments
                    return false;
                }
            }
        }

        for (var hasContent = hasConsumedContent; ; hasContent = true)
        {
            lexer.MarkEnd();

            switch (lexer.Lookahead)
            {
                case '"':
                    if (!isHeredoc && !isExecutionString)
                    {
                        return hasContent;
                    }

                    lexer.Advance(false);
                    break;
                case '`':
                    if (isExecutionString)
                    {
                        return hasContent;
                    }

                    lexer.Advance(false);
                    break;
                case '\n':
                case '\r':
                    if (isHeredoc)
                    {
                        return hasContent;
                    }

                    lexer.Advance(false);
                    break;
                case '\\':
                    lexer.Advance(false);

                    // \{ should not be interpreted as an escape sequence, but both
                    // should be consumed as normal characters
                    if (lexer.Lookahead == '{')
                    {
                        lexer.Advance(false);
                        break;
                    }

                    if (isExecutionString && lexer.Lookahead == '`')
                    {
                        return hasContent;
                    }

                    if (isHeredoc && lexer.Lookahead == '\\')
                    {
                        lexer.Advance(false);
                        break;
                    }

                    if (IsEscapableSequence(lexer))
                    {
                        return hasContent;
                    }

                    break;
                case '$':
                    lexer.Advance(false);

                    if ((IsValidNameChar(lexer) && !WideChar.IsDigit(lexer.Lookahead)) || lexer.Lookahead == '{')
                    {
                        return hasContent;
                    }

                    break;
                case '-':
                    if (isAfterVariable)
                    {
                        lexer.Advance(false);
                        if (lexer.Lookahead == '>')
                        {
                            lexer.Advance(false);
                            if (IsValidNameChar(lexer))
                            {
                                return hasContent;
                            }

                            break;
                        }

                        break;
                    }

                    // Falls through to '['
                    goto case '[';
                case '[':
                    if (isAfterVariable)
                    {
                        return hasContent;
                    }

                    lexer.Advance(false);
                    break;
                case '{':
                    lexer.Advance(false);
                    if (lexer.Lookahead == '$')
                    {
                        return hasContent;
                    }

                    break;
                default:
                    if (lexer.IsAtEnd)
                    {
                        return false;
                    }

                    lexer.Advance(false);
                    break;
            }

            isAfterVariable = false;
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        var isErrorRecovery = validSymbols[SentinelError];

        if (isErrorRecovery)
        {
            return false;
        }

        lexer.MarkEnd();

        if (validSymbols[EncapsedStringCharsAfterVariable])
        {
            lexer.ResultSymbol = EncapsedStringCharsAfterVariable;
            return ScanEncapsedPartString(lexer,
                                          /* is_after_variable */ true,
                                          /* is_heredoc */ false,
                                          /* is_execution_string */ false);
        }

        if (validSymbols[EncapsedStringChars])
        {
            lexer.ResultSymbol = EncapsedStringChars;
            return ScanEncapsedPartString(lexer,
                                          /* is_after_variable */ false,
                                          /* is_heredoc */ false,
                                          /* is_execution_string */ false);
        }

        if (validSymbols[ExecutionStringCharsAfterVariable])
        {
            lexer.ResultSymbol = ExecutionStringCharsAfterVariable;
            return ScanEncapsedPartString(lexer,
                                          /* is_after_variable */ true,
                                          /* is_heredoc */ false,
                                          /* is_execution_string */ true);
        }

        if (validSymbols[ExecutionStringChars])
        {
            lexer.ResultSymbol = ExecutionStringChars;
            return ScanEncapsedPartString(lexer,
                                          /* is_after_variable */ false,
                                          /* is_heredoc */ false,
                                          /* is_execution_string */ true);
        }

        if (validSymbols[EncapsedStringCharsAfterVariableHeredoc])
        {
            lexer.ResultSymbol = EncapsedStringCharsAfterVariableHeredoc;
            return ScanEncapsedPartString(lexer,
                                          /* is_after_variable */ true,
                                          /* is_heredoc */ true,
                                          /* is_execution_string */ false);
        }

        if (validSymbols[EncapsedStringCharsHeredoc])
        {
            lexer.ResultSymbol = EncapsedStringCharsHeredoc;
            return ScanEncapsedPartString(lexer,
                                          /* is_after_variable */ false,
                                          /* is_heredoc */ true,
                                          /* is_execution_string */ false);
        }

        if (validSymbols[NowdocString])
        {
            lexer.ResultSymbol = NowdocString;
            return ScanNowdocString(lexer);
        }

        if (validSymbols[HeredocEnd])
        {
            lexer.ResultSymbol = HeredocEnd;
            if (_heredocs.Count == 0)
            {
                return false;
            }

            var heredoc = _heredocs[_heredocs.Count - 1];

            while (WideChar.IsSpace(lexer.Lookahead))
            {
                lexer.Advance(true);
            }

            var word = ScanHeredocWord(lexer, _word);
            if (!StringEq(word, heredoc.Word))
            {
                return false;
            }

            lexer.MarkEnd();
            _heredocs.RemoveAt(_heredocs.Count - 1);
            return true;
        }

        if (!ScanWhitespace(lexer))
        {
            return false;
        }

        if (validSymbols[EofToken] && lexer.IsAtEnd)
        {
            lexer.ResultSymbol = EofToken;
            return true;
        }

        if (validSymbols[HeredocStart])
        {
            lexer.ResultSymbol = HeredocStart;
            var heredoc = new Heredoc();

            while (WideChar.IsSpace(lexer.Lookahead))
            {
                lexer.Advance(true);
            }

            ScanHeredocWord(lexer, heredoc.Word);
            if (heredoc.Word.Count == 0)
            {
                return false;
            }

            lexer.MarkEnd();

            _heredocs.Add(heredoc);
            return true;
        }

        if (validSymbols[AutomaticSemicolon])
        {
            lexer.ResultSymbol = AutomaticSemicolon;

            if (lexer.Lookahead != '?')
            {
                return false;
            }

            lexer.Advance(false);

            return lexer.Lookahead == '>';
        }

        return false;
    }

    private sealed class Heredoc
    {
        public bool EndWordIndentationAllowed;

        public readonly List<int> Word = [];
    }
}
