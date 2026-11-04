using System.Buffers.Binary;

namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-ocaml, shared by the OCaml and OCaml type grammars: a port of its
/// <c>common/scanner.h</c> (MIT, Max Brunsfeld and Pieter Goetschalckx), which the <c>src/scanner.c</c> files of both
/// grammars include without options. Its state is whether the scanner is inside a string and the identifier of the
/// quoted string being scanned.
/// </summary>
/// <remarks>
/// The C scanner stores the identifier as an array of <c>int32_t</c> but serializes and deserializes it with a
/// <c>memcpy</c> of as many bytes as it has characters: the port keeps the bytes of the array (little-endian, never
/// cleared, like the memory of <c>ts_realloc</c>, zero when allocated) so that the serialized states and the characters
/// compared after a deserialization are the ones of the C code.
/// </remarks>
public sealed class OCamlScanner : IExternalScanner
{
    // enum TokenType
    private const int Comment = 0;
    private const int LeftQuotedStringDelim = 1;
    private const int RightQuotedStringDelim = 2;
    private const int StringDelim = 3;
    private const int LineNumberDirective = 4;
    private const int NullCharacter = 5;
    private const int ErrorSentinel = 6;

    private bool _inString;

    private int _quotedStringIdLength;
    private int _quotedStringIdCapacity;

    // The int32_t array quoted_string_id, as its bytes
    private byte[] _quotedStringId = [];

    private void QuotedStringIdClear() => _quotedStringIdLength = 0;

    private void QuotedStringIdResize(int minCapacity)
    {
        var capacity = _quotedStringIdCapacity;

        if (capacity >= minCapacity)
        {
            return;
        }

        if (capacity < 16)
        {
            capacity = 16;
        }

        while (capacity < minCapacity)
        {
            capacity <<= 1;
        }

        _quotedStringIdCapacity = capacity;
        Array.Resize(ref _quotedStringId, capacity * sizeof(int));
    }

    private void QuotedStringIdAssign(ReadOnlySpan<byte> buffer)
    {
        var length = buffer.Length;
        if (length > 0)
        {
            QuotedStringIdResize(length);
            buffer.CopyTo(_quotedStringId);
        }

        _quotedStringIdLength = length;
    }

    private int QuotedStringIdCopy(Span<byte> buffer)
    {
        var length = _quotedStringIdLength;
        if (length > 0)
        {
            _quotedStringId.AsSpan(0, length).CopyTo(buffer);
        }

        return length;
    }

    private void QuotedStringIdPush(int c)
    {
        QuotedStringIdResize(_quotedStringIdLength + 1);
        BinaryPrimitives.WriteInt32LittleEndian(_quotedStringId.AsSpan(_quotedStringIdLength++ * sizeof(int)), c);
    }

    private int QuotedStringIdAt(int i) => BinaryPrimitives.ReadInt32LittleEndian(_quotedStringId.AsSpan(i * sizeof(int)));

    private static void Advance(Lexer lexer) => lexer.Advance(false);

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    private static bool Eof(Lexer lexer) => lexer.IsAtEnd;

    private static void ScanString(Lexer lexer)
    {
        for (; ; )
        {
            switch (lexer.Lookahead)
            {
                case '\\':
                    Advance(lexer);
                    Advance(lexer);
                    break;
                case '"':
                    Advance(lexer);
                    return;
                case '\0':
                    if (Eof(lexer))
                    {
                        return;
                    }

                    Advance(lexer);
                    break;
                default:
                    Advance(lexer);
                    break;
            }
        }
    }

    private static readonly int[] LowerUtf8Chars =
    [
        // https://github.com/ocaml/ocaml/blob/5.3.0-rc1/utils/misc.ml#L281-L298
        0xdf,   // ß
        0xe0,   // à
        0xe1,   // á
        0xe2,   // â
        0xe3,   // ã
        0xe4,   // ä
        0xe5,   // å
        0xe6,   // æ
        0xe7,   // ç
        0xe8,   // è
        0xe9,   // é
        0xea,   // ê
        0xeb,   // ë
        0xec,   // ì
        0xed,   // í
        0xee,   // î
        0xef,   // ï
        0xf0,   // ð
        0xf1,   // ñ
        0xf2,   // ò
        0xf3,   // ó
        0xf4,   // ô
        0xf5,   // õ
        0xf6,   // ö
        0xf8,   // ø
        0xf9,   // ù
        0xfa,   // ú
        0xfb,   // û
        0xfc,   // ü
        0xfd,   // ý
        0xfe,   // þ
        0xff,   // ÿ
        0x153,  // œ
        0x161,  // š
        0x17e,  // ž
    ];

    private static readonly int[][] LowerUtf8Pairs =
    [
        // https://github.com/ocaml/ocaml/blob/5.3.0-rc1/utils/misc.ml#L326-L340
        ['a', 0x300, 0xe0],   // à
        ['a', 0x301, 0xe1],   // á
        ['a', 0x302, 0xe2],   // â
        ['a', 0x303, 0xe3],   // ã
        ['a', 0x308, 0xe4],   // ä
        ['a', 0x30a, 0xe5],   // å
        ['c', 0x327, 0xe7],   // ç
        ['e', 0x300, 0xe8],   // è
        ['e', 0x301, 0xe9],   // é
        ['e', 0x302, 0xea],   // ê
        ['e', 0x308, 0xeb],   // ë
        ['i', 0x300, 0xec],   // ì
        ['i', 0x301, 0xed],   // í
        ['i', 0x302, 0xee],   // î
        ['i', 0x308, 0xef],   // ï
        ['n', 0x303, 0xf1],   // ñ
        ['o', 0x300, 0xf2],   // ò
        ['o', 0x301, 0xf3],   // ó
        ['o', 0x302, 0xf4],   // ô
        ['o', 0x303, 0xf5],   // õ
        ['o', 0x308, 0xf6],   // ö
        ['s', 0x30c, 0x161],  // š
        ['u', 0x300, 0xf9],   // ù
        ['u', 0x301, 0xfa],   // ú
        ['u', 0x302, 0xfb],   // û
        ['u', 0x308, 0xfc],   // ü
        ['y', 0x301, 0xfd],   // ý
        ['y', 0x308, 0xff],   // ÿ
        ['z', 0x30c, 0x17e],  // ž
    ];

    private static int SearchLowerUtf8Char(int c)
    {
        var @base = 0;
        var size = LowerUtf8Chars.Length;

        for (var range = size; range != 0; range >>= 1)
        {
            var probe = @base + (range >> 1);
            if (c == LowerUtf8Chars[probe])
            {
                return LowerUtf8Chars[probe];
            }
            else if (c > LowerUtf8Chars[probe])
            {
                @base = probe + 1;
                range--;
            }
        }

        return 0;
    }

    private static int SearchLowerUtf8Pair(int c, int diacritic)
    {
        var @base = 0;
        var size = LowerUtf8Pairs.Length;

        for (var range = size; range != 0; range >>= 1)
        {
            var probe = LowerUtf8Pairs[@base + (range >> 1)];

            if (c == probe[0] && diacritic == probe[1])
            {
                return probe[2];
            }
            else if (c > probe[0] || (c == probe[0] && diacritic > probe[1]))
            {
                @base = @base + (range >> 1) + 1;
                range--;
            }
        }

        return 0;
    }

    private static int ScanQuotedStringDelimChar(Lexer lexer)
    {
        var c = lexer.Lookahead;
        int r;

        if (c == '|')
        {
            return 0;
        }

        if (c == '_')
        {
            Advance(lexer);
            return c;
        }

        if (c >= 'a' && c <= 'z')
        {
            Advance(lexer);

            if (lexer.Lookahead >= 0x300 && lexer.Lookahead <= 0x327 &&
                (r = SearchLowerUtf8Pair(c, lexer.Lookahead)) != 0)
            {
                Advance(lexer);
                return r;
            }
            else
            {
                return c;
            }
        }

        if ((r = SearchLowerUtf8Char(c)) != 0)
        {
            Advance(lexer);
            return r;
        }

        return 0;
    }

    private bool ScanLeftQuotedStringDelimiter(Lexer lexer)
    {
        int c;

        while ((c = ScanQuotedStringDelimChar(lexer)) != 0)
        {
            QuotedStringIdPush(c);
        }

        if (lexer.Lookahead == '|')
        {
            Advance(lexer);
            _inString = true;
            return true;
        }
        else
        {
            QuotedStringIdClear();
            return false;
        }
    }

    private bool ScanRightQuotedStringDelimiter(Lexer lexer)
    {
        for (var i = 0; i < _quotedStringIdLength; i++)
        {
            if (ScanQuotedStringDelimChar(lexer) != QuotedStringIdAt(i))
            {
                return false;
            }
        }

        if (lexer.Lookahead == '}')
        {
            _inString = false;
            QuotedStringIdClear();
            return true;
        }
        else
        {
            return false;
        }
    }

    private bool ScanQuotedString(Lexer lexer)
    {
        if (!ScanLeftQuotedStringDelimiter(lexer))
        {
            return false;
        }

        for (; ; )
        {
            switch (lexer.Lookahead)
            {
                case '|':
                    Advance(lexer);
                    if (ScanRightQuotedStringDelimiter(lexer))
                    {
                        return true;
                    }

                    break;
                case '\0':
                    if (Eof(lexer))
                    {
                        return false;
                    }

                    Advance(lexer);
                    break;
                default:
                    Advance(lexer);
                    break;
            }
        }
    }

    private static int ScanCharacter(Lexer lexer)
    {
        var last = 0;

        switch (lexer.Lookahead)
        {
            case '\\':
                Advance(lexer);
                if (WideChar.IsDigit(lexer.Lookahead))
                {
                    Advance(lexer);
                    for (var i = 0; i < 2; i++)
                    {
                        if (!WideChar.IsDigit(lexer.Lookahead))
                        {
                            return 0;
                        }

                        Advance(lexer);
                    }
                }
                else
                {
                    switch (lexer.Lookahead)
                    {
                        case 'x':
                            Advance(lexer);
                            for (var i = 0; i < 2; i++)
                            {
                                if (!WideChar.IsDigit(lexer.Lookahead) &&
                                    (WideChar.ToUpper(lexer.Lookahead) < 'A' ||
                                     WideChar.ToUpper(lexer.Lookahead) > 'F'))
                                {
                                    return 0;
                                }

                                Advance(lexer);
                            }

                            break;
                        case 'o':
                            Advance(lexer);
                            for (var i = 0; i < 3; i++)
                            {
                                if (!WideChar.IsDigit(lexer.Lookahead) || lexer.Lookahead > '7')
                                {
                                    return 0;
                                }

                                Advance(lexer);
                            }

                            break;
                        case '\'':
                        case '"':
                        case '\\':
                        case 'n':
                        case 't':
                        case 'b':
                        case 'r':
                        case ' ':
                            last = lexer.Lookahead;
                            Advance(lexer);
                            break;
                        default:
                            return 0;
                    }
                }

                break;
            case '\'':
                break;
            case '\r':
                Advance(lexer);
                while (lexer.Lookahead == '\r')
                {
                    Advance(lexer);
                }

                if (lexer.Lookahead != '\n')
                {
                    return 0;
                }

                Advance(lexer);
                break;
            case '\0':
                if (Eof(lexer))
                {
                    return 0;
                }

                Advance(lexer);
                break;
            default:
                if (lexer.Lookahead < 256)
                {
                    last = lexer.Lookahead;
                    Advance(lexer);
                }
                else
                {
                    return 0;
                }

                break;
        }

        if (lexer.Lookahead == '\'')
        {
            Advance(lexer);
            return 0;
        }

        return last;
    }

    private static bool IsLowercaseExt(int c) => (c >= 'a' && c <= 'z') || c == '_' || c >= 192;

    private static bool IsIdentstart(int c) => IsLowercaseExt(c) || (c >= 'A' && c <= 'Z');

    private static bool IsIdentchar(int c) => IsIdentstart(c) || (c >= '0' && c <= '9') || c == '\'';

    private static bool ScanIdentifier(Lexer lexer)
    {
        if (IsIdentstart(lexer.Lookahead))
        {
            Advance(lexer);
            while (IsIdentchar(lexer.Lookahead))
            {
                Advance(lexer);
            }

            return true;
        }

        return false;
    }

    private static bool ScanExtattrident(Lexer lexer)
    {
        while (ScanIdentifier(lexer))
        {
            if (lexer.Lookahead != '.')
            {
                return true;
            }

            Advance(lexer);
        }

        return false;
    }

    private bool ScanComment(Lexer lexer)
    {
        var last = 0;

        if (lexer.Lookahead != '*')
        {
            return false;
        }

        Advance(lexer);

        for (; ; )
        {
            switch (last != 0 ? last : lexer.Lookahead)
            {
                case '(':
                    if (last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    ScanComment(lexer);
                    break;
                case '*':
                    if (last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    if (lexer.Lookahead == ')')
                    {
                        Advance(lexer);
                        return true;
                    }

                    break;
                case '\'':
                    if (last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    last = ScanCharacter(lexer);
                    break;
                case '"':
                    if (last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    ScanString(lexer);
                    break;
                case '{':
                    if (last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    if (lexer.Lookahead == '%')
                    {
                        Advance(lexer);
                        if (lexer.Lookahead == '%')
                        {
                            Advance(lexer);
                        }

                        if (ScanExtattrident(lexer))
                        {
                            while (WideChar.IsSpace(lexer.Lookahead))
                            {
                                Advance(lexer);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (ScanQuotedString(lexer))
                    {
                        Advance(lexer);
                    }

                    break;
                case '\0':
                    if (Eof(lexer))
                    {
                        return true;
                    }

                    if (last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    break;
                default:
                    if (ScanIdentifier(lexer) || last != 0)
                    {
                        last = 0;
                    }
                    else
                    {
                        Advance(lexer);
                    }

                    break;
            }
        }
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        buffer[0] = _inString ? (byte)1 : (byte)0;
        if (_quotedStringIdLength < ExternalScanner.StateCapacity)
        {
            return QuotedStringIdCopy(buffer.Slice(1)) + 1;
        }
        else
        {
            return 1;
        }
    }

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        if (state.Length > 0)
        {
            _inString = state[0] != 0;
            QuotedStringIdAssign(state.Slice(1));
        }
        else
        {
            _inString = false;
            QuotedStringIdClear();
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (!validSymbols[ErrorSentinel] &&
            validSymbols[LeftQuotedStringDelim] &&
            (IsLowercaseExt(lexer.Lookahead) || lexer.Lookahead == '|'))
        {
            lexer.ResultSymbol = LeftQuotedStringDelim;
            return ScanLeftQuotedStringDelimiter(lexer);
        }

        if (!validSymbols[ErrorSentinel] &&
            validSymbols[RightQuotedStringDelim] && (lexer.Lookahead == '|'))
        {
            Advance(lexer);
            lexer.ResultSymbol = RightQuotedStringDelim;
            return ScanRightQuotedStringDelimiter(lexer);
        }

        if (_inString && validSymbols[StringDelim] &&
            lexer.Lookahead == '"')
        {
            Advance(lexer);
            _inString = false;
            lexer.ResultSymbol = StringDelim;
            return true;
        }

        while (WideChar.IsSpace(lexer.Lookahead))
        {
            Skip(lexer);
        }

        if (!_inString && validSymbols[StringDelim] &&
            lexer.Lookahead == '"')
        {
            Advance(lexer);
            _inString = true;
            lexer.ResultSymbol = StringDelim;
            return true;
        }

        if (!_inString && validSymbols[LineNumberDirective] &&
            lexer.Lookahead == '#' && lexer.GetColumn() == 0)
        {
            Advance(lexer);

            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }

            if (!WideChar.IsDigit(lexer.Lookahead))
            {
                return false;
            }

            while (WideChar.IsDigit(lexer.Lookahead))
            {
                Advance(lexer);
            }

            while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t')
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '"')
            {
                return false;
            }

            Advance(lexer);

            while (lexer.Lookahead != '\n' && lexer.Lookahead != '\r' &&
                   lexer.Lookahead != '"' && !Eof(lexer))
            {
                Advance(lexer);
            }

            if (lexer.Lookahead != '"')
            {
                return false;
            }

            Advance(lexer);

            while (lexer.Lookahead != '\n' && lexer.Lookahead != '\r' &&
                   !Eof(lexer))
            {
                Advance(lexer);
            }

            lexer.ResultSymbol = LineNumberDirective;
            return true;
        }

        if (!_inString && validSymbols[Comment] &&
            lexer.Lookahead == '(')
        {
            Advance(lexer);
            lexer.ResultSymbol = Comment;
            return ScanComment(lexer);
        }

        if (validSymbols[NullCharacter] && lexer.Lookahead == '\0' &&
            !Eof(lexer))
        {
            Advance(lexer);
            lexer.ResultSymbol = NullCharacter;
            return true;
        }

        return false;
    }
}
