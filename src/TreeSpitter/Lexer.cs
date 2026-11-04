using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TreeSpitter;

/// <summary>
/// The state of the lexer during a parse: the UTF-8 input, the current position and the token being lexed. Lex
/// functions, the table-driven one of the virtual machine and the generated ones, read and update it like the
/// <c>TSLexer</c> of tree-sitter. Positions are byte offsets.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class Lexer
{
    /// <summary>The code point returned for invalid UTF-8 sequences.</summary>
    public const int DecodeError = -1;

    /// <summary>The UTF-8 input.</summary>
    public byte[] Input = [];

    /// <summary>The length of the input, in bytes.</summary>
    public int Length;

    /// <summary>The current position.</summary>
    public int Position;

    /// <summary>The start of the token, after the skipped characters.</summary>
    public int TokenStart;

    /// <summary>The end of the last accepted token, or -1.</summary>
    public int TokenEnd = -1;

    /// <summary>
    /// The symbol of the last accepted token. External scanners set the index of their token, in the order of the
    /// grammar's <c>externals</c>, like tree-sitter's <c>result_symbol</c>.
    /// </summary>
    public ushort ResultSymbol;

    private int _lookaheadSize;

    /// <summary>
    /// Gets the code point at the current position, for external scanners: 0 at the end of the input, and
    /// <see cref="DecodeError"/> for an invalid UTF-8 sequence, like tree-sitter's <c>lookahead</c>.
    /// </summary>
    public int Lookahead { get; private set; }

    /// <summary>Gets whether the lexer is at the end of the input, like tree-sitter's <c>eof()</c>.</summary>
    public bool IsAtEnd => Position >= Length;

    /// <summary>Whether an external scanner read the column, tree-sitter's <c>did_get_column</c>.</summary>
    internal bool DidGetColumn;

    /// <summary>Prepares the lexer for an external scanner at the current position.</summary>
    internal void StartExternalScan()
    {
        DidGetColumn = false;
        Lookahead = Decode(Input, Position, Length, out _lookaheadSize);
    }

    /// <summary>
    /// Advances past the lookahead, for external scanners: a skipped code point isn't part of the token, like
    /// tree-sitter's <c>advance(lexer, skip)</c>. Does nothing at the end of the input.
    /// </summary>
    public void Advance(bool skip)
    {
        if (Position >= Length)
        {
            return;
        }

        Position += _lookaheadSize;
        if (skip)
        {
            TokenStart = Position;
        }

        Lookahead = Decode(Input, Position, Length, out _lookaheadSize);
    }

    /// <summary>Marks the end of the token at the current position, for external scanners, like tree-sitter's <c>mark_end</c>.</summary>
    public void MarkEnd() => TokenEnd = Position;

    /// <summary>
    /// Gets the column of the current position in code points, for external scanners, like tree-sitter's
    /// <c>get_column</c>: the tokens of the scanner then depend on their column.
    /// </summary>
    public int GetColumn()
    {
        DidGetColumn = true;
        var lineStart = Position;
        while (lineStart > 0 && Input[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        var column = 0;
        for (var i = lineStart; i < Position;)
        {
            Decode(Input, i, Length, out var size);
            i += Math.Max(1, size);
            column++;
        }

        return column;
    }

    /// <summary>
    /// Decodes the code point at a position, and its length in bytes (0 at the end of the input). Invalid sequences
    /// decode to <see cref="DecodeError"/>, with a length of one byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Decode(byte[] input, int position, int length, out int size)
    {
        if (position >= length)
        {
            size = 0;
            return 0;
        }

        int b0 = input[position];
        if (b0 < 0x80)
        {
            size = 1;
            return b0;
        }

        return DecodeMultiByte(input, position, length, out size);
    }

    private static int DecodeMultiByte(byte[] input, int position, int length, out int size)
    {
        int b0 = input[position];
        size = 1;
        int count, codePoint, min;
        if (b0 >= 0xC2 && b0 <= 0xDF)
        {
            count = 1;
            codePoint = b0 & 0x1F;
            min = 0x80;
        }
        else if (b0 >= 0xE0 && b0 <= 0xEF)
        {
            count = 2;
            codePoint = b0 & 0x0F;
            min = 0x800;
        }
        else if (b0 >= 0xF0 && b0 <= 0xF4)
        {
            count = 3;
            codePoint = b0 & 0x07;
            min = 0x10000;
        }
        else
        {
            return DecodeError;
        }

        if (position + count >= length)
        {
            return DecodeError;
        }

        for (var i = 1; i <= count; i++)
        {
            int b = input[position + i];
            if ((b & 0xC0) != 0x80)
            {
                return DecodeError;
            }

            codePoint = (codePoint << 6) | (b & 0x3F);
        }

        if (codePoint < min || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return DecodeError;
        }

        size = count + 1;
        return codePoint;
    }
}

/// <summary>
/// A lex function: the lexer of a language, implemented by the table-driven lexer of the virtual machine or by a
/// generated lexer. Implementations are structs, so that the parser is specialized for each of them.
/// </summary>
public interface ILexFunction
{
    /// <summary>Lexes a token from the current position, starting in a lex state. Returns whether a token was accepted.</summary>
    bool Lex(Lexer lexer, int state);

    /// <summary>Lexes a keyword from the current position. Returns whether a keyword was accepted.</summary>
    bool LexKeyword(Lexer lexer, int state);

    /// <summary>
    /// Looks up the bytes of a word token in the keywords of the language: returns the symbol of the keyword, -1 when
    /// the token isn't a keyword, or -2 when <see cref="LexKeyword"/> must run (the keywords aren't a set of strings).
    /// </summary>
    int MatchKeyword(byte[] input, int start, int end);
}

/// <summary>
/// The lexer of the virtual machine: runs the lex automata of a <see cref="Language"/>. Generated parsers use it for
/// the automata too large to be compiled efficiently.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public readonly struct TableLexFunction : ILexFunction
{
    internal TableLexFunction(LexAutomaton main, LexAutomaton keyword, KeywordTable? keywords)
    {
        _main = main;
        _keyword = keyword;
        _keywords = keywords;
    }

    private readonly LexAutomaton _main;
    private readonly LexAutomaton _keyword;
    private readonly KeywordTable? _keywords;

    /// <inheritdoc />
    public bool Lex(Lexer lexer, int state) => Run(_main, lexer, state);

    /// <inheritdoc />
    public bool LexKeyword(Lexer lexer, int state) => Run(_keyword, lexer, state);

    /// <inheritdoc />
    public int MatchKeyword(byte[] input, int start, int end) => _keywords is null ? KeywordTable.UseAutomaton : _keywords.Lookup(input, start, end);

    private static bool Run(LexAutomaton table, Lexer lexer, int state)
    {
        if (table.StateCount == 0)
        {
            return false;
        }

        var input = lexer.Input;
        var length = lexer.Length;
        var position = lexer.Position;
        var tokenStart = lexer.TokenStart;
        var tokenEnd = lexer.TokenEnd;
        var symbol = lexer.ResultSymbol;
        var result = false;
        var accept = table.Accept;
        var eofTarget = table.EofTarget;
        var ascii = table.Ascii;
        var loops = table.SelfLoops;

        while (true)
        {
            var accepted = accept[state];
            if (accepted >= 0)
            {
                result = true;
                symbol = (ushort)accepted;
                tokenEnd = position;
            }

            if (position >= length)
            {
                var target = eofTarget[state];
                if (target < 0 || target == state)
                {
                    break;
                }

                state = target;
                continue;
            }

            int c = input[position];
            int next;
            var size = 1;
            if (c < 128)
            {
                next = ascii[(state << 7) + c] - 1;
                if ((next & ~LexAutomaton.SkipFlag) == state && loops[state] is { } loop)
                {
                    // A run of bytes looping on the state: skip it at once, then continue in the state
                    position = loop.SkipRun(input, position + 1, length);
                    if (loop.Skip)
                    {
                        tokenStart = position;
                    }

                    continue;
                }
            }
            else
            {
                c = Lexer.Decode(input, position, length, out size);
                next = table.Transition(state, c);
            }

            if (next < 0)
            {
                break;
            }

            position += size;
            if ((next & LexAutomaton.SkipFlag) != 0)
            {
                tokenStart = position;
                next &= ~LexAutomaton.SkipFlag;
            }

            state = next;
        }

        lexer.Position = position;
        lexer.TokenStart = tokenStart;
        lexer.TokenEnd = tokenEnd;
        lexer.ResultSymbol = symbol;
        return result;
    }
}

/// <summary>
/// An external scanner: the lexing code of a grammar's <c>externals</c>, the C# port of its <c>scanner.c</c>. The parser
/// creates one per <see cref="Parser"/>, and saves its state after each external token, to restore it before scanning
/// from another position.
/// </summary>
public interface IExternalScanner
{
    /// <summary>
    /// Scans an external token from the current position of the lexer, like tree-sitter's
    /// <c>tree_sitter_x_external_scanner_scan</c>: returns whether a token was found, and sets
    /// <see cref="Lexer.ResultSymbol"/> to its index in the grammar's <c>externals</c>.
    /// </summary>
    /// <param name="lexer">The lexer, with <see cref="Lexer.Lookahead"/>, <see cref="Lexer.Advance"/>, <see cref="Lexer.MarkEnd"/> and <see cref="Lexer.GetColumn"/>.</param>
    /// <param name="validSymbols">[external token]: whether the token is valid at this position.</param>
    bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols);

    /// <summary>Writes the state of the scanner, at most <see cref="ExternalScanner.StateCapacity"/> bytes. Returns the length.</summary>
    int Serialize(Span<byte> buffer);

    /// <summary>Restores a state written by <see cref="Serialize"/>, or the initial state when it's empty.</summary>
    void Deserialize(ReadOnlySpan<byte> state);
}

/// <summary>The limits of external scanners.</summary>
public static class ExternalScanner
{
    /// <summary>The size of the buffer of <see cref="IExternalScanner.Serialize"/>, tree-sitter's <c>TREE_SITTER_SERIALIZATION_BUFFER_SIZE</c>.</summary>
    public const int StateCapacity = 1024;
}
