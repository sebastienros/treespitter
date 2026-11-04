namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-jsdoc: a port of its <c>src/scanner.c</c> (MIT, Max Brunsfeld). It has no state.
/// </summary>
public sealed class JsDocScanner : IExternalScanner
{
    private const int TypeToken = 0;

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer) => 0;

    /// <inheritdoc />
    public void Deserialize(ReadOnlySpan<byte> state)
    {
    }

    // Scan to the next balanced `}` character.
    private static bool ScanForType(Lexer lexer)
    {
        var stack = 0;
        while (true)
        {
            if (lexer.IsAtEnd)
            {
                return false;
            }

            switch (lexer.Lookahead)
            {
                case '{':
                    stack++;
                    break;
                case '}':
                    stack--;
                    if (stack == -1)
                    {
                        return true;
                    }

                    break;
                case '\n':
                case '\0': // fallthrough
                    // Something's gone wrong.
                    return false;
                default:
                    break;
            }

            lexer.Advance(false);
        }
    }

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (validSymbols[TypeToken] && ScanForType(lexer))
        {
            lexer.ResultSymbol = TypeToken;
            lexer.MarkEnd();
            return true;
        }

        return false;
    }
}
