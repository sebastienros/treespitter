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

    /// <summary>The symbol of the last accepted token.</summary>
    public ushort ResultSymbol;

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
}

/// <summary>
/// The lexer of the virtual machine: runs the lex automata of a <see cref="Language"/>. Generated parsers use it for
/// the automata too large to be compiled efficiently.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public readonly struct TableLexFunction : ILexFunction
{
    internal TableLexFunction(LexAutomaton main, LexAutomaton keyword)
    {
        _main = main;
        _keyword = keyword;
    }

    private readonly LexAutomaton _main;
    private readonly LexAutomaton _keyword;

    /// <inheritdoc />
    public bool Lex(Lexer lexer, int state) => Run(_main, lexer, state);

    /// <inheritdoc />
    public bool LexKeyword(Lexer lexer, int state) => Run(_keyword, lexer, state);

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
            }
            else
            {
                c = Lexer.Decode(input, position, length, out size);
                next = c < 0 ? -1 : table.Transition(state, c);
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
