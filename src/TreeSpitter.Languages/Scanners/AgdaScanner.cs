namespace TreeSpitter.Languages.Scanners;

/// <summary>
/// The external scanner of tree-sitter-agda: a port of its <c>src/scanner.c</c> (MIT, LUA Ting-Gan). Its state is the
/// stack of indentations and the count of queued dedents; like the C code, the queue of the tokens to return is kept
/// by the scanner but isn't serialized, and <see cref="Deserialize"/> doesn't reset it.
/// </summary>
public sealed class AgdaScanner : IExternalScanner
{
    // enum TokenType
    private const ushort Newline = 0;
    private const ushort Indent = 1;
    private const ushort Dedent = 2;

    private readonly List<ushort> _indents = [];
    private uint _queuedDedentCount;
    private readonly Queue<ushort> _tokens = new();

    /// <summary>Creates a scanner in its initial state, like <c>tree_sitter_agda_external_scanner_create</c>.</summary>
    public AgdaScanner()
    {
        Deserialize([]);
    }

    private static void Skip(Lexer lexer) => lexer.Advance(true);

    /// <inheritdoc />
    public bool Scan(Lexer lexer, ReadOnlySpan<bool> validSymbols)
    {
        if (_tokens.Count == 0)
        {
            if (validSymbols[Dedent] && _queuedDedentCount > 0)
            {
                _queuedDedentCount--;
                _tokens.Enqueue(Dedent);
                _tokens.Enqueue(Newline);
            }
            else
            {
                var skippedNewline = false;

                while (lexer.Lookahead == ' ' || lexer.Lookahead == '\t' ||
                       lexer.Lookahead == '\r' || lexer.Lookahead == '\n')
                {
                    if (lexer.Lookahead == '\n')
                    {
                        skippedNewline = true;
                        Skip(lexer);
                    }
                    else
                    {
                        Skip(lexer);
                    }
                }

                if (lexer.IsAtEnd)
                {
                    if (validSymbols[Dedent] && _indents.Count > 1)
                    {
                        _indents.RemoveAt(_indents.Count - 1);
                        _tokens.Enqueue(Dedent);
                        _tokens.Enqueue(Newline);
                    }
                    else if (validSymbols[Newline])
                    {
                        _tokens.Enqueue(Newline);
                    }
                }
                else
                {
                    var nextTokenIsComment = false;

                    var indentLength = (ushort)lexer.GetColumn();

                    var indent = indentLength > _indents[_indents.Count - 1];
                    var dedent = indentLength < _indents[_indents.Count - 1];

                    if (!nextTokenIsComment)
                    {
                        if (skippedNewline)
                        {
                            if (indent)
                            {
                                if (validSymbols[Indent])
                                {
                                    _indents.Add(indentLength);
                                    _tokens.Enqueue(Indent);
                                }
                            }
                            else if (dedent)
                            {
                                if (validSymbols[Newline])
                                {
                                    _tokens.Enqueue(Newline);
                                }
                            }
                            else
                            {
                                if (validSymbols[Newline])
                                {
                                    _tokens.Enqueue(Newline);
                                }
                            }
                        }
                        else
                        {
                            if (indent)
                            {
                                if (validSymbols[Indent])
                                {
                                    _indents.Add(indentLength);
                                    _tokens.Enqueue(Indent);
                                }
                            }
                            else if (dedent)
                            {
                                _indents.RemoveAt(_indents.Count - 1);
                                while (indentLength < _indents[_indents.Count - 1])
                                {
                                    _indents.RemoveAt(_indents.Count - 1);
                                    _queuedDedentCount++;
                                }

                                if (validSymbols[Dedent])
                                {
                                    _tokens.Enqueue(Dedent);
                                    _tokens.Enqueue(Newline);
                                }
                                else
                                {
                                    _queuedDedentCount++;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (_tokens.Count == 0)
        {
            return false;
        }

        lexer.ResultSymbol = _tokens.Dequeue();
        return true;
    }

    /// <inheritdoc />
    public int Serialize(Span<byte> buffer)
    {
        if (_indents.Count * sizeof(ushort) + 1 > ExternalScanner.StateCapacity)
        {
            return 0;
        }

        var size = 0;

        buffer[size++] = (byte)_queuedDedentCount;

        // memcpy of the uint16_t array (little-endian)
        foreach (var indent in _indents)
        {
            buffer[size++] = (byte)indent;
            buffer[size++] = (byte)(indent >> 8);
        }

        return size;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The C code tells the initial state (a <c>NULL</c> buffer, which pushes the indentation 0) from an empty state
    /// (an empty stack of indentations): both are an empty span here, read as the initial state. The C serializer
    /// writes an empty state only when the indentations overflow the buffer, and the C scanner then reads past its
    /// empty stack.
    /// </remarks>
    public void Deserialize(ReadOnlySpan<byte> state)
    {
        _queuedDedentCount = 0;
        _indents.Clear();

        if (state.Length == 0)
        {
            _indents.Add(0);
            return;
        }

        _queuedDedentCount = state[0];

        var size = 1;

        if (state.Length > size)
        {
            var count = (state.Length - size) / sizeof(ushort);
            for (var i = 0; i < count; i++)
            {
                _indents.Add((ushort)(state[size] | (state[size + 1] << 8)));
                size += sizeof(ushort);
            }
        }

        if (_indents.Count == 0)
        {
            _indents.Add(0);
        }
    }
}
