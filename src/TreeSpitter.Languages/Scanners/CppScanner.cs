using System.Buffers.Binary;

namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-cpp: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld). Its state is the
/// delimiter of the current raw string literal, serialized as the 32-bit <c>wchar_t</c> code points of Linux.
/// </summary>
public sealed class CppScanner : IExternalScanner
{
    // enum TokenType
    private const int RawStringDelimiter = 0;
    private const int RawStringContent = 1;

    // The spec limits delimiters to 16 chars
    private const int MaxDelimiterLength = 16;

    private const int WCharSize = sizeof(int);

    private byte _delimiterLength;
    private readonly int[] _delimiter = new int[MaxDelimiterLength];

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private void Reset()
    {
        _delimiterLength = 0;
        Array.Clear(_delimiter, 0, _delimiter.Length);
    }

    // Scan the raw string delimiter in R"delimiter(content)delimiter"
    private bool ScanRawStringDelimiter(Lexer lexer)
    {
        if (_delimiterLength > 0)
        {
            // Closing delimiter: must exactly match the opening delimiter.
            // We already checked this when scanning content, but this is how we
            // know when to stop. We can't stop at ", because R"""hello""" is valid.
            for (var i = 0; i < _delimiterLength; ++i)
            {
                if (lexer.Lookahead != _delimiter[i])
                {
                    return false;
                }

                Advance(lexer);
            }

            Reset();
            return true;
        }

        // Opening delimiter: record the d-char-sequence up to (.
        // d-char is any basic character except parens, backslashes, and spaces.
        while (true)
        {
            if (_delimiterLength >= MaxDelimiterLength || lexer.IsAtEnd || lexer.Lookahead == '\\' ||
                WideChar.IsSpace(lexer.Lookahead))
            {
                return false;
            }

            if (lexer.Lookahead == '(')
            {
                // Rather than create a token for an empty delimiter, we fail and
                // let the grammar fall back to a delimiter-less rule.
                return _delimiterLength > 0;
            }

            _delimiter[_delimiterLength++] = lexer.Lookahead;
            Advance(lexer);
        }
    }

    // Scan the raw string content in R"delimiter(content)delimiter"
    private bool ScanRawStringContent(Lexer lexer)
    {
        // The progress made through the delimiter since the last ')'.
        // The delimiter may not contain ')' so a single counter suffices.
        for (var delimiterIndex = -1; ;)
        {
            // If we hit EOF, consider the content to terminate there.
            // This forms an incomplete raw_string_literal, and models the code
            // well.
            if (lexer.IsAtEnd)
            {
                lexer.MarkEnd();
                return true;
            }

            if (delimiterIndex >= 0)
            {
                if (delimiterIndex == _delimiterLength)
                {
                    if (lexer.Lookahead == '"')
                    {
                        return true;
                    }

                    delimiterIndex = -1;
                }
                else
                {
                    if (lexer.Lookahead == _delimiter[delimiterIndex])
                    {
                        delimiterIndex += 1;
                    }
                    else
                    {
                        delimiterIndex = -1;
                    }
                }
            }

            if (delimiterIndex == -1 && lexer.Lookahead == ')')
            {
                // The content doesn't include the )delimiter" part.
                // We must still scan through it, but exclude it from the token.
                lexer.MarkEnd();
                delimiterIndex = 0;
            }

            Advance(lexer);
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[RawStringDelimiter] && validSymbols[RawStringContent])
        {
            // we're in error recovery
            return false;
        }

        // No skipping leading whitespace: raw-string grammar is space-sensitive.
        if (validSymbols[RawStringDelimiter])
        {
            lexer.ResultSymbol = RawStringDelimiter;
            return ScanRawStringDelimiter(lexer);
        }

        if (validSymbols[RawStringContent])
        {
            lexer.ResultSymbol = RawStringContent;
            return ScanRawStringContent(lexer);
        }

        return false;
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        var size = _delimiterLength * WCharSize;
        for (var i = 0; i < _delimiterLength; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(i * WCharSize), _delimiter[i]);
        }

        return size;
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _delimiterLength = (byte)(state.Length / WCharSize);
        if (state.Length > 0)
        {
            for (var i = 0; i < _delimiterLength; i++)
            {
                _delimiter[i] = BinaryPrimitives.ReadInt32LittleEndian(state.Slice(i * WCharSize));
            }
        }
    }
}
